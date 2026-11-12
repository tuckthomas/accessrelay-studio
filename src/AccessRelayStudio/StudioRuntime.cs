using System.Diagnostics;
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
    private readonly EliteInputMonitor _input;
    private readonly MacroEngine _engine = new();
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

    public RuntimeSample? Latest => Volatile.Read(ref _latest);
    public bool HasOutput { get { lock (_sync) return _output is not null; } }
    public bool ScriptsEnabled => _engine.ScriptsEnabled;
    public bool IsVisionTrainingRecording { get { lock (_sync) return _visionTrainingRecording; } }
    public double? RecentOutputHandoffMs { get { lock (_sync) return (_output as UsbipXboxOutput)?.RecentHandoffMs; } }
    public VisionStatusSnapshot VisionStatus { get { lock (_sync) return _vision.Status; } }

    public void ConfigureVision(VisionSettings settings)
    {
        lock (_sync) _vision.Configure(settings);
        _releaseScheduler.Signal();
    }

    public long BeginVisionSession()
    {
        long generation;
        lock (_sync) generation = _vision.BeginSession();
        _releaseScheduler.Signal();
        return generation;
    }

    public void InvalidateVisionSession(string reason)
    {
        lock (_sync) _vision.Invalidate(reason, sourceLost: true, disarm: true);
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
            accepted = _vision.SetArmed(armed);
        }
        _releaseScheduler.Signal();
        return accepted;
    }

    public bool SubmitVisionPrediction(VisionPrediction prediction)
    {
        bool accepted;
        lock (_sync) accepted = _vision.Submit(prediction, Stopwatch.GetTimestamp());
        _releaseScheduler.Signal();
        return accepted;
    }

    public void SetVisionTrainingRecording(bool recording)
    {
        lock (_sync)
        {
            // The entry check and all script/recording transitions share this lock, so training
            // cannot race an enable request after VisionSession's earlier UI eligibility check.
            if (recording && (_engine.ScriptsEnabled || _recorder is not null))
                throw new InvalidOperationException("Turn off macro scripts and finish macro recording before recording manual gameplay training data.");
            _visionTrainingRecording = recording;
            _vision.SetTrainingRecording(recording);
        }
        _releaseScheduler.Signal();
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

    public StudioRuntime(EliteInputMonitor input, StudioProfile initial, StickDeadzone stickDeadzone)
    {
        _input = input;
        _stickDeadzone = stickDeadzone;
        _visionProfile = initial;
        _engine.Apply(initial);
        _releaseScheduler = new ReleaseScheduler(GetReleaseDeadline, AdvanceReleaseDeadline, HighResolutionReleaseTimer.TryCreate());
        _worker = new Thread(Poll) { IsBackground = true, Name = "Gamepad Studio input" };
        _worker.Start();
    }

    public void Apply(StudioProfile profile)
    {
        lock (_sync)
        {
            RequireTrainingStoppedLocked("applying or changing a macro profile");
            _engine.Apply(profile);
            _visionProfile = profile;
            InvalidateVisionLocked("Macro profile changed. Arm vision again after releasing the shoot button.");
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
        lock (_sync) _stickDeadzone = settings;
    }

    public void SelectController(string? id)
    {
        lock (_sync)
        {
            _input.SelectDevice(id);
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
            if (_recorder is not null)
                return;
            if (_pending is not null && !replacePending)
                throw new InvalidOperationException("Save or discard the previous recording first.");
            if (_latest?.Input is not { IsConnected: true } input)
                throw new InvalidOperationException("Connect a controller to start a recording.");

            _recorder = new InputRecorder(channels);
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

                lock (_sync)
                {
                    if (selection != _selectionVersion)
                        continue;
                    if (connection != input.ConnectionVersion)
                    {
                        connection = input.ConnectionVersion;
                        _engine.Tick(default, PaddleButtons.None, false, now);
                        InvalidateVisionLocked("Physical controller connection changed. Vision release is disarmed.");
                    }
                    ControllerState state;
                    if (_recorder is not null)
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
                        state = _engine.Tick(input.State, input.HeldPaddles, input.IsConnected, now, input.PressedPaddles);
                    state = _stickDeadzone.Apply(state);
                    state = _vision.Sample(input.State, state, input.IsConnected, _output?.IsAvailable == true,
                        _recorder is not null || VisionMacroConflict(), Stopwatch.GetTimestamp());
                    WriteOutputLocked(state);
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
            _input.Dispose();
            _stop.Dispose();
        }
    }

    private bool VisionMacroConflict()
    {
        if (!_engine.ScriptsEnabled) return false;
        var binding = _visionProfile.GetBinding(_vision.Settings.ShootButton);
        return _engine.Status.IsActive || (binding.Enabled && binding.Script != ScriptKind.Passthrough);
    }

    private void RequireTrainingStoppedLocked(string action)
    {
        if (_visionTrainingRecording)
            throw new InvalidOperationException($"Finish the gameplay training take before {action}. Training captures manual input only.");
    }

    private void InvalidateVisionLocked(string reason)
    {
        _vision.Invalidate(reason, disarm: true);
        _releaseScheduler.Signal();
    }

    private long? GetReleaseDeadline()
    {
        lock (_sync) return _stop.IsCancellationRequested ? null : _vision.DeadlineTimestamp;
    }

    private void AdvanceReleaseDeadline(long timestamp)
    {
        lock (_sync)
        {
            if (_stop.IsCancellationRequested || !_vision.TryAdvanceDeadline(timestamp)) return;
            try
            {
                WriteOutputLocked(_vision.Output);
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

    private void WriteOutputLocked(ControllerState state)
    {
        // This is the sole report-writing path for polling and deadline wakes alike.
        if (_output is not null && !_output.TryWrite(state)) DisconnectOutput(_output.Status);
    }
}
