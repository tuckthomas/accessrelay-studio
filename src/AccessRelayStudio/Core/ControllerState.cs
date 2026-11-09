namespace EliteScriptStudio.Core;

public readonly record struct ControllerState(
    ushort Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY);

[Flags]
public enum PaddleButtons : byte
{
    None = 0, SmallLeft = 1, SmallRight = 2, LargeLeft = 4, LargeRight = 8
}

// The lower sixteen bits are XInput's button mask. Triggers and paddles
// occupy separate bits so they can share the same edge detector.
public enum ControlId : uint
{
    DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008,
    Menu = 0x0010, View = 0x0020, LeftStick = 0x0040, RightStick = 0x0080,
    LeftBumper = 0x0100, RightBumper = 0x0200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
    LeftTrigger = 0x10000, RightTrigger = 0x20000,
    SmallLeft = 0x40000, SmallRight = 0x80000, LargeLeft = 0x100000, LargeRight = 0x200000
}

public static class ControlMap
{
    public static IReadOnlyList<ControlId> All { get; } = Array.AsReadOnly(Enum.GetValues<ControlId>());

    public static IReadOnlyList<ControlId> Paddles { get; } = Array.AsReadOnly(new[]
    {
        ControlId.SmallLeft, ControlId.SmallRight, ControlId.LargeLeft, ControlId.LargeRight
    });

    // Same-tick ties go to the upper-left paddle first, then upper-right,
    // lower-left and lower-right. Face controls follow their XInput bit order.
    internal static readonly ControlId[] TriggerOrder =
        [.. Paddles, .. All.Where(control => !Paddles.Contains(control))];

    public static bool IsPressed(ControlId control, ControllerState input, PaddleButtons paddles) =>
        (PressedMask(input, paddles) & (uint)control) != 0;

    internal static uint PressedMask(ControllerState input, PaddleButtons paddles)
    {
        const int triggerThreshold = 30;
        uint mask = (uint)input.Buttons | (((uint)paddles & 0xF) << 18);
        if (input.LeftTrigger >= triggerThreshold)
            mask |= (uint)ControlId.LeftTrigger;
        if (input.RightTrigger >= triggerThreshold)
            mask |= (uint)ControlId.RightTrigger;
        return mask;
    }

    internal static ControllerState Consume(ControllerState state, uint controls) => state with
    {
        Buttons = (ushort)((uint)state.Buttons & ~controls),
        LeftTrigger = (controls & (uint)ControlId.LeftTrigger) != 0 ? (byte)0 : state.LeftTrigger,
        RightTrigger = (controls & (uint)ControlId.RightTrigger) != 0 ? (byte)0 : state.RightTrigger
    };
}
