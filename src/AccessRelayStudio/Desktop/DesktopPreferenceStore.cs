using System.Text.Json;

namespace EliteScriptStudio.Desktop;

internal sealed class DesktopPreferenceStore
{
    private readonly string _directory;
    private string PathName => Path.Combine(_directory, "desktop-preferences.json");

    internal DesktopPreferenceStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessRelayStudio", "Preferences");

    internal bool LoadResourceMonitoring() => ReadFlag("resourceMonitoringEnabled", whenMissing: true, whenDamaged: false);
    internal bool LoadXboxCpuConversion() => ReadFlag("xboxCpuConversion", whenMissing: false, whenDamaged: false);

    private bool ReadFlag(string key, bool whenMissing, bool whenDamaged)
    {
        if (!File.Exists(PathName)) return whenMissing;
        try
        {
            if (new FileInfo(PathName).Length > 1024) return whenDamaged;
            using var value = JsonDocument.Parse(File.ReadAllText(PathName));
            return value.RootElement.TryGetProperty(key, out var flag) ? flag.GetBoolean() : whenDamaged;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return whenDamaged; }
    }

    internal void SaveResourceMonitoring(bool enabled) => Save(enabled, LoadXboxCpuConversion());
    internal void SaveXboxCpuConversion(bool enabled) => Save(LoadResourceMonitoring(), enabled);

    private void Save(bool resourceMonitoringEnabled, bool xboxCpuConversion)
    {
        Directory.CreateDirectory(_directory);
        string temporary = Path.Combine(_directory, "desktop-preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { resourceMonitoringEnabled, xboxCpuConversion }));
            File.Move(temporary, PathName, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
