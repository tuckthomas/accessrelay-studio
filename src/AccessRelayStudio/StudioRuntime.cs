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

    public void ConfigureVision(VisionSettings settings)
    {
        lock (_sync) { _feedback?.Stop(); _vision.Configure(settings); }
        _releaseScheduler.Signal();
    }

    public long BeginVisionSession()
    {
        long generation;
        lock (_sync) { _feedback?.Stop(); generation = _vision.BeginSession(); }
        _releaseScheduler.Signal();
        return generation;
    }

    public void InvalidateVisionSession(string reason)
    {
        lock (_sync) { _feedback?.Stop(); _vision.Invalidate(reason, sourceLost: true, disarm: true); }
        _releaseScheduler.Signal();
    }

    public bool SetVisionArmed(bool armed)
    {
        bool accepted;
        lock (_sync)
        {
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
        lock (_sync) { _stickDeadzone = settings; _smoother.Reset(); }
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
                _input.SetVirtualOutputActive(true);
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
                    if (!_configuringInput) _navigation.Sample(input.NavigationState, input.HeldPaddles | input.PressedPaddles, input.NavigationConnected, now);
                    if (!_configuringInput && _inputNeedsNeutral && input.IsConnected
                        && ControlMap.PressedMask(input.State, input.HeldPaddles | input.PressedPaddles) == 0
                        && _stickDeadzone.Apply(input.State) is { LeftX: 0, LeftY: 0, RightX: 0, RightY: 0 })
                        _inputNeedsNeutral = false;
                    if (!_navigation.Enabled && _navigationNeedsNeutral && input.IsConnected
                        && ControlMap.PressedMask(input.State, input.HeldPaddles | input.PressedPaddles) == 0
                        && _stickDeadzone.Apply(input.State) is { LeftX: 0, LeftY: 0, RightX: 0, RightY: 0 })
                        _navigationNeedsNeutral = false;
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
                        bool manualActive = input.IsConnected && _output?.IsAvailable == true;
                        var manual = _smoother.Sample(_stickDeadzone.Apply(input.State), now, manualActive);
                        manual = _sticky.Sample(manual, manualActive);
                        var voice = _voiceCommands.Sample(Stopwatch.GetTimestamp());
                        state = VoiceCommandArbiter.Compose(_engine.Tick(input.State, input.HeldPaddles, input.IsConnected, now, input.PressedPaddles,
                            manual, voice.Sequence), voice.Pulse);
                    }
                    state = _stickDeadzone.Apply(state);
                    var beforeVision = _vision.Status;
                    state = _vision.Sample(input.State, state, input.IsConnected, _output?.IsAvailable == true,
                        _recorder is not null || VisionMacroConflict(), Stopwatch.GetTimestamp());
                    CancelLostLockFeedbackLocked(beforeVision);
                    _latestInputSampleTicks = sampleTicks;
                    WriteTracedOutputLocked(state, VisionTrainingTraceKind.Poll, sampleTicks, input);
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

    private void WriteTracedOutputLocked(ControllerState state, VisionTrainingTraceKind kind,
        long sampleTicks, ControllerSample input)
    {
        var trace = _visionTrainingTrace;
        if (trace is null || sampleTicks < _visionTrainingTraceAttachedAt)
        {
            WriteOutputLocked(state);
            return;
        }
        bool outputAvailable = _output?.IsAvailable == true;
        long before = Stopwatch.GetTimestamp();
        bool accepted = false;
        try { accepted = WriteOutputLocked(state); }
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
        if (_output.TryWrite(state)) return true;
        DisconnectOutput(_output.Status);
        return false;
    }
}
