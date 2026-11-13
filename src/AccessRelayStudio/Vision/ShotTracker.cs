using System.Diagnostics;
using EliteScriptStudio.Core;


namespace EliteScriptStudio.Vision;

/// <summary>Fixed-region temporal tracking. It never silently switches to another visible player.</summary>
internal sealed class ShotTracker
{
    private readonly VisionModelManifest _manifest;
    private readonly VisionSettings _settings;
    private readonly ITemporalFrameInput _input;
    private readonly Func<ModelObservation> _infer;
    private readonly Func<long> _getTimestamp;
    private readonly VisionDiagnostics _diagnostics;
    private long _generation = -1, _sourceGeneration = -1, _shot = -1, _lastCapture, _lastSequence = -1;
    private int _historyCount, _width, _height;
    private bool _identityLost;
    private bool _playerRegionFrozen;
    private long _lastPlayerContentChange;

    internal ShotTracker(VisionModelManifest manifest, VisionSettings settings, float[] history,
        Func<ModelObservation> infer, VisionDiagnostics diagnostics, Func<long>? getTimestamp = null, ITemporalFrameInput? input = null)
    {
        _manifest = manifest;
        _settings = settings;
        _input = input ?? new CpuTemporalFrameInput(manifest, settings, history);
        _infer = infer;
        _diagnostics = diagnostics;
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
    }

    internal void Reset()
    {
        _historyCount = 0;
        _lastCapture = 0;
        _lastSequence = -1;
        _generation = _sourceGeneration = _shot = -1;
        _identityLost = false;
        _playerRegionFrozen = false;
        _input.ResetContent();
        _lastPlayerContentChange = 0;
    }

    internal VisionPrediction? PushFrame(CapturedVisionFrame frame, VisionStatusSnapshot snapshot)
    {
        _diagnostics.FrameSeen();
        long now = _getTimestamp();
        // Tokens are captured before processing. A result must never be retagged after inference completes.
        long generation = snapshot.SessionGeneration, shot = snapshot.ShotToken;
        bool sourceChanged = generation != _generation || frame.SourceGeneration != _sourceGeneration ||
            frame.Width != _width || frame.Height != _height;
        if (sourceChanged || shot != _shot)
        {
            _historyCount = 0;
            _lastCapture = 0;
            _lastSequence = -1;
            _identityLost = false;
            _generation = generation;
            _sourceGeneration = frame.SourceGeneration;
            _shot = shot;
            _width = frame.Width;
            _height = frame.Height;
            if (sourceChanged)
            {
                _playerRegionFrozen = false;
                _input.ResetContent();
                _lastPlayerContentChange = 0;
            }
            _diagnostics.Record(VisionDiagnosticKind.HistoryReset, frame.Sequence, generation, shot);
        }
        if (_identityLost)
        {
            _diagnostics.SetStatus("Player identity lost during this attempt; release and reacquire before another shot");
            return null;
        }
        if (frame.CaptureTicks <= 0 || frame.CaptureTicks > now || frame.Sequence <= _lastSequence ||
            (_lastCapture != 0 && frame.CaptureTicks <= _lastCapture) ||
            Stopwatch.GetElapsedTime(frame.CaptureTicks, now).TotalMilliseconds > _settings.MaximumFrameAgeMs ||
            (frame.CompositorTicks is { } compositor &&
                Stopwatch.GetElapsedTime(compositor, now).TotalMilliseconds > _settings.MaximumFrameAgeMs))
        {
            return Reject(frame, snapshot, now, "The player observation has an invalid, repeated or stale capture timestamp");
        }
        double interval = _lastCapture == 0 ? double.PositiveInfinity
            : Stopwatch.GetElapsedTime(_lastCapture, frame.CaptureTicks).TotalMilliseconds;
        if (interval < _manifest.HistoryIntervalMs * .85) return null;
        if (_lastCapture != 0 && interval > _manifest.HistoryIntervalMs * 2.5)
            _historyCount = 0;
        long preprocessingStarted = Stopwatch.GetTimestamp();
        bool shiftHistory = _historyCount == _manifest.HistoryLength;
        if (shiftHistory)
        {
            _historyCount--;
        }
        bool playerPixelsChanged = _input.PrepareFrame(frame, _historyCount, shiftHistory);
        if (playerPixelsChanged)
        {
            _lastPlayerContentChange = frame.CaptureTicks;
            if (_playerRegionFrozen)
            {
                // A changed region is a new acquisition, not continuation of the frozen temporal sequence.
                _input.RestartWithCurrentFrame(_historyCount);
                _historyCount = 0;
                _playerRegionFrozen = false;
            }
        }
        _historyCount++;
        _lastCapture = frame.CaptureTicks;
        _lastSequence = frame.Sequence;
        _diagnostics.Record(VisionDiagnosticKind.Preprocessed, frame.Sequence, generation, shot,
            Stopwatch.GetElapsedTime(preprocessingStarted).TotalMilliseconds);
        if (Stopwatch.GetElapsedTime(_lastPlayerContentChange, now).TotalMilliseconds > _settings.MaximumFrameAgeMs)
        {
            _playerRegionFrozen = true;
            _historyCount = 0;
            // A negative observation must reach the output arbiter, cancelling an already scheduled
            // release. Returning null would leave a prior deadline alive while browser overlays paint.
            return Reject(frame, snapshot, now, "Selected player pixels are unchanged beyond the freshness limit; pending release must be cancelled");
        }
        if (_historyCount < _manifest.HistoryLength)
        {
            _diagnostics.SetStatus("Collecting the model's required temporal history");
            return null;
        }
        ModelObservation observation = _infer();
        long completed = _getTimestamp();
        double confidence = Math.Min(observation.PlayerConfidence, observation.ShotConfidence);
        bool playerTracked = observation.PlayerConfidence >= _settings.MinimumConfidence;
        if (!playerTracked && snapshot.ShootHeld)
            _identityLost = true;
        _diagnostics.Record(VisionDiagnosticKind.InferenceCompleted, frame.Sequence, generation, shot,
            observation.InferenceWithTransfersMs, confidence, observation.UncertaintyMs,
            captureTimestamp: frame.CaptureTicks, timeToTargetMs: observation.TimeToTargetMs);
        _diagnostics.SetStatus(!playerTracked ? "Player identity is not established in the selected region"
            : confidence < _settings.MinimumConfidence ? "Observing; shot confidence is below the configured threshold"
            : "Temporal observation ready; controller arbiter validates the deadline");
        _diagnostics.Record(VisionDiagnosticKind.PredictionReady, frame.Sequence, generation, shot,
            confidence: confidence, uncertainty: observation.UncertaintyMs,
            captureTimestamp: frame.CaptureTicks, timeToTargetMs: observation.TimeToTargetMs);
        return new VisionPrediction(generation, shot, frame.Sequence, frame.CaptureTicks, completed,
            observation.TimeToTargetMs, confidence, observation.UncertaintyMs, playerTracked);
    }

    private VisionPrediction Reject(CapturedVisionFrame frame, VisionStatusSnapshot snapshot, long now, string reason)
    {
        _diagnostics.Record(VisionDiagnosticKind.ObservationRejected, frame.Sequence, snapshot.SessionGeneration,
            snapshot.ShotToken, captureTimestamp: frame.CaptureTicks);
        _diagnostics.SetStatus(reason);
        return new(snapshot.SessionGeneration, snapshot.ShotToken, frame.Sequence, frame.CaptureTicks, now,
            0, 0, 0, false);
    }
}
