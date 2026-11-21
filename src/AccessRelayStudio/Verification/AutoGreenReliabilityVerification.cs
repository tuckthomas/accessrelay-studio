using EliteScriptStudio.Core;

internal static class AutoGreenReliabilityVerification
{
    internal static void Run()
    {
        AssignedControls();
        TapActivation();
        LateStrokeAndActualHandoff();
        CancelledPaddleHold();
        BannerParsing();
        StreamStatistics();
        LearningEvidence();
        SoloFeedback();
        LiveTempoFeedback();
        RepeatedBanner();
        Console.WriteLine("SUCCESS Auto-green reliability: lead time, actual handoffs, cancellations, banner association, stream health and learning evidence.");
    }
    private static void AssignedControls()
    {
        var action = new VisionControllerAction { Kind = VisionActionKind.RightStickStroke, StrokeDurationMs = 40, EndHoldMs = 6 };
        foreach (var id in ControlMap.All)
        {
            uint bit = (uint)id;
            var input = new ControllerState((ushort)bit, id == ControlId.LeftTrigger ? (byte)30 : (byte)0,
                id == ControlId.RightTrigger ? (byte)30 : (byte)0, 18000, -22000, 0, 0);
            var paddles = (PaddleButtons)(bit >> 18);
            Run(action with { TriggerControl = id }, input, paddles);
        }
        foreach (var direction in Enum.GetValues<StickDirection>())
        {
            var input = direction switch
            {
                StickDirection.LeftUp => new ControllerState { LeftY = 20000 },
                StickDirection.LeftDown => new ControllerState { LeftY = -20000 },
                StickDirection.LeftLeft => new ControllerState { LeftX = -20000 },
                StickDirection.LeftRight => new ControllerState { LeftX = 20000 },
                StickDirection.RightUp => new ControllerState { RightY = 20000 },
                StickDirection.RightDown => new ControllerState { RightY = -20000 },
                StickDirection.RightLeft => new ControllerState { RightX = -20000 },
                _ => new ControllerState { RightX = 20000 }
            };
            Run(action with { TriggerDirection = direction }, input, PaddleButtons.None);
        }

        static void Run(VisionControllerAction binding, ControllerState input, PaddleButtons paddles)
        {
            binding.Validate();
            var arbiter = new ControllerOutputArbiter(1000);
            arbiter.Configure(VisionSettings.Default with { EstimatedVideoDelayMs = 0, EstimatedInputDelayMs = 0, CorrectionMs = 0 }, binding);
            arbiter.BeginSession();
            arbiter.Sample(default, default, true, true, false, 1);
            Check(arbiter.SetArmed(true) && !binding.IsTriggerHeld(default, 0), "Neutral input triggered assistance.");
            var manual = binding.WithoutAssignedTrigger(input);
            Check(manual.LeftX == input.LeftX && manual.LeftY == input.LeftY, "The trigger consumed player movement.");
            if (binding.TriggerControl is { } id)
                Check(!ControlMap.IsPressed(id, manual, paddles & ~binding.ReservedPaddles), "The trigger also activated its ordinary binding.");
            Check(arbiter.Sample(input, manual, true, true, false, 20, 19, paddles).RightY == short.MinValue &&
                arbiter.Status.ShotToken != 0, "Assigned control did not open the shot.");
            arbiter.Sample(default, default, true, true, false, 101);
            Check(arbiter.LastAttempt is { Outcome: VisionActionOutcome.Cancelled, TriggerSample: { } press } &&
                press.Timestamp == 19 && press.LeftX == input.LeftX && press.LeftY == input.LeftY,
                "Release lost the original movement and press timestamp.");

            arbiter.Sample(input, manual, true, true, false, 200, 200, paddles);
            Check(arbiter.Submit(Prediction(arbiter, 1, 200, 50), 200), "Assigned trigger rejected the timing forecast.");
            bool reachedUp = false;
            for (int t = 202; t <= 302; t += 2)
            {
                var sent = arbiter.Sample(input, manual, true, true, false, t, t, paddles);
                arbiter.RecordOutputSubmitted(sent, t);
                reachedUp |= sent.RightY == short.MaxValue;
            }
            Check(reachedUp && arbiter.LastAction is { Outcome: VisionActionOutcome.Completed, TriggerSample: { Timestamp: 200 } },
                "Assigned trigger did not complete its own down-to-up stroke.");
        }
    }

