using EliteScriptStudio.Core;
using System.Text.Json.Nodes;

var tests = new (string Name, Action Run)[]
{
    ("Default bindings and manual passthrough", Defaults),
    ("Preview and runtime agree for all five variants", AllTrajectories),
    ("Down/up sweep totals and phase boundaries", SweepDurations),
    ("Quick taps, held inputs and priority", TriggerEdges),
    ("Disconnect, emergency stop and hot apply", SafeReset),
    ("Session script switch survives profile changes and restores passthrough", SessionScriptSwitch),
    ("Optional face and trigger consumption", NativeBindings),
    ("Recorder filters and replays only right stick through a complete tap", RightStickRecording),
    ("Recorder captures and overlays a single axis", SingleAxisRecording),
    ("Recorder preserves ignored channels, timestamps and safe cancellation", FilteredRecording),
    ("Recording profile round-trip and version 1 compatibility", RecordingPersistence),
    ("Version 2 recording masks keep both axes for legacy stick aliases", LegacyRecordingMasks),
    ("Live input retains quick presses and projects both sticks", LiveFeedback),
    ("Profile round-trip, in-place save and import", Persistence),
    ("Profile deletion only removes the matching file and respects open handles", ProfileDeletion)
};
foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine($"PASS {name}");
}

static void LiveFeedback()
{
    var feedback = new PhysicalInputFeedback();
    var state = new ControllerState((ushort)(ControlId.X | ControlId.DPadUp | ControlId.DPadLeft), 255, 0,
        short.MaxValue, 0, 0, short.MinValue);
    uint expected = (uint)(ControlId.X | ControlId.DPadUp | ControlId.DPadLeft | ControlId.LeftTrigger | ControlId.LargeRight);
    Equal(expected, feedback.Sample(state, PaddleButtons.LargeRight, true, 1, 1));
    Equal(expected, feedback.Sample(default, PaddleButtons.None, true, 1, 60));
    Equal(0u, feedback.Sample(default, PaddleButtons.None, true, 1, 121));
    feedback.Sample(state, PaddleButtons.LargeRight, true, 1, 130);
    Equal(0u, feedback.Sample(default, PaddleButtons.None, false, 2, 135));
    Equal(0u, feedback.Sample(default, PaddleButtons.None, true, 3, 140));
    Equal((15d, 0d), PhysicalInputFeedback.StickOffset(state.LeftX, state.LeftY));
    Equal((0d, 15d), PhysicalInputFeedback.StickOffset(state.RightX, state.RightY));
    Equal((0d, -15d), PhysicalInputFeedback.StickOffset(0, short.MaxValue));
    var diagonal = PhysicalInputFeedback.StickOffset(short.MaxValue, short.MaxValue);
    Equal(15d, Math.Round(Math.Sqrt(diagonal.X * diagonal.X + diagonal.Y * diagonal.Y), 8));
}

static void Defaults()
{
    var profile = StudioProfile.CreateDefault();
    Equal(ScriptKind.Rhythm, profile.GetBinding(ControlId.LargeRight).Script);
    Equal(ScriptKind.IntentionalMiss, profile.GetBinding(ControlId.LargeLeft).Script);
    Equal(ScriptKind.Moving, profile.GetBinding(ControlId.SmallRight).Script);
    Equal(ScriptKind.Stepback, profile.GetBinding(ControlId.SmallLeft).Script);
    Equal(ScriptKind.Passthrough, profile.GetBinding(ControlId.X).Script);
    Equal(false, profile.ScriptsEnabled);
    var input = new ControllerState((ushort)ControlId.X, 14, 19, 800, -2100, 330, 889);
    var engine = Start(profile, input);
    Equal(input, engine.Tick(input, PaddleButtons.LargeRight, true, 1));
    Equal(false, engine.Status.IsActive);
}

