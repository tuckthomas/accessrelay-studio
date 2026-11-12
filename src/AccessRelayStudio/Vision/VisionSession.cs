using System.Diagnostics;
using System.Buffers;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal readonly record struct VisionSessionStatus(bool Running, bool ModelReady, bool CanArm, string Message,
    long Frames, long Predictions, long DroppedFrames, double LastProcessingMs, long LatestCaptureTicks,
    int CaptureWidth = 0, int CaptureHeight = 0);

internal sealed class VisionPreview(byte[] pixels, int width, int height, int stride, long sequence) : IDisposable
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public long Sequence { get; } = sequence;
    public int Length => checked(Stride * Height);
    public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels);
}

/// <summary>Capture and inference live outside controller locks. Only StudioRuntime arbitrates output.</summary>
internal sealed class VisionSession : IDisposable
{
    private readonly StudioRuntime _runtime;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Thread? _worker;
    private VisionModel? _model;
    private VisionTrainingRecorder? _recorder;
    private VisionSettings? _activeSettings;
    private bool _trainingInhibited;
    private string? _modelHash;
    private VisionDiagnosticEvent[] _lastModelEvents = [];
    private VisionPreview? _preview;
    private VisionSessionStatus _status = new(false, false, false, "Observation is off.", 0, 0, 0, 0, 0);
    private readonly FrameMetric[] _metrics = new FrameMetric[2048];
    private int _metricIndex, _metricCount;
    private bool _disposed;
    private readonly record struct FrameMetric(long Sequence, long CaptureTicks, long CompletedTicks,
        double ProcessingMs, bool PredictionAccepted, VisionStatusSnapshot Output,
        long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections);

    internal VisionSession(StudioRuntime runtime) => _runtime = runtime;
    internal VisionSessionStatus Status { get { lock (_gate) return _status; } }
    internal string TrainingStatus { get { lock (_gate) return _recorder?.Message ?? "No training take is recording."; } }
    internal bool IsRecordingTraining { get { lock (_gate) return _recorder?.IsRecording == true; } }
    internal VisionPreview? TakePreview() { lock (_gate) { var value = _preview; _preview = null; return value; } }

