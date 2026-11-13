using System.Net.Http;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Desktop;

internal sealed class DesktopMarketplaceService(DesktopControllerService controller, IReadOnlyList<IVisionGamePlugin> visionPlugins) : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource _stop = new();
    private MarketplaceCatalog? _catalog;
    private bool _busy;
    internal object GetState() => new { repositoryUrl = MarketplaceClient.RepositoryUrl, loaded = _catalog is not null,
        entries = (_catalog?.Entries ?? []).Select(entry => new { entry.Id, entry.Name, entry.Description, entry.Author, entry.Version,
            entry.Kind, entry.License, entry.Tags, installed = controller.HasMarketplacePack(entry.Id, entry.Version, entry.ManifestSha256),
            trainingPluginId = visionPlugins.FirstOrDefault(plugin => plugin.Id == entry.Id)?.Id }) };
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
            if (action == "refresh") { _catalog = await client.ReadAsync(_stop.Token); return GetState(); }
            if (action != "install") throw new InvalidDataException("Unknown marketplace action.");
            string id = args.Text("id"), version = args.Text("version");
            var entry = _catalog?.Entries.FirstOrDefault(item => item.Id == id && item.Version == version)
                ?? throw new InvalidOperationException("Refresh the marketplace and choose a listed package.");
            string cache = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "MarketplaceCache"));
            string staging = Path.GetFullPath(Path.Combine(cache, Guid.NewGuid().ToString("N")));
            try
            {
                var path = await client.DownloadPackAsync(entry, staging, _stop.Token);
                await controller.ImportMarketplacePackAsync(path, _stop.Token);
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
}
