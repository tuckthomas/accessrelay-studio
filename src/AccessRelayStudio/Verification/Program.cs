using EliteScriptStudio.Packs.Nba2K27;
using EliteScriptStudio.Core;
using System.Text.Json.Nodes;

var tests = new (string Name, Action Run)[]
{
    ("Versioned game packs, resource integrity, bounded import, tuning, Lua output and legacy profile compatibility", GamePackVerification.Run),
    ("Local voice command bounds, cancellation, synthetic acoustic templates, queue overload and recognizer faults", VoiceCommandVerification.Run),
    ("Co-pilot ownership, disconnects, conflicts, whole-vector axes, persistence and trace provenance", CoPilotVerification.Run),
    ("Feedback opt-in, bounded queue, deduplication, cancellation, faults and PCM limits", FeedbackVerification.Run),
    ("Switch navigation ordering, debounce, bounded history and fresh physical edges", SwitchNavigationVerification.Run),
    ("Elapsed-time stick smoothing, independent axes, neutralization and persistence", StickSmoothingVerification.Run),
    ("Sticky button edges, resets, manual composition and legacy profile compatibility", StickyButtonVerification.Run),
    ("Vision deadlines, manual cancellation, release latch, generations, freshness and ownership", VisionVerification.Run),
    ("NBA 2K27 player shot profile CRUD, selection, configuration hashes and bounded storage", Nba2K27ShotProfileVerification.Run),
    ("Lua timing, live input, state, cancellation, budgets and profile compatibility", LuaVerification.Run),
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
    ("Recorder starts its duration limit at the first included input", DelayedRecordingStart),
    ("Recording profile round-trip and version 1 compatibility", RecordingPersistence),
    ("Saved macros duplicate, retime and rename independently while preserving bindings", RecordingDuplication),
    ("Recording replacement at capacity and selective deletion persist", RecordingReplacementAndDeletion),
    ("Recording interval and frame edits preserve timing and manual input", RecordingEdits),
    ("Recorded right-stick phases use exact frame boundaries", RecordedRightStickPhases),
    ("NBA 2K27 feedback retimes the selected shot and preserves recorded input", Nba2K27Feedback),
    ("Version 2 recording masks keep both axes for legacy stick aliases", LegacyRecordingMasks),
    ("Live input retains quick presses and projects both sticks", LiveFeedback),
    ("Stick output returns to zero at center while preserving movement and buttons", CenteredStickOutput),
    ("Profile round-trip, in-place save and import", Persistence),
    ("Profile deletion only removes the matching file and respects open handles", ProfileDeletion)
};
foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine($"PASS {name}");
}

