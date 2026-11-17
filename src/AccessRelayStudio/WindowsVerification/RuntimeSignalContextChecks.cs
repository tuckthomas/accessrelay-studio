using System.Diagnostics;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;
using EliteScriptStudio.Vision;

internal static class RuntimeSignalContextChecks
{
    internal static void Run()
    {
        using var input = new FakeControllerInput();
        using var runtime = new StudioRuntime(input, StudioProfile.CreateDefault() with { ScriptsEnabled = false }, new(0, 0));
        runtime.Attach(new FakeControllerOutput());
        Wait(() => runtime.ReadVisionSignalContext().InputTimestamp > 0, "Runtime did not publish a polled input context.");
        runtime.ConfigureVision(VisionSettings.Default, new() { Kind = VisionActionKind.RightStickStroke, HoldQualificationMs = 70 });
        runtime.BeginVisionSession();
        Wait(() => runtime.ReadVisionSignalContext().HoldStartedTimestamp == 0, "Neutral input retained a hold transition.");
        var down = new ControllerState { RightY = short.MinValue };
        input.SetState(down);
        Wait(() => runtime.ReadVisionSignalContext().HoldStartedTimestamp > 0, "The host did not expose the physical transition before arming.");
        var first = runtime.ReadVisionSignalContext();
        Wait(() => runtime.ReadVisionSignalContext().InputTimestamp >= first.InputTimestamp + Stopwatch.Frequency / 8,
            "The poll timestamp did not advance during a steady hold.");
        var steady = runtime.ReadVisionSignalContext();
        Check(steady.Physical == down && steady.HoldStartedTimestamp == first.HoldStartedTimestamp
            && steady.HoldStartedTimestamp <= steady.InputTimestamp, "Polling moved the physical press time or mixed its input state.");
        Check(steady.StrokeAnchorTimestamp == 0, "An observation-only hold invented a committed stroke.");
        input.SetState(default);
        Wait(() => runtime.ReadVisionSignalContext().HoldStartedTimestamp == 0, "Neutral did not clear the physical hold timestamp.");
        input.SetState(down);
        Wait(() => runtime.ReadVisionSignalContext().HoldStartedTimestamp > first.HoldStartedTimestamp,
            "A new physical press reused the previous transition timestamp.");

        // Verify live auto-OCR feedback parsing & damped adaptive timing step.
        var rhythmSlightlyEarly = LiveFeedbackOcr.ParseFeedback([
            new("RHYTHM", .95, new(.42, .02, .10, .03)),
            new("SLIGHTLY EARLY", .92, new(.42, .06, .16, .03)),
            new("OPEN", .90, new(.42, .10, .06, .03))
        ]);
        Check(rhythmSlightlyEarly is { ShotType: "rhythm", Timing: "slightlyEarly", Tempo: "none" }, "RHYTHM slightlyEarly banner parse failed.");
        var rhythmTempo = LiveFeedbackOcr.ParseFeedback([
            new("RHYTHM", .95, new(.42, .02, .09, .03)),
            new("EXCELLENT", .95, new(.42, .06, .10, .03)),
            new("FAST", .91, new(.53, .06, .06, .03))
        ]);
        Check(rhythmTempo is { ShotType: "rhythm", Timing: "excellent", Tempo: "fast" }, "RHYTHM excellent + fast banner parse failed.");
        var timingOnly = LiveFeedbackOcr.ParseFeedback([
            new("T1M1NG", .88, new(.42, .02, .09, .03)),
            new("SLIGHTLY LATE", .93, new(.42, .06, .15, .03))
        ]);
        Check(timingOnly is { ShotType: "timing", Timing: "slightlyLate", Tempo: "none" }, "Normalized TIMING slightlyLate parse failed.");
        int streak = 0;
        var s1 = LiveFeedbackOcr.ComputeAdaptiveStep(3.5, "slightlyEarly", ref streak);
        Check(s1.NextCorrectionMs == 5.0 && streak == 1, $"First slightlyEarly step unexpected: {s1}");
        var s2 = LiveFeedbackOcr.ComputeAdaptiveStep(s1.NextCorrectionMs, "slightlyEarly", ref streak);
        Check(s2.NextCorrectionMs == 7.5 && streak == 2, $"Repeated slightlyEarly step unexpected: {s2}");
        var s3 = LiveFeedbackOcr.ComputeAdaptiveStep(s2.NextCorrectionMs, "excellent", ref streak);
        Check(s3.NextCorrectionMs == 7.5 && streak == 0, $"Excellent changed correction: {s3}");
        Check(LiveFeedbackOcr.BuildBannerSearchCrop(new(.41, .004, .197, .111)).Height >= .36, "Banner search crop too short for OCR detector threshold.");
        Check(LiveFeedbackOcr.SelectSearchCrop(3, new(.3, 0, .4, .36)).Top == .24, "Above-player middle band missing from OCR search rotation.");

        Console.WriteLine("SUCCESS Runtime signal context: coherent polling snapshots, live auto-OCR feedback parsing/adaptation, and neutral/repress transitions.");
    }
    private static void Wait(Func<bool> condition, string message)
    {
        long began = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) throw new InvalidOperationException(message);
            Thread.Sleep(2);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
