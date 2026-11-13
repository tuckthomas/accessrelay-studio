using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;

namespace EliteScriptStudio.Packs;

/// <summary>Installed manifests enable reviewed adapters; they never authorize loading downloaded code.</summary>
internal sealed class InstalledVisionPlugins(string directory, Func<string, string?> dataDirectory)
{
    private readonly Dictionary<string, IVisionGamePlugin> _instances = new(StringComparer.Ordinal);
    internal IReadOnlyList<IVisionGamePlugin> Plugins { get; private set; } = [];
    internal static bool Supports(GamePackManifest manifest) => manifest.GameId == "nba2k27"
        && manifest.TrainingAdapter == "nba2k27-training-v1" && manifest.VisionAdapter == "temporal-release-v1";

    internal void Reload()
    {
        var packs = new GamePackLibrary(directory).LoadAll(out _);
        var plugins = new List<IVisionGamePlugin>();
        foreach (var pack in packs.Where(pack => Supports(pack.Manifest)))
        {
            string id = pack.Manifest.GameId;
            if (!_instances.TryGetValue(id, out var plugin))
                _instances[id] = plugin = new Nba2K27VisionPlugin(dataDirectory("ShotProfiles"));
            if (!plugins.Any(item => item.Id == id)) plugins.Add(plugin);
        }
        Plugins = plugins;
    }

    // Controller tuning is independent of whether a training plugin is installed.
    internal static IReadOnlyList<IGameControllerPlugin> ControllerAdapters(Func<string, string?> dataDirectory) =>
        [new Nba2K27VisionPlugin(dataDirectory("ShotProfiles"))];
}
