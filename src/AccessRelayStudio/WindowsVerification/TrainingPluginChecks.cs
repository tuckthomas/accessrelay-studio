using System.Diagnostics;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Packs;
using EliteScriptStudio.Packs.Nba2K27;
using EliteScriptStudio.Vision;

internal static class TrainingPluginChecks
{
    internal static void Run(string evidenceDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(evidenceDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string installed = Path.Combine(root, "installed");
        var registry = new InstalledVisionPlugins(installed, component => Path.Combine(root, component));
        registry.Reload(); Check(registry.Plugins.Count == 0, "An uninstalled adapter was available.");
        var library = new GamePackLibrary(installed);
        void Import(GamePackManifest manifest)
        {
            string path = Path.Combine(root, "source", manifest.Id); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "pack.json"), JsonSerializer.Serialize(manifest, GamePackDefinition.Json));
            library.Import(Path.Combine(path, "pack.json"), CancellationToken.None);
            registry.Reload();
        }
        var pack = new GamePackManifest { Id = "nba2k27", Version = "1.0.0", Name = "Controller package", Description = "Fixture", License = "0BSD", VisionAdapter = "temporal-release-v1" };
        Import(pack); Check(registry.Plugins.Count == 0, "A controller package enabled training.");
        Import(pack with { Id = "unsupported-training", GameId = "another-game", TrainingAdapter = "unknown-v1" });
        Check(registry.Plugins.Count == 0, "An unsupported adapter enabled training.");
        Import(pack with { Id = "nba2k27-training", GameId = "nba2k27", TrainingAdapter = "nba2k27-training-v1" });
        Check(registry.Plugins.Count == 1 && registry.Plugins[0].Id == "nba2k27", "Installing training did not enable the NBA adapter.");
        var plugin = registry.Plugins[0]; registry.Reload(); Check(ReferenceEquals(plugin, registry.Plugins[0]), "Refresh replaced active profile state.");

        var profile = new Nba2K27ShotProfile(Guid.NewGuid(), "Scene acquisition", 83, 91, "Base", "Upper 1", "Upper 2", true, "Push");
        var settings = VisionSettings.Default with { Crop = new(0, 0, 1, 1), TrainingCapture = new() {
            Configured = true, SourceWidth = 2560, SourceHeight = 1440, EvidenceMode = VisionEvidenceMode.SceneFirst,
            FeedbackRegion = new(.4, .1, .2, .1) } };
        ((IVisionCaptureProfile)profile).ValidateCapture(settings.TrainingCapture);
        var regions = ((IVisionCaptureProfile)profile).EvidenceRegions(settings);
        Check(regions.Count == 3 && regions.Single(r => r.Name == "scene") is { Width: 1280, Height: 720 }
            && !regions.Any(r => r.Name == "meter"), "The moving scene was replaced by fixed player/meter crops.");
        var store = new VisionSettingsStore(Path.Combine(root, "settings")); store.Save(profile.Id, settings);
        Check(store.Load(profile.Id).TrainingCapture.EvidenceMode == VisionEvidenceMode.SceneFirst, "Evidence mode failed persistence.");

        // Real writer round-trip with pixels at all corners; no floor mask may clip raised hands.
        var small = settings with { TrainingCapture = settings.TrainingCapture with { SourceWidth = 8, SourceHeight = 8 } };
        using var recorder = new VisionTrainingRecorder(root, profile, small, "Synthetic full scene");
        for (int i = 0; i < 2; i++)
        {
            var pixels = new byte[8 * 8 * 4];
            for (int p = 0; p < 64; p++) { pixels[p * 4] = (byte)(p + i); pixels[p * 4 + 3] = 255; }
            using var frame = new CapturedVisionFrame(pixels, 8, 8, Stopwatch.GetTimestamp(), i + 1, 1, () => { });
            recorder.Capture(frame, default);
        }
        recorder.Stop(complete: true); recorder.Dispose();
        Check(recorder.Snapshot.IsComplete && recorder.Snapshot.StoredFrames == 2, "Scene take did not finalize.");
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "session.json")));
        Check(metadata.RootElement.GetProperty("captureEvidence").GetProperty("version").GetInt32() == 2
            && metadata.RootElement.GetProperty("preprocessing").GetString() == "scene-first-untracked-v1", "Scene evidence was labeled as a tracked player crop.");
        byte[] scene = File.ReadAllBytes(Path.Combine(recorder.DirectoryPath, "scene.bgra"));
        Check(scene[0] == 0 && scene[63 * 4] == 63 && scene[64 * 4] == 1 && scene[127 * 4] == 64,
            "Full scene or synchronized frame pixels were lost.");
        Console.WriteLine($"SUCCESS: installation gating, unsupported-adapter rejection, scene geometry, persistence and actual scene writer. Evidence: {root}");
    }
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
}
