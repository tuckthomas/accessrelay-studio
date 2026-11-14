using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

internal static class PerformanceVerification
{
    internal static void Run()
    {
        EmptyAndCaptureOnly();
        PercentilesAndOptionalStages();
        InvalidFramesAndFinish();
        RollingWindow();
        RecordingDoesNotAllocate();
        FingerprintsAndComparisons();
        SavedRunsAndImports();
        ReadbackAndMeasurementGenerations();
        ReadbackLeaseMetadata();
        GpuReadbackScopes();
    }

    private static void GpuReadbackScopes()
    {
        var buffer = new VisionPerformanceBuffer(1000, () => 1000);
        buffer.Reset(Context() with { PreprocessingBackend = "d3d11-cuda-rgb-v2-experimental" });
        Require(!buffer.Record(Frame(1) with { FreshnessReadbackBytes = 15 }), "Unbounded freshness metadata was accepted.");
        Require(!buffer.Record(Frame(1) with { OptionalReadbackBytes = -1 }), "Negative optional image readback was accepted.");
        Require(!buffer.Record(Frame(1) with { OptionalReadbackMs = 2 }), "Image readback time without image data was invented.");
        buffer.Record(Frame(1) with { FreshnessReadbackBytes = 16 });
        buffer.Record(Frame(2) with { FreshnessReadbackBytes = 16, OptionalReadbackBytes = 8192, OptionalReadbackMs = 2.5 });
        var value = buffer.Snapshot();
        Require(value.PreprocessingBackend == "d3d11-cuda-rgb-v2-experimental" && value.CaptureReadback.Samples == 0 &&
            value.OptionalImageReadback.Samples == 1 && value.OptionalImageReadback.P95Ms == 2.5 &&
            value.WindowOptionalImageReadbackBytes == 8192 && value.WindowFreshnessReadbackBytes == 32,
            "GPU metadata, optional image copies and automatic capture readback were conflated.");
        for (int i = 3; i <= VisionPerformanceBuffer.Capacity + 2; i++) buffer.Record(Frame(i));
        value = buffer.Snapshot();
        Require(value.OptionalImageReadback.Samples == 0 && value.WindowOptionalImageReadbackBytes == 0 && value.WindowFreshnessReadbackBytes == 0,
            "Expired readbacks remained in the bounded measurement window.");
        var baseline = DecisionRun(); var gpu = baseline with { Snapshot = baseline.Snapshot with { PreprocessingBackend = "d3d11-cuda-rgb-v2-experimental" } };
        Require(VisionPerformanceRuns.Compare(baseline, gpu).Comparable, "Matching CPU/GPU preparation runs could not be compared.");
        Require(VisionPerformanceBuffer.Fingerprint(VisionSettings.Default) == VisionPerformanceBuffer.Fingerprint(
            VisionSettings.Default with { PreprocessingBackend = VisionPreprocessingBackend.D3D11Cuda }), "Backend choice polluted otherwise identical comparison settings.");
    }

