using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteScriptStudio.Core;

internal sealed class VoiceCommandStore(string directory)
{
    private static readonly JsonSerializerOptions Format = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };
    private string PathFor(Guid id) => Path.Combine(directory, id.ToString("N") + ".json");
    internal VoiceCommandSettings Load(Guid profile)
    {
        try
        {
            using var stream = File.OpenRead(PathFor(profile));
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Voice-command preferences exceed 4 MiB.");
            var settings = JsonSerializer.Deserialize<VoiceCommandSettings>(stream, Format) ?? throw new InvalidDataException("Missing voice-command settings.");
            settings.Validate(); return settings;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }
    internal void Save(Guid profile, VoiceCommandSettings settings)
    {
        settings.Validate(); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Format);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Voice-command preferences exceed 4 MiB.");
        Directory.CreateDirectory(directory); string target = PathFor(profile), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
