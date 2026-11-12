using EliteScriptStudio.Core;

namespace EliteScriptStudio.Packs.Nba2K27;

internal enum Nba2K27TimingFeedback { None, Early, SlightlyEarly, Excellent, SlightlyLate, Late }
internal enum Nba2K27TempoFeedback { None, Fast, SlightlyFast, Excellent, SlightlySlow, Slow }

internal readonly record struct Nba2K27Intervals(int? DownHoldMs, int? UpSweepMs);
internal readonly record struct Nba2K27Adjustment(int DownHoldDeltaMs, int UpSweepDeltaMs);

internal static class Nba2K27Tuning
{
    public static Nba2K27Adjustment Suggest(Nba2K27TimingFeedback timing, Nba2K27TempoFeedback tempo) => new(
        timing switch
        {
            Nba2K27TimingFeedback.Early => 16,
            Nba2K27TimingFeedback.SlightlyEarly => 8,
            Nba2K27TimingFeedback.SlightlyLate => -8,
            Nba2K27TimingFeedback.Late => -16,
            _ => 0
        },
        tempo switch
        {
            Nba2K27TempoFeedback.Fast => 8,
            Nba2K27TempoFeedback.SlightlyFast => 4,
            Nba2K27TempoFeedback.SlightlySlow => -4,
            Nba2K27TempoFeedback.Slow => -8,
            _ => 0
        });

    public static Nba2K27Intervals GetIntervals(MacroRecording recording)
    {
        var (downHold, upSweep) = FindShot(recording);
        return new(downHold is { } hold ? recording.IntervalDuration(hold) : null,
            upSweep is { } sweep ? recording.IntervalDuration(sweep) : null);
    }

    public static ScriptTiming Apply(ScriptTiming timing, Nba2K27Adjustment adjustment)
    {
        int hold = AdjustDuration(timing.DownHold, adjustment.DownHoldDeltaMs, 1, ScriptTiming.MaxMs);
        int sweep = AdjustDuration(timing.UpSweep, adjustment.UpSweepDeltaMs, 4, ScriptTiming.MaxMs);
        return hold == timing.DownHold && sweep == timing.UpSweep ? timing : timing with { DownHold = hold, UpSweep = sweep };
    }

    public static MacroRecording Apply(MacroRecording recording, Nba2K27Adjustment adjustment)
    {
        var (hold, sweep) = FindShot(recording);
        // Shorten first when it would free space for a longer hold near the recording limit.
        if (adjustment.DownHoldDeltaMs > 0 && adjustment.UpSweepDeltaMs < 0)
        {
            recording = Resize(recording, sweep, adjustment.UpSweepDeltaMs);
            return Resize(recording, hold, adjustment.DownHoldDeltaMs);
        }

        recording = Resize(recording, hold, adjustment.DownHoldDeltaMs);
        return Resize(recording, sweep, adjustment.UpSweepDeltaMs);
    }

    private static (RecordedInterval? DownHold, RecordedInterval? UpSweep) FindShot(MacroRecording recording)
    {
        var (_, hold, sweep, upHold) = recording.FindRightStickIntervals();
        if (upHold is not { } up || recording.Frames.Skip(up.End)
                .Any(frame => frame.State.RightY <= -MacroRecording.RightStickHoldThreshold))
            return default;
        return (hold, sweep);
    }

    private static MacroRecording Resize(MacroRecording recording, RecordedInterval? interval, int deltaMs)
    {
        if (interval is not { } range || deltaMs == 0)
            return recording;

        int duration = recording.IntervalDuration(range);
        int maximum = duration + InputRecorder.MaxDurationMs - recording.DurationMs;
        int target = AdjustDuration(duration, deltaMs, range.End - range.Start, maximum);
        return recording.WithIntervalDuration(range.Start, range.End, target);
    }

    private static int AdjustDuration(int current, int delta, int minimum, int maximum) =>
        (int)Math.Clamp((long)current + delta, minimum, maximum);
}