static void CenteredStickOutput()
{
    var resting = new ControllerState((ushort)ControlId.A, 70, 160, -213, 923, 513, -431);
    Equal(resting with { LeftX = 0, LeftY = 0, RightX = 0, RightY = 0 }, StickDeadzone.Default.Apply(resting));
    Equal(resting, new StickDeadzone(0, 0).Apply(resting));
    Equal(resting with { LeftX = 0, LeftY = 0 }, new StickDeadzone(3.5, 1).Apply(resting));
    Equal(resting with { RightX = 0, RightY = 0 }, new StickDeadzone(1, 2.5).Apply(resting));

    var diagonal = resting with { LeftX = 1200, LeftY = 1200 };
    Equal(diagonal with { RightX = 0, RightY = 0 }, StickDeadzone.Default.Apply(diagonal));
    var boundary = resting with { LeftX = -1638, LeftY = 0, RightX = 1639, RightY = 0 };
    Equal(boundary with { LeftX = 0 }, StickDeadzone.Default.Apply(boundary));
    var full = resting with { LeftX = short.MinValue, LeftY = short.MinValue, RightX = 0, RightY = short.MaxValue };
    Equal(full, StickDeadzone.Default.Apply(full));
    Equal(default, StickDeadzone.Default.Apply(full with { Buttons = 0, LeftTrigger = 0, RightTrigger = 0,
        LeftX = 0, LeftY = 1000, RightX = -1000, RightY = 0 }));

    string directory = Path.Combine(Path.GetTempPath(), $"AccessRelay.Deadzone.Verify.{Guid.NewGuid():N}");
    try
    {
        var store = new ProfileStore(directory);
        Equal(StickDeadzone.Default, store.LoadStickDeadzone());
        var customized = new StickDeadzone(0, 3.5);
        store.SaveStickDeadzone(customized);
        Equal(customized, new ProfileStore(directory).LoadStickDeadzone());
        Expect<InvalidDataException>(() => store.SaveStickDeadzone(new(51, 1)));
        Equal(customized, store.LoadStickDeadzone());
        Equal(0, store.LoadAll().Count);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
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
    Equal(true, empty.Capture(ignored, 1000 + InputRecorder.MaxDurationMs));
    Equal(0, empty.ElapsedMs);
    Equal<MacroRecording?>(null, empty.Stop("No captured movement", 50000));
    Equal(false, empty.Capture(active, 50001));
    var capped = new InputRecorder(RecordingChannels.RightStick);
    capped.Capture(active, 1000);
    Equal(false, capped.Capture(active, 1000 + InputRecorder.MaxDurationMs));
    Equal(InputRecorder.MaxDurationMs, capped.Stop("Long hold", 50000)!.DurationMs);
}

static void DelayedRecordingStart()
{
    const long armedAt = 100;
    long firstInputAt = armedAt + InputRecorder.MaxDurationMs + 5000;
    var recorder = new InputRecorder(RecordingChannels.RightStickY);
    var ignored = default(ControllerState) with { Buttons = (ushort)ControlId.A, LeftY = -32000 };
    var first = default(ControllerState) with { RightY = -28000 };
    var second = first with { RightY = 25000 };

    Equal(true, recorder.Capture(default, armedAt));
    Equal(true, recorder.Capture(ignored, firstInputAt - 1));
    Equal(false, recorder.HasInput);
    Equal(0, recorder.ElapsedMs);
    Equal(true, recorder.Capture(first, firstInputAt));
    Equal(true, recorder.HasInput);
    Equal(0, recorder.ElapsedMs);
    Equal(true, recorder.Capture(second, firstInputAt + 200));
    Equal(200, recorder.ElapsedMs);
    Equal(false, recorder.Capture(second, firstInputAt + InputRecorder.MaxDurationMs));

    var take = recorder.Stop("Delayed movement", firstInputAt + InputRecorder.MaxDurationMs + 2000)!;
    Equal(InputRecorder.MaxDurationMs, take.DurationMs);
    Equal(2, take.Frames.Length);
    Equal(new RecordedFrame(0, first), take.Frames[0]);
    Equal(new RecordedFrame(200, second), take.Frames[1]);
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

static void RecordingDuplication()
{
    string directory = Path.Combine(Path.GetTempPath(), $"AccessRelay.RecordingDuplication.Verify.{Guid.NewGuid():N}");
    var source = new MacroRecording(Guid.NewGuid(), "Three-pointer", RecordingChannels.RightStickY,
        [new(0, default(ControllerState) with { RightY = -30000 }),
         new(80, default), new(112, default(ControllerState) with { RightY = 30000 }),
         new(150, default)], 180);
    var original = Enabled().WithRecording(source)
        .WithBinding(ControlId.LargeRight, new(ScriptKind.Recorded, RecordingId: source.Id));
    string path = Path.Combine(directory, original.Id.ToString("N") + ".json");
    try
    {
        var store = new ProfileStore(directory);
        store.Save(original);
        var snapshot = store.Load(original.Id);
        var (updated, copy) = snapshot.DuplicateRecording(source.Id, "Midrange");
        Equal(false, source.Id == copy.Id);
        store.SaveRecordingEdit(snapshot, updated);

        var saved = store.Load(original.Id);
        var savedCopy = saved.Recordings[copy.Id];
        Equal(2, saved.Recordings.Count);
        Equal("Midrange", savedCopy.Name);
        Equal(source.Channels, savedCopy.Channels);
        Equal(source.DurationMs, savedCopy.DurationMs);
        Sequence(source.Frames, savedCopy.Frames);
        Sequence(original.Bindings.OrderBy(binding => binding.Key), saved.Bindings.OrderBy(binding => binding.Key));

        var retimed = Nba2K27Tuning.Apply(savedCopy, new Nba2K27Adjustment(-16, 0));
        store.SaveRecordingEdit(saved, saved.WithRecording(retimed));
        var edited = store.Load(original.Id);
        Sequence(source.Frames, edited.Recordings[source.Id].Frames);
        Equal(source.DurationMs, edited.Recordings[source.Id].DurationMs);
        Equal(new Nba2K27Intervals(64, 32), Nba2K27Tuning.GetIntervals(edited.Recordings[copy.Id]));

        var renamed = edited.RenameRecording(source.Id, "Standing three")
            .RenameRecording(copy.Id, "Midrange pull-up");
        store.SaveRecordingEdit(edited, renamed);
        var reloaded = store.Load(original.Id);
        Sequence(edited.Recordings.Keys.Order(), reloaded.Recordings.Keys.Order());
        Equal("Standing three", reloaded.Recordings[source.Id].Name);
        Equal("Midrange pull-up", reloaded.Recordings[copy.Id].Name);
        foreach (var (id, recording) in edited.Recordings)
        {
            Equal(id, reloaded.Recordings[id].Id);
            Equal(recording.Channels, reloaded.Recordings[id].Channels);
            Equal(recording.DurationMs, reloaded.Recordings[id].DurationMs);
            Sequence(recording.Frames, reloaded.Recordings[id].Frames);
        }
        Sequence(original.Bindings.OrderBy(binding => binding.Key), reloaded.Bindings.OrderBy(binding => binding.Key));
        Expect<InvalidDataException>(() => reloaded.RenameRecording(copy.Id, "   "));
        Expect<InvalidDataException>(() => reloaded.RenameRecording(copy.Id, new string('a', 81)));

        for (int i = reloaded.Recordings.Count; i < MacroRecording.MaxRecordingsPerProfile; i++)
            reloaded = reloaded.WithRecording(source with { Id = Guid.NewGuid(), Name = $"Take {i}" });
        Expect<InvalidOperationException>(() => reloaded.DuplicateRecording(source.Id, "Extra copy"));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}

static void RecordingReplacementAndDeletion()
{
    string directory = Path.Combine(Path.GetTempPath(), $"AccessRelay.RecordingLifecycle.Verify.{Guid.NewGuid():N}");
    try
    {
        var store = new ProfileStore(directory);
        var original = new MacroRecording(Guid.NewGuid(), "Original take", RecordingChannels.RightStickY,
            [new(0, default(ControllerState) with { RightY = 8000 })], 100);
        var other = original with { Id = Guid.NewGuid(), Name = "Keep this take" };
        var profile = Enabled().WithRecording(original).WithRecording(other)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Recorded, RecordingId: original.Id))
            .WithBinding(ControlId.A, new(ScriptKind.Recorded, Consume: true, RecordingId: original.Id))
            .WithBinding(ControlId.LargeLeft, new(ScriptKind.Recorded, RecordingId: other.Id));
        for (int i = profile.Recordings.Count; i < MacroRecording.MaxRecordingsPerProfile; i++)
            profile = profile.WithRecording(original with { Id = Guid.NewGuid(), Name = $"Extra take {i}" });
        store.Save(profile);

        var replacement = original with
        {
            Name = "Replacement take",
            Frames = [new(0, default(ControllerState) with { RightY = -16000 })],
            DurationMs = 150
        };
        store.Save(store.Load(profile.Id).WithRecording(replacement));
        var replaced = store.Load(profile.Id);
        Equal(MacroRecording.MaxRecordingsPerProfile, replaced.Recordings.Count);
        Equal(replacement.Name, replaced.Recordings[original.Id].Name);
        Equal(replacement.DurationMs, replaced.Recordings[original.Id].DurationMs);
        Sequence(replacement.Frames, replaced.Recordings[original.Id].Frames);
        Sequence(profile.Bindings.OrderBy(binding => binding.Key), replaced.Bindings.OrderBy(binding => binding.Key));

        store.Save(replaced.WithoutRecording(original.Id));
        var deleted = store.Load(profile.Id);
        Sequence(replaced.Recordings.Keys.Where(id => id != original.Id).Order(), deleted.Recordings.Keys.Order());
        Sequence(replaced.Bindings.Where(binding => binding.Value.RecordingId != original.Id).OrderBy(binding => binding.Key),
            deleted.Bindings.OrderBy(binding => binding.Key));
        Equal(other.Name, deleted.Recordings[other.Id].Name);
        Sequence(other.Frames, deleted.Recordings[other.Id].Frames);
        Equal(profile.ScriptsEnabled, deleted.ScriptsEnabled);
        Equal(profile.Name, deleted.Name);
        Sequence(profile.Timings.OrderBy(timing => timing.Key), deleted.Timings.OrderBy(timing => timing.Key));
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

static void RecordingEdits()
{
    var original = new MacroRecording(Guid.NewGuid(), "Timing test",
        RecordingChannels.RightStickY | RecordingChannels.RightTrigger | RecordingChannels.A,
        [new(0, default(ControllerState) with { RightY = -20000 }),
         new(20, default(ControllerState) with { RightY = -10000 }),
         new(40, default),
         new(41, default(ControllerState) with { RightY = 20000 }),
         new(80, default)], 100);

    var scaled = original.WithIntervalDuration(1, 4, 36);
    Sequence([0, 20, 32, 33, 56], scaled.Frames.Select(frame => frame.TimeMs));
    Equal(76, scaled.DurationMs);
    Sequence(original.Frames.Select(frame => frame.State), scaled.Frames.Select(frame => frame.State));
    var compressed = original.WithIntervalDuration(1, 4, 3);
    Sequence([0, 20, 21, 22, 23], compressed.Frames.Select(frame => frame.TimeMs));
    Equal(43, compressed.DurationMs);
    var shortenedTail = original.WithIntervalDuration(4, original.Frames.Length, 10);
    Sequence(original.Frames, shortenedTail.Frames);
    Equal(90, shortenedTail.DurationMs);
    var stretched = original.WithIntervalDuration(0, original.Frames.Length, 200);
    Sequence([0, 40, 80, 82, 160], stretched.Frames.Select(frame => frame.TimeMs));
    Equal(200, stretched.DurationMs);

    var editState = new ControllerState((ushort)(ControlId.A | ControlId.B), 99, 123, 9000, -5000, 18000, 15000);
    var edited = scaled.WithFrame(2, 31, editState);
    Equal(original.Id, edited.Id);
    Equal(original.Name, edited.Name);
    Equal(original.Channels, edited.Channels);
    Equal(scaled.DurationMs, edited.DurationMs);
    Equal(new RecordedFrame(31, new((ushort)ControlId.A, 0, 123, 0, 0, 0, 15000)), edited.Frames[2]);
    Equal(new RecordedFrame(40, default), original.Frames[2]);

    var manual = new ControllerState((ushort)ControlId.B, 64, 72, -9000, 6000, -12000, 4000);
    var profile = Enabled().WithRecording(edited)
        .WithBinding(ControlId.LargeRight, new(ScriptKind.Recorded, RecordingId: original.Id));
    var engine = Start(profile, manual);
    Equal(manual with { RightTrigger = 0, RightY = -20000 }, engine.Tick(manual, PaddleButtons.LargeRight, true, 100));
    Equal(manual with { RightTrigger = 0, RightY = -10000 }, engine.Tick(manual, PaddleButtons.None, true, 130));
    Equal(manual with { Buttons = (ushort)(ControlId.A | ControlId.B), RightTrigger = 123, RightY = 15000 },
        engine.Tick(manual, PaddleButtons.None, true, 131));
    Equal(manual with { RightTrigger = 0, RightY = 20000 }, engine.Tick(manual, PaddleButtons.None, true, 133));
    Equal(manual with { RightTrigger = 0, RightY = 0 }, engine.Tick(manual, PaddleButtons.None, true, 175));
    Equal(manual, engine.Tick(manual, PaddleButtons.None, true, 176));

    Expect<ArgumentOutOfRangeException>(() => original.WithIntervalDuration(-1, 4, 36));
    Expect<ArgumentOutOfRangeException>(() => original.WithIntervalDuration(1, 1, 36));
    Expect<ArgumentOutOfRangeException>(() => original.WithIntervalDuration(1, 4, 2));
    Expect<ArgumentOutOfRangeException>(() => original.WithIntervalDuration(1, 4, int.MaxValue));
    Expect<ArgumentOutOfRangeException>(() => original.WithFrame(5, 90, default));
    Expect<ArgumentOutOfRangeException>(() => original.WithFrame(0, 1, default));
    Expect<ArgumentOutOfRangeException>(() => original.WithFrame(2, 41, default));
    Expect<ArgumentOutOfRangeException>(() => original.WithFrame(4, original.DurationMs, default));

    string directory = Path.Combine(Path.GetTempPath(), $"AccessRelay.RecordingEdit.Verify.{Guid.NewGuid():N}");
    string path = Path.Combine(directory, profile.Id.ToString("N") + ".json");
    try
    {
        var store = new ProfileStore(directory);
        store.Save(profile.WithRecording(original));
        var snapshot = store.Load(profile.Id);
        store.SaveRecordingEdit(snapshot, snapshot.WithRecording(edited));

        var saved = store.LoadAll().Single();
        Equal(profile.Id, saved.Id);
        Equal(profile.GetBinding(ControlId.LargeRight), saved.GetBinding(ControlId.LargeRight));
        Equal(edited.DurationMs, saved.Recordings[original.Id].DurationMs);
        Sequence(edited.Frames, saved.Recordings[original.Id].Frames);

        byte[] savedBytes = File.ReadAllBytes(path);
        Expect<InvalidOperationException>(() => store.SaveRecordingEdit(snapshot, snapshot.WithRecording(stretched)));
        Sequence(savedBytes, File.ReadAllBytes(path));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}

static void RecordedRightStickPhases()
{
    var recording = Take(200, (0, 0), (10, -1600), (15, -4000), (25, -19000), (40, -24576), (70, -28000),
        (100, -10000), (115, 15000), (140, 24576), (155, 30000), (175, 20000), (185, 0));
    var phases = recording.FindRightStickIntervals();
    Check(recording, phases.DownSweep, new(2, 4), 25);
    Check(recording, phases.DownHold, new(4, 6), 60);
    Check(recording, phases.UpSweep, new(6, 8), 40);
    Check(recording, phases.UpHold, new(8, 10), 35);

    var edited = recording.WithIntervalDuration(2, 4, 40);
    Equal(phases, edited.FindRightStickIntervals());
    Check(edited, phases.DownSweep, new(2, 4), 40);
    Check(edited, phases.UpSweep, new(6, 8), 40);

    var direct = Take(60, (0, -30000), (25, 26000));
    var directPhases = direct.FindRightStickIntervals();
    Equal<RecordedInterval?>(null, directPhases.DownSweep);
    Check(direct, directPhases.DownHold, new(0, 1), 25);
    Equal<RecordedInterval?>(null, directPhases.UpSweep);
    Check(direct, directPhases.UpHold, new(1, 2), 35);

    var interrupted = Take(60, (0, -30000), (10, 0), (20, -30000), (30, 30000));
    var interruptedPhases = interrupted.FindRightStickIntervals();
    Check(interrupted, interruptedPhases.DownHold, new(0, 1), 10);
    Equal<RecordedInterval?>(null, interruptedPhases.UpSweep);
    Equal<RecordedInterval?>(null, interruptedPhases.UpHold);

    var holding = Take(60, (0, -30000));
    Check(holding, holding.FindRightStickIntervals().DownHold, new(0, 1), 60);
    Equal(default, Take(60, (0, 0), (25, 30000)).FindRightStickIntervals());
    var ignoredY = Take(60, (0, 0)) with { Channels = RecordingChannels.RightStickX };
    Equal(default, ignoredY.FindRightStickIntervals());

    static MacroRecording Take(int duration, params (int Time, short RightY)[] samples) =>
        new(Guid.NewGuid(), "Recorded phases", RecordingChannels.RightStickY,
            [.. samples.Select(sample => new RecordedFrame(sample.Time, default(ControllerState) with { RightY = sample.RightY }))], duration);

    static void Check(MacroRecording take, RecordedInterval? actual, RecordedInterval expected, int duration)
    {
        Equal<RecordedInterval?>(expected, actual);
        Equal(duration, take.IntervalDuration(actual!.Value));
    }
}

static void Nba2K27Feedback()
{
    var lateAndSlow = Nba2K27Tuning.Suggest(Nba2K27TimingFeedback.Late, Nba2K27TempoFeedback.Slow);
    Equal(new Nba2K27Adjustment(-16, -8), lateAndSlow);
    Equal(new Nba2K27Adjustment(16, 8), Nba2K27Tuning.Suggest(Nba2K27TimingFeedback.Early, Nba2K27TempoFeedback.Fast));
    Equal(new Nba2K27Adjustment(8, 4), Nba2K27Tuning.Suggest(Nba2K27TimingFeedback.SlightlyEarly, Nba2K27TempoFeedback.SlightlyFast));
    Equal(new Nba2K27Adjustment(-8, -4), Nba2K27Tuning.Suggest(Nba2K27TimingFeedback.SlightlyLate, Nba2K27TempoFeedback.SlightlySlow));
    Equal(default, Nba2K27Tuning.Suggest(Nba2K27TimingFeedback.Excellent, Nba2K27TempoFeedback.None));
    Equal(default, Nba2K27Tuning.Suggest(Nba2K27TimingFeedback.None, Nba2K27TempoFeedback.Excellent));

    var timing = new ScriptTiming(StartDelay: 20, DownHold: 80, UpSweep: 33);
    Equal(timing with { DownHold = 64, UpSweep = 25 }, Nba2K27Tuning.Apply(timing, lateAndSlow));
    Equal(true, ReferenceEquals(timing, Nba2K27Tuning.Apply(timing, default)));
    Equal(timing with { DownHold = 1, UpSweep = ScriptTiming.MaxMs },
        Nba2K27Tuning.Apply(timing, new(int.MinValue, int.MaxValue)));

    var recording = new MacroRecording(Guid.NewGuid(), "Test shot", RecordingChannels.RightStickY | RecordingChannels.A,
        [Frame(0, 0), Frame(20, -25000, ControlId.A), Frame(30, -30000), Frame(100, -10000), Frame(116, 15000),
            Frame(133, 32000), Frame(155, 0)], 175);
    Equal(new Nba2K27Intervals(80, 33), Nba2K27Tuning.GetIntervals(recording));
    var adjusted = Nba2K27Tuning.Apply(recording, lateAndSlow);
    Equal(recording.Id, adjusted.Id);
    Equal(recording.Channels, adjusted.Channels);
    Equal(151, adjusted.DurationMs);
    Equal(new Nba2K27Intervals(64, 25), Nba2K27Tuning.GetIntervals(adjusted));
    Sequence(recording.Frames.Select(frame => frame.State), adjusted.Frames.Select(frame => frame.State));
    Equal(true, ReferenceEquals(recording, Nba2K27Tuning.Apply(recording, default)));
    Equal(new Nba2K27Intervals(2, 2),
        Nba2K27Tuning.GetIntervals(Nba2K27Tuning.Apply(recording, new(int.MinValue, int.MinValue))));

    var atLimit = Nba2K27Tuning.Apply(recording with { DurationMs = InputRecorder.MaxDurationMs }, new(16, -8));
    Equal(InputRecorder.MaxDurationMs, atLimit.DurationMs);
    Equal(new Nba2K27Intervals(88, 25), Nba2K27Tuning.GetIntervals(atLimit));

    var snap = recording with { Frames = [Frame(0, -30000), Frame(100, 30000), Frame(150, 0)] };
    Equal(new Nba2K27Intervals(100, null), Nba2K27Tuning.GetIntervals(snap));
    Equal(new Nba2K27Intervals(84, null), Nba2K27Tuning.GetIntervals(Nba2K27Tuning.Apply(snap, lateAndSlow)));

    var multiShot = recording with
    {
        Frames = [.. recording.Frames, Frame(170, -30000), Frame(200, 30000)],
        DurationMs = 220
    };
    Equal(default, Nba2K27Tuning.GetIntervals(multiShot));
    Equal(true, ReferenceEquals(multiShot, Nba2K27Tuning.Apply(multiShot, lateAndSlow)));
    Equal(default, Nba2K27Tuning.GetIntervals(recording with { Frames = [Frame(0, -30000), Frame(150, 0)] }));

    static RecordedFrame Frame(int time, short rightY, ControlId button = 0) =>
        new(time, default(ControllerState) with { RightY = rightY, Buttons = (ushort)button });
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
