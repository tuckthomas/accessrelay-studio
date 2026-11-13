using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal readonly record struct VisionLatencyStatistics(int Samples, double? LatestMs, double? P50Ms,
    double? P95Ms, double? P99Ms, double? MaximumMs);

internal sealed record VisionPerformanceContext(string ExecutionProvider, string? ModelManifestHash,
    string AnimationProfile, string CameraView, VisionCrop Crop, int CaptureWidth, int CaptureHeight,
    string SettingsFingerprint, long MeasurementGeneration = 0, string PreprocessingBackend = "cpu-rgb-v2");

internal readonly record struct VisionPerformanceFrame(long Sequence, long CaptureTimestamp, long StartedTimestamp,
    long CompletedTimestamp, bool HasDecision, double? PreprocessingMs, double? InferenceWithTransfersMs,
    long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, bool RecordingTraining, double? CaptureReadbackMs = null,
    long OptionalReadbackBytes = 0, double OptionalReadbackMs = 0, int FreshnessReadbackBytes = 0);

internal sealed record VisionPerformanceSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ExecutionProvider { get; init; } = "Capture only";
    public string PreprocessingBackend { get; init; } = "cpu-rgb-v2";
    public string? ModelManifestHash { get; init; }
    public string AnimationProfile { get; init; } = "";
    public string CameraView { get; init; } = "";
    public VisionCrop Crop { get; init; } = new(0, 0, 1, 1);
    public int CaptureWidth { get; init; }
    public int CaptureHeight { get; init; }
    public string SettingsFingerprint { get; init; } = "";
    public double ElapsedSeconds { get; init; }
    public long TotalFrames { get; init; }
    public long TotalDecisions { get; init; }
    public long TotalInferences { get; init; }
    public long ReplacedCaptureFrames { get; init; }
    public double ProcessingFps { get; init; }
    public VisionLatencyStatistics FrameAge { get; init; }
    public VisionLatencyStatistics Processing { get; init; }
    public VisionLatencyStatistics FrameToDecision { get; init; }
    public VisionLatencyStatistics Preprocessing { get; init; }
    public VisionLatencyStatistics InferenceWithTransfers { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VisionLatencyStatistics CaptureReadback { get; init; }
    public VisionLatencyStatistics OptionalImageReadback { get; init; }
    public long WindowOptionalImageReadbackBytes { get; init; }
    public long WindowFreshnessReadbackBytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long MeasurementGeneration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeLockSnapshot? RuntimeLocks { get; init; }
    public double DecisionBudgetMs { get; init; } = 1000d / 60;
    public int DecisionBudgetExceededCount { get; init; }
    public long AutomatedReleases { get; init; }
    public long MissedDeadlines { get; init; }
    public long WindowAllocatedBytes { get; init; }
    public long WindowGen0Collections { get; init; }
    public long WindowGen1Collections { get; init; }
    public long WindowGen2Collections { get; init; }
    public bool RecordingTraining { get; init; }
    public bool Armed { get; init; }
    public double? LatestHelperHandoffMs { get; init; }
}

/// <summary>One bounded frame window. Recording does not allocate; percentile work runs on the caller,
/// after copying into pooled memory and releasing the collector lock.</summary>
internal sealed class VisionPerformanceBuffer
{
    internal const int Capacity = 2048;
    private readonly object _gate = new();
    private readonly VisionPerformanceFrame[] _frames = new VisionPerformanceFrame[Capacity];
    private readonly long _frequency;
    private readonly Func<long> _timestamp;
    private VisionPerformanceContext _context = new("Capture only", null, "", "", new(0, 0, 1, 1), 0, 0, "");
    private int _next, _count;
    private long _started, _finished, _lastSequence = -1, _totalFrames, _totalDecisions, _totalInferences;

