using System.Collections.Immutable;
using System.IO;

namespace EliteScriptStudio.Core;

[Flags]
public enum RecordingChannels : uint
{
    None = 0,
    DPadUp = (uint)ControlId.DPadUp, DPadDown = (uint)ControlId.DPadDown,
    DPadLeft = (uint)ControlId.DPadLeft, DPadRight = (uint)ControlId.DPadRight,
    Menu = (uint)ControlId.Menu, View = (uint)ControlId.View,
    LeftStickClick = (uint)ControlId.LeftStick, RightStickClick = (uint)ControlId.RightStick,
    LeftBumper = (uint)ControlId.LeftBumper, RightBumper = (uint)ControlId.RightBumper,
    A = (uint)ControlId.A, B = (uint)ControlId.B, X = (uint)ControlId.X, Y = (uint)ControlId.Y,
    LeftTrigger = (uint)ControlId.LeftTrigger, RightTrigger = (uint)ControlId.RightTrigger,
    LeftStickX = 0x40000, RightStickX = 0x80000, LeftStickY = 0x100000, RightStickY = 0x200000,
    LeftStick = LeftStickX | LeftStickY,
    RightStick = RightStickX | RightStickY,
    Buttons = DPadUp | DPadDown | DPadLeft | DPadRight | Menu | View | LeftStickClick | RightStickClick
        | LeftBumper | RightBumper | A | B | X | Y,
    All = Buttons | LeftTrigger | RightTrigger | LeftStick | RightStick
}

public readonly record struct RecordedFrame(int TimeMs, ControllerState State);

public sealed record MacroRecording(Guid Id, string Name, RecordingChannels Channels,
    ImmutableArray<RecordedFrame> Frames, int DurationMs)
{
    public const int MaxFrames = 4096;
    public const int MaxRecordingsPerProfile = 8;

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl))
            throw new InvalidDataException("The recording needs a name of up to 80 characters and an ID.");
        if (!RecordingFilter.IsValid(Channels))
            throw new InvalidDataException("Select at least one recording channel.");
        if (DurationMs is < 1 or > InputRecorder.MaxDurationMs || Frames.IsDefaultOrEmpty
            || Frames.Length > MaxFrames || Frames[0].TimeMs != 0)
            throw new InvalidDataException("The recording has an invalid duration or frame count.");

        int previousTime = -1;
        foreach (var frame in Frames)
        {
            if (frame.TimeMs <= previousTime || frame.TimeMs >= DurationMs
                || RecordingFilter.Overlay(Channels, default, frame.State) != frame.State)
                throw new InvalidDataException("The recording contains an invalid frame.");
            previousTime = frame.TimeMs;
        }
    }

    internal int FrameIndexAt(int elapsedMs)
    {
        // The first frame is always at 0 ms. Find the latest change at or before this tick.
        int first = 0;
        int last = Frames.Length;
        while (last - first > 1)
        {
            int middle = first + (last - first) / 2;
            if (Frames[middle].TimeMs <= elapsedMs)
                first = middle;
            else
                last = middle;
        }
        return first;
    }
}

internal static class RecordingFilter
{
    public static bool IsValid(RecordingChannels channels) =>
        channels != RecordingChannels.None && (channels & ~RecordingChannels.All) == 0;

    public static ControllerState Overlay(RecordingChannels channels, ControllerState input, ControllerState frame)
    {
        ushort buttons = (ushort)(channels & RecordingChannels.Buttons);
        return new ControllerState(
            (ushort)((input.Buttons & ~buttons) | (frame.Buttons & buttons)),
            channels.HasFlag(RecordingChannels.LeftTrigger) ? frame.LeftTrigger : input.LeftTrigger,
            channels.HasFlag(RecordingChannels.RightTrigger) ? frame.RightTrigger : input.RightTrigger,
            channels.HasFlag(RecordingChannels.LeftStickX) ? frame.LeftX : input.LeftX,
            channels.HasFlag(RecordingChannels.LeftStickY) ? frame.LeftY : input.LeftY,
            channels.HasFlag(RecordingChannels.RightStickX) ? frame.RightX : input.RightX,
            channels.HasFlag(RecordingChannels.RightStickY) ? frame.RightY : input.RightY);
    }
}
