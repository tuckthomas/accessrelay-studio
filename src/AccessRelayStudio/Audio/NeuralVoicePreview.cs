using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EliteScriptStudio.Audio;

internal sealed class NeuralVoicePreview
{
    internal const int CaptureSeconds = 12;
    private readonly NeuralVoiceWorker _neural;
    private readonly VoiceSignal _signal;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _completion;
    private string _message = $"Recording a {CaptureSeconds}-second sample. Speak now.";
    private float _inputPeak;

    public NeuralVoicePreview(string microphoneId, string outputId, VoiceSettings settings, NeuralVoiceWorker neural,
        Action<NeuralVoicePreview, string, bool> onFinished)
    {
        _neural = neural;
        _signal = new VoiceSignal(settings, CaptureSeconds * 1000, primeMilliseconds: 0);
        var stop = _stop.Token;
        _completion = Task.Run(() => RunAsync(microphoneId, outputId, onFinished, stop));
    }

    public VoiceStatus Status => new(true, Volatile.Read(ref _message), Volatile.Read(ref _inputPeak),
        _signal.OutputPeak, _signal.BufferedMilliseconds);

    public bool StopRequested => _stop.IsCancellationRequested;

    public void SetSettings(VoiceSettings settings) => _signal.SetSettings(settings);

    public void RequestStop()
    {
        _stop.Cancel();
        // Closing the host also releases any blocked reads from its anonymous pipe.
        _neural.Dispose();
    }

    public void WaitForStop() => _completion.GetAwaiter().GetResult();

    public void DisposeCancellation() => _stop.Dispose();

    private async Task RunAsync(string microphoneId, string outputId, Action<NeuralVoicePreview, string, bool> onFinished,
        CancellationToken stop)
    {
        string message;
        bool failed = false;
        try
        {
            float[] converted;
            using (_neural)
            {
                float[] recorded = await RecordAsync(microphoneId, stop).ConfigureAwait(false);
                Volatile.Write(ref _inputPeak, 0);
                Volatile.Write(ref _message, "Converting the recorded sample…");
                stop.ThrowIfCancellationRequested();
                converted = await _neural.ConvertPreviewAsync(recorded, stop).ConfigureAwait(false);
            }

            await PlayAsync(outputId, converted, stop).ConfigureAwait(false);
            message = "Preview finished. Record another sample to listen again.";
        }
        catch (Exception) when (stop.IsCancellationRequested) { message = "Voice processing is off."; }
        catch (COMException error) { message = VoiceSession.DescribeAudioError(error); failed = true; }
        catch (Exception error) { message = $"Voice preview stopped. {error.Message}"; failed = true; }
        onFinished(this, message, failed);
    }

    private async Task<float[]> RecordAsync(string microphoneId, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var microphone = enumerator.GetDevice(microphoneId);
        if (microphone.DataFlow != DataFlow.Capture || VoiceSession.IsBundledMicrophone(microphone))
            throw new InvalidOperationException("Choose a physical microphone as the voice input.");

        var recorded = new float[VoiceSignal.SampleRate * CaptureSeconds];
        int written = 0;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new WasapiCapture(microphone, true, 20) { WaveFormat = _signal.WaveFormat };
        capture.DataAvailable += (_, args) =>
        {
            var samples = MemoryMarshal.Cast<byte, float>(args.Buffer.AsSpan(0, args.BytesRecorded));
            int count = Math.Min(samples.Length, recorded.Length - written);
            float peak = 0;
            for (int index = 0; index < count; index++)
            {
                float sample = samples[index];
                float level = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
                recorded[written++] = level;
                peak = Math.Max(peak, Math.Abs(level));
            }
            Volatile.Write(ref _inputPeak, Math.Max(peak, Volatile.Read(ref _inputPeak) * .84f));
            if (written == recorded.Length)
                complete.TrySetResult();
        };
        capture.RecordingStopped += (_, args) => complete.TrySetException(args.Exception ??
            new InvalidOperationException("Microphone capture stopped before the sample was complete."));
        stop.ThrowIfCancellationRequested();
        capture.StartRecording();
        try
        {
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(CaptureSeconds + 4), stop).ConfigureAwait(false);
            return recorded;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The selected microphone did not provide a complete sample.");
        }
    }

    private async Task PlayAsync(string outputId, float[] converted, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var outputDevice = enumerator.GetDevice(outputId);
        if (outputDevice.DataFlow != DataFlow.Render)
            throw new InvalidOperationException("Choose a playback device as the voice destination.");

        using var output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 20);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is { } error)
                complete.TrySetException(error);
            else
                complete.TrySetResult();
        };
        _signal.Write(converted);
        output.Init(new FiniteSamples(_signal));
        stop.ThrowIfCancellationRequested();
        Volatile.Write(ref _message, $"Playing the converted sample · {outputDevice.FriendlyName}");
        output.Play();
        await complete.Task.WaitAsync(stop).ConfigureAwait(false);
    }

    private sealed class FiniteSamples(VoiceSignal signal) : IWaveProvider
    {
        private float[] _work = [];
        public WaveFormat WaveFormat => signal.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            int samples = Math.Min(count / sizeof(float), signal.BufferedSamples);
            if (samples == 0)
                return 0;
            if (_work.Length < samples)
                _work = new float[samples];
            signal.Read(_work, 0, samples);
            int bytes = samples * sizeof(float);
            Buffer.BlockCopy(_work, 0, buffer, offset, bytes);
            return bytes;
        }
    }
}
