using System.Diagnostics;
using System.Buffers;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;

namespace EliteScriptStudio;

internal sealed record RuntimeSample(ControllerSample Input, ControllerState Output, bool OutputActive,
    string OutputStatus, MacroStatus Macro, double MeanPollMs, double P95PollMs, double P99PollMs,
    long PaddlePulse, PaddleButtons LastPaddles, uint VisibleControls);

internal readonly record struct RecorderState(bool Connected, bool IsRecording, bool HasInput, bool SupportsBackgroundInput, int ElapsedMs,
    MacroRecording? Pending, string? Note);

internal sealed record LuaCalibrationAction(long Id, string RunId, string Key, Guid ProfileId, ControlId Control,
    Guid ScriptId, string SourceSha256, long StartedAtMs, long CompletedAtMs);

internal sealed class StudioRuntime : IDisposable
{
    private readonly IControllerInput _input;
    private bool _configuringInput, _inputNeedsNeutral;
    private readonly MacroEngine _engine = new();
    private readonly StickyButtons _sticky = new();
    private readonly StickSmoother _smoother = new();
    private readonly SwitchNavigationInput _navigation = new();
    private readonly VoiceCommandArbiter _voiceCommands = new(Stopwatch.Frequency);
    private readonly RuntimeLockMeasurements _lockMeasurements = new();
    private long _lockMeasurementToken;
    private readonly FeedbackDispatcher? _feedback;
    private FeedbackIdentity? _lockedFeedback;
    private long _profileFeedbackToken, _testFeedbackToken, _lastReleaseFeedback;
    private bool _navigationNeedsNeutral;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ControllerOutputArbiter _vision = new(Stopwatch.Frequency);
    private readonly ReleaseScheduler _releaseScheduler;
    private StudioProfile _visionProfile;
    private StickDeadzone _stickDeadzone;
    private IControllerOutput? _output;
    private string _outputStatus = "Virtual output off";
    private RuntimeSample? _latest;
    private InputRecorder? _recorder;
    private MacroRecording? _pending;
    private string? _recordingNote;
    private long _recordingConnection;
    private long _recordingBeganAt;
    private int _selectionVersion;
    private bool _visionTrainingRecording;
    private VisionTrainingTrace? _visionTrainingTrace;
    private long _visionTrainingTraceAttachedAt, _latestInputSampleTicks;
    private ControlId? _luaCalibrationControl, _luaCalibrationTrigger, _luaCalibrationConsumed;
    private Guid _luaCalibrationScript;
    private string _luaCalibrationKey = "", _luaCalibrationHash = "";
    private long _luaCalibrationConnection, _luaCalibrationStarted, _luaCalibrationAfterReceipt, _luaCalibrationTriggerAt;
    private bool _luaCalibrationRunning, _luaCalibrationButtonDown;
    private long _luaCalibrationPickupAt, _luaCalibrationPickupConnection;
    private string _luaCalibrationMessage = "Choose a saved paddle shot to calibrate.";
    private LuaCalibrationAction? _lastLuaCalibrationAction;

    public bool LuaCalibrationArmed { get { lock (_sync) return _luaCalibrationControl.HasValue; } }
    public string LuaCalibrationMessage { get { lock (_sync) return _luaCalibrationMessage; } }
    public LuaCalibrationAction? LastLuaCalibrationAction { get { lock (_sync) return _lastLuaCalibrationAction; } }
    public bool LuaCalibrationPickupActive { get { lock (_sync) return _luaCalibrationPickupAt != 0; } }

    public void CollectLuaCalibrationBall(int delayMs = 3000)
    {
        lock (_sync)
        {
            RequireTrainingStoppedLocked("collecting a practice ball");
            if (_latest?.Input is not { IsConnected: true } input || _output?.IsAvailable != true
                || _recorder is not null || _navigation.Enabled || _navigationNeedsNeutral || _inputNeedsNeutral
                || _voiceCommands.Enabled || _engine.Status.IsActive || _luaCalibrationControl.HasValue || _luaCalibrationPickupAt != 0
                || !LuaCalibrationNeutral(input.State) || input.HeldPaddles != PaddleButtons.None)
                throw new InvalidOperationException("Finish the current trial and release controls before collecting a practice ball.");
            _lastLuaCalibrationAction = null;
            _vision.SetArmed(false); _releaseScheduler.Signal();
            _luaCalibrationPickupConnection = input.ConnectionVersion;
            _luaCalibrationPickupAt = _clock.ElapsedMilliseconds + Math.Clamp(delayMs, 3000, 10000);
            _luaCalibrationMessage = $"Ball pickup in {Math.Clamp(delayMs, 3000, 10000) / 1000} seconds. Resume the game and keep the controller neutral.";
        }
    }

