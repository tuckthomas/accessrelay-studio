using System.IO;

namespace EliteScriptStudio.Core;

internal readonly record struct VisionCrop(double Left, double Top, double Width, double Height)
{
    public void Validate()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || Left < 0 || Top < 0 || Width <= 0 || Height <= 0 || Left + Width > 1 || Top + Height > 1)
            throw new InvalidDataException("Choose a nonempty player region inside the captured image.");
    }
}

internal sealed record VisionSettings
{
    public static VisionSettings Default { get; } = new();
    public VisionCrop Crop { get; init; } = new(0, 0, 1, 1);
    public string ModelManifestPath { get; init; } = "";
    public string AnimationProfile { get; init; } = "Default";
    public ControlId ShootButton { get; init; } = ControlId.X;
    public double MinimumConfidence { get; init; } = .9;
    public double MaximumUncertaintyMs { get; init; } = 5;
    public double MaximumFrameAgeMs { get; init; } = 80;
    public double EstimatedVideoDelayMs { get; init; }
    public double EstimatedInputDelayMs { get; init; }
    public double CorrectionMs { get; init; }
    public double MaximumLatenessMs { get; init; } = 5;
    public double MaximumShotDurationMs { get; init; } = 5000;

    public void Validate()
    {
        Crop.Validate();
        if (!Enum.IsDefined(ShootButton) || (uint)ShootButton > ushort.MaxValue)
            throw new InvalidDataException("Vision release requires a digital controller button.");
        if (ModelManifestPath is null || ModelManifestPath.Length > 2048 || ModelManifestPath.Any(char.IsControl))
            throw new InvalidDataException("The model manifest path is invalid.");
        if (string.IsNullOrWhiteSpace(AnimationProfile) || AnimationProfile.Length > 80 || AnimationProfile.Any(char.IsControl))
            throw new InvalidDataException("Choose an animation profile of up to 80 characters.");
        Check(MinimumConfidence, 0, 1, "Confidence");
        Check(MaximumUncertaintyMs, 0, 1000, "Uncertainty");
        Check(MaximumFrameAgeMs, 1, 1000, "Frame age");
        Check(EstimatedVideoDelayMs, 0, 1000, "Estimated video delay");
        Check(EstimatedInputDelayMs, 0, 1000, "Estimated input delay");
        Check(CorrectionMs, -1000, 1000, "Timing correction");
        Check(MaximumLatenessMs, 0, 100, "Deadline lateness");
        Check(MaximumShotDurationMs, 1, 10000, "Shot duration");
    }

    private static void Check(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }
}
