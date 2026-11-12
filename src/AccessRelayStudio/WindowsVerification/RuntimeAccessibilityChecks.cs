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
        Wait(() => runtime.Latest is { OutputActive: true } sample && sample.Input.IsConnected, "Input/output did not connect.");
        var down = default(ControllerState) with { Buttons = (ushort)ControlId.A };
        input.SetState(down);
        Wait(() => runtime.Latest?.Output.Buttons == down.Buttons, "Sticky press did not reach output.");
        input.SetState(default);
        Wait(() => runtime.Latest is { } sample && sample.Input.State.Buttons == 0 && sample.Output.Buttons == down.Buttons,
            "Sticky release was not retained.");
        runtime.StopOutput(stopScripts: true);
        runtime.Attach(new UsbipXboxOutput());
        Wait(() => runtime.Latest is { OutputActive: true } sample && sample.Output.Buttons == 0, "Stop/restart resumed a sticky hold.");
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
        Smoothing();
        Navigation();
        Feedback();
    }

    private static void Smoothing()
    {
        using var input = new EliteInputMonitor();
        var profile = StudioProfile.CreateDefault() with { Smoothing = new(100, 100) };
        using var runtime = new StudioRuntime(input, profile, new StickDeadzone(5, 5));
        runtime.Attach(new UsbipXboxOutput());
        Wait(() => runtime.Latest is { OutputActive: true } sample && sample.Input.IsConnected, "Smoothing input/output did not connect.");
        var step = default(ControllerState) with { RightX = short.MaxValue };
        input.SetState(step);
        Wait(() => runtime.Latest is { } sample && sample.Input.State == step && sample.Output.RightX is > 0 and < short.MaxValue,
            "Manual stick output was not smoothed.");
        input.SetState(step with { RightX = 100 });
        Wait(() => runtime.Latest is { } sample && sample.Input.State.RightX == 100 && sample.Output.RightX == 0,
            "Returning to the deadzone left a filter tail.");
        runtime.StartRecording(RecordingChannels.All);
        input.SetState(step);
        Wait(() => runtime.Latest?.Output.RightX == short.MaxValue, "Recording changed physical stick input.");
        input.SetState(default);
        Wait(() => runtime.Latest?.Input.State == default(ControllerState), "Neutral recording sample was not observed.");
        runtime.StopRecording(); runtime.DiscardRecording();
        runtime.SetVisionTrainingRecording(true);
        input.SetState(step); Wait(() => runtime.Latest?.Output.RightX == short.MaxValue, "Training smoothed manual input.");
        input.SetState(default); Wait(() => runtime.Latest?.Output.RightX == 0, "Training neutralization failed.");
        runtime.SetVisionTrainingRecording(false);
        runtime.SetScriptsEnabled(true);
        var baseline = runtime.Latest;
        Wait(() => !ReferenceEquals(runtime.Latest, baseline), "Scripts did not observe released controls.");
        input.SetState(default, PaddleButtons.LargeRight);
        Wait(() => runtime.Latest is { } sample && sample.Macro.IsActive && sample.Output.RightY == short.MinValue + 1,
            "Smoothing altered the timed sequence output.");
        runtime.SetScriptsEnabled(false);
        input.SetState(default); Wait(() => runtime.Latest?.Output == default(ControllerState), "Disabling scripts retained output.");
        runtime.StopOutput(stopScripts: true); runtime.Attach(new UsbipXboxOutput());
        Wait(() => runtime.Latest?.Output == default(ControllerState), "Output restart retained smoothing history.");
        Console.WriteLine("SUCCESS: real runtime smoothing, immediate deadzone neutralization, raw recording/training and unsmoothed sequences with simulated devices.");
    }

    private static void Navigation()
    {
        using var input = new EliteInputMonitor();
        using var runtime = new StudioRuntime(input, StudioProfile.CreateDefault(), new StickDeadzone(5, 5));
        runtime.Attach(new UsbipXboxOutput());
        Wait(() => runtime.Latest is { OutputActive: true } sample && sample.Input.IsConnected, "Navigation input/output did not connect.");
        var config = new SwitchNavigationSettings(true, ControlId.DPadRight, ControlId.A, 50);
        runtime.ConfigureNavigation(config);
        var previous = runtime.Latest;
        Wait(() => !ReferenceEquals(runtime.Latest, previous), "Navigation did not observe released controls.");
        var step = default(ControllerState) with { Buttons = (ushort)ControlId.DPadRight, RightX = short.MaxValue };
        input.SetState(step);
        Wait(() => runtime.NavigationStatus.Events.Length == 1, "Physical step did not reach navigation.");
        Wait(() => runtime.Latest is { } sample && sample.Input.State == step && sample.Output == default(ControllerState),
            "Navigation leaked physical controls to gameplay.");
        input.SetState(default); Wait(() => runtime.Latest?.Input.State == default(ControllerState), "Navigation release was not observed.");
        var select = default(ControllerState) with { Buttons = (ushort)ControlId.A };
        input.SetState(select);
        Wait(() => runtime.NavigationStatus.Events.Length == 2, "Physical selection did not reach navigation.");
        var history = runtime.NavigationStatus;
        if (history.Events[0].Action != SwitchNavigationAction.Step || history.Events[1].Action != SwitchNavigationAction.Activate)
            throw new InvalidOperationException("Navigation event order changed.");
        runtime.BeginVisionSession();
        if (runtime.SetVisionArmed(true)) throw new InvalidOperationException("Vision armed during controller navigation.");
        runtime.ConfigureNavigation(config with { Enabled = false });
        previous = runtime.Latest;
        Wait(() => !ReferenceEquals(runtime.Latest, previous) && runtime.Latest?.Output == default(ControllerState),
            "Exiting navigation forwarded a held selection press.");
        input.SetState(default); Wait(() => runtime.Latest?.Input.State == default(ControllerState), "Exit release was not observed.");
        input.SetState(select); Wait(() => runtime.Latest?.Output.Buttons == select.Buttons, "Fresh gameplay press after navigation was lost.");
        input.SetState(default); Wait(() => runtime.Latest?.Input.State == default(ControllerState), "Gameplay release was not observed.");
        runtime.ConfigureNavigation(config);
        long generation = runtime.NavigationStatus.Generation;
        input.SetConnection(false, 2); Wait(() => runtime.Latest?.Input.IsConnected == false, "Navigation disconnect was not observed.");
        input.SetState(select); input.SetConnection(true, 3);
        Wait(() => runtime.Latest is { } sample && sample.Input.IsConnected && sample.Input.State == select, "Navigation reconnect was not observed.");
        if (runtime.NavigationStatus.Generation <= generation || runtime.NavigationStatus.Events.Length != 0)
            throw new InvalidOperationException("Reconnect retained navigation history or selected a held control.");
        runtime.Apply(StudioProfile.CreateDefault());
        if (runtime.NavigationStatus.Events.Length != 0) throw new InvalidOperationException("Profile change retained navigation events.");
        runtime.StopOutput(stopScripts: true);
        if (runtime.NavigationStatus.Enabled || runtime.NavigationStatus.Events.Length != 0)
            throw new InvalidOperationException("Stop retained controller navigation.");
        Console.WriteLine("SUCCESS: runtime switch navigation, neutral gameplay output, ordered bridge history, release gate, profile/disconnect resets and Stop.");
    }

    private static void Feedback()
    {
        using var input = new EliteInputMonitor();
        var output = new CueOutput();
        var feedback = new FeedbackDispatcher(output);
        feedback.Configure(new(AudioEnabled: true));
        var profile = StudioProfile.CreateDefault();
        using var runtime = new StudioRuntime(input, profile, new StickDeadzone(0, 0), feedback);
        runtime.Attach(new UsbipXboxOutput());
        Wait(() => runtime.Latest is { OutputActive: true } sample && sample.Input.IsConnected, "Feedback runtime did not connect.");
        runtime.Apply(StudioProfile.CreateDefault("Another setup"));
        Wait(() => output.Cues.Contains(FeedbackCueKind.ProfileChanged), "Profile change did not emit feedback.");
        if (!runtime.TestFeedback()) throw new InvalidOperationException("Explicit feedback test was not queued.");
        Wait(() => output.Cues.Contains(FeedbackCueKind.Test), "Explicit test cue was not delivered.");
        runtime.ConfigureVision(VisionSettings.Default with { MaximumFrameAgeMs = 500, MaximumLatenessMs = 100 });
        runtime.BeginVisionSession();
        if (!runtime.SetVisionArmed(true)) throw new InvalidOperationException("Synthetic feedback vision session did not arm.");
        input.SetState(default(ControllerState) with { Buttons = (ushort)ControlId.X });
        Wait(() => runtime.VisionStatus.ShotToken != 0, "Synthetic feedback shot did not begin.");
        var shot = runtime.VisionStatus;
        long now = Stopwatch.GetTimestamp();
        if (!runtime.SubmitVisionPrediction(new(shot.SessionGeneration, shot.ShotToken, 1, now, now, 200, 1, 0, true)))
            throw new InvalidOperationException("Synthetic feedback prediction was rejected.");
        Wait(() => output.Cues.Contains(FeedbackCueKind.PlayerLocked), "Accepted player lock did not emit feedback.");
        Wait(() => runtime.VisionStatus.AutomatedReleases == 1, "Synthetic feedback deadline did not execute.");
        Wait(() => output.Cues.Contains(FeedbackCueKind.Released), "Completed release did not emit feedback.");
        if (output.Cues.Count(kind => kind == FeedbackCueKind.Released) != 1)
            throw new InvalidOperationException("Polling and deadline workers duplicated a release cue.");
        runtime.StopOutput(stopScripts: true);
        Console.WriteLine("SUCCESS: actual runtime profile, accepted player lock and completed release feedback with isolated simulated cue output.");
    }

    private sealed class CueOutput : IFeedbackOutput
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<FeedbackCueKind> Cues = new();
        public Task<string?> PlayAsync(FeedbackCue cue, FeedbackSettings settings, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Cues.Enqueue(cue.Kind); return Task.FromResult<string?>(null); }
        public void Stop() { }
        public void Dispose() { }
    }

    private static void Wait(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 5000) throw new InvalidOperationException(message); Thread.Sleep(5); }
    }
}
