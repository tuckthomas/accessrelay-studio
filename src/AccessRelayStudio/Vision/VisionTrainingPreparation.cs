using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

/// <summary>Offline exact-frame annotation. Original scene/controller evidence is never overwritten.</summary>
internal static class VisionTrainingPreparation
{
    private const string AnalysisPolicy = "identity-pose-multicolor-floor-v3";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    internal static string Prepare(string directory, IVisionCaptureProfile profile, VisionSettings settings,
        CancellationToken cancellation = default, Action<int, int>? progress = null,
        Func<VisionCaptureModelSet, IVisionAnalysisModels>? models = null)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string Child(string name)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Recorded evidence cannot redirect to another file.");
            return path;
        }
        byte[] metadata = File.ReadAllBytes(Child("session.json"));
        if (metadata.Length > 4 * 1024 * 1024) throw new InvalidDataException("The clip metadata exceeds its bound.");
        var source = JsonNode.Parse(metadata)?.AsObject() ?? throw new InvalidDataException("Empty clip metadata.");
        if (source["captureEvidence"]?["version"]?.GetValue<int>() != 2) return directory;
        if (source["schemaVersion"]?.GetValue<int>() != 1 || source["complete"]?.GetValue<bool>() != true
            || source["stopwatchFrequency"]?.GetValue<long>() != Stopwatch.Frequency
            || source["shotProfileFingerprint"]?.GetValue<string>() != profile.ConfigurationFingerprint
            || source["animationProfile"]?.GetValue<string>() != profile.AnimationProfile
            || source["captureEvidence"]?["tracking"]?.GetValue<string>() != "unannotated-scene")
            throw new InvalidDataException("Choose a completed raw clip matching the selected player profile and capture clock.");
        int originalWidth = source["captureWidth"]!.GetValue<int>(), originalHeight = source["captureHeight"]!.GetValue<int>();
        if (originalWidth is < 1 or > 16384 || originalHeight is < 1 or > 16384)
            throw new InvalidDataException("Invalid recorded source dimensions.");
        var frames = source["frames"]!.AsArray();
        if (frames.Count is < 4 or > 10000) throw new InvalidDataException("Choose a clip with 4–10000 frames.");
        long previousTicks = -1, previousSequence = -1, generation = frames[0]!["sourceGeneration"]!.GetValue<long>();
        foreach (var frame in frames)
        {
            long ticks = frame!["captureTicks"]!.GetValue<long>(), sequence = frame["sequence"]!.GetValue<long>();
            if (ticks <= previousTicks || sequence <= previousSequence || frame["sourceGeneration"]!.GetValue<long>() != generation)
                throw new InvalidDataException("Source frames must retain one generation and increasing receipt timestamps/sequences.");
            previousTicks = ticks; previousSequence = sequence;
        }
        var scene = source["captureEvidence"]!["streams"]!.AsArray().Single(s => s?["name"]?.GetValue<string>() == "scene")!;
        int width = scene["width"]!.GetValue<int>(), height = scene["height"]!.GetValue<int>();
        if (scene["file"]!.GetValue<string>() != "scene.bgra" || scene["pixelFormat"]!.GetValue<string>() != "BGRA8"
            || width is < 1 or > 1280 || height is < 1 or > 1280 || scene["stride"]!.GetValue<int>() != width * 4
            || scene["crop"].Deserialize<VisionCrop>(Json) != new VisionCrop(0, 0, 1, 1))
            throw new InvalidDataException("Offline preparation requires the complete recorded gameplay scene.");
        int frameBytes = checked(width * height * 4);
        using var pixelsFile = File.Open(Child("scene.bgra"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (pixelsFile.Length != (long)frameBytes * frames.Count || pixelsFile.Length > VisionTrainingRecorder.SceneEvidenceByteLimit)
            throw new InvalidDataException("The recorded scene is truncated or exceeds its size bound.");
        string sessionHash = Convert.ToHexStringLower(SHA256.HashData(metadata));
        string sceneHash = Convert.ToHexStringLower(SHA256.HashData(pixelsFile)); pixelsFile.Position = 0;
        var layout = source["captureEvidence"]!["layout"].Deserialize<VisionTrainingCaptureSettings>(Json)!;
        var version = File.Exists(Child("game-version.json")) ? JsonNode.Parse(File.ReadAllBytes(Child("game-version.json"))) : null;
        if (version is not null)
        {
            if (version["sessionSha256"]?.GetValue<string>() != sessionHash)
                throw new InvalidDataException("The inspected game version belongs to a different clip.");
            layout = layout with { GameBuild = version["installedGameVersion"]!.GetValue<string>() + " (" + version["platform"]!.GetValue<string>() + ")" };
        }
        if (string.IsNullOrWhiteSpace(layout.GameBuild)) throw new InvalidDataException("Record the exact game build before preparing this clip.");
        string destination = directory + "-tracked-v1";
        if (File.Exists(Child("prepared-clip.json")))
        {
            var pointer = JsonNode.Parse(File.ReadAllBytes(Child("prepared-clip.json")));
            string? name = pointer?["directoryName"]?.GetValue<string>();
            if (name is not null && Path.GetFileName(name) == name && name.StartsWith(Path.GetFileName(directory) + "-tracked-v1", StringComparison.Ordinal))
                destination = Path.Combine(Path.GetDirectoryName(directory)!, name);
        }
        if (Directory.Exists(destination))
        {
            var saved = JsonNode.Parse(File.ReadAllBytes(Path.Combine(destination, "session.json")));
            if (saved?["complete"]?.GetValue<bool>() == true && saved["preparation"]?["sourceSessionSha256"]?.GetValue<string>() == sessionHash
                && saved["preparation"]?["cropPolicy"]?.GetValue<string>() == "pose-envelope-hud-disjoint-v1"
                && saved["preparation"]?["analysisPolicy"]?.GetValue<string>() == AnalysisPolicy
                && saved["captureEvidence"]?["layout"]?["gameBuild"]?.GetValue<string>() == layout.GameBuild
                && saved["preparation"]?["sourceSceneSha256"]?.GetValue<string>() == sceneHash) return destination;
            // Keep interrupted output available for diagnosis; retry in a fresh folder.
            destination += "-" + Guid.NewGuid().ToString("N")[..8];
        }
        settings = profile.PrepareAutomaticCapture(settings with { AnimationProfile = profile.AnimationProfile,
            CameraView = profile.CameraView, TrainingCapture = layout });
        var pixels = new byte[frameBytes];
        VisionRegionObservation Read(IVisionRegionTracker tracker, int index)
        {
            cancellation.ThrowIfCancellationRequested();
            var frame = frames[index]!;
            var entry = frame["evidence"]!.AsArray().Single(e => e?["name"]?.GetValue<string>() == "scene")!;
            if (entry["file"]!.GetValue<string>() != "scene.bgra" || entry["offsetBytes"]!.GetValue<long>() != (long)index * frameBytes
                || entry["byteLength"]!.GetValue<int>() != frameBytes)
                throw new InvalidDataException("The recorded scene index is inconsistent.");
            pixelsFile.Position = (long)index * frameBytes; pixelsFile.ReadExactly(pixels);
            return tracker.Track(pixels, width, height, width * 4, frame["sequence"]!.GetValue<long>(),
                frame["sourceGeneration"]!.GetValue<long>(), frame["captureTicks"]!.GetValue<long>());
        }
        // Learn the fixed feedback location and player-relative meter from this clip, then replay
        // from the beginning with that policy. Neither identity nor pose is copied between frames.
        using (var tracker = profile.CreateRegionTracker(settings, models) ?? throw new InvalidDataException("This plugin has no automatic capture policy."))
        using (cancellation.Register(tracker.RequestStop))
        {
            bool found = false;
            for (int index = 0; index < frames.Count; index++)
            {
                var observation = Read(tracker, index); progress?.Invoke(index, frames.Count * 2);
                if (!observation.AcquisitionComplete || !observation.Valid || (profile.UsesSupervisionOverlay && !observation.MeterDetected)) continue;
                settings = profile.CompleteAutomaticCapture(settings, observation, originalWidth, originalHeight);
                found = true; break;
            }
            if (!found) throw new InvalidDataException("The full recording is safe. Automatic analysis could not acquire this player and the required HUD; review the raw clip.");
        }
        profile.ValidateCapture(settings.TrainingCapture);
        var regions = profile.EvidenceRegions(settings);
        long outputBound = regions.Sum(r => (long)r.Width * r.Height * 4) * frames.Count;
        if (outputBound > 4L * 1024 * 1024 * 1024 || new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < outputBound + 64L * 1024 * 1024)
            throw new IOException("There is not enough space for the prepared player/HUD evidence.");
        Directory.CreateDirectory(destination);
        var output = (JsonObject)source.DeepClone();
        output["sessionId"] = Path.GetFileName(destination); output["complete"] = false;
        output["crop"] = JsonSerializer.SerializeToNode(settings.Crop, Json);
        output["preprocessing"] = "bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2";
        output["captureEvidence"]!["version"] = 4; output["captureEvidence"]!["tracking"] = "identity-pose-v1";
        output["captureEvidence"]!["acquisitionOnly"] = false;
        output["captureEvidence"]!["layout"] = JsonSerializer.SerializeToNode(settings.TrainingCapture, Json);
        output["captureEvidence"]!["streams"] = JsonSerializer.SerializeToNode(regions.Select(r => new { r.Name, r.File, r.Width, r.Height,
            stride = r.Width * 4, pixelFormat = "BGRA8", r.Crop }), Json);
        var indexOut = new JsonArray(); output["frames"] = indexOut;
        var results = new List<object>();
        output["preparation"] = JsonSerializer.SerializeToNode(new { version = 1, sourceSessionId = source["sessionId"]!.GetValue<string>(),
            cropPolicy = "pose-envelope-hud-disjoint-v1",
            analysisPolicy = AnalysisPolicy,
            sourceSessionSha256 = sessionHash, sourceSceneSha256 = sceneHash, sourceFrameCount = frames.Count,
            analysisWidth = width, analysisHeight = height, createdAtUtc = DateTimeOffset.UtcNow,
            gameVersionFile = version is not null ? "source-game-version.json" : null,
            timing = "Original frame receipt timestamps and sequence numbers; no interpolated poses or target labels.",
            rawMetadataFile = "acquisition-session.json", frameReportFile = "frame-results.json" }, Json);
        void Save() => File.WriteAllBytes(Path.Combine(destination, "session.json"), JsonSerializer.SerializeToUtf8Bytes(output, Json));
        File.WriteAllBytes(Path.Combine(destination, "acquisition-session.json"), metadata); Save();
        if (version is not null) File.Copy(Child("game-version.json"), Path.Combine(destination, "source-game-version.json"));
        if (source["captureEvidence"]?["controllerTraceFile"]?.GetValue<string>() is { } trace)
        {
            if (trace != "controller-trace.json") throw new InvalidDataException("Unexpected controller evidence file.");
            File.Copy(Child(trace), Path.Combine(destination, trace));
        }
        var writers = regions.Select(r => new FileStream(Path.Combine(destination, r.File), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 1024 * 1024)).ToArray();
        try
        {
            using var tracker = profile.CreateRegionTracker(settings, models)!;
            using var registration = cancellation.Register(tracker.RequestStop);
            for (int index = 0; index < frames.Count; index++)
            {
                var observation = Read(tracker, index);
                string? rejection = Rejection(observation, profile.UsesSupervisionOverlay, originalWidth, originalHeight);
                results.Add(new { sourceIndex = index, sequence = observation.Sequence, usable = rejection is null,
                    reason = rejection ?? "Identity/pose and overlay-disjoint player crop available.", observation });
                progress?.Invoke(frames.Count + index, frames.Count * 2);
                if (rejection is not null) continue;
                var frame = (JsonObject)frames[index]!.DeepClone();
                var entries = new JsonArray();
                for (int i = 0; i < regions.Count; i++)
                {
                    var region = regions[i]; int bytes = checked(region.Width * region.Height * 4);
                    long offset = writers[i].Position;
                    VisionCrop crop = region.Name == "player" ? observation.Player : region.Name == "meter" ? observation.Meter!.Value : region.Crop;
                    if (region.Name == "scene" && region.Width == width && region.Height == height) writers[i].Write(pixels);
                    else writers[i].Write(Crop(pixels, width, height, region.Width, region.Height, crop));
                    if (region.Name == "player") frame["offsetBytes"] = offset;
                    else entries.Add(JsonSerializer.SerializeToNode(new { region.Name, region.File, offsetBytes = offset, byteLength = bytes }, Json));
                }
                frame["evidence"] = entries;
                frame["tracking"] = JsonSerializer.SerializeToNode(observation with { TextRegions = null, DetectedPeople = null }, Json);
                frame["sourceFrameIndex"] = index; indexOut.Add(frame);
            }
            if (indexOut.Count < 4) throw new InvalidDataException("The recording is retained, but fewer than four player frames passed tracking and HUD exclusion. Inspect frame-results.json.");
            output["complete"] = true;
            output["completionReason"] = $"Prepared {indexOut.Count}/{frames.Count} exact-frame player crops. Rejected frames remain in the raw recording and frame-results.json. Review shot coverage before training.";
            output["preparation"]!["retainedFrameCount"] = indexOut.Count;
        }
        finally
        {
            foreach (var writer in writers) writer.Dispose();
            File.WriteAllBytes(Path.Combine(destination, "frame-results.json"), JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, frames = results }, Json));
            Save();
        }
        string pointerFile = Child("prepared-clip.json"), temporary = Child("prepared-clip-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, directoryName = Path.GetFileName(destination) }, Json));
        File.Move(temporary, pointerFile, overwrite: true);
        return destination;
    }

    private static string? Rejection(VisionRegionObservation observation, bool meterOn, int width, int height)
    {
        if (!observation.Valid || !observation.AcquisitionComplete || observation.Identity is not { CaseSensitive: true } identity
            || identity.ExpectedTag != identity.ObservedTag || identity.VerifiedSequence > observation.Sequence
            || observation.Keypoints is not { Length: 17 } || observation.Court.Length < 3 || observation.Feedback is null)
            return observation.Message;
        if (meterOn && observation.Meter is null) return "The player-associated meter region is missing.";
        foreach (var overlay in new[] { observation.Meter, observation.Feedback })
            if (overlay is { } hud && observation.Player.Left < hud.Left + hud.Width + 1d / width
                && hud.Left - 1d / width < observation.Player.Left + observation.Player.Width
                && observation.Player.Top < hud.Top + hud.Height + 1d / height
                && hud.Top - 1d / height < observation.Player.Top + observation.Player.Height)
                return "The required player framing overlaps a meter/feedback overlay; pixels were not masked.";
        return null;
    }

    private static byte[] Crop(byte[] pixels, int width, int height, int targetWidth, int targetHeight, VisionCrop crop)
    {
        crop.Validate(); var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++) for (int x = 0; x < targetWidth; x++)
        {
            int sy = Math.Min(height - 1, (int)((crop.Top + (y + .5) / targetHeight * crop.Height) * height));
            int sx = Math.Min(width - 1, (int)((crop.Left + (x + .5) / targetWidth * crop.Width) * width));
            pixels.AsSpan((sy * width + sx) * 4, 4).CopyTo(target.AsSpan((y * targetWidth + x) * 4, 4));
        }
        return target;
    }
}
