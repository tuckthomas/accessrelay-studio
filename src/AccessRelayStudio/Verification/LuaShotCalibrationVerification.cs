using System.Collections.Immutable;
using EliteScriptStudio.Core;

internal static class LuaShotCalibrationVerification
{
    internal static void Run()
    {
        var recording = new MacroRecording(Guid.NewGuid(), "Saved paddle rhythm", RecordingChannels.RightStick,
            [new(0, Stick(-800, -1800)), new(50, Stick(-1200, -32768)), new(200, Stick(-1100, -32768)),
             new(450, Stick(-1000, -20000)), new(470, Stick(-500, 0)), new(490, Stick(0, 32767)),
             new(550, default)], 650);
        var original = RecordedLua.Convert(recording) with { Tuning = ImmutableDictionary<string, double>.Empty.Add("kept", 3) };
        True(LuaShotCalibration.IsCompatible(original), "Canonical complete Lua must be compatible.");
        var baseline = LuaShotCalibration.TrainingFor(original);
        Equal(400, baseline.HoldMs); Equal<int?>(40, baseline.TempoMs);
        Equal(original.Source, RecordedLua.Convert(baseline.Baseline).Source);
        var late = LuaShotCalibration.ApplyFeedback(original, "late", "none");
        Equal(392, late.Training!.HoldMs); Equal<int?>(40, late.Training.TempoMs);
        Equal(original.Id, late.Id); Equal(original.Name, late.Name); Equal(original.Tuning, late.Tuning);
        Equal(baseline.Baseline.Frames.Length, late.Training.Baseline.Frames.Length);
        for (int i = 0; i < recording.Frames.Length; i++) Equal(recording.Frames[i], late.Training.Baseline.Frames[i]);
        Equal(0, late.Training.RetimedRecording().Frames[0].TimeMs);
        Equal(recording.Frames[0].State, late.Training.RetimedRecording().Frames[0].State);
        var early = LuaShotCalibration.ApplyFeedback(late, "early", "none");
        Equal(original.Source, early.Source);
        var tempo = LuaShotCalibration.ApplyFeedback(late, "excellent", "fast");
        Equal(392, tempo.Training!.HoldMs); Equal<int?>(44, tempo.Training.TempoMs);
        var slight = LuaShotCalibration.ApplyFeedback(original, "slightlyLate", "slightlySlow");
        Equal(396, slight.Training!.HoldMs); Equal<int?>(38, slight.Training.TempoMs);
        Equal(late.Source, LuaShotCalibration.ApplyFeedback(late, "excellent", "none").Source);
        var phase = baseline.Phases();
        var minimum = baseline with { HoldMs = phase.Hold.End - phase.Hold.Start,
            TempoMs = phase.Tempo!.Value.End - phase.Tempo.Value.Start };
        var minimumScript = RecordedLua.Convert(minimum.RetimedRecording()) with { Training = minimum };
        var boundedMinimum = LuaShotCalibration.ApplyFeedback(minimumScript, "late", "slow");
        Equal(minimum.HoldMs, boundedMinimum.Training!.HoldMs); Equal(minimum.TempoMs, boundedMinimum.Training.TempoMs);
        var maximum = baseline with { HoldMs = 10000 };
        var maximumScript = RecordedLua.Convert(maximum.RetimedRecording()) with { Training = maximum };
        Equal(10000, LuaShotCalibration.ApplyFeedback(maximumScript, "early", "none").Training!.HoldMs);
        var established = RecordedInputTraining.Create("test-pack", "standing", "rightStick", recording with { Channels = RecordingChannels.All }, "late", "none");
        var establishedScript = established.ToScript(original.Id, original.Name);
        var establishedTuned = LuaShotCalibration.ApplyFeedback(establishedScript, "late", "none");
        Equal(RecordingChannels.RightStick, RecordedLua.Constants(establishedTuned.Source).Select(block => RecordedLua.Parse(block.Duration, block.Header, block.Data)).Single().Channels);
        Equal(established.Baseline, establishedTuned.Training!.Baseline);
        Reject(() => LuaShotCalibration.ApplyFeedback(original, "invented", "none"));
        Reject(() => LuaShotCalibration.ApplyFeedback(original, "late", "invented"));
        True(!LuaShotCalibration.IsCompatible(original with { Source = original.Source + "wait(20)\n" }), "Custom trailing Lua cannot be rewritten.");
        True(!LuaShotCalibration.IsCompatible(RecordedLua.Convert(recording with { Channels = RecordingChannels.All })), "Other channels cannot be adopted.");
        True(!LuaShotCalibration.IsCompatible(RecordedLua.Convert(recording with { Frames = recording.Frames.RemoveAt(6) })), "A missing neutral endpoint must be rejected.");
        var edited = late with { Source = late.Source + "wait(20)\n" };
        True(!LuaShotCalibration.IsCompatible(edited), "Edited trained source cannot be rewritten.");
        var profileId = Guid.NewGuid();
        var key = LuaShotCalibration.ConfigurationKey(profileId, ControlId.LargeRight, original);
        True(key != LuaShotCalibration.ConfigurationKey(profileId, ControlId.LargeRight, late), "Retiming changes the calibration key.");
        True(key != LuaShotCalibration.ConfigurationKey(Guid.NewGuid(), ControlId.LargeRight, original), "Profile identity is included.");
        True(key != LuaShotCalibration.ConfigurationKey(profileId, ControlId.SmallRight, original), "Paddle identity is included.");
        VerifyCompletions(original);
        VerifyPlayback(original, late);
        VerifyPersistence(late);
        Console.WriteLine("SUCCESS Lua shot calibration: natural paddle completions, cancellation and error rejection, immutable baseline, bounded feedback, real retimed output and persistence.");
    }

