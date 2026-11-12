using System.Text.Json;

namespace EliteScriptStudio.Desktop;

internal sealed class DesktopPreferenceStore
{
    private readonly string _directory;
    private string PathName => Path.Combine(_directory, "desktop-preferences.json");

    internal DesktopPreferenceStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessRelayStudio", "Preferences");

    internal bool LoadResourceMonitoring()
    {
        if (!File.Exists(PathName)) return true;
        try
        {
            if (new FileInfo(PathName).Length > 1024) return false;
            using var value = JsonDocument.Parse(File.ReadAllText(PathName));
            return value.RootElement.GetProperty("resourceMonitoringEnabled").GetBoolean();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return false; }
    }

    internal void SaveResourceMonitoring(bool enabled)
    {
        Directory.CreateDirectory(_directory);
        string temporary = Path.Combine(_directory, "desktop-preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { resourceMonitoringEnabled = enabled }));
            File.Move(temporary, PathName, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
