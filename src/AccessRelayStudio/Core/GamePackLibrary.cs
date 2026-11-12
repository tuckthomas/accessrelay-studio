using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteScriptStudio.Core;

internal sealed record GamePackDefinition(string DirectoryPath, GamePackManifest Manifest, string ManifestSha256, byte[] ManifestBytes)
{
    internal static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };
    internal static GamePackDefinition Read(string manifestPath)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        if (Path.GetFileName(manifestPath) != "pack.json") throw new InvalidDataException("Select a file named pack.json.");
        RejectLinks(root, manifestPath);
        using var stream = File.OpenRead(manifestPath);
        if (stream.Length is <= 0 or > 128 * 1024) throw new InvalidDataException("Game-pack manifest exceeds 128 KiB or is empty.");
        byte[] bytes = new byte[stream.Length]; stream.ReadExactly(bytes);
        var manifest = JsonSerializer.Deserialize<GamePackManifest>(bytes, Json) ?? throw new InvalidDataException("Empty game-pack manifest.");
        manifest.Validate();
        var pack = new GamePackDefinition(root, manifest, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
        foreach (var asset in manifest.Assets)
        {
            using var file = pack.OpenResource(asset);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(file)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A game-pack resource SHA-256 does not match: " + asset.Path);
            if (asset.Kind == GamePackAssetKind.Lua) _ = new LuaControllerProgram(pack.Lua(asset, manifest.ResolveTuning(ImmutableDictionary<string, double>.Empty)));
        }
        var referencedModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets.Where(asset => asset.Kind == GamePackAssetKind.VisionManifest))
        {
            using var file = pack.OpenResource(asset); using var document = JsonDocument.Parse(file, new() { MaxDepth = 16 });
            var model = document.RootElement;
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("modelFile", out var modelFile) || modelFile.ValueKind != JsonValueKind.String
                || !model.TryGetProperty("modelSha256", out var modelHash) || modelHash.ValueKind != JsonValueKind.String
                || !model.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int schemaVersion) || schemaVersion != 1)
                throw new InvalidDataException("Malformed typed vision resource manifest.");
            string? filename = modelFile.GetString(), sha = modelHash.GetString();
            string modelPath = asset.Path[..(asset.Path.LastIndexOf('/') + 1)] + filename;
            if (filename is null || filename.Contains('/') || filename.Contains('\\')
                || !manifest.Assets.Any(resource => resource.Kind == GamePackAssetKind.Model && resource.Path == modelPath && string.Equals(resource.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                || (model.TryGetProperty("outputContract", out var contract) && (contract.ValueKind != JsonValueKind.String || contract.GetString() != "temporal-release-v1")))
                throw new InvalidDataException("A typed vision manifest must reference a declared ONNX resource and matching SHA-256.");
            referencedModels.Add(modelPath);
        }
        if (manifest.Assets.Any(asset => asset.Kind == GamePackAssetKind.Model && !referencedModels.Contains(asset.Path)))
            throw new InvalidDataException("Every ONNX resource needs a declared typed vision manifest.");
        return pack;
    }
    internal string ResourcePath(GamePackAsset asset) => Path.Combine(DirectoryPath, asset.Path.Replace('/', Path.DirectorySeparatorChar));
    private FileStream OpenResource(GamePackAsset asset)
    {
        string path = ResourcePath(asset); RejectLinks(DirectoryPath, path);
        var stream = File.OpenRead(path);
        if (stream.Length != asset.Bytes) { stream.Dispose(); throw new InvalidDataException("A game-pack resource length changed: " + asset.Path); }
        return stream;
    }
    internal static void RejectLinks(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root), fullPath = Path.GetFullPath(path);
        if (!Path.GetRelativePath(fullRoot, fullPath).Split(Path.DirectorySeparatorChar).All(segment => segment != ".."))
            throw new InvalidDataException("Game-pack resources must stay inside the pack folder.");
        string? current = fullPath;
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symbolic links/reparse points are unsupported in game packs.");
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidDataException("Resource is outside the pack root.");
    }
    private Guid ScriptId(string path) => new(SHA256.HashData(Encoding.UTF8.GetBytes(Manifest.Id + ":" + Manifest.Version + ":" + path)).AsSpan(0, 16));
    private LuaControllerScript Lua(GamePackAsset asset, ImmutableDictionary<string, double> tuning)
    {
        using var stream = OpenResource(asset); byte[] bytes = new byte[stream.Length]; stream.ReadExactly(bytes);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Lua resource changed after validation.");
        string name = Manifest.Name + " · " + Path.GetFileNameWithoutExtension(asset.Path);
        return new(ScriptId(asset.Path), name[..Math.Min(name.Length, 80)], new UTF8Encoding(false, true).GetString(bytes)) { Tuning = tuning };
    }
    internal StudioProfile CreateProfile(IReadOnlyDictionary<string, double> overrides)
    {
        var values = Manifest.ResolveTuning(overrides);
        var scripts = Manifest.Assets.Where(asset => asset.Kind == GamePackAssetKind.Lua).Select(asset => Lua(asset, values)).ToImmutableDictionary(script => script.Id);
        var profile = StudioProfile.CreateDefault(Manifest.Name) with
        {
            ScriptsEnabled = false, LuaScripts = scripts,
            GamePack = new(Manifest.Id, Manifest.Version, ManifestSha256, values),
            Bindings = Manifest.Bindings.ToImmutableDictionary(binding => binding.Control, binding => new ScriptBinding(ScriptKind.Lua, Consume: binding.Consume, LuaScriptId: ScriptId(binding.Script)))
        };
        profile.Validate(); return profile;
    }
    internal StudioProfile Retune(StudioProfile profile, IReadOnlyDictionary<string, double> overrides)
    {
        if (profile.GamePack is not { } reference || reference.Id != Manifest.Id || reference.Version != Manifest.Version || reference.ManifestSha256 != ManifestSha256)
            throw new InvalidDataException("The selected setup belongs to a different pack/version/hash.");
        var tuned = CreateProfile(overrides); var scripts = profile.LuaScripts;
        foreach (var (id, script) in tuned.LuaScripts)
            if (scripts.TryGetValue(id, out var existing))
            {
                if (existing.Source != script.Source) throw new InvalidOperationException("An imported Lua script was edited. Create a fresh setup to apply pack tuning.");
                scripts = scripts.SetItem(id, existing with { Tuning = script.Tuning });
            }
        return profile with { GamePack = tuned.GamePack, LuaScripts = scripts };
    }
    internal void CopyTo(string destination, CancellationToken cancellation)
    {
        foreach (var asset in Manifest.Assets)
        {
            cancellation.ThrowIfCancellationRequested();
            using var source = OpenResource(asset);
            string path = Path.Combine(destination, asset.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[64 * 1024]; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0; int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested(); total += count;
                    if (total > asset.Bytes) throw new InvalidDataException("Game-pack resource grew during import.");
                    hash.AppendData(buffer, 0, count); target.Write(buffer, 0, count);
                }
                if (total != asset.Bytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Game-pack resource changed during import.");
            }
        }
        cancellation.ThrowIfCancellationRequested(); File.WriteAllBytes(Path.Combine(destination, "pack.json"), ManifestBytes);
    }
}
internal sealed class GamePackLibrary(string directory, string? bundledDirectory = null)
{
    internal IReadOnlyList<GamePackDefinition> LoadAll(out string[] errors)
    {
        var result = new List<GamePackDefinition>(); var issues = new List<string>();
        foreach (string root in new[] { bundledDirectory, directory }.OfType<string>().Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (string folder in Directory.EnumerateDirectories(root).Where(path => !Path.GetFileName(path).StartsWith('.')).Take(129))
            {
                string manifest = Path.Combine(folder, "pack.json");
                if (result.Count + issues.Count >= 128) { issues.Add("Game-pack library scan limit reached (128)."); break; }
                try
                {
                    var pack = GamePackDefinition.Read(manifest);
                    if (result.Any(previous => previous.Manifest.Id == pack.Manifest.Id && previous.Manifest.Version == pack.Manifest.Version))
                        throw new InvalidDataException("Duplicate pack/version; the first library entry was retained.");
                    result.Add(pack);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException) { issues.Add(Path.GetFileName(Path.GetDirectoryName(manifest)) + ": " + error.Message); }
            }
        }
        errors = issues.ToArray(); return result;
    }
    internal GamePackDefinition Import(string manifestPath, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); var pack = GamePackDefinition.Read(manifestPath);
        Directory.CreateDirectory(directory); GamePackDefinition.RejectLinks(directory, directory);
        string destination = Path.Combine(directory, pack.Manifest.Id + "@" + pack.Manifest.Version);
        if (Directory.Exists(destination))
        {
            var existing = GamePackDefinition.Read(Path.Combine(destination, "pack.json"));
            if (existing.ManifestSha256 != pack.ManifestSha256) throw new InvalidDataException("This pack/version is already installed with another hash. Use a new version.");
            return existing;
        }
        string staging = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            pack.CopyTo(staging, cancellation); var checkedCopy = GamePackDefinition.Read(Path.Combine(staging, "pack.json"));
            cancellation.ThrowIfCancellationRequested(); Directory.Move(staging, destination);
            return checkedCopy with { DirectoryPath = destination };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