    internal VisionPerformanceBuffer(long frequency = 0, Func<long>? timestamp = null)
    {
        _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        if (_frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _started = _timestamp();
    }

    internal void Reset(VisionPerformanceContext context)
    {
        lock (_gate)
        {
            _context = context; _next = _count = 0; _lastSequence = -1;
            _totalFrames = _totalDecisions = _totalInferences = 0;
            _started = _timestamp(); _finished = 0;
        }
    }

    internal void Finish() { lock (_gate) _finished = _timestamp(); }

    internal bool Record(VisionPerformanceFrame frame)
    {
        if (frame.CaptureTimestamp <= 0 || frame.StartedTimestamp < frame.CaptureTimestamp ||
            frame.CompletedTimestamp < frame.StartedTimestamp || frame.AllocatedBytes < 0 ||
            frame.Gen0Collections < 0 || frame.Gen1Collections < 0 || frame.Gen2Collections < 0 ||
            !ValidOptional(frame.PreprocessingMs) || !ValidOptional(frame.InferenceWithTransfersMs) || !ValidOptional(frame.CaptureReadbackMs) ||
            frame.OptionalReadbackBytes is < 0 or > LatestFrameBuffer.MaximumFrameBytes || frame.FreshnessReadbackBytes is not (0 or 16) ||
            !ValidOptional(frame.OptionalReadbackMs) || (frame.OptionalReadbackBytes == 0 && frame.OptionalReadbackMs != 0))
            return false;
        lock (_gate)
        {
            if (frame.Sequence <= _lastSequence || _finished != 0) return false;
            // A policy rejection without inference is processing work, not a model decision.
            frame = frame with { HasDecision = frame.HasDecision && frame.InferenceWithTransfersMs.HasValue };
            _lastSequence = frame.Sequence;
            _frames[_next] = frame; _next = (_next + 1) % Capacity; _count = Math.Min(Capacity, _count + 1);
            _totalFrames++;
            if (frame.HasDecision) _totalDecisions++;
            if (frame.InferenceWithTransfersMs.HasValue) _totalInferences++;
            return true;
        }
    }

    internal VisionPerformanceSnapshot Snapshot(long replacedCaptureFrames = 0, long automatedReleases = 0,
        long missedDeadlines = 0, double? helperHandoffMs = null, bool armed = false, bool recordingTraining = false, RuntimeLockSnapshot? runtimeLocks = null)
    {
        var copy = ArrayPool<VisionPerformanceFrame>.Shared.Rent(Capacity);
        var values = ArrayPool<double>.Shared.Rent(Capacity);
        try
        {
            VisionPerformanceContext context; int count; long totalFrames, decisions, inferences; double elapsed;
            lock (_gate)
            {
                context = _context; count = _count; totalFrames = _totalFrames;
                decisions = _totalDecisions; inferences = _totalInferences;
                elapsed = Math.Max(0, ((double)(_finished == 0 ? _timestamp() : _finished) - _started) / _frequency);
                int first = (_next - count + Capacity) % Capacity;
                for (int i = 0; i < count; i++) copy[i] = _frames[(first + i) % Capacity];
            }
            VisionLatencyStatistics Statistics(int kind)
            {
                int used = 0; double? latest = null;
                for (int i = 0; i < count; i++)
                {
                    var frame = copy[i];
                    double? value = kind switch
                    {
                        0 => Milliseconds(frame.StartedTimestamp, frame.CaptureTimestamp),
                        1 => Milliseconds(frame.CompletedTimestamp, frame.StartedTimestamp),
                        2 => frame.HasDecision ? Milliseconds(frame.CompletedTimestamp, frame.CaptureTimestamp) : null,
                        3 => frame.PreprocessingMs,
                        4 => frame.InferenceWithTransfersMs,
                        5 => frame.CaptureReadbackMs,
                        _ => frame.OptionalReadbackBytes > 0 ? frame.OptionalReadbackMs : null
                    };
                    if (value.HasValue) { values[used++] = value.Value; latest = value; }
                }
                if (used == 0) return new(0, null, null, null, null, null);
                Array.Sort(values, 0, used);
                double Percentile(double p) => values[Math.Max(0, (int)Math.Ceiling(p * used) - 1)];
                return new(used, latest, Percentile(.5), Percentile(.95), Percentile(.99), values[used - 1]);
            }
            long allocated = 0, gen0 = 0, gen1 = 0, gen2 = 0, optionalBytes = 0, freshnessBytes = 0;
            int exceeded = 0; const double budget = 1000d / 60;
            for (int i = 0; i < count; i++)
            {
                var frame = copy[i];
                allocated += frame.AllocatedBytes; gen0 += frame.Gen0Collections;
                gen1 += frame.Gen1Collections; gen2 += frame.Gen2Collections;
                optionalBytes += frame.OptionalReadbackBytes; freshnessBytes += frame.FreshnessReadbackBytes;
                recordingTraining |= frame.RecordingTraining;
                if (frame.HasDecision && Milliseconds(frame.CompletedTimestamp, frame.CaptureTimestamp) > budget) exceeded++;
            }
            return new()
            {
                ExecutionProvider = context.ExecutionProvider, ModelManifestHash = context.ModelManifestHash,
                PreprocessingBackend = context.PreprocessingBackend,
                AnimationProfile = context.AnimationProfile, CameraView = context.CameraView, Crop = context.Crop,
                CaptureWidth = context.CaptureWidth, CaptureHeight = context.CaptureHeight, SettingsFingerprint = context.SettingsFingerprint,
                ElapsedSeconds = elapsed, TotalFrames = totalFrames, TotalDecisions = decisions, TotalInferences = inferences,
                ReplacedCaptureFrames = replacedCaptureFrames, ProcessingFps = elapsed > 0 ? totalFrames / elapsed : 0,
                FrameAge = Statistics(0), Processing = Statistics(1), FrameToDecision = Statistics(2),
                Preprocessing = Statistics(3), InferenceWithTransfers = Statistics(4), DecisionBudgetMs = budget,
                CaptureReadback = Statistics(5), MeasurementGeneration = context.MeasurementGeneration,
                OptionalImageReadback = Statistics(6), WindowOptionalImageReadbackBytes = optionalBytes, WindowFreshnessReadbackBytes = freshnessBytes,
                RuntimeLocks = runtimeLocks?.Generation == context.MeasurementGeneration ? runtimeLocks : null,
                DecisionBudgetExceededCount = exceeded, AutomatedReleases = automatedReleases, MissedDeadlines = missedDeadlines,
                WindowAllocatedBytes = allocated, WindowGen0Collections = gen0, WindowGen1Collections = gen1,
                WindowGen2Collections = gen2, RecordingTraining = recordingTraining, Armed = armed,
                LatestHelperHandoffMs = helperHandoffMs is >= 0 && double.IsFinite(helperHandoffMs.Value) ? helperHandoffMs : null
            };
        }
        finally { ArrayPool<VisionPerformanceFrame>.Shared.Return(copy); ArrayPool<double>.Shared.Return(values); }
    }

    private double Milliseconds(long end, long start) => ((double)end - start) * 1000 / _frequency;
    private static bool ValidOptional(double? value) => value is null || (double.IsFinite(value.Value) && value.Value >= 0);
    internal static string Fingerprint(VisionSettings settings) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(settings with { ExecutionProvider = VisionExecutionProvider.Cuda,
            PreprocessingBackend = VisionPreprocessingBackend.Cpu, ModelManifestPath = "" })));
}

