using System.Text.Json;

namespace EliteScriptStudio.Core;

public enum VisionActionKind { ButtonRelease, RightStickStroke }
public enum VisionStrokeCurve { Linear, Smoothstep }

/// <summary>Game-neutral output description. The host alone writes controller reports.</summary>
public sealed record VisionControllerAction
{
    private const PaddleButtons SupportedPaddles = PaddleButtons.SmallLeft | PaddleButtons.SmallRight | PaddleButtons.LargeLeft | PaddleButtons.LargeRight;
    public VisionActionKind Kind { get; init; }
    public PaddleButtons TriggerPaddle { get; init; }
    public double HoldQualificationMs { get; init; }
    public int AxisThreshold { get; init; } = 16000;
    public double StrokeDurationMs { get; init; } = 80;
    public double AnchorFraction { get; init; } = .5;
    public double EndHoldMs { get; init; } = 48;
    public VisionStrokeCurve Curve { get; init; }
    public string ConfigurationKey { get; init; } = "";
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Curve)
            || (TriggerPaddle & ~SupportedPaddles) != 0 || (TriggerPaddle != PaddleButtons.None && Kind != VisionActionKind.RightStickStroke)
            || !double.IsFinite(HoldQualificationMs)
            || HoldQualificationMs is < 0 or > 500 || AxisThreshold is < 4000 or > 30000
            || !double.IsFinite(StrokeDurationMs) || StrokeDurationMs is < 8 or > 500
            || !double.IsFinite(AnchorFraction) || AnchorFraction is < 0 or > 1
            || !double.IsFinite(EndHoldMs) || EndHoldMs is < 0 or > 500
            || ConfigurationKey.Length > 128 || ConfigurationKey.Any(char.IsControl))
            throw new InvalidDataException("The plugin's controller action is invalid.");
    }
    public double AnchorTimeFraction
    {
        get
        {
            if (Curve == VisionStrokeCurve.Linear) return AnchorFraction;
            double lo = 0, hi = 1;
            for (int i = 0; i < 32; i++) { double mid = (lo + hi) / 2; if (mid * mid * (3 - 2 * mid) < AnchorFraction) lo = mid; else hi = mid; }
            return (lo + hi) / 2;
        }
    }
}

public enum VisionActionOutcome { Completed, Cancelled }

public sealed record VisionActionReceipt(long Id, long SessionGeneration, long ShotToken,
    long StartedTimestamp, long AnchorTimestamp, long CompletedTimestamp,
    double StrokeDurationMs, double AnchorFraction, string ConfigurationKey, string RunId = "")
{
    public VisionActionOutcome Outcome { get; init; }
    public string? CancellationReason { get; init; }
    // Successful local device handoffs, not Xbox input receipt. Zero means unobserved.
    public long ActualStartedTimestamp { get; init; }
    public long ActualAnchorTimestamp { get; init; }
    public double? AnchorLatenessMs { get; init; }
    public bool EligibleForFeedback { get; init; }
    public StreamTelemetryAssessment? StreamTelemetry { get; init; }
    public double CorrectionMs { get; init; }
    public double TempoCorrectionMs { get; init; }
}

public readonly record struct VisionSignalContext(ControllerState? Physical, long InputTimestamp,
    long SessionGeneration, long ShotToken, long ShotStartedTimestamp, bool Armed)
{
    // Host poll transition, including observation-only holds before qualification opens a shot.
    public long HoldStartedTimestamp { get; init; }
    // Nonzero only after the host has actually started the calibrated stroke.
    public long StrokeAnchorTimestamp { get; init; }
    public PaddleButtons HeldPaddles { get; init; }
}

public sealed record VisionSignalObservation(long Sequence, long CaptureTimestamp, bool Acquired,
    VisionCrop? Region, double Progress, double Target, double VelocityPerMs,
    double? TimeToTargetMs, double Confidence, double UncertaintyMs, string Message,
    int Samples = 0, double ProcessingMs = 0, int CaptureWidth = 0, int CaptureHeight = 0, bool InvalidateAttempt = false)
{
    // A fresh frame may finish an already anchored stroke; this is never a new timing forecast.
    public bool ContinueCommittedAction { get; init; }
}

/// <summary>Trusted plugin CPU predictor. No model loading or output/device APIs are exposed.</summary>
public interface IVisionLivePredictor : IDisposable
{
    string Name { get; }
    VisionControllerAction Action { get; }
    bool CanArm { get; }
    string Status { get; }
    VisionSignalObservation Observe(ReadOnlySpan<byte> bgra, int width, int height, int stride,
        long sequence, long generation, long captureTicks, VisionSignalContext context);
    void Reset();
}
