using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT;

namespace EliteScriptStudio;

/// <summary>Experimental vision controls. Loading settings never starts capture or arms output.</summary>
internal sealed class VisionPanel : UserControl, IDisposable
{
    private sealed record ModelChoice(string ManifestPath, string Name, string Status, bool Available,
        VisionModelLibraryEntry? Entry = null);
    private const string ViewingNotice = "Live view is running. Record a training clip to save footage; viewing alone does not save it.";
    private const string RecordingNotice = "Recording a manual training clip. Return focus to Remote Play and perform the shot.";
    private const string FinalizingNotice = "Finishing the training clip. Wait for its saved status before reviewing it.";
    [ComImport, Guid("905A0FEF-BC53-11DF-8C49-001E4FC686DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess { void Buffer(out nint data); }
    private readonly Func<StudioRuntime?> _runtime;
    private readonly Func<Task> _connectOutput;
    private readonly Microsoft.UI.WindowId _windowId;
    private readonly VisionSettingsStore _store = new();
    private readonly VisionModelLibrary _modelLibrary = new();
    private VisionSession? _session;
    private Guid _profileId;
    private Guid _macroProfileId;
    private Nba2K27ShotProfile? _shotProfile;
    private VisionSettings _loadedSettings = VisionSettings.Default;
    private readonly Grid _layout = new() { RowSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly StackPanel _configuration = new() { Spacing = 12, MaxWidth = 1080, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _live = new() { Spacing = 12, MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TabView _tabs = new() { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false };
    private readonly TabViewItem _configurationTab = new() { Header = new TextBlock { Text = "Configuration", FontSize = 14 }, IsClosable = false };
    private readonly TabViewItem _liveTab = new() { Header = new TextBlock { Text = "Live Capture", FontSize = 14 }, IsClosable = false };
    private readonly TextBlock _sourceContext = Text("Choose a Remote Play window in Configuration.");
    private readonly TextBlock _profile = Text("Select a macro profile.");
    private readonly TextBlock _shotProfileInfo = Text("Select a saved NBA 2K27 player/shot profile in Plugins before collecting training data.");
    private readonly TextBlock _trainingInfo = Text("Not recording — live view frames are not saved.");
    private readonly TextBlock _trainingDetails = Text("Record a training clip to save a 10-second take. A model is not required.");
    private readonly TextBlock _diagnosticStatus = Text("Live timing details appear after a session starts.");
    private readonly Expander _diagnostics = new() { Header = new TextBlock { Text = "Timing and diagnostics", FontSize = 14 }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = Text("Observation is off.");
    private readonly VisionPerformancePanel _performance;
    private readonly TextBlock _notice = Text("Start live view to preview, or record a training clip to save footage.");
    private readonly ComboBox _source = new() { Header = "Remote Play window", PlaceholderText = "Choose a Remote Play window", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Title" };
    private readonly Button _refreshSources = new() { Content = "Refresh windows", FontSize = 14 };
    private readonly ComboBox _modelPicker = new() { Header = "Gameplay model", FontSize = 14, DisplayMemberPath = nameof(ModelChoice.Name), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _modelInfo = Text("No trained gameplay model is included. You can view capture and record training clips without one.");
    private readonly TextBox _modelPath = new() { Header = "Installed model location", FontSize = 14, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _modelErrors = Text("");
    private readonly Button _importModel = new() { Content = "Import model package…", FontSize = 14 };
    private readonly ComboBox _executionProvider = new() { Header = "Inference execution provider", FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "CUDA", "CPU" }, SelectedIndex = 0 };
    private readonly TextBox _animation = new() { Header = "Game / camera / animation configuration", FontSize = 14, Text = "Default", MaxLength = 80 };
    private readonly ComboBox _shoot = new() { Header = "Shoot button", FontSize = 14, ItemsSource = new[] { ControlId.X, ControlId.A, ControlId.B, ControlId.Y, ControlId.RightBumper, ControlId.LeftBumper } };
    private readonly NumberBox _left = Number("Player crop left (%)", 0, 0, 99);
    private readonly NumberBox _top = Number("Player crop top (%)", 0, 0, 99);
    private readonly NumberBox _width = Number("Player crop width (%)", 100, 1, 100);
    private readonly NumberBox _height = Number("Player crop height (%)", 100, 1, 100);
    private readonly NumberBox _confidence = Number("Minimum confidence (%)", 90, 0, 100);
    private readonly NumberBox _uncertainty = Number("Maximum prediction uncertainty (ms)", 5, 0, 1000);
    private readonly NumberBox _frameAge = Number("Maximum local frame age (ms)", 80, 1, 1000);
    private readonly NumberBox _videoDelay = Number("Estimated video delay (ms)", 0, 0, 1000);
    private readonly NumberBox _inputDelay = Number("Estimated input delay (ms)", 0, 0, 1000);
    private readonly NumberBox _correction = Number("Calibrated correction (ms; negative sends earlier)", 0, -1000, 1000);
    private readonly NumberBox _lateness = Number("Maximum local deadline lateness (ms)", 5, 0, 100);
    private readonly ToggleSwitch _armed = new() { Header = "Automatic release", OffContent = "Disarmed", OnContent = "Armed", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _preview = new() { MaxHeight = 480, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _previewPlaceholder = Text("Start observation to see a fresh capture preview.");
    private readonly TextBlock _previewCaption = Text("Preview updates appear here while Live Capture is visible.");
    private readonly Button _start = new() { Content = new TextBlock { Text = "Start live view", FontSize = 14, TextWrapping = TextWrapping.Wrap }, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _recordTraining = new() { Content = "Record training clip…", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _stopTraining = new() { Content = "Stop recording", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _save = new() { Content = "Save settings", FontSize = 14 };
    private readonly Control[] _configurationControls;
    private WriteableBitmap? _bitmap;
    private bool _refreshing, _busy, _disposed, _populating, _pageVisible, _liveSelected, _previewDesired, _refreshingSources;
    private bool _refreshingModels, _modelsLoading;
    private int _modelContextVersion;
    private int _workflowVersion;
    private VisionSettings? _pendingModelContext;
    private VisionSettings? _runningModelContext;
    private string _selectedModelPath = "";
    private bool _manualTrainingView;
    private bool? _configurationEditable;
    private CaptureSource? _selectedSourceIdentity;
    private nint _lastObservedSource;
    private long _performanceRefreshedAt;
    private long _sourcesRefreshedAt;

    internal VisionPanel(Microsoft.UI.WindowId windowId, Func<StudioRuntime?> runtime, Func<Task> connectOutput, Action openShotProfiles)
    {
        _runtime = runtime; _windowId = windowId; _connectOutput = connectOutput;
        if (Application.Current.Resources.TryGetValue("StudioAccentBrush", out var recordAccent) && recordAccent is Brush accentBrush)
            _recordTraining.Background = accentBrush;
        _performance = new VisionPerformancePanel(SavePerformanceRun, LoadPerformanceRun);
        _configurationControls = [_source, _modelPicker, _executionProvider, _animation, _shoot, _left, _top, _width, _height,
            _confidence, _uncertainty, _frameAge, _videoDelay, _inputDelay, _correction, _lateness];
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(_sourceContext);
        _sourceContext.MaxLines = 1; _sourceContext.TextTrimming = TextTrimming.CharacterEllipsis;
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _start.Click += (_, _) => RunAction(Start);
        controls.Children.Add(_start);
        var stop = new Button { Content = new TextBlock { Text = "Stop live view", FontSize = 14, TextWrapping = TextWrapping.Wrap }, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        stop.Click += (_, _) => RunAction(() => { _session?.StopTraining(); Stop(); });
        Grid.SetColumn(stop, 1); controls.Children.Add(stop);
        Grid.SetColumn(_armed, 2); controls.Children.Add(_armed);
        header.Children.Add(controls);
        var recordingActions = Pair(_recordTraining, _stopTraining);
        _recordTraining.Click += (_, _) => RecordTraining();
        _stopTraining.Click += (_, _) => RunAction(() => _session?.StopTraining());
        header.Children.Add(recordingActions);
        _trainingInfo.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        header.Children.Add(_trainingInfo);
        _status.MaxLines = 3; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_status);
        header.Children.Add(_notice);
        _layout.Children.Add(header);
        _configurationTab.Content = TabScroll(_configuration);
        _liveTab.Content = TabScroll(_live);
        _tabs.TabItems.Add(_configurationTab); _tabs.TabItems.Add(_liveTab);
        _tabs.SelectedItem = _configurationTab;
        Grid.SetRow(_tabs, 1); _layout.Children.Add(_tabs);
        Content = _layout;
        _configuration.Children.Add(_profile);
        _configuration.Children.Add(_shotProfileInfo);
        var profileActions = Row();
        AddButton(profileActions, "Manage NBA 2K27 shot profiles", openShotProfiles);
        _configuration.Children.Add(profileActions);
        _configuration.Children.Add(Text("Choose your player/shot profile and Remote Play window, then use Record training clip above. Recording starts live view for you. No gameplay model is needed to save clips."));
        _configuration.Children.Add(Text("Route your physical controller through AccessRelay's virtual Xbox controller into Remote Play. Choose the intended player's region below; no shot meter or special in-game icon is required."));
        _configuration.Children.Add(_source);
        _configuration.Children.Add(Text("Browser windows show their active tab; background tabs are not separate capture sources."));
        var sourceActions = Row();
        _refreshSources.Click += (_, _) => RunAction(RefreshWindows);
        sourceActions.Children.Add(_refreshSources);
        AddButton(sourceActions, "Connect virtual controller", async () =>
        {
            try { await _connectOutput(); }
            catch (Exception error) { _notice.Text = error.Message; }
        });
        _configuration.Children.Add(sourceActions);
        _configuration.Children.Add(_modelPicker);
        _configuration.Children.Add(_modelInfo);
        var models = Row();
        _importModel.Click += (_, _) => ImportModelPackage();
        models.Children.Add(_importModel);
        _configuration.Children.Add(models);
        var advanced = new StackPanel { Spacing = 12 };
        var advancedSettings = new Expander { Header = new TextBlock { Text = "Advanced settings and calibration", FontSize = 14 },
            Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch };
        _configuration.Children.Add(advancedSettings);
        advanced.Children.Add(_executionProvider);
        advanced.Children.Add(Text("Stop live view before changing CPU or CUDA. Starting a session uses the selected provider; an unavailable provider does not silently switch to another."));
        var modelDetails = new StackPanel { Spacing = 8 };
        modelDetails.Children.Add(_modelPath); modelDetails.Children.Add(_modelErrors);
        advanced.Children.Add(new Expander { Header = new TextBlock { Text = "Model package details", FontSize = 14 }, Content = modelDetails,
            HorizontalAlignment = HorizontalAlignment.Stretch });
        var settingsFiles = Row();
        AddButton(settingsFiles, "Import settings…", ImportSettings);
        AddButton(settingsFiles, "Export settings…", ExportSettings);
        advanced.Children.Add(settingsFiles);
        advanced.Children.Add(_animation);
        advanced.Children.Add(_shoot);
        advanced.Children.Add(Text("Player region · normalized to the captured window. Keep the intended player inside this crop. Repositioning it invalidates calibration and requires restarting live view."));
        advanced.Children.Add(Pair(_left, _top));
        advanced.Children.Add(Pair(_width, _height));
        advanced.Children.Add(Pair(_confidence, _uncertainty));
        advanced.Children.Add(Pair(_frameAge, _lateness));
        advanced.Children.Add(Pair(_videoDelay, _inputDelay));
        advanced.Children.Add(_correction);
        advanced.Children.Add(Text("Calibration uses estimated combined streaming/input delay and reviewed practice outcomes. A positive correction sends later; a negative correction sends earlier. These values do not measure exact Xbox render or input-application times."));
        var settings = Row();
        _save.Click += (_, _) => RunAction(SaveSettings);
        settings.Children.Add(_save);
        AddButton(settings, "Export calibration template…", ExportCalibrationTemplate);
        AddButton(settings, "Review measured calibration…", ReviewCalibration);
        AddButton(settings, "Export timing diagnostics…", ExportDiagnostics);
        advanced.Children.Add(settings);
        _armed.Toggled += (_, _) =>
        {
            if (_refreshing) return;
            var runtime = _runtime();
            if (_armed.IsOn && _session?.Status.CanArm != true)
            {
                runtime?.SetVisionArmed(false);
                _notice.Text = "A fresh capture and compatible validated model are required before arming.";
            }
            else if (runtime?.SetVisionArmed(_armed.IsOn) != true && _armed.IsOn)
                _notice.Text = runtime?.VisionStatus.Message ?? "Controller monitor unavailable.";
            RefreshStatus();
        };
        var previewHost = new Grid { MinHeight = 180, MaxHeight = 480, HorizontalAlignment = HorizontalAlignment.Stretch };
        previewHost.Children.Add(_preview);
        _previewPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
        _previewPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        _previewPlaceholder.IsHitTestVisible = false;
        previewHost.Children.Add(_previewPlaceholder);
        _live.Children.Add(_trainingDetails);
        var training = Row();
        AddButton(training, "Review and label clip…", ReviewTake);
        _live.Children.Add(training);
        _live.Children.Add(previewHost);
        _live.Children.Add(_previewCaption);
        var diagnosticsContent = new StackPanel { Spacing = 10 };
        diagnosticsContent.Children.Add(_diagnosticStatus);
        diagnosticsContent.Children.Add(_performance);
        _diagnostics.Content = diagnosticsContent;
        _live.Children.Add(_diagnostics);
        _tabs.SelectionChanged += (_, _) =>
        {
            _liveSelected = ReferenceEquals(_tabs.SelectedItem, _liveTab);
            UpdatePresentationVisibility();
            RefreshStatus();
        };
        _shoot.SelectedItem = ControlId.X;
        _modelPicker.SelectionChanged += (_, _) => ModelSelectionChanged();
        _modelPicker.DropDownOpened += (_, _) => RefreshModelsFromFields();
        _animation.TextChanged += (_, _) => InvalidateCorrection();
        _source.SelectionChanged += (_, _) => SourceSelectionChanged();
        _source.DropDownOpened += (_, _) =>
        {
            if (!_busy && _session?.Status.Running != true) RunAction(RefreshWindows);
        };
        _shoot.SelectionChanged += (_, _) => InvalidateCorrection();
        _executionProvider.SelectionChanged += (_, _) => { InvalidateCorrection(); if (!_populating) RefreshModelsFromFields(); };
        foreach (var field in new[] { _left, _top, _width, _height, _videoDelay, _inputDelay })
            field.ValueChanged += (_, _) => InvalidateCorrection();
        AutomationProperties.SetName(_preview, "Remote Play capture preview");
        AutomationProperties.SetName(_armed, "Arm experimental vision release");
        AutomationProperties.SetName(_executionProvider, "Vision inference provider: CPU or CUDA");
        AutomationProperties.SetName(_tabs, "Remote Play Vision sections");
        AutomationProperties.SetName(_configurationTab, "Vision Configuration");
        AutomationProperties.SetName(_liveTab, "Vision Live Capture");
        AutomationProperties.SetName(_source, "Remote Play source window");
        AutomationProperties.SetName(_modelPicker, "Installed gameplay model or capture and recording only");
        AutomationProperties.SetName(_recordTraining, "Record and save a training clip");
        AutomationProperties.SetName(_stopTraining, "Stop the current training clip recording");
        AutomationProperties.SetName(stop, "Stop Remote Play live view");
        ApplyModelChoices(Array.Empty<VisionModelLibraryEntry>(), Array.Empty<string>());
        RefreshWindows();
        try
        {
            var profiles = new Nba2K27ShotProfileStore();
            if (profiles.LoadSelectedId() is Guid selected) _shotProfile = profiles.Load(selected);
        }
        catch (Exception error) { _notice.Text = "Could not load the selected shot profile: " + error.Message; }
    }

    private static TextBlock Text(string value) => new() { Text = value, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private static NumberBox Number(string label, double value, double min, double max)
    {
        var box = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 14 };
        AutomationProperties.SetName(box, label);
        return box;
    }
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private static ScrollViewer TabScroll(FrameworkElement content) => new()
    {
        Content = content, Margin = new Thickness(0, 10, 6, 4),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Enabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled
    };
    private static Grid Pair(Control left, Control right)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 1); return grid;
    }
    private void AddButton(Panel target, string label, Action action)
    {
        var button = new Button { Content = label, FontSize = 14 };
        button.Click += (_, _) => RunAction(action);
        target.Children.Add(button);
    }
    private void RunAction(Action action)
    {
        try { if (!_disposed) action(); }
        catch (Exception error) { _notice.Text = error.Message; }
    }

    internal void SetPageVisible(bool visible)
    {
        if (_disposed || _pageVisible == visible) return;
        _pageVisible = visible;
        UpdatePresentationVisibility();
        RefreshStatus();
    }

    private bool LivePresentationVisible => _pageVisible && _liveSelected && !_disposed;

    private void UpdatePresentationVisibility()
    {
        bool desired = LivePresentationVisible;
        if (_previewDesired == desired) return;
        _previewDesired = desired;
        _session?.SetPreviewEnabled(desired);
        if (desired && _session?.Status.Running == true) ShowWaitingForPreview();
    }

    private void ShowWaitingForPreview()
    {
        // Keep the allocated bitmap for reuse, but never label an older cached frame as current.
        _preview.Visibility = Visibility.Collapsed;
        _previewPlaceholder.Visibility = Visibility.Visible;
        _previewPlaceholder.Text = "Waiting for a fresh capture preview…";
        _previewCaption.Text = "Waiting for a fresh capture preview…";
    }

    internal void SetProfile(Guid id, string name)
    {
        if (_macroProfileId == id) return;
        Stop(); _macroProfileId = id; _profileId = _shotProfile?.Id ?? id; _profile.Text = "Macro input profile · " + name;
        try { LoadContextSettings(); _notice.Text = "Settings loaded. Observation and automatic release remain off."; }
        catch (Exception error) { Populate(VisionSettings.Default); _notice.Text = "Could not load vision settings: " + error.Message; }
    }
    internal void SetShotProfile(Nba2K27ShotProfile? profile)
    {
        if (_shotProfile?.Id == profile?.Id && _shotProfile?.ConfigurationFingerprint == profile?.ConfigurationFingerprint)
        { _shotProfile = profile; UpdateShotProfileText(); return; }
        Stop(); _shotProfile = profile; _profileId = profile?.Id ?? _macroProfileId;
        try { LoadContextSettings(); _notice.Text = "Shot configuration changed. Observation stopped; review the model and calibration before restarting."; }
        catch (Exception error) { Populate(VisionSettings.Default); _notice.Text = error.Message; }
    }
    private void UpdateShotProfileText() => _shotProfileInfo.Text = _shotProfile is { } profile
        ? $"NBA 2K27 · {profile.Name} · height {profile.HeightInches} in · 3PT {profile.ThreePointRating} · Camera {profile.CameraView}\nBase: {profile.Base} · Upper 1: {profile.UpperRelease1} · Upper 2: {profile.UpperRelease2}\nSpeed {profile.ShotSpeed}/4 · Blend {profile.Release1Percent}% / {profile.Release2Percent}% · Meter {(profile.UsesShotMeter ? "on · " + profile.MeterReleaseType : "off · release type not applicable")}"
        : "Select a saved NBA 2K27 player/shot profile in Plugins before collecting training data.";
    private void LoadContextSettings()
    {
        var settings = _store.Load(_profileId);
        if (_shotProfile is { } profile)
        {
            string configuration = "nba2k27:" + profile.ConfigurationFingerprint;
            if (settings.AnimationProfile != configuration)
                settings = settings with { AnimationProfile = configuration, ModelManifestPath = "", CorrectionMs = 0 };
            settings = settings with { CameraView = profile.CameraView };
        }
        Populate(settings); _animation.IsReadOnly = _shotProfile is not null; UpdateShotProfileText();
    }
    private void RefreshWindows()
    {
        if (_refreshingSources) return;
        if (_session?.Status.Running == true)
            throw new InvalidOperationException("Stop observation before refreshing or changing its source window.");
        _sourcesRefreshedAt = Stopwatch.GetTimestamp();
        var previous = _source.SelectedItem as CaptureSource;
        bool wasOpen = _source.IsDropDownOpen;
        var sources = RemotePlayCapture.EnumerateWindows();
        if (_source.ItemsSource is IReadOnlyList<CaptureSource> current && current.SequenceEqual(sources)) return;
        var selected = previous is null ? null
            : sources.FirstOrDefault(source => source.Hwnd == previous.Hwnd && source.ProcessId == previous.ProcessId);
        _refreshingSources = true;
        try
        {
            // Replace records so changed browser/window titles appear, without treating the
            // ItemsSource reset's temporary empty selection as a different capture source.
            _source.ItemsSource = sources;
            _source.SelectedItem = selected;
            if (wasOpen && !_source.IsDropDownOpen) _source.IsDropDownOpen = true;
        }
        finally { _refreshingSources = false; }
        SourceSelectionChanged();
    }
    private void SourceSelectionChanged()
    {
        if (_refreshingSources) return;
        var previous = _selectedSourceIdentity;
        var selected = _source.SelectedItem as CaptureSource;
        _selectedSourceIdentity = selected;
        if (previous?.Hwnd == selected?.Hwnd && previous?.ProcessId == selected?.ProcessId) return;
        InvalidateCorrection();
        if (previous is not null && selected is null)
            _notice.Text = "The selected window is no longer available. Choose a source window before starting observation.";
    }
    private void ModelSelectionChanged()
    {
        if (_refreshingModels || _populating || _modelPicker.SelectedItem is not ModelChoice selected) return;
        if (!string.Equals(_selectedModelPath, selected.ManifestPath, StringComparison.OrdinalIgnoreCase))
        {
            _selectedModelPath = selected.ManifestPath;
            InvalidateCorrection();
        }
        UpdateModelInfo();
    }
    private void UpdateModelInfo()
    {
        _modelPath.Text = _selectedModelPath.Length == 0 ? "No model selected" : _selectedModelPath;
        if (_modelPicker.SelectedItem is not ModelChoice selected) return;
        _modelInfo.Text = selected.Entry is { } entry
            ? $"{entry.DisplayName} · {entry.ModelVersion}\n{entry.Status}\nLive view checks model readiness again. Selecting a model does not arm release assistance."
            : selected.Status;
    }
    private void ApplyModelChoices(IReadOnlyList<VisionModelLibraryEntry> entries, IReadOnlyList<string> errors)
    {
        var choices = new List<ModelChoice>
        {
            new("", "No model — capture/record only", entries.Count == 0
                ? "No shooting model is installed yet. You can view Remote Play and record training clips without one."
                : "No model selected. Live view and training clips work without one. Choose an installed model only when you want animation inference.", true)
        };
        choices.AddRange(entries.Select(entry => new ModelChoice(entry.ManifestPath,
            entry.DisplayName + " · " + entry.ModelVersion + (entry.IsCompatible ? "" : " · different shot/camera"),
            entry.Status, true, entry)));
        var selected = choices.FirstOrDefault(choice => string.Equals(choice.ManifestPath, _selectedModelPath, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new(_selectedModelPath, "Saved model not installed — import its package",
                "The saved model is not installed in this library. Import its model package folder, or choose No model to use live view and record training clips.", false);
            choices.Add(selected);
        }
        bool wasOpen = _modelPicker.IsDropDownOpen;
        _refreshingModels = true;
        try
        {
            _modelPicker.ItemsSource = choices;
            _modelPicker.SelectedItem = selected;
            if (wasOpen && !_modelPicker.IsDropDownOpen) _modelPicker.IsDropDownOpen = true;
        }
        finally { _refreshingModels = false; }
        _modelErrors.Text = errors.Count == 0 ? "" : "Packages that could not be loaded:\n" + string.Join("\n", errors.Take(4));
        UpdateModelInfo();
    }
    private void RefreshModelsFromFields()
    {
        if (_disposed || _populating || _refreshingModels || _busy || _session?.Status.Running == true) return;
        try { RefreshModelLibrary(ReadSettings()); }
        catch (Exception error) { _modelInfo.Text = "Finish the settings fields before checking model compatibility. " + error.Message; }
    }
    private async void RefreshModelLibrary(VisionSettings settings)
    {
        // One discovery runs at a time; rapid settings/profile changes replace only the pending
        // context. No model file hashing or directory enumeration runs on the UI/inference loop.
        if (_modelsLoading && (_pendingModelContext == settings || (_pendingModelContext is null && _runningModelContext == settings))) return;
        _pendingModelContext = settings;
        _modelContextVersion++;
        if (_modelsLoading) return;
        _modelsLoading = true;
        RefreshStatus();
        try
        {
            while (_pendingModelContext is { } context)
            {
                _pendingModelContext = null;
                _runningModelContext = context;
                int version = _modelContextVersion;
                try
                {
                    var found = await Task.Run(() => _modelLibrary.Discover(context));
                    if (_disposed) return;
                    if (version == _modelContextVersion) ApplyModelChoices(found.Models, found.Errors);
                }
                catch (Exception error)
                {
                    if (!_disposed && version == _modelContextVersion)
                        _modelInfo.Text = "Could not read the model library. " + error.Message;
                }
            }
        }
        finally { _runningModelContext = null; _modelsLoading = false; RefreshStatus(); }
    }
    private VisionSettings ReadSettings()
    {
        if (_executionProvider.SelectedIndex is < 0 or > 1)
            throw new InvalidDataException("Choose CPU or CUDA for inference.");
        var settings = _loadedSettings with
        {
            ModelManifestPath = _selectedModelPath, AnimationProfile = _shotProfile is { } profile ? "nba2k27:" + profile.ConfigurationFingerprint : _animation.Text.Trim(),
            CameraView = _shotProfile?.CameraView ?? _loadedSettings.CameraView,
            ExecutionProvider = _executionProvider.SelectedIndex == 1 ? VisionExecutionProvider.Cpu : VisionExecutionProvider.Cuda,
            ShootButton = _shoot.SelectedItem is ControlId button ? button : ControlId.X,
            Crop = new(_left.Value / 100, _top.Value / 100, _width.Value / 100, _height.Value / 100),
            MinimumConfidence = _confidence.Value / 100, MaximumUncertaintyMs = _uncertainty.Value,
            MaximumFrameAgeMs = _frameAge.Value, EstimatedVideoDelayMs = _videoDelay.Value,
            EstimatedInputDelayMs = _inputDelay.Value, CorrectionMs = _correction.Value,
            MaximumLatenessMs = _lateness.Value
        };
        settings.Validate(); return settings;
    }
    private void Populate(VisionSettings settings)
    {
        _populating = true;
        try
        {
            _loadedSettings = settings;
            _selectedModelPath = settings.ModelManifestPath;
            ApplyModelChoices(Array.Empty<VisionModelLibraryEntry>(), Array.Empty<string>());
            _animation.Text = settings.AnimationProfile; _shoot.SelectedItem = settings.ShootButton;
            _executionProvider.SelectedIndex = settings.ExecutionProvider == VisionExecutionProvider.Cpu ? 1 : 0;
            _left.Value = settings.Crop.Left * 100; _top.Value = settings.Crop.Top * 100;
            _width.Value = settings.Crop.Width * 100; _height.Value = settings.Crop.Height * 100;
            _confidence.Value = settings.MinimumConfidence * 100; _uncertainty.Value = settings.MaximumUncertaintyMs;
            _frameAge.Value = settings.MaximumFrameAgeMs; _videoDelay.Value = settings.EstimatedVideoDelayMs;
            _inputDelay.Value = settings.EstimatedInputDelayMs; _correction.Value = settings.CorrectionMs;
            _lateness.Value = settings.MaximumLatenessMs;
        }
        finally { _populating = false; }
        RefreshModelLibrary(settings);
    }
    private void InvalidateCorrection()
    {
        if (_populating) return;
        _correction.Value = 0;
        _notice.Text = "Capture, model or timing configuration changed. The previous correction was cleared; review calibration again.";
    }
    private void SaveSettings()
    {
        if (_session?.Status.Running == true) throw new InvalidOperationException("Stop observation before changing its model, crop, or timing settings.");
        var settings = ReadSettings(); _store.Save(_profileId, settings); _runtime()?.ConfigureVision(settings);
        _notice.Text = "Vision settings saved for this macro profile. Revalidate calibration after changing source, model or animation.";
    }
    private void Start() => StartSession(loadModel: true);
    private void StartSession(bool loadModel)
    {
        if (_runtime() is not { } runtime) throw new InvalidOperationException("Controller monitor unavailable.");
        if (_source.SelectedItem is not CaptureSource source) throw new InvalidOperationException("Choose a window to observe.");
        if (loadModel && _modelPicker.SelectedItem is not ModelChoice { Available: true })
            throw new InvalidOperationException("Choose an installed model, import its package, or select No model for live view only.");
        var settings = ReadSettings(); _store.Save(_profileId, settings);
        _session ??= new VisionSession(runtime);
        _session.SetPreviewEnabled(LivePresentationVisible);
        ShowWaitingForPreview();
        _session.Start(source.Hwnd, settings, loadModel);
        _manualTrainingView = !loadModel;
        _workflowVersion++;
        _lastObservedSource = source.Hwnd;
        _notice.Text = ViewingNotice;
        RefreshStatus();
    }
    internal void Stop()
    {
        _workflowVersion++;
        _session?.Stop(); _runtime()?.SetVisionArmed(false);
        if (_notice.Text is ViewingNotice or RecordingNotice or FinalizingNotice)
            _notice.Text = "Live view stopped. Clip recording status is shown above; viewing alone does not save footage.";
        RefreshStatus();
    }

    internal void RefreshStatus()
    {
        if (_disposed) return;
        var session = _session?.Status;
        var runtime = _runtime()?.VisionStatus;
        var training = _session?.TrainingSnapshot;
        _refreshing = true;
        _armed.IsOn = runtime?.Armed == true;
        _armed.IsEnabled = session?.CanArm == true && runtime.HasValue;
        _start.IsEnabled = !_busy && !_modelsLoading && _session?.IsStopping != true && session?.Running != true && _source.SelectedItem is CaptureSource &&
            _modelPicker.SelectedItem is ModelChoice { Available: true };
        bool editable = session?.Running != true && !_busy;
        _refreshSources.IsEnabled = editable && !_busy;
        _importModel.IsEnabled = editable && !_modelsLoading;
        _recordTraining.IsEnabled = !_busy && _session?.IsStopping != true && training?.IsRecording != true &&
            (training is not { HasTake: true, IsFinalized: false });
        _stopTraining.IsEnabled = !_busy && training?.IsRecording == true;
        if (_pageVisible && !_liveSelected && editable && !_busy && !_source.IsDropDownOpen && !_refreshingSources &&
            (_sourcesRefreshedAt == 0 || Stopwatch.GetElapsedTime(_sourcesRefreshedAt).TotalSeconds >= 2))
            RunAction(RefreshWindows);
        if (_pageVisible && !_liveSelected && _configurationEditable != editable)
        {
            foreach (var control in _configurationControls) control.IsEnabled = editable;
            _configurationEditable = editable;
        }
        _save.IsEnabled = editable && !_modelsLoading;
        _refreshing = false;
        string statusText = session?.Running == true ? (_manualTrainingView ? "Live view for training is running." : "Live view is running.") : "Live view is stopped.";
        if (_session?.IsStopping == true) statusText = "Live view is stopping.";
        else if (session?.CaptureFailed == true) statusText = "Live view could not continue. Details are in Timing and diagnostics.";
        else if (session?.Running == true && session?.SourceStale == true) statusText += " The video is paused, stale or unavailable.";
        if (session is { Running: true, ModelLoadFailed: true }) statusText += " Selected AI model unavailable; viewing and recording still work.";
        if (session is { Frames: > 0 } observed) statusText += $" Live frames viewed: {observed.Frames:N0}.";
        if (_selectedModelPath.Length == 0) statusText += " No model is needed to record clips.";
        if (_status.Text != statusText)
        {
            _status.Text = statusText;
            ToolTipService.SetToolTip(_status, statusText);
        }
        UpdateTrainingStatus(training);
        if (session?.Running != true && _notice.Text == ViewingNotice)
            _notice.Text = "Live view is stopped. Viewing alone does not save training clips.";
        if ((_notice.Text == RecordingNotice && training?.IsRecording != true) ||
            (_notice.Text == FinalizingNotice && training?.IsFinalized == true))
            _notice.Text = training is { HasTake: true, IsFinalized: false } ? FinalizingNotice
                : training is { IsFinalized: true, IsComplete: true } ? "Training clip saved. Review and label it before model training."
                : "Training recording ended. Check the clip status above.";
        if (_pageVisible)
        {
            string context = _source.SelectedItem is CaptureSource source
                ? "Source: " + source.Title + " · " + (_shotProfile is { } profile ? "Shot: " + profile.Name + " · " + profile.CameraView : _profile.Text)
                : "Choose a Remote Play window in Configuration.";
            if (_sourceContext.Text != context)
            {
                _sourceContext.Text = context;
                ToolTipService.SetToolTip(_sourceContext, context);
            }
        }
        // Tab/page visibility controls presentation only. Capture, model processing, training
        // and controller arbitration continue independently on their existing workers.
        if (!LivePresentationVisible) return;
        // MainWindow refreshes preview/status frequently. Percentile snapshots allocate and sort
        // only on this low-rate presentation path, never on each UI tick or capture frame.
        long now = Stopwatch.GetTimestamp();
        if (_diagnostics.IsExpanded && (_performanceRefreshedAt == 0 || Stopwatch.GetElapsedTime(_performanceRefreshedAt, now).TotalMilliseconds >= 500))
        {
            _performanceRefreshedAt = now;
            _performance.Update(_session?.Performance);
            string details = session?.Message ?? "No live view session.";
            if (runtime is { } output) details += "\n" + output.State + " · " + output.Message;
            if (session is { } measured)
                details += $"\nLive frames viewed: {measured.Frames:N0} · timing samples excluded: {measured.ExcludedTimingFrames:N0}. These exclusions affect timing statistics; clip frame counts are shown separately.";
            _diagnosticStatus.Text = details;
        }
        if (_diagnostics.IsExpanded) _performance.SetBusy(_busy);
        if (_session?.TakePreview() is { } preview)
        {
            using (preview)
            {
                if (_bitmap is null || _bitmap.PixelWidth != preview.Width || _bitmap.PixelHeight != preview.Height)
                    _preview.Source = _bitmap = new WriteableBitmap(preview.Width, preview.Height);
                var access = _bitmap.PixelBuffer.As<IBufferByteAccess>();
                access.Buffer(out nint destination);
                Marshal.Copy(preview.Pixels, 0, destination, preview.Length);
                _bitmap.Invalidate();
                _preview.Visibility = Visibility.Visible;
                _previewPlaceholder.Visibility = Visibility.Collapsed;
                _previewCaption.Text = $"Capture preview · frame {preview.Sequence:N0}";
            }
        }
        if (session?.Running != true)
        {
            bool showingLastFrame = _bitmap is not null && _preview.Visibility == Visibility.Visible;
            _previewCaption.Text = showingLastFrame ? "Observation is off. The image is the last captured preview."
                : "Observation is off. Start observation to see a fresh capture preview.";
            if (!showingLastFrame)
            {
                _previewPlaceholder.Visibility = Visibility.Visible;
                _previewPlaceholder.Text = "Start observation to see a fresh capture preview.";
            }
        }
    }

    private void UpdateTrainingStatus(VisionTrainingStatus? training)
    {
        string summary;
        if (training is not { HasTake: true } take)
            summary = "Not recording — live view frames are not saved.";
        else if (take.IsRecording)
            summary = $"RECORDING training clip · {take.StoredFrames:N0} frames written · {take.DroppedFrames:N0} dropped.";
        else if (!take.IsFinalized)
            summary = $"Saving training clip · {take.StoredFrames:N0} frames written. Please wait.";
        else if (take.IsComplete)
            summary = $"Last training clip saved: {take.StoredFrames:N0} frames. Not recording — live view frames are not saved.";
        else
            summary = $"Training clip incomplete · {take.StoredFrames:N0} frames retained. Not recording.";
        if (_trainingInfo.Text != summary) _trainingInfo.Text = summary;
        if (!LivePresentationVisible) return;
        _trainingDetails.Text = training is { HasTake: true } clip
            ? clip.Message + "\nClip folder: " + clip.DirectoryPath + "\nReview and label captured shots before using them to train a model."
            : "Use Record training clip above to choose a save folder and record 10 seconds of manual gameplay. Live view starts automatically. A model is not required. Clips must be reviewed and labeled before model training.";
    }

    private async void ImportModelPackage()
    {
        if (_busy || _modelsLoading || _session?.Status.Running == true) return;
        _busy = true; RefreshStatus();
        try
        {
            var settings = ReadSettings();
            Guid profileId = _profileId;
            var picker = new FolderPicker(_windowId);
            if (await picker.PickSingleFolderAsync() is not { } folder) return;
            string manifest = Path.Combine(folder.Path, "manifest.json");
            if (!File.Exists(manifest))
                throw new InvalidDataException("Choose an exported model package folder containing manifest.json and its model files. A gameplay model is not included with the app.");
            var imported = await Task.Run(() => _modelLibrary.Import(manifest, settings));
            if (_disposed) return;
            if (_profileId == profileId && ReadSettings() == settings)
            {
                if (!string.Equals(_selectedModelPath, imported.ManifestPath, StringComparison.OrdinalIgnoreCase))
                { _selectedModelPath = imported.ManifestPath; InvalidateCorrection(); }
                _notice.Text = "Model package imported. Selecting a model does not arm release assistance.";
            }
            else _notice.Text = "Model package imported. Select it from the library for the current profile if it is compatible.";
            RefreshModelLibrary(ReadSettings());
        }
        catch (Exception error) { if (!_disposed) _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void ImportSettings()
    {
        if (_busy || _session?.Status.Running == true) return;
        _busy = true;
        try
        {
            var picker = new FileOpenPicker(_windowId); picker.FileTypeFilter.Add(".json");
            if (await picker.PickSingleFileAsync() is { } file)
            {
                var imported = _store.Import(file.Path);
                if (_shotProfile is { } profile && imported.AnimationProfile != "nba2k27:" + profile.ConfigurationFingerprint)
                    throw new InvalidDataException("These settings belong to a different player/shot configuration. Select the matching shot profile first.");
                if (_shotProfile is { } selectedProfile && imported.CameraView != selectedProfile.CameraView)
                    throw new InvalidDataException("These settings use a different camera view. Select the matching shot profile first.");
                Populate(imported);
                _notice.Text = "Imported settings. Save and review calibration before starting observation. Automatic release is off.";
            }
        }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void ExportSettings()
    {
        if (_busy) return; _busy = true;
        try
        {
            var settings = ReadSettings();
            var picker = new FileSavePicker(_windowId) { SuggestedFileName = "AccessRelay-Vision-Settings" };
            picker.FileTypeChoices.Add("Vision settings", [".json"]);
            if (await picker.PickSaveFileAsync() is { } file) _store.Export(file.Path, _profileId, settings);
        }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void ExportDiagnostics()
    {
        if (_busy || _session is null) return; _busy = true;
        try
        {
            var picker = new FileSavePicker(_windowId) { SuggestedFileName = "AccessRelay-Vision-Diagnostics" };
            picker.FileTypeChoices.Add("Timing diagnostics", [".json"]);
            if (await picker.PickSaveFileAsync() is { } file) await Task.Run(() => _session.ExportDiagnostics(file.Path));
        }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void SavePerformanceRun()
    {
        if (_busy || _session is null) return;
        _busy = true; RefreshStatus();
        try
        {
            string label = _performance.RunLabel;
            var run = _session.SavePerformanceRun(label.Length == 0 ? "Vision performance run" : label);
            if (run.Snapshot.TotalFrames == 0)
                throw new InvalidOperationException("Observe a window before saving a performance run.");
            var picker = new FileSavePicker(_windowId) { SuggestedFileName = "AccessRelay-Vision-Performance" };
            picker.FileTypeChoices.Add("Vision performance run", [".json"]);
            if (await picker.PickSaveFileAsync() is { } file)
            {
                await Task.Run(() => VisionPerformanceRuns.Write(file.Path, run));
                if (_disposed) return;
                _performance.RememberRun(run, selectAsBaseline: false);
                _notice.Text = "Performance run saved. Choose a baseline to compare matching runs; a live comparison does not control the gameplay scene.";
            }
        }
        catch (Exception error) { if (!_disposed) _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void LoadPerformanceRun()
    {
        if (_busy) return;
        _busy = true; RefreshStatus();
        try
        {
            var picker = new FileOpenPicker(_windowId); picker.FileTypeFilter.Add(".json");
            if (await picker.PickSingleFileAsync() is { } file)
            {
                var run = await Task.Run(() => VisionPerformanceRuns.Read(file.Path));
                if (_disposed) return;
                _performance.RememberRun(run, selectAsBaseline: true);
                _notice.Text = "Performance baseline loaded. Deltas require matching configuration and enough model decisions in both runs.";
            }
        }
        catch (Exception error) { if (!_disposed) _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void RecordTraining()
    {
        if (_busy) return;
        _busy = true; RefreshStatus();
        try
        {
            if (_shotProfile is not { } profile) throw new InvalidOperationException("Create and select a complete NBA 2K27 player/shot profile in Plugins first.");
            profile.Validate();
            if (_source.SelectedItem is not CaptureSource source) throw new InvalidOperationException("Choose the Remote Play window in Configuration before recording a clip.");
            var runtime = _runtime() ?? throw new InvalidOperationException("Controller monitor unavailable.");
            if (_session?.IsStopping == true)
                throw new InvalidOperationException("Live view is still stopping. Wait for it to finish, then record a clip.");
            if (runtime.ScriptsEnabled || runtime.Recording.IsRecording)
                throw new InvalidOperationException("Turn off macro scripts and finish macro recording before recording a manual training clip.");
            if (_session?.TrainingSnapshot is { HasTake: true, IsFinalized: false })
                throw new InvalidOperationException("Wait for the current clip to finish recording or saving.");
            var settings = ReadSettings();
            int workflowVersion = _workflowVersion;
            Guid profileId = _profileId;
            var picker = new FolderPicker(_windowId);
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                if (_disposed) return;
                var selectedProfile = _shotProfile;
                if (_workflowVersion != workflowVersion || _profileId != profileId || selectedProfile is null || selectedProfile.Id != profile.Id ||
                    selectedProfile.ConfigurationFingerprint != profile.ConfigurationFingerprint || ReadSettings() != settings ||
                    _source.SelectedItem is not CaptureSource current || current.Hwnd != source.Hwnd || current.ProcessId != source.ProcessId ||
                    !ReferenceEquals(runtime, _runtime()))
                    throw new InvalidOperationException("The source or shot settings changed while choosing a folder. Check Configuration and choose Record training clip again.");
                var currentSource = RemotePlayCapture.EnumerateWindows().FirstOrDefault(window => window.Hwnd == source.Hwnd && window.ProcessId == source.ProcessId)
                    ?? throw new InvalidOperationException("The selected Remote Play window is no longer available. Choose its window again.");
                if (runtime.ScriptsEnabled || runtime.Recording.IsRecording)
                    throw new InvalidOperationException("Turn off macro scripts and finish macro recording before recording a manual training clip.");
                if (_session?.IsStopping == true)
                    throw new InvalidOperationException("Live view is still stopping. Wait for it to finish, then record a clip.");
                if (_session?.Status.Running != true) StartSession(loadModel: false);
                _session!.StartTraining(folder.Path, profile, currentSource.Title);
                _tabs.SelectedItem = _liveTab;
                _notice.Text = RecordingNotice;
            }
        }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void ReviewCalibration()
    {
        if (_busy || _session?.Status.Running == true) return;
        _busy = true;
        try
        {
            var picker = new FileOpenPicker(_windowId); picker.FileTypeFilter.Add(".json");
            if (await picker.PickSingleFileAsync() is not { } file) return;
            var settings = ReadSettings();
            var source = _session?.Status;
            if (source is not { CaptureWidth: > 0, CaptureHeight: > 0 } ||
                _source.SelectedItem is not CaptureSource selected || selected.Hwnd != _lastObservedSource)
                throw new InvalidOperationException("Observe the selected source, then stop observation before reviewing calibration for its measured dimensions.");
            var (manifest, manifestHash) = VisionModelManifest.Read(settings.ModelManifestPath);
            var imported = JsonSerializer.Deserialize<VisionCalibrationImport>(
                VisionModelManifest.ReadBounded(file.Path, 256 * 1024), VisionModelManifest.Json)
                ?? throw new InvalidDataException("The calibration file must contain configuration-bound, reviewed measured shot-error samples.");
            imported.ValidateContext(manifestHash, manifest, settings, source.Value.CaptureWidth, source.Value.CaptureHeight);
            var estimate = VisionCalibration.Estimate(imported.Attempts, settings.CorrectionMs);
            var dialog = StudioDialog.Create(XamlRoot, ActualTheme);
            dialog.Title = "Review timing correction";
            dialog.Content = Text($"{estimate.ReviewedAttempts} reviewed measured attempts\nMedian signed error: {estimate.MedianSignedErrorMs:F2} ms\nResidual p95: {estimate.ResidualP95Ms:F2} ms\nProposed correction: {estimate.RecommendedCorrectionMs:F2} ms\n\nPositive error means the release arrived late. Applying this changes the local timing setting; it does not certify gameplay accuracy.");
            dialog.PrimaryButtonText = "Use correction"; dialog.CloseButtonText = "Cancel";
            dialog.DefaultButton = ContentDialogButton.Close;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _correction.Value = estimate.RecommendedCorrectionMs;
                SaveSettings();
                VisionCalibration.Save(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AccessRelayStudio", "Vision", "Calibration", _profileId.ToString("N") + ".json"), new VisionCalibrationRecord
                {
                    ModelManifestHash = manifestHash, AnimationProfile = settings.AnimationProfile,
                    GameVersion = manifest.GameVersion, CameraConfiguration = manifest.CameraConfiguration, ExecutionProvider = settings.ExecutionProviderName,
                    ProcedureVersion = imported.ProcedureVersion, CaptureWidth = source.Value.CaptureWidth,
                    CaptureHeight = source.Value.CaptureHeight, Crop = settings.Crop, CreatedAtUtc = DateTimeOffset.UtcNow,
                    Estimate = estimate
                });
                _notice.Text = "Measured correction saved. Validate it in a new practice session before relying on it.";
            }
        }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void ExportCalibrationTemplate()
    {
        if (_busy || _session?.Status.Running == true) return;
        _busy = true;
        try
        {
            var settings = ReadSettings();
            var source = _session?.Status;
            if (source is not { CaptureWidth: > 0, CaptureHeight: > 0 } ||
                _source.SelectedItem is not CaptureSource selected || selected.Hwnd != _lastObservedSource)
                throw new InvalidOperationException("Observe the selected source, then stop observation before exporting its calibration context.");
            var (manifest, hash) = VisionModelManifest.Read(settings.ModelManifestPath);
            if (manifest.AnimationProfile != settings.AnimationProfile ||
                (settings.CameraView.Length != 0 && manifest.CameraConfiguration != settings.CameraView))
                throw new InvalidDataException("Select the model for this player/shot configuration first.");
            var template = new VisionCalibrationImport
            {
                ModelManifestHash = hash, AnimationProfile = settings.AnimationProfile, GameVersion = manifest.GameVersion,
                CameraConfiguration = manifest.CameraConfiguration, ExecutionProvider = settings.ExecutionProviderName, ProcedureVersion = "",
                CaptureWidth = source.Value.CaptureWidth, CaptureHeight = source.Value.CaptureHeight, Crop = settings.Crop,
                PreviousCorrectionMs = settings.CorrectionMs, EstimatedVideoDelayMs = settings.EstimatedVideoDelayMs,
                EstimatedInputDelayMs = settings.EstimatedInputDelayMs
            };
            var picker = new FileSavePicker(_windowId) { SuggestedFileName = "AccessRelay-Calibration-Samples" };
            picker.FileTypeChoices.Add("Calibration samples", [".json"]);
            if (await picker.PickSaveFileAsync() is { } file)
            {
                File.WriteAllText(file.Path, JsonSerializer.Serialize(template, VisionModelManifest.Json));
                _notice.Text = "Calibration context exported. Add the measurement procedure and at least five reviewed signed-error attempts; no measurements were invented.";
            }
        }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    private async void ReviewTake()
    {
        if (_busy) return;
        _busy = true;
        try { await VisionDatasetReview.ShowAsync(XamlRoot, ActualTheme, _windowId); }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; RefreshStatus(); }
    }
    public void Dispose() { if (_disposed) return; _session?.Dispose(); _disposed = true; }
}
