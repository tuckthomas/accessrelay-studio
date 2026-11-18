namespace EliteScriptStudio.Core;

/// <summary>Portable, single-owner controller state machine. The runtime serializes every call.
/// Only a physical press can open a shot, and a completed release stays forced up until physical up.</summary>
internal sealed class ControllerOutputArbiter
{
    private readonly long _frequency;
    private readonly StreamTelemetryWindow _telemetry;
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
    private long _accepted, _rejected, _releases, _missed, _cancelled;
    private long _actualStrokeStart, _actualAnchor;
    private bool _cancelledTriggerHold;
    private VisionControllerAction _action = new();
    private long _holdStarted, _strokeStart, _strokeAnchor, _upStarted, _receiptId;
    private bool _strokeStarted, _ownsRightStick, _axisLatched, _physicalNeutral;
    private short _rightY;
    private readonly string _runId = Guid.NewGuid().ToString("N");
    public VisionActionReceipt? LastAction { get; private set; }
    public VisionActionReceipt? LastAttempt { get; private set; }
    public VisionControllerAction Action => _action;
    private VisionTriggerSample _press;
    public VisionTriggerSample? TriggerSample => HoldStartedTimestamp > 0 && _press.Timestamp == HoldStartedTimestamp ? _press : null;
    public long HoldStartedTimestamp => _held ? _holdStarted : 0;
    public long StrokeAnchorTimestamp => _strokeStarted ? _strokeAnchor : 0;
    public void ClearActionReceipt() { LastAction = LastAttempt = null; }

