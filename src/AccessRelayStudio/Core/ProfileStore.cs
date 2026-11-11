using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteScriptStudio.Core;

public sealed class ProfileStore
{
    private const int OriginalVersion = 1;
    private const int RecordingVersion = 2;
    private const int LuaVersion = 3;
    private const int MaxFileBytes = 12 * 1024 * 1024;
    private const string SelectionFile = "active-macro-profile.txt";
    private const string StickDeadzoneFile = "stick-deadzones.json";
    private sealed record ProfileDocument(int Version, StudioProfile Profile);

    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<ControlId>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<ScriptKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<ShotVariant>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<RecordingChannels>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };

    public ProfileStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EliteScriptStudio", "Profiles"));
    }

    public string DirectoryPath { get; }

    public Guid? LoadSelectedId()
    {
        try
        {
            using var stream = File.Open(Path.Combine(DirectoryPath, SelectionFile), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (stream.Length != 32)
                return null;
            Span<byte> id = stackalloc byte[32];
            stream.ReadExactly(id);
            return Guid.TryParseExact(Encoding.ASCII.GetString(id), "N", out var selected) ? selected : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void SaveSelectedId(Guid id) => WriteAtomically(Path.Combine(DirectoryPath, SelectionFile),
        stream => stream.Write(Encoding.ASCII.GetBytes(id.ToString("N"))));

    internal StickDeadzone LoadStickDeadzone()
    {
        try
        {
            using var stream = File.Open(Path.Combine(DirectoryPath, StickDeadzoneFile), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length <= 256 && JsonSerializer.Deserialize<double[]>(stream) is [double left, double right])
            {
                var settings = new StickDeadzone(left, right);
                if (settings.IsValid)
                    return settings;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return StickDeadzone.Default;
    }

    internal void SaveStickDeadzone(StickDeadzone settings)
    {
        if (!settings.IsValid)
            throw new InvalidDataException($"Deadzone must be between 0 and {StickDeadzone.MaximumPercent}%.");
        WriteAtomically(Path.Combine(DirectoryPath, StickDeadzoneFile),
            stream => JsonSerializer.Serialize(stream, new[] { settings.LeftPercent, settings.RightPercent }));
    }

    public IReadOnlyList<StudioProfile> LoadAll()
    {
        if (!Directory.Exists(DirectoryPath))
            return [];

        var profiles = new List<StudioProfile>();
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out Guid id))
            {
                try { profiles.Add(Load(id)); }
                catch (FileNotFoundException) { } // A concurrent delete removed this entry.
            }
        }
        return profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ThenBy(profile => profile.Id).ToArray();
    }

    public StudioProfile Load(Guid id)
    {
        var profile = Read(FileFor(id));
        if (profile.Id != id)
            throw new InvalidDataException("The stored profile ID does not match its filename.");
        return profile;
    }

    public void Save(StudioProfile profile) => Write(profile, FileFor(profile.Id));

    internal void SaveRecordingEdit(StudioProfile original, StudioProfile updated)
    {
        if (original.Id != updated.Id || !string.Equals(JsonSerializer.Serialize(Load(original.Id), Format),
            JsonSerializer.Serialize(original, Format), StringComparison.Ordinal))
            throw new InvalidOperationException("This profile changed in another window. Reopen AccessRelay before saving the edit.");
        Save(updated);
    }

    public void Delete(Guid id)
    {
        try { File.Delete(FileFor(id)); }
        catch (DirectoryNotFoundException) { }
    }

    public StudioProfile Import(string path)
    {
        var profile = Read(path) with { Id = Guid.NewGuid() };
        Save(profile);
        return profile;
    }

    public void Export(StudioProfile profile, string path) => Write(profile, path);

    internal static string FormatRecording(MacroRecording recording) => JsonSerializer.Serialize(recording, Format);

    internal void ExportRecording(MacroRecording recording, string path)
    {
        recording.Validate();
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a location outside the macro profiles folder.");

        WriteAtomically(path, stream => JsonSerializer.Serialize(stream, recording, Format));
    }

    private string FileFor(Guid id) => Path.Combine(DirectoryPath, id.ToString("N") + ".json");

    private static StudioProfile Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaxFileBytes)
            throw new InvalidDataException("The profile file is too large.");
        ProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileDocument>(stream, Format);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The profile file is invalid.", error);
        }
        if (document is null || document.Version is not (OriginalVersion or RecordingVersion or LuaVersion) || document.Profile is null)
            throw new InvalidDataException("The profile file format is unsupported.");
        document.Profile.Validate();
        return document.Profile;
    }

    private static void Write(StudioProfile profile, string path)
    {
        profile.Validate();
        WriteAtomically(path, stream =>
        {
            int version = profile.LuaScripts.Count != 0 ? LuaVersion : profile.Recordings.Count == 0 ? OriginalVersion : RecordingVersion;
            JsonSerializer.Serialize(stream, new ProfileDocument(version, profile), Format);
            if (stream.Length > MaxFileBytes)
                throw new InvalidDataException("The profile file is too large.");
        });
    }

    private static void WriteAtomically(string path, Action<FileStream> write)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetRandomFileName() + ".tmp");
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using (stream)
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
