namespace EliteScriptStudio.Core;

internal sealed record SwitchNavigationSettings(bool Enabled = false, ControlId Step = ControlId.DPadRight,
    ControlId Activate = ControlId.A, int DebounceMs = 150)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(Step) || !Enum.IsDefined(Activate) || Step == Activate || DebounceMs is < 50 or > 1000)
            throw new InvalidDataException("Choose different navigation controls and a debounce of 50–1000 ms.");
    }
}

internal enum SwitchNavigationAction { Step, Activate }
internal readonly record struct SwitchNavigationEvent(long Sequence, long TimestampMs, SwitchNavigationAction Action);
internal sealed record SwitchNavigationSnapshot(bool Enabled, long Generation, long NowMs, SwitchNavigationEvent[] Events);

/// <summary>Bounded ordered physical edges for the UI. Repeated status reads do not
/// consume events; generation/sequence numbers let the UI reject replay and gaps.</summary>
internal sealed class SwitchNavigationInput
{
    private const int Capacity = 32;
    private readonly Queue<SwitchNavigationEvent> _events = new(Capacity);
    private SwitchNavigationSettings _settings = new();
    private uint _previous, _needsRelease = uint.MaxValue;
    private long _generation, _sequence;
    private long? _lastStep, _lastActivate;
    internal bool Enabled => _settings.Enabled;

    internal void Configure(SwitchNavigationSettings settings) { settings.Validate(); _settings = settings; ResetInput(); }
    internal void Stop() => Configure(_settings with { Enabled = false });
    internal void ResetInput()
    {
        _generation++; _sequence = 0; _previous = 0; _needsRelease = uint.MaxValue;
        _lastStep = _lastActivate = null; _events.Clear();
    }

    internal void Sample(ControllerState state, PaddleButtons paddles, bool connected, long nowMs)
    {
        if (!Enabled || !connected) return;
        uint pressed = ControlMap.PressedMask(state, paddles);
        _needsRelease &= pressed;
        uint rising = pressed & ~_previous & ~_needsRelease;
        _previous = pressed;
        bool step = (rising & (uint)_settings.Step) != 0, activate = (rising & (uint)_settings.Activate) != 0;
        // Ambiguous simultaneous presses never select an unintended item.
        if (step && activate) return;
        if (step) Publish(SwitchNavigationAction.Step, nowMs, ref _lastStep);
        if (activate) Publish(SwitchNavigationAction.Activate, nowMs, ref _lastActivate);
    }

    private void Publish(SwitchNavigationAction action, long nowMs, ref long? previous)
    {
        if (previous.HasValue && ((double)nowMs - previous.Value) < _settings.DebounceMs) return;
        previous = nowMs;
        if (_events.Count == Capacity) _events.Dequeue();
        _events.Enqueue(new(++_sequence, nowMs, action));
    }

    internal SwitchNavigationSnapshot Snapshot(long nowMs) => new(Enabled, _generation, nowMs, _events.ToArray());
}
