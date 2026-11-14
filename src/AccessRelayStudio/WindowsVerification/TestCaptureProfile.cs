using EliteScriptStudio.Core;

/// <summary>Game-independent recorder fixture; contains no game policy.</summary>
internal sealed class TestCaptureProfile : IVisionCaptureProfile
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name => "Preview fixture";
    public string CameraView => "Fixture camera";
    public string ConfigurationFingerprint => new('a', 64);
    public string AnimationProfile => "preview-fixture";
    public bool UsesSupervisionOverlay => false;
    public void Validate() { }
    public object ExportProfile() => new { Id, Name };
    public void ValidateCapture(VisionTrainingCaptureSettings settings) => settings.Validate();
    public IReadOnlyList<VisionEvidenceRegion> EvidenceRegions(VisionSettings settings) =>
        [new("primary", "frames.bgra", 160, 160, settings.Crop)];
}
