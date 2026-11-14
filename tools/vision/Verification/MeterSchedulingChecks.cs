using EliteScriptStudio.Core;

internal static class MeterSchedulingChecks
{
    private static readonly ControllerState Down = new() { RightY = short.MinValue };
    private static readonly VisionSettings Settings = VisionSettings.Default with { EstimatedVideoDelayMs = 20, EstimatedInputDelayMs = 10 };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ControllerOutputArbiter Ready(VisionControllerAction? action = null)
    {
        var arbiter = new ControllerOutputArbiter(1000);
        arbiter.Configure(Settings, action ?? new() { Kind = VisionActionKind.RightStickStroke, HoldQualificationMs = 70,
            StrokeDurationMs = 80, AnchorFraction = .5, EndHoldMs = 48, ConfigurationKey = "fixture" });
        arbiter.BeginSession(); arbiter.Sample(default, default, true, true, false, 10);
        Check(arbiter.SetArmed(true), "A fresh neutral, eligible session should arm.");
        return arbiter;
    }
    private static void Predict(ControllerOutputArbiter a, long at, double tau, long frame = -1)
    {
        var s = a.Status;
        Check(a.Submit(new(s.SessionGeneration, s.ShotToken, frame < 0 ? at : frame, at, at, tau, .99, 1, true), at), "Current prediction was rejected: " + a.Status.Message);
    }
    internal static void Run()
    {
        var a = Ready();
        Check(a.Sample(Down, Down, true, true, false, 100).RightY == short.MinValue, "The hold qualifier delayed physical passthrough.");
        a.Sample(Down, Down, true, true, false, 169); Check(a.Status.ShotToken == 0, "An unqualified flick armed vision.");
        a.Sample(Down, Down, true, true, false, 170); Check(a.Status.ShotStartedTimestamp == 100, "Qualification replaced the actual physical onset.");
        Predict(a, 170, 160); // receipt170 +160 - video20 - input10 = neutral anchor300; start260
        Check(a.DeadlineTimestamp == 260, "The stroke start did not subtract only the selected anchor fraction.");
        short center = short.MaxValue, endpoint = 0;
        for (long t = 172; t <= 388; t += 2)
        {
            a.Sample(Down, Down, true, true, false, t);
            if (t % 16 == 0) Predict(a, t, 330 - t);
            if (t == 300) center = a.Output.RightY;
            if (t == 340) endpoint = a.Output.RightY;
        }
        Check(Math.Abs((int)center) <= 1 && endpoint == short.MaxValue, "The ramp missed its neutral anchor or up endpoint.");
        Check(a.Output.RightY == 0 && a.Status.AutomatedReleases == 1 && a.LastAction?.AnchorTimestamp == 300,
            "The completed stroke did not return neutral with one receipt.");
        for (long t = 390; t < 500; t += 2) a.Sample(Down, Down, true, true, false, t);
        Check(a.Status.ShotToken == 0 && a.Output.RightY == 0, "Holding down retriggered the completed stroke.");
        a.Sample(default, default, true, true, false, 500); a.Sample(Down, Down, true, true, false, 510);
        a.Sample(Down, Down, true, true, false, 580); Check(a.Status.ShotToken != 0, "Neutral did not permit a fresh physical hold.");

        foreach (string cause in new[] { "manual", "stale", "source", "disconnect", "conflict" })
        {
            var b = Ready(); b.Sample(Down, Down, true, true, false, 100); b.Sample(Down, Down, true, true, false, 170); Predict(b, 170, 80);
            b.Sample(Down, Down, true, true, false, 180); // anchor220, start180
            if (cause == "manual") b.Sample(default, default, true, true, false, 182);
            if (cause == "stale") b.TryAdvanceDeadline(260);
            if (cause == "source") b.Invalidate("source lost", sourceLost: true, disarm: true);
            if (cause == "disconnect") b.Sample(default, default, false, false, false, 182);
            if (cause == "conflict") b.Sample(Down, Down, true, true, true, 182);
            Check(b.DeadlineTimestamp is null && b.Output.RightY == 0 && b.LastAction is null, cause + " did not cancel and neutralize the stroke.");
        }
        var c = Ready(new() { Kind = VisionActionKind.RightStickStroke, HoldQualificationMs = 70, StrokeDurationMs = 100,
            AnchorFraction = .25, Curve = VisionStrokeCurve.Smoothstep });
        c.Sample(Down, Down, true, true, false, 100); c.Sample(Down, Down, true, true, false, 170); Predict(c, 170, 160);
        Check(c.DeadlineTimestamp == 267, "Smoothstep anchor used an axis fraction as a time fraction.");
        var digital = Ready(new() { HoldQualificationMs = 70, ConfigurationKey = "button" });
        var x = new ControllerState { Buttons = (ushort)ControlId.X };
        digital.Sample(x, x, true, true, false, 100); digital.Sample(x, x, true, true, false, 170); Predict(digital, 170, 50);
        digital.Sample(x, x, true, true, false, 190);
        Check(digital.Output.Buttons == 0 && digital.LastAction is { StrokeDurationMs: 0 }, "Qualified button release or its calibration receipt failed.");
        Console.WriteLine("SUCCESS Meter scheduling: passthrough, qualification, delay accounting, linear/smooth anchors, refresh, neutral latch, button receipts and cancellation.");
    }
}
