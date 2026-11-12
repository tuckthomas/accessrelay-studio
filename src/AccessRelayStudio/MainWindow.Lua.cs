using EliteScriptStudio.Core;

namespace EliteScriptStudio;

public sealed partial class MainWindow
{
    private bool SaveLuaScript(LuaControllerScript script, ControlId? control)
    {
        if (BlockDuringVisionTraining()) return false;
        if (RecorderInUse())
        {
            Status.Text = "Finish and save or discard the recording before saving a Lua script.";
            return false;
        }
        var previous = _draft;
        _draft = _draft.WithLuaScript(script);
        if (control is { } assigned)
            _draft = _draft.WithBinding(assigned, new(ScriptKind.Lua, LuaScriptId: script.Id));
        if (!SaveDraft())
        {
            _draft = previous;
            return false;
        }
        _luaPanel?.SetProfile(_draft);
        RefreshInspector();
        return true;
    }

    private async Task<bool> DeleteLuaScript(LuaControllerScript script)
    {
        if (BlockDuringVisionTraining()) return false;
        if (RecorderInUse())
        {
            Status.Text = "Finish and save or discard the recording before deleting a Lua script.";
            return false;
        }
        var expected = _draft;
        if (!await StudioDialog.ConfirmDeletionAsync(Root.XamlRoot, Root.ActualTheme, "Lua script", script.Name,
            "The script and its control assignments will be removed from this macro profile.", "Delete Lua script?")
            || _closing || _draft != expected || RecorderInUse())
            return false;
        if (BlockDuringVisionTraining()) return false;
        _draft = _draft.WithoutLuaScript(script.Id);
        if (!SaveDraft())
        {
            _draft = expected;
            return false;
        }
        _luaPanel?.SetProfile(_draft);
        RefreshInspector();
        return true;
    }
}