    internal void Start(nint hwnd, VisionSettings settings)
    {
        settings.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker?.IsAlive == true) throw new InvalidOperationException("Stop the current observation session first.");
            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            var cancellation = _stop.Token;
            _runtime.ConfigureVision(settings);
            _activeSettings = settings;
            _runtime.InvalidateVisionSession("Preparing capture and model.");
            _status = new(true, false, false, "Starting window capture…", 0, 0, 0, 0, 0);
            _metricCount = _metricIndex = 0;
            _modelHash = null; _lastModelEvents = [];
            _preview?.Dispose(); _preview = null;
            _worker = new Thread(() => Run(hwnd, settings, cancellation))
                { IsBackground = true, Name = "AccessRelay vision" };
            _worker.Start();
        }
    }

    private void Run(nint hwnd, VisionSettings settings, CancellationToken cancellation)
    {
        VisionModel? model = null;
        try
        {
            using var capture = new RemotePlayCapture();
            capture.Start(hwnd);
            string modelMessage = "Capture only. Select a validated animation model to enable assistance.";
            if (!string.IsNullOrWhiteSpace(settings.ModelManifestPath))
            {
                try
                {
                    model = VisionModel.Load(settings.ModelManifestPath, settings, cancellation);
                    lock (_gate) { _model = model; _modelHash = model.ManifestHash; }
                    modelMessage = model.ValidationSummary;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { modelMessage = "Capture only. Model unavailable: " + error.Message; }
            }
            cancellation.ThrowIfCancellationRequested();
            long previewAt = 0, sourceGeneration = -1;
            long frames = 0, predictions = 0;
            bool sourceReady = false;
            WaitHandle[] waits = [cancellation.WaitHandle, capture.FrameReady];
            while (!cancellation.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits, 100) == 0) break;
                lock (_gate)
                {
                    if (_trainingInhibited && _recorder?.IsFinalized == true)
                    {
                        _runtime.SetVisionTrainingRecording(false);
                        _trainingInhibited = false;
                    }
                }
                var source = capture.Status;
                long now = Stopwatch.GetTimestamp();
                bool fresh = source.IsRunning && source.LatestFrameTicks > 0 &&
                    Stopwatch.GetElapsedTime(source.LatestFrameTicks, now).TotalMilliseconds <= settings.MaximumFrameAgeMs &&
                    source.ContentChangedTicks > 0 && Stopwatch.GetElapsedTime(source.ContentChangedTicks, now).TotalMilliseconds < 500;
                if (!fresh && sourceReady)
                {
                    _runtime.InvalidateVisionSession("Capture is unavailable, stale, or unchanged. Resume observation and rearm.");
                    sourceReady = false;
                    model?.ResetTracking();
                }
                if (!fresh)
                    lock (_gate) _status = _status with { CanArm = false, Message = "Source is stale or unchanged. " + modelMessage };
                if (capture.TryAcquireLatest(out var frame) && frame is not null)
                {
                    using (frame)
                    {
                        var observedStatus = _runtime.VisionStatus;
                        if (sourceReady && !observedStatus.SourceReady)
                        { sourceReady = false; model?.ResetTracking(); }
                        long started = Stopwatch.GetTimestamp();
                        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
                        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                        VisionTrainingRecorder? recorder;
                        lock (_gate) recorder = _recorder;
                        recorder?.Capture(frame, observedStatus);
                        if (sourceGeneration != frame.SourceGeneration)
                        {
                            _runtime.InvalidateVisionSession("Capture source changed. Reacquiring player.");
                            sourceReady = false;
                            sourceGeneration = frame.SourceGeneration;
                            model?.ResetTracking();
                        }
                        // Readiness invalidation from profile/controller/Stop all must never auto-rearm output.
                        if (fresh && model?.CanArm == true && !sourceReady)
                        {
                            _runtime.BeginVisionSession();
                            sourceReady = true;
                            observedStatus = _runtime.VisionStatus;
                        }
                        bool accepted = false;
                        if (model is not null)
                        {
                            var prediction = model.PushFrame(frame, observedStatus);
                            if (prediction is { } observation && !cancellation.IsCancellationRequested)
                            {
                                predictions++;
                                if (fresh && sourceReady && model.CanArm)
                                    accepted = _runtime.SubmitVisionPrediction(observation);
                            }
                        }
                        long completed = Stopwatch.GetTimestamp();
                        double processingMs = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;
                        frames++;
                        lock (_gate)
                        {
                            _metrics[_metricIndex] = new(frame.Sequence, frame.CaptureTicks, completed, processingMs,
                                accepted, _runtime.VisionStatus, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart,
                                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
                            _metricIndex = (_metricIndex + 1) % _metrics.Length;
                            _metricCount = Math.Min(_metricCount + 1, _metrics.Length);
                            // A low-rate preview copy does not retain a leased capture resource.
                            if (Stopwatch.GetElapsedTime(previewAt, completed).TotalMilliseconds >= 200)
                            {
                                var copy = ArrayPool<byte>.Shared.Rent(checked(frame.Stride * frame.Height));
                                frame.Pixels.Span.CopyTo(copy);
                                _preview?.Dispose();
                                _preview = new(copy, frame.Width, frame.Height, frame.Stride, frame.Sequence);
                                previewAt = completed;
                            }
                            _status = new(true, model is not null, !cancellation.IsCancellationRequested && !_trainingInhibited && fresh && model?.CanArm == true && sourceReady,
                                fresh ? modelMessage : "Source is stale or unchanged. " + modelMessage,
                                frames, predictions, source.DroppedFrames, processingMs, frame.CaptureTicks, frame.Width, frame.Height);
                        }
                    }
                }
                else if (!source.IsRunning)
                    throw new InvalidOperationException(source.Message);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) _status = _status with { Message = "Observation stopped: " + error.Message };
        }
        finally
        {
            _runtime.InvalidateVisionSession("Vision observation is stopped.");
            lock (_gate)
            {
                _recorder?.Dispose();
                if (_trainingInhibited) _runtime.SetVisionTrainingRecording(false);
                _trainingInhibited = false;
            }
            lock (_gate)
            {
                if (model is not null) _lastModelEvents = model.Diagnostics.ExportEvents();
                _model = null;
                _status = _status with { Running = false, CanArm = false,
                    Message = cancellation.IsCancellationRequested ? "Observation stopped." : _status.Message };
            }
            try { model?.Dispose(); }
            catch (Exception error) { lock (_gate) _status = _status with { Message = "Model shutdown failed: " + error.Message }; }
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _model?.RequestStop();
            _recorder?.Stop();
            _preview?.Dispose(); _preview = null;
            _status = _status with { CanArm = false, Message = "Stopping observation…" };
        }
        _runtime.InvalidateVisionSession("Vision stopped by the player.");
    }

    internal void ExportDiagnostics(string path)
    {
        FrameMetric[] metrics;
        VisionSessionStatus status;
        string? modelHash;
        VisionDiagnosticEvent[] modelEvents;
        lock (_gate)
        {
            metrics = new FrameMetric[_metricCount];
            int first = (_metricIndex - _metricCount + _metrics.Length) % _metrics.Length;
            for (int i = 0; i < metrics.Length; i++) metrics[i] = _metrics[(first + i) % _metrics.Length];
            status = _status;
            modelHash = _modelHash;
            modelEvents = _model?.Diagnostics.ExportEvents() ?? _lastModelEvents;
        }
        var samples = metrics.Select(item => item.ProcessingMs).Order().ToArray();
        double Percentile(double quantile) => samples.Length == 0 ? 0 : samples[Math.Max(0, (int)Math.Ceiling(samples.Length * quantile) - 1)];
        var report = new
        {
            version = 1, exportedAtUtc = DateTimeOffset.UtcNow, stopwatchFrequency = Stopwatch.Frequency,
            timingScope = "PC capture, processing and local controller handoff. Xbox render and applied input times are not measured.",
            gcScope = "Collection counts during a frame correlate with GC activity; they are not measured pause durations. Allocation bytes cover the vision worker thread only.",
            sampleCount = samples.Length, p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99),
            maximumMs = samples.LastOrDefault(), status, output = _runtime.VisionStatus, frames = metrics,
            modelManifestHash = modelHash, modelEvents
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal void StartTraining(string directory, Nba2K27ShotProfile profile, string sourceTitle)
    {
        lock (_gate)
        {
            if (!_status.Running || _activeSettings is null) throw new InvalidOperationException("Start window observation before recording training data.");
            if (_runtime.ScriptsEnabled || _runtime.Recording.IsRecording)
                throw new InvalidOperationException("Turn off macro scripts and finish macro recording before capturing manual gameplay training data.");
            if (_recorder?.IsRecording == true) throw new InvalidOperationException("A training take is already recording.");
            if (_activeSettings.AnimationProfile != "nba2k27:" + profile.ConfigurationFingerprint)
                throw new InvalidOperationException("Restart observation using the selected NBA 2K27 shot profile before recording.");
            if (_activeSettings.CameraView != profile.CameraView)
                throw new InvalidOperationException("Restart observation using the selected camera view before recording.");
            _runtime.SetVisionTrainingRecording(true);
            _trainingInhibited = true;
            _status = _status with { CanArm = false };
            _recorder?.Dispose();
            try { _recorder = new VisionTrainingRecorder(directory, profile, _activeSettings, sourceTitle); }
            catch
            {
                _runtime.SetVisionTrainingRecording(false);
                _trainingInhibited = false;
                throw;
            }
        }
    }

    internal void StopTraining() { lock (_gate) _recorder?.Stop(complete: true); }

    public void Dispose()
    {
        Thread? worker;
        lock (_gate) { if (_disposed) return; _disposed = true; worker = _worker; }
        Stop();
        // The worker owns model/capture disposal and must finish before the controller runtime is disposed.
        if (worker is not null && worker != Thread.CurrentThread) worker.Join();
        _recorder?.Dispose();
        lock (_gate) { _stop?.Dispose(); _stop = null; }
    }
}
