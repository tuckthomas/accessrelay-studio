using System.Diagnostics;
using System.Threading.Channels;

namespace EliteScriptStudio.Core;

public sealed record FeedbackSettings(bool AudioEnabled = false, bool HapticsEnabled = false,
    double Volume = .2, double Strength = .15)
{
    public void Validate()
    {
        if (!double.IsFinite(Volume) || Volume is < 0 or > 1 || !double.IsFinite(Strength) || Strength is < 0 or > .5)
            throw new InvalidDataException("Choose feedback volume 0–100% and haptic intensity 0–50%.");
    }
}

internal enum FeedbackCueKind { ProfileChanged, PlayerLocked, Released, Test }
internal readonly record struct FeedbackIdentity(long Generation, long Token);
internal readonly record struct FeedbackCue(FeedbackCueKind Kind, FeedbackIdentity Identity, long ConnectionVersion,
    long Epoch, long CreatedTicks);
internal sealed record FeedbackStatus(long Played, long Dropped, string? Error, double LastQueueDelayMs);

internal interface IFeedbackOutput : IDisposable
{
    Task<string?> PlayAsync(FeedbackCue cue, FeedbackSettings settings, CancellationToken cancellation);
    void Stop();
}

/// <summary>Only TryPublish is called from input/deadline owners. Device work runs
/// on one bounded worker; configuration epochs invalidate queued and active cues.</summary>
internal sealed class FeedbackDispatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly IFeedbackOutput _output;
    private readonly Channel<FeedbackCue?> _queue = Channel.CreateBounded<FeedbackCue?>(new BoundedChannelOptions(8)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<FeedbackCueKind, (FeedbackIdentity Id, long At)> _recent = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private FeedbackSettings _settings = new();
    private CancellationTokenSource? _active;
    private long _epoch, _played, _dropped;
    private double _queueDelay;
    private string? _error;
    private int _resetRequested, _disposed;

    internal FeedbackDispatcher(IFeedbackOutput output, Func<long>? timestamp = null, long frequency = 0)
    {
        _output = output; _timestamp = timestamp ?? Stopwatch.GetTimestamp; _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        if (_frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _worker = Task.Run(RunAsync);
    }
    internal FeedbackStatus Status => new(Interlocked.Read(ref _played), Interlocked.Read(ref _dropped),
        Volatile.Read(ref _error), Volatile.Read(ref _queueDelay));

    internal void Configure(FeedbackSettings settings)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        settings.Validate(); Volatile.Write(ref _settings, settings); Stop();
    }
    internal void Stop()
    {
        CancellationTokenSource? active;
        lock (_gate) { _epoch++; _recent.Clear(); active = _active; Volatile.Write(ref _error, null); }
        Interlocked.Exchange(ref _resetRequested, 1);
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
        _queue.Writer.TryWrite(null);
    }

    internal bool TryPublish(FeedbackCueKind kind, FeedbackIdentity identity, long connectionVersion)
    {
        if (Volatile.Read(ref _disposed) != 0 || !Enum.IsDefined(kind)) return false;
        var settings = Volatile.Read(ref _settings);
        if (!settings.AudioEnabled && !settings.HapticsEnabled) return false;
        long now = _timestamp(), epoch;
        lock (_gate)
        {
            if (_recent.TryGetValue(kind, out var last) && (last.Id == identity || ((double)now - last.At) * 1000 / _frequency < 120)) return false;
            _recent[kind] = (identity, now); epoch = _epoch;
        }
        if (_queue.Writer.TryWrite(new(kind, identity, connectionVersion, epoch, now))) return true;
        Interlocked.Increment(ref _dropped); return false;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var pending in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                if (Interlocked.Exchange(ref _resetRequested, 0) != 0) SafeStop();
                if (pending is not { } cue) continue;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                FeedbackSettings settings;
                lock (_gate)
                {
                    if (cue.Epoch != _epoch) { Interlocked.Increment(ref _dropped); continue; }
                    double age = ((double)_timestamp() - cue.CreatedTicks) * 1000 / _frequency;
                    if (age is < 0 or > 500) { Interlocked.Increment(ref _dropped); continue; }
                    _active = cancellation; settings = _settings; Volatile.Write(ref _queueDelay, age);
                }
                try
                {
                    string? error = await _output.PlayAsync(cue, settings, cancellation.Token).ConfigureAwait(false);
                    lock (_gate)
                        if (cue.Epoch == _epoch && !cancellation.IsCancellationRequested)
                        { Volatile.Write(ref _error, error); Interlocked.Increment(ref _played); }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { SafeStop(); }
                catch (Exception error)
                {
                    lock (_gate) if (cue.Epoch == _epoch) Volatile.Write(ref _error, "Feedback unavailable: " + error.Message);
                    SafeStop();
                }
                finally { lock (_gate) if (ReferenceEquals(_active, cancellation)) _active = null; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            SafeStop();
            try { _output.Dispose(); } catch (Exception error) { Volatile.Write(ref _error, "Feedback cleanup failed: " + error.Message); }
        }
    }
    private void SafeStop() { try { _output.Stop(); } catch (Exception error) { Volatile.Write(ref _error, "Feedback could not stop: " + error.Message); } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop(); _queue.Writer.TryComplete(); _stop.Cancel();
        _worker.GetAwaiter().GetResult(); _stop.Dispose();
    }
}

internal static class FeedbackSounds
{
    internal const int SampleRate = 48_000;
    internal static int DurationMs(FeedbackCueKind kind) => kind switch
    { FeedbackCueKind.PlayerLocked => 40, FeedbackCueKind.Released => 80, FeedbackCueKind.ProfileChanged => 120, _ => 100 };
    internal static float[] Render(FeedbackCueKind kind, double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        int duration = DurationMs(kind), count = SampleRate * duration / 1000;
        var samples = new float[count];
        double first = kind switch { FeedbackCueKind.ProfileChanged => 440, FeedbackCueKind.PlayerLocked => 660, FeedbackCueKind.Released => 880, _ => 660 };
        double second = kind switch { FeedbackCueKind.ProfileChanged => 660, FeedbackCueKind.Released => 1100, _ => 880 };
        for (int index = 0; index < count; index++)
        {
            int local = kind == FeedbackCueKind.PlayerLocked ? index : index % (count / 2);
            int length = kind == FeedbackCueKind.PlayerLocked ? count : count / 2;
            double envelope = Math.Min(1, Math.Min(local, length - 1 - local) / (SampleRate * .005));
            double frequency = kind == FeedbackCueKind.PlayerLocked || index < count / 2 ? first : second;
            samples[index] = (float)(.25 * volume * envelope * Math.Sin(2 * Math.PI * frequency * local / SampleRate));
        }
        return samples;
    }
}