    public ControllerOutputArbiter(long timestampFrequency)
    {
        if (timestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        _frequency = timestampFrequency;
        _telemetry = new(timestampFrequency);
    }

    public void ObserveStreamTelemetry(DirectStreamMetrics reading) => _telemetry.Add(reading);
    public void ObserveStreamTelemetry(StreamTelemetryReading reading) => _telemetry.Add(reading);
    public StreamTelemetryAssessment StreamTelemetry(long now) => _telemetry.Assess(_shotStarted > 0 ? _shotStarted : now, now,
        _settings.MaximumUncertaintyMs, _settings.StreamTelemetry.Enabled);
    public VisionSettings Settings => _settings;
    private double EffectiveStrokeDurationMs => Math.Clamp(_action.StrokeDurationMs + _settings.TempoCorrectionMs, 8, 500);
    public long? DeadlineTimestamp => _deadline;
    public ControllerState Output
    {
        get
        {
            var output = _composed with { Buttons = (ushort)(_composed.Buttons & ~_latched) };
            return _ownsRightStick || _axisLatched ? output with { RightX = 0, RightY = _axisLatched ? (short)0 : _rightY } : output;
        }
    }
    public VisionStatusSnapshot Status => new(State, _armed, _sourceReady, _message, _generation,
        _shot, _shotStarted, _held, _deadline, _confidence, _uncertainty, _accepted, _rejected, _releases, _missed)
        { CancelledAttempts = _cancelled, HoldingCancelledTriggerShot = _cancelledTriggerHold };

    private VisionRunState State => _latched != 0 || _axisLatched ? VisionRunState.ReleasedLatched
        : !_sourceReady ? (_armed ? VisionRunState.Suspended : VisionRunState.Disabled)
        : !_armed ? VisionRunState.Observing
        : !_eligible || _cancelledTriggerHold ? VisionRunState.Suspended
        : _deadline.HasValue ? VisionRunState.ReleaseScheduled
        : _shot != 0 ? VisionRunState.TrackingShot : VisionRunState.Ready;

    public void Configure(VisionSettings settings, VisionControllerAction? action = null)
    {
        settings.Validate();
        (action ?? new()).Validate();
        Invalidate("Vision configuration changed. Validate the source and model again.", sourceLost: true, disarm: true);
        _action = action ?? new();
        _settings = settings;
        _telemetry.Reset();
    }

    public bool TryAdjustTiming(Func<VisionSettings, VisionSettings> update, VisionControllerAction? action = null)
    {
        if (_shot != 0 || _strokeStarted || _deadline.HasValue || _cancelledTriggerHold || _held) return false;
        var nextSettings = update(_settings);
        nextSettings.Validate();
        if (action is not null) action.Validate();
        _settings = nextSettings;
        if (action is not null) _action = action;
        return true;
    }

    public long BeginSession()
    {
        Invalidate("Observation ready. Release the shot trigger before arming a new attempt.", disarm: true);
        _sourceReady = true;
        return _generation;
    }

    public bool SetArmed(bool armed)
    {
        if (armed && _settings.PreprocessingBackend == VisionPreprocessingBackend.D3D11Cuda)
        {
            Invalidate("Experimental GPU preprocessing is observation-only until matching hardware acceptance.", disarm: true);
            return false;
        }
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
        bool usingAssignedTrigger = _action.Kind == VisionActionKind.RightStickStroke && _action.HasAssignedTrigger;
        Invalidate(armed
            ? (usingAssignedTrigger ? "Armed. Hold the assigned shot control." : "Armed. Waiting for a fresh physical press.")
            : "Observation only. Automatic release is off.");
        _armed = armed;
        _needsPhysicalRelease = _held || (_action.Kind == VisionActionKind.RightStickStroke && !_physicalNeutral);
        return true;
    }

    public void SetTrainingRecording(bool recording)
    {
        _trainingRecording = recording;
        Invalidate(recording ? "Training recording is active. Automatic release is inhibited."
            : "Training recording ended. Release the shot trigger and arm again.", disarm: true);
    }

    public void Invalidate(string reason, bool sourceLost = false, bool disarm = false)
    {
        if (_shot != 0) RecordCancelled(reason, _lastSampleAt);
        _cancelledTriggerHold = false;
        _generation++;
        _shot = _shotStarted = 0;
        _deadline = null;
        if (_strokeStarted) _axisLatched = true;
        _ownsRightStick = _strokeStarted = false;
        _strokeStart = _strokeAnchor = _holdStarted = _upStarted = 0;
        _lastFrame = -1;
        _needsPhysicalRelease = true;
        _message = reason;
        if (sourceLost) _sourceReady = false;
        if (disarm) _armed = false;
        // A release latch survives reconfiguration/disarming while its physical button is still held.
    }

    public ControllerState Sample(ControllerState physical, ControllerState composed, bool connected,
        bool outputAvailable, bool conflictingAutomation, long now, long? inputTimestamp = null,
        PaddleButtons heldPaddles = PaddleButtons.None)
    {
        _composed = connected ? composed : default;
        _lastSampleAt = now;
        _latched &= physical.Buttons;
        bool usingAssignedTrigger = _action.Kind == VisionActionKind.RightStickStroke && _action.HasAssignedTrigger;
        bool held = connected && (_action.Kind == VisionActionKind.RightStickStroke
            ? _action.IsTriggerHeld(physical, heldPaddles)
            : (physical.Buttons & (ushort)_settings.ShootButton) != 0);
        _physicalNeutral = Math.Abs((int)physical.RightY) < 4000 && Math.Abs((int)physical.RightX) < 4000;
        if ((_physicalNeutral && !(usingAssignedTrigger && held)) || !connected) _axisLatched = false;
        bool rising = held && !_held;
        if (rising)
        {
            _holdStarted = inputTimestamp is > 0 && inputTimestamp <= now ? inputTimestamp.Value : now;
            _press = new(_holdStarted, physical.LeftX, physical.LeftY, _composed.LeftX, _composed.LeftY);
        }
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
            if (_shot != 0 || _deadline.HasValue) CancelAttempt(usingAssignedTrigger
                ? "Shot trigger release cancelled the pending action."
                : "Physical release cancelled the pending action.", now);
            _cancelledTriggerHold = false;
            _ownsRightStick = false;
            if (_action.Kind != VisionActionKind.RightStickStroke || _physicalNeutral) _needsPhysicalRelease = false;
            _holdStarted = 0;
            if (_armed && _sourceReady) _message = usingAssignedTrigger
                ? "Armed. Hold the assigned shot control."
                : "Armed. Waiting for a fresh physical press.";
            return Output;
        }
        if (_cancelledTriggerHold)
        {
            // Tracking failure must not manufacture a release while the user still holds the shot control.
            // Explicit Stop/disconnect still neutralizes immediately; this watchdog is bounded.
            if (Milliseconds((double)now - _holdStarted) > _settings.MaximumShotDurationMs)
            { _cancelledTriggerHold = _ownsRightStick = false; _axisLatched = true; _message = "Cancelled trigger hold timed out. Release the shot control."; }
            return Output;
        }
        bool readyForTrigger = _armed && _sourceReady && !_needsPhysicalRelease && !_axisLatched && _latched == 0;
        if (usingAssignedTrigger && readyForTrigger && !_strokeStarted && !_deadline.HasValue)
        {
            _ownsRightStick = true;
            _rightY = short.MinValue;
        }
        bool qualified = _action.Kind == VisionActionKind.RightStickStroke || _action.HoldQualificationMs > 0
            ? _holdStarted != 0 && Milliseconds((double)now - _holdStarted) >= _action.HoldQualificationMs : rising;
        if (qualified && _shot == 0 && readyForTrigger)
        {
            _shot = ++_nextShot;
            _shotStarted = _action.HoldQualificationMs > 0 || _action.Kind == VisionActionKind.RightStickStroke ? _holdStarted : now;
            _deadline = null;
            _actualStrokeStart = _actualAnchor = 0;
            _lastFrame = -1;
            _message = "Tracking the current physical shot.";
        }
        if (_shot != 0 && Milliseconds((double)now - _shotStarted) > _settings.MaximumShotDurationMs)
            CancelAttempt("Shot expired. Release the shot trigger before another attempt.", now);
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
        var telemetry = StreamTelemetry(now);
        // Observed stream variation adds uncertainty. Missing overlay data is explicit
        // and pauses adaptation, but never masquerades as zero network delay.
        if (double.IsFinite(prediction.UncertaintyMs) && prediction.UncertaintyMs >= 0)
            prediction = prediction with { UncertaintyMs = Math.Sqrt(prediction.UncertaintyMs * prediction.UncertaintyMs
            + telemetry.AdditionalUncertaintyMs * telemetry.AdditionalUncertaintyMs) };
        _uncertainty = prediction.UncertaintyMs;
        // Explicit continuation cannot create or move a deadline, or excuse a tracking rejection.
        if (prediction.ContinueCommittedAction)
        {
            if (_action.Kind == VisionActionKind.RightStickStroke && _strokeStarted
                && prediction.CaptureTimestamp >= _strokeAnchor && prediction.CaptureTimestamp <= prediction.CompletedTimestamp
                && prediction.CompletedTimestamp <= now && age >= 0 && age <= _settings.MaximumFrameAgeMs)
            {
                _lastCaptureAt = prediction.CaptureTimestamp; _accepted++;
                _message = "Completing the anchored stick stroke; no new timing forecast.";
                return true;
            }
            _rejected++; CancelAttempt("Continuation rejected: no anchored stroke or fresh post-anchor frame.", now);
            return false;
        }
        string? rejection = null;
        if (prediction.CaptureTimestamp < _shotStarted) rejection = "The frame was captured before this shot started.";
        else if (prediction.CaptureTimestamp > prediction.CompletedTimestamp || prediction.CompletedTimestamp > now || age < 0)
            rejection = "The frame and prediction timestamps are out of order.";
        else if (age > _settings.MaximumFrameAgeMs) rejection = $"The frame was {age:F1} ms old (limit {_settings.MaximumFrameAgeMs} ms).";
        else if (!prediction.PlayerTracked) rejection = prediction.TrackingFailure ?? "The predictor lost track of the player or shot meter.";
        else if (!double.IsFinite(prediction.TimeToReleaseMs) || prediction.TimeToReleaseMs < 0 || prediction.TimeToReleaseMs > _settings.MaximumShotDurationMs)
            rejection = "The predicted release time was outside this shot's timing window.";
        else if (!double.IsFinite(prediction.Confidence) || prediction.Confidence < _settings.MinimumConfidence || prediction.Confidence > 1)
            rejection = $"Prediction confidence was {prediction.Confidence:P1} (minimum {_settings.MinimumConfidence:P1}).";
        else if (telemetry.State == "unstable") rejection = telemetry.Message;
        else if (!double.IsFinite(prediction.UncertaintyMs) || prediction.UncertaintyMs < 0 || prediction.UncertaintyMs > _settings.MaximumUncertaintyMs)
            rejection = $"Timing uncertainty was {prediction.UncertaintyMs:F1} ms (limit {_settings.MaximumUncertaintyMs} ms).";
        if (rejection is not null)
        {
            _rejected++;
            CancelAttempt(rejection, now);
            return false;
        }
        // A started ramp has fixed deadlines. Fresh, valid observations still
        // refresh tracking, but their revised target cannot retroactively make
        // that ramp's start late. Tracking/input/freshness failures above still cancel.
        if (_action.Kind == VisionActionKind.RightStickStroke && _strokeStarted)
        { _lastCaptureAt = prediction.CaptureTimestamp; _accepted++; return true; }
        double waitMs = prediction.TimeToReleaseMs - _settings.EstimatedVideoDelayMs
            - age - _settings.EstimatedInputDelayMs + _settings.CorrectionMs;
        if (!double.IsFinite(waitMs) || waitMs < -_settings.MaximumLatenessMs)
        {
            _missed++;
            _rejected++;
            CancelAttempt("Prediction arrived after the allowed release deadline.", now);
            return false;
        }
        // Settings bound the horizon; double arithmetic avoids long subtraction/addition overflow.
        double deadline = now + waitMs * _frequency / 1000;
        if (deadline < 0 || deadline >= long.MaxValue)
        {
            _rejected++;
            CancelAttempt("Release deadline is outside the monotonic clock range.", now);
            return false;
        }
        _deadline = (long)Math.Round(deadline);
        if (_action.Kind == VisionActionKind.RightStickStroke)
        {
            _lastCaptureAt = prediction.CaptureTimestamp;
            _strokeAnchor = _deadline.Value;
            double strokeDurationMs = EffectiveStrokeDurationMs;
            _strokeStart = _strokeAnchor - (long)Math.Round(_action.AnchorTimeFraction * strokeDurationMs * _frequency / 1000);
            if (_strokeAnchor <= now || Milliseconds((double)now - _strokeStart) > _settings.MaximumLatenessMs)
            { _missed++; _rejected++; CancelAttempt("The complete stick stroke cannot start before its calibrated anchor.", now); return false; }
            _deadline = _strokeStart;
            _ownsRightStick = true; _rightY = short.MinValue;
        }
        _lastCaptureAt = prediction.CaptureTimestamp;
        _accepted++;
        _message = "Release scheduled from a current animation prediction.";
        return true;

    }

