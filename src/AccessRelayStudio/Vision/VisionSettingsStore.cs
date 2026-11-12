using System.Text.Json;
using System.Text.Json.Serialization;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

/// <summary>Vision preferences are separate from legacy macro profiles and never contain an armed flag.</summary>
internal sealed class VisionSettingsStore
{
    private sealed record Document(int Version, Guid ProfileId, VisionSettings Settings);
    private const int MaximumBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ControlId>(JsonNamingPolicy.CamelCase, false) }
    };
    private readonly string _directory;

    internal VisionSettingsStore(string? directory = null) => _directory = Path.GetFullPath(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessRelayStudio", "Vision"));

    internal VisionSettings Load(Guid profileId)
    {
        var path = Path.Combine(_directory, profileId.ToString("N") + ".json");
        return File.Exists(path) ? Read(path, profileId) : VisionSettings.Default;
    }

    internal VisionSettings Import(string path) => Read(path, null);

    private static VisionSettings Read(string path, Guid? expectedProfile)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The vision settings file is empty or too large.");
        var document = JsonSerializer.Deserialize<Document>(stream, Format)
            ?? throw new InvalidDataException("The vision settings file is empty.");
        if (document.Version != 1 || document.ProfileId == Guid.Empty ||
            (expectedProfile.HasValue && document.ProfileId != expectedProfile))
            throw new InvalidDataException("This vision settings file has an unsupported version or profile identity.");
        var settings = document.Settings ?? throw new InvalidDataException("Vision settings are missing.");
        settings.Validate();
        return settings;
    }

    internal void Save(Guid profileId, VisionSettings settings)
    {
        Directory.CreateDirectory(_directory);
        Export(Path.Combine(_directory, profileId.ToString("N") + ".json"), profileId, settings);
    }

    internal void Export(string path, Guid profileId, VisionSettings settings)
    {
        settings.Validate();
        if (profileId == Guid.Empty) throw new InvalidDataException("Choose a macro profile first.");
        var absolute = Path.GetFullPath(path);
        var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Document(1, profileId, settings), Format);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, absolute, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
