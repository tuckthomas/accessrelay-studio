using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using EliteScriptStudio.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EliteScriptStudio.Audio;

internal sealed record LocalSpeechEngine(string Id, string Name, string Culture);

/// <summary>Independent raw physical microphone capture. The bounded worker runs
/// before conversion/effects; no model work or driver writes occur on the callback.</summary>
internal sealed class VoiceCommandSession : IDisposable
{
    private readonly VoiceRecognitionPump _pump;
    private WasapiCapture? _capture;
    private short[] _partial = new short[320];
    private int _partialCount, _disposed;
    internal Task<AcousticTemplate> Learned => _pump.Learned;
    internal Task Completion => _pump.Completion;
    internal Task Ready => _pump.Ready;
    internal VoiceRecognitionStatus Status => _pump.Status;
    internal static LocalSpeechEngine[] Engines()
    {
        try { return SpeechRecognitionEngine.InstalledRecognizers().Select(engine => new LocalSpeechEngine(engine.Id, engine.Name, engine.Culture.Name)).ToArray(); }
        catch (Exception error) when (error is PlatformNotSupportedException or COMException or InvalidOperationException) { return []; }
    }
    internal VoiceCommandSession(string microphoneId, VoiceCommandSettings settings, Func<bool> isCurrent,
        Action<VoiceCommandObservation>? onCommand, bool learning = false, Action? onStopped = null)
    {
        settings.Validate();
        using var enumerator = new MMDeviceEnumerator();
        using var microphone = enumerator.GetDevice(microphoneId);
        if (microphone.DataFlow != DataFlow.Capture || VoiceSession.IsBundledMicrophone(microphone))
            throw new InvalidOperationException("Choose a physical microphone for local commands.");
        if (!learning && settings.Bindings.Any(binding => binding.Phrase.Length != 0) && !Engines().Any(engine => engine.Id == settings.RecognizerId))
            throw new InvalidOperationException("Choose an installed Windows desktop speech recognizer for phrase commands.");
        _capture = new WasapiCapture(microphone, true, 20) { WaveFormat = new WaveFormat(16000, 16, 1) };
        _capture.DataAvailable += Capture; _capture.RecordingStopped += CaptureStopped;
        // Stop notifications can originate on WASAPI's callback or a recognizer
        // worker: queue endpoint teardown to avoid disposing inside that callback.
        _pump = new VoiceRecognitionPump(settings, isCurrent, onCommand,
            () => settings.Bindings.Any(binding => binding.Phrase.Length != 0) ? new WindowsPhraseRecognizer(settings) : null,
            () => { onStopped?.Invoke(); _ = Task.Run(CloseCapture); }, learning, startImmediately: false);
        try { _capture?.StartRecording(); _pump.Start(); _ = Task.Run(WatchContext); }
        catch { Dispose(); throw; }

        async Task WatchContext()
        {
            try
            {
                while (Status.Running)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    if (!isCurrent()) { _pump.Dispose(); break; }
                }
            }
            catch (Exception error) { _pump.Fail(error.Message); }
        }
    }
    private void Capture(object? sender, WaveInEventArgs args)
    {
        if (!Status.Running) return;
        if (args.BytesRecorded % 2 != 0) { _pump.Fail("The microphone returned incomplete PCM samples."); return; }
        var pcm = MemoryMarshal.Cast<byte, short>(args.Buffer.AsSpan(0, args.BytesRecorded));
        for (int index = 0; index < pcm.Length; index++)
        {
            _partial[_partialCount++] = pcm[index];
            if (_partialCount != 320) continue;
            long ticks = Stopwatch.GetTimestamp() - (long)((pcm.Length - index - 1) * (double)Stopwatch.Frequency / 16000);
            if (!_pump.TryWrite(_partial, ticks)) return;
            _partial = new short[320]; _partialCount = 0;
        }
    }
    private void CaptureStopped(object? sender, StoppedEventArgs args)
    { if (Status.Running) _pump.Fail(args.Exception?.Message ?? "Microphone capture stopped. Restart commands explicitly."); }
    private void CloseCapture()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null) return;
        capture.DataAvailable -= Capture; capture.RecordingStopped -= CaptureStopped;
        try { capture.StopRecording(); } catch (Exception error) { _pump.Fail(error.Message); }
        try { capture.Dispose(); } catch (Exception error) { _pump.Fail(error.Message); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pump.Dispose(); CloseCapture();
    }
}

/// <summary>Installed Windows desktop acoustic/language model, constrained to the
/// configured phrase grammar. Microsoft OS model licensing; System.Speech is MIT.
/// Utterance endpoint detection precedes recognition; no sub-100 ms guarantee.</summary>
internal sealed class WindowsPhraseRecognizer : ILocalPhraseRecognizer
{
    private readonly SpeechRecognitionEngine _engine;
    private readonly VoiceCommandSettings _settings;
    internal WindowsPhraseRecognizer(VoiceCommandSettings settings)
    {
        _settings = settings;
        var engine = new SpeechRecognitionEngine(settings.RecognizerId);
        try
        {
            var builder = new GrammarBuilder { Culture = engine.RecognizerInfo.Culture };
            builder.Append(new Choices(settings.Bindings.Where(binding => binding.Phrase.Length != 0).Select(binding => binding.Phrase).ToArray()));
            engine.LoadGrammar(new Grammar(builder)); engine.MaxAlternates = 1;
            engine.InitialSilenceTimeout = TimeSpan.FromMilliseconds(500); engine.BabbleTimeout = TimeSpan.FromSeconds(2);
            _engine = engine;
        }
        catch { engine.Dispose(); throw; }
    }
    public (Guid Id, double Confidence)? Recognize(AcousticUtterance utterance)
    {
        using var stream = new MemoryStream(MemoryMarshal.AsBytes(utterance.Pcm.AsSpan()).ToArray(), writable: false);
        _engine.SetInputToAudioStream(stream, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        var result = _engine.Recognize(TimeSpan.FromSeconds(2));
        var binding = result is null ? null : _settings.Bindings.FirstOrDefault(binding => string.Equals(binding.Phrase, result.Text, StringComparison.OrdinalIgnoreCase));
        return binding is not null && result!.Confidence >= binding.MinimumConfidence ? (binding.Id, result.Confidence) : null;
    }
    public void Dispose() => _engine.Dispose();
}
