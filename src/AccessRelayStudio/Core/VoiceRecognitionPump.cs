using System.Diagnostics;
using System.Threading.Channels;

namespace EliteScriptStudio.Core;

internal interface ILocalPhraseRecognizer : IDisposable
{
    (Guid Id, double Confidence)? Recognize(AcousticUtterance utterance);
}
internal sealed record VoiceRecognitionStatus(bool Running, bool Learning, bool Ready, string Message, string? Error, long CapturedFrames, long Recognized, long Rejected);
internal sealed class VoiceRecognitionPump : IDisposable
{
    private readonly record struct Frame(short[] Pcm, long Ticks);
    private readonly Channel<Frame> _frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(25)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<AcousticTemplate> _learned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly VoiceCommandSettings _settings;
    private readonly Func<bool> _isCurrent;
    private readonly Action<VoiceCommandObservation>? _onCommand;
    private readonly Func<ILocalPhraseRecognizer?> _createRecognizer;
    private readonly bool _learning;
    private readonly Action _onStopped;
    private long _captured, _recognized, _rejected;
    private string _message = "Listening locally. No audio is transmitted.";
    private string? _error;
    private volatile bool _running = true;
    private int _stoppedNotified;
    private int _started;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal Task<AcousticTemplate> Learned => _learned.Task;
    internal Task Ready => _ready.Task;
    internal VoiceRecognitionStatus Status => new(_running, _learning, _ready.Task.IsCompletedSuccessfully, Volatile.Read(ref _message), Volatile.Read(ref _error),
        Interlocked.Read(ref _captured), Interlocked.Read(ref _recognized), Interlocked.Read(ref _rejected));
    internal VoiceRecognitionPump(VoiceCommandSettings settings, Func<bool> isCurrent, Action<VoiceCommandObservation>? onCommand,
        Func<ILocalPhraseRecognizer?> createRecognizer, Action onStopped, bool learning = false, bool startImmediately = true)
    {
        settings.Validate(); _settings = settings; _isCurrent = isCurrent; _onCommand = onCommand;
        _createRecognizer = createRecognizer; _onStopped = onStopped; _learning = learning;
        if (startImmediately) Start();
    }
    internal void Start() { if (Interlocked.Exchange(ref _started, 1) == 0) Completion = Task.Run(Work); }
    // Ownership of a successfully submitted array transfers to this bounded worker.
    internal bool TryWrite(short[] pcm, long ticks)
    {
        if (_stop.IsCancellationRequested) return false;
        if (!_ready.Task.IsCompletedSuccessfully) return true; // Discard warm-up audio; never recognize it later.
        if (pcm.Length != 320) { Fail("Microphone framing is invalid."); return false; }
        if (!_frames.Writer.TryWrite(new(pcm, ticks))) { Fail("Recognition fell behind microphone capture. Listening stopped; restart explicitly."); return false; }
        Interlocked.Increment(ref _captured); return true;
    }
    private async Task Work()
    {
        ILocalPhraseRecognizer? recognizer = null;
        try
        {
            if (!_learning) recognizer = _createRecognizer();
            _stop.Token.ThrowIfCancellationRequested(); _ready.TrySetResult();
            var segmenter = new AcousticUtteranceSegmenter(_settings.MinimumRms);
            await foreach (var frame in _frames.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (!_isCurrent()) { Volatile.Write(ref _message, "Controller context changed. Listening stopped; restart explicitly."); break; }
                if (frame.Ticks > Stopwatch.GetTimestamp() || (double)Stopwatch.GetTimestamp() - frame.Ticks > Stopwatch.Frequency / 4d)
                    throw new InvalidOperationException("Microphone audio became stale. Listening stopped; restart explicitly.");
                var utterance = segmenter.Push(frame.Pcm, frame.Ticks);
                if (utterance is null) continue;
                var features = AcousticTemplates.Extract(utterance.Pcm);
                if (_learning) { _learned.TrySetResult(features); Volatile.Write(ref _message, "Personal sound template captured locally."); break; }
                var match = AcousticTemplates.Match(features, _settings) ?? recognizer?.Recognize(utterance);
                long recognized = Stopwatch.GetTimestamp();
                if (!_stop.IsCancellationRequested && _isCurrent() && match is { } accepted && (double)recognized - utterance.EndTicks <= Stopwatch.Frequency)
                { _onCommand?.Invoke(new(accepted.Id, accepted.Confidence, Math.Max(0, utterance.OnsetTicks - Stopwatch.Frequency / 50), utterance.EndTicks, recognized)); Interlocked.Increment(ref _recognized); }
                else Interlocked.Increment(ref _rejected);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Fail(error.Message); }
        finally
        {
            _running = false; _stop.Cancel(); _learned.TrySetCanceled(); _ready.TrySetCanceled(); NotifyStopped();
            while (_frames.Reader.TryRead(out _)) { }
            try { recognizer?.Dispose(); } catch (Exception error) { Volatile.Write(ref _error, error.Message); }
        }
    }
    private void NotifyStopped()
    {
        if (Interlocked.Exchange(ref _stoppedNotified, 1) == 0)
            try { _onStopped(); } catch (Exception error) { Volatile.Write(ref _error, error.Message); }
    }
    internal void Fail(string error) { Volatile.Write(ref _error, error); _stop.Cancel(); _running = false; _learned.TrySetCanceled(); _ready.TrySetException(new InvalidOperationException(error)); NotifyStopped(); }
    public void Dispose()
    {
        _stop.Cancel(); _frames.Writer.TryComplete(); _learned.TrySetCanceled(); _ready.TrySetCanceled(); _running = false;
        Volatile.Write(ref _message, "Local command capture stopped."); NotifyStopped();
    }
}
