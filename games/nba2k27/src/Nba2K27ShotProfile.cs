using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using EliteScriptStudio.Core;

namespace EliteScriptStudio.Packs.Nba2K27;

/// <summary>Player and custom shot configuration. Entry guards are not a game animation catalog.</summary>
internal sealed record Nba2K27ShotProfile(Guid Id, string Name, int HeightInches, int ThreePointRating,
    string Base, string UpperRelease1, string UpperRelease2, bool UsesShotMeter = false,
    string MeterReleaseType = "", int ShotSpeed = 4, int Release1Percent = 50, string CameraView = "2K") : IVisionCaptureProfile
{
    public const int MaximumProfiles = 64;
    public const int MinimumHeightInches = 36;
    public const int MaximumHeightInches = 108;
    public static IReadOnlyList<string> CameraViews { get; } = Array.AsReadOnly(new[] { "2K", "2K Low", "2K Legacy" });

    // An incomplete draft can be displayed and edited, but cannot be persisted or selected.
    public static Nba2K27ShotProfile Create() => new(Guid.NewGuid(), "New shot profile", 78, 80, "", "", "");

    public void Validate()
    {
        if (Id == Guid.Empty) throw new InvalidDataException("A shot profile needs an ID.");
        CheckLabel(Name, 80, "Shot profile name");
        if (HeightInches is < MinimumHeightInches or > MaximumHeightInches)
            throw new InvalidDataException($"Player height must be between {MinimumHeightInches} and {MaximumHeightInches} inches. These are data-entry limits, not game catalog limits.");
        if (ThreePointRating is < 0 or > 100)
            throw new InvalidDataException("The three-point rating must be between 0 and 100.");
        if (CameraView is not ("2K" or "2K Low" or "2K Legacy"))
            throw new InvalidDataException("Choose the camera view 2K, 2K Low, or 2K Legacy.");
        CheckLabel(Base, 120, "Base");
        CheckLabel(UpperRelease1, 120, "Upper Release 1");
        CheckLabel(UpperRelease2, 120, "Upper Release 2");
        if (UsesShotMeter) CheckLabel(MeterReleaseType, 120, "Meter release type");
        if (ShotSpeed is < 1 or > 4)
            throw new InvalidDataException("Shot speed must be one of the four options, from 1 (slowest) to 4 (fastest).");
        if (Release1Percent is < 0 or > 100)
            throw new InvalidDataException("Upper Release 1 allocation must be between 0 and 100 percent; Upper Release 2 receives the remainder.");
    }

    bool ITrainingCaptureProfile.UsesSupervisionOverlay => UsesShotMeter;
    string IVisionCaptureProfile.AnimationProfile => "nba2k27:" + ConfigurationFingerprint;
    object IVisionCaptureProfile.ExportProfile() => this;
    void IVisionCaptureProfile.ValidateCapture(VisionTrainingCaptureSettings settings)
    {
        settings.Validate(); Validate();
        if (!settings.Configured || settings.FeedbackRegion is null)
            throw new InvalidDataException("Mark the gameplay scene and fixed shot feedback panel first.");
        if (settings.Purpose == VisionTrainingPurpose.MeterSupervisedTraining && (!UsesShotMeter ||
            (settings.EvidenceMode == VisionEvidenceMode.FixedRegions && settings.MeterRegion is null)))
            throw new InvalidDataException("NBA 2K27 training requires the shot meter on in the game and selected shot profile.");
        if (settings.Purpose == VisionTrainingPurpose.MeterOffValidation && UsesShotMeter)
            throw new InvalidDataException("NBA 2K27 meter-off validation requires the meter off in the game and selected profile.");
    }

    IReadOnlyList<VisionEvidenceRegion> IVisionCaptureProfile.EvidenceRegions(VisionSettings settings)
    {
        var layout = settings.TrainingCapture;
        var regions = new List<VisionEvidenceRegion> { new("player", "frames.bgra", 256, 256,
            layout.EvidenceMode == VisionEvidenceMode.SceneFirst ? layout.GameplayRegion : settings.Crop) };
        if (!layout.Configured) return regions;
        bool sceneFirst = layout.EvidenceMode == VisionEvidenceMode.SceneFirst;
        double width = layout.SourceWidth * layout.GameplayRegion.Width, height = layout.SourceHeight * layout.GameplayRegion.Height;
        double scale = Math.Min(1, 1280 / Math.Max(width, height));
        int sceneWidth = sceneFirst ? Math.Max(1, (int)Math.Round(width * scale)) : 640;
        int sceneHeight = sceneFirst ? Math.Max(1, (int)Math.Round(height * scale)) : 360;
        regions.Add(new("scene", "scene.bgra", sceneWidth, sceneHeight, layout.GameplayRegion));
        if (!sceneFirst && layout.Purpose == VisionTrainingPurpose.MeterSupervisedTraining && layout.MeterRegion is { } meter)
            regions.Add(new("meter", "meter.bgra", 256, 256, meter));
        if (layout.FeedbackRegion is { } feedback) regions.Add(new("feedback", "feedback.bgra", 640, 192, feedback));
        return regions;
    }

    public int Release2Percent => 100 - Release1Percent;

    [JsonIgnore]
    public string ConfigurationFingerprint
    {
        get
        {
            Validate();
            // Fixed-property canonical payload excludes display name and ID so duplicates of the
            // same game configuration share an identity; actual game-setting edits invalidate it.
            string canonical = JsonSerializer.Serialize(new
            {
                version = 3, heightInches = HeightInches, threePointRating = ThreePointRating, cameraView = CameraView,
                @base = Normalize(Base), upperRelease1 = Normalize(UpperRelease1), upperRelease2 = Normalize(UpperRelease2),
                usesShotMeter = UsesShotMeter, meterReleaseType = UsesShotMeter ? Normalize(MeterReleaseType) : "",
                shotSpeed = ShotSpeed, release1Percent = Release1Percent, release2Percent = Release2Percent
            });
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    public static string FormatHeight(int inches) => $"{inches / 12}' {inches % 12}\"";

    private static string Normalize(string text) => text.Trim().Normalize(NormalizationForm.FormC);
    private static void CheckLabel(string? text, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException($"{name} must contain 1–{maximum} readable characters.");
        try { _ = text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException error) { throw new InvalidDataException($"{name} contains invalid Unicode text.", error); }
    }
}
