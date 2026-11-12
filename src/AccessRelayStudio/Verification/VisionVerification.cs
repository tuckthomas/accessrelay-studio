using System.IO;
using EliteScriptStudio.Core;

internal static class VisionVerification
{
    internal static void Run()
    {
        SettingsValidation();
        DeadlineAndLatch();
        ManualCancellationAndGeneration();
        PredictionRejectionAndFreshness();
        InvalidationsAndOwnership();
        TrainingInhibitsAuthoritativeOutput();
        SchedulerTimerAdapter();
    }

    private static void SettingsValidation()
    {
        VisionSettings.Default.Validate();
        (VisionSettings.Default with { CorrectionMs = -20 }).Validate();
        foreach (var settings in new[]
        {
            VisionSettings.Default with { Crop = new(double.NaN, 0, 1, 1) },
            VisionSettings.Default with { Crop = new(.5, 0, .6, 1) },
            VisionSettings.Default with { Crop = new(0, 0, 0, 1) },
            VisionSettings.Default with { ShootButton = ControlId.LeftTrigger },
            VisionSettings.Default with { ShootButton = ControlId.SmallLeft },
            VisionSettings.Default with { MinimumConfidence = double.NaN },
            VisionSettings.Default with { MaximumFrameAgeMs = 0 },
            VisionSettings.Default with { EstimatedInputDelayMs = -1 },
            VisionSettings.Default with { MaximumLatenessMs = double.PositiveInfinity },
            VisionSettings.Default with { ModelManifestPath = "bad\npath" }
        }) Expect<InvalidDataException>(settings.Validate);
    }

    private static void DeadlineAndLatch()
    {
        var clock = new FakeClock();
        var settings = VisionSettings.Default with { EstimatedVideoDelayMs = 25, EstimatedInputDelayMs = 5, CorrectionMs = 3 };
        var arbiter = Prepare(clock, settings);
        var physical = new ControllerState((ushort)(ControlId.X | ControlId.B), 21, 94, 100, -200, 300, -400);
        clock.Now = 100;
        Equal(physical, Sample(arbiter, clock, physical));
        var prediction = Predict(arbiter, 1, 105, 108, 100);
        clock.Now = 110;
        Check(arbiter.Submit(prediction, clock.Now), "A current prediction schedules a release.");
        Equal(178L, arbiter.DeadlineTimestamp!.Value); // Capture 105 + H 100 - V 25 - U 5 + C 3.
        clock.Now = 150;
        Sample(arbiter, clock, physical);
        Check(!arbiter.TryAdvanceDeadline(177), "A release cannot fire early.");
        Check(arbiter.TryAdvanceDeadline(178), "A due release can wake independently of the next input sample.");
        var released = physical with { Buttons = (ushort)ControlId.B };
        Equal(released, arbiter.Output);
        Equal(1L, arbiter.Status.AutomatedReleases);
        Check(!arbiter.TryAdvanceDeadline(178), "The same deadline cannot release twice.");
        clock.Now = 180;
        Equal(released, Sample(arbiter, clock, physical));
        Equal(VisionRunState.ReleasedLatched, arbiter.Status.State);
        // Configuration changes cannot reassert a physical hold that has already been released.
        arbiter.Configure(settings with { ShootButton = ControlId.Y });
        Equal(released, Sample(arbiter, clock, physical));
        clock.Now = 181;
        Equal(default(ControllerState), Sample(arbiter, clock, default));
        Equal(0L, arbiter.Status.ShotToken);
    }

    private static void ManualCancellationAndGeneration()
    {
        var clock = new FakeClock();
        var arbiter = Prepare(clock);
        var held = default(ControllerState) with { Buttons = (ushort)ControlId.X };
        clock.Now = 100;
        Sample(arbiter, clock, held);
        var old = Predict(arbiter, 1, 100, 100, 30);
        Check(arbiter.Submit(old, clock.Now), "Shot schedules.");
        clock.Now = 110;
        Equal(default(ControllerState), Sample(arbiter, clock, default));
        Check(arbiter.DeadlineTimestamp is null && !arbiter.TryAdvanceDeadline(130), "Early manual release cancels scheduling.");
        clock.Now = 120;
        Sample(arbiter, clock, held);
        Check(arbiter.Status.ShotToken != old.ShotToken, "Fresh physical press has a new shot token.");
        Check(!arbiter.Submit(old, clock.Now), "Old shot observations cannot affect the new shot.");
        var newer = Predict(arbiter, 2, 120, 120, 30);
        arbiter.Invalidate("Profile changed.", disarm: true);
        arbiter.BeginSession();
        Check(arbiter.SetArmed(true), "Ready source can rearm.");
        Sample(arbiter, clock, held);
        Equal(0L, arbiter.Status.ShotToken);
        Check(!arbiter.Submit(newer, clock.Now), "Session/profile invalidation rejects pending old observations.");
        clock.Now = 125;
        Sample(arbiter, clock, default);
        clock.Now = 126;
        Sample(arbiter, clock, held);
        Check(arbiter.Status.ShotToken != 0, "Releasing and pressing creates a fresh eligible attempt.");
    }

