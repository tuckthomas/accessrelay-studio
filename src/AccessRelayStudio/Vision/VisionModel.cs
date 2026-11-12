using System.Diagnostics;
using System.Security.Cryptography;
using EliteScriptStudio.Core;
using Microsoft.ML.OnnxRuntime;

namespace EliteScriptStudio.Vision;

/// <summary>One-thread CUDA session with reusable host tensors and explicit native GPU transfers.</summary>
/// <remarks>Load, PushFrame and Dispose run on the vision worker. RequestStop may be called from another thread.</remarks>
internal sealed class VisionModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions;
    private readonly OrtValue[] _inputs, _outputs;
    private readonly string[] _inputNames, _outputNames;
    private readonly float[] _input, _output = new float[5];
    private readonly ShotTracker _tracker;
    private readonly VisionValidationReport? _validatedReport;
    private int _stopping, _disposed;

    internal VisionModelManifest Manifest { get; }
    internal string ManifestHash { get; }
    internal bool CanArm { get; }
    internal string ValidationSummary { get; }
    internal VisionDiagnostics Diagnostics { get; } = new();

    private VisionModel(VisionModelManifest manifest, string manifestHash, VisionSettings settings,
        InferenceSession session, RunOptions runOptions, float[] input, OrtValue inputValue, OrtValue outputValue,
        float[] output, string blocker, VisionValidationReport? validatedReport)
    {
        Manifest = manifest;
        ManifestHash = manifestHash;
        _session = session;
        _runOptions = runOptions;
        _input = input;
        _output = output;
        _validatedReport = validatedReport;
        _inputs = [inputValue];
        _outputs = [outputValue];
        _inputNames = [manifest.InputName];
        _outputNames = [manifest.OutputName];
        CanArm = blocker.Length == 0;
        ValidationSummary = CanArm ? $"Validation gates pass for {manifest.AnimationProfile}; {manifest.GameVersion}; {manifest.CameraConfiguration}. Confirm this source configuration and calibration before arming." : blocker;
        _tracker = new ShotTracker(manifest, settings, _input, RunInference, Diagnostics);
    }

    internal static VisionModel Load(string manifestPath, VisionSettings settings, CancellationToken cancellation = default)
    {
        settings.Validate();
        cancellation.ThrowIfCancellationRequested();
        var (manifest, hash) = VisionModelManifest.Read(manifestPath);
        if (settings.CameraView.Length != 0 && settings.CameraView != manifest.CameraConfiguration)
            throw new InvalidDataException("The animation model does not match the selected camera view.");
        string modelPath = VisionModelManifest.ResolveChildFile(manifestPath, manifest.ModelFile);
        if (!string.Equals(Path.GetExtension(modelPath), ".onnx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The vision model must be an ONNX file.");
        using (var modelStream = File.OpenRead(modelPath))
        {
            if (modelStream.Length <= 0 || modelStream.Length > 1_073_741_824)
                throw new InvalidDataException("Vision model files must be between 1 byte and 1 GiB.");
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(modelStream));
            if (!string.Equals(actualHash, manifest.ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The ONNX model SHA-256 does not match its manifest.");
        }
        cancellation.ThrowIfCancellationRequested();

        string[] providers = OrtEnv.Instance().GetAvailableProviders();
        if (!providers.Contains("CUDAExecutionProvider", StringComparer.Ordinal))
            throw new InvalidOperationException("The loaded ONNX Runtime does not provide CUDA. Install the matching GPU runtime; automatic CPU vision is disabled.");

        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string>
        {
            ["device_id"] = "0",
            ["gpu_mem_limit"] = "2147483648",
            ["arena_extend_strategy"] = "kSameAsRequested",
            ["cudnn_conv_algo_search"] = "HEURISTIC",
            ["do_copy_in_default_stream"] = "1"
        });
        using var options = SessionOptions.MakeSessionOptionWithCudaProvider(cuda);
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        InferenceSession? session = null;
        RunOptions? run = null;
        OrtValue? inputValue = null, outputValue = null;
        try
        {
            // Session creation may perform native compilation. It is deliberately outside the armed loop.
            session = new InferenceSession(modelPath, options);
            RequireMetadata(session.InputMetadata, manifest.InputName,
                [1, manifest.HistoryLength, 3, manifest.InputHeight, manifest.InputWidth], "input");
            RequireMetadata(session.OutputMetadata, manifest.OutputName, [1, 5], "output");
            int inputLength = checked(manifest.HistoryLength * 3 * manifest.InputWidth * manifest.InputHeight);
            var input = GC.AllocateUninitializedArray<float>(inputLength, pinned: true);
            Array.Clear(input);
            var output = GC.AllocateUninitializedArray<float>(5, pinned: true);
            Array.Clear(output);
            inputValue = OrtValue.CreateTensorValueFromMemory(input,
                [1, manifest.HistoryLength, 3, manifest.InputHeight, manifest.InputWidth]);
            outputValue = OrtValue.CreateTensorValueFromMemory(output, [1, 5]);
            run = new RunOptions();
            var warmupRun = run;
            using var registration = cancellation.Register(() => warmupRun.Terminate = true);
            string[] inputNames = [manifest.InputName], outputNames = [manifest.OutputName];
            OrtValue[] inputs = [inputValue], outputs = [outputValue];
            for (int warm = 0; warm < 3; warm++)
            {
                cancellation.ThrowIfCancellationRequested();
                session.Run(run, inputNames, inputs, outputNames, outputs);
                ValidateObservation(output, manifest.MaximumPredictionHorizonMs);
            }
            cancellation.ThrowIfCancellationRequested();
            string blocker = manifest.CheckArmingEvidence(manifestPath, settings, out var validatedReport);
            var result = new VisionModel(manifest, hash, settings, session, run, input, inputValue, outputValue, output, blocker, validatedReport);
            session = null;
            run = null;
            inputValue = outputValue = null;
            return result;
        }
        catch (OnnxRuntimeException error)
        {
            throw new InvalidOperationException("The CUDA-only vision session could not load or warm this model. Verify ONNX Runtime/CUDA/cuDNN versions and fully supported GPU operators. No CPU fallback was enabled.", error);
        }
        finally
        {
            outputValue?.Dispose();
            inputValue?.Dispose();
            run?.Dispose();
            session?.Dispose();
        }
    }

    internal VisionPrediction? PushFrame(CapturedVisionFrame frame, VisionStatusSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _stopping) != 0) return null;
        if (_validatedReport is { } report && (frame.Width != report.CaptureWidth || frame.Height != report.CaptureHeight))
            throw new InvalidDataException("The capture dimensions changed from the validated model configuration. Recalibrate and validate this source before assistance.");
        return _tracker.PushFrame(frame, snapshot);
    }

    internal void ResetTracking() => _tracker.Reset();

    // Cooperative cancellation is not a hard bound on a GPU kernel or device-driver stall.
    internal void RequestStop()
    {
        Interlocked.Exchange(ref _stopping, 1);
        if (Volatile.Read(ref _disposed) == 0)
            try { _runOptions.Terminate = true; } catch (ObjectDisposedException) { }
    }

    private ModelObservation RunInference()
    {
        if (Volatile.Read(ref _stopping) != 0) throw new OperationCanceledException("Vision is stopping.");
        long started = Stopwatch.GetTimestamp();
        // Host buffers are reused. ORT performs the measured upload/download; these are not VRAM buffers.
        _session.Run(_runOptions, _inputNames, _inputs, _outputNames, _outputs);
        ValidateObservation(_output, Manifest.MaximumPredictionHorizonMs);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Diagnostics.InferenceCompleted(elapsed, _output[0], _output[1], _output[3], _output[4]);
        return new(_output[0], _output[1], _output[2], _output[3], _output[4], elapsed);
    }

    private static void RequireMetadata(IReadOnlyDictionary<string, NodeMetadata> metadata, string name, int[] shape, string kind)
    {
        if (metadata.Count != 1 || !metadata.TryGetValue(name, out var node) || !node.IsTensor ||
            node.ElementType != typeof(float) || !node.Dimensions.AsSpan().SequenceEqual(shape))
            throw new InvalidDataException($"The model {kind} must be a single float32 tensor named '{name}' with fixed shape [{string.Join(',', shape)}].");
    }

    internal static void ValidateObservation(ReadOnlySpan<float> values, double maximumHorizon)
    {
        if (values.Length != 5) throw new InvalidDataException("The vision model must return five observation values.");
        for (int index = 0; index < values.Length; index++)
            if (!float.IsFinite(values[index])) throw new InvalidDataException("The vision model returned nonfinite output.");
        if (values[0] is < 0 or > 1 || values[1] is < 0 or > 1 || values[4] is < 0 or > 1 ||
            values[2] < 0 || values[2] > maximumHorizon || values[3] < 0 || values[3] > maximumHorizon)
            throw new InvalidDataException("The vision model output is outside its confidence, phase, horizon or uncertainty contract.");
    }

    public void Dispose()
    {
        RequestStop();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Owner must join PushFrame before disposal; no native buffer may be freed during an active Run.
        _outputs[0].Dispose();
        _inputs[0].Dispose();
        _runOptions.Dispose();
        _session.Dispose();
        Diagnostics.SetStatus("Vision model stopped");
    }
}

internal readonly record struct ModelObservation(double PlayerConfidence, double ShotConfidence, double TimeToTargetMs,
    double UncertaintyMs, double Phase, double InferenceWithTransfersMs);
