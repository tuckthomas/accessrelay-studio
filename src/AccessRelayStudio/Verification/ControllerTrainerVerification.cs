using System.Collections.Immutable;
using System.Text.Json;
using EliteScriptStudio.Core;

internal static class ControllerTrainerVerification
{
    internal static void WriteFixture(string root, string manifestPath)
    {
        var pack = new GamePackLibrary(Path.Combine(root, "GamePacks")).Import(manifestPath, CancellationToken.None);
        var definition = pack.Manifest.ControllerTrainer ?? throw new Exception("The package does not declare a trainer.");
        var profile = pack.CreateProfile(ImmutableDictionary<string, double>.Empty);
        ControlId[] controls = [ControlId.X, ControlId.Y, ControlId.B];
        int index = 0;
        foreach (var shot in definition.Shots)
        {
            var baseline = new MacroRecording(Guid.NewGuid(), "Synthetic verification attempt", RecordingChannels.All,
                [new(0, new(0, 0, 0, 0, 0, 0, -32767)), new(400, new(0, 0, 0, 0, 0, 0, -18000)),
                 new(420, default), new(440, new(0, 0, 0, 0, 0, 0, 32767)), new(520, default)], 640);
            var training = RecordedInputTraining.Create(pack.Manifest.Id, shot.Id, "rightStick", baseline, "late", "fast");
            var script = training.ToScript(Guid.NewGuid(), shot.Label);
            profile = profile.WithLuaScript(script).WithBinding(controls[index++], new(ScriptKind.Lua, Consume: true, LuaScriptId: script.Id));
        }
        var store = new ProfileStore(Path.Combine(root, "ControllerProfiles")); store.Save(profile); store.SaveSelectedId(profile.Id);
        Console.WriteLine("SUCCESS: wrote isolated synthetic controller trainer fixtures to " + root);
    }