    private static void PredictionRejectionAndFreshness()
    {
        foreach (var mutate in new Func<VisionPrediction, VisionPrediction>[]
        {
            p => p with { Confidence = double.NaN }, p => p with { Confidence = .1 },
            p => p with { UncertaintyMs = 50 }, p => p with { UncertaintyMs = -1 },
            p => p with { PlayerTracked = false }, p => p with { TimeToReleaseMs = double.PositiveInfinity },
            p => p with { CaptureTimestamp = 99 }, p => p with { CompletedTimestamp = 101 }
        })
        {
            var clock = new FakeClock();
            var arbiter = Prepare(clock);
            clock.Now = 100;
            Sample(arbiter, clock, Held);
            Check(!arbiter.Submit(mutate(Predict(arbiter, 1, 100, 100, 20)), 100), "Invalid prediction is rejected.");
            Check(arbiter.DeadlineTimestamp is null && arbiter.Status.ShotToken == 0, "Invalid current-shot prediction cancels the attempt.");
            Equal(Held, arbiter.Output);
        }
        var time = new FakeClock();
        var ordering = Prepare(time);
        time.Now = 100;
        Sample(ordering, time, Held);
        var valid = Predict(ordering, 2, 100, 100, 20);
        Check(ordering.Submit(valid, 100), "Current sequence accepted.");
        Check(!ordering.Submit(valid with { FrameSequence = 1 }, 101), "Out-of-order frame rejected.");
        Equal(120L, ordering.DeadlineTimestamp!.Value);
        Check(!ordering.TryAdvanceDeadline(126), "Late scheduler wake-up does not fire an obsolete release.");
        Equal(1L, ordering.Status.MissedDeadlines);
        Equal(Held, ordering.Output);

        var stale = Prepare(time);
        time.Now = 200;
        Sample(stale, time, Held);
        Check(!stale.Submit(Predict(stale, 1, 200, 290, 200), 290), "Stale captured image cancels assistance.");
        var expired = Prepare(time);
        time.Now = 300;
        Sample(expired, time, Held);
        Check(!expired.Submit(Predict(expired, 1, 300, 320, 1), 320), "Already missed prediction is rejected.");
        Equal(1L, expired.Status.MissedDeadlines);

        var sampleStall = Prepare(time);
        time.Now = 400;
        Sample(sampleStall, time, Held);
        Check(sampleStall.Submit(Predict(sampleStall, 1, 400, 400, 60), 400), "Future prediction accepted.");
        Check(!sampleStall.TryAdvanceDeadline(460), "Release is skipped when physical input sampling stalled.");

        var frameStall = Prepare(time, VisionSettings.Default with { MaximumFrameAgeMs = 20 });
        time.Now = 500;
        Sample(frameStall, time, Held);
        Check(frameStall.Submit(Predict(frameStall, 1, 500, 500, 30), 500), "A near-future deadline can be scheduled.");
        time.Now = 525;
        Sample(frameStall, time, Held);
        Check(!frameStall.TryAdvanceDeadline(530), "A frozen video source cannot fire a deadline using an expired observation.");

        var slightlyLate = Prepare(time);
        time.Now = 600;
        Sample(slightlyLate, time, Held);
        Check(slightlyLate.Submit(Predict(slightlyLate, 1, 600, 605, 3), 605), "A bounded slightly-late prediction can be accepted.");
        Equal(603L, slightlyLate.DeadlineTimestamp!.Value);
        Check(slightlyLate.TryAdvanceDeadline(605), "Accepted original deadline is retained for lateness accounting.");

        var overflow = new ControllerOutputArbiter(long.MaxValue);
        overflow.Sample(default, default, true, true, false, 0);
        overflow.BeginSession();
        Check(overflow.SetArmed(true), "Large-frequency clock route can arm.");
        overflow.Sample(Held, Held, true, true, false, 1);
        Check(!overflow.Submit(Predict(overflow, 1, 1, 1, 5000), 1), "Unrepresentable absolute deadline is rejected without integer overflow.");
    }

