using System.Diagnostics;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;

internal static class RuntimeTrainingChecks
{
    internal static void Run()
    {
        using var input = new EliteInputMonitor();
        var profile = StudioProfile.CreateDefault() with { ScriptsEnabled = false };
        using var runtime = new StudioRuntime(input, profile, new StickDeadzone(0, 0));
        runtime.Attach(new UsbipXboxOutput());
        WaitUntil(() => runtime.Latest?.Input.IsConnected == true, "Simulated controller did not reach the runtime.");

        runtime.SetScriptsEnabled(true);
        ExpectBlocked(() => runtime.SetVisionTrainingRecording(true));
        Check(!runtime.IsVisionTrainingRecording, "Rejected training entry changed the active flag.");
        runtime.SetScriptsEnabled(false);
        runtime.StartRecording(RecordingChannels.All);
        ExpectBlocked(() => runtime.SetVisionTrainingRecording(true));
        runtime.StopRecording(); runtime.DiscardRecording();

        runtime.SetVisionTrainingRecording(true);
        ExpectBlocked(() => runtime.Apply(profile with { ScriptsEnabled = true }));
        ExpectBlocked(() => runtime.Apply(profile with { Name = "Another disabled profile" }));
        ExpectBlocked(() => runtime.SetScriptsEnabled(true));
        ExpectBlocked(() => runtime.StartRecording(RecordingChannels.All));
        ExpectBlocked(() => runtime.StartRecording(RecordingChannels.All, replacePending: true));
        runtime.SetScriptsEnabled(false); runtime.StopRecording(); runtime.DiscardRecording();
        runtime.StopOutput(stopScripts: true);
        Check(runtime.IsVisionTrainingRecording, "Stopping output silently ended the training guard.");
        runtime.Attach(new UsbipXboxOutput());
        var physical = new ControllerState((ushort)(ControlId.X | ControlId.A), 80, 95, 11000, -12000, 13000, -14000);
        input.SetState(physical, PaddleButtons.LargeRight);
        WaitUntil(() => runtime.Latest is { } sample && sample.Input.State == physical && sample.Output == physical,
            "Training changed physical controller input or allowed a paddle macro.");
        Check(!runtime.ScriptsEnabled && !runtime.Recording.IsRecording, "A blocked transition enabled automation.");
        Check(!runtime.SetVisionArmed(true), "A direct caller armed vision during training.");
        runtime.SetVisionTrainingRecording(false);
        Check(!runtime.ScriptsEnabled && !runtime.VisionStatus.Armed, "Finishing training automatically resumed assistance.");
        runtime.Apply(profile);
        runtime.StartRecording(RecordingChannels.All);
        Check(runtime.Recording.IsRecording, "Macro recording did not unlock after training.");
        runtime.StopRecording(); runtime.DiscardRecording();
        runtime.SetScriptsEnabled(true);
        Check(runtime.ScriptsEnabled, "Script enable did not unlock after training.");
        runtime.SetScriptsEnabled(false);

        // Competing public entry points must never both succeed, regardless of winner.
        for (int iteration = 0; iteration < 20; iteration++)
        {
            Race(runtime, () => runtime.SetScriptsEnabled(true), () => runtime.SetScriptsEnabled(false),
                () => runtime.ScriptsEnabled);
            Race(runtime, () => runtime.StartRecording(RecordingChannels.All),
                () => { runtime.StopRecording(); runtime.DiscardRecording(); }, () => runtime.Recording.IsRecording);
        }
        Console.WriteLine("SUCCESS: runtime training entry/enable/recording races, profile guards, manual passthrough, allowed stops and explicit resume verified with simulated devices.");
    }

    private static void Race(StudioRuntime runtime, Action competingTransition, Action cleanup, Func<bool> competingState)
    {
        runtime.SetVisionTrainingRecording(false);
        cleanup();
        using var start = new Barrier(2);
        bool trainingAccepted = false, transitionAccepted = false;
        Task training = Task.Run(() =>
        {
            start.SignalAndWait();
            try { runtime.SetVisionTrainingRecording(true); trainingAccepted = true; }
            catch (InvalidOperationException) { }
        });
        Task transition = Task.Run(() =>
        {
            start.SignalAndWait();
            try { competingTransition(); transitionAccepted = true; }
            catch (InvalidOperationException) { }
        });
        Task.WaitAll(training, transition);
        Check(trainingAccepted != transitionAccepted, "Training and a conflicting transition did not have exactly one winner.");
        Check(!(runtime.IsVisionTrainingRecording && competingState()), "Training raced with enabled automation.");
        runtime.SetVisionTrainingRecording(false);
        cleanup();
    }

    private static void ExpectBlocked(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("training", StringComparison.OrdinalIgnoreCase), "Blocked transition did not explain the training requirement.");
            return;
        }
        throw new InvalidOperationException("A conflicting runtime transition was allowed during training.");
    }

    private static void WaitUntil(Func<bool> condition, string reason)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException(reason);
            Thread.Sleep(1);
        }
    }

    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
