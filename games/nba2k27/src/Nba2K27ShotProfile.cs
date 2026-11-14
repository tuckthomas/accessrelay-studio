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
    string MeterReleaseType = "", int ShotSpeed = 4, int Release1Percent = 50, string CameraView = "2K", string Gamertag = "") : IVisionCaptureProfile
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
        if (Gamertag is null || Gamertag.Length > 32 || Gamertag.Any(char.IsControl)) throw new InvalidDataException("Enter a displayed gamertag of at most 32 characters.");
    }

    bool ITrainingCaptureProfile.UsesSupervisionOverlay => UsesShotMeter;
    string IVisionCaptureProfile.AnimationProfile => "nba2k27:" + ConfigurationFingerprint;
    object IVisionCaptureProfile.ExportProfile() => this;
    bool IVisionCaptureProfile.ReferenceTriggered(ControllerState input) => Math.Abs((int)input.RightY) > 16000 || ControlMap.IsPressed(ControlId.X, input, PaddleButtons.None);
    int IVisionCaptureProfile.ReferenceDelayMs => 200;
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
        if (settings.EvidenceMode == VisionEvidenceMode.Anchored)
        {
            if (string.IsNullOrWhiteSpace(settings.GameBuild))
                throw new InvalidDataException("Enter the game's build or patch in capture setup so training can match compatible recordings.");
            Nba2K27CaptureConfiguration.Read(settings.PluginConfiguration).Validate(CameraView, UsesShotMeter, requireReference: true);
        }
        if (settings.EvidenceMode == VisionEvidenceMode.Automatic)
        {
            if (string.IsNullOrWhiteSpace(settings.GameBuild)) throw new InvalidDataException("Enter the game build or patch before saving detected capture areas.");
            Nba2K27AutomaticCapture.Read(settings.PluginConfiguration).Validate(this, complete: true);
        }
    }

    VisionTrainingCaptureSettings IVisionCaptureProfile.ConfigureCapture(VisionTrainingCaptureSettings settings, ReadOnlySpan<byte> referenceBitmap)
    {
        if (settings.EvidenceMode == VisionEvidenceMode.Automatic)
        { Nba2K27AutomaticCapture.Read(settings.PluginConfiguration).Validate(this, complete: true); return settings; }
        if (settings.EvidenceMode != VisionEvidenceMode.Anchored) return settings;
        var configuration = Nba2K27CaptureConfiguration.Read(settings.PluginConfiguration);
        configuration.Validate(CameraView, UsesShotMeter, requireReference: false);
        if (!VisionTrainingCaptureSettings.Contains(settings.GameplayRegion, configuration.Player)
            || (configuration.Meter is { } meter && !VisionTrainingCaptureSettings.Contains(settings.GameplayRegion, meter))
            || configuration.Court.Any(p => p.X < settings.GameplayRegion.Left || p.Y < settings.GameplayRegion.Top
                || p.X > settings.GameplayRegion.Left + settings.GameplayRegion.Width || p.Y > settings.GameplayRegion.Top + settings.GameplayRegion.Height))
            throw new InvalidDataException("Keep the initial player, meter and floor anchors inside the gameplay image.");
        configuration = (configuration with { Viewport = settings.GameplayRegion }).WithReference(referenceBitmap);
        return settings with { PluginConfiguration = JsonSerializer.SerializeToElement(configuration, Nba2K27CaptureConfiguration.Json) };
    }

    IVisionRegionTracker? IVisionCaptureProfile.CreateRegionTracker(VisionSettings settings, Func<VisionCaptureModelSet, IVisionAnalysisModels>? models)
    {
        if (settings.TrainingCapture.EvidenceMode == VisionEvidenceMode.Automatic)
        {
            var automatic = Nba2K27AutomaticCapture.Read(settings.TrainingCapture.PluginConfiguration);
            automatic.Validate(this, settings.TrainingCapture.Configured);
            if (models is null) throw new InvalidOperationException("The host does not provide automatic pose/text detection.");
            return new Nba2K27AutomaticTracker(automatic, () => models(Nba2K27AutomaticCapture.Models));
        }
        if (settings.TrainingCapture is not { Configured: true, EvidenceMode: VisionEvidenceMode.Anchored } capture) return null;
        var configuration = Nba2K27CaptureConfiguration.Read(capture.PluginConfiguration);
        configuration.Validate(CameraView, UsesShotMeter, requireReference: true);
        return new Nba2K27RegionTracker(configuration);
    }

    VisionSettings IVisionCaptureProfile.PrepareAutomaticCapture(VisionSettings settings)
    {
        var configuration = new Nba2K27AutomaticCapture { Gamertag = Gamertag, Camera = CameraView, MeterOn = UsesShotMeter };
        configuration.Validate(this, complete: false);
        return settings with { Crop = new(0, 0, 1, 1), TrainingCapture = settings.TrainingCapture with {
            Configured = false, EvidenceMode = VisionEvidenceMode.Automatic, GameplayRegion = new(0, 0, 1, 1),
            Purpose = UsesShotMeter ? VisionTrainingPurpose.MeterSupervisedTraining : VisionTrainingPurpose.MeterOffValidation,
            MeterRegion = null, FeedbackRegion = null, PluginConfiguration = JsonSerializer.SerializeToElement(configuration, Nba2K27CaptureConfiguration.Json) } };
    }
    VisionSettings IVisionCaptureProfile.CompleteAutomaticCapture(VisionSettings settings, VisionRegionObservation observation, int width, int height)
    {
        if (!observation.Valid || !observation.AcquisitionComplete || observation.Identity?.ExpectedTag != Gamertag || observation.BodyBounds is not { } body)
            throw new InvalidDataException("Automatic capture acquisition has not confirmed this player and the required regions.");
        var configuration = Nba2K27AutomaticCapture.Read(settings.TrainingCapture.PluginConfiguration) with { Feedback = observation.Feedback,
            RelativeMeter = observation.Meter is { } meter ? new((meter.Left - body.Left) / body.Width, (meter.Top - body.Top) / body.Height, meter.Width / body.Width, meter.Height / body.Height) : null };
        return settings with { Crop = observation.Player, TrainingCapture = settings.TrainingCapture with {
            Configured = true, SourceWidth = width, SourceHeight = height, MeterRegion = observation.Meter, FeedbackRegion = observation.Feedback,
            PluginConfiguration = JsonSerializer.SerializeToElement(configuration, Nba2K27CaptureConfiguration.Json) } };
    }

    IReadOnlyList<VisionEvidenceRegion> IVisionCaptureProfile.EvidenceRegions(VisionSettings settings)
    {
        var layout = settings.TrainingCapture;
        var regions = new List<VisionEvidenceRegion> { new("player", "frames.bgra", 256, 256,
            layout.EvidenceMode == VisionEvidenceMode.SceneFirst ? layout.GameplayRegion : settings.Crop) };
        if (!layout.Configured) return regions;
        bool sceneFirst = layout.EvidenceMode is VisionEvidenceMode.SceneFirst or VisionEvidenceMode.Anchored or VisionEvidenceMode.Automatic;
        double width = layout.SourceWidth * layout.GameplayRegion.Width, height = layout.SourceHeight * layout.GameplayRegion.Height;
        double scale = Math.Min(1, 1280 / Math.Max(width, height));
        int sceneWidth = sceneFirst ? Math.Max(1, (int)Math.Round(width * scale)) : 640;
        int sceneHeight = sceneFirst ? Math.Max(1, (int)Math.Round(height * scale)) : 360;
        regions.Add(new("scene", "scene.bgra", sceneWidth, sceneHeight, layout.GameplayRegion));
        if (layout.EvidenceMode != VisionEvidenceMode.SceneFirst && layout.Purpose == VisionTrainingPurpose.MeterSupervisedTraining && layout.MeterRegion is { } meter)
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
            if (Gamertag.Length != 0) canonical += "\nidentity-tag-v1:" + Normalize(Gamertag);
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
