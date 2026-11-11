using EliteScriptStudio.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace EliteScriptStudio;

public sealed partial class RecorderPanel : UserControl
{
    private static readonly SolidColorBrush IdleIndicator = new(ColorHelper.FromArgb(255, 143, 169, 199));
    private static readonly SolidColorBrush ReadyIndicator = new(ColorHelper.FromArgb(255, 76, 199, 117));
    private static readonly SolidColorBrush RecordingIndicator = new(ColorHelper.FromArgb(255, 249, 95, 106));
    private readonly Dictionary<RecordingChannels, CheckBox> _filters = [];
    private const RecordingChannels WithoutLeftStick = RecordingChannels.All &
        ~(RecordingChannels.LeftStick | RecordingChannels.LeftStickClick);
    private RecordingChannels _included = RecordingChannels.All;
    private bool _syncFilters = true;
    private bool _connected;
    private bool _recording;
    private bool _wideLayout;
    private MacroRecording? _pending;
    private MacroRecording? _replacement;
    private MacroRecording? _saved;
    private string? _savedProfile;
    private bool _hasPending => _pending is not null;
    private int _filterColumns;

    public RecorderPanel()
    {
        InitializeComponent();
        CaptureBar.Maximum = InputRecorder.MaxDurationMs;
        PaddlePicker.ItemsSource = new PaddleOption[]
        {
            new("Leave paddle bindings as they are", null),
            new("Small left paddle · P1", ControlId.SmallLeft),
            new("Small right paddle · P2", ControlId.SmallRight),
            new("Large left paddle · P3", ControlId.LargeLeft),
            new("Large right paddle · P4", ControlId.LargeRight)
        };
        PaddlePicker.SelectedIndex = 0;

        AddGroup("Sticks and triggers",
            ("Left stick X", RecordingChannels.LeftStickX),
            ("Left stick Y", RecordingChannels.LeftStickY),
            ("Right stick X", RecordingChannels.RightStickX),
            ("Right stick Y", RecordingChannels.RightStickY),
            ("Left stick click", RecordingChannels.LeftStickClick),
            ("Right stick click", RecordingChannels.RightStickClick),
            ("Left trigger", RecordingChannels.LeftTrigger),
            ("Right trigger", RecordingChannels.RightTrigger));
        AddGroup("D-pad and bumpers",
            ("D-pad up", RecordingChannels.DPadUp),
            ("D-pad down", RecordingChannels.DPadDown),
            ("D-pad left", RecordingChannels.DPadLeft),
            ("D-pad right", RecordingChannels.DPadRight),
            ("Left bumper", RecordingChannels.LeftBumper),
            ("Right bumper", RecordingChannels.RightBumper));
        AddGroup("Face and center buttons",
            ("A", RecordingChannels.A), ("B", RecordingChannels.B),
            ("X", RecordingChannels.X), ("Y", RecordingChannels.Y),
            ("Menu", RecordingChannels.Menu), ("View", RecordingChannels.View));
        ArrangeFilters(1);
        UpdateFilters();
        ShowState(connected: false, recording: false);
    }

    public event Action<RecordingChannels>? StartRequested;
    public event Action? StopRequested;
    public event Action? RecordAgainRequested;
    public event Action? DiscardRequested;
    public event Action<string, ControlId?>? SaveRequested;
    public event Action<MacroRecording>? SaveJsonRequested;

    public void SelectInputs(RecordingChannels channels) => SetSelection(channels);

