using System.Collections.Immutable;
using System.Text.Json;
using EliteScriptStudio.Core;

internal static class LuaConversionVerification
{
    internal static void Run(string? profileDirectory = null)
    {
        var states = new[] { new ControllerState((ushort)(ControlId.A | ControlId.B), 0, 255, short.MinValue, 17, -12345, short.MaxValue),
            new ControllerState((ushort)ControlId.X, 117, 3, 41, -809, short.MinValue, 0), default };
        foreach (var channels in new[] { RecordingChannels.All, RecordingChannels.RightStickY, RecordingChannels.A | RecordingChannels.LeftTrigger })
        {
            var frames = states.Select((state, i) => new RecordedFrame(new[] { 0, 13, 47 }[i], RecordingFilter.Overlay(channels, default, state))).ToImmutableArray();
            var recording = new MacroRecording(Guid.NewGuid(), "Captured input", channels, frames, 99);
            VerifyRecording(recording);
        }
        // Maximum-size capture stays ordinary bounded Lua and does not exhaust the
        // interpreter instruction budget when triggered or when a poll arrives late.
        var longest = new MacroRecording(Guid.NewGuid(), "Full capture", RecordingChannels.All,
            Enumerable.Range(0, MacroRecording.MaxFrames).Select(i => new RecordedFrame(i, states[i % states.Length])).ToImmutableArray(), MacroRecording.MaxFrames);
        var longestScript = RecordedLua.Convert(longest);
        if (longestScript.Source.Length > LuaControllerScript.MaxSourceLength) throw new Exception("Capture did not fit Lua source bounds.");
        VerifyRecording(longest, everyMillisecond: false);

        foreach (var kind in new[] { ScriptKind.Rhythm, ScriptKind.IntentionalMiss, ScriptKind.Moving, ScriptKind.Stepback })
        {
            var legacy = Legacy() with { Bindings = ImmutableDictionary<ControlId, ScriptBinding>.Empty.Add(ControlId.A, new(kind, Consume: true)) };
            var converted = LuaProfileMigration.Convert(legacy);
            var before = new MacroEngine(); before.Apply(legacy); var after = new MacroEngine(); after.Apply(converted);
            long clock = 0;
            for (int run = 0; run < 2; run++)
            {
                var variant = Trajectory.VariantFor(kind, run == 1);
                int duration = Trajectory.Build(legacy, variant).Sum(phase => phase.DurationMs);
                Compare(before, after, default, clock++);
                for (int t = 0; t <= duration + 2; t++)
                    Compare(before, after, new ControllerState(t == 0 ? (ushort)ControlId.A : (ushort)0, 9, 71, -2000, 77, 4421, -311), clock + t);
                clock += duration + 4;
            }
            if (converted.Bindings[ControlId.A].Consume != true || converted.Bindings[ControlId.A].Script != ScriptKind.Lua)
                throw new Exception("Binding flags were not preserved.");
        }

        foreach (var (duration, header, data) in new[] { (10, "RightY", "0,-32769"), (10, "RightY,RightY", "0,0,0"),
            (10, "A", "0,2"), (10, "RightY", "1,0"), (10, "RightY", "0,0\n0,1"), (10, "File", "0,0") })
        {
            try { _ = RecordedLua.Parse(duration, header, data); throw new Exception("Invalid raw input was accepted."); }
            catch (InvalidDataException) { }
        }
        var blank = StudioProfile.CreateDefault(); blank.Validate();
        if (blank.Bindings.Count != 0 || blank.Timings.Count != 0 || blank.Recordings.Count != 0) throw new Exception("New profiles still contain predetermined actions.");
        Persistence();
        if (profileDirectory is not null)
        {
            foreach (var profile in new ProfileStore(profileDirectory).LoadAll())
            {
                foreach (var recording in profile.Recordings.Values) VerifyRecording(recording);
                var converted = LuaProfileMigration.Convert(profile);
                if (converted.Recordings.Count != 0 || converted.Timings.Count != 0 || converted.Id != profile.Id || converted.Name != profile.Name)
                    throw new Exception("Profile conversion retained old sequences or changed profile identity.");
                Console.WriteLine($"Verified {profile.Name}: {profile.Recordings.Count} captures converted; {converted.LuaScripts.Count} Lua scripts; mappings preserved.");
            }
        }
        Console.WriteLine("SUCCESS Lua conversion: exact selected-channel values, absolute timing, late polls, completion, cancellation, maximum captures, old bindings and persistence.");
    }

