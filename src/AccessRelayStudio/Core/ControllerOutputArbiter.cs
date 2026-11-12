namespace EliteScriptStudio.Core;

/// <summary>Portable, single-owner controller state machine. The runtime serializes every call.
/// Only a physical press can open a shot, and a completed release stays forced up until physical up.</summary>
internal sealed class ControllerOutputArbiter
{
    private readonly long _frequency;
    private VisionSettings _settings = VisionSettings.Default;
    private long _generation = 1, _shot, _nextShot, _shotStarted, _lastFrame = -1;
    private long? _deadline;
    private long _lastSampleAt;
    private long _lastCaptureAt;
    private bool _sourceReady, _armed, _held, _needsPhysicalRelease = true, _eligible, _trainingRecording;
    private ushort _latched;
    private ControllerState _composed;
    private string _message = "Vision is off.";
    private double _confidence, _uncertainty;
    private long _accepted, _rejected, _releases, _missed;

    public ControllerOutputArbiter(long timestampFrequency)
    {
        if (timestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        _frequency = timestampFrequency;
    }

    public VisionSettings Settings => _settings;
    public long? DeadlineTimestamp => _deadline;
    public ControllerState Output => _composed with { Buttons = (ushort)(_composed.Buttons & ~_latched) };
    public VisionStatusSnapshot Status => new(State, _armed, _sourceReady, _message, _generation,
        _shot, _shotStarted, _held, _deadline, _confidence, _uncertainty, _accepted, _rejected, _releases, _missed);

    private VisionRunState State => _latched != 0 ? VisionRunState.ReleasedLatched
        : !_sourceReady ? (_armed ? VisionRunState.Suspended : VisionRunState.Disabled)
        : !_armed ? VisionRunState.Observing
        : !_eligible ? VisionRunState.Suspended
        : _deadline.HasValue ? VisionRunState.ReleaseScheduled
        : _shot != 0 ? VisionRunState.TrackingShot : VisionRunState.Ready;

    public void Configure(VisionSettings settings)
    {
        settings.Validate();
        _settings = settings;
        Invalidate("Vision configuration changed. Validate the source and model again.", sourceLost: true, disarm: true);
    }

    public long BeginSession()
    {
        Invalidate("Observation ready. Release the shoot button before arming a new attempt.", disarm: true);
        _sourceReady = true;
        return _generation;
    }

    public bool SetArmed(bool armed)
    {
        if (_trainingRecording)
        {
            Invalidate("Training recording is active. Automatic release is inhibited.", disarm: true);
            return false;
        }
        if (armed && (!_sourceReady || !_eligible))
        {
            _message = "Arming requires a validated source/model, connected physical controller, available output, and no conflicting macro or recording.";
            return false;
        }
        Invalidate(armed ? "Armed. Waiting for a fresh physical press." : "Observation only. Automatic release is off.");
        _armed = armed;
        _needsPhysicalRelease = _held;
        return true;
    }

    public void SetTrainingRecording(bool recording)
    {
        _trainingRecording = recording;
        Invalidate(recording ? "Training recording is active. Automatic release is inhibited."
            : "Training recording ended. Release the shoot button and explicitly arm again.", disarm: true);
    }

    public void Invalidate(string reason, bool sourceLost = false, bool disarm = false)
    {
        _generation++;
        _shot = _shotStarted = 0;
        _deadline = null;
        _lastFrame = -1;
        _needsPhysicalRelease = true;
        _message = reason;
        if (sourceLost) _sourceReady = false;
        if (disarm) _armed = false;
        // A release latch survives reconfiguration/disarming while its physical button is still held.
    }

    public ControllerState Sample(ControllerState physical, ControllerState composed, bool connected,
        bool outputAvailable, bool conflictingAutomation, long now)
    {
        _composed = connected ? composed : default;
        _lastSampleAt = now;
        _latched &= physical.Buttons;
        bool held = connected && (physical.Buttons & (ushort)_settings.ShootButton) != 0;
        bool rising = held && !_held;
        _held = held;
        bool eligible = connected && outputAvailable && !conflictingAutomation && !_trainingRecording;
        if (!eligible)
        {
            if (_eligible || _shot != 0 || _deadline.HasValue)
                Invalidate("Vision suspended: input/output unavailable, recording, or conflicting automation.");
            _eligible = false;
            return Output;
        }
        _eligible = true;
        if (!held)
        {
            if (_shot != 0 || _deadline.HasValue) CancelAttempt("Physical release cancelled the pending action.");
            _needsPhysicalRelease = false;
            if (_armed && _sourceReady) _message = "Armed. Waiting for a fresh physical press.";
            return Output;
        }
        if (rising && !_needsPhysicalRelease && _latched == 0 && _armed && _sourceReady)
        {
            _shot = ++_nextShot;
            _shotStarted = now;
            _deadline = null;
            _lastFrame = -1;
            _message = "Tracking the current physical shot.";
        }
        if (_shot != 0 && Milliseconds((double)now - _shotStarted) > _settings.MaximumShotDurationMs)
            CancelAttempt("Shot expired. Release the shoot button before another attempt.");
        TryAdvanceDeadline(now);
        return Output;
    }

    public bool Submit(VisionPrediction prediction, long now)
    {
        if (_trainingRecording || !_armed || !_sourceReady || !_eligible || !_held || _shot == 0 || _latched != 0
            || prediction.SessionGeneration != _generation || prediction.ShotToken != _shot)
        {
            _rejected++;
            return false;
        }
        if (prediction.FrameSequence <= _lastFrame)
        {
            _rejected++;
            return false;
        }
        _lastFrame = prediction.FrameSequence;
        _confidence = prediction.Confidence;
        _uncertainty = prediction.UncertaintyMs;
        double age = Milliseconds((double)now - prediction.CaptureTimestamp);
        if (prediction.CaptureTimestamp < _shotStarted || prediction.CaptureTimestamp > prediction.CompletedTimestamp
            || prediction.CompletedTimestamp > now || age < 0 || age > _settings.MaximumFrameAgeMs
            || !double.IsFinite(prediction.TimeToReleaseMs) || prediction.TimeToReleaseMs < 0
            || prediction.TimeToReleaseMs > _settings.MaximumShotDurationMs
            || !double.IsFinite(prediction.Confidence) || prediction.Confidence < _settings.MinimumConfidence
            || prediction.Confidence > 1 || !prediction.PlayerTracked
            || !double.IsFinite(prediction.UncertaintyMs) || prediction.UncertaintyMs < 0
            || prediction.UncertaintyMs > _settings.MaximumUncertaintyMs)
        {
            _rejected++;
            CancelAttempt("Prediction rejected: stale frame, uncertain player/animation, or invalid timing.");
            return false;
        }
        double waitMs = prediction.TimeToReleaseMs - _settings.EstimatedVideoDelayMs
            - age - _settings.EstimatedInputDelayMs + _settings.CorrectionMs;
        if (!double.IsFinite(waitMs) || waitMs < -_settings.MaximumLatenessMs)
        {
            _missed++;
            _rejected++;
            CancelAttempt("Prediction arrived after the allowed release deadline.");
            return false;
        }
        // Settings bound the horizon; double arithmetic avoids long subtraction/addition overflow.
        double deadline = now + waitMs * _frequency / 1000;
        if (deadline < 0 || deadline >= long.MaxValue)
        {
            _rejected++;
            CancelAttempt("Release deadline is outside the monotonic clock range.");
            return false;
        }
        _deadline = (long)Math.Round(deadline);
        _lastCaptureAt = prediction.CaptureTimestamp;
        _accepted++;
        _message = "Release scheduled from a current animation prediction.";
        return true;
    }

    public bool TryAdvanceDeadline(long now)
    {
        if (_deadline is not long deadline || now < deadline) return false;
        _deadline = null;
        if (_trainingRecording || !_armed || !_sourceReady || !_eligible || !_held || _shot == 0) return false;
        if (Milliseconds((double)now - deadline) > _settings.MaximumLatenessMs
            || Milliseconds((double)now - _lastSampleAt) > 50
            || Milliseconds((double)now - _lastCaptureAt) > _settings.MaximumFrameAgeMs
            || Milliseconds((double)now - _shotStarted) > _settings.MaximumShotDurationMs)
        {
            _missed++;
            CancelAttempt("Release skipped because scheduling or physical-input freshness exceeded its limit.");
            return false;
        }
        _latched |= (ushort)_settings.ShootButton;
        _needsPhysicalRelease = true;
        _releases++;
        _message = "Released. Waiting for physical button release before rearming.";
        return true;
    }

    private void CancelAttempt(string message)
    {
        _shot = _shotStarted = 0;
        _deadline = null;
        _needsPhysicalRelease = true;
        _message = message;
    }

    private double Milliseconds(double ticks) => ticks * 1000.0 / _frequency;
}