    public bool TryAdvanceDeadline(long now)
    {
        if (_deadline is not long deadline || now < deadline) return false;
        if (_action.Kind == VisionActionKind.RightStickStroke) return AdvanceStroke(now, deadline);
        _deadline = null;
        if (_trainingRecording || !_armed || !_sourceReady || !_eligible || !_held || _shot == 0) return false;
        if (Milliseconds((double)now - deadline) > _settings.MaximumLatenessMs
            || Milliseconds((double)now - _lastSampleAt) > 50
            || Milliseconds((double)now - _lastCaptureAt) > _settings.MaximumFrameAgeMs
            || Milliseconds((double)now - _shotStarted) > _settings.MaximumShotDurationMs)
        {
            _missed++;
            CancelAttempt("Release skipped because scheduling or physical-input freshness exceeded its limit.", now);
            return false;
        }
        _latched |= (ushort)_settings.ShootButton;
        _needsPhysicalRelease = true;
        _releases++;
        LastAction = LastAttempt = new(++_receiptId, _generation, _shot, now, deadline, now, 0, 0, _action.ConfigurationKey, _runId);
        LastAction = LastAttempt = LastAction with { TriggerSample = _press.Timestamp == _shotStarted ? _press : null, CorrectionMs = _settings.CorrectionMs, TempoCorrectionMs = _settings.TempoCorrectionMs, StreamTelemetry = StreamTelemetry(now) };
        _shot = _shotStarted = 0;
        _message = "Released. Waiting for physical button release before rearming.";
        return true;
    }

