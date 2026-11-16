using System.Diagnostics;
using System.Buffers;
using System.Text.Json;
using System.Runtime.InteropServices;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal readonly record struct VisionSessionStatus(bool Running, bool ModelReady, bool CanArm, string Message,
    long Frames, long Predictions, long DroppedFrames, double LastProcessingMs, long LatestCaptureTicks,
    int CaptureWidth = 0, int CaptureHeight = 0, long ExcludedTimingFrames = 0,
    bool ModelLoadFailed = false, bool SourceStale = false, bool CaptureFailed = false,
    CapturePipelineSnapshot? CapturePipeline = null);

internal sealed class VisionPreview(byte[] pixels, int width, int height, int stride, long sequence, long captureTicks = 0, VisionRegionObservation? tracking = null, VisionSignalObservation? signal = null) : IDisposable
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public long Sequence { get; } = sequence;
    public long CaptureTicks { get; } = captureTicks;
    public VisionRegionObservation? Tracking { get; } = tracking;
    public VisionSignalObservation? Signal { get; } = signal;
    public int Length => checked(Stride * Height);
    private static CapturedCropFrame? SliceCrop(CapturedVisionFrame frame, VisionCrop crop)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return null;
        int x0 = Math.Clamp((int)Math.Floor(crop.Left * frame.Width), 0, frame.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(crop.Top * frame.Height), 0, frame.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((crop.Left + crop.Width) * frame.Width), x0 + 1, frame.Width);
        int y1 = Math.Clamp((int)Math.Ceiling((crop.Top + crop.Height) * frame.Height), y0 + 1, frame.Height);
        int cw = x1 - x0, ch = y1 - y0, cStride = cw * 4;
        byte[] pixels = new byte[checked(cStride * ch)];
        var src = frame.Pixels.Span;
        for (int y = 0; y < ch; y++)
            src.Slice((y0 + y) * frame.Stride + x0 * 4, cStride).CopyTo(pixels.AsSpan(y * cStride, cStride));
        return new(pixels, cw, ch, cStride, frame.CaptureTicks);
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels);
}

internal sealed record CapturedCropFrame(byte[] Pixels, int Width, int Height, int Stride, long CaptureTicks);

/// <summary>Capture and inference live outside controller locks. Only StudioRuntime arbitrates output.</summary>
internal sealed class VisionSession : IDisposable
{
    private readonly StudioRuntime _runtime;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Thread? _worker;
    private VisionModel? _model;
    private VisionTrainingRecorder? _recorder;
    private VisionDiagnosticRecorder? _diagnosticRecorder;
    private VisionRegionObservation? _tracking;
    private IVisionRegionTracker? _regionTracker;
    private IVisionLivePredictor? _livePredictor;
    private VisionSignalObservation? _signal;
    internal VisionSignalObservation? Signal => Volatile.Read(ref _signal);
    internal VisionRegionObservation? Tracking => Volatile.Read(ref _tracking);
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
    private long _attemptBeganAt;
    private bool _attemptNeedsNeutral;
    private static int _attemptWritePending;
    private static long _attemptReportNumber;
    private (VisionCrop Crop, TaskCompletionSource<CapturedCropFrame?> Completion)? _cropRequest;
    private bool _disposed;
    private readonly record struct FrameMetric(long Sequence, long CaptureTicks, long CompletedTicks,
        double ProcessingMs, bool PredictionSubmitted, bool PredictionAccepted, VisionStatusSnapshot Output,
        long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, long? CompositorTicks,
        VisionSignalObservation? Signal, VisionSignalContext? PhysicalContext, ControllerState? RequestedOutput, bool SourceFresh);

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
    internal VisionDiagnosticStatus? DiagnosticSnapshot { get { lock (_gate) return _diagnosticRecorder?.Snapshot; } }
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

