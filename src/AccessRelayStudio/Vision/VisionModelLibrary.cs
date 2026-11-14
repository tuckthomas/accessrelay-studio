using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal sealed record VisionModelLibraryEntry(string ManifestPath, string DisplayName, string ModelVersion,
    string AnimationProfile, string CameraConfiguration, bool IsCompatible, bool EvidenceAccepted, string Status);
internal sealed record VisionModelLibrarySnapshot(IReadOnlyList<VisionModelLibraryEntry> Models, IReadOnlyList<string> Errors);

/// <summary>Explicitly imported, self-contained packages. Discovery never loads or executes an ONNX model.</summary>
internal sealed class VisionModelLibrary
{
    internal const int MaximumModels = 32, MaximumCandidates = 128;
    internal const long MaximumModelBytes = 1_073_741_824;
    private const int MaximumJsonBytes = 256 * 1024;
    private readonly object _gate = new();
    internal string DirectoryPath { get; }

    internal VisionModelLibrary(string? directory = null) => DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessRelayStudio", "Vision", "Models"));

    // Hashing potentially large weights belongs on a worker, at import/manual refresh, never a UI status poll.
    internal VisionModelLibrarySnapshot Discover(VisionSettings settings)
    {
        settings.Validate();
        lock (_gate)
        {
            var entries = new List<VisionModelLibraryEntry>();
            var errors = new List<string>();
            if (!Directory.Exists(DirectoryPath)) return new(entries, errors);
            try
            {
                EnsureRegularPath(DirectoryPath, DirectoryPath);
                var directories = Directory.EnumerateDirectories(DirectoryPath).Take(MaximumCandidates + 1).ToArray();
                if (directories.Length > MaximumCandidates)
                    errors.Add($"Only the first {MaximumCandidates} model-library folders were inspected. Remove unrecognized folders before importing more models.");
                foreach (string directory in directories.Take(MaximumCandidates))
                {
                    string id = Path.GetFileName(directory);
                    if (!VisionModelManifest.IsSha256(id)) continue; // Includes incomplete import staging folders.
                    if (entries.Count == MaximumModels)
                    { errors.Add($"The model library supports up to {MaximumModels} installed models."); break; }
                    try { entries.Add(ReadInstalled(directory, settings)); }
                    catch (Exception error) when (IsPackageError(error))
                    { errors.Add($"Model {id[..12]} was skipped: {error.Message}"); }
                }
            }
            catch (Exception error) when (IsPackageError(error))
            { errors.Add("The model library could not be read: " + error.Message); }
            return new(entries.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ModelVersion, StringComparer.OrdinalIgnoreCase).ToArray(), errors.ToArray());
        }
    }

    internal VisionModelLibraryEntry Import(string manifestPath, VisionSettings settings, CancellationToken cancellation = default)
    {
        settings.Validate();
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            string sourceManifest = Path.GetFullPath(manifestPath);
            string sourceRoot = Path.GetDirectoryName(sourceManifest)!;
            EnsureRegularPath(sourceRoot, sourceManifest);
            byte[] manifestBytes = VisionModelManifest.ReadBounded(sourceManifest, MaximumJsonBytes);
            var manifest = ParseManifest(manifestBytes);
            string id = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            string destination = Path.Combine(DirectoryPath, id);
            var files = References(sourceManifest, manifest);
            Directory.CreateDirectory(DirectoryPath);
            EnsureRegularPath(DirectoryPath, DirectoryPath);
            bool replacing = Directory.Exists(destination);
            if (replacing)
            {
                try { return ReadInstalled(destination, settings); }
                catch (Exception error) when (IsPackageError(error)) { /* Verify a fresh copy before changing the damaged installation. */ }
            }
            var existing = Directory.EnumerateDirectories(DirectoryPath).Take(MaximumCandidates + 1).ToArray();
            if (existing.Length > MaximumCandidates || existing.Count(path => VisionModelManifest.IsSha256(Path.GetFileName(path))) - (replacing ? 1 : 0) >= MaximumModels)
                throw new InvalidDataException($"The model library is full or contains too many folders. It supports {MaximumModels} installed models.");

            long required = manifestBytes.Length;
            foreach (var file in files)
            {
                EnsureRegularPath(sourceRoot, file.Source);
                long length = new FileInfo(file.Source).Length;
                if (length <= 0 || length > file.MaximumBytes)
                    throw new InvalidDataException($"Package file '{file.Relative}' is empty or exceeds its size limit.");
                required = checked(required + length);
            }
            if (new DriveInfo(Path.GetPathRoot(DirectoryPath)!).AvailableFreeSpace < required + 64L * 1024 * 1024)
                throw new IOException("The selected model package needs more free space in the local model library.");

            string staging = Path.Combine(DirectoryPath, ".import-" + Guid.NewGuid().ToString("N"));
            try
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(staging);
                File.WriteAllBytes(Path.Combine(staging, "manifest.json"), manifestBytes);
                foreach (var file in files)
                {
                    string target = Path.Combine(staging, file.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyVerified(sourceRoot, file, target, cancellation);
                }
                // Parse copied evidence as well as checking hashes, before publishing the completed directory.
                var result = ReadInstalled(staging, settings, expectedId: id);
                cancellation.ThrowIfCancellationRequested();
                return PublishVerified(staging, destination, result, settings);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    string relative = Path.GetRelativePath(DirectoryPath, Path.GetFullPath(staging));
                    if (relative != Path.GetFileName(staging) || !relative.StartsWith(".import-", StringComparison.Ordinal))
                        throw new InvalidOperationException("Refusing cleanup outside the owned model-import staging directory.");
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
    }

    private VisionModelLibraryEntry PublishVerified(string staging, string destination, VisionModelLibraryEntry result, VisionSettings settings)
    {
        EnsureLibrarySibling(staging);
        EnsureLibrarySibling(destination);
        EnsureRegularPath(DirectoryPath, staging);
        if (!Directory.Exists(destination))
        {
            Directory.Move(staging, destination);
            return result with { ManifestPath = Path.Combine(destination, "manifest.json") };
        }
        // Another import may have repaired the installation while this copy was being prepared.
        try { return ReadInstalled(destination, settings); }
        catch (Exception error) when (IsPackageError(error)) { }
        EnsureRegularPath(DirectoryPath, destination); // Never move a reparse-point library root/package.
        string quarantine = Path.Combine(DirectoryPath, ".quarantine-" + Path.GetFileName(destination)[..12] + "-" + Guid.NewGuid().ToString("N"));
        EnsureLibrarySibling(quarantine);
        FileAttributes previousAttributes = File.GetAttributes(destination);
        Directory.Move(destination, quarantine);
        try
        {
            File.SetAttributes(quarantine, previousAttributes | FileAttributes.Hidden);
            Directory.Move(staging, destination);
        }
        catch (Exception publishError)
        {
            try
            {
                EnsureLibrarySibling(destination);
                EnsureLibrarySibling(quarantine);
                EnsureRegularPath(DirectoryPath, quarantine);
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new IOException("Another file or package now occupies the original installation path.");
                Directory.Move(quarantine, destination);
                File.SetAttributes(destination, previousAttributes);
            }
            catch (Exception rollbackError)
            {
                throw new IOException($"The replacement could not be published or restored automatically. Original files were retained; inspect '{destination}' and '{quarantine}'.",
                    new AggregateException(publishError, rollbackError));
            }
            throw new IOException("The replacement could not be published. The previous package was restored unchanged.", publishError);
        }
        return result with
        {
            ManifestPath = Path.Combine(destination, "manifest.json"),
            Status = result.Status + $" The damaged installation was repaired; its previous files are preserved in '{Path.GetFileName(quarantine)}'."
        };
    }

    private void EnsureLibrarySibling(string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(DirectoryPath, full);
        if (Path.IsPathRooted(relative) || relative != Path.GetFileName(full) || relative is "." or "..")
            throw new InvalidOperationException("A model-library move must stay directly inside its library directory.");
        EnsureRegularPath(DirectoryPath, DirectoryPath);
    }

    private VisionModelLibraryEntry ReadInstalled(string directory, VisionSettings settings, string? expectedId = null)
    {
        string path = Path.Combine(directory, "manifest.json");
        EnsureRegularPath(DirectoryPath, directory);
        EnsureRegularPath(directory, path);
        byte[] bytes = VisionModelManifest.ReadBounded(path, MaximumJsonBytes);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(hash, expectedId ?? Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed manifest no longer matches its package identity. Import the package again.");
        var manifest = ParseManifest(bytes);
        foreach (var file in References(path, manifest))
        {
            EnsureRegularPath(directory, file.Source);
            VerifyHash(file, default);
            if (file.MaximumBytes == MaximumJsonBytes)
                _ = JsonSerializer.Deserialize<VisionValidationReport>(VisionModelManifest.ReadBounded(file.Source, MaximumJsonBytes), VisionModelManifest.Json)
                    ?? throw new InvalidDataException("The model validation report is empty.");
        }
        bool profileMatches = settings.AnimationProfile.Trim() == manifest.AnimationProfile;
        bool cameraMatches = settings.CameraView.Length == 0 || settings.CameraView == manifest.CameraConfiguration;
        string regionPolicy = settings.TrainingCapture.EvidenceMode switch { VisionEvidenceMode.Automatic => "identity-pose-v1", VisionEvidenceMode.Anchored => "visual-anchors-v1", _ => "fixed-region-v1" };
        bool regionMatches = manifest.InputRegionPolicy == regionPolicy;
        string blocker = !profileMatches ? "This model belongs to a different player/shot profile."
            : !cameraMatches ? "This model belongs to a different camera view."
            : !regionMatches ? "Configure capture using this model's fixed or tracked input-region policy."
            : manifest.CheckArmingEvidence(path, settings);
        bool accepted = blocker.Length == 0;
        return new(path, manifest.DisplayName, manifest.ModelVersion, manifest.AnimationProfile, manifest.CameraConfiguration,
            profileMatches && cameraMatches && regionMatches, accepted,
            accepted ? "Validation evidence matches these settings. Runtime compatibility, source and calibration are still required." : blocker);
    }

    private static VisionModelManifest ParseManifest(byte[] bytes)
    {
        var result = JsonSerializer.Deserialize<VisionModelManifest>(bytes, VisionModelManifest.Json)
            ?? throw new InvalidDataException("The model manifest is empty.");
        result.Validate();
        foreach (string value in new[] { result.DisplayName, result.ModelVersion, result.AnimationProfile, result.CameraConfiguration })
            if (value.Length > 120 || value.Any(char.IsControl))
                throw new InvalidDataException("Model names and configuration labels must be readable and at most 120 characters.");
        result.Validation?.Validate();
        return result;
    }

    private sealed record PackageFile(string Relative, string Source, long MaximumBytes, string Hash);

    private static PackageFile[] References(string manifestPath, VisionModelManifest manifest)
    {
        var model = Reference(manifestPath, manifest.ModelFile, ".onnx", MaximumModelBytes, manifest.ModelSha256);
        if (manifest.Validation is not { } evidence) return [model];
        var report = Reference(manifestPath, evidence.ReportFile, ".json", MaximumJsonBytes, evidence.ReportSha256);
        if (string.Equals(model.Source, report.Source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model and evidence must be separate package files.");
        return [model, report];
    }

    private static PackageFile Reference(string manifestPath, string relative, string extension, long maximum, string hash)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 ||
            relative.Any(character => char.IsControl(character) || "<>\":|?*".Contains(character)))
            throw new InvalidDataException("Model package paths must be bounded, ordinary relative file paths.");
        string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || segments.Length > 8 || segments.Any(segment =>
            segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') || IsReservedName(segment)))
            throw new InvalidDataException("Model package paths cannot escape the package or use reserved names.");
        if (!string.Equals(Path.GetExtension(normalized), extension, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "manifest.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model must be .onnx and evidence must be a separate .json file.");
        string full = VisionModelManifest.ResolveChildFile(manifestPath, normalized);
        if (string.Equals(full, Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The manifest cannot also be a model or evidence file.");
        return new(normalized, full, maximum, hash);
    }

    private static bool IsReservedName(string segment)
    {
        string name = segment.Split('.')[0].TrimEnd(' ');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                (name[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }

    private static void EnsureRegularPath(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A model package file escaped its package directory.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Model packages cannot use symbolic links or directory junctions.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), fullRoot, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new InvalidDataException("The model package path has no valid parent directory.");
    }

    private static void VerifyHash(PackageFile file, CancellationToken cancellation) => Transfer(file, null, cancellation);
    private static void CopyVerified(string sourceRoot, PackageFile file, string target, CancellationToken cancellation)
    { EnsureRegularPath(sourceRoot, file.Source); Transfer(file, target, cancellation); }

    private static void Transfer(PackageFile file, string? target, CancellationToken cancellation)
    {
        using var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length <= 0 || source.Length > file.MaximumBytes)
            throw new InvalidDataException($"Package file '{file.Relative}' is empty or exceeds its size limit.");
        using var destination = target is null ? null : new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            for (int count; (count = source.Read(buffer, 0, buffer.Length)) != 0;)
            {
                cancellation.ThrowIfCancellationRequested();
                total = checked(total + count);
                if (total > file.MaximumBytes) throw new InvalidDataException("A model package file grew beyond its size limit.");
                hash.AppendData(buffer, 0, count);
                destination?.Write(buffer, 0, count);
            }
            if (total != source.Length || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package file '{file.Relative}' does not match its SHA-256 hash.");
            destination?.Flush(flushToDisk: true);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool IsPackageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
