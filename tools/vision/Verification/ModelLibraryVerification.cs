using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

internal static class ModelLibraryVerification
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelayModelLibraryVerification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ManagedImport(directory);
            EvidenceAndContext(directory);
            InvalidPackages(directory);
            RepairInstalledPackages(directory);
            DiscoveryBounds(directory);
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(resolved), temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("AccessRelayModelLibraryVerification-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned model-library verification directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static VisionSettings Settings => VisionSettings.Default with { AnimationProfile = "verification-shot", CameraView = "2K" };

    private static (VisionModelManifest Manifest, string Path) Package(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "weights"));
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        // File-management fixture only. No actual ONNX graph is loaded or declared trained.
        File.WriteAllBytes(Path.Combine(directory, "weights", "model.onnx"), bytes);
        var manifest = new VisionModelManifest
        {
            DisplayName = "UNTRAINED library verification", ModelVersion = "test-v1", ModelFile = "weights/model.onnx",
            ModelSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), AnimationProfile = Settings.AnimationProfile,
            GameVersion = "verification-game", CameraConfiguration = "2K", License = "0BSD",
            InputWidth = 32, InputHeight = 32, HistoryLength = 2
        };
        string path = Path.Combine(directory, "source-manifest.json");
        WriteManifest(path, manifest);
        return (manifest, path);
    }

    private static void ManagedImport(string directory)
    {
        var library = new VisionModelLibrary(Path.Combine(directory, "managed-library"));
        var empty = library.Discover(Settings);
        Require(empty.Models.Count == 0 && empty.Errors.Count == 0 && !Directory.Exists(library.DirectoryPath),
            "An empty library created or automatically discovered a diagnostic model.");
        var package = Package(Path.Combine(directory, "managed-source"));
        byte[] originalManifest = File.ReadAllBytes(package.Path);
        var entry = library.Import(package.Path, Settings);
        Require(entry.DisplayName == package.Manifest.DisplayName && entry.IsCompatible && !entry.EvidenceAccepted &&
            entry.Status.Contains("Observation only", StringComparison.OrdinalIgnoreCase),
            "An unvalidated imported model was hidden or presented as validated.");
        Require(File.ReadAllBytes(entry.ManifestPath).AsSpan().SequenceEqual(originalManifest),
            "Import changed manifest bytes and invalidated its identity.");
        Require(library.Import(package.Path, Settings).ManifestPath == entry.ManifestPath &&
            Directory.GetDirectories(library.DirectoryPath).Length == 1, "Identical imports created duplicate packages.");
        File.Delete(package.Path);
        File.Delete(Path.Combine(Path.GetDirectoryName(package.Path)!, "weights", "model.onnx"));
        Require(library.Discover(Settings).Models.Single().ManifestPath == entry.ManifestPath,
            "The installed model still depended on its original package files.");
        var changed = library.Discover(Settings with { AnimationProfile = "another-shot" }).Models.Single();
        Require(!changed.IsCompatible && !changed.EvidenceAccepted && changed.Status.Contains("shot profile"),
            "An incompatible shot profile could select a ready model.");
        changed = library.Discover(Settings with { CameraView = "2K Low" }).Models.Single();
        Require(!changed.IsCompatible && changed.Status.Contains("camera"), "Camera mismatch was not shown.");
    }

    private static void EvidenceAndContext(string directory)
    {
        var package = Package(Path.Combine(directory, "evidence-source"));
        var gates = new VisionValidationEvidence
        {
            ReportFile = "evidence/report.json", DatasetId = "synthetic-fixture-only", CalibrationProcedureVersion = "fixture-v1",
            ExecutionProvider = "CUDAExecutionProvider", MinimumTargetShots = 100, MinimumNegativeShots = 100,
            MinimumConfidence = .9, MaximumUncertaintyMs = 5, MaximumFrameAgeMs = 80, MaximumPredictionP99ErrorMs = 3,
            MaximumFalseReleaseRate = 0, MinimumIdentityAccuracy = .99, MinimumTargetCoverage = .8,
            MaximumDeadlineMissRate = .01, MinimumHardwareSessionMinutes = 30
        };
        var report = new VisionValidationReport
        {
            SchemaVersion = 1, DatasetId = gates.DatasetId, Split = "held-out", ModelSha256 = package.Manifest.ModelSha256,
            ModelVersion = package.Manifest.ModelVersion, AnimationProfile = package.Manifest.AnimationProfile,
            GameVersion = package.Manifest.GameVersion, CameraConfiguration = package.Manifest.CameraConfiguration,
            CalibrationProcedureVersion = gates.CalibrationProcedureVersion, ExecutionProvider = gates.ExecutionProvider,
            MeterOff = true, NoPlayerIconRequired = true, ThresholdsLockedBeforeEvaluation = true,
            MinimumConfidence = gates.MinimumConfidence, MaximumUncertaintyMs = gates.MaximumUncertaintyMs,
            MaximumFrameAgeMs = gates.MaximumFrameAgeMs, TargetShots = 100, NegativeShots = 100, EvaluatedPredictions = 90,
            TargetCoverage = .9, IdentityAccuracy = 1, PredictionP99ErrorMs = 2, FalseReleaseRate = 0,
            CaptureWidth = 1920, CaptureHeight = 1080, PlayerCrop = Settings.Crop, HardwareValidated = true,
            HardwareDescription = "Synthetic fixture, not real measurements", RemotePlayClient = "Synthetic fixture",
            HardwareSessionMinutes = 30, HardwareAttempts = 100, DeadlineMissRate = 0
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, VisionModelManifest.Json);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(package.Path)!, "evidence"));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(package.Path)!, "evidence", "report.json"), bytes);
        gates = gates with { ReportSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        WriteManifest(package.Path, package.Manifest with { Validation = gates });
        var library = new VisionModelLibrary(Path.Combine(directory, "evidence-library"));
        var entry = library.Import(package.Path, Settings);
        Require(entry.IsCompatible && entry.EvidenceAccepted && entry.Status.Contains("calibration"),
            "Matching synthetic evidence did not pass the library gate or omitted remaining runtime requirements.");
        Require(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(entry.ManifestPath)!, "evidence", "report.json"))
            .AsSpan().SequenceEqual(bytes), "The managed package omitted or altered its evidence.");
        var cpu = library.Discover(Settings with { ExecutionProvider = VisionExecutionProvider.Cpu }).Models.Single();
        Require(cpu.IsCompatible && !cpu.EvidenceAccepted && cpu.Status.Contains("execution provider"),
            "A CUDA report made the CPU library entry appear validated.");
    }

    private static void InvalidPackages(string directory)
    {
        var package = Package(Path.Combine(directory, "invalid-source"));
        var library = new VisionModelLibrary(Path.Combine(directory, "invalid-library"));
        foreach (string relative in new[] { "../outside.onnx", "C:/outside.onnx", "CON.onnx", "CON .onnx", "COM¹.onnx", "weights/model.onnx:stream", "weights/./model.onnx" })
        {
            WriteManifest(package.Path, package.Manifest with { ModelFile = relative });
            Throws(() => library.Import(package.Path, Settings));
        }
        WriteManifest(package.Path, package.Manifest with { ModelSha256 = new('a', 64) });
        Throws(() => library.Import(package.Path, Settings));
        Require(!Directory.EnumerateDirectories(library.DirectoryPath).Any(), "A failed hash check left a partial installed or staging package.");
        WriteManifest(package.Path, package.Manifest);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { library.Import(package.Path, Settings, cancelled.Token); throw new InvalidOperationException("Cancelled import proceeded."); }
            catch (OperationCanceledException) { }
        }
        var entry = library.Import(package.Path, Settings);
        string model = Path.Combine(Path.GetDirectoryName(entry.ManifestPath)!, "weights", "model.onnx");
        File.WriteAllBytes(model, [9, 9]);
        var corrupted = library.Discover(Settings);
        Require(corrupted.Models.Count == 0 && corrupted.Errors.Count == 1 && corrupted.Errors[0].Contains("SHA-256"),
            "A corrupted installed model was offered or failed without an actionable error.");
        var second = Package(Path.Combine(directory, "another-source"));
        WriteManifest(second.Path, second.Manifest with { ModelVersion = "second-version" });
        library.Import(second.Path, Settings);
        var mixed = library.Discover(Settings);
        Require(mixed.Models.Count == 1 && mixed.Errors.Count == 1, "A malformed entry prevented valid packages from being discovered.");
        File.WriteAllText(second.Path, new string(' ', 256 * 1024 + 1));
        Throws(() => library.Import(second.Path, Settings));
    }

    private static void DiscoveryBounds(string directory)
    {
        var library = new VisionModelLibrary(Path.Combine(directory, "bounded-library"));
        Directory.CreateDirectory(library.DirectoryPath);
        for (int index = 0; index <= VisionModelLibrary.MaximumCandidates; index++)
            Directory.CreateDirectory(Path.Combine(library.DirectoryPath, index.ToString("x64")));
        var result = library.Discover(Settings);
        Require(result.Models.Count == 0 && result.Errors.Count <= VisionModelLibrary.MaximumCandidates + 1 &&
            result.Errors.Any(error => error.Contains("first", StringComparison.Ordinal)),
            "Discovery did not report and bound excessive malformed folders.");
    }

    private static void RepairInstalledPackages(string directory)
    {
        var package = Package(Path.Combine(directory, "repair-source"));
        var library = new VisionModelLibrary(Path.Combine(directory, "repair-library"));
        var entry = library.Import(package.Path, Settings);
        string installedDirectory = Path.GetDirectoryName(entry.ManifestPath)!;
        string installedModel = Path.Combine(installedDirectory, "weights", "model.onnx");
        byte[] originalManifest = File.ReadAllBytes(package.Path);
        byte[] originalModel = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(package.Path)!, "weights", "model.onnx"));

        File.WriteAllBytes(installedModel, [9, 9]);
        var repaired = library.Import(package.Path, Settings);
        string[] quarantines = Directory.GetDirectories(library.DirectoryPath, ".quarantine-*");
        Require(repaired.ManifestPath == entry.ManifestPath && repaired.Status.Contains("repaired") && quarantines.Length == 1 &&
            File.ReadAllBytes(installedModel).AsSpan().SequenceEqual(originalModel) &&
            File.ReadAllBytes(Path.Combine(quarantines[0], "weights", "model.onnx")).AsSpan().SequenceEqual(new byte[] { 9, 9 }),
            "Re-import did not repair a corrupted weight file while preserving its damaged copy.");
        if (OperatingSystem.IsWindows())
            Require((File.GetAttributes(quarantines[0]) & FileAttributes.Hidden) != 0, "A quarantined package is not hidden.");

        File.Delete(installedModel);
        library.Import(package.Path, Settings);
        Require(File.ReadAllBytes(installedModel).AsSpan().SequenceEqual(originalModel) &&
            Directory.GetDirectories(library.DirectoryPath, ".quarantine-*").Length == 2,
            "A missing installed model could not be repaired.");

        File.AppendAllText(entry.ManifestPath, " ");
        byte[] changedManifest = File.ReadAllBytes(entry.ManifestPath);
        library.Import(package.Path, Settings);
        Require(File.ReadAllBytes(entry.ManifestPath).AsSpan().SequenceEqual(originalManifest) &&
            Directory.GetDirectories(library.DirectoryPath, ".quarantine-*").Any(path =>
                File.ReadAllBytes(Path.Combine(path, "manifest.json")).AsSpan().SequenceEqual(changedManifest)),
            "Re-import did not repair and preserve a changed installed manifest.");

        // A bad replacement must not rename, overwrite or otherwise disturb the old installation.
        File.WriteAllBytes(installedModel, [7, 7]);
        string sourceModel = Path.Combine(Path.GetDirectoryName(package.Path)!, "weights", "model.onnx");
        File.WriteAllBytes(sourceModel, [8, 8]);
        int priorQuarantines = Directory.GetDirectories(library.DirectoryPath, ".quarantine-*").Length;
        Throws(() => library.Import(package.Path, Settings));
        Require(File.ReadAllBytes(installedModel).AsSpan().SequenceEqual(new byte[] { 7, 7 }) &&
            File.ReadAllBytes(sourceModel).AsSpan().SequenceEqual(new byte[] { 8, 8 }) &&
            Directory.GetDirectories(library.DirectoryPath, ".quarantine-*").Length == priorQuarantines &&
            !Directory.GetDirectories(library.DirectoryPath, ".import-*").Any(),
            "An invalid replacement changed the original package or left staging data behind.");
        File.WriteAllBytes(sourceModel, originalModel);
        library.Import(package.Path, Settings);
        int repairedQuarantines = Directory.GetDirectories(library.DirectoryPath, ".quarantine-*").Length;
        library.Import(package.Path, Settings);
        Require(Directory.GetDirectories(library.DirectoryPath, ".quarantine-*").Length == repairedQuarantines &&
            library.Discover(Settings).Models.Count == 1 && library.Discover(Settings).Errors.Count == 0,
            "Valid re-import was not idempotent or quarantines appeared as installed models.");
        Require(File.ReadAllBytes(package.Path).AsSpan().SequenceEqual(originalManifest), "Repair altered the original source manifest.");
    }

    private static void WriteManifest(string path, VisionModelManifest manifest) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, VisionModelManifest.Json));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or JsonException) { return; }
        throw new InvalidOperationException("An invalid model package was imported.");
    }
}
