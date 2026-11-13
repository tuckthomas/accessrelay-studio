using System.Diagnostics;
using System.Buffers;
using System.Text.Json;
using System.Runtime.InteropServices;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal readonly record struct VisionSessionStatus(bool Running, bool ModelReady, bool CanArm, string Message,
    long Frames, long Predictions, long DroppedFrames, double LastProcessingMs, long LatestCaptureTicks,
    int CaptureWidth = 0, int CaptureHeight = 0, long ExcludedTimingFrames = 0,
    bool ModelLoadFailed = false, bool SourceStale = false, bool CaptureFailed = false);

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
    private bool _trainingInhibited, _stopping;
    private string? _modelHash;
    private VisionDiagnosticEvent[] _lastModelEvents = [];
    private VisionPreview? _preview;
    private bool _previewEnabled;
    private VisionSessionStatus _status = new(false, false, false, "Observation is off.", 0, 0, 0, 0, 0);
    private readonly FrameMetric[] _metrics = new FrameMetric[2048];
    private readonly VisionPerformanceBuffer _performance = new();
    private long _lockMeasurementToken;
    private long _performanceReplacedFrames, _performanceDropBaseline, _performanceReleaseBaseline, _performanceMissBaseline;
    private int _metricIndex, _metricCount;
    private bool _disposed;
    private readonly record struct FrameMetric(long Sequence, long CaptureTicks, long CompletedTicks,
        double ProcessingMs, bool PredictionAccepted, VisionStatusSnapshot Output,
        long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, long? CompositorTicks);

    internal VisionSession(StudioRuntime runtime) => _runtime = runtime;
    internal VisionSessionStatus Status { get { lock (_gate) return _status; } }
    internal bool IsStopping { get { lock (_gate) return _worker?.IsAlive == true && (_stopping || !_status.Running); } }
    internal VisionPerformanceSnapshot Performance
    {
        get
        {
            long replaced, releaseBaseline, missBaseline, lockToken; bool training;
            lock (_gate)
            {
                replaced = _performanceReplacedFrames; releaseBaseline = _performanceReleaseBaseline;
                missBaseline = _performanceMissBaseline; training = _trainingInhibited;
                lockToken = _lockMeasurementToken;
            }
            var output = _runtime.VisionStatus;
            return _performance.Snapshot(replaced, Math.Max(0, output.AutomatedReleases - releaseBaseline),
                Math.Max(0, output.MissedDeadlines - missBaseline), _runtime.RecentOutputHandoffMs, output.Armed, training, _runtime.LockMeasurements(lockToken));
        }
    }
    internal VisionPerformanceRun SavePerformanceRun(string label)
    {
        var snapshot = Performance;
        if (snapshot.TotalFrames == 0) throw new InvalidOperationException("Observe a source before saving a performance run.");
        return new() { Label = string.IsNullOrWhiteSpace(label) ? snapshot.ExecutionProvider + " observation" : label.Trim(), Snapshot = snapshot };
    }
    internal string TrainingStatus { get { lock (_gate) return _recorder?.Message ?? "No training take is recording."; } }
    internal bool IsRecordingTraining { get { lock (_gate) return _recorder?.IsRecording == true; } }
    internal VisionTrainingStatus TrainingSnapshot
    {
        get
        {
            lock (_gate) return _recorder?.Snapshot ?? new(false, false, false, false, "", 0, 0,
                "Viewing only. Observation frames are not saved as training data.");
        }
    }
    internal VisionPreview? TakePreview() { lock (_gate) { var value = _preview; _preview = null; return value; } }

    // Presentation is optional. Hiding a tab must not stop observation, recording or controller output.
    internal void SetPreviewEnabled(bool enabled)
    {
        lock (_gate)
        {
            _previewEnabled = enabled && !_disposed;
            if (!_previewEnabled) { _preview?.Dispose(); _preview = null; }
        }
    }

    internal void Start(nint hwnd, VisionSettings settings, bool loadModel = true)
    {
        settings.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker?.IsAlive == true) throw new InvalidOperationException("Stop the current observation session first.");
            _stopping = false;
            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            var cancellation = _stop.Token;
            _runtime.ConfigureVision(settings);
            _activeSettings = settings;
            _runtime.InvalidateVisionSession("Preparing capture and model.");
            _status = new(true, false, false, "Starting window capture…", 0, 0, 0, 0, 0);
            _metricCount = _metricIndex = 0;
            _lockMeasurementToken = _runtime.BeginLockMeasurements();
            _performance.Reset(new("Capture only", null, settings.AnimationProfile, settings.CameraView,
                settings.Crop, 0, 0, VisionPerformanceBuffer.Fingerprint(settings), _lockMeasurementToken, settings.PreprocessingBackendName));
            _performanceReplacedFrames = _performanceDropBaseline = 0;
            _performanceReleaseBaseline = _runtime.VisionStatus.AutomatedReleases;
            _performanceMissBaseline = _runtime.VisionStatus.MissedDeadlines;
            _modelHash = null; _lastModelEvents = [];
            _preview?.Dispose(); _preview = null;
            _worker = new Thread(() => Run(hwnd, settings, cancellation, loadModel))
                { IsBackground = true, Name = "AccessRelay vision" };
            try { _worker.Start(); }
            catch
            {
                _performance.Finish(); _runtime.FinishLockMeasurements(_lockMeasurementToken);
                _status = _status with { Running = false, CanArm = false, Message = "Observation worker could not start." };
                throw;
            }
        }
    }

    private void Run(nint hwnd, VisionSettings settings, CancellationToken cancellation, bool loadModel)
    {
        VisionModel? model = null;
        bool modelLoadFailed = false;
        try
        {
            using var capture = new RemotePlayCapture();
            bool gpuFrames = loadModel && !string.IsNullOrWhiteSpace(settings.ModelManifestPath)
                && settings.PreprocessingBackend == VisionPreprocessingBackend.D3D11Cuda;
            nint adapter = gpuFrames ? CudaD3D11Interop.FindAdapter() : 0;
            try { capture.Start(hwnd, gpuFrames, adapter); }
            finally { if (adapter != 0) Marshal.Release(adapter); }
            string modelMessage = "Capture only. Select a validated animation model to enable assistance.";
            if (loadModel && !string.IsNullOrWhiteSpace(settings.ModelManifestPath))
            {
                try
                {
                    model = VisionModel.Load(settings.ModelManifestPath, settings, cancellation,
                        (manifest, configuration) => new CudaTemporalFrameInput(manifest, configuration, capture.GraphicsDevice,
                            capture.Status.Width, capture.Status.Height));
                    lock (_gate) { _model = model; _modelHash = model.ManifestHash; }
                    modelMessage = model.ValidationSummary;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    modelLoadFailed = true;
                    modelMessage = "Capture only. Model unavailable: " + error.Message;
                    lock (_gate) _status = _status with { ModelLoadFailed = true, Message = modelMessage };
                }
            }
            cancellation.ThrowIfCancellationRequested();
            long previewAt = 0, sourceGeneration = -1;
            long frames = 0, predictions = 0, excludedTimingFrames = 0;
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
                    lock (_gate) _status = _status with { CanArm = false, SourceStale = true,
                        Message = "Source is stale or unchanged. " + modelMessage };
                if (capture.TryAcquireLatest(out var frame) && frame is not null)
                {
                    using (frame)
                    {
                        if (frame.CompositorTicks is { } compositor &&
                            Stopwatch.GetElapsedTime(compositor).TotalMilliseconds > settings.MaximumFrameAgeMs)
                        {
                            fresh = false;
                            if (sourceReady) _runtime.InvalidateVisionSession("The compositor frame is stale. Resume observation and rearm.");
                            sourceReady = false;
                            model?.ResetTracking();
                        }
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
                            var contextOutput = _runtime.VisionStatus;
                            lock (_gate)
                            {
                                _lockMeasurementToken = _runtime.BeginLockMeasurements();
                                _performance.Reset(new(model?.ExecutionProvider ?? "Capture only", model?.ManifestHash,
                                    settings.AnimationProfile, settings.CameraView, settings.Crop, frame.Width, frame.Height,
                                    VisionPerformanceBuffer.Fingerprint(settings), _lockMeasurementToken, settings.PreprocessingBackendName));
                                _performanceDropBaseline = source.DroppedFrames; _performanceReplacedFrames = 0;
                                _performanceReleaseBaseline = contextOutput.AutomatedReleases;
                                _performanceMissBaseline = contextOutput.MissedDeadlines;
                            }
                        }
                        // Readiness invalidation from profile/controller/Stop all must never auto-rearm output.
                        if (fresh && model?.CanArm == true && !sourceReady)
                        {
                            _runtime.BeginVisionSession();
                            sourceReady = true;
                            observedStatus = _runtime.VisionStatus;
                        }
                        bool accepted = false;
                        bool hasDecision = false;
                        if (model is not null)
                        {
                            var prediction = model.PushFrame(frame, observedStatus);
                            if (prediction is { } observation && !cancellation.IsCancellationRequested)
                            {
                                hasDecision = true;
                                predictions++;
                                if (fresh && sourceReady && model.CanArm)
                                    accepted = _runtime.SubmitVisionPrediction(observation);
                            }
                        }
                        long completed = Stopwatch.GetTimestamp();
                        double processingMs = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;
                        double? preprocessingMs = null, inferenceMs = null;
                        model?.Diagnostics.FrameTimings(frame.Sequence, out preprocessingMs, out inferenceMs);
                        frames++;
                        bool buildPreview;
                        lock (_gate) buildPreview = _previewEnabled && !cancellation.IsCancellationRequested &&
                            Stopwatch.GetElapsedTime(previewAt, completed).TotalMilliseconds >= 200;
                        if (buildPreview)
                        {
                            // GPU readback can wait on the driver. Never hold the
                            // UI/Stop state lock while materializing CPU pixels.
                            var copy = ArrayPool<byte>.Shared.Rent(checked(frame.Stride * frame.Height));
                            VisionPreview? nextPreview = null;
                            try { frame.Pixels.Span.CopyTo(copy); nextPreview = new(copy, frame.Width, frame.Height, frame.Stride, frame.Sequence); }
                            catch { ArrayPool<byte>.Shared.Return(copy); throw; }
                            lock (_gate)
                            {
                                if (_previewEnabled && !cancellation.IsCancellationRequested)
                                { _preview?.Dispose(); _preview = nextPreview; }
                                else nextPreview.Dispose();
                            }
                            previewAt = completed;
                        }
                        lock (_gate)
                        {
                            _metrics[_metricIndex] = new(frame.Sequence, frame.CaptureTicks, completed, processingMs,
                                accepted, _runtime.VisionStatus, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart,
                                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, frame.CompositorTicks);
                            _metricIndex = (_metricIndex + 1) % _metrics.Length;
                            _metricCount = Math.Min(_metricCount + 1, _metrics.Length);
                            _performanceReplacedFrames = Math.Max(0, source.DroppedFrames - _performanceDropBaseline);
                            if (!_performance.Record(new(frame.Sequence, frame.CaptureTicks, started, completed, hasDecision && inferenceMs.HasValue,
                                preprocessingMs, inferenceMs, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart,
                                Math.Max(0, GC.CollectionCount(0) - gen0), Math.Max(0, GC.CollectionCount(1) - gen1),
                                Math.Max(0, GC.CollectionCount(2) - gen2), _trainingInhibited, frame.CaptureReadbackMs,
                                frame.OptionalReadbackBytes, frame.OptionalReadbackMs, frame.FreshnessReadbackBytes))) excludedTimingFrames++;
                            _status = new(true, model is not null, !cancellation.IsCancellationRequested && !_trainingInhibited && fresh && model?.CanArm == true && sourceReady,
                                fresh ? modelMessage : "Source is stale or unchanged. " + modelMessage,
                                frames, predictions, source.DroppedFrames, processingMs, frame.CaptureTicks, frame.Width, frame.Height,
                                excludedTimingFrames, modelLoadFailed, !fresh);
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
            lock (_gate) _status = _status with { CaptureFailed = !cancellation.IsCancellationRequested,
                Message = cancellation.IsCancellationRequested ? "Observation stopped." : "Observation stopped: " + error.Message };
        }
        finally
        {
            lock (_gate) _stopping = true;
            _performance.Finish();
            _runtime.FinishLockMeasurements(_lockMeasurementToken);
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
                _status = _status with { Running = false, CanArm = false, CaptureFailed = !cancellation.IsCancellationRequested && _status.CaptureFailed,
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
            _stopping = true;
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
            version = 3, exportedAtUtc = DateTimeOffset.UtcNow, stopwatchFrequency = Stopwatch.Frequency,
            timingScope = "CaptureTicks marks local WGC frame receipt before image copies. CompositorTicks retains unmodified WGC metadata and may lead receipt. Processing and local controller handoff are measured; Xbox render and applied input times are not.",
            gcScope = "Collection counts during a frame correlate with GC activity; they are not measured pause durations. Allocation bytes cover the vision worker thread only.",
            sampleCount = samples.Length, p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99),
            maximumMs = samples.LastOrDefault(), status, output = _runtime.VisionStatus, frames = metrics,
            modelManifestHash = modelHash, modelEvents, performance = Performance
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal void StartTraining(string directory, IVisionCaptureProfile profile, string sourceTitle)
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("Live view is stopping. Record a training clip after it has stopped.");
            if (!_status.Running || _activeSettings is null) throw new InvalidOperationException("Start live view before recording training data.");
            if (_runtime.ScriptsEnabled || _runtime.Recording.IsRecording)
                throw new InvalidOperationException("Turn off macro scripts and finish macro recording before capturing manual gameplay training data.");
            if (_recorder?.IsRecording == true) throw new InvalidOperationException("A training take is already recording.");
            if (_activeSettings.AnimationProfile != profile.AnimationProfile)
                throw new InvalidOperationException("Restart observation using the selected game plugin profile before recording.");
            if (_activeSettings.CameraView != profile.CameraView)
                throw new InvalidOperationException("Restart observation using the selected camera view before recording.");
            profile.ValidateCapture(_activeSettings.TrainingCapture);
            if (_status.CaptureWidth != _activeSettings.TrainingCapture.SourceWidth ||
                _status.CaptureHeight != _activeSettings.TrainingCapture.SourceHeight)
                throw new InvalidOperationException("Wait for a fresh live preview and confirm training capture setup for the current window size.");
            _runtime.SetVisionTrainingRecording(true);
            _trainingInhibited = true;
            _status = _status with { CanArm = false };
            _recorder?.Dispose();
            try
            {
                var trace = new VisionTrainingTrace();
                _runtime.SetVisionTrainingTrace(trace);
                _recorder = new VisionTrainingRecorder(directory, profile, _activeSettings, sourceTitle,
                    controllerTrace: trace, detachControllerTrace: () => _runtime.SetVisionTrainingTrace(null));
            }
            catch
            {
                _runtime.SetVisionTrainingTrace(null);
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
