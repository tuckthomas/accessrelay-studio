using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;
using EliteScriptStudio.Vision;
using Microsoft.ML.OnnxRuntime;

internal static class AutomaticCaptureModelChecks
{
    internal static void CheckPoseSuppression()
    {
        static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
        var points = Enumerable.Range(0, 17).Select(i => new VisionKeypoint(.44 + i % 3 * .02, .25 + i * .025, .95)).ToArray();
        var first = new VisionPersonPose(new(.4, .2, .16, .6), .95, points);
        var duplicate = first with { Bounds = new(.43, .21, .055, .58), Confidence = .7,
            Keypoints = points.Select(p => p with { X = p.X + .001, Y = p.Y - .001 }).ToArray() };
        Check(VisionAnalysisModels.IsDuplicatePose(first, duplicate, 1920, 1080),
            "Different detection extents split a near-identical skeleton into two people.");
        var different = first with { Keypoints = points.Select(p => p with { X = p.X + .025 }).ToArray() };
        Check(!VisionAnalysisModels.IsDuplicatePose(first, different, 1920, 1080),
            "Distinct skeletons with overlapping detection boxes were suppressed.");
        var limbs = points.ToArray(); foreach (int i in new[] { 9, 10, 15, 16 }) limbs[i] = limbs[i] with { X = limbs[i].X + .1 };
        Check(!VisionAnalysisModels.IsDuplicatePose(first, first with { Keypoints = limbs }, 1920, 1080),
            "Different limb configurations were treated as one person.");
        var sparse = points.Select((p, i) => i < 6 ? p with { Confidence = .1 } : p).ToArray();
        Check(!VisionAnalysisModels.IsDuplicatePose(first, first with { Keypoints = sparse }, 1920, 1080),
            "Insufficient common pose evidence authorized suppression.");
        var missingTorso = points.ToArray(); missingTorso[11] = missingTorso[11] with { Confidence = .1 };
        Check(!VisionAnalysisModels.IsDuplicatePose(first, first with { Keypoints = missingTorso }, 1920, 1080),
            "Missing torso evidence authorized suppression.");
        Console.WriteLine("SUCCESS: the joint comparison identifies duplicate extents and rejects different poses or insufficient evidence. Crowded-scene acceptance remains separate.");
    }

