namespace EliteScriptStudio.Core;

/// <summary>Manual press-to-toggle intent, composed before macros and vision.
/// This never writes output or generates physical presses for automation.</summary>
internal sealed class StickyButtons
{
    private uint _mask, _latched, _previous, _needsRelease;

    internal static bool Supports(ControlId control) => Enum.IsDefined(control) && (uint)control <= (uint)ControlId.RightTrigger;

    internal void Configure(IEnumerable<ControlId> controls)
    {
        uint mask = 0;
        foreach (var control in controls)
        {
            if (!Supports(control)) throw new InvalidDataException("Sticky controls must be gamepad buttons or triggers.");
            mask |= (uint)control;
        }
        _mask = mask;
        Reset();
    }

    internal void Reset() { _latched = _previous = 0; _needsRelease = _mask; }

    internal ControllerState Sample(ControllerState physical, bool active)
    {
        if (!active) { Reset(); return physical; }
        uint pressed = ControlMap.PressedMask(physical, PaddleButtons.None) & _mask;
        _needsRelease &= pressed;
        _latched ^= pressed & ~_previous & ~_needsRelease;
        _previous = pressed;
        return physical with
        {
            Buttons = (ushort)((physical.Buttons & ~_mask) | (_latched & ushort.MaxValue)),
            LeftTrigger = (_mask & (uint)ControlId.LeftTrigger) == 0 ? physical.LeftTrigger
                : (_latched & (uint)ControlId.LeftTrigger) != 0 ? byte.MaxValue : (byte)0,
            RightTrigger = (_mask & (uint)ControlId.RightTrigger) == 0 ? physical.RightTrigger
                : (_latched & (uint)ControlId.RightTrigger) != 0 ? byte.MaxValue : (byte)0
        };
    }
}
