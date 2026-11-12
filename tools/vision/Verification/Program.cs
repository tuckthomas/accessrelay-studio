using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

if (args is ["--gpu-fixture", var manifestPath])
{
    GpuFixture(manifestPath);
    return;
}
if (args is ["--preprocessing-fixture", var outputDirectory])
{
    CanonicalPreprocessing(outputDirectory);
    return;
}

var tests = new (string Name, Action Test)[]
{
    ("BGRA player crop produces RGB planar data without channel inversion", CropColors),
    ("Canonical nearest256 recording and live crop preprocessing agree", () => CanonicalPreprocessing(null)),
    ("Manifest/model output rejects invalid dimensions and nonfinite results", InvalidModels),
    ("Arming requires hashed matching held-out and hardware evidence", ArmingEvidence),
    ("Temporal history rejects old frames and preserves observation tokens", TemporalHistory),
    ("Frozen player ROI cancels a scheduled release despite changing outside pixels", FrozenPlayerRegion),
    ("Calibration sign, reviewed measurements and bounded diagnostics", CalibrationAndDiagnostics)
};
foreach (var test in tests) { test.Test(); Console.WriteLine($"SUCCESS {test.Name}"); }

static void CropColors()
{
    byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255];
    float[] rgb = new float[12];
    FramePreprocessor.FillRgb(bgra, 2, 2, 8, new(0, 0, 1, 1), 2, 2, [0, 0, 0], [1, 1, 1], rgb);
    Require(rgb.AsSpan().SequenceEqual(new float[] { 1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1 }), "RGB channel layout differs.");
    float[] right = new float[3];
    FramePreprocessor.FillRgb(bgra, 2, 2, 8, new(.5, 0, .5, .5), 1, 1, [0, 0, 0], [1, 1, 1], right);
    Require(right.AsSpan().SequenceEqual(new float[] { 0, 1, 0 }), "Player crop did not isolate the green pixel.");
    Throws(() => FramePreprocessor.FillRgb(bgra, 2, 2, 8, new(.9, 0, .5, 1), 1, 1, [0, 0, 0], [1, 1, 1], right));
}

static VisionModelManifest Manifest() => new()
{
    DisplayName = "Verification data only", ModelVersion = "test", ModelFile = "model.onnx", ModelSha256 = new('a', 64),
    AnimationProfile = "Test", GameVersion = "test-game", CameraConfiguration = "test-camera", License = "0BSD",
    HistoryLength = 2, InputWidth = 32, InputHeight = 32, HistoryIntervalMs = 10
};

static void CanonicalPreprocessing(string? directory)
{
    const int width = 53, height = 41, targetWidth = 37, targetHeight = 43;
    VisionCrop crop = new(.137, .213, .681, .537);
    float[] mean = [.13f, .21f, .37f], standardDeviation = [.31f, .53f, .71f];
    byte[] source = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            source[offset] = (byte)((x * 11 + y * 17) % 256);
            source[offset + 1] = (byte)((x * 29 + y * 7) % 256);
            source[offset + 2] = (byte)((x * 3 + y * 43) % 256);
            source[offset + 3] = 255;
        }
    byte[] stored = new byte[256 * 256 * 4];
    // Independent recording side follows VisionTrainingRecorder's nearest source sampling contract.
    for (int y = 0; y < 256; y++)
    {
        int sy = Math.Min(height - 1, (int)((crop.Top + (y + .5) / 256 * crop.Height) * height));
        for (int x = 0; x < 256; x++)
        {
            int sx = Math.Min(width - 1, (int)((crop.Left + (x + .5) / 256 * crop.Width) * width));
            source.AsSpan((sy * width + sx) * 4, 4).CopyTo(stored.AsSpan((y * 256 + x) * 4, 4));
        }
    }
    float[] live = new float[3 * targetWidth * targetHeight], recorded = new float[live.Length];
    FramePreprocessor.FillRgb(source, width, height, width * 4, crop, targetWidth, targetHeight, mean, standardDeviation, live);
    FramePreprocessor.FillRgb(stored, 256, 256, 1024, new(0, 0, 1, 1), targetWidth, targetHeight, mean, standardDeviation, recorded);
    Require(live.AsSpan().SequenceEqual(recorded), "Live source crop and recorded nearest256 data preprocessing differ.");
    if (directory is not null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "source.bgra"), source);
        File.WriteAllBytes(Path.Combine(directory, "stored256.bgra"), stored);
        File.WriteAllBytes(Path.Combine(directory, "expected-rgb.f32"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(live.AsSpan()).ToArray());
        File.WriteAllText(Path.Combine(directory, "parity.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, sourceWidth = width, sourceHeight = height, crop,
            targetWidth, targetHeight, rgbMean = mean, rgbStandardDeviation = standardDeviation,
            preprocessing = "bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2"
        }, VisionModelManifest.Json));
        Console.WriteLine("SUCCESS exported live C# / canonical recording parity fixture.");
    }
}

