using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;
using Microsoft.ML.OnnxRuntime;

internal static class RecordedWindowProbe
{
    internal static void Run(string manifestPath, string tensorPath, string provider, string runtimeRoot)
    {
        string runtime = Path.Combine(Path.GetFullPath(runtimeRoot), "onnxruntime.dll");
        if (!File.Exists(runtime)) throw new FileNotFoundException("Choose the installed app's native runtime explicitly.", runtime);
        OrtEnv.DisableDllImportResolver = true;
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
            name is "onnxruntime" or "onnxruntime.dll" ? NativeLibrary.Load(runtime) : 0);
        var backend = provider switch { "cpu" => VisionExecutionProvider.Cpu, "cuda" => VisionExecutionProvider.Cuda,
            _ => throw new InvalidDataException("Choose cpu or cuda explicitly.") };
        var manifest = VisionModelManifest.Read(manifestPath).Manifest;
        var settings = VisionSettings.Default with { AnimationProfile = manifest.AnimationProfile, ExecutionProvider = backend };
        using var adapter = VisionModel.Load(manifestPath, settings);
        if (adapter.CanArm) throw new InvalidDataException("Use this probe only with an observation-only candidate.");
        int length = checked(manifest.HistoryLength * 3 * manifest.InputHeight * manifest.InputWidth);
        if (new FileInfo(tensorPath).Length != checked(length * sizeof(float)))
            throw new InvalidDataException("Recorded tensor size differs from the model input contract.");
        byte[] bytes = File.ReadAllBytes(tensorPath);
        var input = GC.AllocateUninitializedArray<float>(length, pinned: true);
        Buffer.BlockCopy(bytes, 0, input, 0, bytes.Length);
        if (input.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Tensor contains nonfinite values.");
        var output = GC.AllocateUninitializedArray<float>(5, pinned: true);
        using var options = VisionModel.CreateSessionOptions(backend);
        using var session = new InferenceSession(VisionModelManifest.ResolveChildFile(manifestPath, manifest.ModelFile), options);
        using var inputValue = OrtValue.CreateTensorValueFromMemory(input, [1, manifest.HistoryLength, 3, manifest.InputHeight, manifest.InputWidth]);
        using var outputValue = OrtValue.CreateTensorValueFromMemory(output, [1, 5]);
        using var run = new RunOptions();
        string[] names = [manifest.InputName], outputs = [manifest.OutputName];
        OrtValue[] inputs = [inputValue], values = [outputValue];
        for (int warm = 0; warm < 10; warm++) session.Run(run, names, inputs, outputs, values);
        double[] samples = new double[100];
        for (int index = 0; index < samples.Length; index++)
        {
            long start = Stopwatch.GetTimestamp();
            session.Run(run, names, inputs, outputs, values);
            samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        VisionModel.ValidateObservation(output, manifest.MaximumPredictionHorizonMs);
        Array.Sort(samples);
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, executionProvider = settings.ExecutionProviderName,
            manifest.ModelSha256, inputSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), observation = output,
            nativeRuntime = runtime,
            samples = samples.Length, p50Ms = samples[49], p95Ms = samples[94], p99Ms = samples[98], canArm = adapter.CanArm,
            scope = "C# ONNX inference on a recorded, preprocessed window including host transfers. Excludes capture, pose/OCR, preprocessing, Remote Play and controller delivery.",
            hardwareAcceptance = false }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
}