static void AllTrajectories()
{
    var profile = Enabled();
    var input = new ControllerState((ushort)(ControlId.X | ControlId.DPadUp), 80, 50, -2300, 2400, 150, 810);
    var engine = Start(profile, input);
    long start = 50;
    Check(PaddleButtons.LargeRight, ShotVariant.Rhythm);
    Check(PaddleButtons.LargeLeft, ShotVariant.MissEarly);
    Check(PaddleButtons.LargeLeft, ShotVariant.MissLate);
    Check(PaddleButtons.SmallRight, ShotVariant.Moving);
    Check(PaddleButtons.SmallLeft, ShotVariant.Stepback);

    void Check(PaddleButtons paddle, ShotVariant variant)
    {
        var phases = Trajectory.Build(profile, variant);
        int offset = 0;
        for (int index = 0; index < phases.Count; index++)
        {
            var phase = phases[index];
            var expected = input with { RightY = phase.RightY, RightX = phase.RightX ?? input.RightX };
            var buttons = offset == 0 ? paddle : PaddleButtons.None;
            Equal(expected, engine.Tick(input, buttons, true, start + offset));
            Equal(index, engine.Status.PhaseIndex);
            Equal(phase, engine.Status.Phase);
            Equal(variant == ShotVariant.MissLate, engine.Status.LateMiss);
            offset += phase.DurationMs;
            Equal(expected, engine.Tick(input, PaddleButtons.None, true, start + offset - 1));
        }
        Equal(input, engine.Tick(input, PaddleButtons.None, true, start + offset));
        Equal(false, engine.Status.IsActive);
        start += offset + 5;
    }
}

static void SweepDurations()
{
    var profile = Enabled().WithTiming(ShotVariant.Rhythm,
        new ScriptTiming(StartDelay: 11, DownSweep: 38, DownHold: 520, UpSweep: 69, UpHold: 50, CenterHold: 90));
    var phases = Trajectory.Build(profile, ScriptKind.Rhythm);
    Equal(778, phases.Sum(phase => phase.DurationMs));
    Sequence([11, 13, 13, 12, 520, 18, 17, 17, 17, 50, 90],
        phases.Select(phase => phase.DurationMs));
    Sequence<short>([0, -8192, -16384, -24575, -32767, -24575, -8192, 8192, 24575, 32767, 0],
        phases.Select(phase => phase.RightY));
    var stepback = profile.WithTiming(ShotVariant.Stepback, new ScriptTiming(FlickX: 26000, FlickY: 0));
    var engine = Start(stepback, new ControllerState(0, 0, 0, 0, 0, -2700, 0));
    var input = new ControllerState(0, 0, 0, 0, 0, -2700, 0);
    Equal((short)26000, engine.Tick(input, PaddleButtons.SmallLeft, true, 1).RightX);
    Equal((short)0, engine.Tick(input, PaddleButtons.None, true, 65).RightX);
    Equal((short)-2700, engine.Tick(input, PaddleButtons.None, true, 305).RightX);
}

static void TriggerEdges()
{
    var engine = Start(Enabled());
    Equal((short)-32767, engine.Tick(default, PaddleButtons.None, true, 1, PaddleButtons.LargeRight).RightY);
    Equal(ScriptKind.Rhythm, engine.Status.Script);
    engine.Tick(default, PaddleButtons.LargeRight, true, 4);
    Equal(3, engine.Status.ElapsedMs);
    engine.Tick(default, PaddleButtons.LargeRight, true, 810);
    Equal(false, engine.Status.IsActive);
    engine.Tick(default, PaddleButtons.LargeRight, true, 811);
    Equal(false, engine.Status.IsActive);
    engine.Tick(default, PaddleButtons.None, true, 812);
    engine.Tick(default, PaddleButtons.LargeRight, true, 813);
    Equal(true, engine.Status.IsActive);

    var all = PaddleButtons.SmallLeft | PaddleButtons.SmallRight | PaddleButtons.LargeLeft;
    engine.Tick(default, all, true, 815);
    Equal(ControlId.SmallLeft, engine.Status.Control);
    Equal(ScriptKind.Stepback, engine.Status.Script);
    Equal(0, engine.Status.ElapsedMs);
}

