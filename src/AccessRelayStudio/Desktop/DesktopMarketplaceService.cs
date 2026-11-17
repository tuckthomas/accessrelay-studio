using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Desktop;

internal sealed class DesktopMarketplaceService(DesktopControllerService controller, InstalledGamePlugins visionPlugins, Action? requirePluginIdle = null) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _covers = new(StringComparer.OrdinalIgnoreCase);
    private MarketplaceCatalog? _catalog;
    private bool _busy;
    internal object GetState() => new { repositoryUrl = MarketplaceClient.RepositoryUrl, loaded = _catalog is not null,
        entries = (_catalog?.Entries ?? []).Select(entry =>
        {
            bool installed = controller.HasMarketplacePack(entry.Id, entry.Version, entry.ManifestSha256);
            string? coverKey = !string.IsNullOrEmpty(entry.CoverSha256) && _covers.ContainsKey(entry.CoverSha256)
                ? entry.CoverSha256
                : installed && visionPlugins.HasCover(entry.Id, entry.Version) ? $"{entry.Id}/{entry.Version}" : null;
            return new { entry.Id, entry.Name, entry.Description, entry.Author, entry.Version,
                entry.Kind, entry.GameId, entry.License, entry.Tags, entry.NativeCode, installed,
                coverUrl = coverKey is null ? null : "https://media.accessrelay.test/marketplace/cover/" + coverKey,
                trainingPluginId = entry.Kind == "training-plugin" && installed
                    ? visionPlugins.Plugins.FirstOrDefault(plugin => plugin.Id == entry.GameId)?.Id : null };
        }) };
    internal DesktopMedia? ReadCover(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        if (_covers.TryGetValue(relative, out var cached)) return new(cached.Bytes, cached.ContentType);
        string[] parts = relative.Split('/', 2);
        if (parts.Length == 2 && visionPlugins.ReadCoverByPackage(parts[0], parts[1]) is { } installed)
            return new(installed.Bytes, installed.ContentType);
        return null;
    }
    internal async Task<object?> InvokeAsync(string action, JsonElement args)
    {
        if (action == "sourceOpen")
        {
            string id = args.Text("id"), requestedVersion = args.Text("version");
            var entry = _catalog?.Entries.FirstOrDefault(item => item.Id == id && (requestedVersion.Length == 0 || item.Version == requestedVersion));
            var url = entry is null ? MarketplaceClient.RepositoryUrl : MarketplaceClient.RepositoryUrl + "/tree/main/" + entry.ManifestPath[..^"pack.json".Length];
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); return null;
        }
        if (_busy) throw new InvalidOperationException("A marketplace operation is already running.");
        _busy = true;
        try
        {
            var client = new MarketplaceClient(_http);
            if (action == "refresh") { _catalog = await client.ReadAsync(_stop.Token); await CacheCoversAsync(client, _catalog); return GetState(); }
            if (action != "install") throw new InvalidDataException("Unknown marketplace action.");
            string id = args.Text("id"), version = args.Text("version");
            var entry = _catalog?.Entries.FirstOrDefault(item => item.Id == id && item.Version == version)
                ?? throw new InvalidOperationException("Refresh the marketplace and choose a listed package.");
            if (entry.NativeCode) requirePluginIdle?.Invoke();
            string cache = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "MarketplaceCache"));
            string staging = Path.GetFullPath(Path.Combine(cache, Guid.NewGuid().ToString("N")));
            try
            {
                var path = await client.DownloadPackAsync(entry, staging, _stop.Token);
                if (entry.NativeCode) requirePluginIdle?.Invoke();
                var pack = GamePackDefinition.Read(path);
                if (entry.Kind == "training-plugin" && !InstalledGamePlugins.Supports(pack.Manifest))
                    throw new InvalidOperationException("This package has no compatible game implementation. Choose an updated implementation plugin.");
                await controller.ImportMarketplacePackAsync(path, _stop.Token);
                visionPlugins.Reload();
                if (visionPlugins.Errors.Length != 0) throw new InvalidOperationException(string.Join("\n", visionPlugins.Errors));
                return GetState();
            }
            finally
            {
                if (staging.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(staging))
                {
                    GamePackDefinition.RejectLinks(cache, staging);
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
        finally { _busy = false; }
    }
    public void Dispose() { _stop.Cancel(); _http.Dispose(); }
    private async Task CacheCoversAsync(MarketplaceClient client, MarketplaceCatalog catalog)
    {
        string coverDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "MarketplaceCache", "covers"));
        foreach (var entry in catalog.Entries)
        {
            if (string.IsNullOrEmpty(entry.CoverPath) || string.IsNullOrEmpty(entry.CoverSha256)) continue;
            if (_covers.ContainsKey(entry.CoverSha256)) continue;
            try
            {
                string extension = Path.GetExtension(entry.CoverPath).ToLowerInvariant();
                string diskPath = Path.Combine(coverDirectory, entry.CoverSha256 + extension);
                byte[]? bytes = null;
                if (File.Exists(diskPath))
                {
                    byte[] existing = await File.ReadAllBytesAsync(diskPath, _stop.Token);
                    if (Convert.ToHexStringLower(SHA256.HashData(existing)).Equals(entry.CoverSha256, StringComparison.OrdinalIgnoreCase))
                        bytes = existing;
                }
                if (bytes is null)
                {
                    bytes = await client.DownloadCoverAsync(entry, _stop.Token);
                    Directory.CreateDirectory(coverDirectory);
                    await File.WriteAllBytesAsync(diskPath, bytes, _stop.Token);
                }
                _covers[entry.CoverSha256] = (bytes, InstalledGamePlugins.ContentTypeForImage(entry.CoverPath));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* Cover art is optional visual enhancement; never block catalog loading. */ }
        }
    }
}