    public void StartLuaCalibrationTrial(ControlId control, bool automatic, int delayMs = 3000)
    {
        lock (_sync)
        {
            RequireTrainingStoppedLocked("starting a paddle calibration trial");
            if (_latest?.Input is not { IsConnected: true } input || _output?.IsAvailable != true || !_engine.ScriptsEnabled)
                throw new InvalidOperationException("Connect controller output and enable Run scripts before starting a paddle trial.");
            if (_recorder is not null || _navigation.Enabled || _navigationNeedsNeutral || _inputNeedsNeutral || _voiceCommands.Enabled || _engine.Status.IsActive || _luaCalibrationPickupAt != 0)
                throw new InvalidOperationException("Finish the running script, recording or navigation before starting a paddle trial.");
            if (!LuaCalibrationNeutral(input.State) || input.HeldPaddles != PaddleButtons.None)
                throw new InvalidOperationException("Release the controller controls before starting the complete scripted shot.");
            var binding = _visionProfile.GetBinding(control);
            if (!binding.Enabled || binding.Script != ScriptKind.Lua || binding.LuaScriptId is not { } id
                || !_visionProfile.LuaScripts.TryGetValue(id, out var script) || !LuaShotCalibration.IsCompatible(script))
                throw new InvalidOperationException("Choose an enabled recorded right-stick Lua shot.");
            CancelLuaCalibrationLocked("Previous paddle trial cleared.", clearReceipt: true);
            // Arming observes a neutral sample under the polling lock, so a first
            // button edge immediately after Apply is not mistaken for an old hold.
            _engine.Tick(input.State, PaddleButtons.None, true, _clock.ElapsedMilliseconds);
            _vision.SetArmed(false); _releaseScheduler.Signal();
            _luaCalibrationControl = control; _luaCalibrationTrigger = automatic ? control : null;
            _luaCalibrationConsumed = control; _luaCalibrationButtonDown = false;
            _luaCalibrationScript = id; _luaCalibrationHash = LuaShotCalibration.SourceSha256(script);
            _luaCalibrationKey = LuaShotCalibration.ConfigurationKey(_visionProfile.Id, control, script);
            _luaCalibrationConnection = input.ConnectionVersion;
            _luaCalibrationStarted = _clock.ElapsedMilliseconds;
            _luaCalibrationAfterReceipt = _engine.LastLuaCompletion?.Id ?? 0;
            _luaCalibrationTriggerAt = _luaCalibrationStarted + (automatic ? Math.Clamp(delayMs, 3000, 10000) : 0);
            _luaCalibrationRunning = false;
            _luaCalibrationMessage = automatic ? $"One complete scripted shot starts in {Math.Clamp(delayMs, 3000, 10000) / 1000} seconds. Resume the game and keep the controller neutral."
                : "Press the selected paddle once. The script performs the complete shot; leave the right stick neutral.";
        }
    }

    public void PauseLuaCalibrationTrial()
    {
        lock (_sync) CancelLuaCalibrationLocked("Paddle trial paused.", clearReceipt: false);
    }

    private bool LuaCalibrationNeutral(ControllerState state) => _stickDeadzone.Apply(state) == default;

    private void CancelLuaCalibrationLocked(string message, bool clearReceipt = true)
    {
        if (_luaCalibrationPickupAt != 0) { _luaCalibrationPickupAt = 0; _output?.Neutralize(); }
        if (_luaCalibrationControl is { } control)
        {
            _engine.CancelLua(control);
            if (_luaCalibrationRunning) _output?.Neutralize();
        }
        _luaCalibrationControl = _luaCalibrationTrigger = null; _luaCalibrationRunning = false;
        _luaCalibrationMessage = message;
        if (clearReceipt) _lastLuaCalibrationAction = null;
    }

    private bool SampleLuaCalibrationPickupLocked(ControllerSample input, long now, out ControllerState state)
    {
        state = default;
        if (_luaCalibrationPickupAt == 0) return false;
        if (!input.IsConnected || input.ConnectionVersion != _luaCalibrationPickupConnection || _output?.IsAvailable != true
            || _recorder is not null || _visionTrainingRecording || _navigation.Enabled || _navigationNeedsNeutral
            || _configuringInput || _inputNeedsNeutral || _voiceCommands.Enabled || _engine.Status.IsActive
            || !LuaCalibrationNeutral(input.State) || input.HeldPaddles != PaddleButtons.None || input.PressedPaddles != PaddleButtons.None)
        { CancelLuaCalibrationLocked("Practice ball pickup cancelled because input or output changed."); return false; }
        if (now >= _luaCalibrationPickupAt + 100)
        { _luaCalibrationPickupAt = 0; _luaCalibrationMessage = "Ball pickup sent. Confirm possession before running the shot."; }
        else if (now >= _luaCalibrationPickupAt) state = default(ControllerState) with { Buttons = (ushort)ControlId.A };
        return true;
    }

    private ControlId? SampleLuaCalibrationLocked(ControllerSample input, long now)
    {
        if (_luaCalibrationControl is not { } control) return null;
        var paddles = input.HeldPaddles | input.PressedPaddles;
        bool down = ControlMap.IsPressed(control, input.State, input.HeldPaddles);
        if (down) _luaCalibrationConsumed = control;
        bool anotherPress = _luaCalibrationRunning && down && !_luaCalibrationButtonDown;
        _luaCalibrationButtonDown = down;
        if (!input.IsConnected || input.ConnectionVersion != _luaCalibrationConnection || _output?.IsAvailable != true
            || !_engine.ScriptsEnabled || _recorder is not null || _visionTrainingRecording || _navigation.Enabled
            || _navigationNeedsNeutral || _configuringInput || _inputNeedsNeutral || _voiceCommands.Enabled
            || !LuaCalibrationNeutral(ControlMap.Consume(input.State, (uint)control))
            || (_luaCalibrationRunning && input.PressedPaddles != PaddleButtons.None)
            || anotherPress
            || (ControlMap.PressedMask(default, paddles) & ~(uint)control) != 0)
        {
            CancelLuaCalibrationLocked("Trial cancelled because physical input, connection or output changed."); return null;
        }
        if (_engine.Status is { IsActive: true } active && (active.Control != control || active.Script != ScriptKind.Lua))
        { CancelLuaCalibrationLocked("Trial cancelled because another script started."); return null; }
        if (now - _luaCalibrationStarted > 30000)
        { CancelLuaCalibrationLocked("Paddle trial expired. Start a new trial when ready."); return null; }
        if (now < _luaCalibrationTriggerAt) return null;
        var trigger = _luaCalibrationTrigger; _luaCalibrationTrigger = null;
        return trigger;
    }

