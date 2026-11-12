using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EliteScriptStudio.Audio;

public enum VoicePreset { Clean, Warm, Bright, Deep, Radio }

public sealed record VoiceSettings(VoicePreset Preset, double PitchSemitones, double GainDb, bool Muted);
public sealed record VoiceEndpoint(string Id, string Name, bool IsDefault);
public sealed record VoiceDevices(VoiceEndpoint[] Microphones, VoiceEndpoint[] Outputs);
public sealed record VoiceStatus(bool IsRunning, string Message, float InputPeak, float OutputPeak, int BufferedMilliseconds, string? Error = null);

public sealed class VoiceSession : IDisposable
{
    private readonly object _gate = new();
    private VoiceStream? _stream;
    private NeuralVoicePreview? _preview;
    private string _message = "Voice processing is off.";
    private string? _error;
    private bool _disposed;

    public VoiceStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_preview is { } preview)
                    return preview.Status;
                return _stream is { } stream
                    ? new(true, _message, stream.InputPeak, stream.OutputPeak, stream.BufferedMilliseconds)
                    : new(false, _message, 0, 0, 0, _error);
            }
        }
    }

    public static VoiceDevices Discover()
    {
        using var enumerator = new MMDeviceEnumerator();
        return new(Enumerate(enumerator, DataFlow.Capture), Enumerate(enumerator, DataFlow.Render));
    }

    public void Start(string microphoneId, string outputId, VoiceSettings settings) =>
        StartStream(microphoneId, outputId, settings, null, null, null, CancellationToken.None);

    internal async Task StartPreviewAsync(string microphoneId, string outputId, VoiceSettings settings,
        VoiceModelOption model, VoiceProfile? profile, CancellationToken cancellation)
    {
        if (!model.UsesReference)
        {
            StartStream(microphoneId, outputId, settings, null, null, null, cancellation);
            return;
        }

        var neural = (await OpenModelAsync(model, profile, cancellation).ConfigureAwait(false))!;
        try
        {
            lock (_gate)
            {
                EnsureCanStart(cancellation);
                _error = null;
                _preview = new NeuralVoicePreview(microphoneId, outputId, settings, neural, OnPreviewFinished);
            }
        }
        catch
        {
            neural.Dispose();
            throw;
        }
    }

    internal async Task StartVirtualAsync(string microphoneId, VoiceSettings settings, VoiceModelOption model,
        VoiceProfile? profile, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var neural = await OpenModelAsync(model, profile, cancellation).ConfigureAwait(false);
        VirtualMicPipe? pipe = null;
        try
        {
            // Model hosts emit bursts of converted samples. The USB buffer absorbs the gaps between them.
            pipe = await VirtualMicPipe.OpenAsync(cancellation, neural is null ? 60 : 240).ConfigureAwait(false);
            string endpointName = await WaitForVirtualEndpointAsync(cancellation).ConfigureAwait(false);
            StartStream(microphoneId, null, settings, pipe, endpointName, neural, cancellation);
        }
        catch
        {
            try { pipe?.Dispose(); }
            finally { neural?.Dispose(); }
            throw;
        }
    }

    internal async Task SwitchVirtualProfileAsync(VoiceModelOption model, VoiceProfile profile, CancellationToken cancellation)
    {
        VoiceStream stream;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stream = _stream is { HasVirtualOutput: true } current ? current
                : throw new InvalidOperationException("Start the virtual microphone before changing its live profile.");
        }

        var replacement = await OpenModelAsync(model, profile, cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Choose a reference-voice model to switch profiles on this stream.");
        NeuralVoiceWorker? previous;
        try
        {
            previous = await stream.ReplaceWorkerAsync(replacement, cancellation).ConfigureAwait(false);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
        try { previous?.Dispose(); }
        catch (Exception error)
        {
            // The replacement already owns the live stream. A retired host's cleanup cannot roll it back.
            System.Diagnostics.Trace.TraceWarning("Retired voice host cleanup failed: {0}", error.Message);
        }
    }

    private static async Task<NeuralVoiceWorker?> OpenModelAsync(VoiceModelOption model, VoiceProfile? profile,
        CancellationToken cancellation)
    {
        if (!model.UsesReference)
            return null;
        if (profile?.ReferencePath is not { } reference)
            throw new InvalidOperationException("Select an imported voice profile for the AI model.");
        return await NeuralVoiceWorker.OpenAsync(model, reference, cancellation).ConfigureAwait(false);
    }

    private void StartStream(string microphoneId, string? outputId, VoiceSettings settings,
        VirtualMicPipe? pipe, string? virtualEndpointName, NeuralVoiceWorker? neural, CancellationToken cancellation)
    {
        lock (_gate)
        {
            EnsureCanStart(cancellation);

            try
            {
                _stream = new VoiceStream(microphoneId, outputId, settings, OnFault, pipe, virtualEndpointName, neural);
                _error = null;
                _message = $"Voice processing is live · {_stream.OutputName}";
            }
            catch (COMException error)
            {
                _message = DescribeAudioError(error);
                throw new InvalidOperationException(_message, error);
            }
        }
    }

    private void EnsureCanStart(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not null || _preview is not null)
            throw new InvalidOperationException("Stop the current voice session before starting a new one.");
    }

    public void SetSettings(VoiceSettings settings)
    {
        lock (_gate)
        {
            _stream?.SetSettings(settings);
            _preview?.SetSettings(settings);
        }
    }

    public void Stop() => Stop(disposing: false);

    public void Dispose() => Stop(disposing: true);

    private void Stop(bool disposing)
    {
        NeuralVoicePreview? preview;
        lock (_gate)
        {
            if (disposing)
                _disposed = true;
            StopStream();
            preview = _preview;
            preview?.RequestStop();
            _message = "Voice processing is off.";
            _error = null;
        }
        preview?.WaitForStop();
    }

    private void OnPreviewFinished(NeuralVoicePreview preview, string message, bool failed)
    {
        lock (_gate)
        {
            bool stopped = preview.StopRequested;
            preview.DisposeCancellation();
            if (_preview != preview)
                return;
            _preview = null;
            _message = stopped ? "Voice processing is off." : message;
            _error = !stopped && failed ? message : null;
        }
    }

    private void OnFault(VoiceStream stream, string message)
    {
        lock (_gate)
        {
            if (_stream != stream)
                return;

            StopStream();
            _message = message;
            _error = message;
        }
    }

    private void StopStream()
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
    }

    private static VoiceEndpoint[] Enumerate(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(flow, Role.Communications))
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            defaultId = defaultDevice.ID;
        }

        var endpoints = new List<VoiceEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                // An endpoint can disappear during enumeration.
                try
                {
                    string id = device.ID;
                    if (flow != DataFlow.Capture || !IsBundledMicrophone(device))
                        endpoints.Add(new(id, device.FriendlyName, id == defaultId));
                }
                catch (COMException) { }
            }
        }
        return endpoints.OrderByDescending(endpoint => endpoint.IsDefault).ThenBy(endpoint => endpoint.Name).ToArray();
    }

    internal static bool IsBundledMicrophone(MMDevice device) => VirtualMicDeviceIdentity.MatchesEndpoint(device.ID);

    private static async Task<string> WaitForVirtualEndpointAsync(CancellationToken cancellation)
    {
        using var enumerator = new MMDeviceEnumerator();
        for (int attempt = 0; attempt < 60; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            string? name = null;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        if (name is null && IsBundledMicrophone(device))
                            name = device.FriendlyName;
                    }
                    catch (COMException) { }
                }
            }
            if (name is not null)
                return name;
            await Task.Delay(250, cancellation).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Windows attached the virtual USB device but did not expose its microphone. Check Windows Sound settings and try again.");
    }

    internal static string DescribeAudioError(Exception error) => error.HResult switch
    {
        unchecked((int)0x80070005) => "Windows denied microphone access. Check Privacy & security → Microphone.",
        unchecked((int)0x88890004) => "The selected audio device disconnected. Refresh the audio device list.",
        unchecked((int)0x8889000A) => "The selected audio device is busy. Close its exclusive audio session or choose another device.",
        _ => $"Voice audio stopped. {error.Message}"
    };

    private sealed class VoiceStream : IDisposable
    {
        private readonly object _workerGate = new();
        private readonly VoiceSignal _signal;
        private readonly Action<VoiceStream, string> _onFault;
        private readonly VirtualMicPipe? _virtual;
        private NeuralVoiceWorker? _neural;
        private PendingWorker? _pendingWorker;
        private WasapiCapture? _capture;
        private WasapiOut? _output;
        private float _inputPeak;
        private int _stopping;
        private int _faulted;

        public VoiceStream(string microphoneId, string? outputId, VoiceSettings settings, Action<VoiceStream, string> onFault,
            VirtualMicPipe? virtualPipe, string? virtualEndpointName, NeuralVoiceWorker? neural)
        {
            _signal = new VoiceSignal(settings, neural is null ? 60 : VoiceSignal.ReferenceBufferMilliseconds,
                virtualPipe is null ? neural?.OutputPrimeMilliseconds ?? 20 : 0);
            _onFault = onFault;
            _virtual = virtualPipe;
            _neural = neural;
            using var enumerator = new MMDeviceEnumerator();
            using var microphone = enumerator.GetDevice(microphoneId);
            using var output = outputId is not null ? enumerator.GetDevice(outputId) : null;

            if (microphone.DataFlow != DataFlow.Capture || IsBundledMicrophone(microphone))
                throw new InvalidOperationException("Choose a physical microphone as the voice input.");
            if (output is not null && output.DataFlow != DataFlow.Render)
                throw new InvalidOperationException("Choose a playback device as the voice destination.");

            OutputName = output?.FriendlyName ?? virtualEndpointName ?? VirtualMicPipe.DeviceName;
            try
            {
                // WASAPI shared mode converts between this voice format and each endpoint's mix format.
                _capture = new WasapiCapture(microphone, true, 20) { WaveFormat = _signal.WaveFormat };
                _capture.DataAvailable += CaptureSamples;
                _capture.RecordingStopped += CaptureStopped;
                if (output is not null)
                {
                    _output = new WasapiOut(output, AudioClientShareMode.Shared, true, 20);
                    _output.PlaybackStopped += PlaybackStopped;
                    _output.Init(_signal.ToWaveProvider());
                }
                _virtual?.Start(_signal, error => ReportFailure(error, "Virtual microphone playback stopped."));
                if (_neural is { } worker)
                    StartWorker(worker);
                _capture.StartRecording();
                _output?.Play();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string OutputName { get; }
        public bool HasVirtualOutput => _virtual is not null;
        public float InputPeak => Volatile.Read(ref _neural) is null ? _signal.InputPeak : Volatile.Read(ref _inputPeak);
        public float OutputPeak => _signal.OutputPeak;
        public int BufferedMilliseconds => _signal.BufferedMilliseconds;

        public void SetSettings(VoiceSettings settings)
        {
            lock (_workerGate)
                _signal.SetSettings(_neural is null ? settings : settings with { Preset = VoicePreset.Clean, PitchSemitones = 0 });
        }

        public async Task<NeuralVoiceWorker?> ReplaceWorkerAsync(NeuralVoiceWorker replacement, CancellationToken cancellation)
        {
            var pending = new PendingWorker(replacement);
            try
            {
                lock (_workerGate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    EnsureReplaceable();
                    if (_pendingWorker is not null)
                        throw new InvalidOperationException("A voice profile is already loading.");
                    _pendingWorker = pending;
                    StartWorker(replacement);
                }

                await pending.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
                lock (_workerGate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    EnsureReplaceable();
                    if (pending.Fault is { } fault)
                        throw new InvalidOperationException($"The new profile stopped during startup. {fault.Message}", fault);
                    var previous = _neural;
                    _signal.ReplaceWithReference(pending.Audio.AsSpan(0, pending.Count), primeMilliseconds: 0);
                    Volatile.Write(ref _neural, replacement);
                    _pendingWorker = null;
                    return previous;
                }
            }
            finally
            {
                lock (_workerGate)
                    if (ReferenceEquals(_pendingWorker, pending))
                        _pendingWorker = null;
            }
        }

        private void EnsureReplaceable()
        {
            if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _faulted) != 0)
                throw new InvalidOperationException("The virtual microphone stopped while the profile was loading.");
        }

        private void StartWorker(NeuralVoiceWorker worker) => worker.Start((samples, count) =>
        {
            lock (_workerGate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                if (ReferenceEquals(_neural, worker))
                    _signal.Write(samples.AsSpan(0, count));
                else if (_pendingWorker is { } pending && ReferenceEquals(pending.Worker, worker))
                    pending.Append(samples.AsSpan(0, count));
            }
        }, error =>
        {
            lock (_workerGate)
            {
                if (ReferenceEquals(_neural, worker))
                    ReportFailure(error, "AI voice processing stopped.");
                else if (_pendingWorker is { } pending && ReferenceEquals(pending.Worker, worker))
                    pending.Fail(error);
            }
        });

        private void CaptureSamples(object? sender, WaveInEventArgs args)
        {
            var samples = MemoryMarshal.Cast<byte, float>(args.Buffer.AsSpan(0, args.BytesRecorded));
            lock (_workerGate)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                if (_neural is not { } worker)
                    _signal.Write(samples);
                else if (!worker.Capture(samples))
                    ReportFailure(null, "The AI model could not keep up with the microphone stream.");

                if (_pendingWorker is { Fault: null } pending)
                {
                    if (!pending.Ready.Task.IsCompleted && _neural?.IsInputQueueFull == true)
                        pending.Fail(new InvalidOperationException("The active model is too busy to load a second profile."));
                    else if (!pending.Worker.Capture(samples))
                        pending.Fail(new InvalidOperationException("The new profile could not keep up with the microphone stream."));
                }
            }

            float peak = 0;
            foreach (float sample in samples)
                if (float.IsFinite(sample))
                    peak = Math.Max(peak, Math.Abs(sample));
            Volatile.Write(ref _inputPeak, Math.Max(peak, Volatile.Read(ref _inputPeak) * .84f));
        }

        private void CaptureStopped(object? sender, StoppedEventArgs args) =>
            ReportFailure(args.Exception, "Microphone capture stopped. Check the selected audio device.");

        private void PlaybackStopped(object? sender, StoppedEventArgs args) =>
            ReportFailure(args.Exception, "Voice playback stopped. Check the selected audio device.");

        private void ReportFailure(Exception? error, string message)
        {
            if (Volatile.Read(ref _stopping) != 0 || Interlocked.Exchange(ref _faulted, 1) != 0)
                return;

            // NAudio can raise these on its worker threads. Dispose elsewhere so the workers can exit.
            _ = Task.Run(() => _onFault(this, error is null ? message : DescribeAudioError(error)));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
                return;

            try
            {
                if (_capture is { } capture)
                {
                    capture.DataAvailable -= CaptureSamples;
                    capture.RecordingStopped -= CaptureStopped;
                    capture.Dispose();
                }
            }
            finally
            {
                try
                {
                    if (_output is { } output)
                    {
                        output.PlaybackStopped -= PlaybackStopped;
                        output.Dispose();
                    }
                }
                finally
                {
                    NeuralVoiceWorker? worker;
                    lock (_workerGate)
                    {
                        worker = _neural;
                        Volatile.Write(ref _neural, null);
                        _pendingWorker?.Fail(new InvalidOperationException("The virtual microphone stopped while the profile was loading."));
                    }
                    try { worker?.Dispose(); }
                    finally { _virtual?.Dispose(); }
                }
            }
        }

        private sealed class PendingWorker(NeuralVoiceWorker worker)
        {
            public NeuralVoiceWorker Worker { get; } = worker;
            public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public float[] Audio { get; } = new float[VoiceSignal.SampleRate];
            public int Count { get; private set; }
            public Exception? Fault { get; private set; }

            public void Append(ReadOnlySpan<float> samples)
            {
                if (Fault is not null)
                    return;
                int count = Math.Min(Audio.Length - Count, samples.Length);
                samples[..count].CopyTo(Audio.AsSpan(Count));
                Count += count;
                if (Count >= VoiceSignal.SampleRate * Worker.OutputPrimeMilliseconds / 1000)
                    Ready.TrySetResult();
            }

            public void Fail(Exception error)
            {
                Fault ??= error;
                Ready.TrySetResult();
            }
        }
    }
}
