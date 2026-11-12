using EliteScriptStudio.Packs.Nba2K27;
using System.Diagnostics;
using System.Text.Json;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;
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
            EnrichedTake(directory, profile, settings);
            EnrichedInterruptedTakes(directory, profile, settings);
            EnrichedTraceLifetime(directory, profile, settings);
            EnrichedTraceFailures(directory, profile, settings);
            EnrichedStorageLimit(directory, profile, settings);
            EnrichedPartialStreamFailure(directory, profile, settings);
            ResolutionCaptureProbe(directory, profile, settings);
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
        Check(!root.TryGetProperty("captureEvidence", out _), "A legacy primary-only fixture was mislabeled as enriched training evidence.");
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

    private static CapturedVisionFrame Frame(long sequence, long generation, int width = 8, int height = 8)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = (byte)(64 + sequence);
                pixels[offset + 3] = 255;
            }
        return new(pixels, width, height, Stopwatch.GetTimestamp(), sequence, generation, () => { });
    }

    private static VisionSettings EvidenceSettings(VisionSettings settings, int width = 8, int height = 8) => settings with
    {
        TrainingCapture = new()
        {
            Configured = true, SourceWidth = width, SourceHeight = height,
            HudName = "Verification lower-left meter / upper-right feedback",
            GameBuild = "NBA 2K27 synthetic verification", ShotCategory = "Catch and shoot",
            Purpose = VisionTrainingPurpose.MeterSupervisedTraining,
            GameplayRegion = new(0, 0, 1, 1), MeterRegion = new(0, .5, .25, .5),
            FeedbackRegion = new(.5, 0, .5, .25)
        }
    };

    private static VisionTrainingTrace SeedTrace()
    {
        var trace = new VisionTrainingTrace();
        long ticks = Stopwatch.GetTimestamp();
        trace.Record(new(VisionTrainingTraceKind.Poll, ticks, ticks, ticks,
            new(4096, 0, 255, short.MinValue, short.MaxValue, 12345, -23456),
            new(4096, 0, 255, 0, 0, 12345, -23456), true, 17,
            PaddleButtons.LargeRight, PaddleButtons.SmallLeft, true, true));
        return trace;
    }

    private static void EnrichedTake(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        var configured = EvidenceSettings(settings);
        var trace = SeedTrace();
        int detachCalls = 0;
        using var recorder = new VisionTrainingRecorder(directory, profile, configured, "Synchronized evidence fixture",
            controllerTrace: trace, detachControllerTrace: () => Interlocked.Increment(ref detachCalls));
        using var first = Frame(41, 9);
        using var second = Frame(42, 9);
        recorder.Capture(first, default(VisionStatusSnapshot) with { ShootHeld = true });
        recorder.Capture(second, default);
        recorder.Stop(complete: true);
        recorder.Dispose();
        CheckFinalSnapshot(recorder, 2, true);
        Check(detachCalls == 1, "Controller trace detach was not invoked exactly once across stop/finalize/dispose.");
        using var metadata = ReadMetadata(recorder);
        var root = metadata.RootElement;
        var evidence = root.GetProperty("captureEvidence");
        Check(evidence.GetProperty("version").GetInt32() == 1
                && evidence.GetProperty("purpose").GetString() == "meterSupervisedTraining",
            "Enriched evidence version/purpose was missing or misrepresented.");
        var layout = evidence.GetProperty("layout");
        Check(layout.GetProperty("sourceWidth").GetInt32() == 8 && layout.GetProperty("sourceHeight").GetInt32() == 8
                && layout.GetProperty("hudName").GetString() == configured.TrainingCapture.HudName
                && layout.GetProperty("gameBuild").GetString() == configured.TrainingCapture.GameBuild
                && layout.GetProperty("shotCategory").GetString() == configured.TrainingCapture.ShotCategory,
            "Evidence metadata lost source geometry or HUD/build/shot-category provenance.");
        Check(evidence.GetProperty("maximumVisualBytes").GetInt64() == VisionTrainingRecorder.EvidenceByteLimit,
            "Evidence metadata declared the wrong aggregate visual storage limit.");
        Check(evidence.GetProperty("controllerTraceFile").GetString() == "controller-trace.json"
                && evidence.GetProperty("controllerSamples").GetInt32() == 1
                && evidence.GetProperty("droppedControllerSamples").GetInt64() == 0,
            "Visual evidence did not identify its complete controller trace.");
        Check(evidence.GetProperty("labelStatus").GetString()!.Contains("Raw synchronized evidence only", StringComparison.Ordinal)
                && evidence.GetProperty("shotTokenScope").GetString()!.Contains("not a gameplay shot identifier", StringComparison.Ordinal),
            "Raw meter/feedback observations or arbiter tokens were presented as reviewed shot labels.");
        AssertRegions(recorder, metadata, configured, [first, second]);
        using var controller = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "controller-trace.json")));
        var samples = controller.RootElement.GetProperty("samples");
        Check(samples.GetArrayLength() == 1 && samples[0].GetProperty("kind").GetString() == "poll"
                && samples[0].GetProperty("sampleTicks").GetInt64() == trace.Snapshot().Samples[0].SampleTicks
                && samples[0].GetProperty("physical").GetProperty("leftX").GetInt16() == short.MinValue
                && samples[0].GetProperty("physical").GetProperty("rightTrigger").GetByte() == 255
                && samples[0].GetProperty("requestedOutput").GetProperty("leftX").GetInt16() == 0
                && samples[0].GetProperty("heldPaddles").GetString() == "largeRight"
                && samples[0].GetProperty("pressedPaddles").GetString() == "smallLeft"
                && samples[0].GetProperty("acceptedForLocalHandoff").GetBoolean(),
            "Persisted controller evidence lost raw axes, output transformation, paddles or sampling times.");
        Check(controller.RootElement.GetProperty("timingScope").GetString()!.Contains("Xbox receipt/application is not measured", StringComparison.Ordinal),
            "Local controller handoff was mislabeled as measured Xbox receipt.");
        using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "labels.example.json")));
        Check(labels.RootElement.GetProperty("shots").GetArrayLength() == 0, "Meter capture invented target-release labels.");
        Console.WriteLine("SUCCESS: synchronized player/scene/meter/feedback pixels, frame provenance, HUD geometry, controller persistence and label boundaries verified.");
    }

    private static void AssertRegions(VisionTrainingRecorder recorder, JsonDocument metadata, VisionSettings settings,
        CapturedVisionFrame[] sources)
    {
        var root = metadata.RootElement;
        var streams = root.GetProperty("captureEvidence").GetProperty("streams");
        bool meterOn = settings.TrainingCapture.Purpose == VisionTrainingPurpose.MeterSupervisedTraining;
        Check(streams.GetArrayLength() == (meterOn ? 4 : 3), "A synchronized evidence stream is missing or an unused meter stream was added.");
        var entries = root.GetProperty("frames");
        long visualBytes = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            string name = stream.GetProperty("name").GetString()!;
            string file = stream.GetProperty("file").GetString()!;
            int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
            Check(stream.GetProperty("stride").GetInt32() == width * 4
                    && stream.GetProperty("pixelFormat").GetString() == "BGRA8", "Stored evidence pixel layout is invalid.");
            var crop = JsonSerializer.Deserialize<VisionCrop>(stream.GetProperty("crop").GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            VisionCrop expectedCrop = name switch
            {
                "player" => settings.Crop, "scene" => settings.TrainingCapture.GameplayRegion,
                "meter" => settings.TrainingCapture.MeterRegion!.Value,
                "feedback" => settings.TrainingCapture.FeedbackRegion!.Value,
                _ => throw new InvalidOperationException("Unexpected evidence region name.")
            };
            Check(crop == expectedCrop, "Stored evidence crop geometry changed.");
            int length = checked(width * height * 4);
            byte[] pixels = File.ReadAllBytes(Path.Combine(recorder.DirectoryPath, file));
            visualBytes += pixels.Length;
            Check(pixels.Length == sources.Length * length, "An evidence file length disagrees with its indexed frames.");
            for (int index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                var entry = entries[index];
                Check(entry.GetProperty("sequence").GetInt64() == source.Sequence
                        && entry.GetProperty("captureTicks").GetInt64() == source.CaptureTicks
                        && entry.GetProperty("sourceGeneration").GetInt64() == source.SourceGeneration
                        && entry.GetProperty("observedTicks").GetInt64() >= source.CaptureTicks,
                    "Visual regions lost their shared source frame/QPC/generation provenance.");
                JsonElement regionEntry = name == "player" ? entry
                    : entry.GetProperty("evidence").EnumerateArray().Single(value => value.GetProperty("name").GetString() == name);
                Check(regionEntry.GetProperty("file").GetString() == file
                        && regionEntry.GetProperty("offsetBytes").GetInt64() == index * (long)length
                        && regionEntry.GetProperty("byteLength").GetInt32() == length,
                    "An evidence region offset or byte length points to a different source frame.");
                foreach (var (x, y) in new[] { (0, 0), (width / 2, height / 2), (width - 1, height - 1) })
                {
                    int sx = Math.Min(source.Width - 1, (int)((crop.Left + (x + .5) / width * crop.Width) * source.Width));
                    int sy = Math.Min(source.Height - 1, (int)((crop.Top + (y + .5) / height * crop.Height) * source.Height));
                    int offset = index * length + (y * width + x) * 4;
                    Check(pixels[offset] == (byte)sx && pixels[offset + 1] == (byte)sy
                            && pixels[offset + 2] == (byte)(64 + source.Sequence) && pixels[offset + 3] == 255,
                        "A synchronized evidence pixel came from the wrong region or source frame.");
                }
            }
        }
        Check(visualBytes <= root.GetProperty("captureEvidence").GetProperty("maximumVisualBytes").GetInt64(),
            "Combined evidence files exceeded the declared aggregate storage cap.");
    }

    private static void EnrichedInterruptedTakes(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        var configured = EvidenceSettings(settings);
        using var recorder = new VisionTrainingRecorder(directory, profile, configured, "HUD size-change fixture", controllerTrace: SeedTrace());
        using var valid = Frame(1, 1);
        using var resized = Frame(2, 1, width: 9);
        recorder.Capture(valid, default);
        recorder.Capture(resized, default);
        recorder.Dispose();
        CheckFinalSnapshot(recorder, 1, false);
        using var metadata = ReadMetadata(recorder);
        AssertRegions(recorder, metadata, configured, [valid]);
        Check(!metadata.RootElement.GetProperty("complete").GetBoolean()
                && recorder.Message.Contains("Capture size changed", StringComparison.Ordinal),
            "A capture dimension change silently reused stale HUD coordinates.");

        var meterOff = profile with { UsesShotMeter = false, MeterReleaseType = "" };
        var validation = configured with { AnimationProfile = "nba2k27:" + meterOff.ConfigurationFingerprint,
            TrainingCapture = configured.TrainingCapture with { Purpose = VisionTrainingPurpose.MeterOffValidation } };
        using var heldOut = new VisionTrainingRecorder(directory, meterOff, validation, "Meter-off evidence fixture", controllerTrace: SeedTrace());
        heldOut.Capture(valid, default); heldOut.Stop(complete: true); heldOut.Dispose();
        CheckFinalSnapshot(heldOut, 1, true);
        using var validationMetadata = ReadMetadata(heldOut);
        AssertRegions(heldOut, validationMetadata, validation, [valid]);
        Check(!File.Exists(Path.Combine(heldOut.DirectoryPath, "meter.bgra"))
                && validationMetadata.RootElement.GetProperty("captureEvidence").GetProperty("purpose").GetString() == "meterOffValidation",
            "Meter-off validation evidence was confused with meter-supervised training.");
    }

    private static void EnrichedTraceLifetime(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        using var input = new EliteInputMonitor();
        var physical = new ControllerState((ushort)ControlId.X, 11, 219, 4000, -5000, 17000, -21000);
        input.SetState(physical, PaddleButtons.LargeLeft);
        using var runtime = new StudioRuntime(input, StudioProfile.CreateDefault() with { ScriptsEnabled = false }, new(0, 0));
        runtime.Attach(new UsbipXboxOutput()); runtime.SetVisionTrainingRecording(true);
        var trace = new VisionTrainingTrace();
        runtime.SetVisionTrainingTrace(trace);
        int detached = 0;
        using var recorder = new VisionTrainingRecorder(directory, profile, EvidenceSettings(settings), "Live runtime evidence fixture",
            controllerTrace: trace, detachControllerTrace: () =>
            { runtime.SetVisionTrainingTrace(null); Interlocked.Increment(ref detached); });
        Check(SpinWait.SpinUntil(() => trace.StoredSamples >= 3, TimeSpan.FromSeconds(3)), "The live runtime did not populate controller evidence.");
        using var frame = Frame(1, 1); recorder.Capture(frame, default);
        recorder.Stop(complete: true); recorder.Dispose();
        var snapshot = trace.Snapshot();
        CheckFinalSnapshot(recorder, 1, true);
        Check(detached == 1, "Runtime trace detach was not exactly once.");
        var next = physical with { RightY = 25000 }; input.SetState(next);
        Check(SpinWait.SpinUntil(() => runtime.Latest?.Input.State == next, TimeSpan.FromSeconds(3)), "Runtime polling stalled after evidence finalization.");
        Check(trace.StoredSamples == snapshot.Samples.Length, "Controller samples arrived after evidence was detached and saved.");
        using var traceFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "controller-trace.json")));
        Check(traceFile.RootElement.GetProperty("samples").GetArrayLength() == snapshot.Samples.Length,
            "Persisted controller trace was finalized before its last pre-detach sample.");
        runtime.SetVisionTrainingRecording(false);
    }

    private static void EnrichedTraceFailures(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        foreach (bool overflow in new[] { false, true })
        {
            var trace = new VisionTrainingTrace(1);
            var seed = SeedTrace().Snapshot().Samples[0];
            if (overflow) { trace.Record(seed); trace.Record(seed); }
            using var recorder = new VisionTrainingRecorder(directory, profile, EvidenceSettings(settings), "Incomplete controller evidence fixture",
                controllerTrace: trace);
            using var frame = Frame(1, 1); recorder.Capture(frame, default);
            recorder.Stop(complete: true); recorder.Dispose();
            CheckFinalSnapshot(recorder, 1, false);
            using var metadata = ReadMetadata(recorder);
            Check(!metadata.RootElement.GetProperty("complete").GetBoolean()
                    && metadata.RootElement.GetProperty("captureEvidence").GetProperty("droppedControllerSamples").GetInt64() == (overflow ? 1 : 0),
                "Missing/overflowed controller evidence was marked complete or lost its exact drop count.");
        }
        Console.WriteLine("SUCCESS: stale HUD size rejection, meter-off stream isolation, runtime trace finalization and missing/overflowed controller evidence verified.");
    }

    private static void ResolutionCaptureProbe(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        var configured = EvidenceSettings(settings, 2560, 1440);
        using var recorder = new VisionTrainingRecorder(directory, profile, configured, "1440p synthetic normalized-region probe", controllerTrace: SeedTrace());
        using var frame = Frame(3, 1, 2560, 1440);
        long started = Stopwatch.GetTimestamp(); recorder.Capture(frame, default);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        recorder.Stop(complete: true); recorder.Dispose();
        CheckFinalSnapshot(recorder, 1, true);
        using var metadata = ReadMetadata(recorder);
        AssertRegions(recorder, metadata, configured, [frame]);
        Console.WriteLine($"SUCCESS: 2560x1440 normalized multi-region sampling verified; one synthetic capture copy took {elapsed:F2} ms (observation only, not a latency certification).");
    }

    private static void EnrichedStorageLimit(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        const long expectedBytesPerFrame = (256L * 256 + 640L * 360 + 256L * 256 + 640L * 192) * 4;
        var configured = EvidenceSettings(settings);
        using var recorder = new VisionTrainingRecorder(directory, profile, configured, "Bounded evidence storage fixture",
            controllerTrace: SeedTrace(), byteLimit: expectedBytesPerFrame * 2);
        using var first = Frame(1, 1); using var second = Frame(2, 1); using var third = Frame(3, 1);
        recorder.Capture(first, default); recorder.Capture(second, default); recorder.Capture(third, default);
        Check(SpinWait.SpinUntil(() => !recorder.IsRecording, TimeSpan.FromSeconds(3)), "Enriched evidence did not stop at its aggregate byte limit.");
        recorder.Dispose();
        CheckFinalSnapshot(recorder, 2, true, droppedFrames: 1);
        using var metadata = ReadMetadata(recorder);
        AssertRegions(recorder, metadata, configured, [first, second]);
        Check(metadata.RootElement.GetProperty("droppedFrames").GetInt64() == 1
                && metadata.RootElement.GetProperty("captureEvidence").GetProperty("maximumVisualBytes").GetInt64() == expectedBytesPerFrame * 2,
            "Aggregate evidence storage cap lost its exact dropped-frame count or declared limit.");
    }

    private static void EnrichedPartialStreamFailure(string directory, Nba2K27ShotProfile profile, VisionSettings settings)
    {
        var failure = new DelayedFailureStream();
        int detached = 0;
        using var recorder = new VisionTrainingRecorder(directory, profile, EvidenceSettings(settings), "Partial evidence IO failure fixture",
            streamFactory: path => Path.GetFileName(path) == "feedback.bgra" ? failure : new MemoryStream(),
            controllerTrace: SeedTrace(), detachControllerTrace: () => Interlocked.Increment(ref detached));
        try
        {
            using var first = Frame(1, 1); using var second = Frame(2, 1);
            recorder.Capture(first, default); recorder.Capture(second, default);
            Check(failure.WriteEntered.Task.Wait(TimeSpan.FromSeconds(3)), "The enriched writer did not reach its injected feedback failure.");
            Check(recorder.Snapshot.StoredFrames == 1, "A partly written multi-region frame appeared in the completed frame index.");
            recorder.Stop(complete: true); failure.Release.TrySetResult(); recorder.Dispose();
            CheckFinalSnapshot(recorder, 1, false);
            using var metadata = ReadMetadata(recorder);
            Check(!metadata.RootElement.GetProperty("complete").GetBoolean()
                    && metadata.RootElement.GetProperty("frames").GetArrayLength() == 1 && detached == 1,
                "A partial multi-region write was finalized as complete or failed to detach its controller trace.");
        }
        finally { failure.Release.TrySetResult(); }
        Console.WriteLine("SUCCESS: aggregate multi-stream storage bound and partial feedback-stream failure verified without publishing partial frames.");
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

    private static void CheckFinalSnapshot(VisionTrainingRecorder recorder, long storedFrames, bool complete, long droppedFrames = 0)
    {
        var snapshot = recorder.Snapshot;
        Check(snapshot.HasTake && !snapshot.IsRecording && snapshot.IsFinalized && snapshot.IsComplete == complete &&
            snapshot.DirectoryPath == recorder.DirectoryPath && snapshot.StoredFrames == storedFrames &&
            snapshot.DroppedFrames == droppedFrames && !string.IsNullOrWhiteSpace(snapshot.Message),
            "Final training snapshot disagrees with its writer completion, metadata outcome, path or frame counts.");
    }

    private static void Check(bool success, string error)
    {
        if (!success) throw new InvalidOperationException(error);
    }
}