    public void RecordOutputSubmitted(ControllerState submitted, long timestamp)
    {
        if (_strokeStarted && _ownsRightStick && submitted.RightY == _rightY)
        {
            if (_actualStrokeStart == 0) _actualStrokeStart = timestamp;
            double travel = ((double)submitted.RightY - short.MinValue) / 65535;
            if (_actualAnchor == 0 && travel + 1d / 65535 >= _action.AnchorFraction) _actualAnchor = timestamp;
        }
        else if (_action.Kind == VisionActionKind.ButtonRelease && LastAction is { ActualAnchorTimestamp: 0 } receipt
            && (submitted.Buttons & (ushort)_settings.ShootButton) == 0 && _latched != 0)
        {
            LastAction = LastAttempt = receipt with { ActualStartedTimestamp = timestamp, ActualAnchorTimestamp = timestamp,
                AnchorLatenessMs = Milliseconds((double)timestamp - receipt.AnchorTimestamp),
                EligibleForFeedback = Math.Abs(Milliseconds((double)timestamp - receipt.AnchorTimestamp)) <= _settings.MaximumLatenessMs };
        }
    }

    private void RecordCancelled(string message, long now)
    {
        _cancelled++;
        LastAttempt = new(++_receiptId, _generation, _shot, _strokeStart > 0 ? _strokeStart : _shotStarted, _strokeAnchor, now,
            _action.Kind == VisionActionKind.RightStickStroke ? EffectiveStrokeDurationMs : 0,
            _action.AnchorFraction, _action.ConfigurationKey, _runId)
        { TriggerSample = _press.Timestamp == _shotStarted ? _press : null, CorrectionMs = _settings.CorrectionMs, TempoCorrectionMs = _settings.TempoCorrectionMs, Outcome = VisionActionOutcome.Cancelled, CancellationReason = message,
            ActualStartedTimestamp = _actualStrokeStart, ActualAnchorTimestamp = _actualAnchor, StreamTelemetry = StreamTelemetry(now) };
    }

