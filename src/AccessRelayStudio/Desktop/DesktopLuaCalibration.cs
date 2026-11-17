using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Desktop;

internal sealed partial class DesktopVisionService
{
    private readonly DesktopControllerService? _luaController;
    private ControlId _luaControl = ControlId.LargeRight;
    private string? _luaTrialContext;
    private bool _luaFeedbackSaved;
    private string _luaAdjustment = "";
    private readonly Dictionary<string, List<string>> _luaExcellent = [];
    private bool _luaHistoryLoaded;
    private StudioProfile? _luaChoicesProfile;
    private sealed record LuaChoice(string Control, string Name, string ScriptName);
    private LuaChoice[] _luaChoices = [];

    private LuaChoice[] LuaChoices()
    {
        var profile = _activeProfile();
        if (!ReferenceEquals(profile, _luaChoicesProfile))
        {
            _luaChoicesProfile = profile;
            _luaChoices = profile.Bindings.Where(pair => pair.Value.Enabled && pair.Value.Script == ScriptKind.Lua
                && pair.Value.LuaScriptId is { } id && profile.LuaScripts.TryGetValue(id, out var script)
                && LuaShotCalibration.IsCompatible(script))
                .Select(pair => new LuaChoice(pair.Key.ToString(), pair.Key switch {
                    ControlId.LargeRight => "P4 · lower right paddle", ControlId.SmallRight => "P2 · upper right paddle",
                    ControlId.LargeLeft => "P3 · lower left paddle", ControlId.SmallLeft => "P1 · upper left paddle",
                    _ => pair.Key.ToString() }, profile.LuaScripts[pair.Value.LuaScriptId!.Value].Name)).ToArray();
        }
        return _luaChoices;
    }

    private LuaControllerScript SelectedLua()
    {
        if (_plugin?.SupportsRhythmLuaCalibration != true || _luaController is null || !LuaChoices().Any(c => c.Control == _luaControl.ToString()))
            throw new InvalidOperationException("Assign a recorded full rhythm-shot Lua to a button or paddle first.");
        var profile = _activeProfile();
        return profile.LuaScripts[profile.GetBinding(_luaControl).LuaScriptId!.Value];
    }

