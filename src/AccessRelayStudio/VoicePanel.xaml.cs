using EliteScriptStudio.Audio;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EliteScriptStudio;

internal readonly record struct LiveVoiceControlState(bool IsStarting, bool IsRunning, bool CanStart, string Message);
internal readonly record struct VoiceProfileControlState(IReadOnlyList<VoiceProfile> Profiles, Guid? SelectedProfileId,
    bool CanSelect, bool IsLoading, string Message);

public sealed partial class VoicePanel : UserControl, IDisposable
{
    private readonly VoiceSession _session = new();
    private readonly VoiceProfiles _profiles = new();
    private readonly VoiceProfileEditor _profileEditor;
    private VoiceDevices? _devices;
    private bool _loaded;
    private bool _restoringVoiceSelection;
    private bool _refreshing;
    private bool _stacked;
    private string? _previewOutputId;
    private string? _error;
    private string? _opening;
    private string? _profileMessage;
    private CancellationTokenSource? _start;
    private CancellationTokenSource? _profileSwitch;
    private IReadOnlyList<VoiceProfile> _availableProfiles = [];
    private VoiceProfile? _selectedProfile;
    private bool _headphoneSession;
    private bool _deletingProfile;
    private bool _disposed;

    public VoicePanel(WindowId windowId)
    {
        InitializeComponent();
        _profileEditor = new VoiceProfileEditor(windowId, _profiles);
        _profileEditor.ProfileSaved += VoiceProfileSaved;
        CreateVoiceProfileHost.Content = _profileEditor;
        Microphone.DisplayMemberPath = nameof(VoiceEndpoint.Name);
        Playback.DisplayMemberPath = nameof(VoiceEndpoint.Name);
        Model.DisplayMemberPath = nameof(VoiceModelOption.Name);
        Profile.DisplayMemberPath = nameof(VoiceProfile.Name);
        Model.ItemsSource = VoiceModelOption.Installed();
        Model.SelectedIndex = 0;
        Preset.ItemsSource = Enum.GetValues<VoicePreset>();
        Preset.SelectedItem = VoicePreset.Clean;
        Loaded += (_, _) => EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        if (_loaded || _disposed)
            return;
        _loaded = true;
        DiscoverDevices();
        RestoreVoiceSelection();
        UpdateModel();
        UpdateSettings();
    }

    internal LiveVoiceControlState GetLiveVoiceState()
    {
        if (_disposed)
            return new(false, false, false, "Voice is unavailable.");

        EnsureInitialized();
        var status = _session.Status;
        if (_headphoneSession && (_start is not null || status.IsRunning))
            return new(false, false, false, "Stop the headphone preview in Live Voice to start the virtual microphone.");

        bool starting = _start is { IsCancellationRequested: false };
        bool canStart = !_deletingProfile && _start is null && !status.IsRunning && _profileSwitch is null && CanStart(useHeadphones: false);
        string message = _error ?? _opening ?? (!status.IsRunning && VirtualMicPipe.SetupIssue is { } issue ? issue
            : Input is null ? "Choose a microphone in Live Voice."
            : SelectedModel is not { } model ? "Choose a voice model in Live Voice."
            : model.UsesReference && SelectedProfile is null ? "Choose a voice profile in Live Voice."
            : status.Message);
        return new(starting, status.IsRunning, canStart, message);
    }

    internal Task SetLiveVoiceEnabledAsync(bool enabled)
    {
        if (_disposed)
            return Task.CompletedTask;
        if (enabled)
        {
            EnsureInitialized();
            return StartVoiceAsync(useHeadphones: false);
        }
        if (!_headphoneSession)
            Stop();
        return Task.CompletedTask;
    }

    internal VoiceProfileControlState GetVoiceProfileState()
    {
        EnsureInitialized();
        var selected = SelectedModel?.UsesReference == true ? _selectedProfile : null;
        string message = _profileMessage ?? (_availableProfiles.Count == 0 ? "Import a voice profile in Live Voice."
            : ReferenceModel is null ? "A reference-voice model is required."
            : selected is not null ? string.Empty
            : "Choose an imported voice profile.");
        return new(_availableProfiles, selected?.Id, CanSelectProfile, _profileSwitch is not null, message);
    }

