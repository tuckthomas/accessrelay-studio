using EliteScriptStudio.Core;
using EliteScriptStudio.Packs.Nba2K27;

namespace EliteScriptStudio.Packs;

/// <summary>Composition of reviewed adapters; a manifest never authorizes loading a DLL.</summary>
internal static class BuiltInVisionPlugins
{
    internal static IReadOnlyList<IVisionGamePlugin> Load(string bundledDirectory, Func<string, string?> dataDirectory)
    {
        string manifest = Path.Combine(bundledDirectory, "nba2k27", "pack.json");
        if (!File.Exists(manifest)) return [];
        try
        {
            var pack = GamePackDefinition.Read(manifest);
            return pack.Manifest.Id == "nba2k27" && pack.Manifest.VisionAdapter == "temporal-release-v1"
                ? [new Nba2K27VisionPlugin(dataDirectory("ShotProfiles"))] : [];
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return []; }
    }
}
