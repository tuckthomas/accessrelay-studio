using EliteScriptStudio.Core;

internal static class AutoGreenReliabilityVerification
{
    internal static void Run()
    {
        LateStrokeAndActualHandoff();
        CancelledPaddleHold();
        BannerParsing();
        StreamStatistics();
        LearningEvidence();
        Console.WriteLine("SUCCESS Auto-green reliability: lead time, actual handoffs, cancellations, banner association, stream health and learning evidence.");
    }
    private static ControllerOutputArbiter Paddle(double maxDuration = 5000)
    {
        var arbiter = new ControllerOutputArbiter(1000);
        arbiter.Configure(VisionSettings.Default with { MinimumConfidence = .65, MaximumUncertaintyMs = 12, MaximumShotDurationMs = maxDuration },
            new() { Kind = VisionActionKind.RightStickStroke, TriggerPaddle = PaddleButtons.LargeRight, StrokeDurationMs = 56, AnchorFraction = .85, EndHoldMs = 48 });
        arbiter.BeginSession(); arbiter.Sample(default, default, true, true, false, 1);
        Check(arbiter.SetArmed(true), "Could not arm fixture.");
        Poll(arbiter, 100);
        return arbiter;
    }
    private static void Poll(ControllerOutputArbiter a, long t, bool held = true)
    {
        a.Sample(default, default, true, true, false, t, heldPaddles: held ? PaddleButtons.LargeRight : PaddleButtons.None);
        a.RecordOutputSubmitted(a.Output, t + 1);
    }
    private static VisionPrediction Prediction(ControllerOutputArbiter a, long seq, long t, double tau) =>
        new(a.Status.SessionGeneration, a.Status.ShotToken, seq, t, t, tau, .95, 2, true);
    private static void LateStrokeAndActualHandoff()
    {
        var late = Paddle();
        Check(!late.Submit(Prediction(late, 1, 100, 20), 100), "Late forecast still clamped the full stroke to now.");
        Check(late.LastAction is null && late.LastAttempt?.Outcome == VisionActionOutcome.Cancelled, "Rejected stroke masqueraded as completion.");
        var valid = Paddle();
        Check(valid.Submit(Prediction(valid, 1, 100, 48), 100), "A full feasible stroke was rejected.");
        for (int t = 100; t <= 206; t += 2) Poll(valid, t);
        var receipt = valid.LastAction ?? throw new Exception("No valid stroke receipt.");
        Check(receipt.AnchorTimestamp == 148 && receipt.ActualAnchorTimestamp == 149 && receipt.ActualStartedTimestamp == 101
            && receipt.AnchorLatenessMs == 1 && receipt.EligibleForFeedback, "Receipt did not distinguish planned anchor from successful output handoff.");
        var unobserved = Paddle();
        unobserved.Submit(Prediction(unobserved, 1, 100, 48), 100);
        for (int t = 100; t <= 206; t += 2) unobserved.Sample(default, default, true, true, false, t, heldPaddles: PaddleButtons.LargeRight);
        Check(unobserved.LastAction is { EligibleForFeedback: false, ActualAnchorTimestamp: 0 }, "An unobserved output claimed successful handoff timing.");
    }
    private static void CancelledPaddleHold()
    {
        var a = Paddle(200);
        a.Submit(Prediction(a, 1, 100, 100) with { PlayerTracked = false }, 100);
        Poll(a, 120);
        Check(a.Output.RightY == short.MinValue && a.Status.HoldingCancelledPaddleShot, "Tracking failure released the still-held paddle shot.");
        Check(a.Status.CancelledAttempts == 1 && a.LastAction is null, "Cancellation was not recorded separately.");
        Check(!a.TryAdjustTiming(s => s with { CorrectionMs = 4 }), "Cancelled held input allowed tuning.");
        Poll(a, 130, false);
        Check(a.Output.RightY == 0 && !a.Status.HoldingCancelledPaddleShot, "Physical release failed to end cancelled hold.");
        var watchdog = Paddle(200);
        watchdog.Submit(Prediction(watchdog, 1, 100, 10), 100);
        Poll(watchdog, 302);
        Check(watchdog.Output.RightY == 0 && !watchdog.Status.HoldingCancelledPaddleShot, "Cancelled down hold outlived its watchdog.");
        var stopped = Paddle(); stopped.Submit(Prediction(stopped, 1, 100, 10), 100); stopped.SetArmed(false);
        Check(stopped.Output.RightY == 0, "Disarming did not override cancelled hold.");
        var moving = Paddle(); moving.Submit(Prediction(moving, 1, 100, 60), 100);
        for (int t = 102; t <= 130; t += 2) Poll(moving, t);
        Check(moving.Output.RightY != short.MinValue, "Stroke never started.");
        moving.Submit(Prediction(moving, 2, 130, 30) with { PlayerTracked = false }, 130);
        Check(moving.Output.RightY == 0 && !moving.Status.HoldingCancelledPaddleShot, "Mid-stroke cancellation reversed back down.");
    }
    private static void BannerParsing()
    {
        VisionTextRegion L(string text, double y, double x = .4, double confidence = .95) => new(text, confidence, new(x, y, .15, .03));
        Check(ShotFeedbackAnalysis.ParseFeedback([L("EXCELLENT", .1)]) is null, "Unheaded Excellent fabricated rhythm evidence.");
        var swapped = ShotFeedbackAnalysis.ParseFeedback([L("TEMPO EXCELLENT", .1), L("TIMING LATE", .2)]);
        Check(swapped is { Timing: "late", Tempo: "excellent", ShotType: "rhythm" }, "Tempo Excellent was mistaken for timing Excellent.");
        Check(ShotFeedbackAnalysis.ParseFeedback([L("TEMPO EXCELLENT / TIMING LATE", .1)]) is { Timing: "late", Tempo: "excellent" },
            "Combined OCR columns confused heading roles.");
        Check(ShotFeedbackAnalysis.ParseFeedback([L("RHYTHM LATE EXCELLENT", .1)]) is null, "Conflicting grades in one OCR line were accepted.");
        var distant = ShotFeedbackAnalysis.ParseFeedback([L("RHYTHM FAST LATE", .1), L("EXCELLENT", .6)]);
        Check(distant is { Timing: "late", Tempo: "fast" }, "A distant grade contaminated this banner.");
        Check(ShotFeedbackAnalysis.ParseFeedback([L("RHYTHM", .1), L("LATE", .14), L("EXCELLENT", .18)]) is null,
            "Contradictory nearby grades were accepted.");
        Check(ShotFeedbackAnalysis.ParseFeedback([L("RHYTHM", .1, confidence: .6), L("EXCELLENT", .14)]) is null,
            "Low confidence heading was accepted.");
        Check(ShotFeedbackAnalysis.ParseFeedback([L("RHYTHM", .1), L("FAST LAT3", .14)]) is { Timing: "late", Tempo: "fast" },
            "Combined tempo/timing line failed.");
        Check(ShotFeedbackAnalysis.ParseFeedback([L("RHYTHM", .1), L("EXCELLENT", .14), L("FAST", .14, .6)]) is { Timing: "excellent", Tempo: "fast" },
            "Separate timing and tempo grades failed.");
    }
    private static void StreamStatistics()
    {
        var parsed = StreamTelemetryParser.Parse("FPS: 60 | Ping: 12.5 ms | Jitter: 1,2 ms | Decode: 2.5 ms | Packet loss: 0%", 1000);
        Check(parsed is { Fps: 60, PingMs: 12.5, JitterMs: 1.2, DecodeMs: 2.5, PacketLossPercent: 0 }, "Labelled overlay values failed.");
        Check(StreamTelemetryParser.Parse("60 FPS | 12 ms Ping | 1 ms Jitter | 2 ms Decode", 1000) is { Fps: 60, PingMs: 12 }, "Value-before-label overlay failed.");
        Check(StreamTelemetryParser.Parse("FPS 60 Ping 20 Jitter 1", 1000) is null, "Missing decode fabricated a measurement.");
        Check(StreamTelemetryParser.Parse("FPS 60 Ping 20 Ping 80 Jitter 1 Decode 2", 1000) is null, "Conflicting readings were accepted.");
        var window = new StreamTelemetryWindow(1000);
        Check(window.Assess(100, 100, 12, true) is { State: "unavailable", CanAdapt: false }, "Missing statistics looked healthy.");
        window.Add(parsed!);
        window.Add(parsed! with { CaptureTimestamp = 1800 });
        Check(window.Assess(1800, 2000, 12, true) is { State: "stable", CanAdapt: true }, "Stable readings could not qualify.");
        window.Add(parsed! with { CaptureTimestamp = 2600, PingMs = 80 });
        Check(window.Assess(2500, 2700, 12, true) is { State: "unstable", CanAdapt: false }, "Latency spike qualified for tuning.");
        Check(window.Assess(6000, 6000, 12, true) is { State: "unavailable", CanAdapt: false }, "Stale statistics stayed healthy.");
        var direct = new StreamTelemetryWindow(1000);
        var sample = new DirectStreamMetrics(1000, null, null, 2, 4, 60, null, null, 0, null, 1920, 1080);
        direct.Add(sample); direct.Add(sample with { CaptureTimestamp = 1800 });
        Check(direct.Assess(1800, 2100, 12, true) is { CanAdapt: true, Latest: null, Direct.RttMs: null, Direct.Width: 1920 },
            "Missing RTT hid measured native statistics or fabricated overlay evidence.");
        direct.Add(sample with { CaptureTimestamp = 2100, PacketLossPercent = 2 });
        Check(!direct.Assess(2100, 2200, 12, true).CanAdapt, "Native packet loss permitted adaptation.");
        direct.Add(sample with { CaptureTimestamp = 3000, DecodeMs = null });
        Check(direct.Assess(3000, 3200, 12, true).State == "unavailable", "Missing decoder statistics permitted adaptation.");
        var combined = Paddle();
        combined.ObserveStreamTelemetry(parsed! with { CaptureTimestamp = 20, JitterMs = 8 });
        combined.ObserveStreamTelemetry(parsed! with { CaptureTimestamp = 80, JitterMs = 8 });
        Check(!combined.Submit(Prediction(combined, 1, 100, 100) with { UncertaintyMs = 10 }, 100),
            "Observed jitter was not combined with model uncertainty.");
        var future = new StreamTelemetryWindow(1000); future.Add(parsed! with { CaptureTimestamp = 10000 });
        Check(future.Assess(1000, 2000, 12, true).Latest is null, "Future evidence contaminated a shot.");
    }
    private static void LearningEvidence()
    {
        var gate = new ShotFeedbackLearningGate();
        var read = new OcrFeedbackRead(1, "run", "rhythm", "late", "none", "RHYTHM LATE", 100, 1, "25'6", "Open");
        var receipt = new VisionActionReceipt(1, 1, 1, 10, 20, 30, 56, .85, "key") { EligibleForFeedback = true,
            StreamTelemetry = new("stable", "", true, 1, null, 2) };
        Check(gate.Check(receipt, read, "profile") is not null && gate.Check(receipt with { Id = 2 }, read with { ReceiptId = 2 }, "profile") is null,
            "Reliable repeated context could not qualify.");
        Check(gate.Check(receipt with { Id = 3 }, read with { Distance = "12 ft" }, "profile") is not null, "Different shot distance shared its correction streak.");
        Check(gate.Check(receipt with { Id = 4 }, read with { Distance = "12 ft" }, "profile") is not null, "Repeated different context changed the shared correction.");
        Check(gate.Check(receipt with { Id = 4 }, read, "profile") is not null, "Duplicate receipt counted as new learning evidence.");
        Check(gate.Check(receipt with { Id = 5, Outcome = VisionActionOutcome.Cancelled }, read, "profile") is not null, "Cancelled shot qualified for adaptation.");
        Check(gate.Check(receipt with { Id = 6, StreamTelemetry = new("unstable", "spike", false, 30, null, 2) }, read, "profile") == "spike",
            "Unstable stream qualified for adaptation.");
    }
    private static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
}
