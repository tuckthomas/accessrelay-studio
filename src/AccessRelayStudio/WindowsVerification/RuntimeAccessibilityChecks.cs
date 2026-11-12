using System.Collections.Immutable;
using System.Diagnostics;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;

internal static class RuntimeAccessibilityChecks
{
    internal static void Run()
    {
        using var input = new EliteInputMonitor();
        var profile = StudioProfile.CreateDefault() with { StickyControls = ImmutableHashSet.Create(ControlId.A) };
        using var runtime = new StudioRuntime(input, profile, new StickDeadzone(0, 0));
        runtime.Attach(new UsbipXboxOutput());
        Wait(() => runtime.Latest?.Input.IsConnected == true, "Input did not connect.");
        var down = default(ControllerState) with { Buttons = (ushort)ControlId.A };
        input.SetState(down);
        Wait(() => runtime.Latest?.Output.Buttons == down.Buttons, "Sticky press did not reach output.");
        input.SetState(default);
        Wait(() => runtime.Latest is { } sample && sample.Input.State.Buttons == 0 && sample.Output.Buttons == down.Buttons,
            "Sticky release was not retained.");
        runtime.StopOutput(stopScripts: true);
        runtime.Attach(new UsbipXboxOutput());
        Wait(() => runtime.Latest?.Output.Buttons == 0, "Stop/restart resumed a sticky hold.");
        input.SetState(down);
        Wait(() => runtime.Latest?.Output.Buttons == down.Buttons, "New sticky press was ignored.");
        runtime.SelectController(null);
        Wait(() => runtime.Latest is { } sample && sample.Input.State == down && sample.Output.Buttons == 0,
            "Device change latched an already held control.");
        input.SetState(default); Wait(() => runtime.Latest?.Input.State.Buttons == 0, "Physical release was not sampled.");
        input.SetState(down); Wait(() => runtime.Latest?.Output.Buttons == down.Buttons, "Fresh press after device selection failed.");
        input.SetState(default); Wait(() => runtime.Latest?.Input.State.Buttons == 0, "Release was not sampled.");
        runtime.Apply(profile with { StickyControls = ImmutableHashSet<ControlId>.Empty });
        Wait(() => runtime.Latest?.Output.Buttons == 0, "Profile change retained a synthetic hold.");
        runtime.Apply(profile);
        runtime.ConfigureVision(VisionSettings.Default with { ShootButton = ControlId.A });
        runtime.BeginVisionSession();
        Wait(() => runtime.Latest?.Input.State.Buttons == 0, "Neutral input was not sampled.");
        if (runtime.SetVisionArmed(true)) throw new InvalidOperationException("Vision armed with a sticky shoot button.");
        runtime.SetVisionTrainingRecording(true);
        input.SetState(down); Wait(() => runtime.Latest?.Output.Buttons == down.Buttons, "Training altered a manual press.");
        input.SetState(default); Wait(() => runtime.Latest?.Output.Buttons == 0, "Training latched manual input.");
        runtime.SetVisionTrainingRecording(false);
        runtime.StartRecording(RecordingChannels.All);
        input.SetState(down); Wait(() => runtime.Latest?.Output.Buttons == down.Buttons, "Macro recording altered manual input.");
        input.SetState(default); Wait(() => runtime.Latest?.Output.Buttons == 0, "Macro recording latched input.");
        runtime.StopRecording(); runtime.DiscardRecording();
        input.SetConnection(false, 2); Wait(() => runtime.Latest?.Input.IsConnected == false, "Disconnect was not sampled.");
        input.SetState(down); input.SetConnection(true, 3);
        Wait(() => runtime.Latest is { } sample && sample.Input.IsConnected && sample.Output.Buttons == 0,
            "Reconnect latched a held control.");
        Console.WriteLine("SUCCESS: real runtime sticky composition, Stop, profile/device changes, disconnect and raw recording with simulated devices.");
    }

    private static void Wait(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 5000) throw new InvalidOperationException(message); Thread.Sleep(5); }
    }
}
