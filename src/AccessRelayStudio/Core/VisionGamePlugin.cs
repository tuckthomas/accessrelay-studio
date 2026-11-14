using System.Text.Json;

namespace EliteScriptStudio.Core;

/// <summary>Game-owned configuration consumed by host-owned capture and scheduling.</summary>
public interface IVisionCaptureProfile : ITrainingCaptureProfile
{
    Guid Id { get; }
    string Name { get; }
    string CameraView { get; }
    string ConfigurationFingerprint { get; }
    string AnimationProfile { get; }
    string AnalysisPolicy => "plugin-regions-v1";
    object ExportProfile();
    void ValidateCapture(VisionTrainingCaptureSettings settings);
    IReadOnlyList<VisionEvidenceRegion> EvidenceRegions(VisionSettings settings);
    IVisionRegionTracker? CreateRegionTracker(VisionSettings settings, Func<VisionCaptureModelSet, IVisionAnalysisModels>? models = null) => null;
    VisionSettings PrepareAutomaticCapture(VisionSettings settings) => throw new NotSupportedException("This plugin does not provide automatic capture acquisition.");
    VisionSettings CompleteAutomaticCapture(VisionSettings settings, VisionRegionObservation observation, int width, int height) =>
        throw new NotSupportedException("This plugin does not provide automatic capture acquisition.");
    VisionSettings PrepareCalibrationCapture(VisionSettings settings, int width, int height) => throw new NotSupportedException("This plugin has no calibration capture policy.");
    bool CalibrationTriggered(ControllerState? input, VisionRegionObservation? observation) => input is { } state && ReferenceTriggered(state);
    VisionTrainingCaptureSettings ConfigureCapture(VisionTrainingCaptureSettings settings, ReadOnlySpan<byte> referenceBitmap) => settings;
    bool ReferenceTriggered(ControllerState input) => false;
    int ReferenceDelayMs => 0;
}

public sealed record VisionEvidenceRegion(string Name, string File, int Width, int Height, VisionCrop Crop);

/// <summary>Installed, trusted game implementation. The host owns device activation and capture lifecycle.</summary>
public interface IVisionGamePlugin
{
    string Id { get; }
    string Name { get; }
    string OutputContract { get; }
    string? TrainingWorkflowFile => null;
    IReadOnlyList<string> CameraViews { get; }
    IReadOnlyList<IVisionCaptureProfile> Profiles { get; }
    IVisionCaptureProfile? SelectedProfile { get; }
    void SelectProfile(Guid id);
    IVisionCaptureProfile SaveProfile(JsonElement profile, string expectedFingerprint);
    IVisionCaptureProfile DuplicateProfile(Guid id, string name);
    void DeleteProfile(Guid id);
}

public interface IGameControllerPlugin
{
    string Id { get; }
    (StudioProfile Profile, object Result) ApplyControllerAction(StudioProfile profile, string action, JsonElement args);
}

/// <summary>API 1 for installed .NET game extensions, shared from the host assembly.</summary>
public interface IGamePluginFactory
{
    IVisionGamePlugin Create(GamePluginContext context);
}

public sealed record GamePluginContext(string PackageDirectory, string? ProfileDirectory);
