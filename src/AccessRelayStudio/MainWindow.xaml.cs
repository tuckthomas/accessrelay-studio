using EliteScriptStudio.Packs.Nba2K27;
using EliteScriptStudio.Audio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Diagnostics;
using EliteScriptStudio.Input;
using EliteScriptStudio.Output;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace EliteScriptStudio;

public sealed partial class MainWindow : Window
{
    private enum StudioPage
    {
        Dashboard, Bindings, Parameters, Profiles, Calibration, Monitor, Sequences, Recorder, Lua, Plugins, Vision, Voice, Settings, Updates, Help, About
    }
    private static readonly Brush ConnectedBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 54, 221, 93));
    private static readonly Brush WaitingBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 169, 187, 209));
    private readonly ProfileStore _store = new();
    private StickDeadzone _stickDeadzone;
    private readonly DispatcherQueueTimer _refresh;
    private readonly DispatcherQueueTimer _performanceRefresh;
    private SystemPerformanceMonitor? _performance;
    private StudioRuntime? _runtime;
    private StudioProfile _active = StudioProfile.CreateDefault();
    private StudioProfile _draft;
    private IReadOnlyList<StudioProfile> _macroProfiles = [];
    private ComboBox? _managedMacroProfile;
    private bool _deletingMacroProfile;
    private bool _syncingVoiceProfile;
    private ControlId _selected = ControlId.RightStick;
    private ShotVariant _variant = ShotVariant.Rhythm;
    private bool _showBuiltInTiming;
    private readonly Dictionary<ControlId, ComboBox> _quick = new();
    private TextBlock? _monitor;
    private TextBlock? _probe;
    private int _probeIndex = -1;
    private long _probePulse;
    private static readonly (PaddleButtons Mask, string Label, string Key)[] ProbeOrder =
    [
        (PaddleButtons.SmallLeft, "small left", "Num /"), (PaddleButtons.LargeLeft, "large left", "Num −"),
        (PaddleButtons.SmallRight, "small right", "Num *"), (PaddleButtons.LargeRight, "large right", "Num +")
    ];
    private bool _syncing;
    private bool _syncingFeatureSwitches;
    private bool _dashboardStacked;
    private bool _openingOutput;
    private bool _openingScriptsOutput;
    private CancellationTokenSource? _outputStart;
    private bool _installingVirtualDeviceDriver;
    private bool _closeAfterOpen;
    private bool _closing;
    private string? _scriptsOutputError;
    private long _previewAt;
    private IReadOnlyList<StickPhase> _preview = [];
    private TextBlock? _previewStatus;
    private RecorderPanel? _recorderPanel;
    private LuaScriptPanel? _luaPanel;
    private Nba2K27PluginPanel? _nba2K27Plugin;
    private VisionPanel? _visionPanel;
    private StackPanel? _savedMacroList;
    private StackPanel? _macroNameEditor;
    private MacroRecording? _replacementRecording;
    private Guid? _parameterRecordingId;
    private RecordingEditor? _recordingEditor;
    private bool _exportingRecording;
    private VoicePanel? _voicePanel;
    private string? _recordError;
    private IReadOnlyList<ControllerDevice> _knownDevices = [];
    private string? _chosenDeviceId;
    private string? _chosenDeviceName;
    private ControllerDevice? _currentDevice;
    private ComboBox? _monitorDevicePicker;
    private bool _syncingDevices;

    public MainWindow()
    {
        _draft = _active;
        _stickDeadzone = _store.LoadStickDeadzone();
        InitializeComponent();
        Title = BuildInfo.WindowTitle;
        TitleBrand.Text = BuildInfo.ProductName;
        TitleSubtitle.Text = BuildInfo.Subtitle;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(StudioTitleBar);
        string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AccessRelayStudio.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(1672, display.Width - 24), Math.Min(941, display.Height - 24)));
        var title = AppWindow.TitleBar;
        title.ButtonBackgroundColor = Colors.Transparent;
        title.ButtonInactiveBackgroundColor = Colors.Transparent;
        title.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 38, 50, 68);
        title.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 49, 66, 88);
        title.ForegroundColor = Colors.White;
        title.ButtonForegroundColor = Colors.White;
        title.InactiveForegroundColor = Colors.LightGray;
        title.ButtonInactiveForegroundColor = Colors.LightGray;

        _syncing = true;
        ControlPicker.ItemsSource = ControlMap.All.Select(control => new Choice<ControlId>(control, ControlName(control))).ToArray();
        TimingPicker.ItemsSource = Enum.GetValues<ShotVariant>().Select(variant => new Choice<ShotVariant>(variant, VariantName(variant))).ToArray();
        BuildQuickBindings(elite: false);
        Map.EliteLayoutChanged += (_, elite) =>
        {
            BuildQuickBindings(elite);
            RefreshInspector();
        };
        try
        {
            var profiles = _store.LoadAll();
            if (profiles.Count == 0)
            {
                _store.Save(_active);
                profiles = [_active];
            }
            Guid? selectedId = _store.LoadSelectedId();
            _active = profiles.FirstOrDefault(profile => profile.Id == selectedId)
                ?? profiles.FirstOrDefault(profile => profile.Name == "PC Profile") ?? profiles[0];
            _draft = _active;
            SetProfiles(profiles);
        }
        catch (Exception error)
        {
            SetProfiles([_active]);
            Status.Text = $"Profile storage error. {error.Message}";
        }

        SelectPage(StudioPage.Dashboard);
        TopNavigation.SelectedIndex = -1;
        FillDevicePickers();
        _syncing = false;
        RefreshInspector();
        ShowPage();
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(32);
        _refresh.Tick += RefreshMonitor;
        _performanceRefresh = DispatcherQueue.CreateTimer();
        _performanceRefresh.Interval = TimeSpan.FromSeconds(1);
        _performanceRefresh.Tick += (_, _) => RefreshDashboardPerformance();
        Root.Loaded += StartMonitoring;
        AppWindow.Changed += (_, _) => UpdateVisionPresentationVisibility();
        AppWindow.Closing += (_, args) =>
        {
            if (_nba2K27Plugin?.HasUnsavedShotProfileChanges == true)
            {
                args.Cancel = true;
                SelectPage(StudioPage.Plugins);
                Status.Text = "Save or revert the NBA 2K27 shot profile before closing Studio.";
                return;
            }
            if (_luaPanel?.HasUnsavedChanges == true)
            {
                args.Cancel = true;
                SelectPage(StudioPage.Lua);
                Status.Text = "Save & apply or revert your Lua edits before closing Studio.";
                return;
            }
            if (RecorderInUse())
            {
                args.Cancel = true;
                SelectPage(StudioPage.Recorder);
                Status.Text = "Save or discard the recording before closing Studio.";
                return;
            }
            if (_openingOutput)
            {
                args.Cancel = true;
                _closeAfterOpen = true;
                _outputStart?.Cancel();
            }
        };
        Closed += (_, _) =>
        {
            _closing = true;
            _refresh.Stop();
            _performanceRefresh.Stop();
            _performance?.Dispose();
            _visionPanel?.Dispose();
            try { _voicePanel?.Dispose(); }
            finally
            {
                try { _runtime?.Dispose(); }
                finally { Map.Close(); }
            }
        };
    }

    private void StartMonitoring(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= StartMonitoring;
        try
        {
            _runtime = new StudioRuntime(new EliteInputMonitor(WindowNative.GetWindowHandle(this)), _active, _stickDeadzone);
            _runtime.SetScriptsEnabled(false);
        }
        catch (Exception error)
        {
            Status.Text = $"Controller monitor unavailable. {error.Message}";
            ConnectionLabel.Text = "Monitor unavailable";
        }
        _refresh.Start();
        _performance = new SystemPerformanceMonitor { Enabled = CurrentPage == StudioPage.Dashboard };
        _performanceRefresh.Start();
    }

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private Choice<ScriptBinding>[] Scripts() =>
    [
        .. Enum.GetValues<ScriptKind>().Where(script => script is not (ScriptKind.Recorded or ScriptKind.Lua))
            .Select(script => new Choice<ScriptBinding>(new(script), ScriptName(script))),
        .. _draft.Recordings.Values.OrderBy(recording => recording.Name, StringComparer.OrdinalIgnoreCase)
            .Select(recording => new Choice<ScriptBinding>(
                new(ScriptKind.Recorded, RecordingId: recording.Id), $"{recording.Name} · Recording")),
        .. _draft.LuaScripts.Values.OrderBy(script => script.Name, StringComparer.OrdinalIgnoreCase)
            .Select(script => new Choice<ScriptBinding>(new(ScriptKind.Lua, LuaScriptId: script.Id), $"{script.Name} · Lua"))
    ];

    private static string ScriptName(ScriptKind script) => script switch
    {
        ScriptKind.Rhythm => "Timed stick sequence",
        ScriptKind.IntentionalMiss => "Alternating stick sequence",
        ScriptKind.Moving => "Moving stick sequence",
        ScriptKind.Stepback => "Two-stage stick sequence",
        _ => "Passthrough"
    };

    private string BindingName(ScriptBinding binding) =>
        binding.Script == ScriptKind.Recorded && binding.RecordingId is Guid id
            && _draft.Recordings.TryGetValue(id, out var recording)
            ? recording.Name : binding.Script == ScriptKind.Lua && binding.LuaScriptId is Guid luaId
                && _draft.LuaScripts.TryGetValue(luaId, out var lua) ? lua.Name : ScriptName(binding.Script);

    private MacroRecording? SelectedRecording(StudioProfile profile)
    {
        if (_showBuiltInTiming)
            return null;
        if ((CurrentPage is StudioPage.Parameters or StudioPage.Calibration) && _parameterRecordingId is Guid selected)
            return profile.Recordings.GetValueOrDefault(selected);
        var binding = profile.GetBinding(_selected);
        return binding.Script == ScriptKind.Recorded && binding.RecordingId is Guid id
            ? profile.Recordings.GetValueOrDefault(id) : null;
    }

    private static string VariantName(ShotVariant variant) => variant switch
    {
        ShotVariant.MissEarly => "Alternating stick sequence · Early",
        ShotVariant.MissLate => "Alternating stick sequence · Late",
        ShotVariant.Moving => "Moving stick sequence",
        ShotVariant.Stepback => "Two-stage stick sequence",
        _ => "Timed stick sequence"
    };

    private static string ControlName(ControlId control) => control switch
    {
        ControlId.SmallLeft => "Small Left Paddle (P1)",
        ControlId.SmallRight => "Small Right Paddle (P2)",
        ControlId.LargeLeft => "Large Left Paddle (P3)",
        ControlId.LargeRight => "Large Right Paddle (P4)",
        ControlId.DPadUp => "D-pad up",
        ControlId.DPadDown => "D-pad down",
        ControlId.DPadLeft => "D-pad left",
        ControlId.DPadRight => "D-pad right",
        ControlId.LeftStick => "Left stick click",
        ControlId.RightStick => "Right stick click",
        ControlId.LeftTrigger => "Left trigger",
        ControlId.RightTrigger => "Right trigger",
        ControlId.LeftBumper => "Left bumper",
        ControlId.RightBumper => "Right bumper",
        _ => control.ToString()
    };

    private void MapSelected(object? sender, string tag)
    {
        string id = tag == "D-pad" ? nameof(ControlId.DPadUp) : tag.Replace(" ", "");
        if (Enum.TryParse<ControlId>(id, ignoreCase: true, out var selected))
            SelectControl(selected);
    }

    private void ControlChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncing && ControlPicker.SelectedItem is Choice<ControlId> choice)
            SelectControl(choice.Value);
    }

    private void SelectControl(ControlId selected)
    {
        _selected = selected;
        _parameterRecordingId = null;
        _showBuiltInTiming = false;
        var script = _draft.GetBinding(selected).Script;
        if (script is not (ScriptKind.Passthrough or ScriptKind.Recorded or ScriptKind.Lua))
            _variant = Trajectory.VariantFor(script);
        RefreshInspector();
        RefreshTimingPage();
    }

    private void RefreshInspector()
    {
        _syncing = true;
        ControlPicker.SelectedIndex = Array.IndexOf(ControlMap.All.ToArray(), _selected);
        SelectedControlHint.Text = _selected switch
        {
            ControlId.SmallLeft => "Rear Paddle · Small (Left) · Num /",
            ControlId.SmallRight => "Rear Paddle · Small (Right) · Num *",
            ControlId.LargeLeft => "Rear Paddle · Large (Left) · Num −",
            ControlId.LargeRight => "Rear Paddle · Large (Right) · Num +",
            _ => "Front Control · Physical gamepad input"
        };
        var binding = _draft.GetBinding(_selected);
        var scripts = Scripts();
        SetScriptPicker(Assignment, scripts, binding);
        BindingEnabled.IsChecked = binding.Enabled;
        ConsumeInput.IsChecked = binding.Consume;
        ConsumeInput.IsEnabled = !ControlMap.Paddles.Contains(_selected) && binding.Script != ScriptKind.Passthrough;
        BindingEnabled.IsEnabled = binding.Script != ScriptKind.Passthrough;
        foreach (var (control, picker) in _quick)
            SetScriptPicker(picker, scripts, _draft.GetBinding(control));
        TimingPicker.SelectedIndex = (int)_variant;
        Map.Select(_selected.ToString());
        RefreshBindingHints();
        BuildTimingFields();
        _syncing = false;
    }

    private static void SetScriptPicker(ComboBox picker, Choice<ScriptBinding>[] scripts, ScriptBinding binding)
    {
        picker.ItemsSource = scripts;
        picker.SelectedIndex = Array.FindIndex(scripts, choice =>
            choice.Value.Script == binding.Script && choice.Value.RecordingId == binding.RecordingId
                && choice.Value.LuaScriptId == binding.LuaScriptId);
    }

    private void RefreshBindingHints() =>
        Map.UpdateBindingHints(control =>
        {
            var current = _draft.GetBinding(control);
            return current.Enabled ? BindingName(current) : $"{BindingName(current)} (disabled)";
        }, control => _draft.GetBinding(control) is { Enabled: true, Script: not ScriptKind.Passthrough });

    private void AssignmentChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncing && Assignment.SelectedItem is Choice<ScriptBinding> script)
            Assign(_selected, script.Value);
    }

    private void QuickChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncing && sender is ComboBox { Tag: ControlId control, SelectedItem: Choice<ScriptBinding> script })
            Assign(control, script.Value);
    }

    private void Assign(ControlId control, ScriptBinding selection)
    {
        var before = _draft.GetBinding(control);
        _draft = _draft.WithBinding(control, selection with { Consume = selection.Script != ScriptKind.Passthrough && before.Consume });
        SelectControl(control);
        DraftChanged();
    }

    private void BindingChanged(object sender, RoutedEventArgs args)
    {
        if (_syncing)
            return;
        var binding = _draft.GetBinding(_selected);
        _draft = _draft.WithBinding(_selected, binding with { Enabled = BindingEnabled.IsChecked == true, Consume = ConsumeInput.IsChecked == true });
        RefreshBindingHints();
        DraftChanged();
    }

    private async void EnabledChanged(object sender, RoutedEventArgs args)
    {
        if (_syncingFeatureSwitches || _closing || _runtime is not { } runtime || sender is not ToggleSwitch toggle)
            return;
        _scriptsOutputError = null;
        if (!toggle.IsOn)
        {
            if (_openingScriptsOutput)
            {
                _openingScriptsOutput = false;
                _outputStart?.Cancel();
                Status.Text = "Virtual Xbox output startup cancelled.";
            }
            runtime.SetScriptsEnabled(false);
        }
        else if (runtime.HasOutput)
        {
            try { runtime.SetScriptsEnabled(true); }
            catch (InvalidOperationException error) { Status.Text = _scriptsOutputError = error.Message; }
        }
        else if (!_openingOutput)
        {
            await StartOutputAsync(enableScripts: true);
            return;
        }
        RefreshFeatureControls();
    }

    private void DraftChanged() => Status.Text = "Draft changed · Apply to save and use these settings";

    private void TimingSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_syncing || TimingPicker.SelectedItem is not Choice<ShotVariant> choice)
            return;
        _variant = choice.Value;
        _parameterRecordingId = null;
        BuildTimingFields();
        RefreshTimingPage();
    }

    private void BuildTimingFields()
    {
        TimingInputs.Children.Clear();
        TimingInputs.RowDefinitions.Clear();
        var recording = SelectedRecording(_draft);
        TimingPicker.Visibility = recording is null ? Visibility.Visible : Visibility.Collapsed;
        TimingHint.Text = recording is null
            ? "Timings in milliseconds · 0 is instant down"
            : RecordingTimingHint(recording);
        if (recording is not null)
        {
            AddRecordedTimingFields(recording);
            return;
        }
        var timing = _draft.GetTiming(_variant);
        AddField("Down Hold (ms)", timing.DownHold, 1, ScriptTiming.MaxMs, ScriptTiming.MaxMs, (t, ms) => t with { DownHold = ms });
        AddField("Up Sweep (ms)", timing.UpSweep, 4, ScriptTiming.MaxMs, 360, (t, ms) => t with { UpSweep = ms });
        AddField("Down Sweep (ms)", timing.DownSweep, 0, ScriptTiming.MaxMs, 360, (t, ms) => t with { DownSweep = ms is 1 or 2 ? 3 : ms });
        AddField("Start Delay (ms)", timing.StartDelay, 0, ScriptTiming.MaxMs, 800, (t, ms) => t with { StartDelay = ms });
        AddField("Up Hold (ms)", timing.UpHold, 1, ScriptTiming.MaxMs, 400, (t, ms) => t with { UpHold = ms });
        AddField("Center Hold (ms)", timing.CenterHold, 1, ScriptTiming.MaxMs, 500, (t, ms) => t with { CenterHold = ms });
        if (_variant == ShotVariant.Stepback)
        {
            AddField("Flick Hold (ms)", timing.Flick, 1, ScriptTiming.MaxMs, 360, (t, ms) => t with { Flick = ms });
            AddField("Stepback Delay (ms)", timing.Settle, 1, ScriptTiming.MaxMs, 800, (t, ms) => t with { Settle = ms });
            AddField("Flick X (%)", Percent(timing.FlickX), -100, 100, 100, (t, value) => t with { FlickX = Axis(value) });
            AddField("Flick Y (%)", Percent(timing.FlickY), -100, 100, 100, (t, value) => t with { FlickY = Axis(value) });
        }
    }

    private static string RecordingTimingHint(MacroRecording recording) =>
        $"{recording.Name} · {recording.DurationMs} ms · {recording.Frames.Length} frames";

    private void AddRecordedTimingFields(MacroRecording recording)
    {
        var (downSweep, downHold, upSweep, upHold) = recording.FindRightStickIntervals();
        AddRange("Down Hold (ms)", downHold, InputRecorder.MaxDurationMs);
        AddRange("Up Sweep (ms)", upSweep, 360);
        AddRange("Down Sweep (ms)", downSweep, 360);
        AddRange("Up Hold (ms)", upHold, 400);
        if (TimingInputs.Children.Count == 0)
            AddRange("Duration (ms)", new RecordedInterval(0, recording.Frames.Length), 1500);

        if (CurrentPage != StudioPage.Parameters)
        {
            int row = TimingInputs.RowDefinitions.Count;
            TimingInputs.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var edit = new Button { Content = "Open frame editor", HorizontalAlignment = HorizontalAlignment.Stretch };
            edit.Click += (_, _) => OpenRecordedParameters(recording.Id);
            Grid.SetRow(edit, row);
            TimingInputs.Children.Add(edit);
        }

        void AddRange(string label, RecordedInterval? range, int sliderMaximum)
        {
            if (range is not { } interval)
                return;
            MacroRecording Current() => _draft.Recordings.GetValueOrDefault(recording.Id) ?? recording;
            int Limit(int next)
            {
                var current = Current();
                return Math.Min(next, InputRecorder.MaxDurationMs - current.DurationMs + current.IntervalDuration(interval));
            }
            AddParameterField(label, recording.IntervalDuration(interval), interval.End - interval.Start,
                InputRecorder.MaxDurationMs, Limit(sliderMaximum), next =>
                {
                    var current = Current();
                    if (next != current.IntervalDuration(interval))
                        StageRecordingEdit(current.WithIntervalDuration(interval.Start, interval.End, next));
                }, Limit);
        }
    }

    private void AddField(string label, int value, int minimum, int maximum, int sliderMaximum,
        Func<ScriptTiming, int, ScriptTiming> edit)
    {
        var variant = _variant;
        AddParameterField(label, value, minimum, maximum, sliderMaximum, next =>
        {
            var original = _draft.GetTiming(variant);
            var updated = edit(original, next);
            if (original == updated)
                return;
            _draft = _draft.WithTiming(variant, updated);
            DraftChanged();
            RefreshTimingPage();
        }, next => label == "Down Sweep (ms)" && next is 1 or 2 ? 3 : next);
    }

    private void AddParameterField(string label, int value, int minimum, int maximum, int sliderMaximum,
        Action<int> changed, Func<int, int> normalize)
    {
        int rowIndex = TimingInputs.RowDefinitions.Count;
        TimingInputs.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1.13, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(74) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1.2, GridUnitType.Star) });
        var caption = new TextBlock
        {
            Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(caption, label);
        var number = new NumberBox
        {
            Value = value, Minimum = minimum, Maximum = maximum, Height = 28, MinHeight = 27, MinWidth = 0, FontSize = 13,
            Padding = new Thickness(8, 2, 19, 2), SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Value = value, Minimum = minimum, Maximum = Math.Max(value, sliderMaximum), StepFrequency = 1,
            MinWidth = 30, Height = 27, VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(number, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, label + " slider");
        bool normalizing = false;
        void SetValue(double entered)
        {
            if (normalizing)
                return;
            int next = normalize(double.IsFinite(entered) ? (int)Math.Clamp(Math.Round(entered), minimum, maximum) : minimum);
            normalizing = true;
            number.Value = next;
            if (next > slider.Maximum)
                slider.Maximum = next;
            slider.Value = next;
            normalizing = false;
            changed(next);
        }
        number.ValueChanged += (_, args) => SetValue(double.IsFinite(args.NewValue) ? args.NewValue : args.OldValue);
        slider.ValueChanged += (_, args) => SetValue(args.NewValue);
        row.Children.Add(caption);
        Grid.SetColumn(number, 1);
        row.Children.Add(number);
        Grid.SetColumn(slider, 2);
        row.Children.Add(slider);
        Grid.SetRow(row, rowIndex);
        TimingInputs.Children.Add(row);
    }

    private static short Axis(int percent) => (short)Math.Round(percent * 32767.0 / 100);
    private static int Percent(short axis) => (int)Math.Round(axis * 100.0 / 32767);

    private void Apply(object sender, RoutedEventArgs args) => SaveDraft();

    private void CycleControl(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string direction } || !int.TryParse(direction, out int delta))
            return;
        var choices = ControlMap.Paddles.Contains(_selected) ? ControlMap.Paddles : ControlMap.All;
        int current = Array.IndexOf(choices.ToArray(), _selected);
        SelectControl(choices[(current + delta + choices.Count) % choices.Count]);
    }

    private void ResetBindings(object sender, RoutedEventArgs args)
    {
        _draft = _draft with { Bindings = StudioProfile.CreateDefault().Bindings };
        SelectControl(_selected);
        Status.Text = "Default bindings staged · Apply to save";
    }

    private void DuplicateActiveProfile(object sender, RoutedEventArgs args)
    {
        string name = _draft.Name.Length > 75 ? _draft.Name[..75] : _draft.Name;
        SaveNamed(_draft with { Id = Guid.NewGuid(), Name = name + " copy", ScriptsEnabled = false });
    }

    private void ShowProfilePage(object sender, RoutedEventArgs args) => SelectPage(StudioPage.Profiles);

    private void ShowDeviceMonitorPage(object sender, RoutedEventArgs args) => SelectPage(StudioPage.Monitor);

    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingDevices || sender is not ComboBox { SelectedItem: Choice<string?> choice } || choice.Value == _chosenDeviceId)
            return;
        if (RecorderInUse())
        {
            Status.Text = "Save or discard the recording before switching controllers.";
            FillDevicePickers();
            return;
        }

        _chosenDeviceId = choice.Value;
        _chosenDeviceName = choice.Value is null ? null : choice.Label;
        _runtime?.SelectController(_chosenDeviceId);
        FillDevicePickers();
    }

    private void FillDevicePickers()
    {
        var choices = new List<Choice<string?>> { new(null, "Auto-select controller") };
        choices.AddRange(_knownDevices.Select(device => new Choice<string?>(device.Id, device.Name)));
        if (_chosenDeviceId is not null && choices.All(choice => choice.Value != _chosenDeviceId))
            choices.Add(new(_chosenDeviceId, $"{_chosenDeviceName ?? "Selected controller"} (disconnected)"));

        _syncingDevices = true;
        foreach (var picker in new[] { DevicePicker, _monitorDevicePicker })
        {
            if (picker is null)
                continue;
            picker.ItemsSource = choices;
            picker.SelectedIndex = choices.FindIndex(choice => choice.Value == _chosenDeviceId);
        }
        _syncingDevices = false;
    }

    private void ApplyAndTest(object sender, RoutedEventArgs args)
    {
        if (!SaveDraft())
            return;
        _preview = BuildPreview(_active).Phases;
        _previewAt = Environment.TickCount64;
        SelectPage(StudioPage.Calibration);
        ShowPage();
        Status.Text = "Saved · Local simulation uses the same phases as the macro engine";
    }

    private bool SaveDraft()
    {
        if (BlockDuringVisionTraining()) return false;
        if (_runtime?.Recording.IsRecording == true)
        {
            Status.Text = "Stop the recording before applying profile changes.";
            return false;
        }
        if (_recordingEditor is { } editor && !editor.TryGetResult(out _))
        {
            Status.Text = "Update or reset the edited fields before applying.";
            return false;
        }
        try
        {
            foreach (var script in _draft.LuaScripts.Values)
                _ = new LuaControllerProgram(script);
            if (_draft.Recordings != _active.Recordings || _draft.LuaScripts != _active.LuaScripts)
            {
                _store.SaveRecordingEdit(_active, _draft);
                foreach (var (id, recording) in _draft.Recordings)
                    if (_active.Recordings.GetValueOrDefault(id) != recording)
                        _recorderPanel?.ClearSavedPreview(id);
            }
            else
                _store.Save(_draft);
            _runtime?.Apply(_draft);
            _active = _draft;
            SetProfiles(_store.LoadAll());
            RefreshSavedMacros();
            ShowProfileNotice(DashboardMacroProfileNotice, "");
            Status.Text = $"Saved and applied {_active.Name}";
            return true;
        }
        catch (Exception error)
        {
            Status.Text = $"Couldn’t save the profile. {error.Message}";
            return false;
        }
    }

    private void Revert(object sender, RoutedEventArgs args)
    {
        if (BlockDuringVisionTraining()) return;
        _draft = _active;
        _runtime?.Apply(_active);
        ShowProfileNotice(DashboardMacroProfileNotice, "");
        RefreshInspector();
        RefreshTimingPage();
        Status.Text = $"Restored saved settings for {_active.Name}";
    }

    private void SetProfiles(IReadOnlyList<StudioProfile> profiles)
    {
        _macroProfiles = profiles;
        bool previous = _syncing;
        _syncing = true;
        try
        {
            SyncMacroPicker(DashboardMacroProfile);
            if (_managedMacroProfile is { } picker)
                SyncMacroPicker(picker);
        }
        finally { _syncing = previous; }
    }

    private void SyncMacroPicker(ComboBox picker)
    {
        if (!ReferenceEquals(picker.ItemsSource, _macroProfiles))
            picker.ItemsSource = _macroProfiles;
        picker.SelectedItem = _macroProfiles.FirstOrDefault(profile => profile.Id == _active.Id);
    }

    private void ProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncing || sender is not ComboBox { SelectedItem: StudioProfile profile } || profile.Id == _active.Id)
            return;
        string? blocker = _runtime?.IsVisionTrainingRecording == true ? "Finish the gameplay training take before switching macro profiles."
            : RecorderInUse() ? "Finish and save or discard the recording before switching macro profiles."
            : _luaPanel?.HasUnsavedChanges == true ? "Save & apply or revert your Lua edits before switching macro profiles."
            : _draft != _active ? "Apply or revert your bindings draft before switching macro profiles."
            : null;
        if (blocker is not null)
        {
            SetProfiles(_macroProfiles);
            ShowProfileNotice(DashboardMacroProfileNotice, blocker);
            Status.Text = blocker;
            return;
        }
        try { ActivateProfile(profile); }
        catch (Exception error)
        {
            SetProfiles(_macroProfiles);
            string message = $"Couldn’t switch macro profiles. {error.Message}";
            ShowProfileNotice(DashboardMacroProfileNotice, message);
            Status.Text = message;
        }
    }

    private void ActivateProfile(StudioProfile profile, IReadOnlyList<StudioProfile>? profiles = null)
    {
        RequireVisionTrainingStopped();
        profiles ??= _store.LoadAll();
        _runtime?.Apply(profile);
        if (_active.Id != profile.Id)
        {
            _replacementRecording = null;
            _parameterRecordingId = null;
            _recorderPanel?.ClearSavedPreview();
        }
        _active = _draft = profile;
        SetProfiles(profiles);
        RefreshInspector();
        ShowPage();
        string notice = "";
        try { _store.SaveSelectedId(profile.Id); }
        catch (Exception error) { notice = $"Couldn’t remember this macro profile. {error.Message}"; }
        ShowProfileNotice(DashboardMacroProfileNotice, notice);
        Status.Text = notice.Length == 0 ? $"Applied macro profile {profile.Name}" : notice;
    }

    private async void ToggleOutput(object sender, RoutedEventArgs args)
    {
        if (_runtime is not { } runtime || _openingOutput || _closing)
            return;
        if (runtime.HasOutput)
        {
            _scriptsOutputError = null;
            runtime.SetScriptsEnabled(false);
            runtime.StopOutput(stopScripts: false);
            Status.Text = "Virtual output disconnected";
            RefreshFeatureControls();
            return;
        }
        await StartOutputAsync(enableScripts: false);
    }

    private async Task StartOutputAsync(bool enableScripts)
    {
        if (_runtime is not { } runtime || _openingOutput || _closing)
            return;
        if (enableScripts && BlockDuringVisionTraining()) { RefreshFeatureControls(); return; }
        _openingOutput = true;
        _openingScriptsOutput = enableScripts;
        using var start = new CancellationTokenSource();
        _outputStart = start;
        _scriptsOutputError = null;
        runtime.SetScriptsEnabled(false);
        Status.Text = "Starting the virtual Xbox controller…";
        RefreshFeatureControls();
        IControllerOutput? output = null;
        try
        {
            runtime.ReserveVirtualOutput();
            output = await VirtualOutput.TryOpenAsync(start.Token);
            if (_closing || start.IsCancellationRequested)
                return;

            string result = output.Status;
            bool available = output.IsAvailable;
            runtime.Attach(output);
            output = null;
            if (enableScripts)
            {
                if (runtime.IsVisionTrainingRecording)
                {
                    _scriptsOutputError = "Virtual output connected. Macro scripts remain off until the gameplay training take finishes.";
                    result = _scriptsOutputError;
                }
                else if (runtime.HasOutput)
                    runtime.SetScriptsEnabled(true);
                else
                    _scriptsOutputError = available ? runtime.Latest?.OutputStatus ?? result : result;
            }
            Status.Text = result;
        }
        catch (OperationCanceledException) when (start.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing && !start.IsCancellationRequested)
            {
                string message = $"Could not start virtual Xbox output. {error.Message}";
                Status.Text = message;
                if (enableScripts)
                    _scriptsOutputError = message;
            }
        }
        finally
        {
            try { output?.Dispose(); }
            finally
            {
                runtime.ReleaseVirtualOutputReservation();
                _outputStart = null;
                _openingOutput = _openingScriptsOutput = false;
                if (_closeAfterOpen)
                    Close();
                else if (!_closing)
                    RefreshFeatureControls();
            }
        }
    }

    private void StopAll(object sender, RoutedEventArgs args)
    {
        _visionPanel?.Stop();
        _outputStart?.Cancel();
        _openingScriptsOutput = false;
        _scriptsOutputError = null;
        _runtime?.StopOutput(stopScripts: true);
        _voicePanel?.Stop();
        RefreshFeatureControls();
        RefreshRecorder();
        Status.Text = "Vision, voice and scripts stopped · Virtual Xbox output disconnected";
    }

    private bool IsWindowPresenting() => !_closing && AppWindow.IsVisible &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void UpdateVisionPresentationVisibility()
    {
        if (_visionPanel is not null)
            _visionPanel.SetPageVisible(CurrentPage == StudioPage.Vision && IsWindowPresenting());
    }

    private void RefreshMonitor(DispatcherQueueTimer sender, object args)
    {
        UpdateVisionPresentationVisibility();
        _visionPanel?.SetProfile(_active.Id, _active.Name);
        _visionPanel?.RefreshStatus();
        RefreshRecorder();
        var micBadge = _voicePanel?.UpdateStatus() == true ? Visibility.Visible : Visibility.Collapsed;
        if (VoiceLiveBadge.Visibility != micBadge)
            VoiceLiveBadge.Visibility = micBadge;
        if (_runtime?.Latest is { } sample)
        {
            _luaPanel?.ShowRuntime(sample.Macro);
            var input = sample.Input;
            UpdateDeviceDisplay(input);
            if (CurrentPage == StudioPage.Bindings)
            {
                Map.ShowInput(input.IsConnected ? input.State : default, input.IsConnected ? sample.VisibleControls : 0,
                    input.IsConnected);
                CenterFooter.Text = input.IsConnected
                    ? $"Live input  ·  LS {Percent(input.State.LeftX)}, {Percent(input.State.LeftY)}  ·  RS {Percent(input.State.RightX)}, {Percent(input.State.RightY)}  ·  LT {input.State.LeftTrigger * 100 / 255}%  ·  RT {input.State.RightTrigger * 100 / 255}%"
                    : "Connect a gamepad to see live input.";
            }
            ConnectionLabel.Text = input.IsConnected ? "● Connected" : "○ Waiting for input";
            ConnectionLabel.Foreground = input.IsConnected ? ConnectedBrush : WaitingBrush;
            TransportLabel.Text = input.IsPaddleInterfacePresent ? "USB · Elite keypad" : input.Device?.Transport ?? "";
            if (_monitor is not null)
            {
                string pressed = string.Join(", ", ControlMap.All.Where(control => ControlMap.IsPressed(control, input.State, input.HeldPaddles)).Select(ControlName));
                string readout = $"PHYSICAL CONTROLLER · RAW STICK READINGS\n{input.Source}\n{FormatState(input.State)}\n" +
                    $"Distance from center  L {StickDeadzone.DistancePercent(input.State.LeftX, input.State.LeftY):F1}%  " +
                    $"R {StickDeadzone.DistancePercent(input.State.RightX, input.State.RightY):F1}%\n" +
                    $"Pressed  {(pressed.Length == 0 ? "—" : pressed)}\n\nPADDLE SOURCE\n{input.PaddleSource}\n" +
                    $"Held  {input.HeldPaddles}    Last press  {sample.LastPaddles}\n\nVIRTUAL XBOX OUTPUT\n{sample.OutputStatus}\n" +
                    (sample.OutputActive ? $"Last submitted report\n{FormatState(sample.Output)}\n" : "Computed preview\n" + FormatState(sample.Output) + "\n") +
                    $"\nLOCAL POLL INTERVAL\nMean {sample.MeanPollMs:F1} ms · p95 {sample.P95PollMs:F1} ms · p99 {sample.P99PollMs:F1} ms";
                if (_monitor.Text != readout)
                    _monitor.Text = readout;
            }
            if (_probe is not null && _probeIndex >= 0 && sample.PaddlePulse > _probePulse)
            {
                _probePulse = sample.PaddlePulse;
                var expected = ProbeOrder[_probeIndex];
                if (sample.LastPaddles == expected.Mask)
                {
                    _probeIndex++;
                    _probe.Text = _probeIndex == ProbeOrder.Length
                        ? "All four PC Profile key mappings observed."
                        : $"Saw {expected.Key}. Press the {ProbeOrder[_probeIndex].Label} paddle next.";
                    if (_probeIndex == ProbeOrder.Length)
                        _probeIndex = -1;
                }
                else
                    _probe.Text = $"Saw {sample.LastPaddles}. Press the {expected.Label} paddle ({expected.Key}).";
            }
        }
        RefreshFeatureControls();
        if (CurrentPage == StudioPage.Dashboard)
            RefreshDashboardVoiceProfiles();
        if (_previewStatus is not null && _preview.Count > 0)
        {
            long elapsed = Environment.TickCount64 - _previewAt;
            int boundary = 0;
            foreach (var phase in _preview)
            {
                boundary += phase.DurationMs;
                if (elapsed < boundary)
                {
                    string x = phase.RightX is short rightX ? $" · X {Percent(rightX)}%" : "";
                    _previewStatus.Text = $"Simulation · {elapsed} ms · {phase.Label} · Y {Percent(phase.RightY)}%{x}";
                    return;
                }
            }
            _previewStatus.Text = "Simulation complete · Stick centered";
            _preview = [];
        }
    }

    private void BuildQuickBindings(bool elite)
    {
        foreach (var row in _quick.Values)
            QuickBindings.Children.Remove((UIElement)row.Parent);
        _quick.Clear();
        ControlId[] controls = elite
            ? [ControlId.LargeRight, ControlId.LargeLeft, ControlId.SmallRight, ControlId.SmallLeft]
            : [ControlId.A, ControlId.B, ControlId.X, ControlId.Y];
        foreach (var control in controls)
        {
            var combo = new ComboBox
            {
                MinHeight = 27, Padding = new Thickness(10, 2, 28, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch, Tag = control,
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 70, 82, 99))
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, $"{ControlName(control)} script");
            combo.SelectionChanged += QuickChanged;
            var row = new Grid { ColumnSpacing = 7 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1.2, GridUnitType.Star) });
            var caption = new TextBlock
            {
                Text = ControlName(control), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13
            };
            ToolTipService.SetToolTip(caption, caption.Text);
            row.Children.Add(caption);
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            QuickBindings.Children.Add(row);
            _quick.Add(control, combo);
        }
    }

    private void UpdateDeviceDisplay(ControllerSample sample)
    {
        if (!_knownDevices.SequenceEqual(sample.Devices))
        {
            _knownDevices = sample.Devices;
            FillDevicePickers();
        }
        if (_currentDevice == sample.Device)
            return;
        _currentDevice = sample.Device;
        DeviceName.Text = _currentDevice?.Name ?? "Controller";
        Map.UseDevice(_currentDevice);
    }

    private static string FormatState(ControllerState state) =>
        $"L {state.LeftX,6} / {state.LeftY,6}    R {state.RightX,6} / {state.RightY,6}\nLT {state.LeftTrigger,3}   RT {state.RightTrigger,3}   Buttons 0x{state.Buttons:X4}";

    private StudioPage CurrentPage => (StudioPage)Navigation.SelectedIndex;

    private void SelectPage(StudioPage page) => Navigation.SelectedIndex = (int)page;

    private void Navigate(object sender, SelectionChangedEventArgs args)
    {
        if (_syncing)
            return;
        _syncing = true;
        int tab = Navigation.SelectedIndex - (int)StudioPage.Bindings;
        TopNavigation.SelectedIndex = tab >= 0 && tab < TopNavigation.Items.Count ? tab : -1;
        _syncing = false;
        BuildTimingFields();
        ShowPage();
    }

    private void TopNavigate(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncing && TopNavigation.SelectedIndex >= 0)
            SelectPage((StudioPage)((int)StudioPage.Bindings + TopNavigation.SelectedIndex));
    }

    private void SizeTopNavigation(object sender, SizeChangedEventArgs args)
    {
        if (sender is not ListView { Items.Count: 5 } tabs)
            return;
        double contentWidth = args.NewSize.Width - 52;
        double[] proportions = contentWidth >= 535 ? [.168, .193, .181, .212, .246] : [.2, .2, .2, .2, .2];
        for (int index = 0; index < tabs.Items.Count; index++)
        {
            if (tabs.Items[index] is not ListViewItem tab)
                continue;
            tab.Width = Math.Max(45, Math.Floor(contentWidth * proportions[index]));
            tab.Padding = new Thickness(contentWidth < 690 ? 6 : 12, 0, contentWidth < 690 ? 6 : 12, 0);
        }
    }

    private void RefreshTimingPage()
    {
        if (CurrentPage is StudioPage.Parameters or StudioPage.Calibration or StudioPage.Sequences)
            ShowPage();
    }

    private void ArrangeDashboardCards(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 850;
        if (stacked == _dashboardStacked)
            return;
        _dashboardStacked = stacked;
        DashboardRightColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(DashboardVoiceCard, stacked ? 0 : 1);
        Grid.SetRow(DashboardVoiceCard, stacked ? 1 : 0);
        Grid.SetRow(DashboardPerformanceCard, stacked ? 2 : 1);
    }

    private void RefreshDashboardPerformance()
    {
        if (_closing || CurrentPage != StudioPage.Dashboard || _performance is not { } monitor)
            return;

        var stats = monitor.Latest;
        ShowPerformanceMeter(DashboardCpuValue, DashboardCpuMeter, stats.CpuPercent);
        ShowMemoryMeter(DashboardRamValue, DashboardRamMeter, stats.MemoryUsedBytes, stats.MemoryTotalBytes);
        ShowPerformanceMeter(DashboardGpuValue, DashboardGpuMeter, stats.GpuPercent);
        ShowMemoryMeter(DashboardVramValue, DashboardVramMeter, stats.VramUsedBytes, stats.VramTotalBytes,
            stats.GpuName is not null && stats.VramTotalBytes is null ? "No dedicated VRAM" : "Unavailable");
        DashboardGpuAdapter.Visibility = stats.GpuName is null ? Visibility.Collapsed : Visibility.Visible;
        DashboardGpuAdapter.Text = stats.GpuName is { } name ? $"{name} · Busiest engine and dedicated memory" : string.Empty;
        DashboardPollValue.Text = _runtime?.Latest is { MeanPollMs: > 0 } sample
            ? $"Mean {sample.MeanPollMs:F2} ms  ·  P95 {sample.P95PollMs:F2} ms  ·  P99 {sample.P99PollMs:F2} ms"
            : "Waiting for input samples";
        DashboardOutputHandoff.Text = _runtime?.HasOutput == true
            ? _runtime.RecentOutputHandoffMs is { } milliseconds ? $"{milliseconds:F2} ms" : "Waiting for a changed report"
            : "Virtual Xbox output is off";
    }

    private static void ShowPerformanceMeter(TextBlock text, ProgressBar meter, double? percent, string? detail = null)
    {
        text.Text = detail ?? (percent is { } value ? $"{value:F1}%" : "Unavailable");
        meter.Visibility = percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        meter.Value = Math.Clamp(percent ?? 0, 0, 100);
    }

    private static void ShowMemoryMeter(TextBlock text, ProgressBar meter, ulong? used, ulong? total, string unavailable = "Unavailable")
    {
        const double gibibyte = 1024.0 * 1024 * 1024;
        if (used is { } usedBytes && total is > 0)
        {
            double percent = 100.0 * usedBytes / total.Value;
            ShowPerformanceMeter(text, meter, percent,
                $"{usedBytes / gibibyte:F1} / {total.Value / gibibyte:F1} GiB ({percent:F0}%)");
        }
        else
            ShowPerformanceMeter(text, meter, null, unavailable);
    }

    private VoicePanel GetVoicePanel() => _voicePanel ??= new VoicePanel(AppWindow.Id);

    private void RefreshFeatureControls()
    {
        _syncingFeatureSwitches = true;
        try
        {
            var runtime = _runtime;
            bool output = runtime?.HasOutput == true;
            var sample = runtime?.Latest;
            bool scriptsEnabled = runtime?.ScriptsEnabled == true;
            if (!output && scriptsEnabled)
            {
                runtime!.SetScriptsEnabled(false);
                scriptsEnabled = false;
                _scriptsOutputError = sample?.OutputStatus;
            }
            VirtualOutputMenuItem.IsEnabled = runtime is not null && !_openingOutput;
            VirtualOutputMenuItem.Text = _openingOutput ? "Starting virtual Xbox output…"
                : output ? "Stop virtual Xbox output" : "Start virtual Xbox output";
            bool training = runtime?.IsVisionTrainingRecording == true;
            EnableScripts.IsEnabled = DashboardScripts.IsEnabled = runtime is not null && !training && (!_openingOutput || _openingScriptsOutput);
            EnableScripts.IsOn = DashboardScripts.IsOn = scriptsEnabled || _openingScriptsOutput;
            if (CurrentPage != StudioPage.Dashboard)
                return;

            string scriptsMessage = (runtime is not null, scriptsEnabled, output) switch
            {
                (false, _, _) => "Controller monitor unavailable.",
                _ when training => "Off during the gameplay training take. Only manual controller input is captured.",
                _ when _openingScriptsOutput => "Starting the virtual Xbox controller for macros…",
                _ when _outputStart?.IsCancellationRequested == true => "Cancelling virtual Xbox output startup…",
                _ when _openingOutput => "Starting virtual Xbox output for controller passthrough…",
                _ when _scriptsOutputError is { } error => error,
                _ when sample?.Macro.Error is { } luaError => luaError,
                (_, false, true) => "Off. The virtual Xbox controller passes through your physical input.",
                (_, false, false) => "Off. Your controller bindings are ready when you turn scripts on.",
                (_, true, _) when runtime?.Recording.IsRecording == true => "Paused during macro recording.",
                _ when sample?.Input.IsConnected != true => "On. Connect a controller to run scripts.",
                _ => "On. Scripts are available through the virtual Xbox controller."
            };
            if (DashboardScriptsStatus.Text != scriptsMessage)
                DashboardScriptsStatus.Text = scriptsMessage;

            var voice = GetVoicePanel().GetLiveVoiceState();
            DashboardVoice.IsOn = voice.IsStarting || voice.IsRunning;
            DashboardVoice.IsEnabled = DashboardVoice.IsOn || voice.CanStart;
            if (DashboardVoiceStatus.Text != voice.Message)
                DashboardVoiceStatus.Text = voice.Message;
        }
        finally
        {
            _syncingFeatureSwitches = false;
        }
    }

    private async void DashboardVoiceChanged(object sender, RoutedEventArgs args)
    {
        if (_syncingFeatureSwitches || _closing)
            return;
        await GetVoicePanel().SetLiveVoiceEnabledAsync(DashboardVoice.IsOn);
        if (!_closing)
            RefreshFeatureControls();
    }

    private void OpenBindings(object sender, RoutedEventArgs args) => SelectPage(StudioPage.Bindings);

    private void OpenVoice(object sender, RoutedEventArgs args) => SelectPage(StudioPage.Voice);

    private async void DeleteDashboardMacroProfile(object sender, RoutedEventArgs args)
    {
        if (DashboardMacroProfile.SelectedItem is StudioProfile profile && profile.Id == _active.Id)
            await DeleteMacroProfileAsync();
    }

    private async void DeleteDashboardVoiceProfile(object sender, RoutedEventArgs args)
    {
        if (_closing || Root.XamlRoot is not { } root || DashboardVoiceProfile.SelectedItem is not VoiceProfile profile)
            return;
        await GetVoicePanel().DeleteSelectedProfileAsync(root, Root.ActualTheme, profile.Id);
        if (!_closing && CurrentPage == StudioPage.Dashboard)
            RefreshDashboardVoiceProfiles();
    }

    private void RefreshDashboardVoiceProfiles()
    {
        var panel = GetVoicePanel();
        var voice = panel.GetVoiceProfileState();
        _syncingVoiceProfile = true;
        try
        {
            if (!ReferenceEquals(DashboardVoiceProfile.ItemsSource, voice.Profiles))
                DashboardVoiceProfile.ItemsSource = voice.Profiles;
            if (!voice.IsLoading && (DashboardVoiceProfile.SelectedItem as VoiceProfile)?.Id != voice.SelectedProfileId)
                DashboardVoiceProfile.SelectedItem = voice.Profiles.FirstOrDefault(profile => profile.Id == voice.SelectedProfileId);
            DashboardVoiceProfile.IsEnabled = voice.CanSelect;
            DashboardDeleteVoiceProfile.IsEnabled = voice.SelectedProfileId is not null && panel.CanDeleteSelectedProfile;
            ShowProfileNotice(DashboardVoiceProfileNotice, voice.Message);
        }
        finally { _syncingVoiceProfile = false; }
    }

    private async void DashboardVoiceProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingVoiceProfile || _closing || DashboardVoiceProfile.SelectedItem is not VoiceProfile profile)
            return;
        await GetVoicePanel().SelectVoiceProfileAsync(profile.Id);
        if (!_closing && CurrentPage == StudioPage.Dashboard)
            RefreshDashboardVoiceProfiles();
    }

    private static void ShowProfileNotice(TextBlock notice, string message)
    {
        if (notice.Text != message)
            notice.Text = message;
        notice.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowPage()
    {
        CloseMacroNameEditor();
        Details.Children.Clear();
        _savedMacroList = null;
        _recordingEditor = null;
        _managedMacroProfile = null;
        _monitor = null;
        _monitorDevicePicker = null;
        _probe = null;
        _probeIndex = -1;
        _previewStatus = null;
        StudioPage page = CurrentPage;
        _visionPanel?.SetPageVisible(page == StudioPage.Vision && IsWindowPresenting());
        if (_refresh is { } refresh)
            refresh.Interval = TimeSpan.FromMilliseconds(page == StudioPage.Monitor ? 16 : 32);
        if (_performance is { } performance)
            performance.Enabled = page == StudioPage.Dashboard;
        MacroProfileActions.Visibility = page is StudioPage.Bindings or StudioPage.Parameters or StudioPage.Profiles
            or StudioPage.Calibration or StudioPage.Sequences or StudioPage.Recorder or StudioPage.Lua ? Visibility.Visible : Visibility.Collapsed;
        bool fullWidth = page is StudioPage.Dashboard or StudioPage.Voice or StudioPage.Recorder or StudioPage.Lua or StudioPage.Plugins or StudioPage.Sequences or StudioPage.Vision;
        BindingsInspector.Visibility = fullWidth ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(WorkspacePanel, fullWidth ? 2 : 1);
        MapFooter.Visibility = fullWidth ? Visibility.Collapsed : Visibility.Visible;
        int target = page is StudioPage.Parameters or StudioPage.Calibration ? 0 : InspectorCards.Children.Count - 1;
        if (InspectorCards.Children.IndexOf(TimingCard) != target)
        {
            InspectorCards.Children.Remove(TimingCard);
            InspectorCards.Children.Insert(target, TimingCard);
        }
        Map.Visibility = page == StudioPage.Bindings ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.Visibility = page is StudioPage.Bindings or StudioPage.Dashboard or StudioPage.Vision ? Visibility.Collapsed : Visibility.Visible;
        VisionHost.Visibility = page == StudioPage.Vision ? Visibility.Visible : Visibility.Collapsed;
        DashboardScroll.Visibility = page == StudioPage.Dashboard ? Visibility.Visible : Visibility.Collapsed;
        CenterFooter.Text = page switch
        {
            StudioPage.Profiles => "Profiles are stored locally · Apply saves in place",
            StudioPage.Monitor => "Choose the physical controller used by the recorder and virtual output",
            StudioPage.Recorder => "Physical gamepad input · Filters apply to the saved take",
            _ => "Physical gamepad · Live input and bindings"
        };
        switch (page)
        {
            case StudioPage.Dashboard:
                Heading("Dashboard", "Choose a profile and turn each feature on or off independently.");
                RefreshFeatureControls();
                RefreshDashboardVoiceProfiles();
                break;
            case StudioPage.Parameters:
                if (SelectedRecording(_draft) is { } recorded)
                {
                    Heading("Parameters", $"{recorded.Name} · Adjust timing in the inspector, then choose Apply.");
                    _recordingEditor = new RecordingEditor(recorded);
                    _recordingEditor.RecordingChanged += updated =>
                    {
                        StageRecordingEdit(updated);
                        BuildTimingFields();
                    };
                    Details.Children.Add(_recordingEditor);
                }
                else
                {
                    Heading("Parameters", "Edit timings in the inspector and review the exact action list.");
                    ShowTrajectory();
                    AddButton("Reset this timing preset", () =>
                    {
                        _draft = _draft.WithTiming(_variant, StudioProfile.CreateDefault().GetTiming(_variant));
                        BuildTimingFields();
                        ShowPage();
                        DraftChanged();
                    });
                }
                break;
            case StudioPage.Profiles:
                ShowProfiles();
                break;
            case StudioPage.Calibration:
                Heading("Calibration", "Preview the generated stick trajectory. Adjust timing based on feedback from the receiving app.");
                ShowTrajectory();
                break;
            case StudioPage.Monitor:
                Heading("Device Monitor", "Physical input and the last virtual report are shown separately.");
                _monitorDevicePicker = new ComboBox
                {
                    Header = "Input controller", HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 420
                };
                _monitorDevicePicker.SelectionChanged += DeviceSelectionChanged;
                Details.Children.Add(_monitorDevicePicker);
                FillDevicePickers();
                AddStickDeadzoneControls();
                AddButton("Probe Elite keypad paddles", () =>
                {
                    if (_runtime?.Latest is not { } sample || !sample.Input.IsPaddleInterfacePresent || !sample.Input.IsConnected)
                    {
                        _probe!.Text = "Connect the Elite by USB and select PC Profile on the controller.";
                        return;
                    }
                    if (sample.OutputActive || _openingOutput)
                    {
                        _probe!.Text = "Stop virtual output to probe the paddles.";
                        return;
                    }
                    _probePulse = sample.PaddlePulse;
                    _probeIndex = 0;
                    _probe!.Text = "Press the small left paddle (Num /).";
                });
                _probe = DetailText("The Elite keypad probe is available with a wired Elite and its PC Profile.");
                _monitor = DetailText("Waiting for the first input sample");
                _monitor.FontFamily = new FontFamily("Consolas");
                break;
            case StudioPage.Sequences:
                Heading("Input Sequences", "Manage saved macros and open built-in scripts for editing.");
                AddSavedMacroList();
                AddButton("Open Macro Recorder", () => SelectPage(StudioPage.Recorder));
                AddButton("Open Lua scripts", () => SelectPage(StudioPage.Lua));
                DetailText("Built-in scripts").Style = (Style)Root.Resources["Section"];
                foreach (var variant in Enum.GetValues<ShotVariant>())
                    AddButton(VariantName(variant), () =>
                    {
                        _showBuiltInTiming = true;
                        _parameterRecordingId = null;
                        _variant = variant;
                        RefreshInspector();
                        SelectPage(StudioPage.Parameters);
                    });
                break;
            case StudioPage.Recorder:
                Heading("Macro Recorder", "Record an input sequence and choose which controls to replay.");
                ShowRecorder();
                break;
            case StudioPage.Lua:
                Heading("Lua Scripts", "Write controller logic, check its syntax, and save it to the current macro profile.");
                _luaPanel ??= new LuaScriptPanel(SaveLuaScript, DeleteLuaScript);
                _luaPanel.SetProfile(_draft);
                Details.Children.Add(_luaPanel);
                break;
            case StudioPage.Plugins:
                Heading("Plugins", "Tune your saved input sequences for specific games.");
                if (_nba2K27Plugin is null)
                {
                    _nba2K27Plugin = new Nba2K27PluginPanel(SavePluginTuning, VariantName);
                    _nba2K27Plugin.ShotProfileChanged += profile => _visionPanel?.SetShotProfile(profile);
                }
                _nba2K27Plugin.SetProfile(_active);
                Details.Children.Add(_nba2K27Plugin);
                break;
            case StudioPage.Voice:
                Heading("Live Voice", "Process your microphone locally for communication in Windows apps.");
                Details.Children.Add(GetVoicePanel());
                break;
            case StudioPage.Vision:
                Heading("Remote Play Vision", "Experimental window capture, animation inference and timed controller release.");
                _visionPanel ??= new VisionPanel(AppWindow.Id, () => _runtime, async () =>
                {
                    if (_runtime?.HasOutput != true) await StartOutputAsync(enableScripts: false);
                }, () => SelectPage(StudioPage.Plugins));
                _visionPanel.SetProfile(_active.Id, _active.Name);
                if (_nba2K27Plugin is not null) _visionPanel.SetShotProfile(_nba2K27Plugin.SelectedShotProfile);
                if (VisionHost.Children.Count == 0) VisionHost.Children.Add(_visionPanel);
                UpdateVisionPresentationVisibility();
                break;
            case StudioPage.Settings:
                Heading("Settings", "Manage local controller output and find your saved profiles.");
                DetailText("Input · Standard Windows gamepads over USB or wireless");
                DetailText("Output · Virtual Xbox 360 controller");
                DetailText(VirtualMicDriver.IsAvailable ? "Virtual device driver installed. Controller and microphone output can run without administrator access."
                    : "Install the bundled virtual device driver once to enable Xbox controller and microphone output. Windows requests administrator approval for this setup.");
                if (!VirtualMicDriver.IsAvailable)
                    AddButton("Install virtual device driver", async () => await InstallVirtualDeviceDriverAsync());
                AddButton("Check virtual output", async () =>
                {
                    string result = await Task.Run(VirtualOutput.ProbeStatus);
                    if (!_closing)
                        Status.Text = result;
                });
                DetailText($"Profile folder\n{_store.DirectoryPath}");
                break;
            case StudioPage.Updates:
                Heading("Updates", $"Installed Studio build {BuildVersion}");
                DetailText("Dashboard · Switch macro and voice profiles independently while their outputs run.");
                DetailText("Live Voice · Transform your microphone and choose the virtual microphone or headphone preview.");
                DetailText("Input devices · Choose a Windows gamepad from the sidebar or Device Monitor.");
                DetailText("3D controls · Drag a stick to preview its movement. Drag the shell or background to turn the controller.");
                DetailText("Macro Recorder · Capture filtered physical controller input and save a script in the active profile.");
                break;
            case StudioPage.Help:
                Heading("Help & Docs", "Assistive controller input · Local virtual output");
                DetailText("1. Connect a gamepad and choose it in Device Monitor or the sidebar.");
                DetailText("2. For the Elite wired paddle workaround, select PC Profile and run the Elite keypad probe.");
                DetailText("3. If Windows needs the virtual device driver, install it in Settings. This one-time setup requires administrator approval.");
                DetailText("4. Turn on Macro scripts in Dashboard to start the virtual controller. To disconnect both outputs, open Studio actions and choose Stop voice, scripts, and output.");
                break;
            case StudioPage.About:
                Heading("About", $"{BuildInfo.ProductName} {BuildVersion} · Windows x64");
                DetailText($"Running build folder\n{AppContext.BaseDirectory}");
                DetailText("3D control layout · CC0 Xbox One model adapted from CasperH2O");
                DetailText("Elite reference images · Microsoft Store's Xbox Elite Wireless Controller Series 2 gallery");
                DetailText("Virtual Xbox provider · USB/IP");
                AddButton("Microsoft Store controller gallery", async () =>
                    await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.microsoft.com/en-us/d/xbox-elite-wireless-controller-series-2/8rsn7j6375gg")));
                break;
            default:
                Heading("Controller Bindings", "Select a control on the map to edit its script assignment.");
                break;
        }
    }

    private void Heading(string title, string caption)
    {
        PageTitle.Text = title;
        PageCaption.Text = caption;
    }

    private static string BuildVersion => BuildInfo.Version;

    private void AddStickDeadzoneControls()
    {
        var left = DeadzoneField("Left stick (%)", _stickDeadzone.LeftPercent);
        var right = DeadzoneField("Right stick (%)", _stickDeadzone.RightPercent);
        var fields = new Grid { ColumnSpacing = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        fields.Children.Add(left);
        fields.Children.Add(right);

        var heading = DetailText("Stick center deadzones");
        heading.Style = (Style)Root.Resources["Section"];
        DetailText("Applies live to virtual output and saves for this PC. Set a stick to 0% to turn its deadzone off.")
            .Style = (Style)Root.Resources["Hint"];
        Details.Children.Add(fields);
        left.ValueChanged += (_, args) => Save(_stickDeadzone with { LeftPercent = args.NewValue });
        right.ValueChanged += (_, args) => Save(_stickDeadzone with { RightPercent = args.NewValue });

        void Save(StickDeadzone settings)
        {
            if (settings == _stickDeadzone)
                return;
            if (settings.IsValid)
            {
                try
                {
                    _store.SaveStickDeadzone(settings);
                    _stickDeadzone = settings;
                    _runtime?.SetStickDeadzone(settings);
                    Status.Text = "Stick deadzones saved and applied.";
                    return;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Status.Text = $"Couldn’t save the stick deadzones. {error.Message}";
                }
            }
            left.Value = _stickDeadzone.LeftPercent;
            right.Value = _stickDeadzone.RightPercent;
        }

        static NumberBox DeadzoneField(string label, double value) => new()
        {
            Header = label, Value = value, Minimum = 0, Maximum = StickDeadzone.MaximumPercent,
            SmallChange = 0.5, LargeChange = 5, MinWidth = 115,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
    }

    private TextBlock DetailText(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
        Details.Children.Add(block);
        return block;
    }

    private void AddButton(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        Details.Children.Add(button);
    }

    private async Task InstallVirtualDeviceDriverAsync()
    {
        if (_installingVirtualDeviceDriver || _closing)
            return;

        _installingVirtualDeviceDriver = true;
        Status.Text = "Installing the bundled Windows virtual device driver…";
        try
        {
            await VirtualMicDriver.InstallAsync();
            if (!_closing)
            {
                Status.Text = "Virtual device driver ready. You can now start Macro scripts or Live voice.";
                if (CurrentPage == StudioPage.Settings)
                    ShowPage();
            }
        }
        catch (Exception error)
        {
            if (!_closing)
                Status.Text = error.Message;
        }
        finally { _installingVirtualDeviceDriver = false; }
    }

    private void OpenRecorder(object sender, RoutedEventArgs args) => SelectPage(StudioPage.Recorder);

    private bool RecorderInUse() => _runtime?.Recording is { IsRecording: true } or { Pending: not null };

    private void RequireVisionTrainingStopped()
    {
        if (_runtime?.IsVisionTrainingRecording == true)
            throw new InvalidOperationException("Finish the gameplay training take before changing macro profiles, scripts, or recordings. Training captures manual input only.");
    }

    private bool BlockDuringVisionTraining()
    {
        if (_runtime?.IsVisionTrainingRecording != true) return false;
        Status.Text = "Finish the gameplay training take before changing macro profiles, scripts, or recordings. Training captures manual input only.";
        ShowProfileNotice(DashboardMacroProfileNotice, Status.Text);
        return true;
    }

    private void SavePluginTuning(StudioProfile expected, StudioProfile updated)
    {
        RequireVisionTrainingStopped();
        if (_closing || _active != expected)
            throw new InvalidOperationException("The active macro profile changed. Open Plugins and load the macro again.");
        if (RecorderInUse())
            throw new InvalidOperationException("Finish and save or discard the recording first.");
        if (_draft != _active)
            throw new InvalidOperationException("Apply or revert your existing edits before changing a macro here.");

        var profiles = _macroProfiles.Select(profile => profile.Id == updated.Id ? updated : profile).ToArray();
        _store.SaveRecordingEdit(expected, updated);
        _runtime?.Apply(updated);
        _active = _draft = updated;
        foreach (var (id, recording) in updated.Recordings)
            if (expected.Recordings.GetValueOrDefault(id) != recording)
                _recorderPanel?.ClearSavedPreview(id);
        SetProfiles(profiles);
        RefreshInspector();
        Status.Text = $"Saved and applied {updated.Name}";
    }

    private void ShowRecorder()
    {
        if (_recorderPanel is null)
        {
            _recorderPanel = new RecorderPanel();
            _recorderPanel.StartRequested += channels => UpdateRecording(() =>
            {
                if (_runtime is null)
                    throw new InvalidOperationException("Start the controller monitor before recording.");
                _runtime.StartRecording(channels);
            });
            _recorderPanel.StopRequested += () => UpdateRecording(() => _runtime?.StopRecording());
            _recorderPanel.DiscardRequested += () => UpdateRecording(() =>
            {
                _runtime?.DiscardRecording();
                _replacementRecording = null;
            });
            _recorderPanel.RecordAgainRequested += () => UpdateRecording(() =>
            {
                if (_runtime is { } runtime && runtime.Recording.Pending is { } take)
                    runtime.StartRecording(take.Channels, replacePending: true);
            });
            _recorderPanel.SaveRequested += (name, paddle) => UpdateRecording(() => SaveRecording(name, paddle));
            _recorderPanel.SaveJsonRequested += recording => _ = ExportRecordingAsync(recording);
        }
        AddButton("Saved macros", () => _savedMacroList?.StartBringIntoView());
        Details.Children.Add(_recorderPanel);
        AddSavedMacroList();
        RefreshRecorder();
    }

    private void AddSavedMacroList()
    {
        _savedMacroList = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 4) };
        Details.Children.Add(_savedMacroList);
        RefreshSavedMacros();
    }

    private void RefreshSavedMacros(Guid? focusId = null)
    {
        if (_savedMacroList is not { } list)
            return;
        CloseMacroNameEditor();
        list.Children.Clear();
        list.Children.Add(new TextBlock
        {
            Text = "Saved macros", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        list.Children.Add(new TextBlock
        {
            Text = $"{_active.Name} · {_active.Recordings.Count} of {MacroRecording.MaxRecordingsPerProfile} macros",
            Style = (Style)Root.Resources["Hint"]
        });
        if (_active.Recordings.Count == 0)
            list.Children.Add(new TextBlock { Text = "Save a recording to add it here." });

        foreach (var recording in _active.Recordings.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var card = new StackPanel { Spacing = 8 };
            card.Children.Add(new TextBlock
            {
                Text = recording.Name, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            var triggers = _active.Bindings.Where(binding => binding.Value.RecordingId == recording.Id)
                .OrderBy(binding => binding.Key).Select(binding => ControlName(binding.Key)).ToArray();
            string assignment = triggers.Length == 0 ? "Unassigned" : string.Join(", ", triggers);
            card.Children.Add(new TextBlock
            {
                Text = $"{recording.DurationMs / 1000.0:F2} s · {recording.Frames.Length} frames · {assignment}",
                Style = (Style)Root.Resources["Hint"]
            });
            var editActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var otherActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var editTiming = AddAction(editActions, "Edit timing", _ => OpenRecordedParameters(recording.Id));
            editTiming.Background = (Brush)Application.Current.Resources["StudioAccentBrush"];
            AddAction(editActions, "Rename", button => OpenMacroNameEditor(card, button, recording, duplicate: false));
            AddAction(editActions, "Duplicate", button => OpenMacroNameEditor(card, button, recording, duplicate: true));
            AddAction(otherActions, "Re-record", _ => BeginRerecord(recording.Id));
            AddAction(otherActions, "View JSON", button => _ = ShowRecordingJsonAsync(recording));
            AddAction(otherActions, "Delete", button => _ = DeleteRecordingAsync(recording.Id));
            card.Children.Add(editActions);
            card.Children.Add(otherActions);
            list.Children.Add(new Border
            {
                Style = (Style)Root.Resources["DashboardCard"], Child = card
            });
            if (recording.Id == focusId)
            {
                editTiming.Loaded += (_, _) =>
                {
                    card.StartBringIntoView();
                    editTiming.Focus(FocusState.Programmatic);
                };
            }

            Button AddAction(Panel group, string label, Action<Button> action)
            {
                var button = new Button { Content = label };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{label} macro {recording.Name}");
                button.Click += (_, _) => action(button);
                group.Children.Add(button);
                return button;
            }
        }
    }

    private void BeginRerecord(Guid id)
    {
        if (BlockDuringVisionTraining()) return;
        if (RecorderInUse())
        {
            ShowRecordingError("Finish the current recording or discard it before re-recording a saved macro.");
            return;
        }
        if (!_active.Recordings.TryGetValue(id, out var recording))
            return;
        _replacementRecording = recording;
        SelectPage(StudioPage.Recorder);
        _recorderPanel?.SelectInputs(recording.Channels);
        DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
        UpdateRecording(() =>
        {
            if (_runtime is null)
                throw new InvalidOperationException("Start the controller monitor before recording.");
            _runtime.StartRecording(recording.Channels);
        });
    }

    private void ShowRecordingError(string message)
    {
        Status.Text = _recordError = message;
        RefreshRecorder();
    }

    private void UpdateRecording(Action action)
    {
        _recordError = null;
        try { action(); }
        catch (Exception error) { _recordError = error.Message; }
        RefreshRecorder();
    }

    private void RefreshRecorder()
    {
        var state = _runtime?.Recording ?? default;
        RecordingBadge.Visibility = state.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        if (state.IsRecording)
            RecordingBadge.Content = $"● Recording {state.ElapsedMs / 1000.0:F1}s";
        if (CurrentPage == StudioPage.Recorder)
        {
            string? note = _recordError ?? state.Note;
            if (note is null && state.Connected && !state.SupportsBackgroundInput && state.Pending is null)
                note = "Background capture is unavailable for this controller. Keep AccessRelay focused to record it.";
            _recorderPanel?.ShowState(state.Connected, state.IsRecording, state.HasInput, state.ElapsedMs,
                state.Pending, note, _active.Name, _replacementRecording);
        }
    }

    private async Task ExportRecordingAsync(MacroRecording recording)
    {
        if (_exportingRecording || _closing)
            return;

        _exportingRecording = true;
        try
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "AccessRelay-Macro" };
            picker.FileTypeChoices.Add("Recorded macro JSON", [".json"]);
            if (await picker.PickSaveFileAsync() is { } file)
            {
                _store.ExportRecording(recording, file.Path);
                Status.Text = "Saved a recorded macro JSON file.";
                if (CurrentPage == StudioPage.Recorder)
                    _recorderPanel?.ShowExportStatus(recording.Id, "JSON exported.");
            }
        }
        catch (Exception error)
        {
            if (!_closing)
            {
                Status.Text = error.Message;
                if (CurrentPage == StudioPage.Recorder)
                    _recorderPanel?.ShowExportStatus(recording.Id, error.Message);
            }
        }
        finally { _exportingRecording = false; }
    }

    private async Task ShowRecordingJsonAsync(MacroRecording recording)
    {
        if (_closing || Root.XamlRoot is not { } root)
            return;

        var code = new TextBox
        {
            Header = "Recorded macro JSON", Text = ProfileStore.FormatRecording(recording),
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"), Height = 320
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(code, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(code, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = recording.Name, Content = code,
            PrimaryButtonText = "Save JSON", CloseButtonText = "Close"
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ExportRecordingAsync(recording);
        }
        catch (Exception error) { if (!_closing) Status.Text = error.Message; }
    }

    private void OpenRecordedParameters(Guid id)
    {
        if (_closing || !_draft.Recordings.ContainsKey(id))
            return;
        _parameterRecordingId = id;
        _showBuiltInTiming = false;
        if (CurrentPage == StudioPage.Parameters)
        {
            BuildTimingFields();
            ShowPage();
        }
        else
            SelectPage(StudioPage.Parameters);
        DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void StageRecordingEdit(MacroRecording recording)
    {
        if (!_draft.Recordings.TryGetValue(recording.Id, out var current) || current == recording)
            return;
        _draft = _draft.WithRecording(recording);
        TimingHint.Text = RecordingTimingHint(recording);
        _recordingEditor?.UpdateRecording(recording);
        if (CurrentPage == StudioPage.Calibration)
            RefreshTimingPage();
        DraftChanged();
    }

    private void SaveRecording(string name, ControlId? paddle)
    {
        RequireVisionTrainingStopped();
        if (_runtime?.Recording.Pending is not { } pending)
            return;
        var replacement = _replacementRecording;
        if (replacement is not null)
        {
            if (!_active.Recordings.TryGetValue(replacement.Id, out var current) || current != replacement)
                throw new InvalidOperationException("This macro changed. Cancel the re-record and start it again.");
            paddle = null;
        }
        var recording = pending with { Id = replacement?.Id ?? pending.Id, Name = name.Trim() };
        var saved = _active.WithRecording(recording);
        var draft = _draft.WithRecording(recording);
        if (paddle is ControlId target && ControlMap.Paddles.Contains(target))
        {
            var binding = new ScriptBinding(ScriptKind.Recorded, RecordingId: recording.Id);
            saved = saved.WithBinding(target, binding);
            draft = draft.WithBinding(target, binding);
        }

        // Commit the recording and its optional binding without applying unrelated unsaved edits.
        _store.Save(saved);
        _runtime.Apply(saved);
        _draft = _draft == _active ? saved : draft;
        _active = saved;
        _runtime.DiscardRecording();
        _replacementRecording = null;
        _recorderPanel?.ShowSaved(recording, saved.Name);
        if (paddle is ControlId control && ControlMap.Paddles.Contains(control))
        {
            _selected = control;
            _showBuiltInTiming = false;
        }
        SetProfiles(_store.LoadAll());
        RefreshInspector();
        RefreshSavedMacros();
        Status.Text = replacement is not null ? $"Re-recorded {recording.Name} in {_active.Name}"
            : paddle is ControlId assigned
            ? $"Saved {recording.Name} and bound it to {ControlName(assigned)}"
            : $"Saved {recording.Name} in {_active.Name}";
    }

    private void OpenMacroNameEditor(Panel card, Button trigger, MacroRecording source, bool duplicate)
    {
        if (_closing || _active.Recordings.GetValueOrDefault(source.Id) != source)
            return;

        var profile = _active;
        CloseMacroNameEditor();
        var editor = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        _macroNameEditor = editor;
        var message = new TextBlock { Style = (Style)Root.Resources["Hint"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        editor.Children.Add(message);
        card.Children.Add(editor);
        if (RecorderInUse() || duplicate && profile.Recordings.Count >= MacroRecording.MaxRecordingsPerProfile)
        {
            message.Text = RecorderInUse() ? "Finish and save or discard the current recording first."
                : $"This profile already has {MacroRecording.MaxRecordingsPerProfile} saved macros.";
            return;
        }

        var name = new TextBox { Header = duplicate ? "Name for the copy" : "Macro name", MaxLength = 80, MinWidth = 220 };
        name.Text = duplicate ? SuggestedMacroCopyName(profile, source.Name, name.MaxLength) : source.Name;
        var confirm = new Button { Content = duplicate ? "Create copy" : "Save name", IsEnabled = duplicate };
        confirm.Background = (Brush)Application.Current.Resources["StudioAccentBrush"];
        var cancel = new Button { Content = "Cancel" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        editor.Children.Insert(0, name);
        editor.Children.Insert(1, actions);
        name.TextChanged += (_, _) =>
        {
            string value = name.Text.Trim();
            confirm.IsEnabled = value.Length > 0 && !value.Any(char.IsControl) && (duplicate || value != source.Name);
            message.Text = "";
        };
        confirm.Click += (_, _) => Save();
        cancel.Click += (_, _) => Cancel();
        name.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter && confirm.IsEnabled)
            {
                args.Handled = true;
                Save();
            }
            else if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                Cancel();
            }
        };
        name.Focus(FocusState.Programmatic);
        name.SelectAll();
        editor.StartBringIntoView();

        void Cancel()
        {
            CloseMacroNameEditor();
            trigger.Focus(FocusState.Programmatic);
        }

        void Save()
        {
            try { SaveMacroName(profile, source.Id, name.Text, duplicate); }
            catch (Exception error) { if (!_closing) message.Text = error.Message; }
        }
    }

    private void CloseMacroNameEditor()
    {
        if (_macroNameEditor is { Parent: Panel parent } editor)
            parent.Children.Remove(editor);
        _macroNameEditor = null;
    }

    private static string SuggestedMacroCopyName(StudioProfile profile, string source, int maxLength)
    {
        for (int number = 1; ; number++)
        {
            string suffix = number == 1 ? " copy" : $" copy {number}";
            int length = Math.Min(source.Length, maxLength - suffix.Length);
            if (char.IsHighSurrogate(source[length - 1]))
                length--;
            string candidate = source[..length].TrimEnd() + suffix;
            if (!profile.Recordings.Values.Any(macro => string.Equals(macro.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private void SaveMacroName(StudioProfile profile, Guid id, string name, bool duplicate)
    {
        RequireVisionTrainingStopped();
        if (_closing || _active != profile || RecorderInUse())
            throw new InvalidOperationException("The macro profile or recorder state changed. Reopen the macro action and try again.");

        StudioProfile saved, draft;
        MacroRecording result;
        if (duplicate)
        {
            (saved, result) = profile.DuplicateRecording(id, name);
            draft = _draft == profile ? saved : _draft.WithRecording(result);
        }
        else
        {
            saved = profile.RenameRecording(id, name);
            result = saved.Recordings[id];
            draft = _draft == profile ? saved : _draft.RenameRecording(id, name);
        }

        _store.SaveRecordingEdit(profile, saved);
        if (!duplicate)
        {
            _runtime?.Apply(saved);
            _recorderPanel?.RefreshSavedName(result);
        }
        _active = saved;
        _draft = draft;
        _recordError = null;
        SetProfiles(_macroProfiles.Select(item => item.Id == saved.Id ? saved : item).ToArray());
        RefreshInspector();
        RefreshSavedMacros(result.Id);
        RefreshRecorder();
        Status.Text = duplicate ? $"Created “{result.Name}”. Assign it in Bindings when ready."
            : $"Renamed macro to “{result.Name}”.";
    }

    private async Task DeleteRecordingAsync(Guid id)
    {
        if (BlockDuringVisionTraining()) return;
        if (RecorderInUse())
        {
            ShowRecordingError("Finish or discard the current recording before deleting a saved macro.");
            return;
        }
        var profile = _active;
        if (_closing || Root.XamlRoot is not { } root || !profile.Recordings.TryGetValue(id, out var recording))
            return;
        int bindings = profile.Bindings.Values.Count(binding => binding.RecordingId == id);
        string detail = bindings == 0 ? $"This removes the recording from {profile.Name}."
            : $"This also clears {bindings} {(bindings == 1 ? "binding" : "bindings")} in {profile.Name}.";
        try
        {
            if (!await StudioDialog.ConfirmDeletionAsync(root, Root.ActualTheme, "macro", recording.Name, detail,
                    title: "Delete macro?") || _closing || _active.Id != profile.Id || RecorderInUse() ||
                !_active.Recordings.TryGetValue(id, out var current) || current != recording)
                return;

            RequireVisionTrainingStopped();

            var saved = _active.WithoutRecording(id);
            var draft = _draft == _active ? saved : _draft.WithoutRecording(id);
            _store.Save(saved);
            _runtime?.Apply(saved);
            _active = saved;
            _draft = draft;
            if (_parameterRecordingId == id)
                _parameterRecordingId = null;
            if (_replacementRecording?.Id == id)
                _replacementRecording = null;
            _recorderPanel?.ClearSavedPreview(id);
            _recordError = null;
            SetProfiles(_store.LoadAll());
            RefreshInspector();
            RefreshSavedMacros();
            RefreshRecorder();
            Status.Text = $"Deleted {recording.Name} from {saved.Name}";
        }
        catch (Exception error) { if (!_closing) ShowRecordingError(error.Message); }
    }

    private (string Title, IReadOnlyList<StickPhase> Phases, MacroRecording? Recording) BuildPreview(StudioProfile profile)
    {
        if (SelectedRecording(profile) is not { } recording)
            return (VariantName(_variant), Trajectory.Build(profile, _variant), null);
        var frames = recording.Frames;
        var phases = new List<StickPhase>(frames.Length);
        for (int i = 0; i < frames.Length; i++)
        {
            int end = i + 1 < frames.Length ? frames[i + 1].TimeMs : recording.DurationMs;
            int duration = end - frames[i].TimeMs;
            if (duration > 0)
            {
                var state = frames[i].State;
                short? x = recording.Channels.HasFlag(RecordingChannels.RightStickX) ? state.RightX : null;
                phases.Add(new("Captured sample", duration, state.RightY, x));
            }
        }
        return (recording.Name, phases, recording);
    }

    private void ShowTrajectory()
    {
        var (title, phases, recording) = BuildPreview(_draft);
        int total = phases.Sum(phase => phase.DurationMs);
        DetailText($"{title} · {total} ms");
        var plot = new Canvas { Width = 640, Height = 140, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 13, 22, 34)) };
        foreach (double y in new[] { 10d, 70d, 130d })
            plot.Children.Add(new Line { X1 = 0, X2 = 640, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 56, 74, 99)) });
        var points = new PointCollection { new(0, 70) };
        var horizontalPoints = new PointCollection { new(0, 70) };
        bool showHorizontal = phases.Any(phase => phase.RightX.HasValue);
        int time = 0;
        foreach (var phase in phases)
        {
            double y = 70 - phase.RightY * 60.0 / 32768;
            double x = 70 - (phase.RightX ?? 0) * 60.0 / 32768;
            points.Add(new(640.0 * time / total, y));
            if (showHorizontal)
                horizontalPoints.Add(new(640.0 * time / total, x));
            time += phase.DurationMs;
            points.Add(new(640.0 * time / total, y));
            if (showHorizontal)
                horizontalPoints.Add(new(640.0 * time / total, x));
        }
        plot.Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(Colors.DeepSkyBlue), StrokeThickness = 2 });
        if (showHorizontal)
            plot.Children.Add(new Polyline { Points = horizontalPoints, Stroke = new SolidColorBrush(Colors.Gold), StrokeThickness = 1.5 });
        Details.Children.Add(new Viewbox { Child = plot, Stretch = Stretch.Uniform, MaxHeight = 180 });
        _previewStatus = DetailText(showHorizontal ? "Right-stick Y in blue · Right-stick X in gold" : "Up is positive · Down is negative");
        if (recording is not null)
        {
            DetailText($"{recording.Frames.Length} captured frames · {recording.Channels}");
            return;
        }
        foreach (var phase in phases)
            DetailText($"{phase.Label}    {phase.DurationMs} ms    Y {Percent(phase.RightY)}%" +
                (phase.RightX is short x ? $"    X {Percent(x)}%" : ""));
    }

    private void ShowProfiles()
    {
        Heading("Macro profiles", "Choose a macro profile, save timing edits, or create a separate controller setup.");
        var picker = new ComboBox
        {
            Header = "Macro profile", DisplayMemberPath = nameof(StudioProfile.Name),
            MaxWidth = 360, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left
        };
        SyncMacroPicker(picker);
        picker.SelectionChanged += ProfileChanged;
        _managedMacroProfile = picker;
        Details.Children.Add(picker);
        var name = new TextBox { Header = "Profile name", Text = _draft.Name, MaxLength = 80, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        Details.Children.Add(name);
        AddButton("Rename macro profile", () => SaveNamed(_draft with { Name = name.Text.Trim() }));
        AddButton("Duplicate draft", () => SaveNamed(_draft with { Id = Guid.NewGuid(), Name = name.Text.Trim() + " copy", ScriptsEnabled = false }));
        AddButton("Create macro profile", () => SaveNamed(StudioProfile.CreateDefault(name.Text.Trim() + " new")));
        AddButton("Import macro profile", async () => await ProfileFile(save: false));
        AddButton("Export macro profile", async () => await ProfileFile(save: true));
        AddButton("Delete macro profile…", async () => await DeleteMacroProfileAsync());
        DetailText(_store.DirectoryPath);
    }

    private async Task DeleteMacroProfileAsync()
    {
        if (_deletingMacroProfile || _closing || Root.XamlRoot is not { } root)
            return;
        if (BlockDuringVisionTraining()) return;
        if (RecorderInUse() || _draft != _active)
        {
            Status.Text = RecorderInUse() ? "Finish and save or discard the recording before deleting a macro profile."
                : "Apply or revert your bindings draft before deleting this macro profile.";
            ShowProfileNotice(DashboardMacroProfileNotice, Status.Text);
            return;
        }

        var profile = _active;
        _deletingMacroProfile = true;
        try
        {
            var remaining = _store.LoadAll().Where(candidate => candidate.Id != profile.Id).ToArray();
            string detail = remaining.Length == 0 ? "Studio will create an empty Default macro profile."
                : "Studio will select another saved macro profile.";
            bool confirmed = await StudioDialog.ConfirmDeletionAsync(root, Root.ActualTheme, "macro", profile.Name, detail);
            if (!confirmed || _closing || _active.Id != profile.Id || RecorderInUse() || _draft != _active)
                return;

            RequireVisionTrainingStopped();

            remaining = _store.LoadAll().Where(candidate => candidate.Id != profile.Id).ToArray();
            StudioProfile? replacement = null;
            if (remaining.Length == 0)
            {
                replacement = new StudioProfile { Id = Guid.NewGuid(), Name = "Default" };
                _store.Save(replacement);
                remaining = [replacement];
            }

            try { _store.Delete(profile.Id); }
            catch
            {
                if (replacement is not null)
                {
                    try { _store.Delete(replacement.Id); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
            ActivateProfile(remaining[0], remaining);
            Status.Text = $"Deleted “{profile.Name}”. {Status.Text}";
        }
        catch (Exception error)
        {
            if (!_closing)
            {
                Status.Text = $"Couldn’t delete “{profile.Name}”. {error.Message}";
                ShowProfileNotice(DashboardMacroProfileNotice, Status.Text);
            }
        }
        finally { _deletingMacroProfile = false; }
    }

    private void SaveNamed(StudioProfile profile)
    {
        if (BlockDuringVisionTraining()) return;
        if (RecorderInUse())
        {
            Status.Text = "Finish and save or discard the recording before changing profiles.";
            return;
        }
        try
        {
            _store.Save(profile);
            ActivateProfile(profile);
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async Task ProfileFile(bool save)
    {
        try
        {
            if (save)
            {
                var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "AccessRelay-Profile" };
                picker.FileTypeChoices.Add("Studio profile", [".json"]);
                var file = await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    _store.Export(_active, file.Path);
                    Status.Text = "Saved a Studio profile copy";
                }
            }
            else
            {
                if (BlockDuringVisionTraining()) return;
                if (RecorderInUse())
                {
                    Status.Text = "Finish and save or discard the recording before importing.";
                    return;
                }
                if (_draft != _active)
                {
                    Status.Text = "Apply or revert your draft before importing.";
                    return;
                }
                var picker = new FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    RequireVisionTrainingStopped();
                    ActivateProfile(_store.Import(file.Path));
                }
            }
        }
        catch (Exception error) { Status.Text = error.Message; }
    }
}