    internal async Task<CapturedCropFrame?> CaptureRegionOnceAsync(VisionCrop crop, CancellationToken cancellation)
    {
        crop.Validate();
        TaskCompletionSource<CapturedCropFrame?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || _stopping || _worker?.IsAlive != true) return null;
            _cropRequest?.Completion.TrySetResult(null);
            _cropRequest = (crop, tcs);
        }
        using var registration = cancellation.Register(() => tcs.TrySetCanceled(cancellation));
        return await tcs.Task.ConfigureAwait(false);
    }

    internal void Start(nint hwnd, VisionSettings settings, bool loadModel = true, IVisionRegionTracker? regionTracker = null, IVisionLivePredictor? predictor = null)
    {
        settings.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker?.IsAlive == true) throw new InvalidOperationException("Stop the current observation session first.");
            if (_diagnosticRecorder?.Snapshot is { IsFinalized: false })
                throw new InvalidOperationException("Wait for the diagnostic recording to finish saving.");
            _stopping = false;
            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            var cancellation = _stop.Token;
            _runtime.ConfigureVision(settings, predictor?.Action);
            _livePredictor = predictor; Volatile.Write(ref _signal, null);
            _activeSettings = settings;
            Volatile.Write(ref _tracking, null);
            _runtime.InvalidateVisionSession("Preparing capture and model.");
            _status = new(true, false, false, "Starting window capture…", 0, 0, 0, 0, 0);
            _metricCount = _metricIndex = 0;
            _attemptBeganAt = 0; _attemptNeedsNeutral = false;
            _lockMeasurementToken = _runtime.BeginLockMeasurements();
            _performance.Reset(new(predictor?.Name ?? "Capture only", null, settings.AnimationProfile, settings.CameraView,
                settings.Crop, 0, 0, VisionPerformanceBuffer.Fingerprint(settings), _lockMeasurementToken, settings.PreprocessingBackendName));
            _performanceReplacedFrames = _performanceDropBaseline = 0;
            _performanceReleaseBaseline = _runtime.VisionStatus.AutomatedReleases;
            _performanceMissBaseline = _runtime.VisionStatus.MissedDeadlines;
            _modelHash = null; _lastModelEvents = [];
            _preview?.Dispose(); _preview = null;
            _regionTracker = regionTracker;
            _worker = new Thread(() => Run(hwnd, settings, cancellation, loadModel, regionTracker, predictor))
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

    private void Run(nint hwnd, VisionSettings settings, CancellationToken cancellation, bool loadModel, IVisionRegionTracker? regionTracker, IVisionLivePredictor? predictor)
    {
        VisionModel? model = null;
        // Capture-only automatic sessions retain the full scene. Analysis samples independently;
        // exact-frame player/HUD crops are generated offline from the retained recording.
        VisionAcquisitionWorker? acquisition = predictor is null && !loadModel && regionTracker is not null
            && settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Automatic ? new(regionTracker, cancellation) : null;
        bool modelLoadFailed = false;
        try
        {
            using var capture = new RemotePlayCapture();
            bool gpuFrames = predictor is null && loadModel && !string.IsNullOrWhiteSpace(settings.ModelManifestPath)
                && settings.PreprocessingBackend == VisionPreprocessingBackend.D3D11Cuda;
            nint adapter = gpuFrames ? CudaD3D11Interop.FindAdapter() : 0;
            try { capture.Start(hwnd, gpuFrames, adapter); }
            finally { if (adapter != 0) Marshal.Release(adapter); }
            string modelMessage = predictor?.Status ?? "Capture only. Select a validated animation model to enable assistance.";
            if (predictor is null && loadModel && !string.IsNullOrWhiteSpace(settings.ModelManifestPath))
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
            int sourceWidth = 0, sourceHeight = 0;
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
                    QueueAttemptDiagnostics(now, "Capture became unavailable or stale.");
                    sourceReady = false;
                    model?.ResetTracking();
                    predictor?.Reset();
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
                            predictor?.Reset();
                        }
                        var observedStatus = _runtime.VisionStatus;
                        if (sourceReady && !observedStatus.SourceReady)
                        { sourceReady = false; model?.ResetTracking(); predictor?.Reset(); }
                        long started = Stopwatch.GetTimestamp();
                        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
                        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                        VisionTrainingRecorder? recorder;
                        lock (_gate) recorder = _recorder;
                        VisionRegionObservation? regions = null;
                        if (acquisition is not null)
                        {
                            if (acquisition.TakeCompleted() is { } analyzed)
                            {
                                var preview = analyzed.Preview;
                                if (analyzed.Generation == frame.SourceGeneration && preview.Width == frame.Width && preview.Height == frame.Height)
                                {
                                    Volatile.Write(ref _tracking, preview.Tracking);
                                    lock (_gate)
                                    {
                                        if (_previewEnabled && !cancellation.IsCancellationRequested && Stopwatch.GetElapsedTime(previewAt).TotalMilliseconds >= 200)
                                        { _preview?.Dispose(); _preview = preview; previewAt = Stopwatch.GetTimestamp(); }
                                        else preview.Dispose();
                                    }
                                }
                                else { preview.Dispose(); Volatile.Write(ref _tracking, null); }
                            }
                            acquisition.TryQueue(frame);
                        }
                        else
                        {
                            regions = regionTracker?.Track(frame.Pixels.Span, frame.Width, frame.Height, frame.Stride, frame.Sequence, frame.SourceGeneration, frame.CaptureTicks);
                            Volatile.Write(ref _tracking, regions);
                        }
                        frame.PlayerCrop = regions is { Valid: true } ? regions.Player : null;
                        recorder?.Capture(frame, observedStatus, regions, source.Pipeline);
                        if (sourceGeneration != frame.SourceGeneration || sourceWidth != frame.Width || sourceHeight != frame.Height)
                        {
                            _runtime.InvalidateVisionSession("Capture source changed. Reacquiring the configured signal.");
                            sourceReady = false;
                            sourceGeneration = frame.SourceGeneration;
                            sourceWidth = frame.Width; sourceHeight = frame.Height;
                            model?.ResetTracking();
                            predictor?.Reset();
                            var contextOutput = _runtime.VisionStatus;
                            lock (_gate)
                            {
                                _lockMeasurementToken = _runtime.BeginLockMeasurements();
                                _performance.Reset(new(model?.ExecutionProvider ?? predictor?.Name ?? "Capture only", model?.ManifestHash,
                                    settings.AnimationProfile, settings.CameraView, settings.Crop, frame.Width, frame.Height,
                                    VisionPerformanceBuffer.Fingerprint(settings), _lockMeasurementToken, settings.PreprocessingBackendName));
                                _performanceDropBaseline = source.DroppedFrames; _performanceReplacedFrames = 0;
                                _performanceReleaseBaseline = contextOutput.AutomatedReleases;
                                _performanceMissBaseline = contextOutput.MissedDeadlines;
                            }
                        }
                        // Readiness invalidation from profile/controller/Stop all must never auto-rearm output.
                        if (fresh && (model?.CanArm == true || (loadModel && predictor?.CanArm == true)) && regionTracker is null && !sourceReady)
                        {
                            _runtime.BeginVisionSession();
                            sourceReady = true;
                            observedStatus = _runtime.VisionStatus;
                        }
                        bool accepted = false;
                        bool submitted = false;
                        bool hasDecision = false;
                        VisionSignalContext? signalContext = _runtime.ReadVisionSignalContext();
                        if (predictor is not null)
                        {
                            var context = signalContext.Value;
                            var signal = predictor.Observe(frame.Pixels.Span, frame.Width, frame.Height, frame.Stride,
                                frame.Sequence, frame.SourceGeneration, frame.CaptureTicks, context);
                            Volatile.Write(ref _signal, signal); modelMessage = predictor.Status;
                            if (signal.TimeToTargetMs.HasValue || signal.InvalidateAttempt || signal.ContinueCommittedAction)
                            {
                                hasDecision = true; predictions++;
                                if (fresh && sourceReady && predictor.CanArm)
                                {
                                    submitted = true;
                                    accepted = _runtime.SubmitVisionPrediction(new(context.SessionGeneration, context.ShotToken,
                                        frame.Sequence, frame.CaptureTicks, Stopwatch.GetTimestamp(), signal.TimeToTargetMs ?? 0,
                                        signal.Confidence, signal.UncertaintyMs, signal.Acquired && !signal.InvalidateAttempt)
                                    { ContinueCommittedAction = signal.ContinueCommittedAction && !signal.InvalidateAttempt });
                                }
                            }
                        }
                        if (model is not null && (regions is null || regions.Valid))
                        {
                            var prediction = model.PushFrame(frame, observedStatus);
                            if (prediction is { } observation && !cancellation.IsCancellationRequested)
                            {
                                hasDecision = true;
                                predictions++;
                                if (fresh && sourceReady && model.CanArm)
                                {
                                    submitted = true;
                                    accepted = _runtime.SubmitVisionPrediction(observation);
                                }
                            }
                        }
                        else if (regions is { Valid: false }) model?.ResetTracking();
                        long completed = Stopwatch.GetTimestamp();
                        double processingMs = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds;
                        VisionDiagnosticRecorder? diagnostic;
                        lock (_gate) diagnostic = _diagnosticRecorder;
                        diagnostic?.Capture(frame, completed, processingMs, predictor is null ? null : _signal,
                            signalContext, _runtime.Latest?.Output, _runtime.VisionStatus, _runtime.LastVisionAction,
                            fresh, submitted, accepted, source.Pipeline);
                        double? preprocessingMs = null, inferenceMs = null;
                        model?.Diagnostics.FrameTimings(frame.Sequence, out preprocessingMs, out inferenceMs);
                        if (predictor is not null) { preprocessingMs = _signal?.ProcessingMs; inferenceMs = 0; }
                        frames++;
                        bool buildPreview;
                        (VisionCrop Crop, TaskCompletionSource<CapturedCropFrame?> Completion)? cropRequest;
                        lock (_gate)
                        {
                            buildPreview = acquisition is null && _previewEnabled && !cancellation.IsCancellationRequested &&
                                Stopwatch.GetElapsedTime(previewAt, completed).TotalMilliseconds >= 200;
                            cropRequest = _cropRequest;
                            _cropRequest = null;
                        }
                        if (cropRequest is { } pendingCrop)
                            pendingCrop.Completion.TrySetResult(SliceCrop(frame, pendingCrop.Crop));
                        if (buildPreview)
                        {
                            // GPU readback can wait on the driver. Never hold the
                            // UI/Stop state lock while materializing CPU pixels.
                            var copy = ArrayPool<byte>.Shared.Rent(checked(frame.Stride * frame.Height));
                            VisionPreview? nextPreview = null;
                            try { frame.Pixels.Span.CopyTo(copy); nextPreview = new(copy, frame.Width, frame.Height, frame.Stride, frame.Sequence, frame.CaptureTicks, regions, _signal); }
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
                                submitted, accepted, _runtime.VisionStatus, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart,
                                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, frame.CompositorTicks,
                                predictor is null ? null : _signal, signalContext, _runtime.Latest?.Output, fresh);
                            _metricIndex = (_metricIndex + 1) % _metrics.Length;
                            _metricCount = Math.Min(_metricCount + 1, _metrics.Length);
                            _performanceReplacedFrames = Math.Max(0, source.DroppedFrames - _performanceDropBaseline);
                            if (!_performance.Record(new(frame.Sequence, frame.CaptureTicks, started, completed, hasDecision && inferenceMs.HasValue,
                                preprocessingMs, inferenceMs, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart,
                                Math.Max(0, GC.CollectionCount(0) - gen0), Math.Max(0, GC.CollectionCount(1) - gen1),
                                Math.Max(0, GC.CollectionCount(2) - gen2), _trainingInhibited, frame.CaptureReadbackMs,
                                frame.OptionalReadbackBytes, frame.OptionalReadbackMs, frame.FreshnessReadbackBytes))) excludedTimingFrames++;
                            _status = new(true, model is not null || predictor is not null, !cancellation.IsCancellationRequested && !_trainingInhibited && fresh && (model?.CanArm == true || (loadModel && predictor?.CanArm == true)) && sourceReady,
                                fresh ? modelMessage : "Source is stale or unchanged. " + modelMessage,
                                frames, predictions, source.DroppedFrames, processingMs, frame.CaptureTicks, frame.Width, frame.Height,
                                excludedTimingFrames, modelLoadFailed, !fresh, CapturePipeline: source.Pipeline);
                        }
                        if (signalContext is { } physical && predictor is not null)
                        {
                            // Trace ordinary attempts, including unarmed or wrongly directed holds.
                            // Neither this trace nor its disk writer changes controller output.
                            bool active = physical.Physical is { } state && (predictor.Action.Kind == VisionActionKind.RightStickStroke
                                ? (predictor.Action.TriggerPaddle != PaddleButtons.None
                                    ? (physical.HeldPaddles & predictor.Action.TriggerPaddle) == predictor.Action.TriggerPaddle
                                    : Math.Abs((int)state.RightY) >= predictor.Action.AxisThreshold || Math.Abs((int)state.RightX) >= predictor.Action.AxisThreshold)
                                : (state.Buttons & (ushort)settings.ShootButton) != 0);
                            if (!active) { QueueAttemptDiagnostics(completed, "Physical input returned below the hold threshold."); _attemptNeedsNeutral = false; }
                            else if (!_attemptNeedsNeutral)
                            {
                                if (_attemptBeganAt == 0) _attemptBeganAt = frame.CaptureTicks;
                                if (Stopwatch.GetElapsedTime(_attemptBeganAt, completed).TotalMilliseconds >= settings.MaximumShotDurationMs)
                                { QueueAttemptDiagnostics(completed, "Maximum attempt duration reached."); _attemptNeedsNeutral = true; }
                            }
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
            QueueAttemptDiagnostics(Stopwatch.GetTimestamp(), "Observation ended.");
            StopDiagnosticRecording();
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
                _cropRequest?.Completion.TrySetResult(null);
                _cropRequest = null;
                if (model is not null) _lastModelEvents = model.Diagnostics.ExportEvents();
                _model = null;
                _status = _status with { Running = false, CanArm = false, CaptureFailed = !cancellation.IsCancellationRequested && _status.CaptureFailed,
                    Message = cancellation.IsCancellationRequested ? "Observation stopped." : _status.Message };
            }
            try { model?.Dispose(); predictor?.Dispose(); _livePredictor = null; }
            catch (Exception error) { lock (_gate) _status = _status with { Message = "Model shutdown failed: " + error.Message }; }
            try { acquisition?.Dispose(); }
            catch (Exception error) { lock (_gate) _status = _status with { Message = "Capture analysis shutdown failed: " + error.Message }; }
            try { regionTracker?.Dispose(); }
            catch (Exception error) { lock (_gate) _status = _status with { Message = "Capture detector shutdown failed: " + error.Message }; }
            finally { lock (_gate) _regionTracker = null; }
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            _stop?.Cancel();
            _model?.RequestStop();
            _regionTracker?.RequestStop();
            _recorder?.Stop();
            _diagnosticRecorder?.Stop("Preview stopped.");
            _preview?.Dispose(); _preview = null;
            _status = _status with { CanArm = false, Message = "Stopping observation…" };
        }
        _runtime.InvalidateVisionSession("Vision stopped by the player.");
    }

    private void QueueAttemptDiagnostics(long completedAt, string reason)
    {
        long began = _attemptBeganAt; _attemptBeganAt = 0;
        if (began != 0) _attemptNeedsNeutral = true;
        if (began == 0 || Stopwatch.GetElapsedTime(began, completedAt).TotalMilliseconds < (_livePredictor?.Action.HoldQualificationMs ?? 0)
            || Interlocked.CompareExchange(ref _attemptWritePending, 1, 0) != 0) return;
        object report;
        try { report = DiagnosticsReport(began, completedAt, reason); }
        catch (Exception error)
        { Volatile.Write(ref _attemptWritePending, 0); Debug.WriteLine("Vision attempt snapshot: " + error.Message); return; }
        // Keep eight recent reports, written away from both capture and controller threads.
        // No video, audio, training data or user configuration is written here.
        _ = Task.Run(async () =>
        {
            try
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "Diagnostics", "Vision");
                Directory.CreateDirectory(directory);
                long slot = Interlocked.Increment(ref _attemptReportNumber) % 8;
                string path = Path.Combine(directory, $"attempt-{slot}.json"), temporary = path + ".tmp";
                await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) { Debug.WriteLine("Vision attempt diagnostics: " + error.Message); }
            finally { Volatile.Write(ref _attemptWritePending, 0); }
        });
    }

    internal void ExportDiagnostics(string path) => File.WriteAllText(path,
        JsonSerializer.Serialize(DiagnosticsReport(0, long.MaxValue, "Manual export"), new JsonSerializerOptions { WriteIndented = true }));

    private object DiagnosticsReport(long fromTimestamp, long throughTimestamp, string reason)
    {
        FrameMetric[] metrics;
        VisionSessionStatus status;
        string? modelHash;
        VisionDiagnosticEvent[] modelEvents;
        lock (_gate)
        {
            int first = (_metricIndex - _metricCount + _metrics.Length) % _metrics.Length;
            int count = 0;
            for (int i = 0; i < _metricCount; i++)
                if (_metrics[(first + i) % _metrics.Length].CaptureTicks is var at && at >= fromTimestamp && at <= throughTimestamp) count++;
            metrics = new FrameMetric[count];
            for (int i = 0, target = 0; i < _metricCount; i++)
            {
                var metric = _metrics[(first + i) % _metrics.Length];
                if (metric.CaptureTicks >= fromTimestamp && metric.CaptureTicks <= throughTimestamp) metrics[target++] = metric;
            }
            status = _status;
            modelHash = _modelHash;
            modelEvents = _model?.Diagnostics.ExportEvents() ?? _lastModelEvents;
        }
        var samples = metrics.Select(item => item.ProcessingMs).Order().ToArray();
        double Percentile(double quantile) => samples.Length == 0 ? 0 : samples[Math.Max(0, (int)Math.Ceiling(samples.Length * quantile) - 1)];
        var report = new
        {
            version = 4, exportedAtUtc = DateTimeOffset.UtcNow, stopwatchFrequency = Stopwatch.Frequency,
            reason, fromTimestamp, throughTimestamp,
            predictor = _livePredictor is { } predictor ? new { predictor.Name,
                assembly = predictor.GetType().Assembly.GetName().Name,
                version = predictor.GetType().Assembly.GetName().Version?.ToString(), predictor.CanArm, predictor.Status, predictor.Action } : null,
            settings = _activeSettings, lastAction = _runtime.LastVisionAction, outputStatus = _runtime.Latest?.OutputStatus,
            timingScope = "CaptureTicks marks local WGC frame receipt before image copies. CompositorTicks retains unmodified WGC metadata and may lead receipt. Processing and local controller handoff are measured; Xbox render and applied input times are not.",
            gcScope = "Collection counts during a frame correlate with GC activity; they are not measured pause durations. Allocation bytes cover the vision worker thread only.",
            sampleCount = samples.Length, p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99),
            maximumMs = samples.LastOrDefault(), status, output = _runtime.VisionStatus, frames = metrics,
            modelManifestHash = modelHash, modelEvents, performance = Performance
        };
        return report;
    }

    internal void StartTraining(string directory, IVisionCaptureProfile profile, string sourceTitle, VisionSettings? captureOverride = null)
    {
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("Live view is stopping. Record a training clip after it has stopped.");
            if (_diagnosticRecorder?.Snapshot is { IsFinalized: false })
                throw new InvalidOperationException("Finish saving the diagnostic recording before recording a training clip.");
            if (!_status.Running || _activeSettings is null) throw new InvalidOperationException("Start live view before recording training data.");
            if (captureOverride is null && _model is null && _activeSettings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Automatic)
                captureOverride = profile.PrepareCalibrationCapture(_activeSettings, _status.CaptureWidth, _status.CaptureHeight);
            var recordingSettings = captureOverride ?? _activeSettings;
            if (captureOverride is not null && (_model is not null || recordingSettings.AnimationProfile != _activeSettings.AnimationProfile
                || recordingSettings.CameraView != _activeSettings.CameraView
                || recordingSettings.TrainingCapture.EvidenceMode != VisionEvidenceMode.SceneFirst))
                throw new InvalidOperationException("Calibration capture requires a capture-only session with matching source/profile geometry.");
            if (_runtime.ScriptsEnabled || _runtime.Recording.IsRecording)
                throw new InvalidOperationException("Turn off macro scripts and finish macro recording before capturing manual gameplay training data.");
            if (_recorder?.IsRecording == true) throw new InvalidOperationException("A training take is already recording.");
            if (_activeSettings.AnimationProfile != profile.AnimationProfile)
                throw new InvalidOperationException("Restart observation using the selected game plugin profile before recording.");
            if (_activeSettings.CameraView != profile.CameraView)
                throw new InvalidOperationException("Restart observation using the selected camera view before recording.");
            profile.ValidateCapture(recordingSettings.TrainingCapture);
            if (recordingSettings.TrainingCapture.EvidenceMode is VisionEvidenceMode.Anchored or VisionEvidenceMode.Automatic && Tracking is not { Valid: true })
                throw new InvalidOperationException(Tracking?.Message ?? "Start preview and acquire the NBA capture anchors before recording.");
            if (_status.CaptureWidth != recordingSettings.TrainingCapture.SourceWidth ||
                _status.CaptureHeight != recordingSettings.TrainingCapture.SourceHeight)
                throw new InvalidOperationException("Wait for a fresh live preview and confirm training capture setup for the current window size.");
            _runtime.SetVisionTrainingRecording(true);
            _trainingInhibited = true;
            _status = _status with { CanArm = false };
            _recorder?.Dispose();
            try
            {
                var trace = new VisionTrainingTrace();
                _runtime.SetVisionTrainingTrace(trace);
                _recorder = new VisionTrainingRecorder(directory, profile, recordingSettings, sourceTitle,
                    controllerTrace: trace, detachControllerTrace: () => _runtime.SetVisionTrainingTrace(null),
                    capturePipelineAtStart: _status.CapturePipeline);
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

    internal void StartDiagnosticRecording(string parent, object context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping || !_status.Running || _status.CaptureWidth <= 0 || _activeSettings is null)
                throw new InvalidOperationException("Start the preview and wait for gameplay before recording diagnostics.");
            if (_diagnosticRecorder?.Snapshot is { IsFinalized: false })
                throw new InvalidOperationException("A diagnostic recording is already recording or saving.");
            if (_recorder?.Snapshot is { HasTake: true, IsFinalized: false })
                throw new InvalidOperationException("Finish the training clip before recording diagnostics.");
            _diagnosticRecorder?.Dispose();
            _diagnosticRecorder = new(parent, new
            {
                selection = context, settings = _activeSettings,
                appVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                predictor = _livePredictor is { } predictor ? new { predictor.Name,
                    assembly = predictor.GetType().Assembly.GetName().Name,
                    version = predictor.GetType().Assembly.GetName().Version?.ToString(), predictor.Action } : null
            });
        }
    }
    internal void StopDiagnosticRecording() { lock (_gate) _diagnosticRecorder?.Stop(); }

    internal Task FinishDiagnosticRecordingAsync(CancellationToken cancellation)
    {
        VisionDiagnosticRecorder? recorder;
        lock (_gate) { recorder = _diagnosticRecorder; recorder?.Stop(); }
        // Waiting for the capture worker alone does not drain the image writer.
        // Keep its exact recording until it has finalized before restarting capture.
        return recorder?.Completion.WaitAsync(cancellation) ?? Task.CompletedTask;
    }

    private static CapturedCropFrame? SliceCrop(CapturedVisionFrame frame, VisionCrop crop)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return null;
        int x0 = Math.Clamp((int)Math.Floor(crop.Left * frame.Width), 0, frame.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(crop.Top * frame.Height), 0, frame.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((crop.Left + crop.Width) * frame.Width), x0 + 1, frame.Width);
        int y1 = Math.Clamp((int)Math.Ceiling((crop.Top + crop.Height) * frame.Height), y0 + 1, frame.Height);
        int cw = x1 - x0, ch = y1 - y0, cStride = cw * 4;
        byte[] pixels = new byte[checked(cStride * ch)];
        var src = frame.Pixels.Span;
        for (int y = 0; y < ch; y++)
            src.Slice((y0 + y) * frame.Stride + x0 * 4, cStride).CopyTo(pixels.AsSpan(y * cStride, cStride));
        return new(pixels, cw, ch, cStride, frame.CaptureTicks);
    }

    public void Dispose()
    {
        Thread? worker;
        lock (_gate) { if (_disposed) return; _disposed = true; worker = _worker; }
        Stop();
        // The worker owns model/capture disposal and must finish before the controller runtime is disposed.
        if (worker is not null && worker != Thread.CurrentThread) worker.Join();
        _recorder?.Dispose();
        _diagnosticRecorder?.Dispose();
        lock (_gate) { _stop?.Dispose(); _stop = null; }
    }
}