    private static void InvalidationsAndOwnership()
    {
        foreach (string reason in new[] { "Profile changed", "Controller changed", "Output stopped", "Stop all", "Recording started" })
        {
            var clock = new FakeClock();
            var arbiter = Prepare(clock);
            clock.Now = 100;
            Sample(arbiter, clock, Held);
            var pending = Predict(arbiter, 1, 100, 100, 20);
            Check(arbiter.Submit(pending, 100), "Attempt prepared.");
            arbiter.Invalidate(reason, disarm: true);
            Check(!arbiter.TryAdvanceDeadline(120) && !arbiter.Submit(pending, 120), "Invalidated work cannot send output.");
        }
        var time = new FakeClock();
        var blocked = Prepare(time);
        time.Now = 100;
        blocked.Sample(Held, Held, true, true, true, time.Now);
        Check(blocked.Status.ShotToken == 0 && blocked.Status.State == VisionRunState.Suspended,
            "Conflicting automation suspends assistance.");
        Check(!blocked.SetArmed(true), "Cannot arm through an active conflict.");
        time.Now = 101;
        blocked.Sample(Held, Held, true, true, false, time.Now);
        Equal(0L, blocked.Status.ShotToken);
        time.Now = 102;
        blocked.Sample(default, default, true, true, false, time.Now);
        Check(blocked.SetArmed(true), "Can arm after conflict clears.");
        time.Now = 103;
        Sample(blocked, time, Held);
        Check(blocked.Status.ShotToken != 0, "Only a new physical press starts the attempt.");
        Equal(default(ControllerState), blocked.Sample(Held, Held, false, true, false, 104));
        Check(!blocked.TryAdvanceDeadline(200), "Disconnected controller cannot release later.");
    }

    private static ControllerState Held => default(ControllerState) with { Buttons = (ushort)ControlId.X };

    private static void SchedulerTimerAdapter()
    {
        foreach (bool failTimer in new[] { false, true })
        {
            var clock = new SchedulerClock { Now = 100, Deadline = 120 };
            var timer = new FakeDeadlineTimer { FailScheduling = failTimer };
            using var delivered = new ManualResetEventSlim();
            int releases = 0;
            long deliveredAt = -1;
            using (var scheduler = new ReleaseScheduler(
                () => Volatile.Read(ref clock.Deadline) is long deadline && deadline >= 0 ? deadline : null,
                now =>
                {
                    Interlocked.Exchange(ref clock.Deadline, -1);
                    Interlocked.Exchange(ref deliveredAt, now);
                    Interlocked.Increment(ref releases);
                    delivered.Set();
                }, timer, () => Volatile.Read(ref clock.Now), 1000))
            {
                Check(timer.Scheduled.WaitOne(5000), "A future deadline schedules the optional timer.");
                if (!failTimer)
                {
                    Volatile.Write(ref clock.Now, 110);
                    timer.Pulse.Set(); // Deliberately early timer signal.
                    Check(timer.Scheduled.WaitOne(5000), "An early wake rechecks the injected clock and reschedules.");
                    Equal(0, Volatile.Read(ref releases));
                }
                Volatile.Write(ref clock.Now, 120);
                if (failTimer) scheduler.Signal(); else timer.Pulse.Set();
                Check(delivered.Wait(5000), "Timer/fallback wake reaches the due callback.");
                Equal(120L, Interlocked.Read(ref deliveredAt));
                Equal(1, Volatile.Read(ref releases));
            }
            Check(timer.Disposed, "Scheduler disposes its adapter after joining the worker.");
        }

        var cancelledClock = new SchedulerClock { Now = 200, Deadline = 220 };
        var cancelledTimer = new FakeDeadlineTimer();
        int cancelledReleases = 0;
        using (var scheduler = new ReleaseScheduler(
            () => Volatile.Read(ref cancelledClock.Deadline) is long deadline && deadline >= 0 ? deadline : null,
            _ => Interlocked.Increment(ref cancelledReleases), cancelledTimer,
            () => Volatile.Read(ref cancelledClock.Now), 1000))
        {
            Check(cancelledTimer.Scheduled.WaitOne(5000), "Cancellation fixture schedules once.");
            Volatile.Write(ref cancelledClock.Deadline, -1);
            scheduler.Signal();
            Check(cancelledTimer.Cancelled.WaitOne(5000), "Removing a deadline cancels the optional timer.");
            Volatile.Write(ref cancelledClock.Now, 220);
            cancelledTimer.Pulse.Set();
        }
        Equal(0, Volatile.Read(ref cancelledReleases));
        Check(cancelledTimer.Disposed, "Cancelled adapter is released on shutdown.");
    }

