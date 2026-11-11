using EliteScriptStudio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EliteScriptStudio;

public sealed partial class Nba2K27PluginPanel : UserControl
{
    private sealed record MacroChoice(string Name, Guid? RecordingId = null, ShotVariant? Variant = null)
    {
        public string Label => $"{Name} · {(RecordingId.HasValue ? "Recorded macro" : "Built-in script")}";
        public override string ToString() => Label;
    }

    private sealed record TuningUndo(MacroChoice Choice, StudioProfile Before, StudioProfile After);

    private readonly Action<StudioProfile, StudioProfile> _save;
    private readonly Func<ShotVariant, string> _variantName;
    private StudioProfile? _profile;
    private StudioProfile? _proposal;
    private MacroChoice? _loaded;
    private TuningUndo? _undo;
    private bool _refreshing;

    internal Nba2K27PluginPanel(Action<StudioProfile, StudioProfile> save, Func<ShotVariant, string> variantName)
    {
        _save = save;
        _variantName = variantName;
        InitializeComponent();
    }

    internal void SetProfile(StudioProfile profile)
    {
        if (_profile == profile)
            return;

        var previous = _profile?.Id == profile.Id ? MacroPicker.SelectedItem as MacroChoice : null;
        _profile = profile;
        _loaded = null;
        _undo = null;
        ProfileInfo.Text = $"Active macro profile · {profile.Name}";
        var choices = profile.Recordings.Values.OrderBy(recording => recording.Name, StringComparer.OrdinalIgnoreCase)
            .Select(recording => new MacroChoice(recording.Name, RecordingId: recording.Id))
            .Concat(Enum.GetValues<ShotVariant>().Select(variant => new MacroChoice(_variantName(variant), Variant: variant)))
            .ToArray();
        _refreshing = true;
        MacroPicker.ItemsSource = choices;
        MacroPicker.SelectedItem = choices.FirstOrDefault(choice => previous is not null &&
            choice.RecordingId == previous.RecordingId && choice.Variant == previous.Variant) ?? choices[0];
        _refreshing = false;
        MacroSelectionChanged(MacroPicker, null);
    }

    private void MacroSelectionChanged(object sender, SelectionChangedEventArgs? args)
    {
        if (_refreshing)
            return;

        _loaded = null;
        LoadMacroButton.IsEnabled = MacroPicker.SelectedItem is MacroChoice;
        LoadedMacroInfo.Text = "Choose a macro and select Load macro.";
        ResetFeedback();
    }

    private void LoadMacroClicked(object sender, RoutedEventArgs args)
    {
        if (_profile is not { } profile || MacroPicker.SelectedItem is not MacroChoice choice)
            return;

        _loaded = choice;
        var (hold, sweep) = ReadValues(profile, choice);
        if (!hold.HasValue)
            LoadedMacroInfo.Text = $"{choice.Name} · A single down-to-up right-stick shot is needed for tuning.";
        else if (!sweep.HasValue)
            LoadedMacroInfo.Text = $"{choice.Name} · Timing is available. The recording has no upward sweep to retime.";
        else
            LoadedMacroInfo.Text = $"{choice.Name} · Loaded.";

        if (hold.HasValue && choice.RecordingId is Guid id &&
            profile.Recordings[id].Frames.Any(frame => (frame.State with { RightY = 0 }) != default))
            LoadedMacroInfo.Text += " Other captured inputs in this macro share the adjusted timestamps.";
        ResetFeedback();
    }

    private void FeedbackChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing)
            RefreshPreview();
    }

    private void ResetFeedback()
    {
        _refreshing = true;
        TimingFeedback.SelectedIndex = 0;
        TempoFeedback.SelectedIndex = 0;
        _refreshing = false;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _proposal = null;
        TuningStatus.Text = "";
        TimingFeedback.IsEnabled = TempoFeedback.IsEnabled = ApplyTuningButton.IsEnabled = UndoTuningButton.IsEnabled = false;
        DownHoldPreview.Text = UpSweepPreview.Text = "—";
        if (_profile is not { } profile || _loaded is not { } choice)
            return;

        var before = ReadValues(profile, choice);
        TimingFeedback.IsEnabled = before.DownHoldMs.HasValue;
        TempoFeedback.IsEnabled = before.UpSweepMs.HasValue;
        UndoTuningButton.IsEnabled = _undo is { } undo && undo.Choice == choice && undo.After == profile;

        var adjustment = Nba2K27Tuning.Suggest(ReadFeedback<Nba2K27TimingFeedback>(TimingFeedback),
            ReadFeedback<Nba2K27TempoFeedback>(TempoFeedback));
        var updated = profile;
        if (adjustment != default)
        {
            updated = choice.RecordingId is Guid id
                ? profile.WithRecording(Nba2K27Tuning.Apply(profile.Recordings[id], adjustment))
                : profile.WithTiming(choice.Variant!.Value, Nba2K27Tuning.Apply(profile.GetTiming(choice.Variant.Value), adjustment));
            if (updated == profile)
                TuningStatus.Text = "The selected adjustment has reached this macro’s timing limit.";
        }
        if (updated != profile)
            _proposal = updated;
        ApplyTuningButton.IsEnabled = _proposal is not null;

        var after = ReadValues(updated, choice);
        DownHoldPreview.Text = FormatChange(before.DownHoldMs, after.DownHoldMs);
        UpSweepPreview.Text = FormatChange(before.UpSweepMs, after.UpSweepMs);
    }

    private static T ReadFeedback<T>(RadioButtons group) where T : struct, Enum =>
        group.SelectedItem is RadioButton { Tag: string tag } && Enum.TryParse<T>(tag, out var value) ? value : default;

    private static Nba2K27Intervals ReadValues(StudioProfile profile, MacroChoice choice)
    {
        if (choice.RecordingId is Guid id)
            return Nba2K27Tuning.GetIntervals(profile.Recordings[id]);
        var timing = profile.GetTiming(choice.Variant!.Value);
        return new(timing.DownHold, timing.UpSweep);
    }

    private static string FormatChange(int? before, int? after) => before is not int current || after is not int next
        ? "Unavailable" : current == next ? $"{current:N0} ms" : $"{current:N0} → {next:N0} ms ({next - current:+0;-0} ms)";

    private void ApplyTuningClicked(object sender, RoutedEventArgs args)
    {
        if (_profile is not { } profile || _proposal is not { } proposed || _loaded is not { } choice ||
            !TrySave(profile, proposed))
            return;

        _undo = new(choice, profile, proposed);
        ResetFeedback();
        TuningStatus.Text = $"Applied to {choice.Name}. Try the same shot again to check the change.";
    }

    private void UndoTuningClicked(object sender, RoutedEventArgs args)
    {
        if (_undo is not { } undo || _loaded != undo.Choice || _profile != undo.After || !TrySave(undo.After, undo.Before))
            return;

        _undo = null;
        ResetFeedback();
        TuningStatus.Text = $"Restored the previous timing for {undo.Choice.Name}.";
    }

    private bool TrySave(StudioProfile original, StudioProfile updated)
    {
        try { _save(original, updated); }
        catch (Exception error)
        {
            TuningStatus.Text = $"Couldn’t save the adjustment. {error.Message}";
            return false;
        }
        _profile = updated;
        return true;
    }
}