static void SafeReset()
{
    var profile = Enabled();
    var input = new ControllerState((ushort)ControlId.X, 14, 19, 800, -2100, 330, 889);
    var engine = Start(profile, input);
    Equal((short)-32767, engine.Tick(input, PaddleButtons.LargeRight, true, 1).RightY);
    Equal(default(ControllerState), engine.Tick(input, PaddleButtons.LargeRight, false, 2));
    Equal(false, engine.Status.IsActive);
    Equal(input, engine.Tick(input, PaddleButtons.LargeRight, true, 3, PaddleButtons.LargeRight));
    engine.Tick(input, PaddleButtons.None, true, 4);
    Equal((short)-32767, engine.Tick(input, PaddleButtons.LargeRight, true, 5).RightY);
    engine.Stop();
    Equal(default(MacroStatus), engine.Status);
    Equal(input, engine.Tick(input, PaddleButtons.None, true, 6));
    Equal(input, engine.Tick(input, PaddleButtons.LargeRight, true, 7));
    Equal(false, engine.Status.IsActive);

    engine.Apply(profile.WithTiming(ShotVariant.Rhythm, profile.GetTiming(ShotVariant.Rhythm) with { DownHold = 75 }));
    Equal(input, engine.Tick(input, PaddleButtons.LargeRight, true, 8));
    engine.Tick(input, PaddleButtons.None, true, 9);
    Equal((short)-32767, engine.Tick(input, PaddleButtons.LargeRight, true, 10).RightY);
    Equal((short)-24575, engine.Tick(input, PaddleButtons.None, true, 85).RightY);
    engine.Apply(StudioProfile.CreateDefault("Another profile"));
    Equal(input, engine.Tick(input, PaddleButtons.None, true, 86));
    Equal(default(MacroStatus), engine.Status);
}

static void SessionScriptSwitch()
{
    var profile = Enabled().WithBinding(ControlId.A, new ScriptBinding(ScriptKind.Rhythm, Consume: true));
    var input = new ControllerState((ushort)ControlId.X, 14, 19, 800, -2100, 330, 889);
    var held = input with { Buttons = (ushort)(ControlId.X | ControlId.A) };
    var engine = Start(profile, input);
    Equal(true, engine.ScriptsEnabled);
    Equal(input with { RightY = -32767 }, engine.Tick(held, PaddleButtons.None, true, 1));

    engine.SetEnabled(false);
    Equal(false, engine.ScriptsEnabled);
    Equal(false, engine.Status.IsActive);
    Equal(held, engine.Tick(held, PaddleButtons.None, true, 2));
    engine.Apply(profile);
    Equal(false, engine.ScriptsEnabled);
    Equal(held, engine.Tick(held, PaddleButtons.None, true, 3));

    engine.SetEnabled(true);
    Equal(true, engine.ScriptsEnabled);
    Equal(input, engine.Tick(held, PaddleButtons.None, true, 4));
    Equal(false, engine.Status.IsActive);
    engine.Apply(profile with { ScriptsEnabled = false });
    Equal(true, engine.ScriptsEnabled);
    Equal(input, engine.Tick(held, PaddleButtons.None, true, 5));
    engine.Tick(input, PaddleButtons.None, true, 6);
    Equal(input with { RightY = -32767 }, engine.Tick(held, PaddleButtons.None, true, 7));
    engine.SetEnabled(true);
    Equal(true, engine.Status.IsActive);
}

static void NativeBindings()
{
    var profile = Enabled()
        .WithBinding(ControlId.A, new ScriptBinding(ScriptKind.Rhythm, Consume: true))
        .WithBinding(ControlId.LeftTrigger, new ScriptBinding(ScriptKind.Stepback, Consume: true))
        .WithBinding(ControlId.SmallLeft, new ScriptBinding(ScriptKind.Stepback, Enabled: false));
    var manual = new ControllerState((ushort)ControlId.X, 0, 120, 125, 250, 750, -460);
    var engine = Start(profile, manual);
    Equal(manual, engine.Tick(manual, PaddleButtons.SmallLeft, true, 1));
    var face = manual with { Buttons = (ushort)(ControlId.X | ControlId.A) };
    Equal(manual with { RightY = -32767 }, engine.Tick(face, PaddleButtons.None, true, 2));
    Equal(ControlId.A, engine.Status.Control);
    Equal(manual with { RightY = -32767 }, engine.Tick(manual with { LeftTrigger = 255 }, PaddleButtons.None, true, 3));
    Equal(ControlId.LeftTrigger, engine.Status.Control);
}