    private void CompleteLuaCalibrationLocked(ControllerSample input, ControllerState output, bool accepted, long now)
    {
        if (_luaCalibrationControl is not { } control)
        {
            if (_lastLuaCalibrationAction is { } previous && (_engine.Status.IsActive
                || !LuaCalibrationNeutral(ControlMap.Consume(input.State, (uint)previous.Control))))
            { _lastLuaCalibrationAction = null; _luaCalibrationMessage = "A newer shot started. Complete a new calibration trial before saving feedback."; }
            return;
        }
        if (!accepted) { CancelLuaCalibrationLocked("Trial cancelled because output did not accept the report."); return; }
        var status = _engine.Status;
        if (status.IsActive)
        {
            if (status.Control != control || status.Script != ScriptKind.Lua)
            { CancelLuaCalibrationLocked("Trial cancelled because another script started."); return; }
            _luaCalibrationRunning = true;
        }
        if (status.Error is not null)
        { CancelLuaCalibrationLocked("Lua trial failed: " + status.Error); return; }
        var completion = _engine.LastLuaCompletion;
        if (!_luaCalibrationRunning || completion is null || completion.Id <= _luaCalibrationAfterReceipt || status.IsActive) return;
        if (!_luaCalibrationRunning || completion.Control != control || completion.ScriptId != _luaCalibrationScript
            || completion.SourceSha256 != _luaCalibrationHash || input.ConnectionVersion != _luaCalibrationConnection
            || output.RightX != 0 || output.RightY != 0)
        { CancelLuaCalibrationLocked("Trial did not complete the selected script at neutral."); return; }
        _lastLuaCalibrationAction = new(completion.Id, completion.RunId, _luaCalibrationKey, _visionProfile.Id,
            control, completion.ScriptId, completion.SourceSha256, completion.StartedAtMs, completion.CompletedAtMs);
        _luaCalibrationControl = _luaCalibrationTrigger = null; _luaCalibrationRunning = false;
        _luaCalibrationMessage = "Complete scripted shot delivered. Save the game's result to adjust its timing.";
    }

    public RuntimeSample? Latest => Volatile.Read(ref _latest);
    public bool HasOutput { get { lock (_sync) return _output is not null; } }
    public bool ScriptsEnabled => _engine.ScriptsEnabled;
    public bool IsVisionTrainingRecording { get { lock (_sync) return _visionTrainingRecording; } }
    public double? RecentOutputHandoffMs { get { lock (_sync) return (_output as UsbipXboxOutput)?.RecentHandoffMs; } }
    public VisionStatusSnapshot VisionStatus
    {
        get
        {
            long token = Volatile.Read(ref _lockMeasurementToken), requested = token != 0 ? Stopwatch.GetTimestamp() : 0;
            lock (_sync) { if (token != 0) _lockMeasurements.Record(RuntimeLockPath.VisionRead, token, requested, Stopwatch.GetTimestamp()); return _vision.Status; }
        }
    }
    internal long BeginLockMeasurements()
    { lock (_sync) { long token = _lockMeasurements.Begin(); Volatile.Write(ref _lockMeasurementToken, token); return token; } }
    internal void FinishLockMeasurements(long token)
    { lock (_sync) { if (_lockMeasurements.Finish(token)) Volatile.Write(ref _lockMeasurementToken, 0); } }
    internal RuntimeLockSnapshot? LockMeasurements(long token)
    {
        if (token <= 0) return null;
        var samples = ArrayPool<RuntimeLockSample>.Shared.Rent(RuntimeLockMeasurements.Storage);
        try
        {
            Span<int> counts = stackalloc int[RuntimeLockMeasurements.Paths], next = stackalloc int[RuntimeLockMeasurements.Paths];
            Span<long> totals = stackalloc long[RuntimeLockMeasurements.Paths]; bool collecting;
            lock (_sync) if (!_lockMeasurements.Copy(token, samples, counts, next, totals, out collecting)) return null;
            return RuntimeLockMeasurements.Summarize(token, collecting, samples, counts, next, totals, Stopwatch.Frequency);
        }
        finally { ArrayPool<RuntimeLockSample>.Shared.Return(samples); }
    }
    internal SwitchNavigationSnapshot NavigationStatus { get { lock (_sync) return _navigation.Snapshot(_clock.ElapsedMilliseconds, _latest?.Input.NavigationConnected == true); } }
    internal FeedbackStatus? FeedbackStatus => _feedback?.Status;
    internal CoPilotSettings CoPilotSettings => _input.CoPilotSettings;
    internal Guid ActiveProfileId { get { lock (_sync) return _visionProfile.Id; } }
    internal string ActiveProfileName { get { lock (_sync) return _visionProfile.Name; } }
    internal VoiceCommandOutputStatus VoiceCommandStatus { get { lock (_sync) return _voiceCommands.Status; } }
    internal long ActivateVoiceCommands(VoiceCommandSettings settings)
    {
        settings.Validate();
        lock (_sync)
        {
            RequireTrainingStoppedLocked("activating voice commands");
            if (_latest?.Input.IsConnected != true || _output?.IsAvailable != true || _recorder is not null
                || _navigation.Enabled || _navigationNeedsNeutral || _inputNeedsNeutral)
                throw new InvalidOperationException("Connect physical input and output, finish recording and navigation, and release controls before enabling voice commands.");
            if (settings.Bindings.Any(binding => binding.Action == VoiceCommandAction.Sequence && (!_engine.ScriptsEnabled
                || !_visionProfile.GetBinding(binding.Control).Enabled || _visionProfile.GetBinding(binding.Control).Script == ScriptKind.Passthrough)))
                throw new InvalidOperationException("Map each sequence command to an enabled sequence/Lua control and enable sequences before listening.");
            InvalidateVisionLocked("Voice commands enabled. Vision assistance is disarmed.");
            return _voiceCommands.Activate(settings);
        }
    }
    internal bool EnqueueVoiceCommand(long generation, VoiceCommandObservation observation)
    { lock (_sync) return _voiceCommands.Enqueue(generation, observation, Stopwatch.GetTimestamp()); }
    internal void StopVoiceCommands(long? generation = null)
    { lock (_sync) { if (generation.HasValue && generation.Value != _voiceCommands.Generation) return; _voiceCommands.Reset("Voice commands stopped."); _engine.CancelExternal(); } }
    internal void ConfigureCoPilot(CoPilotSettings settings)
    {
        settings.Validate();
        bool begun = false;
        try
        {
            lock (_sync)
            {
                RequireTrainingStoppedLocked("changing co-pilot inputs");
                if (_recorder is not null) throw new InvalidOperationException("Finish macro recording before changing input contributors.");
                _configuringInput = true; _inputNeedsNeutral = true; begun = true;
                Interlocked.Increment(ref _selectionVersion);
                _engine.SetEnabled(false); _sticky.Reset(); _smoother.Reset(); _navigation.ResetInput();
                InvalidateVisionLocked("Input composition changed. Release controls and explicitly enable assistance again.");
                _output?.Neutralize();
            }
            // Native enumeration/disposal may wait for a reader. Never hold the
            // controller/deadline lock while configuring physical devices.
            _input.ConfigureCoPilot(settings);
        }
        finally
        {
            if (begun) lock (_sync) { _configuringInput = false; _navigation.ResetInput(); Interlocked.Increment(ref _selectionVersion); }
        }
    }
    internal void ConfigureFeedback(FeedbackSettings settings) => (_feedback ?? throw new InvalidOperationException("Feedback devices are unavailable.")).Configure(settings);
    internal bool TestFeedback() => _feedback?.TryPublish(FeedbackCueKind.Test, new(-1, Interlocked.Increment(ref _testFeedbackToken)),
        _latest?.Input.ConnectionVersion ?? 0) == true;

