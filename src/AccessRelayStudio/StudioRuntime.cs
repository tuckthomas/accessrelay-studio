using System.Diagnostics;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;

namespace EliteScriptStudio;

internal sealed record RuntimeSample(ControllerSample Input, ControllerState Output, bool OutputActive,
    string OutputStatus, MacroStatus Macro, double MeanPollMs, double P95PollMs, double P99PollMs,
    long PaddlePulse, PaddleButtons LastPaddles, uint VisibleControls);

internal readonly record struct RecorderState(bool Connected, bool IsRecording, int ElapsedMs,
    MacroRecording? Pending, string? Note);

internal sealed class StudioRuntime : IDisposable
{
    private readonly EliteInputMonitor _input;
    private readonly MacroEngine _engine = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IControllerOutput? _output;
    private string _outputStatus = "Virtual output off";
    private RuntimeSample? _latest;
    private InputRecorder? _recorder;
    private MacroRecording? _pending;
    private string? _recordingNote;
    private long _recordingConnection;
    private long _recordingBeganAt;
    private int _selectionVersion;

    public RuntimeSample? Latest => Volatile.Read(ref _latest);
    public bool HasOutput { get { lock (_sync) return _output is not null; } }
    public bool ScriptsEnabled => _engine.ScriptsEnabled;
    public double? RecentOutputHandoffMs { get { lock (_sync) return (_output as UsbipXboxOutput)?.RecentHandoffMs; } }

    public RecorderState Recording
    {
        get
        {
            lock (_sync)
                return new(_latest?.Input.IsConnected == true, _recorder is not null,
                    _recorder?.ElapsedMs ?? 0, _pending, _recordingNote);
        }
    }

    public StudioRuntime(EliteInputMonitor input, StudioProfile initial)
    {
        _input = input;
        _engine.Apply(initial);
        _worker = new Thread(Poll) { IsBackground = true, Name = "Gamepad Studio input" };
        _worker.Start();
    }

    public void Apply(StudioProfile profile)
    {
        lock (_sync) _engine.Apply(profile);
    }

    public void SetScriptsEnabled(bool enabled)
    {
        lock (_sync) _engine.SetEnabled(enabled);
    }

    public void SelectController(string? id)
    {
        lock (_sync)
        {
            _input.SelectDevice(id);
            Interlocked.Increment(ref _selectionVersion);
            _engine.Tick(default, PaddleButtons.None, false, _clock.ElapsedMilliseconds);
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

    public void StartRecording(RecordingChannels channels)
    {
        lock (_sync)
        {
            if (_recorder is not null)
                return;
            if (_pending is not null)
                throw new InvalidOperationException("Save or discard the previous recording first.");
            if (_latest?.Input is not { IsConnected: true } input)
                throw new InvalidOperationException("Connect a controller to start a recording.");

            _recorder = new InputRecorder(channels);
            _recordingConnection = input.ConnectionVersion;
            _recordingBeganAt = _clock.ElapsedMilliseconds;
            _recordingNote = "Recording physical input. Scripts are paused during capture.";
            _engine.Tick(default, PaddleButtons.None, false, _recordingBeganAt);
        }
    }

    public void StopRecording()
    {
        lock (_sync)
            FinishRecording(_clock.ElapsedMilliseconds, "Recording ready to save.");
    }

    public void DiscardRecording()
    {
        lock (_sync)
        {
            _recorder = null;
            _pending = null;
            _recordingNote = null;
        }
    }

    private void FinishRecording(long now, string note)
    {
        if (_recorder is null)
            return;
        _pending = _recorder.Stop("Untitled recording", now);
        _recorder = null;
        _recordingNote = _pending is not null ? note : "No input matched the selected recording filters.";
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
                    else
                        state = _engine.Tick(input.State, input.HeldPaddles, input.IsConnected, now, input.PressedPaddles);
                    if (_output is not null && !_output.TryWrite(state))
                        DisconnectOutput(_output.Status);
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
        try { StopOutput(stopScripts: true); }
        finally
        {
            _input.Dispose();
            _stop.Dispose();
        }
    }
}
