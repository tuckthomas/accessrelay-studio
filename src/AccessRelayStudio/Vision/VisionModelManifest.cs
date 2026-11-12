using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal sealed record VisionModelManifest
{
    public string OutputContract { get; init; } = "temporal-release-v1";
    public int SchemaVersion { get; init; } = 1;
    public string DisplayName { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string ModelFile { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public string InputName { get; init; } = "frames";
    public string OutputName { get; init; } = "observation";
    public string Preprocessing { get; init; } = "bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2";
    public int InputWidth { get; init; } = 160;
    public int InputHeight { get; init; } = 160;
    public int HistoryLength { get; init; } = 4;
    public double HistoryIntervalMs { get; init; } = 16.6666667;
    public double MaximumPredictionHorizonMs { get; init; } = 2000;
    public float[] RgbMean { get; init; } = [0, 0, 0];
    public float[] RgbStandardDeviation { get; init; } = [1, 1, 1];
    public string AnimationProfile { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string CameraConfiguration { get; init; } = "";
    public string License { get; init; } = "";
    public VisionValidationEvidence? Validation { get; init; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal static (VisionModelManifest Manifest, string Hash) Read(string path)
    {
        byte[] bytes = ReadBounded(path, 256 * 1024);
        var manifest = JsonSerializer.Deserialize<VisionModelManifest>(bytes, Json)
            ?? throw new InvalidDataException("The model manifest is empty.");
        manifest.Validate();
        return (manifest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal void Validate()
    {
        if (OutputContract != "temporal-release-v1") throw new InvalidDataException("Unsupported typed vision output contract. This adapter requires temporal-release-v1.");
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 120 ||
            string.IsNullOrWhiteSpace(ModelVersion) || ModelVersion.Length > 80 ||
            string.IsNullOrWhiteSpace(AnimationProfile) || AnimationProfile.Length > 120 ||
            string.IsNullOrWhiteSpace(License) || string.IsNullOrWhiteSpace(GameVersion) ||
            string.IsNullOrWhiteSpace(CameraConfiguration) || !IsSha256(ModelSha256) ||
            string.IsNullOrWhiteSpace(InputName) || string.IsNullOrWhiteSpace(OutputName))
            throw new InvalidDataException("The model manifest needs schema 1, model/version/hash, environment, input/output names and license.");
        if (Preprocessing != "bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2" ||
            InputWidth < 32 || InputWidth > 512 || InputHeight < 32 || InputHeight > 512 ||
            HistoryLength < 2 || HistoryLength > 16 || !double.IsFinite(HistoryIntervalMs) ||
            HistoryIntervalMs < 4 || HistoryIntervalMs > 100 || !double.IsFinite(MaximumPredictionHorizonMs) ||
            MaximumPredictionHorizonMs <= 0 || MaximumPredictionHorizonMs > 2000 ||
            checked((long)HistoryLength * InputWidth * InputHeight * 3) > 16_777_216 ||
            RgbMean is not { Length: 3 } || RgbStandardDeviation is not { Length: 3 })
            throw new InvalidDataException("Unsupported preprocessing, history, dimensions or prediction horizon in the manifest.");
        for (int channel = 0; channel < 3; channel++)
            if (!float.IsFinite(RgbMean[channel]) || !float.IsFinite(RgbStandardDeviation[channel]) || RgbStandardDeviation[channel] <= 0)
                throw new InvalidDataException("Model normalization values must be finite with positive standard deviations.");
    }

    internal string CheckArmingEvidence(string manifestPath, VisionSettings settings) =>
        CheckArmingEvidence(manifestPath, settings, out _);

    internal string CheckArmingEvidence(string manifestPath, VisionSettings settings, out VisionValidationReport? acceptedReport)
    {
        acceptedReport = null;
        if (settings.CameraView.Length != 0 && settings.CameraView != CameraConfiguration)
            return "The selected camera view does not match the model's camera configuration.";
        if (Validation is not { } evidence)
            return "Observation only: this model has no held-out validation and hardware report.";
        try
        {
            evidence.Validate();
            if (evidence.ExecutionProvider != settings.ExecutionProviderName)
                return "The locked validation evidence was measured with a different execution provider than the selected backend.";
            if (!string.Equals(settings.AnimationProfile.Trim(), AnimationProfile, StringComparison.Ordinal))
                return "The selected animation profile does not match the model's validated profile.";
            if (settings.MinimumConfidence < evidence.MinimumConfidence ||
                settings.MaximumUncertaintyMs > evidence.MaximumUncertaintyMs ||
                settings.MaximumFrameAgeMs > evidence.MaximumFrameAgeMs)
                return "The configured confidence/freshness limits are weaker than the locked validation limits.";
            string reportPath = ResolveChildFile(manifestPath, evidence.ReportFile);
            byte[] bytes = ReadBounded(reportPath, 256 * 1024);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), evidence.ReportSha256, StringComparison.OrdinalIgnoreCase))
                return "The held-out report SHA-256 does not match the manifest.";
            var report = JsonSerializer.Deserialize<VisionValidationReport>(bytes, Json)
                ?? throw new InvalidDataException("The held-out report is empty.");
            string blocker = report.Check(this, evidence);
            if (blocker.Length == 0 && report.PlayerCrop != settings.Crop)
                blocker = "The selected player crop does not match the locked validation configuration.";
            if (blocker.Length == 0) acceptedReport = report;
            return blocker;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return $"Observation only: validation evidence could not be accepted. {error.Message}";
        }
    }

    internal static string ResolveChildFile(string manifestPath, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Model and report paths must be relative to the manifest directory.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string resolved = Path.GetFullPath(Path.Combine(directory, relative));
        string inside = Path.GetRelativePath(directory, resolved);
        if (inside == ".." || inside.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(inside))
            throw new InvalidDataException("Model and report files must stay inside the manifest directory.");
        return resolved;
    }

    internal static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > maximum) throw new InvalidDataException("Vision JSON file size is outside the allowed limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool FiniteRange(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
}

internal sealed record VisionValidationEvidence
{
    public string ReportFile { get; init; } = "";
    public string ReportSha256 { get; init; } = "";
    public string DatasetId { get; init; } = "";
    public string CalibrationProcedureVersion { get; init; } = "";
    public string ExecutionProvider { get; init; } = "";
    public int MinimumTargetShots { get; init; }
    public int MinimumNegativeShots { get; init; }
    public double MinimumConfidence { get; init; }
    public double MaximumUncertaintyMs { get; init; }
    public double MaximumFrameAgeMs { get; init; }
    public double MaximumPredictionP99ErrorMs { get; init; }
    public double MaximumFalseReleaseRate { get; init; }
    public double MinimumIdentityAccuracy { get; init; }
    public double MinimumTargetCoverage { get; init; }
    public double MaximumDeadlineMissRate { get; init; }
    public double MinimumHardwareSessionMinutes { get; init; }

    internal void Validate()
    {
        if (ExecutionProvider is not ("CPUExecutionProvider" or "CUDAExecutionProvider"))
            throw new InvalidDataException("Validation evidence must explicitly bind CPUExecutionProvider or CUDAExecutionProvider.");
        if (!VisionModelManifest.IsSha256(ReportSha256) || string.IsNullOrWhiteSpace(ReportFile) ||
            string.IsNullOrWhiteSpace(DatasetId) || string.IsNullOrWhiteSpace(CalibrationProcedureVersion) ||
            MinimumTargetShots < 1 || MinimumNegativeShots < 1 ||
            !VisionModelManifest.FiniteRange(MinimumConfidence, .01, 1) ||
            !VisionModelManifest.FiniteRange(MaximumUncertaintyMs, .01, 500) ||
            !VisionModelManifest.FiniteRange(MaximumFrameAgeMs, .01, 1000) ||
            !VisionModelManifest.FiniteRange(MaximumPredictionP99ErrorMs, .01, 500) ||
            !VisionModelManifest.FiniteRange(MaximumFalseReleaseRate, 0, 1) ||
            !VisionModelManifest.FiniteRange(MinimumIdentityAccuracy, .01, 1) ||
            !VisionModelManifest.FiniteRange(MinimumTargetCoverage, .01, 1) ||
            !VisionModelManifest.FiniteRange(MaximumDeadlineMissRate, 0, 1) ||
            !VisionModelManifest.FiniteRange(MinimumHardwareSessionMinutes, 30, 1440))
            throw new InvalidDataException("Validation evidence needs complete, finite, locked acceptance gates and sample-count requirements.");
    }
}

internal sealed record VisionValidationReport
{
    public int SchemaVersion { get; init; }
    public string DatasetId { get; init; } = "";
    public string Split { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string AnimationProfile { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string CameraConfiguration { get; init; } = "";
    public string CalibrationProcedureVersion { get; init; } = "";
    public string ExecutionProvider { get; init; } = "";
    public bool MeterOff { get; init; }
    public bool NoPlayerIconRequired { get; init; }
    public bool ThresholdsLockedBeforeEvaluation { get; init; }
    public double MinimumConfidence { get; init; }
    public double MaximumUncertaintyMs { get; init; }
    public double MaximumFrameAgeMs { get; init; }
    public int TargetShots { get; init; }
    public int NegativeShots { get; init; }
    public int EvaluatedPredictions { get; init; }
    public double TargetCoverage { get; init; }
    public double IdentityAccuracy { get; init; }
    public double? PredictionP99ErrorMs { get; init; }
    public double FalseReleaseRate { get; init; }
    public int CaptureWidth { get; init; }
    public int CaptureHeight { get; init; }
    public VisionCrop PlayerCrop { get; init; }
    public bool HardwareValidated { get; init; }
    public string HardwareDescription { get; init; } = "";
    public string RemotePlayClient { get; init; } = "";
    public double HardwareSessionMinutes { get; init; }
    public int HardwareAttempts { get; init; }
    public double DeadlineMissRate { get; init; }

    internal string Check(VisionModelManifest model, VisionValidationEvidence gates)
    {
        if (ExecutionProvider is not ("CPUExecutionProvider" or "CUDAExecutionProvider") || ExecutionProvider != gates.ExecutionProvider)
            return "The held-out and hardware report must explicitly match the locked execution provider.";
        if (SchemaVersion != 1 || Split != "held-out" || !ThresholdsLockedBeforeEvaluation || !MeterOff || !NoPlayerIconRequired ||
            DatasetId != gates.DatasetId || !string.Equals(ModelSha256, model.ModelSha256, StringComparison.OrdinalIgnoreCase) ||
            ModelVersion != model.ModelVersion || AnimationProfile != model.AnimationProfile || GameVersion != model.GameVersion ||
            CameraConfiguration != model.CameraConfiguration || CalibrationProcedureVersion != gates.CalibrationProcedureVersion)
            return "The report does not establish held-out, no-meter/no-icon validation for this model and configuration.";
        if (MinimumConfidence != gates.MinimumConfidence || MaximumUncertaintyMs != gates.MaximumUncertaintyMs ||
            MaximumFrameAgeMs != gates.MaximumFrameAgeMs)
            return "The report and manifest do not use the same locked confidence and freshness thresholds.";
        if (TargetShots < gates.MinimumTargetShots || NegativeShots < gates.MinimumNegativeShots || EvaluatedPredictions < 1 ||
            EvaluatedPredictions > TargetShots || !VisionModelManifest.FiniteRange(TargetCoverage, gates.MinimumTargetCoverage, 1) ||
            !VisionModelManifest.FiniteRange(IdentityAccuracy, gates.MinimumIdentityAccuracy, 1) ||
            PredictionP99ErrorMs is not { } predictionError ||
            !VisionModelManifest.FiniteRange(predictionError, 0, gates.MaximumPredictionP99ErrorMs) ||
            !VisionModelManifest.FiniteRange(FalseReleaseRate, 0, gates.MaximumFalseReleaseRate))
            return "The held-out sample counts or accuracy/error measurements do not pass the manifest's locked gates.";
        if (!HardwareValidated || CaptureWidth < 1 || CaptureHeight < 1 || CaptureWidth > 16384 || CaptureHeight > 16384 ||
            string.IsNullOrWhiteSpace(HardwareDescription) || string.IsNullOrWhiteSpace(RemotePlayClient) ||
            !VisionModelManifest.FiniteRange(HardwareSessionMinutes, gates.MinimumHardwareSessionMinutes, 1440) ||
            HardwareAttempts < 1 || !VisionModelManifest.FiniteRange(DeadlineMissRate, 0, gates.MaximumDeadlineMissRate))
            return "Observation only: matching hardware/Remote Play timing validation has not passed.";
        try { PlayerCrop.Validate(); }
        catch (InvalidDataException) { return "The report's validated player crop is invalid."; }
        return "";
    }
}