static void RightStickRecording()
{
    var recorder = new InputRecorder(RecordingChannels.RightStick);
    var live = new ControllerState((ushort)ControlId.X, 77, 13, 9000, -14000, 400, -800);
    Equal(true, recorder.Capture(live, 100));
    Equal(false, recorder.HasInput);
    recorder.Capture(live with { RightX = 4200, RightY = -29000 }, 200);
    recorder.Capture(live with { LeftX = -999, RightX = 4200, RightY = -29000 }, 220);
    recorder.Capture(live with { RightX = 9000, RightY = 24000 }, 236);
    recorder.Capture(live with { RightX = 0, RightY = 0 }, 280);
    recorder.Capture(default, 300);
    var recording = recorder.Stop("Fadeaway", 2000)!;
    Equal(200, recording.DurationMs); // 80 ms actual movement + 120 ms neutral tail.
    Sequence([0, 36, 80], recording.Frames.Select(frame => frame.TimeMs));
    Equal(new ControllerState(0, 0, 0, 0, 0, 4200, -29000), recording.Frames[0].State);
    Equal(recording.Id, recorder.Stop("Fadeaway", 2200)!.Id);
    Equal(false, recorder.Capture(default, 2201));

    var profile = Enabled().WithRecording(recording)
        .WithBinding(ControlId.LargeRight, new(ScriptKind.Recorded, RecordingId: recording.Id));
    var manual = new ControllerState((ushort)(ControlId.Y | ControlId.DPadLeft), 33, 99, -15000, 9000, -717, 1634);
    var engine = Start(profile, manual);
    Equal(manual with { RightX = 4200, RightY = -29000 },
        engine.Tick(manual, PaddleButtons.None, true, 20, PaddleButtons.LargeRight));
    Equal("Fadeaway", engine.Status.RecordingName);
    Equal(manual with { RightX = 4200, RightY = -29000 }, engine.Tick(manual, PaddleButtons.None, true, 55));
    Equal(manual with { RightX = 9000, RightY = 24000 }, engine.Tick(manual, PaddleButtons.None, true, 56));
    Equal(manual with { RightX = 0, RightY = 0 }, engine.Tick(manual, PaddleButtons.None, true, 100));
    Equal(manual, engine.Tick(manual, PaddleButtons.None, true, 220));
    Equal(false, engine.Status.IsActive);
}