    internal static void TraceScene(string runtimeRoot, string assets, string directory, string output)
    {
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        var paths = new[] { runtimeRoot }.Concat(Directory.EnumerateFiles(Path.Combine(runtimeRoot, "CudaRuntime"), "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>().Distinct());
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, paths) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        OrtEnv.DisableDllImportResolver = true;
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) => name is "onnxruntime" or "onnxruntime.dll" ? NativeLibrary.Load(Path.Combine(runtimeRoot, "onnxruntime.dll")) : 0);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var source = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "session.json")));
        var root = source.RootElement;
        if (!root.GetProperty("complete").GetBoolean()) throw new InvalidDataException("Trace a completed saved clip.");
        var profile = root.GetProperty("shotProfile").Deserialize<Nba2K27ShotProfile>(options)!;
        var scene = root.GetProperty("captureEvidence").GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "scene");
        int width = scene.GetProperty("width").GetInt32(), height = scene.GetProperty("height").GetInt32();
        var pixels = new byte[checked(width * height * 4)];
        using var tracker = new Nba2K27AutomaticTracker(new() { Gamertag = profile.Gamertag, MeterOn = profile.UsesShotMeter, Camera = profile.CameraView },
            () => new VisionAnalysisModels(Nba2K27AutomaticCapture.Models, VisionExecutionProvider.Cuda, assets));
        using var raw = File.OpenRead(Path.Combine(directory, scene.GetProperty("file").GetString()!));
        var results = new List<object>(); int index = 0;
        foreach (var frame in root.GetProperty("frames").EnumerateArray().Take(150))
        {
            raw.ReadExactly(pixels);
            var observation = tracker.Track(pixels, width, height, width * 4, frame.GetProperty("sequence").GetInt64(),
                frame.GetProperty("sourceGeneration").GetInt64(), frame.GetProperty("captureTicks").GetInt64());
            results.Add(new { sourceIndex = index++, observation });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { scope = "First 150 saved frames with diagnostic pose/text candidates. Original evidence unchanged; no live input or labels.", frames = results }, options));
        Console.WriteLine($"Traced {index} saved frames: {output}");
    }

    internal static void PrepareScene(string runtimeRoot, string assets, string directory)
    {
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        var paths = new[] { runtimeRoot }.Concat(Directory.EnumerateFiles(Path.Combine(runtimeRoot, "CudaRuntime"), "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>().Distinct());
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, paths) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        OrtEnv.DisableDllImportResolver = true;
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) => name is "onnxruntime" or "onnxruntime.dll" ? NativeLibrary.Load(Path.Combine(runtimeRoot, "onnxruntime.dll")) : 0);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        using var session = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "session.json")));
        var profile = session.RootElement.GetProperty("shotProfile").Deserialize<Nba2K27ShotProfile>(json)!;
        int previous = -1;
        string prepared = VisionTrainingPreparation.Prepare(directory, profile, VisionSettings.Default,
            progress: (done, total) => { int percent = done * 100 / total; if (percent / 10 != previous) { previous = percent / 10; Console.WriteLine($"Preparing saved evidence: {percent}%"); } },
            models: set => new VisionAnalysisModels(set, VisionExecutionProvider.Cuda, assets));
        Console.WriteLine("Prepared recorded gameplay: " + prepared);
    }
    internal static void ProbeFrame(string runtimeRoot, string assets, string imagePath, string outputPath)
    {
        runtimeRoot = Path.GetFullPath(runtimeRoot);
        string runtime = Path.Combine(runtimeRoot, "onnxruntime.dll");
        var paths = new[] { runtimeRoot }.Concat(Directory.EnumerateFiles(Path.Combine(runtimeRoot, "CudaRuntime"), "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>().Distinct());
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, paths) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        OrtEnv.DisableDllImportResolver = true;
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) => name is "onnxruntime" or "onnxruntime.dll" ? NativeLibrary.Load(runtime) : 0);
        var bmp = File.ReadAllBytes(imagePath); int width = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)), sh = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22));
        int height = Math.Abs(sh), offset = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
        if (BinaryPrimitives.ReadInt16LittleEndian(bmp.AsSpan(28)) != 32) throw new InvalidDataException("Use a native BGRA diagnostic bitmap.");
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) bmp.AsSpan(offset + (sh < 0 ? y : height - 1 - y) * width * 4, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        using var tracker = new Nba2K27AutomaticTracker(new() { Gamertag = "DrakulaVlad2" },
            () => new VisionAnalysisModels(Nba2K27AutomaticCapture.Models, VisionExecutionProvider.Cuda, assets));
        var observation = tracker.Track(pixels, width, height, width * 4, 1, 1);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(observation));
        Console.WriteLine($"Single saved gameplay frame: {observation.Message} Bodies={observation.DetectedPeople?.Length ?? 0}. This is not live tracking acceptance.");
    }
    internal static void Run(string runtimeRoot, string assets, string directory, string provider)
    {
        runtimeRoot = Path.GetFullPath(runtimeRoot); directory = Path.GetFullPath(directory);
        string runtime = Path.Combine(runtimeRoot, "onnxruntime.dll");
        var paths = new[] { runtimeRoot }.Concat(Directory.EnumerateFiles(Path.Combine(runtimeRoot, "CudaRuntime"), "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>().Distinct());
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, paths) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        OrtEnv.DisableDllImportResolver = true;
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) => name is "onnxruntime" or "onnxruntime.dll" ? NativeLibrary.Load(runtime) : 0);
        using var models = new VisionAnalysisModels(Nba2K27AutomaticCapture.Models,
            provider == "cuda" ? VisionExecutionProvider.Cuda : VisionExecutionProvider.Cpu, assets);
        (byte[] Pixels, int Width, int Height) Read(string file)
        {
            var bmp = File.ReadAllBytes(Path.Combine(directory, file));
            int width = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)), signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22));
            int height = Math.Abs(signedHeight), offset = BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));
            if (BinaryPrimitives.ReadInt16LittleEndian(bmp.AsSpan(28)) != 32) throw new InvalidDataException("Test fixture must be BGRA32.");
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) bmp.AsSpan(offset + (signedHeight < 0 ? y : height - 1 - y) * width * 4, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            return (pixels, width, height);
        }
        var text = Read("tag.bmp"); var lines = models.ReadText(text.Pixels, text.Width, text.Height, text.Width * 4);
        File.WriteAllText(Path.Combine(directory, "ocr-" + provider + ".json"), JsonSerializer.Serialize(lines));
        if (!lines.Any(line => Nba2K27AutomaticTracker.MatchesTag(line.Text, "DrakulaVlad2")))
            throw new InvalidOperationException("Actual OCR inference failed exact rendered tag: " + string.Join(" | ", lines.Select(line => line.Text)));
        if (lines.Any(line => Nba2K27AutomaticTracker.MatchesTag(line.Text, "drakulavlad2"))) throw new InvalidOperationException("Case-sensitive tag matching accepted changed capitalization.");
        var image = Read("person.bmp"); var people = models.DetectPeople(image.Pixels, image.Width, image.Height, image.Width * 4);
        File.WriteAllText(Path.Combine(directory, "poses-" + provider + ".json"), JsonSerializer.Serialize(people));
        if (!people.Any(person => person.Keypoints.Length == 17 && person.Keypoints.Count(point => point.Confidence >= .35) >= 8))
            throw new InvalidOperationException("Actual pose inference did not detect the upstream human fixture.");
        Console.WriteLine($"SUCCESS: native {provider} ONNX pose inference ({people.Length} people), OCR ({lines.Length} lines) and exact-case gamertag matching. Model smoke checks only; NBA gameplay accuracy is not certified.");
    }
}
