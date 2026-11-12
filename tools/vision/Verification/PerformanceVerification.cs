using System.Text.Json.Nodes;
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
    }

    private static VisionPerformanceContext Context(string provider = "CUDAExecutionProvider") =>
        new(provider, provider == "Capture only" ? null : new('a', 64), "nba2k27:test", "2K",
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
        }) buffer.Record(invalid);
        Require(buffer.Snapshot().TotalFrames == 0, "Invalid timestamps or stage values must be discarded.");
        buffer.Record(valid); buffer.Record(valid); buffer.Record(Frame(0));
        Require(buffer.Snapshot().TotalFrames == 1, "Repeated or old frame sequences must not duplicate samples.");
        now = 5000; buffer.Finish();
        var stopped = buffer.Snapshot();
        now = 100000; buffer.Record(Frame(2));
        var later = buffer.Snapshot();
        Require(stopped.ElapsedSeconds == 4 && stopped.ElapsedSeconds == later.ElapsedSeconds &&
            stopped.ProcessingFps == later.ProcessingFps && later.TotalFrames == 1,
            "Stopped runs must freeze elapsed/FPS and reject later frames.");
        buffer.Reset(Context("CPUExecutionProvider"));
        Require(buffer.Snapshot().TotalFrames == 0 && buffer.Snapshot().ExecutionProvider == "CPUExecutionProvider",
            "A new observation must reset counters and provider context.");
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
            void Reject(Action<JsonObject> change)
            {
                var modified = JsonNode.Parse(json)!.AsObject(); change(modified);
                File.WriteAllText(path, modified.ToJsonString());
                Throws(() => VisionPerformanceRuns.Read(path));
            }
            Reject(node => node["unexpected"] = true);
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