    internal bool CanDeleteSelectedProfile
    {
        get
        {
            EnsureInitialized();
            return CanDeleteProfile(_session.Status.IsRunning);
        }
    }

    internal async Task SelectVoiceProfileAsync(Guid profileId)
    {
        EnsureInitialized();
        if (!CanSelectProfile || (_selectedProfile?.Id == profileId && SelectedModel?.UsesReference == true)
            || ReferenceModel is not { } model || _availableProfiles.FirstOrDefault(candidate => candidate.Id == profileId) is not { } profile)
            return;

        bool running = _session.Status.IsRunning;
        using var change = new CancellationTokenSource();
        _profileSwitch = change;
        _profileMessage = running ? $"Loading {profile.Name}…" : null;
        UpdateStatus();
        try
        {
            if (running)
                await _session.SwitchVirtualProfileAsync(model, profile, change.Token);
            if (_disposed)
                return;

            _restoringVoiceSelection = true;
            try
            {
                SetSelectedProfile(profile);
                Model.SelectedItem = model;
            }
            finally
            {
                _restoringVoiceSelection = false;
            }
            _profileMessage = null;
            SaveVoiceSelection();
            UpdateModel();
            UpdateSettings();
        }
        catch (OperationCanceledException) when (change.IsCancellationRequested)
        {
            _profileMessage = null;
        }
        catch (Exception error)
        {
            _profileMessage = change.IsCancellationRequested ? null : $"Couldn’t load {profile.Name}. {error.Message}";
        }
        finally
        {
            _profileSwitch = null;
            if (!_disposed)
                UpdateStatus();
        }
    }

    public bool UpdateStatus()
    {
        var status = _session.Status;
        bool running = status.IsRunning;
        string message = _error ?? _opening ?? _profileMessage ?? status.Message;
        if (SessionStatus.Text != message)
            SessionStatus.Text = message;
        InputMeter.Value = Meter(status.InputPeak);
        OutputMeter.Value = Meter(status.OutputPeak);
        bool recordedSession = _headphoneSession && SelectedModel?.UsesReference == true;
        BufferStatus.Text = running && !recordedSession ? $"Audio queue · {status.BufferedMilliseconds} ms buffered" : "";
        bool starting = _start is not null;
        StartStop.Content = (running, starting, _headphoneSession, IsPreview, IsRecordedPreview) switch
        {
            (true, _, true, _, _) => "Stop preview",
            (true, _, false, _, _) => "Stop voice",
            (_, true, _, _, _) => "Cancel start",
            (_, _, _, _, true) => "Record voice preview",
            (_, _, _, true, _) => "Start headphone preview",
            _ => "Start virtual mic"
        };
        StartStop.IsEnabled = running || _start is { IsCancellationRequested: false }
            || (!starting && !_deletingProfile && _profileSwitch is null && CanStart(IsPreview));
        bool editable = !running && !starting && !_deletingProfile && _profileSwitch is null;
        Microphone.IsEnabled = Playback.IsEnabled = HeadphoneMode.IsEnabled = RefreshDevices.IsEnabled = editable;
        Model.IsEnabled = editable;
        Profile.IsEnabled = CanSelectProfile;
        DeleteVoiceProfileButton.IsEnabled = CanDeleteProfile(running);
        DeleteVoiceProfileHint.Visibility = running && SelectedProfile is not null ? Visibility.Visible : Visibility.Collapsed;
        return running;
    }

    public void Stop()
    {
        _error = null;
        _profileMessage = null;
        _profileSwitch?.Cancel();
        if (_start is { } start)
        {
            _opening = "Stopping voice…";
            start.Cancel();
        }
        _session.Stop();
        UpdateStatus();
    }

    public void Dispose()
    {
        _disposed = true;
        _start?.Cancel();
        _profileSwitch?.Cancel();
        _profileEditor.Dispose();
        _session.Dispose();
    }