    internal void ConfigureNavigation(SwitchNavigationSettings settings)
    {
        settings.Validate();
        lock (_sync)
        {
            if (settings.Enabled && _latest?.Input.NavigationConnected != true)
                throw new InvalidOperationException("Connect the selected physical controller before using controller switches.");
            if (_configuringInput) throw new InvalidOperationException("Controller inputs are being configured. Try again shortly.");
            if (settings.Enabled && (_visionTrainingRecording || _recorder is not null))
                throw new InvalidOperationException("Finish recording before routing controller input to Studio navigation.");
            bool wasEnabled = _navigation.Enabled;
            _navigation.Configure(settings);
            _navigationNeedsNeutral |= wasEnabled || settings.Enabled;
            _sticky.Reset(); _smoother.Reset();
            if (settings.Enabled) _engine.SetEnabled(false);
            InvalidateVisionLocked("Controller navigation changed. Assistance is disarmed.");
            _output?.Neutralize();
        }
    }

    public void ConfigureVision(VisionSettings settings, VisionControllerAction? action = null)
    {
        lock (_sync) { CancelLuaCalibrationLocked("Capture configuration changed."); _feedback?.Stop(); _vision.Configure(settings, action); }
        _releaseScheduler.Signal();
    }

    public bool TryAdjustVisionTiming(Func<VisionSettings, VisionSettings> update, VisionControllerAction? action = null)
    {
        lock (_sync)
        {
            if (!_vision.TryAdjustTiming(update, action)) return false;
        }
        _releaseScheduler.Signal();
        return true;
    }

    public VisionActionReceipt? LastVisionAction { get { lock (_sync) return _vision.LastAction; } }
    internal bool StageVisionFeedbackTiming(VisionActionReceipt receipt, double timing)
    { lock (_sync) return _vision.StageFeedbackTiming(receipt, timing); }
    public StreamTelemetryAssessment VisionStreamTelemetry { get { lock (_sync) return _vision.StreamTelemetry(Stopwatch.GetTimestamp()); } }
    internal void ObserveVisionStreamTelemetry(DirectStreamMetrics reading) { lock (_sync) _vision.ObserveStreamTelemetry(reading); }
    public void ObserveVisionStreamTelemetry(StreamTelemetryReading reading) { lock (_sync) _vision.ObserveStreamTelemetry(reading); }
    public VisionActionReceipt? LastVisionAttempt { get { lock (_sync) return _vision.LastAttempt; } }
    internal VisionActionReceipt[] VisionAttemptsAfter(long id) { lock (_sync) return _vision.AttemptsAfter(id); }
    internal void NoteVisionForecastLimit(long generation, long token, string reason)
    { lock (_sync) _vision.NoteForecastLimit(generation, token, reason); }
    public VisionControllerAction VisionAction { get { lock (_sync) return _vision.Action; } }
    public long LatestInputTimestamp => Volatile.Read(ref _latestInputSampleTicks);
    public VisionSignalContext ReadVisionSignalContext()
    {
        lock (_sync)
        {
            var status = _vision.Status;
            return new(_latest?.Input is { IsConnected: true } input ? input.State : null,
                _latestInputSampleTicks, status.SessionGeneration, status.ShotToken, status.ShotStartedTimestamp, status.Armed)
            {
                HoldStartedTimestamp = _vision.HoldStartedTimestamp, TriggerSample = _vision.TriggerSample,
                StrokeAnchorTimestamp = _vision.StrokeAnchorTimestamp,
                MinimumStrokeLeadMs = _vision.MinimumStrokeLeadMs,
                HeldPaddles = _latest?.Input is { IsConnected: true } connectedInput ? connectedInput.HeldPaddles : PaddleButtons.None
            };
        }
    }