    public void ShowState(bool connected, bool recording, bool hasInput = false, int elapsedMs = 0,
        MacroRecording? pending = null, string? note = null, string? profileName = null, MacroRecording? replacement = null)
    {
        bool started = recording && !_recording;
        bool replacementChanged = !ReferenceEquals(replacement, _replacement);
        if (started || (replacementChanged && replacement is not null))
        {
            _saved = null;
            _savedProfile = null;
            FiltersExpander.IsExpanded = false;
        }
        bool pendingChanged = !ReferenceEquals(pending, _pending);
        bool stateChanged = connected != _connected || recording != _recording || pendingChanged || replacementChanged;
        _connected = connected;
        _recording = recording;
        _pending = pending;
        _replacement = replacement;
        string status = recording ? hasInput ? "Capturing input" : "Waiting for included input"
            : _hasPending ? "Recording ready to review"
            : connected ? "Ready to record" : "Connect a controller to start";
        if (CaptureStatus.Text != status)
            CaptureStatus.Text = status;
        CaptureIndicator.Fill = recording ? RecordingIndicator : connected || _hasPending ? ReadyIndicator : IdleIndicator;
        CaptureProgress.Visibility = recording && hasInput ? Visibility.Visible : Visibility.Collapsed;
        if (recording && hasInput)
        {
            int boundedElapsed = Math.Clamp(elapsedMs, 0, InputRecorder.MaxDurationMs);
            CaptureBar.Value = boundedElapsed;
            CaptureTime.Text = $"{boundedElapsed / 1000.0:F1} s / {InputRecorder.MaxDurationMs / 1000.0:F0} s";
        }

        if (recording && !hasInput && note is null)
            note = "Switch to your game when ready. The 15-second limit starts with your first included input.";
        CaptureNote.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
        CaptureNote.Text = note ?? string.Empty;
        string profile = profileName ?? "the active macro profile";
        string destination = replacement is not null ? $"Replace “{replacement.Name}” in {profile}"
            : _saved is not null ? $"Saved to {_savedProfile}" : $"Save to {profile}";
        if (SaveTarget.Text != destination)
            SaveTarget.Text = destination;
        if (pendingChanged || started || replacementChanged)
            UpdateReview();
        if (_pending is null && _saved is null)
        {
            string summary = recording ? "Choose Stop & review to see the captured sequence."
                : replacement is not null ? "Start a new take to replace this recording."
                : "No recording yet. Start a recording and move a selected controller input.";
            if (ReviewSummary.Text != summary)
                ReviewSummary.Text = summary;
        }
        if (stateChanged)
            RefreshActions();
    }

    public void ShowSaved(MacroRecording recording, string profileName)
    {
        _saved = recording;
        _savedProfile = profileName;
    }

    internal void RefreshSavedName(MacroRecording recording)
    {
        if (_saved?.Id != recording.Id)
            return;
        _saved = recording;
        UpdateReview();
        RefreshActions();
    }

    public void ClearSavedPreview(Guid? id = null)
    {
        if (_saved is null || (id.HasValue && _saved.Id != id.Value))
            return;
        _saved = null;
        _savedProfile = null;
        if (_pending is null)
        {
            UpdateReview();
            RefreshActions();
        }
    }

    public void ShowExportStatus(Guid recordingId, string message)
    {
        if (GetOutputRecording()?.Id == recordingId)
            ReviewStatus.Text = message;
    }

