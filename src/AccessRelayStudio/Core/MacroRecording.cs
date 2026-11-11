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

internal readonly record struct RecordedInterval(int Start, int End);

public sealed record MacroRecording(Guid Id, string Name, RecordingChannels Channels,
    ImmutableArray<RecordedFrame> Frames, int DurationMs)
{
    public const int MaxFrames = 4096;
    public const int MaxRecordingsPerProfile = 8;
    internal const int RightStickHoldThreshold = 24576;

    internal (RecordedInterval? DownSweep, RecordedInterval? DownHold,
        RecordedInterval? UpSweep, RecordedInterval? UpHold) FindRightStickIntervals()
    {
        if (!Channels.HasFlag(RecordingChannels.RightStickY) || Frames.IsDefaultOrEmpty)
            return default;

        const int downMovement = -1600;
        int down = 0;
        while (down < Frames.Length && Frames[down].State.RightY > -RightStickHoldThreshold)
            down++;
        if (down == Frames.Length)
            return default;

        int downStart = down;
        while (downStart > 0 && Frames[downStart - 1].State.RightY < downMovement)
            downStart--;
        RecordedInterval? downSweep = downStart < down ? new(downStart, down) : null;

        int release = down + 1;
        while (release < Frames.Length && Frames[release].State.RightY <= -RightStickHoldThreshold)
            release++;
        var downHold = new RecordedInterval(down, release);

        int up = release;
        while (up < Frames.Length && Frames[up].State.RightY is > -RightStickHoldThreshold and < RightStickHoldThreshold)
            up++;
        if (up == Frames.Length || Frames[up].State.RightY < RightStickHoldThreshold)
            return (downSweep, downHold, null, null);

        RecordedInterval? upSweep = up > release ? new(release, up) : null;
        int upEnd = up + 1;
        while (upEnd < Frames.Length && Frames[upEnd].State.RightY >= RightStickHoldThreshold)
            upEnd++;
        return (downSweep, downHold, upSweep, new RecordedInterval(up, upEnd));
    }

    internal int IntervalDuration(RecordedInterval interval) =>
        (interval.End == Frames.Length ? DurationMs : Frames[interval.End].TimeMs) - Frames[interval.Start].TimeMs;

    public MacroRecording WithIntervalDuration(int startFrame, int endFrame, int durationMs)
    {
        Validate();
        if (startFrame < 0 || startFrame >= Frames.Length)
            throw new ArgumentOutOfRangeException(nameof(startFrame), "Choose an existing starting frame.");
        if (endFrame <= startFrame || endFrame > Frames.Length)
            throw new ArgumentOutOfRangeException(nameof(endFrame), "Choose a boundary after the starting frame.");
        int frameCount = endFrame - startFrame;
        if (durationMs < frameCount)
            throw new ArgumentOutOfRangeException(nameof(durationMs), $"This interval needs at least {frameCount} ms to keep every frame.");

        int start = Frames[startFrame].TimeMs;
        int end = endFrame < Frames.Length ? Frames[endFrame].TimeMs : DurationMs;
        int oldDuration = end - start;
        long total = (long)DurationMs + durationMs - oldDuration;
        if (total > InputRecorder.MaxDurationMs)
            throw new ArgumentOutOfRangeException(nameof(durationMs), $"The whole recording can last up to {InputRecorder.MaxDurationMs} ms.");
        if (durationMs == oldDuration)
            return this;

        var frames = Frames.ToBuilder();
        int previous = start;
        for (int i = startFrame + 1; i < endFrame; i++)
        {
            int offset = Frames[i].TimeMs - start;
            int scaled = (int)(((long)offset * durationMs + oldDuration / 2) / oldDuration);
            // Leave at least one millisecond for each remaining frame.
            int time = Math.Clamp(start + scaled, previous + 1, start + durationMs - (endFrame - i));
            frames[i] = Frames[i] with { TimeMs = time };
            previous = time;
        }
        int shift = durationMs - oldDuration;
        for (int i = endFrame; i < frames.Count; i++)
            frames[i] = Frames[i] with { TimeMs = Frames[i].TimeMs + shift };

        var edited = this with { Frames = frames.MoveToImmutable(), DurationMs = (int)total };
        edited.Validate();
        return edited;
    }

    public MacroRecording WithFrame(int index, int timeMs, ControllerState state)
    {
        Validate();
        if (index < 0 || index >= Frames.Length)
            throw new ArgumentOutOfRangeException(nameof(index), "Choose an existing frame.");
        if (index == 0 && timeMs != 0)
            throw new ArgumentOutOfRangeException(nameof(timeMs), "The first frame must start at 0 ms.");
        if (index > 0)
        {
            int minimum = Frames[index - 1].TimeMs + 1;
            int maximum = (index + 1 < Frames.Length ? Frames[index + 1].TimeMs : DurationMs) - 1;
            if (timeMs < minimum || timeMs > maximum)
                throw new ArgumentOutOfRangeException(nameof(timeMs), $"This frame must start between {minimum} and {maximum} ms.");
        }

        var frame = new RecordedFrame(timeMs, RecordingFilter.Overlay(Channels, default, state));
        var edited = this with { Frames = Frames.SetItem(index, frame) };
        edited.Validate();
        return edited;
    }

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
