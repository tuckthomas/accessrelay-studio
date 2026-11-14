using System.Text.Json;

namespace EliteScriptStudio.Core;

/// <summary>Game-owned configuration consumed by host-owned capture and scheduling.</summary>
internal interface IVisionCaptureProfile : ITrainingCaptureProfile
{
    Guid Id { get; }
    string Name { get; }
    string CameraView { get; }
    string ConfigurationFingerprint { get; }
    string AnimationProfile { get; }
    object ExportProfile();
    void ValidateCapture(VisionTrainingCaptureSettings settings);
    IReadOnlyList<VisionEvidenceRegion> EvidenceRegions(VisionSettings settings);
    IVisionRegionTracker? CreateRegionTracker(VisionSettings settings, Func<VisionCaptureModelSet, IVisionAnalysisModels>? models = null) => null;
    VisionSettings PrepareAutomaticCapture(VisionSettings settings) => throw new NotSupportedException("This plugin does not provide automatic capture acquisition.");
    VisionSettings CompleteAutomaticCapture(VisionSettings settings, VisionRegionObservation observation, int width, int height) =>
        throw new NotSupportedException("This plugin does not provide automatic capture acquisition.");
    VisionTrainingCaptureSettings ConfigureCapture(VisionTrainingCaptureSettings settings, ReadOnlySpan<byte> referenceBitmap) => settings;
    bool ReferenceTriggered(ControllerState input) => false;
    int ReferenceDelayMs => 0;
}

internal sealed record VisionEvidenceRegion(string Name, string File, int Width, int Height, VisionCrop Crop);

/// <summary>Trusted built-in adapters. Imported packs cannot load native code or write controller output.</summary>
internal interface IVisionGamePlugin
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

internal interface IGameControllerPlugin
{
    string Id { get; }
    (StudioProfile Profile, object Result) ApplyControllerAction(StudioProfile profile, string action, JsonElement args);
}
