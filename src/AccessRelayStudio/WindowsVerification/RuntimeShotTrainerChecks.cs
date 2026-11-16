using System.Diagnostics;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;

internal static class RuntimeShotTrainerChecks
{
    internal static void Run()
    {
        LuaCalibrationTrials();
        var baseline = new MacroRecording(Guid.NewGuid(), "Synthetic old capture", RecordingChannels.All,
            [new(0, new(0, 0, 0, -2933, -888, 50, -100)),
             new(3291, new(0, 0, 0, -2933, -888, 0, -32767)),
             new(3491, new(0, 0, 0, -2933, -888, 0, -15000)),
             new(3531, new(0, 0, 0, -2933, -888, 0, 32767)),
             new(3631, new(0, 0, 0, -2933, -888, 0, 800)),
             new(8000, new(0, 0, 0, -2775, -888, 0, 800))], 9104);
        var training = RecordedInputTraining.Create("test-game", "standing-still", "rightStick", baseline, "late", "fast");
        var script = training.ToScript(Guid.NewGuid(), "One-tap shot");
        var profile = StudioProfile.CreateDefault().WithLuaScript(script)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Lua, Consume: true, LuaScriptId: script.Id));
        using var input = new FakeControllerInput();
        using var runtime = new StudioRuntime(input, profile, new StickDeadzone(0, 0));
        runtime.Attach(new FakeControllerOutput());
        var manual = new ControllerState((ushort)ControlId.B, 70, 220, 18000, -21000, 0, 0);
        input.SetState(manual);
        Wait(() => runtime.Latest?.Input.State == manual && runtime.Latest?.OutputActive == true, "Input/output did not connect.");
        runtime.SetScriptsEnabled(true);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var tuned = (training with { HoldMs = 200 + attempt * 16, TempoMs = 40 + attempt * 4 }).ToScript(script.Id, script.Name);
            runtime.Apply(profile.WithLuaScript(tuned));
            if (!runtime.ScriptsEnabled) throw new Exception("Applying a tuned shot turned Run scripts off.");
            input.TapPaddle(PaddleButtons.LargeRight); // no held level on any poll
            Wait(() => runtime.Latest?.Macro.IsActive == true && runtime.Latest?.Output.RightY < 0, "Brief paddle tap did not start the shot immediately.");
            CheckManual(runtime, manual);
            Wait(() => runtime.Latest?.Output.RightY > 0, "The shot failed to reach the up stroke after paddle release.");
            CheckManual(runtime, manual);
            Wait(() => runtime.Latest?.Macro.IsActive == false && runtime.Latest?.Output == manual, "The shot did not finish and relinquish its channels.");
            if (!runtime.ScriptsEnabled) throw new Exception("Finishing the shot changed Run scripts.");
        }
        runtime.SetScriptsEnabled(false); runtime.Apply(profile);
        if (runtime.ScriptsEnabled) throw new Exception("Editing an off profile unexpectedly enabled scripts.");
        Console.WriteLine("SUCCESS: real StudioRuntime with simulated devices accepts released paddle taps, preserves live movement/buttons/triggers, finishes each shot, and retains Run scripts through three tuning applies.");
    }

    private static void LuaCalibrationTrials()
    {
        var baseline = new MacroRecording(Guid.NewGuid(), "Calibration fixture", RecordingChannels.RightStick,
            [new(0, default), new(8, default(ControllerState) with { RightY = short.MinValue }),
             new(108, default(ControllerState) with { RightY = -20000 }),
             new(128, default(ControllerState) with { RightY = 10000 }),
             new(148, default(ControllerState) with { RightY = short.MaxValue }), new(188, default)], 208);
        var script = RecordedLua.Convert(baseline);
        var profile = StudioProfile.CreateDefault().WithLuaScript(script)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Lua, LuaScriptId: script.Id));
        using var input = new FakeControllerInput();
        using var runtime = new StudioRuntime(input, profile, new StickDeadzone(8, 8));
        var output = new FakeControllerOutput(); runtime.Attach(output);
        Wait(() => runtime.Latest?.Input.IsConnected == true && runtime.Latest?.OutputActive == true, "Lua trial device did not connect.");
        runtime.SetScriptsEnabled(true);

        // The automatic path evaluates exactly the same saved binding without physical stick input.
        runtime.StartLuaCalibrationTrial(ControlId.LargeRight, automatic: true);
        Wait(() => runtime.Latest?.Output.RightY < -16000, "Automatic Lua trial did not start its downstroke.");
        Wait(() => runtime.Latest?.Output.RightY > 16000, "Automatic Lua trial did not deliver its upswing.");
        Wait(() => runtime.LastLuaCalibrationAction is not null, "Completed Lua trial did not create an output receipt.");
        var first = runtime.LastLuaCalibrationAction!;
        if (runtime.LuaCalibrationArmed || runtime.Latest?.Output.RightY != 0 || first.Control != ControlId.LargeRight
            || first.ScriptId != script.Id || first.ProfileId != profile.Id || first.Key != LuaShotCalibration.ConfigurationKey(profile.Id, ControlId.LargeRight, script))
            throw new Exception("Lua trial receipt did not match the completed neutral script.");
        runtime.PauseLuaCalibrationTrial();
        if (runtime.LastLuaCalibrationAction != first) throw new Exception("Pausing cleared a completed receipt.");

        runtime.StartLuaCalibrationTrial(ControlId.LargeRight, automatic: false);
        if (runtime.LastLuaCalibrationAction is not null) throw new Exception("New trial reused a stale receipt.");
        Thread.Sleep(30);
        if (runtime.Latest?.Macro.IsActive == true) throw new Exception("Paddle-armed trial ran without a paddle press.");
        input.TapPaddle(PaddleButtons.LargeRight);
        Wait(() => runtime.LastLuaCalibrationAction is not null, "Released paddle tap did not complete the complete Lua shot.");
        if (runtime.LastLuaCalibrationAction!.Id <= first.Id) throw new Exception("Lua action IDs did not increase.");

        var buttonProfile = profile.WithBinding(ControlId.X, new(ScriptKind.Lua, LuaScriptId: script.Id));
        runtime.Apply(buttonProfile);
        runtime.StartLuaCalibrationTrial(ControlId.X, automatic: false);
        input.SetState(default(ControllerState) with { Buttons = (ushort)ControlId.X });
        Wait(() => runtime.Latest?.Macro.IsActive == true, "Selected button did not start its Lua trial.");
        if (runtime.Latest?.Output.Buttons != 0) throw new Exception("Selected button leaked into the scripted shot.");
        Wait(() => runtime.LastLuaCalibrationAction is not null, "Held selected button cancelled its own completed Lua receipt.");
        Thread.Sleep(20);
        if (runtime.Latest?.Output.Buttons != 0) throw new Exception("Held button leaked after Lua completion before physical release.");
        input.SetState(default);
        Wait(() => runtime.Latest?.Input.State == default(ControllerState), "Selected button did not release.");
        input.SetState(default(ControllerState) with { Buttons = (ushort)ControlId.Y });
        Wait(() => runtime.LastLuaCalibrationAction is null, "A new unrelated button retained stale calibration feedback.");
        input.SetState(default);
        Wait(() => runtime.Latest?.Input.State == default(ControllerState), "Unrelated button did not release.");

        runtime.CollectLuaCalibrationBall();
        if (!runtime.LuaCalibrationPickupActive) throw new Exception("Practice pickup did not wait for its countdown.");
        Wait(() => runtime.Latest?.Output.Buttons == (ushort)ControlId.A, "Practice pickup did not send A from the existing controller.");
        Wait(() => !runtime.LuaCalibrationPickupActive && runtime.Latest?.Output == default(ControllerState), "Practice pickup did not return to neutral.");
        if (runtime.LastLuaCalibrationAction is not null) throw new Exception("Practice pickup fabricated a Lua shot receipt.");

        runtime.Apply(profile);
        if (runtime.LastLuaCalibrationAction is not null) throw new Exception("Applying a profile retained stale Lua calibration feedback.");
        runtime.StartLuaCalibrationTrial(ControlId.LargeRight, automatic: true);
        runtime.PauseLuaCalibrationTrial();
        Thread.Sleep(20);
        if (runtime.LuaCalibrationArmed || runtime.LastLuaCalibrationAction is not null || runtime.Latest?.Macro.IsActive == true)
            throw new Exception("Pausing a queued automatic trial did not cancel its shot.");

        runtime.StartLuaCalibrationTrial(ControlId.LargeRight, automatic: true);
        Wait(() => runtime.Latest?.Macro.IsActive == true, "Cancellation fixture did not start.");
        input.SetState(default(ControllerState) with { RightY = short.MinValue });
        Wait(() => !runtime.LuaCalibrationArmed, "Physical stick input did not cancel the Lua trial.");
        if (runtime.LastLuaCalibrationAction is not null) throw new Exception("Interrupted Lua trial produced a receipt.");
        input.SetState(default);
        Wait(() => runtime.Latest?.Input.State == default(ControllerState), "Physical controls did not return to neutral.");

        runtime.StartLuaCalibrationTrial(ControlId.LargeRight, automatic: true);
        Wait(() => runtime.Latest?.Macro.IsActive == true, "Output rejection fixture did not start.");
        output.RejectWrites = true;
        Wait(() => !runtime.HasOutput, "Rejected Lua output did not disconnect.");
        if (runtime.LastLuaCalibrationAction is not null || runtime.LuaCalibrationArmed)
            throw new Exception("Rejected output generated a valid Lua trial receipt.");
        Console.WriteLine("SUCCESS: full Lua automatic and paddle trials produce source-bound receipts only after accepted neutral completion; manual motion and rejected output cancel.");
    }

    private static void CheckManual(StudioRuntime runtime, ControllerState expected)
    {
        var actual = runtime.Latest!.Output;
        if (actual with { RightX = 0, RightY = 0 } != expected) throw new Exception("Shot playback overrode unrelated manual input.");
    }
    private static void Wait(Func<bool> predicate, string error)
    {
        long deadline = Environment.TickCount64 + 4000;
        while (!predicate() && Environment.TickCount64 < deadline) Thread.Sleep(1);
        if (!predicate()) throw new Exception(error);
    }
}
