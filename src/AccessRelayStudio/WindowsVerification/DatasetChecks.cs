using System.Diagnostics;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

internal static class DatasetChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelayVisionVerification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = new Nba2K27ShotProfile(Guid.NewGuid(), "Verification shot", 78, 83, "Test base", "Test upper one", "Test upper two",
                UsesShotMeter: true, MeterReleaseType: "Verification release type", ShotSpeed: 3, Release1Percent: 35, CameraView: "2K Legacy");
            var settings = VisionSettings.Default with
            {
                AnimationProfile = "nba2k27:" + profile.ConfigurationFingerprint,
                CameraView = profile.CameraView,
                Crop = new(.25, .25, .5, .5)
            };
            CompleteTake(directory, profile, settings);
            InterruptedTakes(directory, profile, settings);
            MetadataFailure(directory, profile, settings);
            LateWriteFailure(directory, profile, settings);
            IndependentTimeout(directory, profile, settings);
            Console.WriteLine("SUCCESS: training crop pixels, offsets, provenance, live/finalizing/finalized snapshots, incomplete takes, metadata/late-write failure and independent timeout verified.");
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(resolved), temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("AccessRelayVisionVerification-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned verification directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CompleteTake(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        using var recorder = new VisionTrainingRecorder(directory, profile, settings, "Synthetic capture verification");
        var initial = recorder.Snapshot;
        Check(initial.HasTake && initial.IsRecording && !initial.IsFinalized && !initial.IsComplete &&
            initial.DirectoryPath == recorder.DirectoryPath && initial.StoredFrames == 0 && initial.DroppedFrames == 0 &&
            !string.IsNullOrWhiteSpace(initial.Message), "A new training snapshot did not identify an active, unsaved take.");
        using var first = Frame(1, 1);
        using var second = Frame(2, 1);
        recorder.Capture(first, default(VisionStatusSnapshot) with { ShotToken = 7, ShootHeld = true });
        recorder.Capture(second, default(VisionStatusSnapshot) with { ShotToken = 7, ShootHeld = false });
        recorder.Stop(complete: true);
        recorder.Dispose();
        CheckFinalSnapshot(recorder, storedFrames: 2, complete: true);
        Check(initial.IsRecording && !initial.IsFinalized && !initial.IsComplete && initial.StoredFrames == 0,
            "The previously captured training snapshot changed after finalization.");
        using var metadata = ReadMetadata(recorder);
        var root = metadata.RootElement;
        Check(root.GetProperty("complete").GetBoolean(), "An explicitly completed take was marked incomplete.");
        Check(root.GetProperty("shotProfileFingerprint").GetString() == profile.ConfigurationFingerprint, "Shot configuration fingerprint changed.");
        Check(root.GetProperty("shotProfile").GetProperty("heightInches").GetInt32() == 78, "Build settings were not preserved.");
        var storedProfile = root.GetProperty("shotProfile");
        Check(storedProfile.GetProperty("usesShotMeter").GetBoolean() && storedProfile.GetProperty("meterReleaseType").GetString() == "Verification release type", "Meter-specific settings were not preserved.");
        Check(storedProfile.GetProperty("shotSpeed").GetInt32() == 3 && storedProfile.GetProperty("release1Percent").GetInt32() == 35, "Shot speed or upper-release blend was not preserved.");
        Check(storedProfile.GetProperty("cameraView").GetString() == "2K Legacy", "The selected camera view was not preserved.");
        Check(root.GetProperty("cameraView").GetString() == "2K Legacy", "Top-level dataset camera provenance does not match the shot profile.");
        Check(root.GetProperty("stopwatchFrequency").GetInt64() == Stopwatch.Frequency, "QPC frequency missing.");
        var frames = root.GetProperty("frames");
        Check(frames.GetArrayLength() == 2, "A queued training frame was lost.");
        Check(frames[0].GetProperty("sequence").GetInt64() == 1 && frames[1].GetProperty("sequence").GetInt64() == 2, "Frame ordering changed.");
        Check(frames[0].GetProperty("offsetBytes").GetInt64() == 0 && frames[1].GetProperty("offsetBytes").GetInt64() == VisionTrainingRecorder.FrameBytes, "Dataset offsets are incorrect.");
        Check(frames[0].GetProperty("physicalShootHeld").GetBoolean() && !frames[1].GetProperty("physicalShootHeld").GetBoolean(), "Manual release observations were not preserved.");
        Check(frames[0].GetProperty("shotToken").GetInt64() == 7, "Shot token missing.");
        Check(root.GetProperty("labelsRequired").GetString()!.Contains("not automatically ground truth", StringComparison.Ordinal), "Training observations were misrepresented as target labels.");
        byte[] pixels = File.ReadAllBytes(Path.Combine(recorder.DirectoryPath, "frames.bgra"));
        Check(pixels.Length == 2 * VisionTrainingRecorder.FrameBytes, "Raw frame length differs from the index.");
        Check(pixels[0] == 2 && pixels[1] == 2, "Top-left crop coordinates are incorrect.");
        int last = VisionTrainingRecorder.FrameBytes - 4;
        Check(pixels[last] == 5 && pixels[last + 1] == 5, "Bottom-right crop coordinates are incorrect.");
        using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "labels.example.json")));
        Check(labels.RootElement.GetProperty("shots").GetArrayLength() == 0, "The recorder invented reviewed labels.");
    }

    private static void InterruptedTakes(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        using (var stopped = new VisionTrainingRecorder(directory, profile, settings, "Synthetic stop"))
        {
            using var frame = Frame(1, 1);
            stopped.Capture(frame, default);
            stopped.Stop();
            stopped.Dispose();
            CheckFinalSnapshot(stopped, storedFrames: 1, complete: false);
            using var metadata = ReadMetadata(stopped);
            Check(!metadata.RootElement.GetProperty("complete").GetBoolean(), "An early stop was marked complete.");
        }
        using (var resized = new VisionTrainingRecorder(directory, profile, settings, "Synthetic generation change"))
        {
            using var before = Frame(1, 1);
            using var after = Frame(2, 2);
            resized.Capture(before, default);
            resized.Capture(after, default);
            resized.Dispose();
            CheckFinalSnapshot(resized, storedFrames: 1, complete: false);
            using var metadata = ReadMetadata(resized);
            Check(!metadata.RootElement.GetProperty("complete").GetBoolean(), "A source generation change was marked complete.");
            Check(metadata.RootElement.GetProperty("frames").GetArrayLength() == 1, "A mismatched-generation frame entered the take.");
        }
        using (var empty = new VisionTrainingRecorder(directory, profile, settings, "Synthetic empty take"))
        {
            empty.Stop(complete: true);
            empty.Dispose();
            CheckFinalSnapshot(empty, storedFrames: 0, complete: false);
            using var metadata = ReadMetadata(empty);
            Check(!metadata.RootElement.GetProperty("complete").GetBoolean(), "An empty take was marked complete.");
        }
    }

    private static void MetadataFailure(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        using var recorder = new VisionTrainingRecorder(directory, profile, settings, "Synthetic metadata lock");
        using var frame = Frame(1, 1);
        recorder.Capture(frame, default);
        // Hold only this owned take's temporary metadata path. Windows denies the final replace write.
        using (var blocker = new FileStream(Path.Combine(recorder.DirectoryPath, "session.json.tmp"),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            recorder.Stop(complete: true);
            recorder.Dispose();
            CheckFinalSnapshot(recorder, storedFrames: 1, complete: false);
            Check(recorder.Snapshot.Message.Contains("finalize take metadata", StringComparison.Ordinal),
                "The training snapshot hid a metadata finalization failure.");
            Check(recorder.Message.Contains("finalize take metadata", StringComparison.Ordinal), "Finalization failure was not reported.");
            using var metadata = ReadMetadata(recorder);
            Check(!metadata.RootElement.GetProperty("complete").GetBoolean(), "Failed finalization replaced the incomplete metadata marker.");
        }
    }

    private static CapturedVisionFrame Frame(long sequence, long generation)
    {
        const int width = 8, height = 8;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = 64;
                pixels[offset + 3] = 255;
            }
        return new(pixels, width, height, Stopwatch.GetTimestamp(), sequence, generation, () => { });
    }

    private static void LateWriteFailure(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        var failedStream = new DelayedFailureStream();
        using var recorder = new VisionTrainingRecorder(directory, profile, settings,
            "Synthetic late disk failure", streamFactory: _ => failedStream);
        try
        {
            using var first = Frame(1, 1);
            using var second = Frame(2, 1);
            recorder.Capture(first, default);
            recorder.Capture(second, default);
            Check(failedStream.WriteEntered.Task.Wait(TimeSpan.FromSeconds(3)), "The injected writer did not reach the second frame.");
            var writing = recorder.Snapshot;
            Check(writing.HasTake && writing.IsRecording && !writing.IsFinalized && !writing.IsComplete && writing.StoredFrames == 1,
                "A blocked writer did not expose only successfully stored frames in its live snapshot.");
            recorder.Stop(complete: true);
            var finalizing = recorder.Snapshot;
            Check(finalizing.HasTake && !finalizing.IsRecording && !finalizing.IsFinalized && !finalizing.IsComplete &&
                finalizing.StoredFrames == 1 && finalizing.DirectoryPath == recorder.DirectoryPath,
                "Stopping an unfinished writer prematurely reported a completed saved take.");
            failedStream.Release.TrySetResult();
            recorder.Dispose();
            CheckFinalSnapshot(recorder, storedFrames: 1, complete: false);
            Check(writing.IsRecording && !finalizing.IsFinalized,
                "Earlier live/finalizing snapshots changed when the writer completed.");
            using var metadata = ReadMetadata(recorder);
            Check(!metadata.RootElement.GetProperty("complete").GetBoolean(), "A disk failure after complete-stop was marked complete.");
            Check(metadata.RootElement.GetProperty("frames").GetArrayLength() == 1, "The failed write was added to the dataset index.");
            Check(recorder.Message.Contains("Training capture failed:", StringComparison.Ordinal), "A post-stop IO failure was hidden.");
        }
        finally { failedStream.Release.TrySetResult(); }
    }

    private static void IndependentTimeout(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        using var recorder = new VisionTrainingRecorder(directory, profile, settings,
            "Synthetic no-frame timeout", durationLimit: TimeSpan.FromMilliseconds(100));
        Check(SpinWait.SpinUntil(() => !recorder.IsRecording, TimeSpan.FromSeconds(3)), "A source without frames did not reach its wall-clock timeout.");
        recorder.Dispose();
        CheckFinalSnapshot(recorder, storedFrames: 0, complete: false);
        using var metadata = ReadMetadata(recorder);
        Check(!metadata.RootElement.GetProperty("complete").GetBoolean(), "A timed-out take without frames was marked complete.");
    }

    private sealed class DelayedFailureStream : MemoryStream
    {
        private int _writes;
        internal TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                await base.WriteAsync(buffer, cancellationToken);
                return;
            }
            WriteEntered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw new IOException("Injected second-frame disk failure.");
        }
    }

    private static JsonDocument ReadMetadata(VisionTrainingRecorder recorder) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "session.json")));

    private static void CheckFinalSnapshot(VisionTrainingRecorder recorder, long storedFrames, bool complete)
    {
        var snapshot = recorder.Snapshot;
        Check(snapshot.HasTake && !snapshot.IsRecording && snapshot.IsFinalized && snapshot.IsComplete == complete &&
            snapshot.DirectoryPath == recorder.DirectoryPath && snapshot.StoredFrames == storedFrames &&
            snapshot.DroppedFrames == 0 && !string.IsNullOrWhiteSpace(snapshot.Message),
            "Final training snapshot disagrees with its writer completion, metadata outcome, path or frame counts.");
    }

    private static void Check(bool success, string error)
    {
        if (!success) throw new InvalidOperationException(error);
    }
}
