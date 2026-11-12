using EliteScriptStudio.Packs.Nba2K27;
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
    private readonly Nba2K27ShotProfileStore _shotProfiles = new();
    private IReadOnlyList<Nba2K27ShotProfile> _savedShotProfiles = [];
    private Nba2K27ShotProfile? _selectedShotProfile;
    private Nba2K27ShotProfile? _shotDraftOriginal;
    private bool _restoringShotProfile, _shotDraftDirty, _shotProfileOperation;

    internal event Action<Nba2K27ShotProfile?>? ShotProfileChanged;
    internal Nba2K27ShotProfile? SelectedShotProfile => _shotDraftDirty ? null : _selectedShotProfile;
    internal bool HasUnsavedShotProfileChanges => _shotDraftDirty;

    internal Nba2K27PluginPanel(Action<StudioProfile, StudioProfile> save, Func<ShotVariant, string> variantName)
    {
        _save = save;
        _variantName = variantName;
        InitializeComponent();
        try { RefreshShotProfiles(_shotProfiles.LoadSelectedId(), chooseFirst: true); }
        catch (Exception error) { ShotProfileStatus.Text = $"Could not load shot profiles. {error.Message}"; LoadShotDraft(Nba2K27ShotProfile.Create(), saved: false); }
    }

    private void RefreshShotProfiles(Guid? selectedId, bool chooseFirst)
    {
        var profiles = _shotProfiles.LoadAll();
        var selected = profiles.FirstOrDefault(profile => profile.Id == selectedId)
            ?? (chooseFirst ? profiles.FirstOrDefault() : null);
        if (selected is not null) _shotProfiles.Select(selected.Id);
        _savedShotProfiles = profiles;
        _restoringShotProfile = true;
        try
        {
            ShotProfilePicker.ItemsSource = profiles;
            ShotProfilePicker.SelectedItem = selected;
            LoadShotDraft(selected ?? Nba2K27ShotProfile.Create(), saved: selected is not null);
        }
        finally { _restoringShotProfile = false; }
        ShotProfileChanged?.Invoke(SelectedShotProfile);
        ShotProfileStatus.Text = selected is null
            ? "Create a profile and enter all three jump-shot creator labels before saving."
            : $"Selected {selected.Name}. Assistance remains off until explicitly armed in Vision.";
    }

    private void LoadShotDraft(Nba2K27ShotProfile profile, bool saved)
    {
        bool restoring = _restoringShotProfile;
        _restoringShotProfile = true;
        try
        {
            _selectedShotProfile = saved ? profile : null;
            _shotDraftOriginal = profile;
            ShotProfileName.Text = profile.Name;
            ShotPlayerHeight.Value = profile.HeightInches;
            ShotThreePointRating.Value = profile.ThreePointRating;
            ShotBase.Text = profile.Base;
            ShotUpperRelease1.Text = profile.UpperRelease1;
            ShotUpperRelease2.Text = profile.UpperRelease2;
            ShotRelease1Percent.Value = profile.Release1Percent;
            ShotRelease2Percent.Value = profile.Release2Percent;
            ShotSpeed.SelectedIndex = profile.ShotSpeed - 1;
            ShotCameraView.SelectedIndex = profile.CameraView switch { "2K" => 0, "2K Low" => 1, "2K Legacy" => 2, _ => -1 };
            ShotUsesMeter.IsOn = profile.UsesShotMeter;
            ShotMeterReleaseType.Text = profile.MeterReleaseType;
            _shotDraftDirty = false;
        }
        finally { _restoringShotProfile = restoring; }
        UpdateShotProfileActions();
    }

    private void ShotProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringShotProfile || _shotProfileOperation || ShotProfilePicker.SelectedItem is not Nba2K27ShotProfile selected)
            return;
        if (_shotDraftDirty)
        {
            _restoringShotProfile = true;
            try { ShotProfilePicker.SelectedItem = _selectedShotProfile; }
            finally { _restoringShotProfile = false; }
            ShotProfileStatus.Text = "Save or revert the current edits before selecting another shot profile.";
            return;
        }
        try
        {
            _shotProfiles.Select(selected.Id);
            LoadShotDraft(selected, saved: true);
            ShotProfileChanged?.Invoke(selected);
            ShotProfileStatus.Text = $"Selected {selected.Name}. Assistance remains off until explicitly armed in Vision.";
        }
        catch (Exception error)
        {
            _restoringShotProfile = true;
            try { ShotProfilePicker.SelectedItem = _selectedShotProfile; }
            finally { _restoringShotProfile = false; }
            ShotProfileStatus.Text = $"Could not select the shot profile. {error.Message}";
        }
    }

    private void ShotTextChanged(object sender, TextChangedEventArgs args) => ShotDraftChanged();
    private void ShotNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ShotDraftChanged();
    private void ShotSpeedChanged(object sender, SelectionChangedEventArgs args) => ShotDraftChanged();
    private void ShotCameraViewChanged(object sender, SelectionChangedEventArgs args) => ShotDraftChanged();
    private void ShotMeterChanged(object sender, RoutedEventArgs args) => ShotDraftChanged();

    private void ShotBlend1Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateShotBlend(firstChanged: true);
    private void ShotBlend2Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateShotBlend(firstChanged: false);

    private void UpdateShotBlend(bool firstChanged)
    {
        if (_restoringShotProfile || _shotDraftOriginal is null) return;
        double changed = firstChanged ? ShotRelease1Percent.Value : ShotRelease2Percent.Value;
        if (double.IsFinite(changed) && changed is >= 0 and <= 100)
        {
            _restoringShotProfile = true;
            try
            {
                if (firstChanged) ShotRelease2Percent.Value = 100 - changed;
                else ShotRelease1Percent.Value = 100 - changed;
            }
            finally { _restoringShotProfile = false; }
        }
        ShotDraftChanged();
    }

    private void ShotDraftChanged()
    {
        if (_restoringShotProfile || _shotDraftOriginal is null)
            return;
        _shotDraftDirty = true;
        UpdateShotProfileActions();
        ShotProfileChanged?.Invoke(null);
        ShotProfileStatus.Text = "Unsaved shot settings. Save or revert them before using this profile in Vision.";
    }

    private void UpdateShotProfileActions()
    {
        if (ShotPlayerHeight is null) return; // XAML value events can occur during InitializeComponent.
        bool editable = !_shotProfileOperation;
        ShotProfilePicker.IsEnabled = editable;
        ShotProfileName.IsEnabled = ShotPlayerHeight.IsEnabled = ShotThreePointRating.IsEnabled =
            ShotBase.IsEnabled = ShotUpperRelease1.IsEnabled = ShotUpperRelease2.IsEnabled =
            ShotRelease1Percent.IsEnabled = ShotRelease2Percent.IsEnabled = ShotSpeed.IsEnabled = ShotCameraView.IsEnabled = ShotUsesMeter.IsEnabled = editable;
        ShotMeterReleaseType.IsEnabled = editable && ShotUsesMeter.IsOn;
        NewShotProfileButton.IsEnabled = editable;
        SaveShotProfileButton.IsEnabled = editable && (_shotDraftDirty || _selectedShotProfile is null);
        RevertShotProfileButton.IsEnabled = editable && _shotDraftDirty;
        DuplicateShotProfileButton.IsEnabled = DeleteShotProfileButton.IsEnabled = editable && !_shotDraftDirty && _selectedShotProfile is not null;
        double height = ShotPlayerHeight.Value;
        ShotHeightHint.Text = double.IsFinite(height) && height == Math.Truncate(height)
            ? $"{(int)height / 12} ft {(int)height % 12} in. Height and rating describe this build; animation names are entered manually."
            : "Enter player height as a whole number of inches.";
    }

    private Nba2K27ShotProfile ReadShotDraft()
    {
        var original = _shotDraftOriginal ?? Nba2K27ShotProfile.Create();
        if (!double.IsFinite(ShotPlayerHeight.Value) || ShotPlayerHeight.Value != Math.Truncate(ShotPlayerHeight.Value) ||
            !double.IsFinite(ShotThreePointRating.Value) || ShotThreePointRating.Value != Math.Truncate(ShotThreePointRating.Value))
            throw new InvalidDataException("Player height and three-point rating must be whole numbers.");
        if (!double.IsFinite(ShotRelease1Percent.Value) || ShotRelease1Percent.Value != Math.Truncate(ShotRelease1Percent.Value) ||
            !double.IsFinite(ShotRelease2Percent.Value) || ShotRelease2Percent.Value != Math.Truncate(ShotRelease2Percent.Value) ||
            ShotRelease1Percent.Value + ShotRelease2Percent.Value != 100)
            throw new InvalidDataException("Upper-release percentages must be whole numbers that total 100%.");
        if (ShotSpeed.SelectedIndex is < 0 or > 3)
            throw new InvalidDataException("Choose one of the four shot speeds.");
        if (ShotCameraView.SelectedItem is not ComboBoxItem { Tag: string cameraView } ||
            cameraView is not ("2K" or "2K Low" or "2K Legacy"))
            throw new InvalidDataException("Choose the 2K, 2K Low, or 2K Legacy camera view.");
        var result = original with
        {
            Name = ShotProfileName.Text.Trim(),
            HeightInches = (int)ShotPlayerHeight.Value,
            ThreePointRating = (int)ShotThreePointRating.Value,
            Base = ShotBase.Text.Trim(),
            UpperRelease1 = ShotUpperRelease1.Text.Trim(),
            UpperRelease2 = ShotUpperRelease2.Text.Trim(),
            Release1Percent = (int)ShotRelease1Percent.Value,
            ShotSpeed = ShotSpeed.SelectedIndex + 1,
            CameraView = cameraView,
            UsesShotMeter = ShotUsesMeter.IsOn,
            MeterReleaseType = ShotUsesMeter.IsOn ? ShotMeterReleaseType.Text.Trim() : ""
        };
        result.Validate();
        return result;
    }

    private void NewShotProfileClicked(object sender, RoutedEventArgs args)
    {
        if (_shotDraftDirty)
        {
            ShotProfileStatus.Text = "Save or revert the current edits before creating another shot profile.";
            return;
        }
        _restoringShotProfile = true;
        try { ShotProfilePicker.SelectedItem = null; LoadShotDraft(Nba2K27ShotProfile.Create(), saved: false); }
        finally { _restoringShotProfile = false; }
        ShotProfileChanged?.Invoke(null);
        ShotProfileStatus.Text = "New shot profile. Enter a name, build settings, base and both upper releases, then save.";
        ShotProfileName.Focus(FocusState.Programmatic);
        ShotProfileName.SelectAll();
    }

    private void SaveShotProfileClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var profile = ReadShotDraft();
            _shotProfiles.Save(profile);
            RefreshShotProfiles(profile.Id, chooseFirst: false);
            ShotProfileStatus.Text = $"Saved {profile.Name}. Vision will use this shot configuration; assistance is not armed.";
        }
        catch (Exception error) { ShotProfileStatus.Text = $"Could not save the shot profile. {error.Message}"; }
    }

    private void RevertShotProfileClicked(object sender, RoutedEventArgs args)
    {
        if (_shotDraftOriginal is not { } original) return;
        LoadShotDraft(original, saved: _selectedShotProfile is not null);
        ShotProfileChanged?.Invoke(SelectedShotProfile);
        ShotProfileStatus.Text = "Shot profile edits reverted. Assistance remains off.";
    }

    private void DuplicateShotProfileClicked(object sender, RoutedEventArgs args)
    {
        if (_shotDraftDirty || _selectedShotProfile is not { } selected) return;
        try
        {
            string name = ShotCopyName(selected.Name);
            var copy = _shotProfiles.Duplicate(selected.Id, name);
            RefreshShotProfiles(copy.Id, chooseFirst: false);
            ShotProfileStatus.Text = $"Created {copy.Name}. Edit its settings and save when ready.";
        }
        catch (Exception error) { ShotProfileStatus.Text = $"Could not duplicate the shot profile. {error.Message}"; }
    }

    private string ShotCopyName(string original)
    {
        for (int number = 1; ; number++)
        {
            string suffix = number == 1 ? " copy" : $" copy {number}";
            string prefix = original[..Math.Min(original.Length, 80 - suffix.Length)].TrimEnd();
            if (prefix.Length != 0 && char.IsHighSurrogate(prefix[^1])) prefix = prefix[..^1];
            string candidate = prefix + suffix;
            if (!_savedShotProfiles.Any(profile => string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private async void DeleteShotProfileClicked(object sender, RoutedEventArgs args)
    {
        if (_shotDraftDirty || _shotProfileOperation || _selectedShotProfile is not { } selected || XamlRoot is not { } root)
            return;
        _shotProfileOperation = true;
        UpdateShotProfileActions();
        try
        {
            if (!await StudioDialog.ConfirmDeletionAsync(root, ActualTheme, "shot", selected.Name,
                    "This removes the saved build and jump-shot settings. Recorded gameplay and model files remain on disk.") || !IsLoaded)
                return;
            _shotProfiles.Delete(selected.Id);
            RefreshShotProfiles(null, chooseFirst: true);
            ShotProfileStatus.Text = $"Deleted {selected.Name}.";
        }
        catch (Exception error) { ShotProfileStatus.Text = $"Could not delete the shot profile. {error.Message}"; }
        finally { _shotProfileOperation = false; UpdateShotProfileActions(); }
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
