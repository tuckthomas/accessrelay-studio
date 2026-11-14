using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace EliteScriptStudio.Core;

/// <summary>Loads checked, installed extensions. Native/.NET plugins are trusted code, not a sandbox.</summary>
internal sealed class InstalledGamePlugins(string directory, Func<string, string?> dataDirectory)
{
    private sealed record Loaded(GamePackDefinition Pack, IVisionGamePlugin Vision);
    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private Loaded[] _active = [];
    internal IReadOnlyList<IVisionGamePlugin> Plugins => _active.Select(item => item.Vision).ToArray();
    internal IReadOnlyList<IGameControllerPlugin> ControllerAdapters => _active.Select(item => item.Vision).OfType<IGameControllerPlugin>().ToArray();
    internal string[] Errors { get; private set; } = [];
    internal object Presentations => _active.Select(item => new {
        id = item.Vision.Id, packageId = item.Pack.Manifest.Id, version = item.Pack.Manifest.Version,
        uiModule = BaseUrl(item.Pack) + item.Pack.Manifest.Runtime!.UiEntry
    }).ToArray();
    internal static bool Supports(GamePackManifest manifest) => manifest is { SchemaVersion: 2, ApiVersion: 2, Runtime.ContractVersion: 1 };
    private static string BaseUrl(GamePackDefinition pack) => "https://plugins.accessrelay.test/plugins/" + pack.Manifest.Id + "/" + pack.ManifestSha256 + "/";

    internal void Reload()
    {
        var packs = new GamePackLibrary(directory).LoadAll(out var issues);
        var errors = issues.ToList(); var active = new List<Loaded>();
        foreach (var group in packs.Where(pack => Supports(pack.Manifest)).GroupBy(pack => pack.Manifest.GameId))
        {
            // New implementations supersede older enabling manifests without deleting user data.
            var pack = group.OrderByDescending(p => Version.TryParse(p.Manifest.Version, out var v) ? v : new Version()).First();
            try
            {
                if (!_loaded.TryGetValue(pack.ManifestSha256, out var loaded))
                {
                    var runtime = pack.Manifest.Runtime!;
                    var context = new PluginLoadContext(pack);
                    var assembly = context.LoadImplementation(runtime.Assembly);
                    var factoryType = assembly.GetType(runtime.Factory, throwOnError: true)!;
                    if (Activator.CreateInstance(factoryType) is not IGamePluginFactory factory)
                        throw new InvalidDataException("The entry point does not implement the shared plugin contract.");
                    var plugin = factory.Create(new(pack.DirectoryPath, dataDirectory(Path.Combine("Plugins", pack.Manifest.GameId, "Profiles"))));
                    if (plugin.Id != pack.Manifest.GameId || plugin.OutputContract != pack.Manifest.VisionAdapter
                        || plugin.TrainingWorkflowFile != Path.GetFileName(runtime.TrainingEntry))
                        throw new InvalidDataException("The loaded implementation does not match its package declaration.");
                    loaded = new(pack, plugin); _loaded.Add(pack.ManifestSha256, loaded);
                }
                active.Add(loaded);
            }
            catch (Exception error) { errors.Add(pack.Manifest.Name + ": " + error.GetBaseException().Message); }
        }
        _active = active.ToArray(); Errors = errors.ToArray();
    }

    internal string WorkflowPath(string gameId, string role = "train")
    {
        var item = Require(gameId); var runtime = item.Pack.Manifest.Runtime!;
        string path = role switch { "train" => runtime.TrainingEntry, "audit" => runtime.AuditEntry, "experiments" => runtime.ExperimentsEntry,
            _ => throw new InvalidOperationException("Unsupported plugin workflow role.") };
        if (path.Length == 0) throw new InvalidOperationException("The selected plugin does not provide this workflow.");
        // Revalidate the Python files before each explicit job, not on the frame-processing path.
        foreach (var asset in item.Pack.Manifest.Assets.Where(a => a.Kind == GamePackAssetKind.Workflow)) _ = ReadChecked(item.Pack, asset);
        return item.Pack.ResourcePath(item.Pack.Manifest.Assets.Single(a => a.Path == path));
    }
    internal string ModelDirectory(string gameId) => Path.Combine(Require(gameId).Pack.DirectoryPath, "capture-models");
    private Loaded Require(string gameId) => _active.FirstOrDefault(item => item.Vision.Id == gameId)
        ?? throw new InvalidOperationException("The game implementation is not installed.");

    internal byte[]? ReadUi(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Host != "plugins.accessrelay.test" || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var item = _active.FirstOrDefault(item => uri.AbsoluteUri.StartsWith(BaseUrl(item.Pack), StringComparison.Ordinal));
        if (item is null) return null;
        string path = uri.AbsoluteUri[BaseUrl(item.Pack).Length..];
        var asset = item.Pack.Manifest.Assets.FirstOrDefault(a => a.Kind == GamePackAssetKind.Ui && a.Path == path);
        return asset is null ? null : ReadChecked(item.Pack, asset);
    }
    private static byte[] ReadChecked(GamePackDefinition pack, GamePackAsset asset)
    {
        string path = pack.ResourcePath(asset); GamePackDefinition.RejectLinks(pack.DirectoryPath, path);
        using var file = File.OpenRead(path);
        if (file.Length != asset.Bytes) throw new InvalidDataException("Plugin resource length changed: " + asset.Path);
        byte[] data = new byte[file.Length]; file.ReadExactly(data);
        if (!Convert.ToHexStringLower(SHA256.HashData(data)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin resource hash changed: " + asset.Path);
        return data;
    }
    private sealed class PluginLoadContext(GamePackDefinition pack) : AssemblyLoadContext(isCollectible: false)
    {
        internal Assembly LoadImplementation(string path) => LoadFromStream(new MemoryStream(ReadChecked(pack,
            pack.Manifest.Assets.Single(a => a.Kind == GamePackAssetKind.Runtime && a.Path == path))));
        protected override Assembly? Load(AssemblyName name)
        {
            var contract = typeof(IGamePluginFactory).Assembly;
            if (name.Name == contract.GetName().Name) return contract;
            var asset = pack.Manifest.Assets.FirstOrDefault(a => a.Kind == GamePackAssetKind.Runtime
                && Path.GetFileNameWithoutExtension(a.Path).Equals(name.Name, StringComparison.OrdinalIgnoreCase));
            return asset is null ? null : LoadImplementation(asset.Path);
        }
    }
}
