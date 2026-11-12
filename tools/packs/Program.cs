using System.Text.Json;
using EliteScriptStudio.Core;

try
{
    if (args is not [var path]) throw new InvalidDataException("Usage: dotnet run --project tools/packs/PackTool.csproj -- path/to/pack.json");
    var pack = GamePackDefinition.Read(path);
    Console.WriteLine(JsonSerializer.Serialize(new { pack.Manifest.Id, pack.Manifest.Version, pack.ManifestSha256,
        resources = pack.Manifest.Assets.Length, pack.Manifest.VisionAdapter, activated = false }));
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
{ Console.Error.WriteLine("Pack validation failed: " + error.Message); return 2; }