static void FilteredRecording()
{
    var channels = RecordingChannels.All & ~(RecordingChannels.LeftStick | RecordingChannels.A | RecordingChannels.LeftTrigger);
    var recorder = new InputRecorder(channels);
    var ignored = new ControllerState((ushort)ControlId.A, 255, 0, -32767, 32767, 0, 0);
    Equal(true, recorder.Capture(ignored, 0));
    Equal(false, recorder.HasInput);
    var active = ignored with
    {
        Buttons = (ushort)(ControlId.A | ControlId.DPadRight | ControlId.X | ControlId.RightStick),
        RightTrigger = 180, RightX = -2120, RightY = 18000
    };
    recorder.Capture(active, 20);
    recorder.Capture(ignored, 72);
    recorder.Capture(active, 120);
    var recording = recorder.Stop("Filtered inputs", 160)!;
    Equal(140, recording.DurationMs);
    Equal(new ControllerState((ushort)(ControlId.DPadRight | ControlId.X | ControlId.RightStick),
        0, 180, 0, 0, -2120, 18000), recording.Frames[0].State);
    Equal(default, recording.Frames[1].State);

    var profile = Enabled().WithRecording(recording)
        .WithBinding(ControlId.LargeRight, new(ScriptKind.Recorded, RecordingId: recording.Id));
    var manual = new ControllerState((ushort)ControlId.A, 61, 5, 18000, -15000, 26000, -20000);
    var expected = recording.Frames[0].State with
    {
        Buttons = (ushort)(recording.Frames[0].State.Buttons | (ushort)ControlId.A),
        LeftTrigger = manual.LeftTrigger, LeftX = manual.LeftX, LeftY = manual.LeftY
    };
    var engine = Start(profile, manual);
    Equal(expected, engine.Tick(manual, PaddleButtons.LargeRight, true, 10));
    Equal(manual with { RightTrigger = 0, RightX = 0, RightY = 0 },
        engine.Tick(manual, PaddleButtons.LargeRight, true, 65));
    Equal(default, engine.Tick(manual, PaddleButtons.LargeRight, false, 111));
    Equal(manual, engine.Tick(manual, PaddleButtons.LargeRight, true, 112));
    engine.Tick(manual, PaddleButtons.None, true, 113);
    Equal(expected, engine.Tick(manual, PaddleButtons.LargeRight, true, 114));
    engine.Stop();
    Equal(manual, engine.Tick(manual, PaddleButtons.None, true, 115));
    Equal(false, engine.Status.IsActive);
    engine.Apply(profile);
    engine.Tick(manual, PaddleButtons.None, true, 116);
    Equal(expected, engine.Tick(manual, PaddleButtons.LargeRight, true, 117));
    Equal(manual, engine.Tick(manual, PaddleButtons.LargeRight, true, 257));
    Equal(manual, engine.Tick(manual, PaddleButtons.LargeRight, true, 258));

    var empty = new InputRecorder(RecordingChannels.RightStick);
    Equal(true, empty.Capture(ignored, 1000));
    Equal(false, empty.Capture(ignored, 1000 + InputRecorder.MaxDurationMs));
    Equal<MacroRecording?>(null, empty.Stop("No captured movement", 50000));
    var capped = new InputRecorder(RecordingChannels.RightStick);
    capped.Capture(active, 1000);
    Equal(false, capped.Capture(active, 1000 + InputRecorder.MaxDurationMs));
    Equal(InputRecorder.MaxDurationMs, capped.Stop("Long hold", 50000)!.DurationMs);
}

static void SingleAxisRecording()
{
    var sample = new ControllerState((ushort)ControlId.Y, 70, 150, 22000, -28000, 31000, -32000);
    var manual = new ControllerState((ushort)ControlId.X, 120, 210, -12000, 19000, -9000, 15000);
    var cases = new (RecordingChannels Axis, ControllerState Captured, ControllerState Output)[]
    {
        (RecordingChannels.LeftStickX, default(ControllerState) with { LeftX = sample.LeftX }, manual with { LeftX = sample.LeftX }),
        (RecordingChannels.LeftStickY, default(ControllerState) with { LeftY = sample.LeftY }, manual with { LeftY = sample.LeftY }),
        (RecordingChannels.RightStickX, default(ControllerState) with { RightX = sample.RightX }, manual with { RightX = sample.RightX }),
        (RecordingChannels.RightStickY, default(ControllerState) with { RightY = sample.RightY }, manual with { RightY = sample.RightY })
    };
    foreach (var (axis, captured, output) in cases)
    {
        var recorder = new InputRecorder(axis);
        recorder.Capture(sample, 20);
        var recording = recorder.Stop(axis.ToString(), 100)!;
        Equal(captured, recording.Frames.Single().State);
        var profile = Enabled().WithRecording(recording)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Recorded, RecordingId: recording.Id));
        var engine = Start(profile, manual);
        Equal(output, engine.Tick(manual, PaddleButtons.LargeRight, true, 10));
        Equal(manual, engine.Tick(manual, PaddleButtons.None, true, 90));
    }
}

