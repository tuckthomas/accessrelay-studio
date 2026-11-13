using System.Collections.Immutable;
using System.Net;
using System.Text;
using EliteScriptStudio.Core;

internal static class MarketplaceVerification
{
    internal static async Task RunAsync(string output)
    {
        string root = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var client = new MarketplaceClient(http);
        var catalog = await client.ReadAsync(CancellationToken.None);
        if (catalog.Entries.Length == 0) throw new Exception("The live marketplace has no listings.");
        var library = new GamePackLibrary(Path.Combine(root, "installed"));
        foreach (var entry in catalog.Entries)
        {
            string path = await client.DownloadPackAsync(entry, Path.Combine(root, "download", entry.Id), CancellationToken.None);
            var installed = library.Import(path, CancellationToken.None);
            var profile = installed.CreateProfile(ImmutableDictionary<string, double>.Empty);
            if (profile.ScriptsEnabled || profile.GamePack?.Id != entry.Id || installed.ManifestSha256 != entry.ManifestSha256)
                throw new Exception("Marketplace installation changed activation state or identity.");
        }
        try
        {
            await client.DownloadPackAsync(catalog.Entries[0] with { ManifestSha256 = new string('0', 64) }, Path.Combine(root, "bad-hash"), CancellationToken.None);
            throw new Exception("Changed package manifest was accepted.");
        }
        catch (InvalidDataException) { }
        try { new MarketplaceCatalog(1, [catalog.Entries[0] with { ManifestPath = "../pack.json" }]).Validate(); throw new Exception("Traversal accepted."); }
        catch (InvalidDataException) { }
        using var oversizedHttp = new HttpClient(new OversizedResponse());
        try { await new MarketplaceClient(oversizedHttp).ReadAsync(CancellationToken.None); throw new Exception("Oversized catalog accepted."); }
        catch (InvalidDataException) { }
        var reloaded = library.LoadAll(out var errors);
        if (errors.Length != 0 || reloaded.Count != catalog.Entries.Length) throw new Exception("Installed packages did not reload cleanly.");
        Console.WriteLine($"SUCCESS: live catalog, {reloaded.Count} real package downloads/imports, sandbox validation, inactive profiles, hashes and download/path limits. Evidence: {root}");
    }
    private sealed class OversizedResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1), Encoding.UTF8) });
    }
}