    private static void VerifyCompletions(LuaControllerScript script)
    {
        var profile = Profile(script);
        var engine = new MacroEngine(); engine.Apply(profile);
        engine.Tick(default, PaddleButtons.None, true, 0);
        engine.Tick(default, PaddleButtons.None, true, 100, PaddleButtons.LargeRight);
        True(engine.Status.IsActive && engine.LastLuaCompletion is null, "A paddle tap starts the whole Lua without a completion receipt.");
        engine.Tick(default, PaddleButtons.None, true, 749);
        True(engine.LastLuaCompletion is null, "A running script cannot be reported complete.");
        engine.Tick(default, PaddleButtons.None, true, 750);
        var first = engine.LastLuaCompletion ?? throw new Exception("Natural completion receipt missing.");
        Equal(ControlId.LargeRight, first.Control); Equal(script.Id, first.ScriptId);
        Equal(LuaShotCalibration.SourceSha256(script), first.SourceSha256);
        Equal(100L, first.StartedAtMs); Equal(750L, first.CompletedAtMs); Equal(engine.RunId, first.RunId);
        engine.Apply(profile);
        engine.Tick(default, PaddleButtons.None, true, 1000, PaddleButtons.LargeRight);
        engine.Tick(default, PaddleButtons.None, true, 1650);
        True(engine.LastLuaCompletion!.Id > first.Id, "Receipt IDs must not repeat after Apply.");
        engine.Tick(default, PaddleButtons.None, true, 1700, PaddleButtons.LargeRight);
        engine.CancelLua(ControlId.LargeRight);
        engine.Tick(default, PaddleButtons.None, true, 2400);
        True(engine.LastLuaCompletion is null && engine.ScriptsEnabled, "Cancellation cannot create a receipt or disable scripts.");
        engine.Tick(default, PaddleButtons.None, true, 2500, PaddleButtons.LargeRight);
        engine.Tick(default, PaddleButtons.None, false, 2600);
        engine.Tick(default, PaddleButtons.None, true, 3200);
        True(engine.LastLuaCompletion is null, "Disconnect cannot report completion.");
        engine.Tick(default, PaddleButtons.None, true, 3300, PaddleButtons.LargeRight);
        engine.Stop();
        engine.Tick(default, PaddleButtons.None, true, 4000);
        True(engine.LastLuaCompletion is null, "Stop cannot report completion.");
        var errorScript = script with { Source = "wait(10)\noutput.axis_raw(\"RightY\", 999999)\n" };
        engine.Apply(Profile(errorScript));
        engine.Tick(default, PaddleButtons.None, true, 4100, PaddleButtons.LargeRight);
        engine.Tick(default, PaddleButtons.None, true, 4120);
        True(engine.LastLuaCompletion is null && engine.Status.Error is not null, "A runtime Lua error cannot report completion.");
        var other = script with { Id = Guid.NewGuid(), Name = "Replacement", Source = "wait(1000)\n" };
        engine.Apply(profile.WithLuaScript(other).WithBinding(ControlId.SmallRight, new(ScriptKind.Lua, LuaScriptId: other.Id)));
        engine.Tick(default, PaddleButtons.None, true, 5000, PaddleButtons.LargeRight);
        engine.Tick(default, PaddleButtons.None, true, 5100, PaddleButtons.SmallRight);
        engine.Tick(default, PaddleButtons.None, true, 5700);
        True(engine.LastLuaCompletion is null, "An interrupted old script cannot complete behind its replacement.");
        engine.Tick(default, PaddleButtons.None, true, 6100);
        Equal(other.Id, engine.LastLuaCompletion!.ScriptId);
    }