static void RecordingPersistence()
{
    string directory = Path.Combine(Path.GetTempPath(), $"EliteScriptStudio.Recordings.Verify.{Guid.NewGuid():N}");
    try
    {
        var store = new ProfileStore(directory);
        var original = Enabled();
        store.Save(original);
        string path = Path.Combine(directory, original.Id.ToString("N") + ".json");
        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Equal(1, legacy["version"]!.GetValue<int>());
        var legacyProfile = legacy["profile"]!.AsObject();
        legacyProfile.Remove("recordings");
        foreach (var binding in legacyProfile["bindings"]!.AsObject())
            binding.Value!.AsObject().Remove("recordingId");
        File.WriteAllText(path, legacy.ToJsonString());
        var loaded = store.Load(original.Id);
        Equal(0, loaded.Recordings.Count);
        Equal(original.GetBinding(ControlId.LargeRight), loaded.GetBinding(ControlId.LargeRight));

        var recorder = new InputRecorder(RecordingChannels.RightStick | RecordingChannels.X);
        recorder.Capture(new ControllerState((ushort)ControlId.X, 0, 0, 0, 0, 4000, 8000), 200);
        recorder.Capture(default, 220);
        var recording = recorder.Stop("Recorded test", 500)!;
        var profile = loaded.WithRecording(recording)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Recorded, RecordingId: recording.Id));
        store.Save(profile);
        Equal(2, JsonNode.Parse(File.ReadAllText(path))!["version"]!.GetValue<int>());
        Verify(store.Load(profile.Id));
        Equal(original.Id, store.LoadAll().Single().Id);

        string exported = Path.Combine(directory, "copy.json");
        store.Export(profile, exported);
        Verify(store.Import(exported));
        Expect<InvalidDataException>(() => store.Save(loaded.WithBinding(ControlId.LargeRight,
            new(ScriptKind.Recorded, RecordingId: recording.Id))));
        Expect<InvalidDataException>(() => store.Save(profile.WithRecording(recording with
        {
            Frames = [recording.Frames[0], recording.Frames[0]]
        })));

        void Verify(StudioProfile result)
        {
            Equal(recording.Id, result.GetBinding(ControlId.LargeRight).RecordingId);
            var saved = result.Recordings[recording.Id];
            Equal(recording.Channels, saved.Channels);
            Equal(recording.DurationMs, saved.DurationMs);
            Sequence(recording.Frames, saved.Frames);
        }
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory))
                File.Delete(path);
            Directory.Delete(directory);
        }
    }
}

static void LegacyRecordingMasks()
{
    string directory = Path.Combine(Path.GetTempPath(), $"EliteScriptStudio.LegacyMasks.Verify.{Guid.NewGuid():N}");
    try
    {
        var store = new ProfileStore(directory);
        var profile = Enabled();
        var sample = new ControllerState((ushort)ControlId.X, 80, 200, 5000, -6000, 7000, -8000);
        var masks = new (string Alias, RecordingChannels Channels, ControllerState Expected)[]
        {
            ("leftStick", RecordingChannels.LeftStick, default(ControllerState) with { LeftX = sample.LeftX, LeftY = sample.LeftY }),
            ("rightStick", RecordingChannels.RightStick, default(ControllerState) with { RightX = sample.RightX, RightY = sample.RightY }),
            ("all", RecordingChannels.All, sample)
        };
        var recordings = new List<MacroRecording>();
        foreach (var (alias, channels, _) in masks)
        {
            var recorder = new InputRecorder(channels);
            recorder.Capture(sample, 0);
            var recording = recorder.Stop(alias, 100)!;
            recordings.Add(recording);
            profile = profile.WithRecording(recording);
        }
        store.Save(profile);
        string path = Path.Combine(directory, profile.Id.ToString("N") + ".json");
        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Equal(2, legacy["version"]!.GetValue<int>());
        var entries = legacy["profile"]!["recordings"]!.AsObject();
        for (int i = 0; i < masks.Length; i++)
            entries[recordings[i].Id.ToString()]!["channels"] = masks[i].Alias;
        File.WriteAllText(path, legacy.ToJsonString());

        var restored = store.Load(profile.Id);
        for (int i = 0; i < masks.Length; i++)
        {
            var recording = restored.Recordings[recordings[i].Id];
            Equal(masks[i].Channels, recording.Channels);
            Equal(masks[i].Expected, recording.Frames.Single().State);
        }
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory))
                File.Delete(path);
            Directory.Delete(directory);
        }
    }
}

