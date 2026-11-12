using System.IO;
using System.Text;
using System.Text.Json;

namespace EliteScriptStudio.Core;

/// <summary>Separate versioned storage; shot profiles never enter the macro-profile enumeration.</summary>
internal sealed class Nba2K27ShotProfileStore
{
    private const int Version = 1;
    private const int MaximumFileBytes = 64 * 1024;
    private const string SelectionFile = "selected-shot-profile.txt";
    private readonly object _gate = new();
    private sealed record Document(int Version, Nba2K27ShotProfile Profile);
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 8,
        WriteIndented = true
    };

    public string DirectoryPath { get; }

    public Nba2K27ShotProfileStore(string? directory = null)
    {
        DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessRelayStudio", "Nba2K27", "ShotProfiles")));
    }

    public IReadOnlyList<Nba2K27ShotProfile> LoadAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(DirectoryPath)) return Array.Empty<Nba2K27ShotProfile>();
            var result = new List<Nba2K27ShotProfile>();
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .Take(Nba2K27ShotProfile.MaximumProfiles + 1))
            {
                if (result.Count == Nba2K27ShotProfile.MaximumProfiles) break;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || id == Guid.Empty) continue;
                try { result.Add(Load(id)); }
                catch (Exception error) when (IsInvalidOrUnavailable(error)) { }
            }
            return result.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public Nba2K27ShotProfile Load(Guid id)
    {
        string path = PathFor(id);
        lock (_gate)
        {
            using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length <= 0 || input.Length > MaximumFileBytes)
                throw new InvalidDataException("The shot profile is empty or too large.");
            Document document;
            try { document = JsonSerializer.Deserialize<Document>(input, Format)
                    ?? throw new InvalidDataException("The shot profile is empty."); }
            catch (JsonException error) { throw new InvalidDataException("The shot profile JSON is invalid.", error); }
            if (document.Version != Version || document.Profile is null || document.Profile.Id != id)
                throw new InvalidDataException("The shot profile version or filename identity is invalid.");
            document.Profile.Validate();
            return document.Profile.UsesShotMeter ? document.Profile : document.Profile with { MeterReleaseType = "" };
        }
    }

    public void Save(Nba2K27ShotProfile profile)
    {
        profile.Validate();
        if (!profile.UsesShotMeter) profile = profile with { MeterReleaseType = "" };
        string path = PathFor(profile.Id);
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!File.Exists(path) && Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .Take(Nba2K27ShotProfile.MaximumProfiles).Count() >= Nba2K27ShotProfile.MaximumProfiles)
                throw new InvalidOperationException($"Save up to {Nba2K27ShotProfile.MaximumProfiles} shot profiles. Delete one before creating another.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(Version, profile), Format);
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("The shot profile is too large.");
            WriteAtomically(path, bytes);
        }
    }

    public Nba2K27ShotProfile Duplicate(Guid id, string newName)
    {
        lock (_gate)
        {
            var copy = Load(id) with { Id = Guid.NewGuid(), Name = newName?.Trim() ?? "" };
            Save(copy);
            return copy;
        }
    }

    public void Delete(Guid id)
    {
        string path = PathFor(id);
        lock (_gate)
        {
            bool selected = LoadSelectedId() == id;
            File.Delete(path);
            if (selected) File.Delete(Path.Combine(DirectoryPath, SelectionFile));
        }
    }

    public Guid? LoadSelectedId()
    {
        lock (_gate)
        {
            try
            {
                using var input = File.Open(Path.Combine(DirectoryPath, SelectionFile), FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (input.Length is <= 0 or > 128) return null;
                using var reader = new StreamReader(input, Encoding.ASCII);
                if (!Guid.TryParseExact(reader.ReadToEnd().Trim(), "N", out var id) || id == Guid.Empty) return null;
                Load(id);
                return id;
            }
            catch (Exception error) when (IsInvalidOrUnavailable(error)) { return null; }
        }
    }

    public void Select(Guid id)
    {
        lock (_gate)
        {
            Load(id); // Selection cannot point at an incomplete, missing, or incompatible profile.
            WriteAtomically(Path.Combine(DirectoryPath, SelectionFile), Encoding.ASCII.GetBytes(id.ToString("N")));
        }
    }

    private string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Choose an existing shot-profile ID.", nameof(id));
        return Path.Combine(DirectoryPath, id.ToString("N") + ".json");
    }

    private static bool IsInvalidOrUnavailable(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidDataException or ArgumentException;

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
