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
        TraceCaptureChecks();
        TraceBufferChecks();
    }

    private static void TraceCaptureChecks()
    {
        using var input = new EliteInputMonitor();
        var profile = StudioProfile.CreateDefault() with { ScriptsEnabled = false };
        var deadzone = new StickDeadzone(20, 20);
        using var runtime = new StudioRuntime(input, profile, deadzone);
        var output = new UsbipXboxOutput();
        runtime.Attach(output);
        runtime.SetVisionTrainingRecording(true);
        var trace = new VisionTrainingTrace();
        runtime.SetVisionTrainingTrace(trace);
        var physical = new ControllerState((ushort)(ControlId.X | ControlId.A), 73, 119,
            -2000, 3000, 1200, -1300);
        input.SetState(physical, PaddleButtons.LargeRight);
        WaitUntil(() => trace.Snapshot().Samples.Count(sample => sample.Physical == physical && sample.Connected) >= 3,
            "Training trace did not record repeated physical samples without a video/model loop.");
        Check(trace.Snapshot().Samples.Where(sample => sample.Physical == physical).All(sample =>
                sample.RequestedOutput == deadzone.Apply(physical) && sample.HeldPaddles == PaddleButtons.LargeRight
                && sample.OutputAvailable && sample.AcceptedForLocalHandoff),
            "Training trace lost raw axes/paddles or confused physical input with processed output.");

        input.SetConnection(false, 2);
        WaitUntil(() => trace.Snapshot().Samples.Any(sample => !sample.Connected && sample.ConnectionVersion == 2),
            "Training trace hid a physical-controller disconnection.");
        input.SetConnection(true, 3);
        WaitUntil(() => trace.Snapshot().Samples.Any(sample => sample.Connected && sample.ConnectionVersion == 3),
            "Training trace lost the new controller connection generation.");

        output.RejectWrites = true;
        WaitUntil(() => trace.Snapshot().Samples.Any(sample => sample.OutputAvailable && !sample.AcceptedForLocalHandoff),
            "A rejected local output was recorded as accepted.");
        WaitUntil(() => trace.Snapshot().Samples.Count(sample => !sample.OutputAvailable && !sample.AcceptedForLocalHandoff) >= 2,
            "Training trace did not distinguish unavailable output from accepted handoff.");

        runtime.SetVisionTrainingTrace(null);
        var detached = trace.Snapshot();
        var changed = physical with { RightX = 22000, RightY = -23000 };
        input.SetState(changed);
        WaitUntil(() => runtime.Latest?.Input.State == changed, "Polling did not continue after detaching the training trace.");
        Check(trace.StoredSamples == detached.Samples.Length && trace.DroppedSamples == detached.DroppedSamples,
            "The trace received a sample after detach returned.");
        Check(detached.Samples.Length >= 7 && detached.Samples.All(sample => sample.Kind == VisionTrainingTraceKind.Poll
                && sample.SampleTicks > 0 && sample.BeforeSubmitTicks >= sample.SampleTicks
                && sample.AfterSubmitTicks >= sample.BeforeSubmitTicks),
            "Training trace timestamps did not preserve the physical-sample/submission ordering.");
        Check(detached.Samples.Where(sample => !sample.Connected).All(sample => sample.RequestedOutput == default),
            "A disconnected sample claimed a nonneutral output.");
        runtime.SetVisionTrainingRecording(false);
        Console.WriteLine("SUCCESS: synchronized raw/processed controller trace, independent polling, paddles, connection changes, local output failure and detach fence verified.");
    }

    private static void TraceBufferChecks()
    {
        var trace = new VisionTrainingTrace(3);
        var sample = new VisionTrainingTraceSample(VisionTrainingTraceKind.Poll, 1, 2, 3,
            default, default, true, 7, PaddleButtons.None, PaddleButtons.None, true, true);
        trace.Record(sample); // Warm the producer before checking its allocation behavior.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++) trace.Record(sample);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "Appending bounded controller trace records allocated managed memory.");
        var snapshot = trace.Snapshot();
        Check(snapshot.Capacity == 3 && snapshot.Samples.Length == 3 && snapshot.DroppedSamples == 98,
            "Controller trace overflow did not retain its original bounded records and exact drop count.");
        Check(snapshot.Samples.All(value => value == sample), "Overflow overwrote an earlier controller sample.");

        var concurrent = new VisionTrainingTrace(1000);
        Parallel.For(1, 2001, index => concurrent.Record(sample with
        {
            SampleTicks = index, BeforeSubmitTicks = index + 1, AfterSubmitTicks = index + 2,
            ConnectionVersion = index
        }));
        var completed = concurrent.Snapshot();
        Check(completed.Samples.Length == 1000 && completed.DroppedSamples == 1000
                && completed.Samples.Select(value => value.SampleTicks).Distinct().Count() == 1000
                && completed.Samples.All(value => value.BeforeSubmitTicks == value.SampleTicks + 1
                    && value.AfterSubmitTicks == value.SampleTicks + 2 && value.ConnectionVersion == value.SampleTicks),
            "Concurrent controller trace producers published torn, duplicate or missing bounded slots.");
        Console.WriteLine("SUCCESS: controller trace zero-allocation append, bounded overflow, immutable slots and concurrent publication verified.");
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