internal sealed record VisionPerformanceRun
{
    public int Version { get; init; } = 4;
    public Guid RunId { get; init; } = Guid.NewGuid();
    public string Label { get; init; } = "Performance run";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public VisionPerformanceSnapshot Snapshot { get; init; } = new();
}

internal readonly record struct VisionPerformanceComparison(bool Comparable, string Message,
    double? FrameToDecisionP95ChangeMs = null, double? FrameToDecisionP99ChangeMs = null);

internal static class VisionPerformanceRuns
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static VisionPerformanceRun Read(string path)
    {
        var result = JsonSerializer.Deserialize<VisionPerformanceRun>(VisionModelManifest.ReadBounded(path, 64 * 1024), Json)
            ?? throw new InvalidDataException("The saved performance run is empty.");
        Validate(result); return result;
    }

    internal static void Write(string path, VisionPerformanceRun run)
    {
        Validate(run);
        string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(run, Json));
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static VisionPerformanceComparison Compare(VisionPerformanceRun baseline, VisionPerformanceRun current)
    {
        Validate(baseline); Validate(current);
        if (baseline.Version != current.Version) return new(false, "Measurement format changed. Recollect the baseline with the current instrumentation before comparing runs.");
        var a = baseline.Snapshot; var b = current.Snapshot;
        if (a.ModelManifestHash is null || b.ModelManifestHash is null ||
            a.FrameToDecision.Samples < 100 || b.FrameToDecision.Samples < 100)
            return new(false, "Collect at least 100 model decisions in each run. Capture-only frames do not count as decisions.");
        if (a.RecordingTraining || b.RecordingTraining || a.Armed != b.Armed)
            return new(false, "Use the same assistance state and no training-capture samples in both runs.");
        if (a.ModelManifestHash != b.ModelManifestHash || a.SettingsFingerprint != b.SettingsFingerprint ||
            a.AnimationProfile != b.AnimationProfile || a.CameraView != b.CameraView || a.Crop != b.Crop ||
            a.CaptureWidth != b.CaptureWidth || a.CaptureHeight != b.CaptureHeight)
            return new(false, "Model, shot profile, camera, crop, source dimensions and timing settings must match; inference and preprocessing backends may differ.");
        return new(true, $"Observed live-run difference ({a.PreprocessingBackend} / {a.ExecutionProvider} → {b.PreprocessingBackend} / {b.ExecutionProvider}). Gameplay inputs can differ; this is not a controlled benchmark.",
            b.FrameToDecision.P95Ms - a.FrameToDecision.P95Ms, b.FrameToDecision.P99Ms - a.FrameToDecision.P99Ms);
    }

    private static void Validate(VisionPerformanceRun run)
    {
        if (run.Version is not (1 or 2 or 3 or 4) || run.RunId == Guid.Empty || string.IsNullOrWhiteSpace(run.Label) || run.Label.Length > 80 ||
            run.Label.Any(char.IsControl) || run.Snapshot is not { } data)
            throw new InvalidDataException("The performance run version, label or snapshot is invalid.");
        if (data.ExecutionProvider is not ("Capture only" or "CPUExecutionProvider" or "CUDAExecutionProvider") ||
            data.PreprocessingBackend is not ("cpu-rgb-v2" or "d3d11-cuda-rgb-v2-experimental") ||
            (data.PreprocessingBackend == "d3d11-cuda-rgb-v2-experimental" && (data.Armed || data.AutomatedReleases != 0)) ||
            !VisionModelManifest.IsSha256(data.SettingsFingerprint) ||
            (data.ModelManifestHash is not null && !VisionModelManifest.IsSha256(data.ModelManifestHash)) ||
            data.CaptureWidth is < 0 or > 16384 || data.CaptureHeight is < 0 or > 16384 ||
            !Nonnegative(data.ElapsedSeconds) || !Nonnegative(data.ProcessingFps) || !Nonnegative(data.DecisionBudgetMs) ||
            data.TotalFrames < 0 || data.TotalDecisions < 0 || data.TotalInferences < 0 || data.ReplacedCaptureFrames < 0 ||
            data.TotalDecisions > data.TotalInferences || data.TotalInferences > data.TotalFrames ||
            data.FrameAge.Samples != Math.Min(data.TotalFrames, VisionPerformanceBuffer.Capacity) ||
            data.Processing.Samples != Math.Min(data.TotalFrames, VisionPerformanceBuffer.Capacity) ||
            data.FrameToDecision.Samples > data.InferenceWithTransfers.Samples ||
            data.FrameToDecision.Samples > data.TotalDecisions || data.InferenceWithTransfers.Samples > data.TotalInferences ||
            ((data.ExecutionProvider == "Capture only" || data.ModelManifestHash is null) &&
                (data.TotalDecisions != 0 || data.TotalInferences != 0 || data.FrameToDecision.Samples != 0 || data.InferenceWithTransfers.Samples != 0)) ||
            data.WindowAllocatedBytes < 0 || data.WindowGen0Collections < 0 || data.WindowGen1Collections < 0 || data.WindowGen2Collections < 0 ||
            data.WindowOptionalImageReadbackBytes < 0 || data.WindowFreshnessReadbackBytes < 0 ||
            data.WindowOptionalImageReadbackBytes > data.FrameAge.Samples * (long)LatestFrameBuffer.MaximumFrameBytes ||
            data.WindowFreshnessReadbackBytes > data.FrameAge.Samples * 16L ||
            data.WindowFreshnessReadbackBytes % 16 != 0 ||
            (data.OptionalImageReadback.Samples == 0 && data.WindowOptionalImageReadbackBytes != 0) ||
            (data.OptionalImageReadback.Samples > 0 && data.WindowOptionalImageReadbackBytes == 0) ||
            data.AutomatedReleases < 0 || data.MissedDeadlines < 0 ||
            data.DecisionBudgetExceededCount < 0 || data.DecisionBudgetExceededCount > data.FrameToDecision.Samples ||
            (data.LatestHelperHandoffMs is { } handoff && !Nonnegative(handoff)))
            throw new InvalidDataException("The saved performance run contains unsupported context or invalid numeric values.");
        data.Crop.Validate();
        if (run.Version < 3 && (data.PreprocessingBackend != "cpu-rgb-v2" || data.OptionalImageReadback.Samples != 0 ||
            data.WindowOptionalImageReadbackBytes != 0 || data.WindowFreshnessReadbackBytes != 0))
            throw new InvalidDataException("GPU/optional image readback measurements require performance format 3.");
        if (data.MeasurementGeneration < 0 || (run.Version == 1 && (data.CaptureReadback.Samples > 0 || data.RuntimeLocks is not null || data.MeasurementGeneration != 0)))
            throw new InvalidDataException("Readback/lock measurements require performance format 2.");
        if (data.RuntimeLocks is { } locks)
        {
            RuntimeLockMeasurements.Validate(locks);
            if (locks.Generation != data.MeasurementGeneration) throw new InvalidDataException("Runtime lock measurements belong to another run.");
        }
        foreach (var stats in new[] { data.FrameAge, data.Processing, data.FrameToDecision, data.Preprocessing, data.InferenceWithTransfers, data.CaptureReadback, data.OptionalImageReadback })
        {
            if (stats.Samples < 0 || stats.Samples > VisionPerformanceBuffer.Capacity || stats.Samples > data.TotalFrames)
                throw new InvalidDataException("Performance sample counts are inconsistent.");
            double?[] values = [stats.LatestMs, stats.P50Ms, stats.P95Ms, stats.P99Ms, stats.MaximumMs];
            if ((stats.Samples == 0 && values.Any(v => v.HasValue)) ||
                (stats.Samples > 0 && values.Any(v => v is null || !Nonnegative(v.Value))) ||
                stats.P50Ms > stats.P95Ms || stats.P95Ms > stats.P99Ms || stats.P99Ms > stats.MaximumMs || stats.LatestMs > stats.MaximumMs)
                throw new InvalidDataException("The performance percentiles are incomplete or inconsistent.");
        }
    }
    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
}