    private static void VerifyPlayback(LuaControllerScript original, LuaControllerScript tuned)
    {
        Dictionary<int, ControllerState> Read(LuaControllerScript script)
        {
            var engine = new MacroEngine(); engine.Apply(Profile(script));
            var states = new Dictionary<int, ControllerState>();
            for (int at = 0; at <= 800; at++)
                states[at] = engine.Tick(default, PaddleButtons.None, true, at, at == 10 ? PaddleButtons.LargeRight : PaddleButtons.None);
            return states;
        }
        var before = Read(original); var after = Read(tuned);
        Equal((short)-32768, before[459].RightY); Equal((short)-20000, before[460].RightY);
        Equal((short)-32768, after[451].RightY); Equal((short)-20000, after[452].RightY);
        Equal((short)32767, before[500].RightY); Equal((short)32767, after[492].RightY);
        Equal(default(ControllerState), after[660]);
        // The entire full baseline window, including the first sub-threshold motion, survives.
        Equal(before[10], after[10]);
    }

    private static void VerifyPersistence(LuaControllerScript script)
    {
        var profile = Profile(script);
        var migrated = LuaProfileMigration.Convert(profile);
        Equal(script.Source, migrated.LuaScripts[script.Id].Source);
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.LuaShot." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory); store.Save(migrated);
            var saved = store.Load(profile.Id).LuaScripts[script.Id];
            Equal(script.Source, saved.Source); Equal(script.Training!.HoldMs, saved.Training!.HoldMs);
            Equal(script.Training.Baseline.Frames.Length, saved.Training.Baseline.Frames.Length);
            Equal(script.Source, LuaProfileMigration.Convert(store.Load(profile.Id)).LuaScripts[script.Id].Source);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static StudioProfile Profile(LuaControllerScript script) => (StudioProfile.CreateDefault() with { ScriptsEnabled = true })
        .WithLuaScript(script).WithBinding(ControlId.LargeRight, new(ScriptKind.Lua, LuaScriptId: script.Id));
    private static ControllerState Stick(short x, short y) => default(ControllerState) with { RightX = x, RightY = y };
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Reject(Action action) { try { action(); } catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException) { return; } throw new Exception("Expected invalid Lua calibration to be rejected."); }
}