    public long BeginVisionSession()
    {
        long generation;
        lock (_sync) { CancelLuaCalibrationLocked("Capture session changed."); _feedback?.Stop(); generation = _vision.BeginSession(); }
        _releaseScheduler.Signal();
        return generation;
    }

    public void InvalidateVisionSession(string reason)
    {
        lock (_sync) { CancelLuaCalibrationLocked(reason); _feedback?.Stop(); _vision.Invalidate(reason, sourceLost: true, disarm: true); }
        _releaseScheduler.Signal();
    }

    public bool SetVisionArmed(bool armed)
    {
        bool accepted;
        lock (_sync)
        {
            if (armed) CancelLuaCalibrationLocked("Physical-stick assistance selected.");
            if (armed && (_output?.IsAvailable != true || _latest?.Input.IsConnected != true || _recorder is not null
                || VisionMacroConflict()))
            {
                InvalidateVisionLocked("Arming requires available input/output and no recording or conflicting macro.");
                return false;
            }
            _feedback?.Stop(); accepted = _vision.SetArmed(armed);
        }
        _releaseScheduler.Signal();
        return accepted;
    }

    public bool SubmitVisionPrediction(VisionPrediction prediction)
    {
        bool accepted;
        long token = Volatile.Read(ref _lockMeasurementToken), requested = token != 0 ? Stopwatch.GetTimestamp() : 0;
        lock (_sync)
        {
            if (token != 0) _lockMeasurements.Record(RuntimeLockPath.VisionSubmission, token, requested, Stopwatch.GetTimestamp());
            var before = _vision.Status;
            accepted = _vision.Submit(prediction, Stopwatch.GetTimestamp());
            if (accepted && _vision.StrokeStartDue(Stopwatch.GetTimestamp())) AdvanceVisionDeadlineLocked();
            CancelLostLockFeedbackLocked(before);
            var identity = new FeedbackIdentity(prediction.SessionGeneration, prediction.ShotToken);
            if (accepted && _lockedFeedback != identity)
            {
                _lockedFeedback = identity;
                _feedback?.TryPublish(FeedbackCueKind.PlayerLocked, identity, _latest?.Input.ConnectionVersion ?? 0);
            }
        }
        _releaseScheduler.Signal();
        return accepted;
    }

    public void SetVisionTrainingRecording(bool recording)
    {
        lock (_sync)
        {
            // The entry check and all script/recording transitions share this lock, so training
            // cannot race an enable request after VisionSession's earlier UI eligibility check.
            if (recording && (_engine.ScriptsEnabled || _recorder is not null || _navigation.Enabled || _navigationNeedsNeutral || _configuringInput || _inputNeedsNeutral || _voiceCommands.Enabled))
                throw new InvalidOperationException("Turn off sequences and controller navigation, release controls, and finish macro recording before recording manual gameplay training data.");
            _visionTrainingRecording = recording;
            _sticky.Reset();
            _smoother.Reset();
            _feedback?.Stop();
            _vision.SetTrainingRecording(recording);
        }
        _releaseScheduler.Signal();
    }

    internal void SetVisionTrainingTrace(VisionTrainingTrace? trace)
    {
        // Polling and scheduled output use the same lock. Returning from detach fences every
        // producer, allowing the caller to finalize the trace without post-detach records.
        lock (_sync)
        {
            _visionTrainingTrace = trace;
            trace?.SetInputComposition(_input.CoPilotSettings, _latest?.Input.PrimaryConnected == true, _latest?.Input.CompanionConnected == true);
            _visionTrainingTraceAttachedAt = Stopwatch.GetTimestamp();
        }
    }

    public RecorderState Recording
    {
        get
        {
            lock (_sync)
                return new(_latest?.Input.IsConnected == true, _recorder is not null, _recorder?.HasInput == true,
                    _latest?.Input.SupportsBackgroundInput == true,
                    _recorder?.ElapsedMs ?? 0, _pending, _recordingNote);
        }
    }

    public StudioRuntime(IControllerInput input, StudioProfile initial, StickDeadzone stickDeadzone, FeedbackDispatcher? feedback = null)
    {
        _input = input;
        _feedback = feedback;
        _stickDeadzone = stickDeadzone;
        _visionProfile = initial;
        _engine.Apply(initial);
        _sticky.Configure(initial.StickyControls);
        _smoother.Configure(initial.Smoothing);
        _releaseScheduler = new ReleaseScheduler(GetReleaseDeadline, AdvanceReleaseDeadline, HighResolutionReleaseTimer.TryCreate());
        _worker = new Thread(Poll) { IsBackground = true, Name = "Gamepad Studio input" };
        _worker.Start();
    }

    public void Apply(StudioProfile profile)
    {
        lock (_sync)
        {
            RequireTrainingStoppedLocked("applying or changing a macro profile");
            bool changed = _visionProfile.Id != profile.Id;
            _engine.Apply(profile);
            _sticky.Configure(profile.StickyControls);
            _smoother.Configure(profile.Smoothing);
            _visionProfile = profile;
            _navigation.ResetInput();
            InvalidateVisionLocked("Macro profile changed. Arm vision again after releasing the shoot button.");
            if (changed) _feedback?.TryPublish(FeedbackCueKind.ProfileChanged, new(0, ++_profileFeedbackToken), _latest?.Input.ConnectionVersion ?? 0);
        }
    }

