using System.IO;
using System.Text.Json;

namespace EliteScriptStudio.Core;

public interface ITrainingCaptureProfile
{
    void Validate();
    bool UsesSupervisionOverlay { get; }
}

public enum VisionTrainingPurpose { MeterSupervisedTraining, MeterOffValidation }
public enum VisionEvidenceMode { FixedRegions, SceneFirst, Anchored, Automatic }

/// <summary>Reviewed automatic or manually configured evidence regions, in normalized coordinates of the captured window.</summary>
public sealed record VisionTrainingCaptureSettings
{
    public bool Configured { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public string HudName { get; init; } = "Custom HUD";
    public string GameBuild { get; init; } = "";
    public string ShotCategory { get; init; } = "Mixed / unclassified";
    public VisionTrainingPurpose Purpose { get; init; } = VisionTrainingPurpose.MeterSupervisedTraining;
    public VisionEvidenceMode EvidenceMode { get; init; }
    public VisionCrop GameplayRegion { get; init; } = new(0, 0, 1, 1);
    public VisionCrop? MeterRegion { get; init; }
    public VisionCrop? FeedbackRegion { get; init; }
    public JsonElement? PluginConfiguration { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Purpose) || !Enum.IsDefined(EvidenceMode)) throw new InvalidDataException("Choose a supported capture purpose and evidence mode.");
        if (Configured && (SourceWidth is < 1 or > 16384 || SourceHeight is < 1 or > 16384))
            throw new InvalidDataException("Confirm the training layout against a fresh capture preview.");
        GameplayRegion.Validate(); MeterRegion?.Validate(); FeedbackRegion?.Validate();
        if (PluginConfiguration is { } configuration && (configuration.ValueKind != JsonValueKind.Object || configuration.GetRawText().Length > 512 * 1024))
            throw new InvalidDataException("The plugin capture configuration is invalid or exceeds 512 KiB.");
        CheckText(HudName, 80, false, "HUD layout name");
        CheckText(GameBuild, 120, true, "Game build");
        CheckText(ShotCategory, 80, false, "Shot category");
        foreach (var region in new[] { MeterRegion, FeedbackRegion })
            if (region is { } value && !Contains(GameplayRegion, value))
                throw new InvalidDataException("Meter and feedback regions must be inside the gameplay region.");
    }

    public static bool Contains(VisionCrop outer, VisionCrop inner) => inner.Left >= outer.Left && inner.Top >= outer.Top
        && inner.Left + inner.Width <= outer.Left + outer.Width + 1e-9
        && inner.Top + inner.Height <= outer.Top + outer.Height + 1e-9;

    private static void CheckText(string? value, int maximum, bool allowEmpty, string name)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException($"{name} must contain {(allowEmpty ? "0" : "1")}–{maximum} readable characters.");
    }
}