    private void CancelAttempt(string message, long now)
    {
        if (_shot != 0) RecordCancelled(message, now);
        bool preserveDown = !_strokeStarted && _armed && _eligible && _held
            && _action.Kind == VisionActionKind.RightStickStroke && _action.HasAssignedTrigger
            && Milliseconds((double)now - _holdStarted) <= _settings.MaximumShotDurationMs;
        _shot = _shotStarted = 0;
        _deadline = null;
        if (_strokeStarted) _axisLatched = true;
        _strokeStarted = false;
        _cancelledTriggerHold = _ownsRightStick = preserveDown;
        if (preserveDown) _rightY = short.MinValue;
        _strokeStart = _strokeAnchor = _upStarted = 0;
        _needsPhysicalRelease = true;
        _message = preserveDown ? message + " Holding down until trigger release or timeout." : message;
    }

    private double Milliseconds(double ticks) => ticks * 1000.0 / _frequency;

    private bool AdvanceStroke(long now, long deadline)
    {
        if (_trainingRecording || !_armed || !_sourceReady || !_eligible || !_held || _shot == 0
            || Milliseconds((double)now - _lastSampleAt) > 50
            // A forecast already accounts for its target horizon. Enforce the configured
            // freshness bound at stroke start too; a hidden 25 ms cutoff rejected valid
            // forecasts between 30 fps frames without any new negative observation.
            || (!_strokeStarted && Milliseconds((double)now - _lastCaptureAt) > _settings.MaximumFrameAgeMs)
            || Milliseconds((double)now - deadline) > _settings.MaximumLatenessMs)
        { _missed++; CancelAttempt("Stick action cancelled: input, capture or scheduler freshness was lost.", now); return true; }
        _strokeStarted = true;
        double strokeDurationMs = EffectiveStrokeDurationMs;
        double elapsed = Milliseconds((double)now - _strokeStart);
        if (_upStarted != 0 && Milliseconds((double)now - _upStarted) >= Math.Max(2, _action.EndHoldMs))
        {
            _deadline = null; _ownsRightStick = false; _axisLatched = true;
            _needsPhysicalRelease = true; _releases++;
            LastAction = LastAttempt = new(++_receiptId, _generation, _shot, _strokeStart, _strokeAnchor, now,
                strokeDurationMs, _action.AnchorFraction, _action.ConfigurationKey, _runId)
            { TriggerSample = _press.Timestamp == _shotStarted ? _press : null, CorrectionMs = _settings.CorrectionMs, TempoCorrectionMs = _settings.TempoCorrectionMs, StreamTelemetry = StreamTelemetry(now), ActualStartedTimestamp = _actualStrokeStart, ActualAnchorTimestamp = _actualAnchor,
                AnchorLatenessMs = _actualAnchor > 0 ? Milliseconds((double)_actualAnchor - _strokeAnchor) : null,
                EligibleForFeedback = _actualAnchor > 0 && Math.Abs(Milliseconds((double)_actualAnchor - _strokeAnchor)) <= _settings.MaximumLatenessMs };
            _strokeStarted = false; _shot = _shotStarted = _upStarted = 0;
            _message = _action.Kind == VisionActionKind.RightStickStroke && _action.HasAssignedTrigger
                ? "Stick stroke completed. Release the shot control before another attempt."
                : "Stick stroke completed. Return the physical stick to neutral before another attempt.";
            return true;
        }
        double phase = Math.Clamp(elapsed / strokeDurationMs, 0, 1);
        if (_action.Curve == VisionStrokeCurve.Smoothstep) phase = phase * phase * (3 - 2 * phase);
        _rightY = (short)Math.Clamp(Math.Round(short.MinValue + phase * 65535), short.MinValue, short.MaxValue);
        if (phase >= 1)
        {
            _upStarted = now;
            // Even a zero configured hold must actually submit the up endpoint
            // before a later report returns neutral; late wakes cannot skip it.
            _deadline = _upStarted + (long)Math.Ceiling(Math.Max(2, _action.EndHoldMs) * _frequency / 1000);
        }
        else _deadline = _strokeStart + (long)Math.Ceiling(Math.Min(elapsed + 2, strokeDurationMs) * _frequency / 1000);
        _message = elapsed < strokeDurationMs ? "Executing the calibrated stick stroke." : "Completing the calibrated up hold.";
        return true;
    }
}