    private void ArrangeCards(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 850;
        if (stacked == _stacked)
            return;
        _stacked = stacked;
        VoiceRightColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(TransformCard, stacked ? 0 : 1);
        Grid.SetRow(TransformCard, stacked ? 1 : 0);
    }

    private bool IsPreview => HeadphoneMode.IsChecked == true;
    private bool IsRecordedPreview => IsPreview && SelectedModel?.UsesReference == true;
    private VoiceEndpoint? Input => Microphone.SelectedItem as VoiceEndpoint;
    private VoiceEndpoint? Output => Playback.SelectedItem as VoiceEndpoint;
    private VoiceModelOption? SelectedModel => Model.SelectedItem as VoiceModelOption;
    private VoiceProfile? SelectedProfile => _selectedProfile;
    private VoiceModelOption? ReferenceModel => SelectedModel is { UsesReference: true } model ? model
        : (Model.ItemsSource as VoiceModelOption[])?.FirstOrDefault(option => option.UsesReference);
    private bool CanSelectProfile => !_disposed && !_deletingProfile && _start is null && _profileSwitch is null
        && (!_headphoneSession || !_session.Status.IsRunning) && ReferenceModel is not null && _availableProfiles.Count != 0;
    private bool CanDeleteProfile(bool running) => !_disposed && !_deletingProfile && _start is null && _profileSwitch is null
        && !running && SelectedProfile is not null;
    private bool CanStart(bool useHeadphones) => Input is not null && (useHeadphones ? Output is not null : VirtualMicPipe.SetupIssue is null)
        && SelectedModel is { } model && (!model.UsesReference || SelectedProfile is not null);

    private void RefreshClicked(object sender, RoutedEventArgs args) => DiscoverDevices();

    private void DiscoverDevices()
    {
        _error = null;
        try
        {
            _devices = VoiceSession.Discover();
        }
        catch (Exception error)
        {
            _devices = null;
            _error = $"Couldn’t list Windows audio devices. {error.Message}";
        }
        UpdateRoutes();
    }

    private void UpdateRoutes()
    {
        _refreshing = true;
        string? previousInput = Input?.Id;
        var microphones = _devices?.Microphones ?? [];
        var outputs = _devices?.Outputs ?? [];
        Microphone.ItemsSource = microphones;
        Playback.ItemsSource = outputs;
        Microphone.SelectedItem = Pick(microphones, previousInput);
        Playback.SelectedItem = outputs.FirstOrDefault(device => device.Id == _previewOutputId);
        Playback.Visibility = IsPreview ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = false;
        UpdateRouteHelp();
    }

    private static VoiceEndpoint? Pick(VoiceEndpoint[] endpoints, string? id) =>
        endpoints.FirstOrDefault(endpoint => endpoint.Id == id)
        ?? endpoints.FirstOrDefault(endpoint => endpoint.IsDefault) ?? endpoints.FirstOrDefault();

    private void UpdateRouteHelp()
    {
        RouteHelp.Text = (IsPreview, IsRecordedPreview) switch
        {
            (_, true) => $"Choose headphones, then speak when the {NeuralVoicePreview.CaptureSeconds}-second recording starts. Studio plays the processed sample after recording ends.",
            (true, _) => "Choose headphones to hear the processed voice without speaker feedback.",
            _ when VirtualMicPipe.SetupIssue is { } issue => issue,
            _ => "Start the bundled virtual microphone, then select AccessRelay Virtual Microphone in the app you want to use. Windows may ask for administrator approval the first time."
        };
        UpdateStatus();
    }

