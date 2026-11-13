using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;
using Microsoft.ML.OnnxRuntime;

/// <summary>Opt-in, synthetic NVIDIA integration checks. Never creates a runtime,
/// controller output, capture window, validation certificate or promotion report.</summary>
internal static class CudaPreprocessingProbe
{
    internal static int Run(string[] args)
    {
        string? Argument(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string? output = Argument("--out");
        FileStream? evidence = null;
        object? report = null; int exit = 1;
        try
        {
            if (output is not null) evidence = new FileStream(Path.GetFullPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Actual D3D11/CUDA interoperability requires Windows and an NVIDIA GPU.");
            nint adapter = CudaD3D11Interop.FindAdapter(); CaptureD3D11 graphics;
            try { graphics = new CaptureD3D11(adapter: adapter); } finally { Marshal.Release(adapter); }
            using (graphics)
            {
                var tensor = TensorChecks(graphics);
                string? model = Argument("--model"), app = Argument("--app-directory");
                object? inference = null;
                if (model is not null)
                {
                    if (app is null) throw new ArgumentException("Pass --app-directory with the complete published app when requesting inference parity.");
                    ConfigureOrt(app);
                    inference = InferenceChecks(graphics, Path.GetFullPath(model));
                }
                exit = inference is not null ? 0 : 3;
                report = new { schemaVersion = 1, status = inference is not null ? "passed" : "partial", tensorInterop = tensor,
                    inferenceParity = inference, reason = inference is null ? "Tensor interoperability passed; provide --model and --app-directory to check actual inference." : null,
                    fixture = "Synthetic CPU-uploaded source images; verification explicitly downloads tensors for comparison.",
                    hardwarePerformanceAccepted = false, gameplayAccuracyValidated = false, promotionApproved = false };
            }
        }
        catch (Exception error)
        {
            bool unavailable = error is NotSupportedException or DllNotFoundException or EntryPointNotFoundException;
            exit = unavailable ? 2 : 1;
            report = new { schemaVersion = 1, status = unavailable ? "unavailable" : "failed", reason = error.Message,
                hardwarePerformanceAccepted = false, gameplayAccuracyValidated = false, promotionApproved = false };
        }
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            if (evidence is not null) { using var writer = new StreamWriter(evidence); writer.Write(json); }
            Console.WriteLine(json);
        }
        catch (IOException error) { Console.Error.WriteLine("Could not save probe evidence: " + error.Message); exit = 1; }
        finally { evidence?.Dispose(); }
        return exit;
    }

    private static object TensorChecks(CaptureD3D11 graphics)
    {
        var manifest = new VisionModelManifest { InputWidth = 32, InputHeight = 32, HistoryLength = 4,
            RgbMean = [.13f, -.21f, .37f], RgbStandardDeviation = [.31f, .53f, 1.71f] };
        var settings = VisionSettings.Default with { Crop = new(.137, .213, .681, .537), PreprocessingBackend = VisionPreprocessingBackend.D3D11Cuda };
        using var gpu = new CudaTemporalFrameInput(manifest, settings);
        var expected = new float[4 * 3 * 32 * 32]; var actual = new float[expected.Length];
        var cpu = new CpuTemporalFrameInput(manifest, settings, expected);
        using var pool = new GpuCaptureBuffer(graphics);
        nint texture = 0; double maximum = 0; int count = 0;
        try
        {
            for (int shape = 0; shape < 2; shape++)
            {
                int width = shape == 0 ? 53 : 35, height = shape == 0 ? 41 : 19;
                var bytes = GpuPreprocessingChecks.Image(width, height);
                D3D11ComputeNative.Release(ref texture); texture = GpuPreprocessingChecks.Texture(graphics, width, height, bytes);
                pool.Clear(); cpu.ResetContent(); gpu.ResetContent();
                for (int sample = 0; sample < 8; sample++)
                {
                    for (int p = 2; p < bytes.Length; p += 4) bytes[p] = (byte)(bytes[p] + 7);
                    GpuPreprocessingChecks.UpdateTexture(graphics, texture, bytes, width * 4);
                    CapturedVisionFrame? frame = null;
                    Require(pool.Publish(texture, width, height, 100 + count, 1 + count, 1 + shape) && pool.TryAcquireLatest(out frame), "CUDA probe could not acquire its owned GPU frame.");
                    using (frame!)
                    using (var reference = new CapturedVisionFrame(bytes, width, height, 100 + count, 1 + count, 1 + shape, () => { }))
                    {
                        int index = Math.Min(sample, 3); bool shift = sample >= 4;
                        bool expectedChanged = cpu.PrepareFrame(reference, index, shift), changed = gpu.PrepareFrame(frame!, index, shift);
                        Require(changed == expectedChanged && frame!.OptionalReadbackBytes == 0 && frame.FreshnessReadbackBytes == 16,
                            "GPU input preparation downloaded an image or changed content semantics.");
                        gpu.ReadTensorForVerification(actual);
                        maximum = Math.Max(maximum, Compare(expected, actual, 2e-6, 2e-6));
                        if (sample == 7)
                        {
                            cpu.RestartWithCurrentFrame(index); gpu.RestartWithCurrentFrame(index);
                            gpu.ReadTensorForVerification(actual); maximum = Math.Max(maximum, Compare(expected, actual, 2e-6, 2e-6));
                        }
                    }
                    count++;
                }
            }
        }
        finally { D3D11ComputeNative.Release(ref texture); }
        return new { passed = true, frames = count, maximumAbsoluteError = maximum, chronologicalHistory = true,
            resizeAndReacquisition = true, cpuImageReadbackDuringPreparation = false, freshnessReadbackBytesPerPreparedFrame = 16,
            verificationTensorReadback = true };
    }

    private static object InferenceChecks(CaptureD3D11 graphics, string manifestPath)
    {
        var (manifest, _) = VisionModelManifest.Read(manifestPath);
        const int width = 53, height = 41;
        var cpuSettings = VisionSettings.Default with { ExecutionProvider = VisionExecutionProvider.Cuda,
            AnimationProfile = manifest.AnimationProfile, CameraView = manifest.CameraConfiguration,
            Crop = new(.137, .213, .681, .537), MaximumFrameAgeMs = 1000, MinimumConfidence = 0, MaximumUncertaintyMs = 1000 };
        var gpuSettings = cpuSettings with { PreprocessingBackend = VisionPreprocessingBackend.D3D11Cuda };
        using var cpu = VisionModel.Load(manifestPath, cpuSettings);
        using var gpu = VisionModel.Load(manifestPath, gpuSettings, deviceInputFactory:
            (model, settings) => new CudaTemporalFrameInput(model, settings, graphics, width, height));
        Require(!gpu.CanArm && !cpu.CanArm, "Synthetic parity settings unexpectedly authorized real assistance.");
        var bytes = GpuPreprocessingChecks.Image(width, height); nint texture = 0;
        using var pool = new GpuCaptureBuffer(graphics);
        int decisions = 0; double maximum = 0; long prior = 0;
        try
        {
            texture = GpuPreprocessingChecks.Texture(graphics, width, height, bytes);
            for (int sample = 0; sample < manifest.HistoryLength + 4; sample++)
            {
                while (prior != 0 && Stopwatch.GetElapsedTime(prior).TotalMilliseconds < manifest.HistoryIntervalMs) Thread.Sleep(1);
                for (int p = 2; p < bytes.Length; p += 4) bytes[p] = (byte)(bytes[p] + 7);
                GpuPreprocessingChecks.UpdateTexture(graphics, texture, bytes, width * 4);
                long ticks = Stopwatch.GetTimestamp(); prior = ticks;
                CapturedVisionFrame? frame = null;
                Require(pool.Publish(texture, width, height, ticks, sample + 1, 1) && pool.TryAcquireLatest(out frame), "Inference parity could not acquire its GPU frame.");
                using (frame!)
                using (var reference = new CapturedVisionFrame(bytes, width, height, ticks, sample + 1, 1, () => { }))
                {
                    var state = default(VisionStatusSnapshot) with { SessionGeneration = 1 };
                    var a = cpu.PushFrame(reference, state); var b = gpu.PushFrame(frame!, state);
                    Require(a.HasValue == b.HasValue, "CPU/GPU inference admitted different temporal samples.");
                    if (a.HasValue)
                    {
                        Require(cpu.LatestObservation is { } && gpu.LatestObservation is { }, "Inference parity used a non-inference observation.");
                        maximum = Math.Max(maximum, Compare(Values(cpu.LatestObservation!.Value), Values(gpu.LatestObservation!.Value), 1e-4, 1e-5));
                        decisions++;
                    }
                }
            }
        }
        finally { D3D11ComputeNative.Release(ref texture); }
        Require(decisions >= 3, "Fewer than three actual device-bound inference pairs completed.");
        return new { passed = true, decisions, maximumAbsoluteError = maximum, executionProvider = "CUDAExecutionProvider",
            modelSha256 = manifest.ModelSha256, preprocessing = manifest.Preprocessing,
            scope = "Numerical synthetic inference parity only; not held-out gameplay or hardware timing acceptance." };
    }
    private static float[] Values(ModelObservation value) => [(float)value.PlayerConfidence, (float)value.ShotConfidence,
        (float)value.TimeToTargetMs, (float)value.UncertaintyMs, (float)value.Phase];
    private static double Compare(float[] expected, float[] actual, double absolute, double relative)
    {
        Require(expected.Length == actual.Length, "Parity dimensions differ."); double maximum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            double error = Math.Abs(expected[i] - actual[i]); maximum = Math.Max(maximum, error);
            Require(float.IsFinite(actual[i]) && error <= absolute + relative * Math.Abs(expected[i]), $"GPU parity differs at {i}: {expected[i]} vs {actual[i]}.");
        }
        return maximum;
    }
    private static void ConfigureOrt(string directory)
    {
        directory = Path.GetFullPath(directory); string runtime = Path.Combine(directory, "onnxruntime.dll");
        if (!File.Exists(runtime)) throw new FileNotFoundException("Choose the complete published app containing onnxruntime.dll.", runtime);
        var paths = new List<string> { directory };
        string cuda = Path.Combine(directory, "CudaRuntime");
        if (Directory.Exists(cuda)) paths.AddRange(Directory.EnumerateFiles(cuda, "*.dll", SearchOption.AllDirectories).Select(Path.GetDirectoryName).OfType<string>().Distinct());
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, paths) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        OrtEnv.DisableDllImportResolver = true;
        NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, assembly, search) =>
            name is "onnxruntime" or "onnxruntime.dll" ? NativeLibrary.Load(runtime) : 0);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
