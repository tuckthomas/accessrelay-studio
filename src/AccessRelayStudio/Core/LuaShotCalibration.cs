using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EliteScriptStudio.Core;

/// <summary>Adopts a saved replay as its own immutable calibration baseline; never infers new motion.</summary>
public static class LuaShotCalibration
{
    public const string AdoptedPackId = "live-lua";

    public static string SourceSha256(LuaControllerScript script) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script.Source)));

    public static string ConfigurationKey(Guid profileId, ControlId control, LuaControllerScript script) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{profileId:N}:{control}:{script.Id:N}:{SourceSha256(script)}")));

    public static bool IsCompatible(LuaControllerScript script)
    {
        try { _ = TrainingFor(script); return true; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException) { return false; }
    }

    public static RecordedInputTraining TrainingFor(LuaControllerScript script)
    {
        script.Validate();
        if (script.Training is { } training)
        {
            if (training.ShotInput != "rightStick" || !training.MatchesGeneratedSource(script.Source))
                throw new InvalidDataException("This Lua has custom edits. Select a saved complete right-stick replay to calibrate.");
            var full = training.RetimedRecording();
            RequireRightStickShot(script.Source == RecordedLua.Convert(full).Source ? full : training.PlaybackRecording());
            return training;
        }
        var blocks = RecordedLua.Constants(script.Source).Take(2).ToArray();
        if (blocks.Length != 1)
            throw new InvalidDataException("Live Lua calibration requires one complete recorded right-stick replay.");
        var block = blocks[0];
        var recording = RecordedLua.Parse(block.Duration, block.Header, block.Data) with { Id = script.Id, Name = script.Name };
        // Parsing a literal buried in custom logic is insufficient: retain only the exact
        // canonical recorded script so feedback can never overwrite executable custom code.
        if (RecordedLua.Convert(recording).Source != script.Source)
            throw new InvalidDataException("This Lua has custom logic. Select an unedited recorded right-stick replay to calibrate.");
        RequireRightStickShot(recording);
        return RecordedInputTraining.Create(AdoptedPackId, "rhythm", "rightStick", recording, "none", "none");
    }

    public static LuaControllerScript ApplyFeedback(LuaControllerScript script, string timing, string tempo)
    {
        int holdAdjustment = timing switch
        {
            "early" => 8, "slightlyEarly" => 4, "late" => -8, "slightlyLate" => -4,
            "excellent" or "none" => 0,
            _ => throw new InvalidDataException("Choose the timing feedback reported by the game.")
        };
        int tempoAdjustment = tempo switch
        {
            "fast" => 4, "slightlyFast" => 2, "slow" => -4, "slightlySlow" => -2,
            "excellent" or "none" => 0,
            _ => throw new InvalidDataException("Choose the tempo feedback reported by the game.")
        };
        var training = TrainingFor(script);
        var phases = training.Phases();
        var tuned = training with
        {
            HoldMs = Math.Clamp(training.HoldMs + holdAdjustment, phases.Hold.End - phases.Hold.Start, 10000),
            TempoMs = training.TempoMs is int duration && phases.Tempo is { } sweep
                ? Math.Clamp(duration + tempoAdjustment, sweep.End - sweep.Start, 10000) : null,
            TimingFeedback = StoredFeedback(timing), TempoFeedback = StoredFeedback(tempo)
        };
        tuned.Validate();
        // Preserve the exact full replay window for newly adopted scripts. Existing trainer
        // scripts retain their established selected-input playback window and full baseline.
        bool fullReplay = script.Source == RecordedLua.Convert(training.RetimedRecording()).Source;
        var updated = fullReplay || training.PackId == AdoptedPackId
            ? RecordedLua.Convert(tuned.RetimedRecording() with { Id = script.Id, Name = script.Name }) with { Training = tuned }
            : tuned.ToScript(script.Id, script.Name);
        return updated with { Tuning = script.Tuning };
    }

    // Recorded trainer metadata uses package identifiers (lowercase); Live UI uses
    // camelCase feedback IDs. Keep those established contracts separate.
    private static string StoredFeedback(string value) => value switch
    {
        "slightlyEarly" => "slightly-early", "slightlyLate" => "slightly-late",
        "slightlyFast" => "slightly-fast", "slightlySlow" => "slightly-slow", _ => value
    };

    private static void RequireRightStickShot(MacroRecording recording)
    {
        if (recording.Channels != RecordingChannels.RightStick)
            throw new InvalidDataException("Live Lua calibration accepts right-stick-only recordings.");
        var phases = recording.FindRightStickIntervals();
        if (phases.DownHold is not { } hold || phases.UpSweep is null || phases.UpHold is null
            || !recording.Frames.Skip(hold.End).Any(frame => frame.State.RightY >= MacroRecording.RightStickHoldThreshold)
            || Math.Abs((int)recording.Frames[^1].State.RightY) > 1600 || Math.Abs((int)recording.Frames[^1].State.RightX) > 1600)
            throw new InvalidDataException("Select a complete down-up rhythm replay that ends at neutral.");
        // Also rejects a second down hold after the release and impossible phase durations.
        _ = RecordedInputTraining.Create(AdoptedPackId, "rhythm", "rightStick", recording, "none", "none");
    }
}