    private void UpdateReview()
    {
        NameError.Visibility = Visibility.Collapsed;
        ReviewStatus.Text = _saved is null ? string.Empty : "Available in Input Sequences.";
        var recording = _pending ?? _saved;
        RecordingName.Text = _replacement?.Name ?? recording?.Name ?? string.Empty;
        if (_saved is null)
            PaddlePicker.SelectedIndex = 0;
        if (recording is null)
        {
            RecordingJson.Text = string.Empty;
            return;
        }

        int frames = recording.Frames.Length;
        ReviewSummary.Text = $"{recording.DurationMs / 1000.0:F2} s · {frames:N0} captured {(frames == 1 ? "frame" : "frames")}";
        RecordingJson.Text = ProfileStore.FormatRecording(GetOutputRecording() ?? recording);
        if (_hasPending)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded && ReferenceEquals(_pending, recording))
                {
                    ReviewCard.StartBringIntoView();
                    RecordingName.Focus(FocusState.Programmatic);
                    RecordingName.SelectAll();
                }
            });
        }
    }

    private void RefreshActions()
    {
        bool editable = !_recording && !_hasPending;
        CapturePreset.IsEnabled = editable;
        FiltersExpander.IsEnabled = editable;
        SelectAllFilters.IsEnabled = editable;
        ClearFilters.IsEnabled = editable;
        foreach (var filter in _filters.Values)
            filter.IsEnabled = editable;
        StartButton.IsEnabled = _connected && editable && _included != RecordingChannels.None;
        StopButton.IsEnabled = _recording;
        RecordingName.IsEnabled = _hasPending;
        PaddlePicker.IsEnabled = _hasPending && _replacement is null;
        SaveProfileButton.Content = _replacement is null ? "Save macro" : "Replace macro";
        SaveProfileButton.IsEnabled = _hasPending;
        RecordAgainButton.IsEnabled = _hasPending;
        DiscardButton.Content = _replacement is null ? "Discard recording" : "Cancel re-record";
        DiscardButton.IsEnabled = _hasPending || _replacement is not null;
        CopyJsonButton.IsEnabled = _hasPending || _saved is not null;
        SaveJsonButton.IsEnabled = CopyJsonButton.IsEnabled;
    }

    private void AddGroup(string title, params (string Label, RecordingChannels Channel)[] options)
    {
        var group = new StackPanel { Spacing = 1 };
        group.Children.Add(new TextBlock
        {
            Text = title, Style = (Style)Resources["RecorderHeading"],
            FontSize = 13, TextWrapping = TextWrapping.Wrap
        });
        foreach (var (label, channel) in options)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                MinWidth = 0, MinHeight = 28
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Record {label}");
            box.Checked += FilterChanged;
            box.Unchecked += FilterChanged;
            _filters.Add(channel, box);
            group.Children.Add(box);
        }
        FilterGroups.Children.Add(group);
    }

    private void FilterWidthChanged(object sender, SizeChangedEventArgs args) =>
        ArrangeFilters(args.NewSize.Width >= 540 ? 3 : args.NewSize.Width >= 305 ? 2 : 1);

    private void WorkspaceWidthChanged(object sender, SizeChangedEventArgs args)
    {
        bool wide = args.NewSize.Width >= 780;
        if (_wideLayout == wide)
            return;
        _wideLayout = wide;
        RecorderWorkspace.ColumnDefinitions[0].Width = new GridLength(wide ? 340 : 1,
            wide ? GridUnitType.Pixel : GridUnitType.Star);
        RecorderWorkspace.ColumnDefinitions[1].Width = new GridLength(wide ? 1 : 0,
            wide ? GridUnitType.Star : GridUnitType.Pixel);
        RecorderWorkspace.ColumnSpacing = wide ? 12 : 0;
        Grid.SetColumn(ReviewCard, wide ? 1 : 0);
        Grid.SetRow(ReviewCard, wide ? 0 : 1);
    }

    private void ArrangeFilters(int columns)
    {
        if (columns == _filterColumns)
            return;
        _filterColumns = columns;
        FilterGroups.ColumnDefinitions.Clear();
        FilterGroups.RowDefinitions.Clear();
        for (int column = 0; column < columns; column++)
            FilterGroups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < FilterGroups.Children.Count; i++)
        {
            if (i % columns == 0)
                FilterGroups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var group = (FrameworkElement)FilterGroups.Children[i];
            Grid.SetColumn(group, i % columns);
            Grid.SetRow(group, i / columns);
        }
    }

    private void CapturePresetChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncFilters || CapturePreset.SelectedIndex < 0)
            return;
        if (CapturePreset.SelectedIndex == 3)
        {
            FiltersExpander.IsExpanded = true;
            return;
        }
        FiltersExpander.IsExpanded = false;
        SetSelection(CapturePreset.SelectedIndex switch
        {
            1 => RecordingChannels.RightStick,
            2 => WithoutLeftStick,
            _ => RecordingChannels.All
        });
    }

    private void SelectAllClicked(object sender, RoutedEventArgs args) => SetSelection(RecordingChannels.All);

    private void ClearSelectionClicked(object sender, RoutedEventArgs args) => SetSelection(RecordingChannels.None);

    private void SetSelection(RecordingChannels included)
    {
        _included = included;
        UpdateFilters();
    }

    private void FilterChanged(object sender, RoutedEventArgs args)
    {
        if (_syncFilters)
            return;
        _included = RecordingChannels.None;
        foreach (var (channel, box) in _filters)
            if (box.IsChecked == true)
                _included |= channel;
        UpdateFilters();
    }

    private void UpdateFilters()
    {
        _syncFilters = true;
        foreach (var (channel, box) in _filters)
            box.IsChecked = (_included & channel) != 0;
        CapturePreset.SelectedIndex = _included switch
        {
            RecordingChannels.All => 0,
            RecordingChannels.RightStick => 1,
            WithoutLeftStick => 2,
            _ => 3
        };
        _syncFilters = false;
        int count = _filters.Keys.Count(channel => (_included & channel) != 0);
        FilterSummary.Text = count > 0
            ? $"Records {count} of {_filters.Count} inputs."
            : "Choose at least one input to enable recording.";
        RefreshActions();
    }

    private void StartClicked(object sender, RoutedEventArgs args)
    {
        if (_connected && !_recording && !_hasPending && _included != RecordingChannels.None)
            StartRequested?.Invoke(_included);
    }

    private void StopClicked(object sender, RoutedEventArgs args)
    {
        if (_recording)
            StopRequested?.Invoke();
    }

    private void RecordingNameChanged(object sender, TextChangedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(RecordingName.Text))
            NameError.Visibility = Visibility.Collapsed;
    }

    private void SaveClicked(object sender, RoutedEventArgs args)
    {
        if (!_hasPending)
            return;
        string name = RecordingName.Text.Trim();
        if (name.Length == 0)
        {
            NameError.Visibility = Visibility.Visible;
            RecordingName.Focus(FocusState.Programmatic);
            return;
        }
        SaveRequested?.Invoke(name, _replacement is null ? (PaddlePicker.SelectedItem as PaddleOption)?.Control : null);
    }

    private void RecordAgainClicked(object sender, RoutedEventArgs args)
    {
        if (_hasPending)
            RecordAgainRequested?.Invoke();
    }

    private void DiscardClicked(object sender, RoutedEventArgs args)
    {
        if (_hasPending || _replacement is not null)
            DiscardRequested?.Invoke();
    }

    private void CopyJsonClicked(object sender, RoutedEventArgs args)
    {
        if (GetOutputRecording() is not { } recording)
            return;
        try
        {
            var data = new DataPackage();
            RecordingJson.Text = ProfileStore.FormatRecording(recording);
            data.SetText(RecordingJson.Text);
            Clipboard.SetContent(data);
            ReviewStatus.Text = "Macro JSON copied.";
        }
        catch (Exception error) { ReviewStatus.Text = error.Message; }
    }

    private void SaveJsonClicked(object sender, RoutedEventArgs args)
    {
        if (GetOutputRecording() is { } recording)
        {
            RecordingJson.Text = ProfileStore.FormatRecording(recording);
            SaveJsonRequested?.Invoke(recording);
        }
    }

    private MacroRecording? GetOutputRecording()
    {
        if (_pending is not { } recording)
            return _saved;
        string name = RecordingName.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
            name = _replacement?.Name ?? recording.Name;
        return recording with { Id = _replacement?.Id ?? recording.Id, Name = name };
    }

    private sealed record PaddleOption(string Label, ControlId? Control)
    {
        public override string ToString() => Label;
    }
}