    private void RouteChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loaded && !_refreshing)
        {
            if (IsPreview && Output is { } output)
                _previewOutputId = output.Id;
            UpdateRouteHelp();
        }
    }

    private void PreviewChanged(object sender, RoutedEventArgs args)
    {
        if (_loaded)
            UpdateRoutes();
    }

    private void ModelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _restoringVoiceSelection)
            return;
        SaveVoiceSelection();
        UpdateModel();
        UpdateSettings();
    }

    private async void ProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _restoringVoiceSelection || Profile.SelectedItem is not VoiceProfile profile
            || profile.Id == _selectedProfile?.Id)
            return;
        _restoringVoiceSelection = true;
        try { Profile.SelectedItem = _selectedProfile; }
        finally { _restoringVoiceSelection = false; }
        await SelectVoiceProfileAsync(profile.Id);
    }

    private void UpdateModel()
    {
        var model = SelectedModel;
        bool neural = model?.UsesReference == true;
        ModelHelp.Text = model?.Description ?? "Choose a voice model.";
        if (neural && SelectedProfile is null)
            ModelHelp.Text += " Import a clean 1–30 second speech clip to start.";
        else if (neural && SelectedProfile is { } profile)
            ModelHelp.Text += $" Saved locally · {profile.Duration.TotalSeconds:0.000} s";
        Preset.Visibility = Pitch.Visibility = neural ? Visibility.Collapsed : Visibility.Visible;
        UpdateRouteHelp();
    }

    private void RestoreVoiceSelection()
    {
        _restoringVoiceSelection = true;
        try
        {
            var selection = _profiles.LoadSelection();
            LoadProfiles(selection?.ProfileId);
            if (Model.ItemsSource is VoiceModelOption[] models)
            {
                var savedModel = selection is null ? null : models.FirstOrDefault(model => model.Package == selection.ModelPackage);
                Model.SelectedItem = savedModel ?? (SelectedProfile is null ? null : models.FirstOrDefault(model => model.UsesReference))
                    ?? models.FirstOrDefault();
            }
        }
        catch (Exception error)
        {
            _error = $"Couldn’t read local voice profiles. {error.Message}";
        }
        finally
        {
            _restoringVoiceSelection = false;
        }
    }

    private void LoadProfiles(Guid? select) => SetProfiles(_profiles.LoadAll(), select);

    private void SetProfiles(IReadOnlyList<VoiceProfile> profiles, Guid? select)
    {
        _availableProfiles = profiles;
        Profile.ItemsSource = _availableProfiles;
        SetSelectedProfile(_availableProfiles.FirstOrDefault(profile => profile.Id == select) ?? _availableProfiles.FirstOrDefault());
    }

    private void SetSelectedProfile(VoiceProfile? profile) => Profile.SelectedItem = _selectedProfile = profile;

    private void SaveVoiceSelection()
    {
        _error = null;
        try
        {
            _profiles.SaveSelection(SelectedProfile?.Id, SelectedModel?.Package);
        }
        catch (Exception error)
        {
            _error = $"Couldn’t remember the selected voice profile and model. {error.Message}";
        }
    }

    private void OpenVoiceProfileEditor(object sender, RoutedEventArgs args) => VoiceTabs.SelectedItem = CreateVoiceProfileTab;

    private async void DeleteVoiceProfile(object sender, RoutedEventArgs args)
    {
        if (XamlRoot is { } root)
            await DeleteSelectedProfileAsync(root, ActualTheme);
    }

    internal async Task DeleteSelectedProfileAsync(XamlRoot root, ElementTheme theme, Guid? expectedProfileId = null)
    {
        if (!CanDeleteSelectedProfile || SelectedProfile is not { } profile
            || (expectedProfileId is Guid id && (profile.Id != id || SelectedModel?.UsesReference != true)))
            return;

        _deletingProfile = true;
        UpdateStatus();
        try
        {
            bool confirmed = await ProfileDeletionDialog.ConfirmAsync(root, theme, "voice", profile.Name,
                "This deletes the saved reference audio. The original audio or video stays on your computer.");
            if (!confirmed || _disposed || _session.Status.IsRunning || SelectedProfile?.Id != profile.Id)
                return;

            _profiles.Delete(profile.Id);
            _restoringVoiceSelection = true;
            try { SetProfiles(_availableProfiles.Where(candidate => candidate.Id != profile.Id).ToArray(), null); }
            finally { _restoringVoiceSelection = false; }
            SaveVoiceSelection();
            _profileMessage = $"Deleted “{profile.Name}”.";
            UpdateModel();
        }
        catch (Exception error)
        {
            if (!_disposed)
                _error = $"Couldn’t delete “{profile.Name}”. {error.Message}";
        }
        finally
        {
            _deletingProfile = false;
            if (!_disposed)
                UpdateStatus();
        }
    }

    private void VoiceTabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ReferenceEquals(VoiceTabs?.SelectedItem, CreateVoiceProfileTab))
            _profileEditor?.PausePlayback();
    }

    private void VoiceProfileSaved(object? sender, VoiceProfile profile)
    {
        if (_disposed)
            return;
        bool selectNew = _start is null && _profileSwitch is null && !_session.Status.IsRunning;
        _restoringVoiceSelection = true;
        try
        {
            LoadProfiles(selectNew ? profile.Id : SelectedProfile?.Id);
            if (selectNew && ReferenceModel is { } model)
                Model.SelectedItem = model;
        }
        catch (Exception error)
        {
            _error = $"Couldn’t refresh the saved voice profiles. {error.Message}";
            return;
        }
        finally { _restoringVoiceSelection = false; }

        if (selectNew)
        {
            _profileMessage = null;
            SaveVoiceSelection();
        }
        UpdateModel();
        UpdateSettings();
    }

    private VoiceSettings Settings() => SelectedModel?.UsesReference == true
        ? new(VoicePreset.Clean, 0, Gain.Value, Mute.IsOn)
        : new((VoicePreset)(Preset.SelectedItem ?? VoicePreset.Clean), Pitch.Value, Gain.Value, Mute.IsOn);

    private void UpdateSettings()
    {
        if (!_loaded)
            return;
        var settings = Settings();
        Pitch.Header = $"Pitch · {Pitch.Value:+0.0;-0.0;0.0} semitones";
        Gain.Header = $"Output gain · {Gain.Value:+0;-0;0} dB";
        _session.SetSettings(settings);
    }

    private void SettingsChanged(object sender, RoutedEventArgs args) => UpdateSettings();
    private void SettingValueChanged(object sender, RangeBaseValueChangedEventArgs args) => UpdateSettings();

    private async void ToggleVoice(object sender, RoutedEventArgs args)
    {
        if (_session.Status.IsRunning || _start is not null)
        {
            Stop();
            return;
        }
        await StartVoiceAsync(IsPreview);
    }

    private async Task StartVoiceAsync(bool useHeadphones)
    {
        if (_disposed || _deletingProfile || _start is not null || _session.Status.IsRunning || _profileSwitch is not null || !CanStart(useHeadphones)
            || Input is not { } microphone || SelectedModel is not { } model)
            return;

        var output = useHeadphones ? Output : null;
        _error = null;
        using var start = new CancellationTokenSource();
        _start = start;
        _headphoneSession = useHeadphones;
        try
        {
            if (output is not null)
            {
                _opening = model.UsesReference ? $"Loading {model.Name} and your voice profile…" : "Starting headphone preview…";
                UpdateStatus();
                await _session.StartPreviewAsync(microphone.Id, output.Id, Settings(), model, SelectedProfile, start.Token);
            }
            else
            {
                if (!VirtualMicDriver.IsAvailable)
                {
                    _opening = "Setting up the bundled Windows virtual microphone…";
                    UpdateStatus();
                    await VirtualMicDriver.InstallAsync();
                    start.Token.ThrowIfCancellationRequested();
                }
                _opening = model.UsesReference ? $"Loading {model.Name} and starting the AccessRelay microphone…"
                    : "Waiting for Windows to register the AccessRelay microphone…";
                UpdateStatus();
                await _session.StartVirtualAsync(microphone.Id, Settings(), model, SelectedProfile, start.Token);
            }
            if (!_disposed)
                UpdateSettings();
        }
        catch (OperationCanceledException) when (start.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && !start.IsCancellationRequested)
                _error = $"Couldn’t start the voice route. {error.Message}";
        }
        finally
        {
            _start = null;
            _opening = null;
            if (!_disposed)
                UpdateStatus();
        }
    }

    private static double Meter(float peak) =>
        peak > 0 ? Math.Clamp((20 * Math.Log10(peak) + 60) / 60 * 100, 0, 100) : 0;
}