    public void SetScriptsEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (enabled) RequireTrainingStoppedLocked("enabling macro scripts");
            _engine.SetEnabled(enabled);
            InvalidateVisionLocked("Macro script state changed. Arm vision again when there is no conflict.");
        }
    }

    public void SetStickDeadzone(StickDeadzone settings)
    {
        lock (_sync) { CancelLuaCalibrationLocked("Controller response changed."); _stickDeadzone = settings; _smoother.Reset(); }
    }

    public void SelectController(string? id)
    {
        lock (_sync)
        {
            _input.SelectDevice(id);
            _navigation.ResetInput();
            _sticky.Reset();
            _smoother.Reset();
            Interlocked.Increment(ref _selectionVersion);
            _engine.Tick(default, PaddleButtons.None, false, _clock.ElapsedMilliseconds);
            InvalidateVisionLocked("Physical controller selection changed.");
            _output?.Neutralize();
        }
    }

    public void ReserveVirtualOutput() => _input.SetVirtualOutputActive(true);

    public void ReleaseVirtualOutputReservation()
    {
        lock (_sync)
        {
            if (_output is null)
                _input.SetVirtualOutputActive(false);
        }
    }

    public void StartRecording(RecordingChannels channels, bool replacePending = false)
    {
        lock (_sync)
        {
            RequireTrainingStoppedLocked("starting a macro recording");
            if (_navigation.Enabled || _navigationNeedsNeutral || _inputNeedsNeutral || _voiceCommands.Enabled)
                throw new InvalidOperationException("Exit controller navigation and release the controls before recording manual input.");
            if (_recorder is not null)
                return;
            if (_pending is not null && !replacePending)
                throw new InvalidOperationException("Save or discard the previous recording first.");
            if (_latest?.Input is not { IsConnected: true } input)
                throw new InvalidOperationException("Connect a controller to start a recording.");

            _recorder = new InputRecorder(channels);
            _sticky.Reset();
            _smoother.Reset();
            _pending = null;
            _recordingConnection = input.ConnectionVersion;
            _recordingBeganAt = _clock.ElapsedMilliseconds;
            _recordingNote = null;
            InvalidateVisionLocked("Recording started. Vision release is disarmed.");
            _engine.Tick(default, PaddleButtons.None, false, _recordingBeganAt);
        }
    }

    public void StopRecording()
    {
        lock (_sync)
            FinishRecording(_clock.ElapsedMilliseconds, null);
    }

    public void DiscardRecording()
    {
        lock (_sync)
        {
            _recorder = null;
            _pending = null;
            _recordingNote = null;
            InvalidateVisionLocked("Recording state changed. Vision release is disarmed.");
        }
    }

    private void FinishRecording(long now, string? note)
    {
        if (_recorder is null)
            return;
        _pending = _recorder.Stop("Untitled recording", now);
        _recorder = null;
        InvalidateVisionLocked("Recording finished. Vision release is disarmed.");
        _recordingNote = _pending is not null ? note
            : "No input was captured. Move an included physical control during recording, or choose All inputs and try again.";
    }

    public void Attach(IControllerOutput output)
    {
        lock (_sync)
        {
            DisconnectOutput();
            _engine.Tick(default, PaddleButtons.None, false, 0);
            _outputStatus = output.Status;
            if (output.IsAvailable)
            {
                _input.SetVirtualOutputActive(output.UsesVirtualDevice);
                _output = output;
            }
            else
            {
                try { output.Dispose(); }
                finally { _input.SetVirtualOutputActive(false); }
            }
        }
    }

    public void StopOutput(bool stopScripts)
    {
        lock (_sync)
        {
            if (stopScripts)
            {
                _engine.SetEnabled(false);
                if (_recorder is not null)
                {
                    _recorder = null;
                    _recordingNote = "Recording cancelled by Stop all.";
                }
            }
            else
                _engine.Tick(default, PaddleButtons.None, false, 0);
            DisconnectOutput();
        }
    }

    private void DisconnectOutput(string status = "Virtual output off")
    {
        _vision.ClearActionReceipt();
        _inputNeedsNeutral |= _input.CoPilotSettings.Enabled;
        _lastReleaseFeedback = _vision.Status.AutomatedReleases;
        if (_navigation.Enabled) { _navigation.Stop(); _navigationNeedsNeutral = true; }
        _sticky.Reset();
        _smoother.Reset();
        InvalidateVisionLocked("Virtual output stopped. Vision release is disarmed.");
        var output = _output;
        _output = null;
        _outputStatus = status;
        if (output is null)
            return;
        try { output.Neutralize(); }
        finally
        {
            try { output.Dispose(); }
            finally { _input.SetVirtualOutputActive(false); }
        }
    }

    private void Poll()
    {
        using var timer = PrecisionTimer.TryStart(4);
        WaitHandle[]? wake = timer is null ? null : [_stop.Token.WaitHandle, timer];
        long connection = -1;
        double last = 0;
        double nextStats = 0;
        var intervals = new List<double>(160);
        var feedback = new PhysicalInputFeedback();
        double mean = 0, p95 = 0, p99 = 0;
        long paddlePulse = 0;
        var lastPaddles = PaddleButtons.None;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                int selection = Volatile.Read(ref _selectionVersion);
                var input = _input.Poll();
                long sampleTicks = Stopwatch.GetTimestamp();
                if (input.PressedPaddles != PaddleButtons.None)
                {
                    paddlePulse++;
                    lastPaddles = input.PressedPaddles;
                }
                double elapsed = _clock.Elapsed.TotalMilliseconds;
                long now = (long)elapsed;
                uint visible = feedback.Sample(input.State, input.HeldPaddles | input.PressedPaddles,
                    input.IsConnected, input.ConnectionVersion, now);
                if (last > 0)
                    intervals.Add(elapsed - last);
                last = elapsed;
                if (elapsed >= nextStats && intervals.Count != 0)
                {
                    intervals.Sort();
                    mean = intervals.Average();
                    p95 = intervals[(int)Math.Ceiling(intervals.Count * .95) - 1];
                    p99 = intervals[(int)Math.Ceiling(intervals.Count * .99) - 1];
                    intervals.Clear();
                    nextStats = elapsed + 500;
                }

                long lockToken = Volatile.Read(ref _lockMeasurementToken), lockRequested = lockToken != 0 ? Stopwatch.GetTimestamp() : 0;
                lock (_sync)
                {
                    if (selection != _selectionVersion)
                        continue;
                    if (lockToken != 0) _lockMeasurements.Record(RuntimeLockPath.InputPoll, lockToken, lockRequested, Stopwatch.GetTimestamp());
                    if (connection != input.ConnectionVersion)
                    {
                        connection = input.ConnectionVersion;
                        _navigation.ResetInput();
                        _sticky.Reset();
                        _smoother.Reset();
                        _engine.Tick(default, PaddleButtons.None, false, now);
                        InvalidateVisionLocked("Physical controller connection changed. Vision release is disarmed.");
                    }
                    ControllerState state;
                    bool pickingUp = SampleLuaCalibrationPickupLocked(input, now, out var pickupState);
                    if (!_configuringInput) _navigation.Sample(input.NavigationState, input.HeldPaddles | input.PressedPaddles, input.NavigationConnected, now);
                    if (!_configuringInput && _inputNeedsNeutral && input.IsConnected
                        && ControlMap.PressedMask(input.State, input.HeldPaddles | input.PressedPaddles) == 0
                        && _stickDeadzone.Apply(input.State) is { LeftX: 0, LeftY: 0, RightX: 0, RightY: 0 })
                        _inputNeedsNeutral = false;
                    if (!_navigation.Enabled && _navigationNeedsNeutral && input.IsConnected
                        && ControlMap.PressedMask(input.State, input.HeldPaddles | input.PressedPaddles) == 0
                        && _stickDeadzone.Apply(input.State) is { LeftX: 0, LeftY: 0, RightX: 0, RightY: 0 })
                        _navigationNeedsNeutral = false;
                    var assigned = _vision.Status.Armed && _vision.Action.HasAssignedTrigger ? _vision.Action : null;
                    if (_navigation.Enabled || _navigationNeedsNeutral || _configuringInput || _inputNeedsNeutral)
                        state = default;
                    else if (_recorder is not null)
                    {
                        // Capture from the physical sample before any script or virtual-output processing.
                        // The unfiltered sample remains available for manual play during the take.
                        if (!input.IsConnected || input.ConnectionVersion != _recordingConnection)
                        {
                            _recorder = null;
                            _recordingNote = "Recording cancelled because the controller connection changed.";
                        }
                        else if (now >= _recordingBeganAt && !_recorder.Capture(input.State, now))
                            FinishRecording(now, "Recording limit reached. Review the take before saving.");
                        state = input.IsConnected ? input.State : default;
                    }
                    else if (_visionTrainingRecording)
                        state = input.IsConnected ? input.State : default;
                    else
                    {
                        var calibrationTrigger = SampleLuaCalibrationLocked(input, now);
                        bool manualActive = input.IsConnected && _output?.IsAvailable == true;
                        var bindingInput = assigned?.WithoutAssignedTrigger(input.State) ?? input.State;
                        var manual = _smoother.Sample(_stickDeadzone.Apply(bindingInput), now, manualActive);
                        manual = _sticky.Sample(manual, manualActive);
                        if (assigned is not null) manual = assigned.WithoutAssignedTrigger(manual);
                        var voice = _voiceCommands.Sample(Stopwatch.GetTimestamp());
                        var reserved = assigned?.ReservedPaddles ?? PaddleButtons.None;
                        var engineHeld = input.HeldPaddles & ~reserved;
                        var enginePressed = input.PressedPaddles & ~reserved;
                        state = VoiceCommandArbiter.Compose(_engine.Tick(bindingInput, engineHeld, input.IsConnected, now, enginePressed,
                            manual, calibrationTrigger ?? voice.Sequence), voice.Pulse);
                    }
                    if (pickingUp) state = pickupState;
                    if (_luaCalibrationConsumed is { } consumed && !ControlMap.IsPressed(consumed, input.State, input.HeldPaddles))
                        _luaCalibrationConsumed = null;
                    if (_luaCalibrationControl is { } trialControl) state = ControlMap.Consume(state, (uint)trialControl);
                    else if (_luaCalibrationConsumed is { } heldControl) state = ControlMap.Consume(state, (uint)heldControl);
                    state = _stickDeadzone.Apply(state);
                    var beforeVision = _vision.Status;
                    state = _vision.Sample(input.State, state, input.IsConnected, _output?.IsAvailable == true,
                        _recorder is not null || VisionMacroConflict(), Stopwatch.GetTimestamp(), sampleTicks, input.HeldPaddles, input.PressedPaddles);
                    CancelLostLockFeedbackLocked(beforeVision);
                    _latestInputSampleTicks = sampleTicks;
                    bool accepted = WriteTracedOutputLocked(state, VisionTrainingTraceKind.Poll, sampleTicks, input);
                    CompleteLuaCalibrationLocked(input, state, accepted, now);
                    PublishReleaseFeedbackLocked();
                    Volatile.Write(ref _latest, new(input, state, _output is not null, _outputStatus,
                        _engine.Status, mean, p95, p99, paddlePulse, lastPaddles, visible));
                }
            }
            catch (Exception error)
            {
                lock (_sync)
                {
                    if (_recorder is not null)
                    {
                        _recorder = null;
                        _recordingNote = "Recording cancelled after a controller error.";
                    }
                    _engine.Stop();
                    try { DisconnectOutput($"Input paused. {error.Message}"); }
                    catch { _outputStatus = "Input paused after a controller error"; }
                    var previous = Latest;
                    if (previous is not null)
                        Volatile.Write(ref _latest, previous with
                        {
                            Input = previous.Input with { IsConnected = false, State = default, HeldPaddles = PaddleButtons.None },
                            OutputActive = false, Output = default, OutputStatus = _outputStatus, VisibleControls = 0
                        });
                }
            }
            if (wake is null)
                _stop.Token.WaitHandle.WaitOne(4);
            else if (WaitHandle.WaitAny(wake) == 0)
                break;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.Join();
        _releaseScheduler.Dispose();
        try { StopOutput(stopScripts: true); }
        finally
        {
            try { _feedback?.Dispose(); } finally { _input.Dispose(); }
            _stop.Dispose();
        }
    }

    private bool VisionMacroConflict()
    {
        if (_navigation.Enabled || _navigationNeedsNeutral || _configuringInput || _inputNeedsNeutral || _voiceCommands.Enabled) return true;
        // Right-axis input does not activate an idle digital-button binding. Any
        // running sequence still suspends the vision owner before output composition.
        if (_vision.Action.Kind == VisionActionKind.RightStickStroke) return _engine.Status.IsActive;
        if (_visionProfile.StickyControls.Contains(_vision.Settings.ShootButton)) return true;
        if (!_engine.ScriptsEnabled) return false;
        var binding = _visionProfile.GetBinding(_vision.Settings.ShootButton);
        return _engine.Status.IsActive || (binding.Enabled && binding.Script != ScriptKind.Passthrough);
    }

    private void RequireTrainingStoppedLocked(string action)
    {
        if (_configuringInput) throw new InvalidOperationException("Controller inputs are being configured. Try again shortly.");
        if (_visionTrainingRecording)
            throw new InvalidOperationException($"Finish the gameplay training take before {action}. Training captures manual input only.");
    }

    private void InvalidateVisionLocked(string reason)
    {
        CancelLuaCalibrationLocked(reason);
        _voiceCommands.Reset(reason); _engine.CancelExternal();
        _feedback?.Stop();
        _vision.Invalidate(reason, disarm: true);
        _releaseScheduler.Signal();
    }

    private long? GetReleaseDeadline()
    {
        lock (_sync) return _stop.IsCancellationRequested ? null : _vision.DeadlineTimestamp;
    }

    private void AdvanceReleaseDeadline(long callbackTimestamp)
    {
        long token = Volatile.Read(ref _lockMeasurementToken), requested = token != 0 ? Stopwatch.GetTimestamp() : 0;
        lock (_sync)
        {
            if (token != 0) _lockMeasurements.Record(RuntimeLockPath.ReleaseDeadline, token, requested, Stopwatch.GetTimestamp());
            AdvanceVisionDeadlineLocked();
        }
    }

    private void AdvanceVisionDeadlineLocked()
    {
        if (_stop.IsCancellationRequested) return;
        var before = _vision.Status;
        // Callback time can precede waiting for this lock. Recheck lateness
        // and physical-input freshness using the clock after ownership is acquired.
        if (!_vision.TryAdvanceDeadline(Stopwatch.GetTimestamp())) { CancelLostLockFeedbackLocked(before); return; }
        try
        {
            WriteTracedOutputLocked(_vision.Output, VisionTrainingTraceKind.DeadlineOutput,
                _latestInputSampleTicks, _latest?.Input ?? default);
            PublishReleaseFeedbackLocked();
            if (_latest is { } previous)
                Volatile.Write(ref _latest, previous with { Output = _vision.Output, OutputActive = _output is not null });
        }
        catch (Exception)
        {
            try { DisconnectOutput("Vision release stopped after an output error."); }
            catch { _outputStatus = "Vision release stopped after an output error."; }
        }
    }

    private void PublishReleaseFeedbackLocked()
    {
        var status = _vision.Status;
        if (status.AutomatedReleases == _lastReleaseFeedback) return;
        _lastReleaseFeedback = status.AutomatedReleases;
        if (_output is not null)
            _feedback?.TryPublish(FeedbackCueKind.Released, new(status.SessionGeneration, status.ShotToken), _latest?.Input.ConnectionVersion ?? 0);
    }

    private void CancelLostLockFeedbackLocked(VisionStatusSnapshot before)
    {
        if (before.ShotToken != 0 && _vision.Status.ShotToken == 0
            && before.State is VisionRunState.TrackingShot or VisionRunState.ReleaseScheduled)
            _feedback?.Stop();
    }

    private bool WriteTracedOutputLocked(ControllerState state, VisionTrainingTraceKind kind,
        long sampleTicks, ControllerSample input)
    {
        var trace = _visionTrainingTrace;
        if (trace is null || sampleTicks < _visionTrainingTraceAttachedAt)
        {
            return WriteOutputLocked(state);
        }
        bool outputAvailable = _output?.IsAvailable == true;
        long before = Stopwatch.GetTimestamp();
        bool accepted = false;
        try { accepted = WriteOutputLocked(state); return accepted; }
        finally
        {
            long after = Stopwatch.GetTimestamp();
            trace.Record(new(kind, sampleTicks, before, after, input.State, state,
                input.IsConnected, input.ConnectionVersion, input.HeldPaddles, input.PressedPaddles,
                outputAvailable, accepted));
        }
    }

    private bool WriteOutputLocked(ControllerState state)
    {
        // This is the sole report-writing path for polling and deadline wakes alike.
        if (_output is null) return false;
        if (_output.TryWrite(state)) { _vision.RecordOutputSubmitted(state, Stopwatch.GetTimestamp()); return true; }
        DisconnectOutput(_output.Status);
        return false;
    }
}
