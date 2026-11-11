namespace EliteScriptStudio.Core;

public readonly record struct MacroStatus(
    ScriptKind Script, ControlId? Control, bool LateMiss, int PhaseIndex,
    int ElapsedMs, int TotalMs, StickPhase? Phase, string? RecordingName = null, string? Error = null)
{
    public bool IsActive => Control.HasValue;
}

public sealed class MacroEngine
{
    private sealed record Plan(IReadOnlyList<StickPhase> Phases, int TotalMs, MacroRecording? Recording = null);
    private sealed record Run(ControlId Control, ScriptKind Script, bool LateMiss, Plan Plan, long StartedAtMs);

    private readonly object _sync = new();
    private StudioProfile _profile = StudioProfile.CreateDefault();
    private Plan[] _plans = [];
    private Dictionary<Guid, Plan> _recordingPlans = [];
    private Dictionary<Guid, LuaControllerProgram> _luaPrograms = [];
    private LuaControllerProgram? _lua;
    private ControlId? _luaControl;
    private long _luaStarted;
    private Run? _active;
    private MacroStatus _status;
    private uint _consume;
    private uint _previous;
    private bool _hasSample;
    private bool _enabled;
    private bool? _enabledOverride;
    private bool _nextMissLate;

    public MacroStatus Status
    {
        get { lock (_sync) return _status; }
    }

    internal bool ScriptsEnabled
    {
        get { lock (_sync) return _enabled; }
    }

    public void Apply(StudioProfile profile)
    {
        profile.Validate();
        var plans = Enum.GetValues<ShotVariant>().Select(variant =>
        {
            var phases = Trajectory.Build(profile, variant);
            return new Plan(phases, phases.Sum(phase => phase.DurationMs));
        }).ToArray();
        var recordingPlans = profile.Recordings.ToDictionary(
            entry => entry.Key, entry => new Plan([], entry.Value.DurationMs, entry.Value));
        var luaPrograms = profile.LuaScripts.ToDictionary(entry => entry.Key, entry => new LuaControllerProgram(entry.Value));
        uint consume = 0;
        foreach (var (control, binding) in profile.Bindings)
        {
            if (binding.Enabled && binding.Consume && binding.Script != ScriptKind.Passthrough)
                consume |= (uint)control;
        }

        lock (_sync)
        {
            _lua?.Cancel();
            _profile = profile;
            _plans = plans;
            _recordingPlans = recordingPlans;
            _luaPrograms = luaPrograms;
            _consume = consume;
            _enabled = _enabledOverride ?? profile.ScriptsEnabled;
            Reset();
        }
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabledOverride = enabled;
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            Reset();
        }
    }

    /// <summary>Cancel the active sequence and inhibit script triggers until Apply or SetEnabled.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _enabled = false;
            Reset();
        }
    }

    /// <summary>Sample at a monotonic time in milliseconds, such as Environment.TickCount64.</summary>
    public ControllerState Tick(ControllerState input, PaddleButtons paddles, bool connected, long timestampMs,
        PaddleButtons tapEdges = PaddleButtons.None)
    {
        lock (_sync)
        {
            if (!connected)
            {
                Reset();
                return default;
            }

            uint pressed = ControlMap.PressedMask(input, paddles);
            uint rising = _hasSample ? (pressed & ~_previous) | ControlMap.PressedMask(default, tapEdges) : 0;
            _previous = pressed;
            _hasSample = true;
            if (!_enabled)
                return input;

            if (_active is { } existing && Elapsed(existing, timestampMs) >= existing.Plan.TotalMs)
            {
                if (existing.Script == ScriptKind.IntentionalMiss)
                    _nextMissLate = !existing.LateMiss;
                _active = null;
            }

            if (rising != 0)
                StartPressedShot(rising, timestampMs);

            ControllerState output = ControlMap.Consume(input, _consume);
            if (_lua is { } lua)
            {
                output = lua.Tick(input, paddles, output, timestampMs, out var error);
                _status = new(ScriptKind.Lua, lua.IsRunning ? _luaControl : null, false, 0,
                    (int)Math.Min(int.MaxValue, Math.Max(0, timestampMs - _luaStarted)), 0, null, lua.Name, error);
                if (!lua.IsRunning)
                {
                    _lua = null;
                    _luaControl = null;
                }
                return output;
            }
            if (_active is not { } active)
            {
                if (_status.Error is null)
                    _status = default;
                return output;
            }

            int elapsed = (int)Elapsed(active, timestampMs);
            if (active.Plan.Recording is { } recording)
            {
                int index = recording.FrameIndexAt(elapsed);
                _status = new(active.Script, active.Control, false, index, elapsed, active.Plan.TotalMs, null, recording.Name);
                return RecordingFilter.Overlay(recording.Channels, output, recording.Frames[index].State);
            }

            int boundary = 0;
            for (int i = 0; i < active.Plan.Phases.Count; i++)
            {
                StickPhase phase = active.Plan.Phases[i];
                boundary += phase.DurationMs;
                if (elapsed < boundary)
                {
                    _status = new(active.Script, active.Control, active.LateMiss, i, elapsed, active.Plan.TotalMs, phase);
                    return output with { RightY = phase.RightY, RightX = phase.RightX ?? output.RightX };
                }
            }
            throw new InvalidOperationException("A running shot must contain a phase for its elapsed time.");
        }
    }

    private void StartPressedShot(uint rising, long timestampMs)
    {
        foreach (var control in ControlMap.TriggerOrder)
        {
            if ((rising & (uint)control) == 0 || _active?.Control == control || _luaControl == control)
                continue;
            var binding = _profile.GetBinding(control);
            if (!binding.Enabled || binding.Script == ScriptKind.Passthrough)
                continue;
            _lua?.Cancel();
            _lua = null;
            _luaControl = null;
            _active = null;
            _status = default;
            if (binding.Script == ScriptKind.Lua)
            {
                _lua = _luaPrograms[binding.LuaScriptId!.Value];
                _luaControl = control;
                _luaStarted = timestampMs;
                _lua.Start(control, timestampMs);
                return;
            }
            bool late = binding.Script == ScriptKind.IntentionalMiss && _nextMissLate;
            var plan = binding.Script == ScriptKind.Recorded
                ? _recordingPlans[binding.RecordingId!.Value]
                : _plans[(int)Trajectory.VariantFor(binding.Script, late)];
            _active = new(control, binding.Script, late, plan, timestampMs);
            return;
        }
    }

    private static long Elapsed(Run active, long timestampMs) => Math.Max(0, timestampMs - active.StartedAtMs);

    private void Reset()
    {
        foreach (var program in _luaPrograms.Values)
            program.Reset();
        _lua = null;
        _luaControl = null;
        _active = null;
        _status = default;
        _previous = 0;
        _hasSample = false;
        _nextMissLate = false;
    }
}