    private static void ReadbackLeaseMetadata()
    {
        nint pixels = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new byte[16], 0, pixels, 16);
            using var buffer = new LatestFrameBuffer();
            Require(buffer.PublishMapped(pixels, 8, 2, 2, 100, 1, 1, Stopwatch.GetTimestamp()), "Measured frame was not published.");
            Require(buffer.TryAcquireLatest(out var frame), "Measured frame lease was not acquired.");
            Require(frame!.CaptureReadbackMs is >= 0, "Readback timing was lost from the frame lease."); frame.Dispose();
            Require(buffer.PublishMapped(pixels, 8, 2, 2, 200, 2, 1), "Unmeasured frame was not published.");
            Require(buffer.TryAcquireLatest(out frame), "Unmeasured frame lease was not acquired.");
            Require(frame!.CaptureReadbackMs is null, "Reused pixel slot retained a previous frame's readback measurement."); frame.Dispose();
            Throws(() => buffer.PublishMapped(pixels, 8, 2, 2, 300, 3, 1, long.MaxValue));
        }
        finally { Marshal.FreeHGlobal(pixels); }
    }

    private static void ReadbackAndMeasurementGenerations()
    {
        long now = 1000;
        var buffer = new VisionPerformanceBuffer(1000, () => now);
        buffer.Reset(Context() with { MeasurementGeneration = 2 });
        Require(!buffer.Record(Frame(1) with { CaptureReadbackMs = double.NaN }), "Invalid readback timing was accepted.");
        buffer.Record(Frame(1) with { CaptureReadbackMs = .75 }); buffer.Record(Frame(2));
        var empty = new RuntimeLockStatistics(0, 0, 0, null, null, null, null, null);
        var locks = new RuntimeLockSnapshot(2, false, empty, empty, empty, empty);
        var result = buffer.Snapshot(runtimeLocks: locks);
        Require(result.CaptureReadback.Samples == 1 && result.CaptureReadback.P95Ms == .75 && result.RuntimeLocks == locks,
            "Readback must count measured frames only and preserve matching lock epochs.");
        Require(buffer.Snapshot(runtimeLocks: locks with { Generation = 1 }).RuntimeLocks is null,
            "A retired controller measurement run was attached to another capture context.");
    }

    private static VisionPerformanceContext Context(string provider = "CUDAExecutionProvider") =>
        new(provider, provider == "Capture only" ? null : new('a', 64), "fixture:test", "2K",
            new(0, 0, 1, 1), 2560, 1440, new('b', 64));

    private static VisionPerformanceFrame Frame(int sequence, bool decision = true,
        double? preprocessing = 1, double? inference = 2) =>
        new(sequence, 1000L + sequence * 1000, 1003L + sequence * 1000,
            1003L + sequence * 1000 + sequence, decision, preprocessing, inference, 10, 1, 0, 0, false);

    private static VisionPerformanceRun DecisionRun(string provider = "CUDAExecutionProvider", int count = 100)
    {
        long now = 1000;
        var buffer = new VisionPerformanceBuffer(1000, () => now);
        buffer.Reset(Context(provider));
        for (int i = 1; i <= count; i++) buffer.Record(Frame(i));
        now += (count + 1) * 1000;
        return new() { Label = "Synthetic verification only", Snapshot = buffer.Snapshot() };
    }

    private static void EmptyAndCaptureOnly()
    {
        long now = 1000;
        var buffer = new VisionPerformanceBuffer(1000, () => now);
        buffer.Reset(Context("Capture only"));
        var empty = buffer.Snapshot(helperHandoffMs: double.NaN);
        Require(empty.TotalFrames == 0 && empty.ProcessingFps == 0 && empty.LatestHelperHandoffMs is null,
            "Empty observation must not manufacture samples or a finite handoff.");
        Require(empty.FrameToDecision == new VisionLatencyStatistics(0, null, null, null, null, null),
            "Empty decision metrics must be nullable, not zeros.");
        buffer.Record(Frame(1, false, null, null)); now = 3000;
        var capture = buffer.Snapshot(helperHandoffMs: .75);
        Require(capture.TotalFrames == 1 && capture.TotalDecisions == 0 && capture.TotalInferences == 0 &&
            capture.Processing.Samples == 1 && capture.FrameAge.Samples == 1 &&
            capture.FrameToDecision.Samples == 0 && capture.Preprocessing.Samples == 0 &&
            capture.InferenceWithTransfers.Samples == 0 && capture.LatestHelperHandoffMs == .75,
            "Capture-only processing must not become an inference/decision sample.");
        Require(buffer.Snapshot(helperHandoffMs: .75).TotalFrames == 1,
            "Polling snapshots must not count the repeated latest helper value as a frame.");
    }

    private static void PercentilesAndOptionalStages()
    {
        var run = DecisionRun();
        var snapshot = run.Snapshot;
        Require(snapshot.FrameToDecision == new VisionLatencyStatistics(100, 103, 53, 98, 102, 103),
            "Decision p50/p95/p99 must use nearest rank, preserve latest and include frame age.");
        Require(snapshot.Processing == new VisionLatencyStatistics(100, 100, 50, 95, 99, 100) &&
            snapshot.FrameAge.P99Ms == 3 && snapshot.DecisionBudgetExceededCount == 87,
            "Processing, frame age and decision budget have different timestamp boundaries.");
        long now = 1000;
        var buffer = new VisionPerformanceBuffer(1000, () => now);
        buffer.Reset(Context());
        buffer.Record(Frame(1));
        // Policy rejection may return an observation without invoking the model. It is processing,
        // not a model decision, even if the caller initially labels it a decision.
        buffer.Record(Frame(2, true, null, null));
        now = 5000;
        snapshot = buffer.Snapshot();
        Require(snapshot.TotalFrames == 2 && snapshot.TotalDecisions == 1 && snapshot.TotalInferences == 1 &&
            snapshot.Preprocessing.Samples == 1 && snapshot.InferenceWithTransfers.Samples == 1 &&
            snapshot.FrameToDecision.Samples == 1 && snapshot.Processing.Samples == 2,
            "A policy-only rejection must not become a model decision or repeat a previous stage sample.");
    }

    private static void InvalidFramesAndFinish()
    {
        long now = 1000;
        var buffer = new VisionPerformanceBuffer(1000, () => now);
        buffer.Reset(Context());
        var valid = Frame(1);
        foreach (var invalid in new[]
        {
            valid with { CaptureTimestamp = 0 }, valid with { StartedTimestamp = valid.CaptureTimestamp - 1 },
            valid with { CompletedTimestamp = valid.StartedTimestamp - 1 }, valid with { AllocatedBytes = -1 },
            valid with { Gen0Collections = -1 }, valid with { PreprocessingMs = double.NaN },
            valid with { InferenceWithTransfersMs = double.PositiveInfinity }, valid with { InferenceWithTransfersMs = -1 }
        }) Require(!buffer.Record(invalid), "Invalid timing data was reported as an accepted sample.");
        Require(buffer.Snapshot().TotalFrames == 0, "Invalid timestamps or stage values must be discarded.");
        Require(buffer.Record(valid), "A valid new timing sample was reported as excluded.");
        Require(!buffer.Record(valid), "A duplicate sequence was reported as an accepted timing sample.");
        Require(!buffer.Record(Frame(0)), "An older sequence was reported as an accepted timing sample.");
        Require(buffer.Snapshot().TotalFrames == 1, "Repeated or old frame sequences must not duplicate samples.");
        now = 5000; buffer.Finish();
        var stopped = buffer.Snapshot();
        now = 100000;
        Require(!buffer.Record(Frame(2)), "A finished collector reported a later sample as accepted.");
        var later = buffer.Snapshot();
        Require(stopped.ElapsedSeconds == 4 && stopped.ElapsedSeconds == later.ElapsedSeconds &&
            stopped.ProcessingFps == later.ProcessingFps && later.TotalFrames == 1,
            "Stopped runs must freeze elapsed/FPS and reject later frames.");
        buffer.Reset(Context("CPUExecutionProvider"));
        Require(buffer.Snapshot().TotalFrames == 0 && buffer.Snapshot().ExecutionProvider == "CPUExecutionProvider",
            "A new observation must reset counters and provider context.");
        Require(buffer.Record(Frame(1)), "Reset did not allow a fresh timing sample after finish.");
    }

    private static void RollingWindow()
    {
        long now = 1000;
        var buffer = new VisionPerformanceBuffer(1000, () => now);
        buffer.Reset(Context());
        const int expired = 17;
        for (int i = 1; i <= VisionPerformanceBuffer.Capacity + expired; i++)
        {
            var frame = Frame(i, i % 6 == 0, i % 2 == 0 ? 1 : null, i % 2 == 0 ? 2 : null) with
            { AllocatedBytes = i <= expired ? 1000000 : 1, Gen0Collections = i <= expired ? 1 : 0,
                RecordingTraining = i <= expired };
            buffer.Record(frame);
        }
        now = 3000000;
        var snapshot = buffer.Snapshot();
        int recentDecisions = Enumerable.Range(expired + 1, VisionPerformanceBuffer.Capacity).Count(i => i % 6 == 0);
        int recentInferences = Enumerable.Range(expired + 1, VisionPerformanceBuffer.Capacity).Count(i => i % 2 == 0);
        Require(snapshot.TotalFrames == VisionPerformanceBuffer.Capacity + expired &&
            snapshot.TotalDecisions == (VisionPerformanceBuffer.Capacity + expired) / 6 &&
            snapshot.Processing.Samples == VisionPerformanceBuffer.Capacity &&
            snapshot.FrameToDecision.Samples == recentDecisions && snapshot.InferenceWithTransfers.Samples == recentInferences,
            "Lifetime counters and recent-stage sample counts must stay distinct after ring overflow.");
        Require(snapshot.WindowAllocatedBytes == VisionPerformanceBuffer.Capacity && snapshot.WindowGen0Collections == 0 &&
            !snapshot.RecordingTraining && snapshot.DecisionBudgetExceededCount == recentDecisions,
            "Expired allocation, GC and training outliers must leave the bounded frame window.");
    }

    private static void FingerprintsAndComparisons()
    {
        var settings = VisionSettings.Default;
        string fingerprint = VisionPerformanceBuffer.Fingerprint(settings);
        Require(fingerprint == VisionPerformanceBuffer.Fingerprint(settings with
            { ExecutionProvider = VisionExecutionProvider.Cpu, ModelManifestPath = "C:/elsewhere/same-manifest.json" }),
            "Backend and manifest path should not prevent an otherwise identical CPU/CUDA comparison.");
        Require(fingerprint != VisionPerformanceBuffer.Fingerprint(settings with { CameraView = "2K Low" }) &&
            fingerprint != VisionPerformanceBuffer.Fingerprint(settings with { CorrectionMs = settings.CorrectionMs + 1 }),
            "Camera and timing changes must invalidate comparison context.");
        var baseline = DecisionRun();
        var current = DecisionRun("CPUExecutionProvider");
        var comparison = VisionPerformanceRuns.Compare(baseline, current);
        Require(comparison.Comparable && comparison.FrameToDecisionP95ChangeMs == 0 &&
            comparison.Message.Contains("not a controlled benchmark", StringComparison.Ordinal),
            "Matching CPU/CUDA runs should compare with an honest live-input qualifier.");
        Require(!VisionPerformanceRuns.Compare(baseline, DecisionRun(count: 99)).Comparable,
            "Fewer than 100 decisions must not produce comparison deltas.");
        foreach (var changed in new[]
        {
            current.Snapshot with { CameraView = "2K Low" }, current.Snapshot with { CaptureWidth = 1920 },
            current.Snapshot with { Crop = new(.1, .1, .8, .8) }, current.Snapshot with { AnimationProfile = "other" },
            current.Snapshot with { ModelManifestHash = new('c', 64) },
            current.Snapshot with { SettingsFingerprint = new('d', 64) }, current.Snapshot with { RecordingTraining = true },
            current.Snapshot with { Armed = true }
        }) Require(!VisionPerformanceRuns.Compare(baseline, current with { Snapshot = changed }).Comparable,
            "Incompatible source, profile, model, timing or assistance state must not compare.");
    }

    private static void RecordingDoesNotAllocate()
    {
        var buffer = new VisionPerformanceBuffer(1000, () => 1000);
        buffer.Reset(Context());
        for (int i = 1; i <= 64; i++) buffer.Record(Frame(i));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 65; i <= 1064; i++) buffer.Record(Frame(i));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0, "Recording numeric frame telemetry must not allocate on its caller's managed heap.");
    }

    private static void SavedRunsAndImports()
    {
        string directory = Path.Combine(Path.GetTempPath(), "accessrelay-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "run.json");
        try
        {
            var run = DecisionRun();
            VisionPerformanceRuns.Write(path, run);
            var restored = VisionPerformanceRuns.Read(path);
            Require(restored == run && VisionPerformanceRuns.Compare(run, restored).Comparable,
                "Saved metrics and context must round-trip without inventing samples.");
            string json = File.ReadAllText(path);
            var legacy = JsonNode.Parse(json)!.AsObject(); legacy["version"] = 1;
            foreach (string field in new[] { "captureReadback", "measurementGeneration", "runtimeLocks" }) legacy["snapshot"]!.AsObject().Remove(field);
            File.WriteAllText(path, legacy.ToJsonString());
            var old = VisionPerformanceRuns.Read(path);
            Require(old.Version == 1 && old.Snapshot.CaptureReadback.Samples == 0 && old.Snapshot.RuntimeLocks is null,
                "Legacy runs must load with unavailable stages, not manufactured zeros.");
            Require(!VisionPerformanceRuns.Compare(old, run).Comparable, "Different instrumentation revisions were directly compared.");
            var version2 = JsonNode.Parse(json)!.AsObject(); version2["version"] = 2;
            foreach (string field in new[] { "preprocessingBackend", "optionalImageReadback", "windowOptionalImageReadbackBytes", "windowFreshnessReadbackBytes" })
                version2["snapshot"]!.AsObject().Remove(field);
            File.WriteAllText(path, version2.ToJsonString());
            var previous = VisionPerformanceRuns.Read(path);
            Require(previous.Version == 2 && previous.Snapshot.PreprocessingBackend == "cpu-rgb-v2" && previous.Snapshot.OptionalImageReadback.Samples == 0,
                "Format 2 did not retain CPU preparation with unavailable new readback stages.");
            Require(!VisionPerformanceRuns.Compare(previous, run).Comparable, "Format 2 was compared against changed instrumentation.");
            void Reject(Action<JsonObject> change)
            {
                var modified = JsonNode.Parse(json)!.AsObject(); change(modified);
                File.WriteAllText(path, modified.ToJsonString());
                Throws(() => VisionPerformanceRuns.Read(path));
            }
            Reject(node => node["unexpected"] = true);
            Reject(node => node["version"] = 4);
            Reject(node => node["snapshot"]!["preprocessingBackend"] = "unrecognized");
            Reject(node => node["snapshot"]!["windowFreshnessReadbackBytes"] = 15);
            Reject(node => { node["version"] = 2; node["snapshot"]!["preprocessingBackend"] = "d3d11-cuda-rgb-v2-experimental"; });
            Reject(node => node["snapshot"]!["totalDecisions"] = 0);
            Reject(node => node["snapshot"]!["totalInferences"] = 0);
            Reject(node =>
            {
                node["snapshot"]!["totalInferences"] = 99;
                node["snapshot"]!["inferenceWithTransfers"]!["samples"] = 99;
            });
            Reject(node => node["snapshot"]!["inferenceWithTransfers"]!["samples"] = 99);
            Reject(node => node["snapshot"]!["processing"]!["samples"] = 99);
            Reject(node => node["snapshot"]!["frameAge"]!["samples"] = 99);
            Reject(node => node["snapshot"]!["executionProvider"] = "Capture only");
            Reject(node => node["snapshot"]!["modelManifestHash"] = null);
            Reject(node => node["snapshot"]!["frameToDecision"]!["p95Ms"] = 99999);
            Reject(node => node["snapshot"]!["frameToDecision"]!["samples"] = 0);
            Reject(node => node["snapshot"]!["settingsFingerprint"] = "invalid");
            File.WriteAllText(path, new string(' ', 64 * 1024 + 1));
            Throws(() => VisionPerformanceRuns.Read(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        { return; }
        throw new InvalidOperationException("Invalid saved performance data was accepted.");
    }
}
