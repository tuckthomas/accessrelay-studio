using System.IO;

namespace EliteScriptStudio.Core;

/// <summary>Immutable physical evidence and editable phase durations for a generated Lua replay.</summary>
public sealed record RecordedInputTraining(string PackId, string ShotId, string ShotInput, MacroRecording Baseline,
    int HoldMs, int? TempoMs, string BaselineTimingFeedback, string BaselineTempoFeedback,
    string TimingFeedback, string TempoFeedback)
{
    public static RecordedInputTraining Create(string packId, string shotId, string input, MacroRecording recording,
        string timing, string tempo)
    {
        var draft = new RecordedInputTraining(packId, shotId, input, recording, 1, null, timing, tempo, timing, tempo);
        var phases = draft.Phases();
        var result = draft with { HoldMs = recording.IntervalDuration(phases.Hold),
            TempoMs = phases.Tempo is { } sweep ? recording.IntervalDuration(sweep) : null };
        result.Validate(); return result;
    }

    public (RecordedInterval Hold, RecordedInterval? Tempo) Phases()
    {
        if (Baseline is null) throw new InvalidDataException("The original physical baseline is missing.");
        Baseline.Validate();
        if (ShotInput == "rightStick")
        {
            var phases = Baseline.FindRightStickIntervals();
            if (phases.DownHold is not { } hold || hold.End >= Baseline.Frames.Length)
                throw new InvalidDataException("Record a full right-stick down hold, then release or move up and return to center.");
            if (Math.Abs((int)Baseline.Frames[^1].State.RightY) > 1600 || Math.Abs((int)Baseline.Frames[^1].State.RightX) > 1600
                || Baseline.Frames.Skip(hold.End).Any(frame => frame.State.RightY <= -MacroRecording.RightStickHoldThreshold))
                throw new InvalidDataException("Record one shot per baseline and return the right stick to center before stopping.");
            return (hold, phases.UpSweep);
        }
        if (!Enum.TryParse<ControlId>(ShotInput, true, out var button) || button is not (ControlId.A or ControlId.B or ControlId.X or ControlId.Y))
            throw new InvalidDataException("Choose the right stick or a face button as the recorded shot input.");
        if (!Baseline.Channels.HasFlag((RecordingChannels)(uint)button))
            throw new InvalidDataException("The shot button was not included in the recording.");
        bool Down(RecordedFrame frame) => (frame.State.Buttons & (ushort)button) != 0;
        int start = 0;
        while (start < Baseline.Frames.Length && !Down(Baseline.Frames[start])) start++;
        int end = start + 1;
        while (end < Baseline.Frames.Length && Down(Baseline.Frames[end])) end++;
        if (end >= Baseline.Frames.Length || Baseline.Frames.Skip(end).Any(Down))
            throw new InvalidDataException("Record exactly one shot-button press and release before stopping.");
        return (new(start, end), null);
    }

    public void Validate()
    {
        if (!GamePackManifest.Identifier(PackId, 60) || !GamePackManifest.Identifier(ShotId, 40)
            || new[] { BaselineTimingFeedback, BaselineTempoFeedback, TimingFeedback, TempoFeedback }.Any(value => !GamePackManifest.Identifier(value, 40)))
            throw new InvalidDataException("The recorded trainer identity or feedback is invalid.");
        var phases = Phases();
        if (HoldMs < phases.Hold.End - phases.Hold.Start || HoldMs > 10000
            || (phases.Tempo is { } tempo ? TempoMs is not int duration || duration < tempo.End - tempo.Start || duration > 10000 : TempoMs is not null))
            throw new InvalidDataException("Choose whole milliseconds within the phase limits; each physical sample needs at least 1 ms.");
        _ = RetimedRecording();
    }

    public MacroRecording RetimedRecording()
    {
        var phases = Phases();
        var recording = Baseline;
        // Edit later intervals first so the original frame boundaries remain stable.
        if (phases.Tempo is { } tempo && TempoMs is int duration)
            recording = recording.WithIntervalDuration(tempo.Start, tempo.End, duration);
        return recording.WithIntervalDuration(phases.Hold.Start, phases.Hold.End, HoldMs);
    }

    public LuaControllerScript ToScript(Guid id, string name)
    {
        Validate();
        return RecordedLua.Convert(PlaybackRecording() with { Id = id, Name = name }) with { Training = this };
    }

    public static RecordingChannels ChannelsFor(string input) => input switch
    {
        "rightStick" => RecordingChannels.RightStick,
        "a" => RecordingChannels.A, "b" => RecordingChannels.B,
        "x" => RecordingChannels.X, "y" => RecordingChannels.Y,
        _ => throw new InvalidDataException("Choose the right stick or a face button as the recorded shot input.")
    };

    /// <summary>Play only the selected shot input, from shot onset through its neutral release.
    /// Keep the complete original evidence and tuning intervals separate from this playback window.</summary>
    public MacroRecording PlaybackRecording()
    {
        var recording = RetimedRecording();
        var channels = ChannelsFor(ShotInput);
        var (hold, _) = Phases();
        int start = hold.Start, releaseSearch = hold.End;
        if (ShotInput == "rightStick")
        {
            var phases = recording.FindRightStickIntervals();
            start = phases.DownSweep?.Start ?? hold.Start;
            releaseSearch = phases.UpHold?.End ?? hold.End;
        }
        bool Neutral(ControllerState state) => ShotInput == "rightStick"
            ? Math.Abs((int)state.RightX) <= 1600 && Math.Abs((int)state.RightY) <= 1600
            : (state.Buttons & (ushort)channels) == 0;
        int release = releaseSearch;
        while (release < recording.Frames.Length && !Neutral(recording.Frames[release].State)) release++;
        if (release >= recording.Frames.Length) throw new InvalidDataException("The shot input must return to neutral before stopping.");
        int origin = recording.Frames[start].TimeMs;
        var frames = new List<RecordedFrame>();
        for (int i = start; i <= release; i++)
        {
            var frame = recording.Frames[i];
            var state = i == release ? default : RecordingFilter.Overlay(channels, default, frame.State);
            if (frames.Count == 0 || frames[^1].State != state) frames.Add(new(frame.TimeMs - origin, state));
        }
        // Send an explicit neutral sample, then relinquish the shot channels. No trailing waiting-room input.
        var playback = recording with { Channels = channels, Frames = [.. frames],
            DurationMs = Math.Min(InputRecorder.MaxDurationMs, recording.Frames[release].TimeMs - origin + 8) };
        playback.Validate(); return playback;
    }

    internal bool MatchesGeneratedSource(string source) => RecordedLua.Convert(PlaybackRecording()).Source == source
        || RecordedLua.Convert(RetimedRecording()).Source == source;

    public object Summary(string source)
    {
        var phases = Phases();
        return new { PackId, ShotId, ShotInput, HoldMs, TempoMs, BaselineTimingFeedback, BaselineTempoFeedback, TimingFeedback, TempoFeedback,
            baselineDurationMs = Baseline.DurationMs, baselineFrames = Baseline.Frames.Length,
            minimumHoldMs = phases.Hold.End - phases.Hold.Start,
            minimumTempoMs = phases.Tempo is { } tempo ? tempo.End - tempo.Start : (int?)null,
            durationMs = PlaybackRecording().DurationMs,
            sourceMatches = MatchesGeneratedSource(source) };
    }
}
