using System.Diagnostics;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Packs;
using EliteScriptStudio.Packs.Nba2K27;
using EliteScriptStudio.Vision;

internal static class TrainingPluginChecks
{
    internal static void CheckAcquisitionWorker(string evidence)
    {
        var profile = new Nba2K27ShotProfile(Guid.NewGuid(), "Acquisition concurrency", 83, 91, "Base", "Upper 1", "Upper 2", true, "Push");
        var settings = ((IVisionCaptureProfile)profile).PrepareCalibrationCapture(VisionSettings.Default, 64, 64);
        using var tracker = new BlockedTracker();
        using var analysis = new VisionAcquisitionWorker(tracker);
        using var recorder = new VisionTrainingRecorder(Path.GetFullPath(evidence), profile, settings, "Synthetic concurrency check");
        var pixels = new byte[64 * 64 * 4]; pixels[0] = 79;
        using (var first = new CapturedVisionFrame(pixels, 64, 64, Stopwatch.GetTimestamp(), 1, 7, () => { }))
        {
            Check(analysis.TryQueue(first), "Initial setup analysis was not queued.");
            Check(tracker.Started.Wait(TimeSpan.FromSeconds(2)), "Setup analysis did not start.");
            for (int i = 1; i <= 12; i++)
            {
                using var next = new CapturedVisionFrame(pixels, 64, 64, Stopwatch.GetTimestamp(), i + 1, 7, () => { });
                Check(!analysis.TryQueue(next), "Setup analysis accumulated a frame backlog.");
                recorder.Capture(next, default);
                Thread.Sleep(3);
            }
        }
        pixels[0] = 1; // The analysis must own its copy after the capture lease ends.
        Check(analysis.TakeCompleted() is null, "Blocked analysis unexpectedly completed.");
        recorder.Stop(complete: true); recorder.Dispose();
        Check(recorder.Snapshot is { IsComplete: true, StoredFrames: > 0 }, "Raw recording waited for blocked analysis.");
        tracker.Release.Set();
        VisionAcquisitionWorker.Result? result = null;
        Check(SpinWait.SpinUntil(() => (result = analysis.TakeCompleted()) is not null, TimeSpan.FromSeconds(2)), "Analysis did not return its frame.");
        using (result!.Preview)
            Check(result.Generation == 7 && result.Preview.Sequence == 1 && result.Preview.Tracking?.Sequence == 1 && result.Preview.Pixels[0] == 79,
                "Analysis pixels, source generation and observation lost their exact frame association.");
        Console.WriteLine($"SUCCESS: raw writer progresses while analysis is blocked; one in-flight frame, owned pixels and matching sequence/generation. {recorder.DirectoryPath}");
    }
    private sealed class BlockedTracker : IVisionRegionTracker
    {
        internal readonly ManualResetEventSlim Started = new(), Release = new();
        public VisionRegionObservation Track(ReadOnlySpan<byte> pixels, int width, int height, int stride, long sequence, long generation)
        { Started.Set(); Release.Wait(); return new(sequence, true, new(0, 0, 1, 1), null, [], 1, "Synthetic blocked analysis"); }
        public void RequestStop() => Release.Set();
        public void Dispose() { Release.Set(); Started.Dispose(); Release.Dispose(); }
    }

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
        AnchoredRoundTrip(root, profile);
        AutomaticRoundTrip(root, profile);
        CalibrationRoundTrip(root, profile);
        Console.WriteLine($"SUCCESS: installation gating, unsupported-adapter rejection, scene geometry, persistence and actual scene writer. Evidence: {root}");
    }
    private static void CalibrationRoundTrip(string root, Nba2K27ShotProfile profile)
    {
        var settings = ((IVisionCaptureProfile)profile).PrepareCalibrationCapture(VisionSettings.Default, 64, 64);
        var pixels = new byte[64 * 64 * 4]; pixels[0] = 11; pixels[^4] = 123;
        using var recorder = new VisionTrainingRecorder(root, profile, settings, "Full scene calibration");
        for (int i = 1; i <= 2; i++)
        {
            using var frame = new CapturedVisionFrame(pixels, 64, 64, Stopwatch.GetTimestamp(), i, 1, () => { });
            // Even a failed/incorrect region detector must not alter or discard raw acquisition pixels.
            recorder.Capture(frame, default, new(i, false, new(.3, .3, .2, .2), null, [], 0, "No meter detected"));
        }
        recorder.Stop(complete: true); recorder.Dispose();
        Check(recorder.Snapshot is { IsComplete: true, AcquisitionOnly: true, StoredFrames: 2 }, "Detector loss discarded the calibration clip.");
        byte[] primary = File.ReadAllBytes(Path.Combine(recorder.DirectoryPath, "frames.bgra"));
        byte[] scene = File.ReadAllBytes(Path.Combine(recorder.DirectoryPath, "scene.bgra"));
        Check(primary[0] == 11 && scene[0] == 11 && scene[64 * 64 * 4 - 4] == 123, "A detected player crop replaced full-scene acquisition.");
        Check(!File.Exists(Path.Combine(recorder.DirectoryPath, "feedback.bgra")), "An unknown feedback area was represented as a known crop.");
        File.WriteAllText(Path.Combine(root, "calibration-clip-path.txt"), recorder.DirectoryPath);
        Console.WriteLine("SUCCESS: full calibration scene survives detector failure, with no fabricated meter/feedback regions. " + recorder.DirectoryPath);
    }
    private static void AutomaticRoundTrip(string root, Nba2K27ShotProfile initial)
    {
        var profile = initial with { Gamertag = "DrakulaVlad2" };
        var configuration = new Nba2K27AutomaticCapture { Gamertag = profile.Gamertag, MeterOn = true, Feedback = new(.7, .02, .2, .1), RelativeMeter = new(1.5, 0, .2, .4) };
        var settings = VisionSettings.Default with { Crop = new(.2, .2, .2, .4), TrainingCapture = new() { Configured = true,
            SourceWidth = 64, SourceHeight = 64, EvidenceMode = VisionEvidenceMode.Automatic, GameBuild = "synthetic-build",
            MeterRegion = new(.6, .2, .05, .3), FeedbackRegion = configuration.Feedback,
            PluginConfiguration = JsonSerializer.SerializeToElement(configuration, Nba2K27CaptureConfiguration.Json) } };
        using var recorder = new VisionTrainingRecorder(root, profile, settings, "Synthetic automatic regions");
        var pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 37; pixels[i + 3] = 255; }
        for (int i = 1; i <= 2; i++)
        {
            using var frame = new CapturedVisionFrame(pixels, 64, 64, Stopwatch.GetTimestamp(), i, 1, () => { });
            var observation = new VisionRegionObservation(i, true, settings.Crop, settings.TrainingCapture.MeterRegion,
                [new(.01, .4), new(.99, .4), new(.99, .99), new(.01, .99)], .95, "Synthetic automatic identity",
                new(profile.Gamertag, profile.Gamertag, true, .98, i), Enumerable.Repeat(new VisionKeypoint(.3, .4, .9), 17).ToArray(),
                configuration.Feedback, true, .9, true, settings.Crop, "synthetic");
            recorder.Capture(frame, default, observation);
        }
        recorder.Stop(complete: true); recorder.Dispose();
        Check(recorder.Snapshot.IsComplete && recorder.Snapshot.StoredFrames == 2, "Automatic recorder failed to finish a valid take.");
        File.WriteAllText(Path.Combine(root, "automatic-clip-path.txt"), recorder.DirectoryPath);
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(recorder.DirectoryPath, "session.json")));
        Check(metadata.RootElement.GetProperty("captureEvidence").GetProperty("version").GetInt32() == 4
            && metadata.RootElement.GetProperty("frames")[0].GetProperty("tracking").GetProperty("keypoints").GetArrayLength() == 17,
            "Version-4 identity/pose metadata was not recorded.");
        Console.WriteLine("SUCCESS: actual version-4 recorder stored exact identity and 17 joints; synthetic take path: " + recorder.DirectoryPath);
    }
    private static void AnchoredRoundTrip(string root, Nba2K27ShotProfile profile)
    {
        var player = new VisionCrop(.2, .2, .2, .4);
        var config = new Nba2K27CaptureConfiguration { Player = player, Meter = new(.7, .25, .1, .3),
            Court = [new(.05, .4), new(.95, .4), new(.99, .99), new(.01, .99)],
            ReferenceWidth = 256, ReferenceHeight = 256, ReferenceGray = new byte[65536] };
        var settings = VisionSettings.Default with { Crop = player, TrainingCapture = new() { Configured = true,
            SourceWidth = 64, SourceHeight = 64, EvidenceMode = VisionEvidenceMode.Anchored, GameBuild = "synthetic-build",
            MeterRegion = config.Meter, FeedbackRegion = new(.5, .02, .3, .1),
            PluginConfiguration = JsonSerializer.SerializeToElement(config, Nba2K27CaptureConfiguration.Json) } };
        var store = new VisionSettingsStore(Path.Combine(root, "anchor-settings")); store.Save(profile.Id, settings);
        Check(Nba2K27CaptureConfiguration.Read(store.Load(profile.Id).TrainingCapture.PluginConfiguration).ReferenceGray.Length == 65536,
            "The saved visual reference failed to reload after exceeding the legacy 32 KiB settings bound.");
        var pixels = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        { int i = (y * 64 + x) * 4; pixels[i] = (byte)x; pixels[i + 1] = (byte)y; pixels[i + 3] = 255; }
        using var recorder = new VisionTrainingRecorder(root, profile, settings, "Synthetic tracked scene");
        for (int i = 0; i < 2; i++)
        {
            var region = player with { Left = player.Left + i * .2 };
            using var frame = new CapturedVisionFrame(pixels, 64, 64, Stopwatch.GetTimestamp(), i + 1, 1, () => { }) { PlayerCrop = region };
            var tracking = new VisionRegionObservation(i + 1, true, region, config.Meter, config.Court, .95, "Synthetic");
            recorder.Capture(frame, default, tracking);
            var manifest = new VisionModelManifest { InputWidth = 32, InputHeight = 32, HistoryLength = 2 };
            var history = new float[2 * 3 * 32 * 32];
            new CpuTemporalFrameInput(manifest, settings, history).PrepareFrame(frame, 0, false);
            var expected = new float[3 * 32 * 32];
            FramePreprocessor.FillRgb(pixels, 64, 64, 256, region, 32, 32, manifest.RgbMean, manifest.RgbStandardDeviation, expected);
            Check(history.AsSpan(0, expected.Length).SequenceEqual(expected), "Live preprocessing ignored the tracked crop.");
        }
        using (var lost = new CapturedVisionFrame(pixels, 64, 64, Stopwatch.GetTimestamp(), 3, 1, () => { }))
            recorder.Capture(lost, default, new(3, false, player, config.Meter, config.Court, 0, "Lost identity"));
        recorder.Dispose();
        Check(!recorder.Snapshot.IsComplete && recorder.Snapshot.StoredFrames == 2, "Lost tracking did not stop the take as incomplete.");
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "session.json")));
        var data = metadata.RootElement;
        Check(data.GetProperty("captureEvidence").GetProperty("version").GetInt32() == 3
            && data.GetProperty("frames")[1].GetProperty("tracking").GetProperty("player").GetProperty("left").GetDouble() == .4,
            "Per-frame crop provenance was lost.");
        var stored = File.ReadAllBytes(Path.Combine(recorder.DirectoryPath, "frames.bgra"));
        Check(stored[0] == 12 && stored[VisionTrainingRecorder.FrameBytes] == 25, "Recorded pixels used the initial stationary crop.");
        Check((new VisionModelManifest { InputRegionPolicy = "visual-anchors-v1" }).CheckArmingEvidence("unused", settings).Contains("anchor"),
            "An unaccepted anchor pipeline could arm.");
        Console.WriteLine("SUCCESS: tracked crop pixels/provenance, live preprocessing parity and recording stop on identity loss. Synthetic software check only.");
    }
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
}
