using System.IO;

namespace EliteScriptStudio.Core;

internal enum VisionTrainingPurpose { MeterSupervisedTraining, MeterOffValidation }

/// <summary>Manually confirmed evidence regions, in normalized coordinates of the captured window.</summary>
internal sealed record VisionTrainingCaptureSettings
{
    public bool Configured { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public string HudName { get; init; } = "Custom HUD";
    public string GameBuild { get; init; } = "";
    public string ShotCategory { get; init; } = "Mixed / unclassified";
    public VisionTrainingPurpose Purpose { get; init; } = VisionTrainingPurpose.MeterSupervisedTraining;
    public VisionCrop GameplayRegion { get; init; } = new(0, 0, 1, 1);
    public VisionCrop? MeterRegion { get; init; }
    public VisionCrop? FeedbackRegion { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Purpose)) throw new InvalidDataException("Choose a supported clip purpose.");
        if (Configured && (SourceWidth is < 1 or > 16384 || SourceHeight is < 1 or > 16384))
            throw new InvalidDataException("Confirm the training layout against a fresh capture preview.");
        GameplayRegion.Validate(); MeterRegion?.Validate(); FeedbackRegion?.Validate();
        CheckText(HudName, 80, false, "HUD layout name");
        CheckText(GameBuild, 120, true, "Game build");
        CheckText(ShotCategory, 80, false, "Shot category");
        foreach (var region in new[] { MeterRegion, FeedbackRegion })
            if (region is { } value && !Contains(GameplayRegion, value))
                throw new InvalidDataException("Meter and feedback regions must be inside the gameplay region.");
    }

    public void ValidateForRecording(Nba2K27ShotProfile profile)
    {
        Validate(); profile.Validate();
        if (!Configured || FeedbackRegion is null)
            throw new InvalidDataException("Set up training capture first: mark the gameplay area, player, meter and shot feedback on the preview.");
        if (Purpose == VisionTrainingPurpose.MeterSupervisedTraining && (!profile.UsesShotMeter || MeterRegion is null))
            throw new InvalidDataException("Training clips require the shot meter on in the game and in the selected shot profile, plus a marked meter region. Use Meter-off validation only for meter-off evaluation footage.");
        if (Purpose == VisionTrainingPurpose.MeterOffValidation && profile.UsesShotMeter)
            throw new InvalidDataException("Meter-off validation requires the shot meter off in the game and in the selected shot profile.");
    }

    internal static bool Contains(VisionCrop outer, VisionCrop inner) => inner.Left >= outer.Left && inner.Top >= outer.Top
        && inner.Left + inner.Width <= outer.Left + outer.Width + 1e-9
        && inner.Top + inner.Height <= outer.Top + outer.Height + 1e-9;

    private static void CheckText(string? value, int maximum, bool allowEmpty, string name)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException($"{name} must contain {(allowEmpty ? "0" : "1")}–{maximum} readable characters.");
    }
}