    private string LuaContext()
    {
        var profile = _activeProfile(); var script = SelectedLua(); var capture = _session?.Status;
        var value = $"{_plugin?.Id}|{_profile?.ConfigurationFingerprint}|{_selectedSourceId}|{capture?.CaptureWidth}|{capture?.CaptureHeight}|{LuaShotCalibration.ConfigurationKey(profile.Id, _luaControl, script)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private object LuaCalibrationSnapshot()
    {
        var choices = LuaChoices();
        if (_luaController is null || _plugin?.SupportsRhythmLuaCalibration != true || choices.Length == 0) return new { available = false };
        if (!choices.Any(c => c.Control == _luaControl.ToString())) _luaControl = Enum.Parse<ControlId>(choices[0].Control);
        var script = SelectedLua(); var training = LuaShotCalibration.TrainingFor(script);
        var runtime = _runtime();
        if ((runtime?.LuaCalibrationArmed == true || runtime?.LuaCalibrationPickupActive == true) && (_session?.Status is not { Running: true, SourceStale: false, CaptureFailed: false }
            || _luaTrialContext != LuaContext())) runtime.PauseLuaCalibrationTrial();
        LoadLuaHistory();
        int count = _luaExcellent.TryGetValue(LuaContext(), out var receipts) ? Math.Min(3, receipts.Count) : 0;
        return new { available = true, controls = choices, selectedControl = _luaControl.ToString(), scriptName = script.Name,
            holdMs = training.HoldMs, tempoMs = training.TempoMs, armed = runtime?.LuaCalibrationArmed == true,
            pickupActive = runtime?.LuaCalibrationPickupActive == true,
            message = runtime?.LuaCalibrationMessage ?? "Controller unavailable.", lastAction = runtime?.LastLuaCalibrationAction,
            feedbackSaved = _luaFeedbackSaved, excellentCount = count, confirmed = count >= 3, lastAdjustment = _luaAdjustment };
    }

    private object? InvokeLuaCalibration(string action, JsonElement args)
    {
        var runtime = _runtime() ?? throw new InvalidOperationException("Controller monitoring is unavailable.");
        if (action == "luaCalibrationPause") { runtime.PauseLuaCalibrationTrial(); return LuaCalibrationSnapshot(); }
        if (AssistantBusy) throw new InvalidOperationException("Wait for the current Live action to finish.");
        if (action == "luaCalibrationSelect")
        {
            runtime.PauseLuaCalibrationTrial();
            if (!Enum.TryParse<ControlId>(args.Text("control"), true, out var control) || !LuaChoices().Any(c => c.Control == control.ToString()))
                throw new InvalidOperationException("Choose a mapped full rhythm-shot Lua.");
            _luaControl = control; _luaTrialContext = null; _luaFeedbackSaved = true; _luaAdjustment = "";
            return LuaCalibrationSnapshot();
        }
        if (action is "luaCalibrationTrial" or "luaCalibrationPickup")
        {
            if (_session?.Status is not { Running: true, SourceStale: false, CaptureFailed: false, CaptureWidth: > 0, CaptureHeight: > 0 }
                || _session.IsRecordingTraining)
                throw new InvalidOperationException("Start a fresh gameplay preview before a Lua practice trial.");
            _ = SelectedLua(); _luaTrialContext = LuaContext();
            if (action == "luaCalibrationPickup")
            {
                runtime.CollectLuaCalibrationBall(8000); _luaFeedbackSaved = true; return LuaCalibrationSnapshot();
            }
            runtime.StartLuaCalibrationTrial(_luaControl, args.Flag("automatic"), 8000);
            _luaFeedbackSaved = false; _luaAdjustment = "";
            return LuaCalibrationSnapshot();
        }
        if (action != "luaCalibrationFeedback") throw new InvalidOperationException("Unknown Lua calibration command.");
        var receipt = runtime.LastLuaCalibrationAction ?? throw new InvalidOperationException("Complete the full paddle-script trial first.");
        if (runtime.LuaCalibrationArmed || _luaFeedbackSaved || _luaTrialContext != LuaContext()
            || args.GetProperty("receiptId").GetInt64() != receipt.Id || args.Text("runId") != receipt.RunId || args.Text("key") != receipt.Key)
            throw new InvalidOperationException("This result is stale or already saved. Run a new paddle-script trial.");
        var profile = _activeProfile(); var original = SelectedLua();
        if (receipt.Key != LuaShotCalibration.ConfigurationKey(profile.Id, _luaControl, original))
            throw new InvalidOperationException("The script changed after this trial. Run another trial before saving.");
        var feedback = args.GetProperty("feedback");
        string timing = feedback.Text("timing", "none"), tempo = feedback.Text("tempo", "none"), shotType = feedback.Text("shotType", "");
        if (shotType is not ("rhythm" or "timing")) throw new InvalidOperationException("Report the game's RHYTHM or TIMING heading.");
        if (timing == "none" && tempo == "none") throw new InvalidOperationException("Enter the feedback shown by the game.");
        if (shotType == "timing" && tempo != "none") throw new InvalidOperationException("A TIMING result has no rhythm tempo grade.");
        var before = LuaShotCalibration.TrainingFor(original);
        var updated = LuaShotCalibration.ApplyFeedback(original, timing, tempo);
        var after = LuaShotCalibration.TrainingFor(updated);
        string context = _luaTrialContext;
        _luaController!.SaveCalibratedLua(profile.Id, _luaControl, receipt.Key, updated);
        _luaFeedbackSaved = true;
        LoadLuaHistory();
        if (!_luaExcellent.TryGetValue(context, out var excellent)) _luaExcellent[context] = excellent = [];
        if (shotType == "rhythm" && timing == "excellent" && tempo is "none" or "excellent")
        {
            string identity = receipt.RunId + ":" + receipt.Id;
            if (!excellent.Contains(identity)) excellent.Add(identity);
        }
        else excellent.Clear();
        _luaAdjustment = $"Result saved. Lua down hold: {before.HoldMs} → {after.HoldMs} ms. "
            + (tempo == "none" ? "No tempo grade reported; upswing duration kept." : $"Lua upswing: {before.TempoMs} → {after.TempoMs} ms.");
        try { SaveLuaHistory(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _luaAdjustment += " Script timing was saved, but Excellent history could not be written."; }
        return LuaCalibrationSnapshot();
    }

    private string LuaHistoryPath => Path.Combine(DesktopEnvironment.DataDirectory("Vision") ?? Path.Combine(AppContext.BaseDirectory, "Vision"), "lua-calibration-results.json");
    private void LoadLuaHistory()
    {
        if (_luaHistoryLoaded) return; _luaHistoryLoaded = true;
        if (!File.Exists(LuaHistoryPath)) return;
        try { foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(LuaHistoryPath)) ?? [])
                if (pair.Key.Length == 64 && pair.Value is not null) _luaExcellent[pair.Key] = pair.Value.Where(v => !string.IsNullOrEmpty(v)).Distinct().Take(3).ToList(); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { /* Unreadable history cannot establish an Excellent result. */ }
    }
    private void SaveLuaHistory()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LuaHistoryPath)!);
        string temporary = LuaHistoryPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_luaExcellent.TakeLast(64).ToDictionary(p => p.Key, p => p.Value)));
        File.Move(temporary, LuaHistoryPath, true);
    }
}
