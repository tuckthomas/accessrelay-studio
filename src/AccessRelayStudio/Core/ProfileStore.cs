using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteScriptStudio.Core;

public sealed class ProfileStore
{
    private const int OriginalVersion = 1;
    private const int RecordingVersion = 2;
    private const int MaxFileBytes = 12 * 1024 * 1024;
    private const string SelectionFile = "active-macro-profile.txt";
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
        if (document is null || document.Version is not (OriginalVersion or RecordingVersion) || document.Profile is null)
            throw new InvalidDataException("The profile file format is unsupported.");
        document.Profile.Validate();
        return document.Profile;
    }

    private static void Write(StudioProfile profile, string path)
    {
        profile.Validate();
        WriteAtomically(path, stream =>
        {
            int version = profile.Recordings.Count == 0 ? OriginalVersion : RecordingVersion;
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