    private static StudioProfile Legacy() => new() { Id = Guid.NewGuid(), Name = "Legacy test", ScriptsEnabled = true };

    private static void VerifyRecording(MacroRecording recording, bool everyMillisecond = true)
    {
        var legacy = Legacy().WithRecording(recording).WithBinding(ControlId.A, new(ScriptKind.Recorded, RecordingId: recording.Id));
        var converted = LuaProfileMigration.Convert(legacy);
        foreach (bool irregular in new[] { false, true })
        {
            var before = new MacroEngine(); before.Apply(legacy); var after = new MacroEngine(); after.Apply(converted);
            Compare(before, after, default, 0);
            int t = 0;
            while (t <= recording.DurationMs + 3)
            {
                Compare(before, after, new ControllerState(t == 0 ? (ushort)ControlId.A : (ushort)ControlId.B, 39, 127, 3300, -4100, -75, 999), 100 + t);
                t += irregular ? t % 31 + 7 : everyMillisecond ? 1 : 1001;
            }
            Compare(before, after, default, 100 + recording.DurationMs + 100);
            // A disconnect and Stop relinquish the script's channels immediately.
            after.Tick(default, PaddleButtons.None, false, 200 + recording.DurationMs);
            after.Stop();
            if (after.Tick(new ControllerState(0, 0, 0, 12, 34, 56, 78), default, true, 201 + recording.DurationMs).RightY != 78)
                throw new Exception("Stopped script retained controller ownership.");
        }
    }

    private static void Compare(MacroEngine before, MacroEngine after, ControllerState input, long time)
    {
        var expected = before.Tick(input, default, true, time);
        var actual = after.Tick(input, default, true, time);
        if (expected != actual || after.Status.Error is not null) throw new Exception($"Lua conversion differs at {time} ms: {expected} vs {actual}; {after.Status.Error}");
    }

    private static void Persistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.LuaMigration." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory);
            var recording = new MacroRecording(Guid.NewGuid(), "Standstill Shot", RecordingChannels.RightStick,
                [new(0, new(0, 0, 0, 0, 0, short.MinValue, 101)), new(40, new(0, 0, 0, 0, 0, 0, 0))], 80);
            var original = Legacy().WithRecording(recording).WithBinding(ControlId.A, new(ScriptKind.Recorded, RecordingId: recording.Id));
            store.Save(original); store.SaveSelectedId(original.Id);
            var migrated = store.MigrateLuaProfiles().Single();
            if (migrated.Recordings.Count != 0 || migrated.Timings.Count != 0 || !migrated.LuaScripts.ContainsKey(recording.Id)) throw new Exception("Saved conversion was incomplete.");
            if (store.LoadSelectedId() != original.Id) throw new Exception("Migration changed selected profile.");
            var saved = File.ReadAllText(Path.Combine(directory, original.Id.ToString("N") + ".json"));
            _ = store.MigrateLuaProfiles();
            if (File.ReadAllText(Path.Combine(directory, original.Id.ToString("N") + ".json")) != saved) throw new Exception("Migration was not idempotent.");
            if (Directory.GetFiles(Path.Combine(directory, "Backups"), "*.json", SearchOption.AllDirectories).Length != 1) throw new Exception("Original was not backed up once.");
            if (JsonDocument.Parse(saved).RootElement.GetProperty("version").GetInt32() != 6) throw new Exception("Lua-only profile format was not selected.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