    internal static void Run()
    {
        var baseline = new MacroRecording(Guid.NewGuid(), "Physical attempt", RecordingChannels.All,
        [new(0, new(0, 0, 255, 12000, 0, 0, 0)), new(100, new(0, 0, 255, 12000, 0, 0, -32767)),
         new(500, new(0, 0, 255, 12000, 0, 0, -18000)), new(520, new(0, 0, 0, 9000, 0, 0, 0)),
         new(540, new(0, 0, 0, 9000, 0, 0, 32767)), new(620, default)], 740);
        var profile = StudioProfile.CreateDefault();
        foreach (string shot in new[] { "standing-still", "off-dribble", "fade-away" })
        {
            var training = RecordedInputTraining.Create("test-game", shot, "rightStick", baseline, "late", "fast");
            Equal(400, training.HoldMs); Equal<int?>(40, training.TempoMs);
            var original = training.ToScript(Guid.NewGuid(), shot);
            Equal(RecordedLua.Convert(training.PlaybackRecording()).Source, original.Source);
            Equal(RecordingChannels.RightStick, training.PlaybackRecording().Channels);
            Equal((short)-32767, training.PlaybackRecording().Frames[0].State.RightY);
            OneTapPlayback(original);
            var tuned = (training with { HoldMs = 384, TempoMs = 48, TimingFeedback = "excellent", TempoFeedback = "slow" }).ToScript(original.Id, shot);
            var recording = tuned.Training!.RetimedRecording();
            Equal(732, recording.DurationMs);
            Equal(484, recording.Frames[2].TimeMs);
            Equal(508, recording.Frames[3].TimeMs);
            Equal(532, recording.Frames[4].TimeMs);
            Equal(612, recording.Frames[5].TimeMs);
            Equal(baseline, tuned.Training.Baseline);
            Equal("late", tuned.Training.BaselineTimingFeedback);
            for (int i = 0; i < baseline.Frames.Length; i++) Equal(baseline.Frames[i].State, recording.Frames[i].State);
            Replay(tuned);
            profile = profile.WithLuaScript(tuned);
        }
        var first = profile.LuaScripts.Values.First();
        var next = first.Training! with { HoldMs = 600 };
        var updated = profile.WithLuaScript(next.ToScript(first.Id, first.Name));
        foreach (var other in profile.LuaScripts.Values.Where(script => script.Id != first.Id)) Equal(other, updated.LuaScripts[other.Id]);

        var buttonCapture = new MacroRecording(Guid.NewGuid(), "Button attempt", RecordingChannels.All,
            [new(0, new(0, 0, 0, 7000, 0, 0, 0)), new(50, new((ushort)ControlId.X, 0, 0, 7000, 0, 0, 0)), new(450, default)], 570);
        var button = RecordedInputTraining.Create("test-game", "button-shot", "x", buttonCapture, "early", "none");
        Equal(400, button.HoldMs); Equal<int?>(null, button.TempoMs);
        var shortened = button with { HoldMs = 200 };
        Equal(250, shortened.RetimedRecording().Frames[2].TimeMs);
        var buttonScript = shortened.ToScript(Guid.NewGuid(), "Button");
        Replay(buttonScript); OneTapPlayback(buttonScript);
        Reject(() => (button with { HoldMs = 0 }).Validate());
        Reject(() => (button with { TempoMs = 20 }).Validate());
        Reject(() => RecordedInputTraining.Create("test-game", "shot", "x", buttonCapture with { Frames = buttonCapture.Frames.RemoveAt(2) }, "late", "none"));
        Reject(() => RecordedInputTraining.Create("test-game", "shot", "rightStick", baseline with { Frames = baseline.Frames.RemoveAt(5) }, "late", "fast"));
        Reject(() => RecordedInputTraining.Create("test-game", "shot", "x", buttonCapture with { Frames = buttonCapture.Frames.Add(new(500, new((ushort)ControlId.X, 0, 0, 0, 0, 0, 0))) }, "early", "none"));
        Reject(() => (next with { HoldMs = 10001 }).Validate());
        Reject(() => (next with { TempoMs = 1 }).Validate());
        var tooLong = baseline with { DurationMs = InputRecorder.MaxDurationMs };
        var full = RecordedInputTraining.Create("test-game", "shot", "rightStick", tooLong, "late", "fast");
        Reject(() => (full with { HoldMs = 401 }).Validate());

        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.ControllerTrainer." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory); store.Save(profile);
            var saved = store.Load(profile.Id);
            foreach (var script in profile.LuaScripts.Values)
            {
                var restored = saved.LuaScripts[script.Id];
                Equal(script.Source, restored.Source);
                Equal(script.Training!.Baseline.Frames.Length, restored.Training!.Baseline.Frames.Length);
                Equal(script.Training.HoldMs, restored.Training.HoldMs);
                Replay(restored);
            }
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, profile.Id.ToString("N") + ".json")));
            Equal(7, document.RootElement.GetProperty("version").GetInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        MigrationAndIdleTrimming();
        Console.WriteLine("SUCCESS Controller trainer: isolated immediate shot playback, one-tap paddle release, completion-edge retriggers, independent shots, retiming, baseline migration and persistence.");
    }

    private static void Replay(LuaControllerScript script)
    {
        var recording = script.Training!.PlaybackRecording();
        var program = new LuaControllerProgram(script); program.Start(ControlId.LargeRight, 100);
        for (int t = 0; t < recording.DurationMs; t++)
        {
            var output = program.Tick(default, default, default, 100 + t, out string? error);
            Equal<string?>(null, error);
            Equal(recording.Frames[recording.FrameIndexAt(t)].State, output);
        }
        Equal(default, program.Tick(default, default, default, 100 + recording.DurationMs, out _));
        Equal(false, program.IsRunning);
    }

    private static void OneTapPlayback(LuaControllerScript script)
    {
        var recording = script.Training!.PlaybackRecording();
        var profile = (StudioProfile.CreateDefault() with { ScriptsEnabled = true }).WithLuaScript(script)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Lua, Consume: true, LuaScriptId: script.Id));
        var engine = new MacroEngine(); engine.Apply(profile);
        var manual = new ControllerState((ushort)ControlId.A, 11, 234, 17000, -21000, 4600, -9000);
        long start = 10;
        for (int repetition = 0; repetition < 5; repetition++)
        {
            // A raw press/release entirely between polls is still a one-tap trigger.
            // Subsequent presses arrive on the old replay's completion tick, without a gap.
            for (int t = 0; t < recording.DurationMs; t++)
            {
                var actual = engine.Tick(manual, PaddleButtons.None, true, start + t,
                    t == 0 ? PaddleButtons.LargeRight : PaddleButtons.None);
                var expected = RecordingFilter.Overlay(recording.Channels, manual, recording.Frames[recording.FrameIndexAt(t)].State);
                Equal(expected, actual);
                Equal<string?>(null, engine.Status.Error);
            }
            start += recording.DurationMs;
        }
        Equal(manual, engine.Tick(manual, PaddleButtons.None, true, start));
        Equal(false, engine.Status.IsActive);
        // The normal held-level input also starts with one press, then continues after release.
        engine.Apply(profile); engine.Tick(manual, default, true, ++start);
        for (int t = 0; t < recording.DurationMs; t++)
        {
            var actual = engine.Tick(manual, t == 0 ? PaddleButtons.LargeRight : PaddleButtons.None, true, start + 1 + t);
            Equal(RecordingFilter.Overlay(recording.Channels, manual, recording.Frames[recording.FrameIndexAt(t)].State), actual);
        }
        // A settings apply keeps the enabled session and accepts a fresh raw paddle tap.
        var edited = script.Training.ToScript(script.Id, script.Name);
        engine.Apply(profile.WithLuaScript(edited));
        Equal(true, engine.ScriptsEnabled);
        Equal(RecordingFilter.Overlay(recording.Channels, manual, recording.Frames[0].State),
            engine.Tick(manual, default, true, start + recording.DurationMs + 2, PaddleButtons.LargeRight));
    }

    private static void MigrationAndIdleTrimming()
    {
        var baseline = new MacroRecording(Guid.NewGuid(), "Drifting idle capture", RecordingChannels.All,
            [new(0, new((ushort)ControlId.B, 0, 255, -2933, -888, 54, -101)),
             new(3291, new(0, 0, 255, -2933, -888, -365, -1700)),
             new(3400, new(0, 0, 255, -2933, -888, 0, -32767)),
             new(4200, new(0, 0, 255, -2933, -888, 0, -18000)),
             new(4230, new(0, 0, 255, -2933, -888, 0, 0)),
             new(4260, new(0, 0, 255, -2933, -888, 0, 32767)),
             new(4380, new(0, 0, 255, -2933, -888, 150, 500)),
             new(8000, new(0, 0, 255, -2775, -888, 175, -1256))], 9104);
        var training = RecordedInputTraining.Create("test-game", "standing-still", "rightStick", baseline, "late", "fast") with { HoldMs = 818, TempoMs = 60 };
        var oldScript = RecordedLua.Convert(training.RetimedRecording()) with { Training = training };
        var profile = StudioProfile.CreateDefault().WithLuaScript(oldScript)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Lua, Consume: true, LuaScriptId: oldScript.Id));
        var repaired = LuaProfileMigration.Convert(profile);
        var script = repaired.LuaScripts[oldScript.Id];
        Equal(818, script.Training!.HoldMs); Equal<int?>(60, script.Training.TempoMs);
        Equal(baseline, script.Training.Baseline);
        Equal(profile.Bindings, repaired.Bindings);
        Equal(1115, script.Training.PlaybackRecording().DurationMs);
        Equal(RecordingChannels.RightStick, script.Training.PlaybackRecording().Channels);
        Equal(default, script.Training.PlaybackRecording().Frames[^1].State);
        Equal(true, ReferenceEquals(repaired, LuaProfileMigration.Convert(repaired)));
        var custom = oldScript with { Source = oldScript.Source + "\n-- custom behavior\n" };
        Equal(custom, LuaProfileMigration.Convert(profile.WithLuaScript(custom)).LuaScripts[custom.Id]);
        OneTapPlayback(script);
    }

    internal static void VerifySavedProfile(string profilePath)
    {
        var original = new ProfileStore(Path.GetDirectoryName(profilePath)).Load(Guid.Parse(Path.GetFileNameWithoutExtension(profilePath)));
        var repaired = LuaProfileMigration.Convert(original);
        foreach (var script in repaired.LuaScripts.Values.Where(script => script.Training is not null && script.Training.MatchesGeneratedSource(script.Source)))
        {
            OneTapPlayback(script); Replay(script);
            var playback = script.Training!.PlaybackRecording();
            Console.WriteLine($"SUCCESS {script.Name}: {script.Training.Baseline.DurationMs} ms original capture -> {playback.DurationMs} ms {playback.Channels} playback; hold {script.Training.HoldMs} ms / tempo {script.Training.TempoMs} ms; original baseline and paddle mapping retained. Five consecutive one-tap replays and post-apply tap passed; no hardware output sent.");
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException) { return; }
        throw new Exception("Invalid training attempt or timing was accepted.");
    }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
}
