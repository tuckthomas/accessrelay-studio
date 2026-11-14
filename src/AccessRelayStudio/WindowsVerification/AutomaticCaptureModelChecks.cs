using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;
using EliteScriptStudio.Vision;
using Microsoft.ML.OnnxRuntime;

internal static class AutomaticCaptureModelChecks
{
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
