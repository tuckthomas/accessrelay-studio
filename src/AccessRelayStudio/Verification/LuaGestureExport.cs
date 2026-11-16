using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteScriptStudio.Core;

internal static class LuaGestureExport
{
    // Offline evaluation only. The separate guarded controller probe owns any
    // real output; exporting a gesture never creates or activates a device.
    public static void Run(string directory, string profileId, string controlName, string destination)
    {
        if (!Guid.TryParse(profileId, out var id) || !Enum.TryParse<ControlId>(controlName, out var control)
            || !Enum.IsDefined(control)) throw new ArgumentException("Select an existing profile and control.");
        var store = new ProfileStore(directory);
        var profile = store.LoadAll().Single(p => p.Id == id);
        var binding = profile.GetBinding(control);
        if (!binding.Enabled || binding.Script != ScriptKind.Lua || binding.LuaScriptId is not { } scriptId)
            throw new InvalidOperationException("The selected control must have an enabled saved Lua binding.");
        var script = profile.LuaScripts[scriptId];
        var engine = new MacroEngine();
        engine.Apply(profile with { ScriptsEnabled = true });
        var deadzone = store.LoadStickDeadzone();
        var points = new List<object>();
        ControllerState? previous = null;
        bool started = false, completed = false;
        int endedAt = 0;
        for (int at = 0; at <= 60000; at += 4)
        {
            var state = deadzone.Apply(engine.Tick(default, PaddleButtons.None, true, at,
                externalTrigger: at == 100 ? control : null));
            if (state.Buttons != 0 || state.LeftTrigger != 0 || state.RightTrigger != 0 || state.LeftX != 0 || state.LeftY != 0)
                throw new InvalidOperationException("This comparison permits right-stick-only Lua output.");
            if (state != previous) points.Add(new { atMs = at, rightX = state.RightX, rightY = state.RightY });
            previous = state;
            started |= engine.Status.IsActive;
            if (engine.Status.Error is { } error) throw new InvalidOperationException(error);
            if (started && !engine.Status.IsActive) { completed = true; endedAt = at; break; }
        }
        if (!completed || previous != default(ControllerState))
            throw new InvalidOperationException("The saved Lua did not complete at neutral within the diagnostic limit.");
        points.Add(new { atMs = endedAt + 200, rightX = (short)0, rightY = (short)0 });
        string path = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, new
        {
            scope = "Offline production MacroEngine evaluation of the unchanged saved Lua. No device output generated.",
            scriptName = script.Name, control = control.ToString(), cadenceMs = 4,
            sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script.Source))),
            deadzone, points
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"SUCCESS Exported {script.Name} through MacroEngine: {points.Count} right-stick changes; neutral completion.");
    }
}