static void InvalidModels()
{
    Manifest().Validate();
    (VisionSettings.Default with { CameraView = "2K Low" }).Validate();
    Throws(() => (VisionSettings.Default with { CameraView = "Unsupported view" }).Validate());
    Require((Manifest() with { CameraConfiguration = "2K" }).CheckArmingEvidence("unused.json",
        VisionSettings.Default with { CameraView = "2K Low" }).Contains("camera view"), "A mismatched camera was not rejected.");
    Throws(() => (Manifest() with { HistoryLength = 0 }).Validate());
    Throws(() => (Manifest() with { RgbStandardDeviation = [1, float.NaN, 1] }).Validate());
    Throws(() => VisionModel.ValidateObservation([1, 1, float.NaN, 1, .5f], 1000));
    Throws(() => VisionModel.ValidateObservation([1, 1, -1, 1, .5f], 1000));
    VisionModel.ValidateObservation([1, 1, 50, 2, .5f], 1000);
    Throws(() => VisionModelManifest.ResolveChildFile(Path.Combine(Path.GetTempPath(), "models", "manifest.json"), "../outside.onnx"));
}

static void ArmingEvidence()
{
    var manifest = Manifest();
    var settings = VisionSettings.Default with { AnimationProfile = "Test", MinimumConfidence = .9, MaximumUncertaintyMs = 5, MaximumFrameAgeMs = 80 };
    Require(manifest.CheckArmingEvidence("unused.json", settings).Length > 0, "An unvalidated model can arm.");
    string directory = Path.Combine(Path.GetTempPath(), "accessrelay-vision-checks-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var report = new VisionValidationReport
        {
            SchemaVersion = 1, DatasetId = "fixture-not-production", Split = "held-out", ModelSha256 = manifest.ModelSha256,
            ModelVersion = manifest.ModelVersion, AnimationProfile = manifest.AnimationProfile, GameVersion = manifest.GameVersion,
            CameraConfiguration = manifest.CameraConfiguration, CalibrationProcedureVersion = "test", MeterOff = true,
            NoPlayerIconRequired = true, ThresholdsLockedBeforeEvaluation = true, MinimumConfidence = .9,
            MaximumUncertaintyMs = 5, MaximumFrameAgeMs = 80, TargetShots = 100, NegativeShots = 100,
            EvaluatedPredictions = 90, TargetCoverage = .9, IdentityAccuracy = 1, PredictionP99ErrorMs = 2,
            FalseReleaseRate = 0, HardwareValidated = false
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, VisionModelManifest.Json);
        File.WriteAllBytes(Path.Combine(directory, "report.json"), bytes);
        var gates = new VisionValidationEvidence
        {
            ReportFile = "report.json", ReportSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            DatasetId = report.DatasetId, CalibrationProcedureVersion = "test", MinimumTargetShots = 100, MinimumNegativeShots = 100,
            MinimumConfidence = .9, MaximumUncertaintyMs = 5, MaximumFrameAgeMs = 80, MaximumPredictionP99ErrorMs = 3,
            MaximumFalseReleaseRate = 0, MinimumIdentityAccuracy = .99, MinimumTargetCoverage = .8,
            MaximumDeadlineMissRate = .01, MinimumHardwareSessionMinutes = 30
        };
        manifest = manifest with { Validation = gates };
        Require(manifest.CheckArmingEvidence(Path.Combine(directory, "manifest.json"), settings).Contains("hardware", StringComparison.OrdinalIgnoreCase),
            "Offline accuracy evidence bypassed the hardware gate.");
        Require(manifest.CheckArmingEvidence(Path.Combine(directory, "manifest.json"), settings with { MinimumConfidence = .1 }).Contains("weaker"),
            "A weakened runtime threshold bypassed the locked gate.");
        File.AppendAllText(Path.Combine(directory, "report.json"), " ");
        Require(manifest.CheckArmingEvidence(Path.Combine(directory, "manifest.json"), settings).Contains("SHA-256"), "Report tampering was not detected.");
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static void TemporalHistory()
{
    var model = Manifest();
    float[] history = new float[2 * 3 * 32 * 32];
    int runs = 0;
    var tracker = new ShotTracker(model, VisionSettings.Default with { MaximumFrameAgeMs = 1000 }, history,
        () => { runs++; return new(1, 1, 50, 2, .3, 1); }, new());
    long now = Stopwatch.GetTimestamp();
    long tenMs = (long)(Stopwatch.Frequency * .01);
    var snapshot = new VisionStatusSnapshot(VisionRunState.TrackingShot, true, true, "test", 7, 9, now - 10 * tenMs,
        true, null, 0, 0, 0, 0, 0, 0);
    byte[] pixels = new byte[4 * 32 * 32];
    using var first = new CapturedVisionFrame(pixels, 32, 32, now - 2 * tenMs, 1, 2, () => { });
    using var second = new CapturedVisionFrame(pixels, 32, 32, now - tenMs, 2, 2, () => { });
    Require(tracker.PushFrame(first, snapshot) is null, "Incomplete history produced a prediction.");
    var prediction = tracker.PushFrame(second, snapshot);
    Require(prediction is { SessionGeneration: 7, ShotToken: 9, FrameSequence: 2 } && runs == 1, "Observation lost its captured tokens.");
    Require(tracker.PushFrame(first, snapshot) is { PlayerTracked: false } && runs == 1, "An old frame was inferred again.");
    Require(tracker.PushFrame(second, snapshot with { ShotToken = 10 }) is null, "A new shot reused old temporal history.");
}

static void FrozenPlayerRegion()
{
    var manifest = Manifest();
    var settings = VisionSettings.Default with { Crop = new(0, 0, .5, 1), MaximumFrameAgeMs = 25 };
    long origin = Stopwatch.GetTimestamp();
    long now = origin;
    long Tick(double milliseconds) => origin + (long)(milliseconds * Stopwatch.Frequency / 1000);
    int runs = 0;
    var tracker = new ShotTracker(manifest, settings, new float[2 * 3 * 32 * 32],
        () => { runs++; return new(1, 1, 100, 2, .3, 1); }, new(), () => now);
    var arbiter = new ControllerOutputArbiter(Stopwatch.Frequency);
    arbiter.Configure(settings);
    arbiter.BeginSession();
    arbiter.Sample(default, default, true, true, false, now);
    Require(arbiter.SetArmed(true), "Test controller could not arm.");
    var held = default(ControllerState) with { Buttons = (ushort)ControlId.X };
    arbiter.Sample(held, held, true, true, false, now);

    VisionPrediction? Observe(int sequence, double elapsedMs, byte playerValue, byte outsideValue)
    {
        now = Tick(elapsedMs);
        var bytes = new byte[64 * 32 * 4];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 64; x++)
            {
                int offset = (y * 64 + x) * 4;
                bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = x < 32 ? playerValue : outsideValue;
                bytes[offset + 3] = 255;
            }
        using var frame = new CapturedVisionFrame(bytes, 64, 32, now, sequence, 1, () => { });
        return tracker.PushFrame(frame, arbiter.Status);
    }

    Require(Observe(1, 1, 10, 20) is null, "Cold player history produced a prediction.");
    var initial = Observe(2, 11, 10, 40);
    Require(initial is { PlayerTracked: true } && arbiter.Submit(initial.Value, now), "Initial valid observation did not schedule release.");
    Require(arbiter.DeadlineTimestamp.HasValue, "No deadline existed before ROI freeze.");
    var advancingPaint = Observe(3, 21, 10, 60);
    Require(advancingPaint is { PlayerTracked: true } && arbiter.Submit(advancingPaint.Value, now), "Pre-threshold observation failed unexpectedly.");
    int beforeFreeze = runs;
    var frozen = Observe(4, 31, 10, 80);
    Require(frozen is { PlayerTracked: false, Confidence: 0 } && runs == beforeFreeze,
        "Changing browser/outside pixels concealed a frozen player region.");
    var rejected = frozen ?? throw new InvalidOperationException("Frozen player ROI did not emit a cancellation observation.");
    Require(!arbiter.Submit(rejected, now) && !arbiter.DeadlineTimestamp.HasValue,
        "Frozen-region rejection left the prior release deadline alive.");
    Require(Observe(5, 41, 30, 100) is null, "Changing player pixels reused frozen temporal history.");
    var reacquired = Observe(6, 51, 30, 120);
    Require(reacquired is { PlayerTracked: true } && !arbiter.Submit(reacquired.Value, now) && !arbiter.DeadlineTimestamp.HasValue,
        "ROI reacquisition bypassed the physical-release/rearm requirement.");
}

static void CalibrationAndDiagnostics()
{
    var attempts = Enumerable.Range(0, 5).Select(index => new VisionCalibrationAttempt(index.ToString(), true, true, 30d));
    var estimate = VisionCalibration.Estimate(attempts, 10);
    Require(estimate.RecommendedCorrectionMs == -20 && estimate.ResidualP95Ms == 0, "Late-release correction sign is incorrect.");
    Throws(() => VisionCalibration.Estimate([new("coarse-early", true, false, 0)], 0));
    var manifest = Manifest();
    var settings = VisionSettings.Default with { AnimationProfile = manifest.AnimationProfile, CorrectionMs = 10 };
    string hash = new('b', 64);
    var imported = new VisionCalibrationImport
    {
        ModelManifestHash = hash, AnimationProfile = manifest.AnimationProfile, GameVersion = manifest.GameVersion,
        CameraConfiguration = manifest.CameraConfiguration, ProcedureVersion = "measured-test", CaptureWidth = 1920,
        CaptureHeight = 1080, Crop = settings.Crop, PreviousCorrectionMs = 10, Attempts = attempts.ToArray()
    };
    imported.ValidateContext(hash, manifest, settings, 1920, 1080);
    Throws(() => imported.ValidateContext(new('c', 64), manifest, settings, 1920, 1080));
    Throws(() => imported.ValidateContext(hash, manifest, settings with { AnimationProfile = "Different shot" }, 1920, 1080));
    Throws(() => imported.ValidateContext(hash, manifest, settings with { Crop = new(.1, 0, .9, 1) }, 1920, 1080));
    Throws(() => imported.ValidateContext(hash, manifest, settings with { EstimatedInputDelayMs = 5 }, 1920, 1080));
    Throws(() => imported.ValidateContext(hash, manifest, settings with { CorrectionMs = 20 }, 1920, 1080));
    Throws(() => imported.ValidateContext(hash, manifest, settings, 1280, 720));
    Throws(() => (imported with { GameVersion = "other-game" }).ValidateContext(hash, manifest, settings, 1920, 1080));
    Throws(() => imported.ValidateContext(hash, manifest, settings with { CameraView = "2K Legacy" }, 1920, 1080));
    var diagnostics = new VisionDiagnostics();
    for (int index = 0; index < 600; index++) diagnostics.Record(VisionDiagnosticKind.Preprocessed, index, 1, 1);
    var events = diagnostics.ExportEvents();
    Require(events.Length == 512 && events[0].FrameSequence == 88 && events[^1].FrameSequence == 599, "Telemetry ring grew or reordered events.");
}

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Throws(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new InvalidOperationException("An invalid model/crop/evidence value was accepted.");
}

static void GpuFixture(string path)
{
    var manifest = VisionModelManifest.Read(path).Manifest;
    var settings = VisionSettings.Default with { AnimationProfile = manifest.AnimationProfile, MaximumFrameAgeMs = 1000 };
    using var model = VisionModel.Load(path, settings);
    Require(!model.CanArm, "The untrained diagnostic fixture unexpectedly passed arming validation.");
    byte[] pixels = new byte[64 * 64 * 4];
    for (int index = 0; index < pixels.Length; index += 4) { pixels[index] = 32; pixels[index + 1] = 64; pixels[index + 2] = 128; pixels[index + 3] = 255; }
    long step = (long)(Stopwatch.Frequency * manifest.HistoryIntervalMs / 1000);
    long baseline = Stopwatch.GetTimestamp() - step * 20;
    var status = new VisionStatusSnapshot(VisionRunState.Observing, false, false, "GPU fixture only", 1, 0, 0,
        false, null, 0, 0, 0, 0, 0, 0);
    int predictions = 0;
    for (int index = 0; index < 20; index++)
    {
        using var frame = new CapturedVisionFrame(pixels, 64, 64, baseline + index * step, index + 1, 1, () => { });
        if (model.PushFrame(frame, status) is { } prediction)
        {
            Require(double.IsFinite(prediction.TimeToReleaseMs) && prediction.SessionGeneration == 1, "GPU result is invalid.");
            predictions++;
        }
    }
    var diagnostics = model.Diagnostics.Snapshot();
    Require(predictions > 0 && diagnostics.Inferences > 0, "No CUDA inference completed.");
    Console.WriteLine("SUCCESS actual CUDA-only session load/warm-up and temporal inference completed on the installed GPU.");
    Console.WriteLine($"Diagnostic observations: {predictions}; last inference including host transfers: {diagnostics.LastInferenceWithTransfersMs:F3} ms.");
    Console.WriteLine($"CanArm={model.CanArm}; {model.ValidationSummary}");
    Console.WriteLine("Synthetic fixture: this proves runtime compatibility only, not gameplay accuracy or Remote Play timing.");
}