    private sealed class SchedulerClock { internal long Now, Deadline; }
    private sealed class FakeDeadlineTimer : IReleaseDeadlineTimer
    {
        internal readonly AutoResetEvent Pulse = new(false), Scheduled = new(false), Cancelled = new(false);
        internal bool FailScheduling, Disposed;
        public WaitHandle WaitHandle => Pulse;
        public bool TrySchedule(long deadlineTimestamp) { Scheduled.Set(); return !FailScheduling; }
        public void Cancel() => Cancelled.Set();
        public void Dispose() { Disposed = true; Pulse.Dispose(); Scheduled.Dispose(); Cancelled.Dispose(); }
    }

    private static void TrainingInhibitsAuthoritativeOutput()
    {
        var clock = new FakeClock();
        var arbiter = Prepare(clock);
        clock.Now = 100;
        Sample(arbiter, clock, Held);
        var beforeTraining = Predict(arbiter, 1, 100, 100, 20);
        Check(arbiter.Submit(beforeTraining, 100), "Release scheduled before training.");
        long generation = arbiter.Status.SessionGeneration;
        arbiter.SetTrainingRecording(true); // No new physical input sample has occurred.
        Check(!arbiter.Status.Armed && arbiter.DeadlineTimestamp is null, "Training start immediately cancels and disarms pending output.");
        Check(arbiter.Status.SessionGeneration != generation, "Training invalidates in-flight prediction tokens.");
        Check(!arbiter.TryAdvanceDeadline(120), "An already queued deadline cannot release during recording.");
        Check(!arbiter.SetArmed(false) && !arbiter.SetArmed(true), "UI toggle attempts cannot rearm during recording.");
        Check(!arbiter.Submit(beforeTraining, 100), "An in-flight inference result is rejected before another physical poll.");
        arbiter.BeginSession(); // Model/source readiness must not bypass the training inhibition.
        Check(!arbiter.SetArmed(true), "Fresh model readiness cannot bypass active training.");
        clock.Now = 110;
        Equal(Held, Sample(arbiter, clock, Held));
        clock.Now = 111;
        Sample(arbiter, clock, default);
        clock.Now = 112;
        Sample(arbiter, clock, Held);
        Check(arbiter.Status.ShotToken == 0, "Physical edges cannot create assisted shots during recording.");
        arbiter.SetTrainingRecording(false);
        Check(!arbiter.Status.Armed && arbiter.DeadlineTimestamp is null, "Training completion never restores the previous armed state.");
        clock.Now = 113;
        Sample(arbiter, clock, Held);
        Check(arbiter.SetArmed(true), "Explicit arming is accepted after recording stops and the route is eligible.");
        clock.Now = 114;
        Sample(arbiter, clock, Held);
        Check(arbiter.Status.ShotToken == 0, "A held button from training cannot become a new assisted shot.");
        clock.Now = 115;
        Sample(arbiter, clock, default);
        clock.Now = 116;
        Sample(arbiter, clock, Held);
        Check(arbiter.Status.ShotToken != 0, "Explicit arm plus physical release/new press permits a fresh attempt.");
        Check(!arbiter.Submit(beforeTraining, 116), "Pre-training tokens stay invalid after rearming.");
    }
    private static ControllerOutputArbiter Prepare(FakeClock clock, VisionSettings? settings = null)
    {
        var arbiter = new ControllerOutputArbiter(clock.Frequency);
        arbiter.Configure(settings ?? VisionSettings.Default);
        Sample(arbiter, clock, default);
        arbiter.BeginSession();
        Check(arbiter.SetArmed(true), "Validated source and connected route can arm.");
        Sample(arbiter, clock, default);
        return arbiter;
    }
    private static ControllerState Sample(ControllerOutputArbiter arbiter, FakeClock clock, ControllerState state) =>
        arbiter.Sample(state, state, true, true, false, clock.Now);
    private static VisionPrediction Predict(ControllerOutputArbiter arbiter, long sequence, long captured, long completed, double horizon) =>
        new(arbiter.Status.SessionGeneration, arbiter.Status.ShotToken, sequence, captured, completed, horizon, .99, 1, true);
    private sealed class FakeClock { public long Now; public long Frequency => 1000; }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
