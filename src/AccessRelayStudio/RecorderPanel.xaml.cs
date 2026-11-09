using EliteScriptStudio.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EliteScriptStudio;

public sealed partial class RecorderPanel : UserControl
{
    private static readonly SolidColorBrush IdleIndicator = new(ColorHelper.FromArgb(255, 143, 169, 199));
    private static readonly SolidColorBrush ReadyIndicator = new(ColorHelper.FromArgb(255, 76, 199, 117));
    private static readonly SolidColorBrush RecordingIndicator = new(ColorHelper.FromArgb(255, 249, 95, 106));
    private readonly Dictionary<RecordingChannels, CheckBox> _filters = [];
    private RecordingChannels _selected;
    private bool _ignoreSelected = true;
    private bool _syncFilters = true;
    private bool _connected;
    private bool _recording;
    private bool _hasPending;
    private int _filterColumns;
    private RecordingChannels IncludedChannels => _ignoreSelected ? RecordingChannels.All & ~_selected : _selected;

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
    public event Action? DiscardRequested;
    public event Action<string, ControlId?>? SaveRequested;

    public void ShowState(bool connected, bool recording, int elapsedMs = 0,
        int? pendingDurationMs = null, int pendingFrames = 0, string? note = null)
    {
        bool hasPending = pendingDurationMs.HasValue && !recording;
        bool stateChanged = connected != _connected || recording != _recording || hasPending != _hasPending;
        _connected = connected;
        _recording = recording;
        _hasPending = hasPending;
        CaptureStatus.Text = recording ? "Recording" : _hasPending ? "Recording ready to save"
            : connected ? "Controller connected" : "Connect a controller to start";
        CaptureIndicator.Fill = recording ? RecordingIndicator : connected || _hasPending ? ReadyIndicator : IdleIndicator;
        CaptureProgress.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording)
        {
            int boundedElapsed = Math.Clamp(elapsedMs, 0, InputRecorder.MaxDurationMs);
            CaptureBar.Value = boundedElapsed;
            CaptureTime.Text = $"{boundedElapsed / 1000.0:F1} s / {InputRecorder.MaxDurationMs / 1000.0:F0} s";
        }

        CaptureNote.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
        CaptureNote.Text = note ?? string.Empty;
        ReviewCard.Visibility = _hasPending ? Visibility.Visible : Visibility.Collapsed;
        if (_hasPending)
            ReviewSummary.Text = $"{pendingDurationMs.GetValueOrDefault() / 1000.0:F2} s · {pendingFrames:N0} captured {(pendingFrames == 1 ? "frame" : "frames")}";
        else
            NameError.Visibility = Visibility.Collapsed;
        if (stateChanged)
            RefreshActions();
    }

    private void RefreshActions()
    {
        bool editable = !_recording && !_hasPending;
        FilterMode.IsEnabled = editable;
        SelectAllFilters.IsEnabled = editable;
        ClearFilters.IsEnabled = editable;
        AllShortcut.IsEnabled = editable;
        RightShortcut.IsEnabled = editable;
        IgnoreLeftShortcut.IsEnabled = editable;
        foreach (var filter in _filters.Values)
            filter.IsEnabled = editable;
        StartButton.Visibility = _recording ? Visibility.Collapsed : Visibility.Visible;
        StartButton.IsEnabled = _connected && editable && IncludedChannels != RecordingChannels.None;
        StopButton.Visibility = _recording ? Visibility.Visible : Visibility.Collapsed;
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
            box.Checked += FilterChanged;
            box.Unchecked += FilterChanged;
            _filters.Add(channel, box);
            group.Children.Add(box);
        }
        FilterGroups.Children.Add(group);
    }

    private void FilterWidthChanged(object sender, SizeChangedEventArgs args) =>
        ArrangeFilters(args.NewSize.Width >= 540 ? 3 : args.NewSize.Width >= 305 ? 2 : 1);

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

    private void FilterModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncFilters || FilterMode.SelectedIndex < 0)
            return;
        _ignoreSelected = FilterMode.SelectedIndex == 1;
        UpdateFilters();
    }

    private void SelectAllClicked(object sender, RoutedEventArgs args) => SetSelection(RecordingChannels.All, _ignoreSelected);

    private void ClearSelectionClicked(object sender, RoutedEventArgs args) => SetSelection(RecordingChannels.None, _ignoreSelected);

    private void AllInputsClicked(object sender, RoutedEventArgs args) => SetSelection(RecordingChannels.None, ignore: true);

    private void RightStickClicked(object sender, RoutedEventArgs args) => SetSelection(RecordingChannels.RightStick, ignore: false);

    private void IgnoreLeftClicked(object sender, RoutedEventArgs args) =>
        SetSelection(RecordingChannels.LeftStick | RecordingChannels.LeftStickClick, ignore: true);

    private void SetSelection(RecordingChannels selected, bool ignore)
    {
        _selected = selected;
        _ignoreSelected = ignore;
        UpdateFilters();
    }

    private void FilterChanged(object sender, RoutedEventArgs args)
    {
        if (_syncFilters)
            return;
        _selected = RecordingChannels.None;
        foreach (var (channel, box) in _filters)
            if (box.IsChecked == true)
                _selected |= channel;
        UpdateFilters();
    }

    private void UpdateFilters()
    {
        _syncFilters = true;
        string action = _ignoreSelected ? "Ignore" : "Include";
        foreach (var (channel, box) in _filters)
        {
            box.IsChecked = (_selected & channel) != 0;
            string label = ((TextBlock)box.Content).Text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"{action} {label}");
        }
        FilterMode.SelectedIndex = _ignoreSelected ? 1 : 0;
        _syncFilters = false;
        var included = IncludedChannels;
        int count = _filters.Keys.Count(channel => (included & channel) != 0);
        string meaning = _ignoreSelected ? "Checked inputs stay manual on playback." : "Only checked inputs are recorded.";
        FilterSummary.Text = count > 0
            ? $"Records {count} of {_filters.Count} inputs. {meaning}"
            : $"No inputs selected for recording. {meaning}";
        RefreshActions();
    }

    private void StartClicked(object sender, RoutedEventArgs args)
    {
        var included = IncludedChannels;
        if (_connected && !_recording && !_hasPending && included != RecordingChannels.None)
            StartRequested?.Invoke(included);
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
        SaveRequested?.Invoke(name, (PaddlePicker.SelectedItem as PaddleOption)?.Control);
    }

    private void DiscardClicked(object sender, RoutedEventArgs args)
    {
        if (_hasPending)
            DiscardRequested?.Invoke();
    }

    private sealed record PaddleOption(string Label, ControlId? Control)
    {
        public override string ToString() => Label;
    }
}
