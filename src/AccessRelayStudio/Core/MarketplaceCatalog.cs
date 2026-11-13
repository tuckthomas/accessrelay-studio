using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace EliteScriptStudio.Core;

internal sealed record MarketplaceEntry(string Id, string Name, string Description, string Author, string Version,
    string Kind, string License, string ManifestPath, string ManifestSha256, string[] Tags);
internal sealed record MarketplaceCatalog(int SchemaVersion, MarketplaceEntry[] Entries)
{
    internal void Validate()
    {
        if (SchemaVersion != 1 || Entries is null || Entries.Length > 500 || Entries.Any(entry => entry is null)
            || Entries.Select(entry => entry.Id + "@" + entry.Version).Distinct().Count() != Entries.Length)
            throw new InvalidDataException("Unsupported or invalid marketplace catalog.");
        foreach (var entry in Entries)
            if (!GamePackManifest.Identifier(entry.Id, 60) || !GamePackManifest.Identifier(entry.Version, 40)
                || entry.Kind is not ("script" or "plugin") || !Text(entry.Name, 80) || !Text(entry.Description, 1000)
                || !Text(entry.Author, 80) || !Text(entry.License, 300) || !GamePackManifest.Hash(entry.ManifestSha256)
                || entry.ManifestPath != $"packages/{entry.Id}/{entry.Version}/pack.json"
                || entry.Tags is null || entry.Tags.Length > 10 || entry.Tags.Any(tag => !Text(tag, 40)))
                throw new InvalidDataException("A marketplace listing has invalid metadata or a package path.");
    }
    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
}

internal sealed class MarketplaceClient(HttpClient http)
{
    internal const string RepositoryUrl = "https://github.com/tuckthomas/accessrelay-marketplace";
    internal const string ContentUrl = "https://raw.githubusercontent.com/tuckthomas/accessrelay-marketplace/main/";
    internal async Task<MarketplaceCatalog> ReadAsync(CancellationToken cancellation)
    {
        byte[] bytes = await DownloadAsync("catalog.json", 1024 * 1024, cancellation).ConfigureAwait(false);
        var catalog = JsonSerializer.Deserialize<MarketplaceCatalog>(bytes, GamePackDefinition.Json)
            ?? throw new InvalidDataException("The marketplace catalog is empty.");
        catalog.Validate(); return catalog;
    }
    internal async Task<string> DownloadPackAsync(MarketplaceEntry entry, string staging, CancellationToken cancellation)
    {
        new MarketplaceCatalog(1, [entry]).Validate();
        Directory.CreateDirectory(staging); GamePackDefinition.RejectLinks(staging, staging);
        byte[] bytes = await DownloadAsync(entry.ManifestPath, 128 * 1024, cancellation).ConfigureAwait(false);
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(entry.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The package manifest changed. Refresh the marketplace before installing.");
        var manifest = JsonSerializer.Deserialize<GamePackManifest>(bytes, GamePackDefinition.Json)
            ?? throw new InvalidDataException("The package manifest is empty.");
        manifest.Validate();
        if (manifest.Id != entry.Id || manifest.Version != entry.Version || manifest.Name != entry.Name || manifest.License != entry.License)
            throw new InvalidDataException("The package does not match its listing.");
        string prefix = entry.ManifestPath[..^"pack.json".Length];
        foreach (var asset in manifest.Assets)
        {
            byte[] content = await DownloadAsync(prefix + asset.Path, checked((int)asset.Bytes), cancellation).ConfigureAwait(false);
            if (content.LongLength != asset.Bytes || !Convert.ToHexStringLower(SHA256.HashData(content)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A downloaded package resource does not match its size or SHA-256.");
            string destination = Path.Combine(staging, asset.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            GamePackDefinition.RejectLinks(staging, Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, content, cancellation).ConfigureAwait(false);
        }
        string path = Path.Combine(staging, "pack.json");
        await File.WriteAllBytesAsync(path, bytes, cancellation).ConfigureAwait(false);
        _ = GamePackDefinition.Read(path); // Includes sandbox compilation and declared-resource checks.
        return path;
    }
    private async Task<byte[]> DownloadAsync(string path, int limit, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(new Uri(ContentUrl + path), HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The marketplace download exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024]; int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("The marketplace download exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
