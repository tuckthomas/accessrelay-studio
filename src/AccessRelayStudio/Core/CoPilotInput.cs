using System.Collections.Immutable;

namespace EliteScriptStudio.Core;

public enum CoPilotAxisOwner { Primary, Companion, Strongest }
public enum CoPilotButtonOwner { Shared, Primary, Companion }
internal sealed record ControllerInputComposition(CoPilotSettings Settings, bool PrimaryConnected, bool CompanionConnected);

public sealed record CoPilotSettings
{
    public bool Enabled { get; init; }
    public string PrimaryDeviceId { get; init; } = "";
    public string CompanionDeviceId { get; init; } = "";
    public CoPilotAxisOwner LeftStick { get; init; } = CoPilotAxisOwner.Primary;
    public CoPilotAxisOwner RightStick { get; init; } = CoPilotAxisOwner.Companion;
    public CoPilotAxisOwner Triggers { get; init; } = CoPilotAxisOwner.Strongest;
    public ImmutableDictionary<ControlId, CoPilotButtonOwner> Buttons { get; init; } = ImmutableDictionary<ControlId, CoPilotButtonOwner>.Empty;

    public void Validate()
    {
        if (PrimaryDeviceId is null || CompanionDeviceId is null || PrimaryDeviceId.Length > 1024 || CompanionDeviceId.Length > 1024
            || PrimaryDeviceId.Any(char.IsControl) || CompanionDeviceId.Any(char.IsControl)
            || (Enabled && (PrimaryDeviceId.Length == 0 || CompanionDeviceId.Length == 0 || PrimaryDeviceId == CompanionDeviceId))
            || !Enum.IsDefined(LeftStick) || !Enum.IsDefined(RightStick) || !Enum.IsDefined(Triggers)
            || Buttons is null || Buttons.Count > 14 || Buttons.Any(pair => !Enum.IsDefined(pair.Key)
                || (uint)pair.Key > ushort.MaxValue || !Enum.IsDefined(pair.Value)))
            throw new InvalidDataException("Choose two different physical controllers and valid co-pilot control owners.");
    }
}

internal static class CoPilotInput
{
    internal static ControllerState Merge(ControllerState primary, bool primaryConnected, ControllerState companion,
        bool companionConnected, CoPilotSettings settings)
    {
        if (!primaryConnected) primary = default;
        if (!settings.Enabled) return primary;
        if (!companionConnected) companion = default;
        ushort buttons = (ushort)(primary.Buttons | companion.Buttons);
        foreach (var (control, owner) in settings.Buttons)
        {
            ushort mask = (ushort)control;
            if (owner == CoPilotButtonOwner.Primary) buttons = (ushort)((buttons & ~mask) | (primary.Buttons & mask));
            else if (owner == CoPilotButtonOwner.Companion) buttons = (ushort)((buttons & ~mask) | (companion.Buttons & mask));
        }
        // Conflicting D-pad directions cancel; axis pairs always come from one
        // participant, never an accidental diagonal assembled from separate pads.
        if ((buttons & 3) == 3) buttons &= unchecked((ushort)~3);
        if ((buttons & 12) == 12) buttons &= unchecked((ushort)~12);
        var left = Stick(primary.LeftX, primary.LeftY, companion.LeftX, companion.LeftY, settings.LeftStick);
        var right = Stick(primary.RightX, primary.RightY, companion.RightX, companion.RightY, settings.RightStick);
        return new(buttons, Trigger(primary.LeftTrigger, companion.LeftTrigger, settings.Triggers),
            Trigger(primary.RightTrigger, companion.RightTrigger, settings.Triggers), left.X, left.Y, right.X, right.Y);
    }
    private static (short X, short Y) Stick(short px, short py, short cx, short cy, CoPilotAxisOwner owner) =>
        owner == CoPilotAxisOwner.Primary || (owner == CoPilotAxisOwner.Strongest && (long)px * px + (long)py * py >= (long)cx * cx + (long)cy * cy)
            ? (px, py) : (cx, cy);
    private static byte Trigger(byte primary, byte companion, CoPilotAxisOwner owner) => owner switch
    { CoPilotAxisOwner.Primary => primary, CoPilotAxisOwner.Companion => companion, _ => Math.Max(primary, companion) };
}