    private static void TapActivation()
    {
        var stroke = new VisionControllerAction { Kind = VisionActionKind.RightStickStroke, TriggerControl = ControlId.LargeRight,
            ContinueAfterTriggerRelease = true, StrokeDurationMs = 40, EndHoldMs = 6 };
        var down = new ControllerState { RightY = -20000 };
        var button = new ControllerState { Buttons = (ushort)VisionSettings.Default.ShootButton };
        foreach (var action in new[] { stroke, stroke with { TriggerControl = null, TriggerDirection = StickDirection.RightDown },
            new VisionControllerAction { Kind = VisionActionKind.ButtonRelease, ContinueAfterTriggerRelease = true } })
        {
            var a = new ControllerOutputArbiter(1000);
            a.Configure(VisionSettings.Default with { EstimatedVideoDelayMs = 0, EstimatedInputDelayMs = 0 }, action);
            a.BeginSession(); a.Sample(default, default, true, true, false, 1);
            Check(a.SetArmed(true), "Cannot arm tap activation.");
            bool isButton = action.Kind == VisionActionKind.ButtonRelease;
            var input = isButton ? button : action.TriggerDirection.HasValue ? down : default;
            var paddle = action.TriggerControl.HasValue ? PaddleButtons.LargeRight : PaddleButtons.None;
            ControllerState Sample(int t, bool pressed, bool pulseOnly = false) =>
                a.Sample(pressed ? input : default, pressed ? action.WithoutAssignedTrigger(input) : default,
                    true, true, false, t, t, pressed && !pulseOnly ? paddle : 0, pressed ? paddle : 0);
            bool IsDown(ControllerState state) => isButton ? (state.Buttons & button.Buttons) != 0 : state.RightY == short.MinValue;
            Check(IsDown(Sample(20, true, pulseOnly: true)) && IsDown(Sample(22, false)), "Short tap lost virtual down.");
            long original = a.Status.ShotToken;
            Check(original > 0 && !a.Status.ShootHeld && a.HoldStartedTimestamp == 0 && a.TriggerSample?.Timestamp == 20,
                "A tap did not separate the physical release from the active shot.");
            Sample(24, true); Sample(26, false);
            Check(a.Status.ShotToken == original && a.TriggerSample?.Timestamp == 20, "A second tap replaced the active shot.");
            Check(a.Submit(Prediction(a, 1, 30, 48), 30), "A released trigger rejected its meter forecast.");
            bool reachedUp = false;
            for (int t = 32; t <= 124; t += 2)
            {
                var sent = Sample(t, false);
                a.RecordOutputSubmitted(sent, t);
                reachedUp |= isButton ? !IsDown(sent) : sent.RightY == short.MaxValue;
            }
            Check(reachedUp && a.LastAttempt is { Id: 1, EligibleForFeedback: true, TriggerSample.Timestamp: 20 }
                && a.Status.ShotToken == 0 && a.Output == default, "Tap did not finish exactly one action and return to rest.");
            Sample(130, true);
            Check(a.Status.ShotToken != 0 && a.Submit(Prediction(a, 1, 130, 48), 130), "Released tap did not rearm.");
            for (int t = 132; t <= 250; t += 2) a.RecordOutputSubmitted(Sample(t, true), t);
            Check(a.LastAttempt?.Id == 2 && a.Status.ShotToken == 0, "Sustained activation fired twice.");
            if (action.TriggerDirection.HasValue)
            {
                a.Sample(down with { RightY = -12000 }, default, true, true, false, 252);
                Sample(254, true);
                Check(a.Status.ShotToken == 0, "Wavering at the stick threshold fired a second shot.");
            }
            Sample(260, false); Sample(262, true); Sample(264, false);
            a.SetArmed(false);
            Check(a.LastAttempt is { Id: 3, Outcome: VisionActionOutcome.Cancelled } && a.Output == default, "Stop failed to cancel a tap.");
            a.SetArmed(true); Sample(270, true); Sample(272, false);
            a.Sample(default, default, false, false, false, 274);
            Check(a.LastAttempt is { Id: 4, Outcome: VisionActionOutcome.Cancelled } && a.Output == default, "Disconnected tap retained output.");
        }
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
        double previousLead = valid.MinimumStrokeLeadMs;
        Check(valid.StageFeedbackTiming(receipt, -4) && valid.MinimumStrokeLeadMs == previousLead, "Feedback moved a held shot.");
        Poll(valid, 210, false);
        Check(valid.MinimumStrokeLeadMs == previousLead + 4, "The next press did not receive the scheduler's new lead.");
        Poll(valid, 215); Poll(valid, 225, false);
        Check(valid.LastAttempt is { Outcome: VisionActionOutcome.Cancelled, AnchorTimestamp: 0, ActualStartedTimestamp: 0, ActualAnchorTimestamp: 0 },
            "A cancelled press inherited its predecessor's planned or actual anchor.");
        var unobserved = Paddle();
        unobserved.Submit(Prediction(unobserved, 1, 100, 48), 100);
        for (int t = 100; t <= 206; t += 2) unobserved.Sample(default, default, true, true, false, t, heldPaddles: PaddleButtons.LargeRight);
        Check(unobserved.LastAction is { EligibleForFeedback: false, ActualAnchorTimestamp: 0 }, "An unobserved output claimed successful handoff timing.");
        var withdrawn = Paddle();
        Check(withdrawn.Submit(Prediction(withdrawn, 1, 100, 90), 100), "The test stroke did not schedule.");
        Check(withdrawn.Submit(Prediction(withdrawn, 2, 116, 0) with { WithdrawPendingForecast = true }, 116),
            "A contradicted forecast could not withdraw before stroke start.");
        for (int t = 118; t < 205; t += 2) Poll(withdrawn, t);
        Check(withdrawn.DeadlineTimestamp == null && withdrawn.Output.RightY == short.MinValue && withdrawn.LastAttempt == null,
            "A withdrawn forecast produced output or released the user's hold.");
        Check(withdrawn.Submit(Prediction(withdrawn, 3, 206, 68), 206), "The same physical shot could not qualify on new evidence.");
        for (int t = 208; t < 350; t += 2) Poll(withdrawn, t);
        Check(withdrawn.LastAction is { Id: 1, ActualAnchorTimestamp: > 0 }, "A replacement forecast did not produce exactly one stroke.");
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
        Check(direct.Assess(3000, 3200, 12, true).State == "unstable", "An incomplete native sample hid observed packet loss.");
        direct.Reset(); direct.Add(sample with { CaptureTimestamp = 3000, DecodeMs = null });
        Check(direct.Assess(3000, 3200, 12, true).State == "unavailable", "Missing decoder statistics permitted adaptation.");
        direct.Reset();
        direct.Add(sample with { CaptureTimestamp = 1000, Fps = 60, JitterMs = .1, DecodeMs = 17.1, PacketLossPercent = 0 });
        direct.Add(sample with { CaptureTimestamp = 2000, Fps = 59.9, JitterMs = .1, DecodeMs = 17.2, PacketLossPercent = 0 });
        Check(direct.Assess(1800, 2100, 12, true).State == "stable", "A steady pipelined Xbox decoder blocked every meter forecast.");
        direct.Add(sample with { CaptureTimestamp = 3000, Fps = 60, JitterMs = .1, DecodeMs = 45, PacketLossPercent = 0 });
        Check(direct.Assess(2900, 3200, 12, true).State == "unstable", "A stalled native decoder permitted adaptation.");
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
        var receipt = new VisionActionReceipt(1, 1, 1, 10, 20, 30, 56, .85, "key", "run") { EligibleForFeedback = true,
            StreamTelemetry = new("stable", "", true, 1, null, 2) };
        Check(gate.Check(receipt, read with { RunId = "previous" }) != null, "An earlier session supplied this shot's grade.");
        Check(gate.Check(receipt, read) == null, "A valid HUD grade could not qualify.");
        Check(gate.Check(receipt with { Id = 2 }, read) != null, "A previous shot supplied this shot's distance.");
        var next = read with { ReceiptId = 2, Distance = "12 ft", Coverage = null };
        Check(gate.Check(receipt with { Id = 2 }, next) == null, "Moving to another distance or missing coverage blocked feedback.");
        Check(gate.Check(receipt with { Id = 2 }, next) != null, "Duplicate receipt counted as new learning evidence.");
        Check(gate.Check(receipt with { Id = 3 }, read with { ReceiptId = 3, Distance = null }) == null, "An unreadable distance discarded a qualified grade.");
        Check(gate.Check(receipt with { Id = 4, Outcome = VisionActionOutcome.Cancelled }, read with { ReceiptId = 4 }) != null, "Cancelled shot qualified for adaptation.");
        Check(gate.Check(receipt with { Id = 5, StreamTelemetry = new("unstable", "spike", false, 30, null, 2) }, read with { ReceiptId = 5 }) == "spike",
            "Unstable stream qualified for adaptation.");
    }
    private static void SoloFeedback()
    {
        var read = new OcrFeedbackRead(1, "run", "rhythm", "late", "none", "", 5, 1, "24'3");
        int direction = 0, tempo = 0, excellent = 0;
        AdaptiveFeedbackResult Step(OcrFeedbackRead result) => ShotFeedbackAnalysis.ComputeAdaptiveFeedback(2, 6, result, false, ref direction, ref tempo, ref excellent, true);
        Check(!Step(read).Changed, "One Late changed the correction.");
        var second = Step(read with { ReceiptId = 2, Distance = "26'7" });
        Check(second.Timing.NextCorrectionMs < 2 && second.Tempo.NextCorrectionMs == 6, "Repeated Late did not advance timing independently of tempo.");
        var key = new string('a', 64);
        var settings = VisionSettings.Default with { CorrectionMs = 2, PracticeTimingCorrections = new() { [key + "/20"] = -18 },
            PluginAssistance = System.Text.Json.JsonSerializer.SerializeToElement(new { confirmed = true, practiceDistanceFeet = 20 }) };
        Check(settings.FeedbackCorrection(key) == 2, "A historical distance-specific offset was applied to every shot.");
        var changed = settings.WithFeedbackCorrection(key, second.Timing.NextCorrectionMs);
        changed = System.Text.Json.JsonSerializer.Deserialize<VisionSettings>(System.Text.Json.JsonSerializer.Serialize(changed))!;
        changed.Validate();
        Check(changed.FeedbackCorrection(key) == second.Timing.NextCorrectionMs && changed.FeedbackCorrection("different-action") == 2
            && changed.PracticeTimingCorrections?[key + "/20"] == -18 && changed.CorrectionMs == 2,
            "Reloading changed another action, legacy history, or the confirmed baseline.");
        var trial = changed with { PluginAssistance = System.Text.Json.JsonSerializer.SerializeToElement(new { confirmed = true, calibrationTrial = true }) };
        Check(trial.FeedbackCorrection(key) == 2, "A new calibration trial inherited an automatic timing offset.");
    }
    private static void LiveTempoFeedback()
    {
        var arbiter = new ControllerOutputArbiter(1000);
        arbiter.Configure(VisionSettings.Default with { CorrectionMs = -4, TempoCorrectionMs = 6 },
            new() { Kind = VisionActionKind.RightStickStroke, TriggerControl = ControlId.LargeRight,
                ContinueAfterTriggerRelease = true, StrokeDurationMs = 48, EndHoldMs = 6 });
        arbiter.BeginSession(); Poll(arbiter, 1, false);
        Check(arbiter.SetArmed(true), "Could not start calibrated assistance.");

        VisionActionReceipt Tap(int at)
        {
            Poll(arbiter, at); Poll(arbiter, at + 2, false);
            Check(arbiter.Submit(Prediction(arbiter, 1, at + 4, 75), at + 4), "Next P4 tap lost its forecast.");
            for (int t = at + 6; t <= at + 140; t += 2) Poll(arbiter, t, false);
            return arbiter.LastAction ?? throw new Exception("P4 tap did not complete its stroke.");
        }
        int timing = 0, tempo = 0, excellent = 0;
        AdaptiveFeedbackResult Grade(VisionActionReceipt shot, string speed, string grade) =>
            ShotFeedbackAnalysis.ComputeAdaptiveFeedback(shot.CorrectionMs, shot.TempoCorrectionMs,
                new(shot.Id, shot.RunId, "rhythm", grade, speed, "", shot.CompletedTimestamp, 1), false,
                ref timing, ref tempo, ref excellent, liveAssistance: true);

        var first = Grade(Tap(100), "slow", "late");
        Check(!first.Changed && first.SuggestedPracticeTempoMs is null, "One Slow/Late grade changed the setup.");
        var second = Tap(300);
        var update = Grade(second, "slow", "late");
        Check(second.Id == 2 && update.Timing.NextCorrectionMs == -8 && update.Tempo.NextCorrectionMs == 6
            && update.SuggestedPracticeTempoMs == 0 && arbiter.StageFeedbackTiming(second, update.Timing.NextCorrectionMs),
            "Two Slow/Late grades failed to stage timing independently of the calibrated tempo.");
        var third = Tap(500);
        Check(arbiter.Status.Armed && third is { Id: 3, EligibleForFeedback: true, CorrectionMs: -8, TempoCorrectionMs: 6, StrokeDurationMs: 54 },
            "The next tap lost assistance or used an unvalidated stroke duration.");
        Grade(third, "fast", "excellent");
        var fourth = Grade(Tap(700), "fast", "excellent");
        Check(!fourth.Changed && fourth.SuggestedPracticeTempoMs == 12 && fourth.Tempo.NextCorrectionMs == 6,
            "Tempo-only feedback changed a calibrated stroke or discarded the practice suggestion.");
    }
    private static void RepeatedBanner()
    {
        var seq = new ShotFeedbackBannerSequence();
        var late = new ShotFeedbackAnalysis.ParsedBanner("rhythm", "late", "none", "RHYTHM LATE", null, "23'9");
        Check(seq.Observe(1, late, true, true) == null && seq.Observe(2, late, true, true) == null, "An existing HUD was credited on startup.");
        seq.Observe(3, null, false, false); seq.Observe(4, null, false, false);
        Check(seq.Observe(5, late, true, true) == null, "A search outside the HUD manufactured a wipe.");
        seq.ConfirmClear(6); seq.Observe(7, late, true, true);
        Check(seq.Observe(8, late, true, true)?.StartedAt == 7 && seq.Observe(9, late, true, true) == null,
            "A repeated grade and distance were lost or counted twice.");
    }
    private static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
}
