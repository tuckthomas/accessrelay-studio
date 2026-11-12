using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal sealed record VisionCalibrationAttempt(string ShotId, bool Reviewed, bool HasMeasuredSignedError,
    double SignedReleaseErrorMs);
internal readonly record struct VisionCalibrationEstimate(int ReviewedAttempts, double PreviousCorrectionMs,
    double RecommendedCorrectionMs, double MedianSignedErrorMs, double ResidualP95Ms);

internal sealed record VisionCalibrationImport
{
    public int SchemaVersion { get; init; } = 1;
    public string ModelManifestHash { get; init; } = "";
    public string AnimationProfile { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string CameraConfiguration { get; init; } = "";
    public string ExecutionProvider { get; init; } = "";
    public string ProcedureVersion { get; init; } = "";
    public int CaptureWidth { get; init; }
    public int CaptureHeight { get; init; }
    public VisionCrop Crop { get; init; }
    public double PreviousCorrectionMs { get; init; }
    public double EstimatedVideoDelayMs { get; init; }
    public double EstimatedInputDelayMs { get; init; }
    public VisionCalibrationAttempt[] Attempts { get; init; } = [];

    internal void ValidateContext(string manifestHash, VisionModelManifest manifest, VisionSettings settings, int width, int height)
    {
        if (SchemaVersion != 1 || !VisionModelManifest.IsSha256(ModelManifestHash) ||
            ModelManifestHash != manifestHash || AnimationProfile != settings.AnimationProfile ||
            AnimationProfile != manifest.AnimationProfile || GameVersion != manifest.GameVersion ||
            CameraConfiguration != manifest.CameraConfiguration || string.IsNullOrWhiteSpace(ProcedureVersion) ||
            ExecutionProvider != settings.ExecutionProviderName ||
            (settings.CameraView.Length != 0 && settings.CameraView != CameraConfiguration) ||
            width <= 0 || height <= 0 || CaptureWidth != width || CaptureHeight != height || Crop != settings.Crop ||
            PreviousCorrectionMs != settings.CorrectionMs || EstimatedVideoDelayMs != settings.EstimatedVideoDelayMs ||
            EstimatedInputDelayMs != settings.EstimatedInputDelayMs || Attempts is null)
            throw new InvalidDataException("Calibration samples do not match the selected model, shot configuration, observed source dimensions, crop and timing settings.");
        Crop.Validate();
    }
}

internal sealed record VisionCalibrationRecord
{
    public int SchemaVersion { get; init; } = 1;
    public string ModelManifestHash { get; init; } = "";
    public string AnimationProfile { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string CameraConfiguration { get; init; } = "";
    public string ExecutionProvider { get; init; } = "";
    public string ProcedureVersion { get; init; } = "";
    public int CaptureWidth { get; init; }
    public int CaptureHeight { get; init; }
    public VisionCrop Crop { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public VisionCalibrationEstimate Estimate { get; init; }

    internal bool Matches(string manifestHash, VisionSettings settings, int width, int height) =>
        SchemaVersion == 1 && ModelManifestHash == manifestHash && AnimationProfile == settings.AnimationProfile &&
        ExecutionProvider == settings.ExecutionProviderName &&
        CaptureWidth == width && CaptureHeight == height && Crop == settings.Crop &&
        !string.IsNullOrWhiteSpace(ProcedureVersion) && Estimate.ReviewedAttempts >= 5 &&
        double.IsFinite(Estimate.RecommendedCorrectionMs) && Math.Abs(Estimate.RecommendedCorrectionMs) <= 1000;
}

/// <summary>Reviewable robust calibration from measured errors; coarse early/late labels are insufficient.</summary>
internal static class VisionCalibration
{
    internal static VisionCalibrationEstimate Estimate(IEnumerable<VisionCalibrationAttempt> attempts,
        double previousCorrectionMs)
    {
        if (!double.IsFinite(previousCorrectionMs) || Math.Abs(previousCorrectionMs) > 1000)
            throw new InvalidDataException("The previous calibration correction is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<double>();
        foreach (var attempt in attempts)
        {
            if (!attempt.Reviewed || !attempt.HasMeasuredSignedError)
                throw new InvalidDataException("Calibration requires reviewed, measured signed millisecond errors; early/late labels alone cannot provide them.");
            if (string.IsNullOrWhiteSpace(attempt.ShotId) || !seen.Add(attempt.ShotId) ||
                !double.IsFinite(attempt.SignedReleaseErrorMs) || Math.Abs(attempt.SignedReleaseErrorMs) > 1000)
                throw new InvalidDataException("Calibration attempts need distinct shot IDs and finite errors within one second.");
            errors.Add(attempt.SignedReleaseErrorMs);
            if (errors.Count > 10000) throw new InvalidDataException("Use at most 10000 reviewed attempts in one calibration estimate.");
        }
        if (errors.Count < 5) throw new InvalidDataException("At least five measured attempts are needed for a provisional calibration estimate.");
        errors.Sort();
        double median = errors.Count % 2 == 0 ? (errors[errors.Count / 2 - 1] + errors[errors.Count / 2]) / 2 : errors[errors.Count / 2];
        var residuals = errors.Select(error => Math.Abs(error - median)).Order().ToArray();
        double spread = residuals[Math.Max(0, (int)Math.Ceiling(.95 * residuals.Length) - 1)];
        // Positive error means release arrived late; reduce a positive-delay correction by that error.
        double correction = Math.Clamp(previousCorrectionMs - median, -1000, 1000);
        return new(errors.Count, previousCorrectionMs, correction, median, spread);
    }

    internal static void Save(string path, VisionCalibrationRecord record)
    {
        if (record.SchemaVersion != 1 || !VisionModelManifest.IsSha256(record.ModelManifestHash) ||
            record.ExecutionProvider is not ("CPUExecutionProvider" or "CUDAExecutionProvider") ||
            record.CaptureWidth <= 0 || record.CaptureHeight <= 0 || string.IsNullOrWhiteSpace(record.ProcedureVersion))
            throw new InvalidDataException("The calibration record lacks a bound model, capture configuration or procedure.");
        record.Crop.Validate();
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(record, VisionModelManifest.Json));
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static VisionCalibrationRecord Load(string path)
    {
        var record = JsonSerializer.Deserialize<VisionCalibrationRecord>(VisionModelManifest.ReadBounded(path, 256 * 1024), VisionModelManifest.Json)
            ?? throw new InvalidDataException("The calibration record is empty.");
        if (record.SchemaVersion != 1 || !VisionModelManifest.IsSha256(record.ModelManifestHash))
            throw new InvalidDataException("The calibration record version or model binding is invalid.");
        record.Crop.Validate();
        return record;
    }
}
