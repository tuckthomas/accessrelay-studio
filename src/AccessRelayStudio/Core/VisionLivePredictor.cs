using System.Text.Json;

namespace EliteScriptStudio.Core;

public enum VisionActionKind { ButtonRelease, RightStickStroke }
public enum VisionStrokeCurve { Linear, Smoothstep }

/// <summary>Game-neutral output description. The host alone writes controller reports.</summary>
public sealed record VisionControllerAction
{
    public VisionActionKind Kind { get; init; }
    public double HoldQualificationMs { get; init; }
    public int AxisThreshold { get; init; } = 16000;
    public double StrokeDurationMs { get; init; } = 80;
    public double AnchorFraction { get; init; } = .5;
    public double EndHoldMs { get; init; } = 48;
    public VisionStrokeCurve Curve { get; init; }
    public string ConfigurationKey { get; init; } = "";
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Curve) || !double.IsFinite(HoldQualificationMs)
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

public sealed record VisionActionReceipt(long Id, long SessionGeneration, long ShotToken,
    long StartedTimestamp, long AnchorTimestamp, long CompletedTimestamp,
    double StrokeDurationMs, double AnchorFraction, string ConfigurationKey, string RunId = "");

public readonly record struct VisionSignalContext(ControllerState? Physical, long InputTimestamp,
    long SessionGeneration, long ShotToken, long ShotStartedTimestamp, bool Armed);

public sealed record VisionSignalObservation(long Sequence, long CaptureTimestamp, bool Acquired,
    VisionCrop? Region, double Progress, double Target, double VelocityPerMs,
    double? TimeToTargetMs, double Confidence, double UncertaintyMs, string Message,
    int Samples = 0, double ProcessingMs = 0, int CaptureWidth = 0, int CaptureHeight = 0, bool InvalidateAttempt = false);

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