static void Persistence()
{
    string directory = Path.Combine(Path.GetTempPath(), $"EliteScriptStudio.Verify.{Guid.NewGuid():N}");
    string exported = Path.Combine(directory, "copy.json");
    try
    {
        var store = new ProfileStore(directory);
        Equal(0, store.LoadAll().Count);
        var profile = Enabled();
        store.Save(profile);
        var draft = profile.WithTiming(ShotVariant.Stepback, new ScriptTiming(Settle: 287));
        store.Save(draft);
        Equal(profile.Id, store.LoadAll().Single().Id);
        Equal(287, store.Load(profile.Id).GetTiming(ShotVariant.Stepback).Settle);
        Equal(profile.GetBinding(ControlId.LargeRight), store.Load(profile.Id).GetBinding(ControlId.LargeRight));

        store.Export(draft, exported);
        var imported = store.Import(exported);
        Equal(false, profile.Id == imported.Id);
        Equal(2, store.LoadAll().Count);
        Equal(287, imported.GetTiming(ShotVariant.Stepback).Settle);
        string profilePath = Path.Combine(directory, profile.Id.ToString("N") + ".json");
        byte[] savedProfile = File.ReadAllBytes(profilePath);
        Equal<Guid?>(null, store.LoadSelectedId());
        store.SaveSelectedId(profile.Id);
        store.SaveSelectedId(imported.Id);
        Equal<Guid?>(imported.Id, new ProfileStore(directory).LoadSelectedId());
        Equal(true, savedProfile.SequenceEqual(File.ReadAllBytes(profilePath)));
        Equal(2, store.LoadAll().Count);
        Expect<InvalidDataException>(() => store.Save(draft.WithTiming(ShotVariant.Stepback, new ScriptTiming(DownSweep: 2))));
        File.WriteAllText(exported, "{");
        Expect<InvalidDataException>(() => store.Import(exported));
        Equal(2, store.LoadAll().Count);

        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(directory, profile.Id.ToString("N") + ".json");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                ExpectLockedFileFailure(() => store.Save(profile));
            Equal(0, Directory.EnumerateFiles(directory, "*.tmp").Count());
            Equal(287, store.Load(profile.Id).GetTiming(ShotVariant.Stepback).Settle);
        }
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory))
                File.Delete(path);
            Directory.Delete(directory);
        }
    }
}

static void ProfileDeletion()
{
    string directory = Path.Combine(Path.GetTempPath(), $"AccessRelay.MacroDelete.Verify.{Guid.NewGuid():N}");
    try
    {
        var store = new ProfileStore(directory);
        var target = Enabled();
        store.Delete(target.Id);
        Equal(false, Directory.Exists(directory));

        var other = Enabled(); // Both profiles have the same display name.
        store.Save(target);
        store.Save(other);
        store.SaveSelectedId(target.Id);
        string targetPath = Path.Combine(directory, target.Id.ToString("N") + ".json");
        string otherPath = Path.Combine(directory, other.Id.ToString("N") + ".json");
        byte[] otherContents = File.ReadAllBytes(otherPath);

        if (OperatingSystem.IsWindows())
        {
            using (File.Open(targetPath, FileMode.Open, FileAccess.Read, FileShare.None))
                ExpectLockedFileFailure(() => store.Delete(target.Id));
            Equal(target.Id, store.Load(target.Id).Id);
        }

        using (var reader = File.Open(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            store.Delete(target.Id);
            Equal((int)'{', reader.ReadByte());
            Equal(other.Id, store.LoadAll().Single().Id);
        }
        store.Delete(target.Id);
        Expect<FileNotFoundException>(() => store.Load(target.Id));
        Equal<Guid?>(target.Id, store.LoadSelectedId());
        Sequence(otherContents, File.ReadAllBytes(otherPath));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory))
                File.Delete(path);
            Directory.Delete(directory);
        }
    }
}

static StudioProfile Enabled() => StudioProfile.CreateDefault() with { ScriptsEnabled = true };

static MacroEngine Start(StudioProfile profile, ControllerState input = default)
{
    var engine = new MacroEngine();
    engine.Apply(profile);
    engine.Tick(input, PaddleButtons.None, true, 0);
    return engine;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
}

static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void ExpectLockedFileFailure(Action action)
{
    try { action(); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
    throw new InvalidOperationException("A locked profile must reject the operation.");
}
