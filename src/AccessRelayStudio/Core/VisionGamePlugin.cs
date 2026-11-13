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
}

/// <summary>Trusted built-in adapters. Imported packs cannot load native code or write controller output.</summary>
internal interface IVisionGamePlugin
{
    string Id { get; }
    string Name { get; }
    string OutputContract { get; }
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
