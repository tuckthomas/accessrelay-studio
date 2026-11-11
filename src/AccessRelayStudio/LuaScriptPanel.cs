using EliteScriptStudio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EliteScriptStudio;

internal sealed class LuaScriptPanel : UserControl
{
    private sealed record Assignment(ControlId? Control, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox _scripts = new() { Header = "Saved Lua scripts", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Header = "Script name", MaxLength = 80 };
    private readonly TextBox _source = new()
    {
        Header = "Lua source", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"), FontSize = 14, MinHeight = 250, MaxHeight = 440,
        MaxLength = LuaControllerScript.MaxSourceLength
    };
    private readonly ComboBox _assignment = new() { Header = "Assign when saved", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly TextBlock _runtime = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly Func<LuaControllerScript, ControlId?, bool> _save;
    private readonly Func<LuaControllerScript, Task<bool>> _delete;
    private StudioProfile? _profile;
    private LuaControllerScript _editing = LuaControllerScript.Create();
    private bool _syncing;
    private bool _dirty;

    internal LuaScriptPanel(Func<LuaControllerScript, ControlId?, bool> save, Func<LuaControllerScript, Task<bool>> delete)
    {
        _save = save;
        _delete = delete;
        var panel = new StackPanel { Spacing = 12 };
        Content = panel;
        panel.Children.Add(_scripts);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddAction(actions, "New script", () => { if (CanSwitch()) Edit(LuaControllerScript.Create()); });
        AddAction(actions, "Revert edits", () => Edit(_profile?.LuaScripts.GetValueOrDefault(_editing.Id) ?? LuaControllerScript.Create()));
        var deleteButton = new Button { Content = "Delete saved script" };
        deleteButton.Click += async (_, _) =>
        {
            if (_profile?.LuaScripts.GetValueOrDefault(_editing.Id) is not { } saved || !CanSwitch())
                return;
            if (await _delete(saved))
                Edit(LuaControllerScript.Create());
        };
        actions.Children.Add(deleteButton);
        panel.Children.Add(actions);
        panel.Children.Add(_name);
        panel.Children.Add(_source);
        Assignment[] assignments =
        [
            new(null, "Keep current assignments"),
            .. ControlMap.All.Select(control => new Assignment(control, control.ToString()))
        ];
        _assignment.ItemsSource = assignments;
        _assignment.SelectedIndex = 0;
        panel.Children.Add(_assignment);
        var saveActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddAction(saveActions, "Check syntax", () => { if (TryCompile(out _)) _message.Text = "Syntax is valid. Checking does not run the script."; });
        AddAction(saveActions, "Save & apply", () =>
        {
            if (TryCompile(out var script) && _save(script, (_assignment.SelectedItem as Assignment)?.Control))
            {
                _dirty = false;
                _editing = script;
                _message.Text = "Saved and applied. Turn on Macro scripts on the Dashboard, then press the assigned control.";
            }
        });
        panel.Children.Add(saveActions);
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_runtime, AutomationLiveSetting.Polite);
        panel.Children.Add(_message);
        panel.Children.Add(_runtime);
        panel.Children.Add(new TextBlock
        {
            Text = "input.down(\"A\") · input.axis(\"RightY\") · input.trigger()\n"
                + "output.button(\"A\", true) · output.axis(\"RightY\", 50)\n"
                + "output.release(\"RightY\") · output.clear() · wait(80) · elapsed()\n"
                + "state.set(\"count\", state.get(\"count\") + 1)\n\n"
                + "Stick axes use −100 to +100 percent; triggers use 0 to 100. Lua variables, functions, tables, conditions, loops, math, and table iterators are available. "
                + "Every loop that runs across input samples must call wait(). One script runs at a time, for up to 60 seconds per press. "
                + "A different assigned control takes over. Outputs remain set through wait() and return to manual input when released, completed, stopped, or disconnected. "
                + "Only run scripts you trust: host APIs are restricted, but the interpreter is in the app process.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14
        });
        _scripts.SelectionChanged += (_, _) =>
        {
            if (_syncing || _scripts.SelectedItem is not LuaControllerScript script || script.Id == _editing.Id)
                return;
            if (CanSwitch()) Edit(script);
            else
            {
                _syncing = true;
                _scripts.SelectedItem = _profile?.LuaScripts.GetValueOrDefault(_editing.Id);
                _syncing = false;
            }
        };
        _name.TextChanged += (_, _) => { if (!_syncing) _dirty = true; };
        _source.TextChanged += (_, _) => { if (!_syncing) _dirty = true; };
        Edit(_editing);
    }

    internal bool HasUnsavedChanges => _dirty;

    internal void SetProfile(StudioProfile profile)
    {
        if (_profile?.Id == profile.Id && _profile.LuaScripts == profile.LuaScripts)
            return;
        bool sameProfile = _profile?.Id == profile.Id;
        _profile = profile;
        _syncing = true;
        _scripts.ItemsSource = profile.LuaScripts.Values.OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _syncing = false;
        Edit(sameProfile && profile.LuaScripts.TryGetValue(_editing.Id, out var current) ? current
            : profile.LuaScripts.Values.OrderBy(script => script.Name).FirstOrDefault() ?? LuaControllerScript.Create());
    }

    internal void ShowRuntime(MacroStatus status)
    {
        string text = status.Error ?? (status.Script == ScriptKind.Lua && status.IsActive
            ? $"Running {status.RecordingName}" : "No Lua script running.");
        if (_runtime.Text != text) _runtime.Text = text;
    }

    private bool CanSwitch()
    {
        if (!_dirty) return true;
        _message.Text = "Save & apply or revert your edits before choosing another script.";
        return false;
    }

    private void Edit(LuaControllerScript script)
    {
        _syncing = true;
        _editing = script;
        _name.Text = script.Name;
        _source.Text = script.Source;
        _assignment.SelectedIndex = 0;
        _scripts.SelectedItem = _profile?.LuaScripts.GetValueOrDefault(script.Id);
        _message.Text = "";
        _dirty = false;
        _syncing = false;
    }

    private bool TryCompile(out LuaControllerScript script)
    {
        script = _editing with { Name = _name.Text.Trim(), Source = _source.Text };
        try { _ = new LuaControllerProgram(script); return true; }
        catch (Exception error) { _message.Text = error.Message; return false; }
    }

    private static void AddAction(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }
}
