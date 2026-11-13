using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EliteScriptStudio.Audio;
using EliteScriptStudio.Core;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace EliteScriptStudio.Desktop;

/// <summary>UI-independent voice routes and local profile preparation. No browser audio processing.</summary>
internal sealed class DesktopVoiceService : IDesktopModule
{
    private readonly WindowId _owner;
    private readonly VoiceSession _session = new();
    private readonly VoiceProfiles _profiles = new(DesktopEnvironment.DataDirectory("VoiceProfiles"));
    private VoiceDevices _devices = new([], []);
    private VoiceModelOption[] _models = [];
    private IReadOnlyList<VoiceProfile> _availableProfiles = [];
    private VoiceSettings _settings = new(VoicePreset.Clean, 0, 0, false);
    private string? _microphoneId, _outputId, _selectedProfileId;
    private string _selectedModelId = "builtin", _destination = "virtual";
    private string _message = "Choose a microphone and voice, then preview or start the virtual microphone.";
    private CancellationTokenSource? _operation;
    private string? _operationName;
    private bool _disposed;
    private string? _source;
    private string _clipName = "", _clipId = "";
    private VoiceWaveform? _waveform;
    private int _startMs, _endMs;
    private VoiceTrimPreview? _clipPreview;
    private VoiceMusicRemoval.Result? _cleaned;
    private int _cleanedStartMs, _cleanedEndMs;
    private bool _useCleaned;
    private readonly Func<StudioRuntime?> _runtime;
    private readonly VoiceCommandStore _commandStore = new(DesktopEnvironment.DataDirectory("VoiceCommands"));
    private VoiceCommandSettings _commandSettings = new();
    private Guid _commandProfile;
    private VoiceCommandSession? _commandSession;
    private CancellationTokenSource? _commandLearning;
    private long _commandGeneration;
    private Task _commandRetiring = Task.CompletedTask;
    private bool _commandStarting;
    private string? _commandLoadError;
    internal bool AssistantVoiceActive => _session.Status.IsRunning || _operation is not null;
    internal bool AssistantCommandsActive => _commandSession?.Status.Running == true || _commandLearning is not null || _commandStarting;

    private void RefreshCommandProfile()
    {
        var profile = _runtime()?.ActiveProfileId ?? Guid.Empty;
        if (profile == _commandProfile) return;
        StopCommands(); _commandProfile = profile; _commandLoadError = null;
        try { _commandSettings = _commandStore.Load(profile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _commandSettings = new(); _commandLoadError = "Voice commands could not be loaded: " + error.Message; }
    }
    private void StopCommands()
    {
        _runtime()?.StopVoiceCommands(); _commandLearning?.Cancel();
        var previous = Interlocked.Exchange(ref _commandSession, null);
        if (previous is not null) { previous.Dispose(); _commandRetiring = previous.Completion; }
    }

    internal DesktopVoiceService(WindowId owner, Func<StudioRuntime?> runtime)
    {
        _runtime = runtime;
        _owner = owner;
        try
        {
            Refresh();
            var selection = _profiles.LoadSelection();
            var selected = _availableProfiles.FirstOrDefault(profile => profile.Id == selection?.ProfileId);
            _selectedProfileId = selected?.Id.ToString();
            _selectedModelId = _models.FirstOrDefault(model => model.SelectionKey == selection?.ModelPackage)?.SelectionKey
                ?? (selected?.Effects is null && selected is not null ? _models.FirstOrDefault(model => model.UsesReference)?.SelectionKey : null)
                ?? "builtin";
            if ((selected?.Effects ?? selected?.Processing) is { } settings) _settings = settings with { Muted = false };
        }
        catch (Exception error) { _message = "Voice setup could not be loaded. " + error.Message; }
    }

    public object GetState()
    {
        RefreshCommandProfile();
        return new
    {
        commands = new { profileId = _commandProfile, profileName = _runtime()?.ActiveProfileName, settings = _commandSettings, engines = VoiceCommandSession.Engines(), error = _commandLoadError },
        devices = _devices,
        models = _models.Select(model => new { id = ModelId(model), model.Name, model.Description, model.UsesReference, model.IsAvailable }),
        profiles = _availableProfiles.Select(profile => new { profile.Id, profile.Name, durationMs = profile.Duration.TotalMilliseconds,
            kind = profile.Effects is null ? "reference" : "effects", profile.Effects }),
        selectedProfileId = _selectedProfileId, selectedModelId = _selectedModelId,
        microphoneId = _microphoneId, outputId = _outputId, destination = _destination, settings = _settings,
        musicRemovalAvailable = VoiceMusicRemoval.IsAvailable, profileDirectory = _profiles.DirectoryPath,
        virtualMicIssue = VirtualMicPipe.SetupIssue,
        clip = _source is null ? null : new
        {
            id = _clipId, name = _clipName, durationMs = _waveform?.Duration.TotalMilliseconds ?? 0,
            startMs = _startMs, endMs = _endMs,
            waveform = _waveform?.Bins.Select(bin => new { bin.Minimum, bin.Maximum }),
            hasCleaned = HasCleanedSelection, useCleaned = _useCleaned
        },
        status = GetStatus()
    };
    }

    public object GetStatus()
    {
        var status = _session.Status;
        if (_commandLearning is null && _commandSession is { Status.Running: false } && _runtime() is { } runtime
            && runtime.VoiceCommandStatus is { Enabled: true } output && output.Generation == _commandGeneration)
            runtime.StopVoiceCommands();
        return new
        {
            commands = new { recognition = _commandSession?.Status, output = _runtime()?.VoiceCommandStatus, error = _commandLoadError },
            status.IsRunning, isStarting = _operationName is "start" or "switch",
            busy = _operation is not null, operation = _operationName,
            message = status.Error ?? (_operation is not null || !status.IsRunning ? _message : status.Message),
            status.Error,
            status.InputPeak, status.OutputPeak, status.BufferedMilliseconds, destination = _destination,
            clipPlaying = _clipPreview?.IsPlaying == true,
            clipPositionMs = (_clipPreview?.Position.TotalMilliseconds ?? 0) + (_useCleaned ? _cleanedStartMs : 0),
            clipError = _clipPreview?.ErrorMessage
        };
    }

    public async Task<object?> InvokeAsync(string action, JsonElement args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            switch (action)
            {
                case "refreshDevices": RequireIdle(); Refresh(); return null;
                case "routeSave":
                    RequireStopped();
                    string microphone = args.Text("microphoneId");
                    string destination = args.Text("destination", "virtual");
                    string output = args.Text("outputId");
                    if (!_devices.Microphones.Any(item => item.Id == microphone))
                        throw new InvalidDataException("Choose an available microphone.");
                    if (destination is not ("virtual" or "headphones")) throw new InvalidDataException("Choose a supported destination.");
                    if (destination == "headphones" && !_devices.Outputs.Any(item => item.Id == output))
                        throw new InvalidDataException("Choose your headphones or speakers.");
                    _microphoneId = microphone; _outputId = output; _destination = destination;
                    _message = "Voice route saved. Nothing is recording or transmitting until you start it.";
                    return null;
                case "modelSelect":
                    RequireStopped();
                    _selectedModelId = FindModel(args.Text("id")).SelectionKey ?? "builtin";
                    if (SelectedProfile is { } profile && (profile.Effects is null) != SelectedModel.UsesReference)
                        _selectedProfileId = null;
                    SaveSelection(); return null;
                case "profileSelect": await SelectProfileAsync(args.Id()); return null;
                case "profileClear": RequireStopped(); _selectedProfileId = null; _selectedModelId = "builtin"; SaveSelection(); return null;
                case "start": await StartAsync(); return null;
                case "commandsSave":
                {
                    RefreshCommandProfile();
                    if (_commandLearning is not null || _commandStarting) throw new InvalidOperationException("Finish or stop template recording/recognizer startup before saving commands.");
                    StopCommands(); var settings = args.GetProperty("settings").Read<VoiceCommandSettings>(); settings.Validate();
                    _commandStore.Save(_commandProfile, settings); _commandSettings = settings; _commandLoadError = null; return null;
                }
                case "commandsStart":
                {
                    if (_commandLearning is not null || _commandStarting) throw new InvalidOperationException("Finish or stop template recording/recognizer startup before listening for commands.");
                    RefreshCommandProfile(); StopCommands();
                    if (!_commandRetiring.IsCompleted) throw new InvalidOperationException("The previous local recognizer is still stopping. Try again shortly.");
                    var runtime = _runtime() ?? throw new InvalidOperationException("Controller runtime is unavailable.");
                    if (_commandSettings.Bindings.Length == 0 || _commandSettings.Bindings.Any(binding => binding.Phrase.Length == 0 && binding.Templates.Length == 0))
                        throw new InvalidOperationException("Each command needs a phrase or at least one recorded personal sound.");
                    long generation = runtime.ActivateVoiceCommands(_commandSettings); _commandGeneration = generation;
                    _commandStarting = true;
                    try
                    {
                        var session = new VoiceCommandSession(args.Text("microphoneId"), _commandSettings,
                            () => runtime.VoiceCommandStatus is { Enabled: true } output && output.Generation == generation,
                            observation => runtime.EnqueueVoiceCommand(generation, observation), onStopped: () => runtime.StopVoiceCommands(generation));
                        _commandSession = session;
                        await session.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch { StopCommands(); throw; }
                    finally { _commandStarting = false; }
                    return null;
                }
                case "commandsStop": StopCommands(); return null;
                case "commandLearn":
                {
                    RefreshCommandProfile();
                    if (_commandLearning is not null || _commandStarting) throw new InvalidOperationException("Finish or stop the current sound recording/recognizer startup.");
                    StopCommands(); Guid recordingProfile = _commandProfile, id = args.Id();
                    if (!_commandRetiring.IsCompleted) throw new InvalidOperationException("The previous local recognizer is still stopping. Try again shortly.");
                    if (!_commandSettings.Bindings.Any(binding => binding.Id == id)) throw new InvalidDataException("Save the command before recording a template.");
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)); _commandLearning = cancellation;
                    try
                    {
                        using var session = new VoiceCommandSession(args.Text("microphoneId"), _commandSettings,
                            () => _runtime()?.ActiveProfileId == recordingProfile && !cancellation.IsCancellationRequested, null, learning: true);
                        _commandSession = session;
                        var template = await session.Learned.WaitAsync(cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (_runtime()?.ActiveProfileId != recordingProfile) throw new InvalidOperationException("Controller setup changed during recording; template discarded.");
                        var settings = _commandSettings with { Bindings = [.. _commandSettings.Bindings.Select(binding => binding.Id == id
                            ? binding with { Templates = [.. binding.Templates.TakeLast(2), template] } : binding)] };
                        _commandStore.Save(recordingProfile, settings); _commandSettings = settings;
                        return new { message = "Personal sound saved locally. Test with background speech before enabling gameplay." };
                    }
                    finally { _commandLearning = null; _commandSession = null; }
                }
                case "stop":
                    StopCommands();
                    _operation?.Cancel(); _clipPreview?.Pause(); _session.Stop();
                    _message = "Voice processing stopped."; return null;
                case "cancel":
                    _operation?.Cancel(); _clipPreview?.Pause(); _message = "Cancelling the current voice operation…"; return null;
                case "settingsSave":
                    _settings = ReadSettings(args.TryGetProperty("settings", out var config) ? config : args);
                    _session.SetSettings(EffectiveSettings()); return null;
                case "muteSet":
                    _settings = _settings with { Muted = args.Flag("muted") };
                    _session.SetSettings(EffectiveSettings()); return null;
                case "profileSaveProcessing":
                    RequireIdle();
                    var referenceProfile = SelectedProfile ?? throw new InvalidOperationException("Choose a saved reference voice first.");
                    if (referenceProfile.ReferencePath is null) throw new InvalidOperationException("Choose a reference voice.");
                    UpdateMetadata(referenceProfile.Id, referenceProfile.Name, null, _settings with { Muted = false });
                    ReloadProfiles(); _message = "Voice settings saved."; return null;
                case "profileSaveEffects":
                    RequireIdle();
                    if (_session.Status.IsRunning && SelectedModel.UsesReference)
                        throw new InvalidOperationException("Stop the reference voice before switching to built-in effects.");
                    var effects = args.TryGetProperty("settings", out var value) ? ReadSettings(value) : _settings;
                    string name = ValidName(args.Text("name"));
                    if (Guid.TryParse(args.Text("id"), out Guid effectsId))
                    {
                        if (FindProfile(effectsId).Effects is null) throw new InvalidDataException("Choose a built-in effects profile to update.");
                        UpdateMetadata(effectsId, name, effects with { Muted = false });
                        ReloadProfiles(effectsId);
                    }
                    else ReloadProfiles(_profiles.SaveEffects(name, effects).Id);
                    _selectedModelId = "builtin"; _settings = effects; _session.SetSettings(EffectiveSettings()); SaveSelection();
                    _message = "Effects profile saved."; return null;
                case "profileRename":
                    RequireStopped(); UpdateMetadata(args.Id(), ValidName(args.Text("name")), null);
                    ReloadProfiles(); _message = "Voice profile renamed."; return null;
                case "profileDuplicate":
                    RequireStopped();
                    var original = FindProfile(args.Id());
                    string copyName = ValidName(args.Text("name", original.Name + " copy"));
                    var copy = original.Effects is { } originalEffects ? _profiles.SaveEffects(copyName, originalEffects)
                        : await RunAsync("save", "Duplicating the voice profile…", token => _profiles.ImportAsync(copyName, original.ReferencePath!, token));
                    if (original.Processing is { } processing) { ReloadProfiles(); UpdateMetadata(copy.Id, copyName, null, processing); }
                    ReloadProfiles(); _message = "Voice profile duplicated."; return new { copy.Id };
                case "profileDelete":
                    RequireStopped(); var deleted = FindProfile(args.Id());
                    _clipPreview?.Pause(); _profiles.Delete(deleted.Id);
                    if (_selectedProfileId == deleted.Id.ToString()) _selectedProfileId = null;
                    ReloadProfiles(); SaveSelection(); _message = "Voice profile deleted."; return null;
                case "clipBrowse":
                    RequireIdle();
                    var picker = new FileOpenPicker(_owner) { SuggestedStartLocation = PickerLocationId.MusicLibrary };
                    foreach (string extension in VoiceProfiles.ClipExtensions) picker.FileTypeFilter.Add(extension);
                    var file = await RunAsync("browse", "Choose a local audio or video clip…", async token =>
                    {
                        var chosen = await picker.PickSingleFileAsync();
                        token.ThrowIfCancellationRequested();
                        return chosen;
                    });
                    if (file is not null) await LoadClipAsync(file.Path);
                    return null;
                case "clipOpenProfile":
                    RequireIdle(); var reference = FindProfile(args.Id());
                    if (reference.ReferencePath is null) throw new InvalidDataException("This profile uses built-in effects and has no reference clip.");
                    await LoadClipAsync(reference.ReferencePath, reference.Name); return null;
                case "clipTrim":
                    RequireIdle(); RequireClip();
                    int startMs = args.Number("startMs"), endMs = args.Number("endMs");
                    ValidateRange(startMs, endMs, 300_000);
                    _clipPreview?.Pause(); _startMs = startMs; _endMs = endMs;
                    if (!HasCleanedSelection) _useCleaned = false;
                    ResetPreview(); return null;
                case "clipPlay":
                    RequireIdle(); RequireClip(); ValidateRange(_startMs, _endMs, 300_000);
                    if (_clipPreview is null) ResetPreview();
                    int offset = _useCleaned ? _cleanedStartMs : 0;
                    int position = Math.Clamp(args.Number("positionMs", _startMs), _startMs, _endMs);
                    await RunAsync("audition", "Preparing the selected audio…", token => _clipPreview!.PlayAsync(
                        TimeSpan.FromMilliseconds(_startMs - offset), TimeSpan.FromMilliseconds(_endMs - offset),
                        TimeSpan.FromMilliseconds(position - offset), token));
                    _message = "Playing the selected reference audio through the Windows default playback device."; return null;
                case "clipPause": _clipPreview?.Pause(); return null;
                case "clipClean":
                    RequireIdle(); RequireClip(); ValidateRange(_startMs, _endMs, 300_000);
                    _clipPreview?.Pause();
                    var cleaned = await RunAsync("cleanup", "Separating speech from background music locally…", token => VoiceMusicRemoval.RemoveAsync(
                        _source!, TimeSpan.FromMilliseconds(_startMs), TimeSpan.FromMilliseconds(_endMs), token));
                    _clipPreview?.Dispose(); _clipPreview = null; _cleaned?.Dispose();
                    _cleaned = cleaned; _cleanedStartMs = _startMs; _cleanedEndMs = _endMs; _useCleaned = true;
                    ResetPreview(); _message = "Cleaned speech is ready. Audition it before saving."; return null;
                case "clipUseCleaned":
                    RequireIdle();
                    if (args.Flag("enabled") && !HasCleanedSelection) throw new InvalidDataException("Clean the selected range before choosing cleaned speech.");
                    _useCleaned = args.Flag("enabled"); ResetPreview(); return null;
                case "clipSave":
                    RequireIdle(); RequireClip(); ValidateRange(_startMs, _endMs, 30_000);
                    string clipName = ValidName(args.Text("name"));
                    _clipPreview?.Pause();
                    int savedOffset = _useCleaned && HasCleanedSelection ? _cleanedStartMs : 0;
                    string savedPath = _useCleaned && HasCleanedSelection ? _cleaned!.Path : _source!;
                    var saved = await RunAsync("save", "Saving the trimmed reference voice…", token => _profiles.ImportAsync(clipName,
                        savedPath, TimeSpan.FromMilliseconds(_startMs - savedOffset), TimeSpan.FromMilliseconds(_endMs - savedOffset), token));
                    ReloadProfiles();
                    if ((_models.FirstOrDefault(model => model.UsesReference && ModelId(model) == _selectedModelId)
                        ?? _models.FirstOrDefault(model => model.UsesReference)) is { } installed)
                    { _selectedModelId = ModelId(installed); _selectedProfileId = saved.Id.ToString(); }
                    SaveSelection(); _message = $"Saved {saved.Name} ({saved.Duration.TotalSeconds:0.000} seconds)."; return new { saved.Id };
                case "clipClear": RequireIdle(); ClearClip(); _message = "Reference editor cleared."; return null;
                default: throw new InvalidOperationException("This voice command is not supported.");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) _message = "Voice operation cancelled.";
            return null;
        }
        catch (Exception error)
        {
            if (!_disposed) _message = error.Message;
            throw;
        }
    }

    private void Refresh()
    {
        _devices = VoiceSession.Discover(); _models = VoiceModelOption.Installed();
        _microphoneId = Pick(_devices.Microphones, _microphoneId)?.Id;
        _outputId = Pick(_devices.Outputs, _outputId)?.Id;
        ReloadProfiles();
    }

    private async Task StartAsync()
    {
        RequireStopped();
        if (!_devices.Microphones.Any(item => item.Id == _microphoneId)) throw new InvalidDataException("Choose a microphone first.");
        var model = SelectedModel;
        var profile = SelectedProfile;
        if (model.UsesReference && profile?.ReferencePath is null) throw new InvalidDataException("Choose a reference voice profile for this model.");
        if (_destination == "headphones" && !_devices.Outputs.Any(item => item.Id == _outputId))
            throw new InvalidDataException("Choose headphones or speakers first.");
        _clipPreview?.Pause();
        await RunAsync("start", model.UsesReference ? "Loading the reference model and voice profile…" : "Starting the voice route…", async token =>
        {
            if (_destination == "headphones")
                await _session.StartPreviewAsync(_microphoneId!, _outputId!, EffectiveSettings(), model, profile, token);
            else
            {
                // Driver setup is an explicit Settings action. Starting audio never silently installs software.
                if (VirtualMicPipe.SetupIssue is { } issue) throw new InvalidOperationException(issue);
                await _session.StartVirtualAsync(_microphoneId!, EffectiveSettings(), model, profile, token);
            }
            if (!_disposed) _session.SetSettings(EffectiveSettings());
        });
        _message = _session.Status.Message;
    }

    private async Task SelectProfileAsync(Guid id)
    {
        RequireIdle();
        var profile = FindProfile(id);
        var model = profile.Effects is not null ? _models.FirstOrDefault(item => !item.UsesReference)
            : _models.FirstOrDefault(item => item.UsesReference && ModelId(item) == _selectedModelId)
                ?? _models.FirstOrDefault(item => item.UsesReference);
        if (model is null) throw new InvalidOperationException("No installed reference-voice model is available. The saved profile is retained.");
        if (_session.Status.IsRunning)
        {
            if (SelectedModel.UsesReference != model.UsesReference)
                throw new InvalidOperationException("Stop voice before switching between built-in effects and a reference model.");
            if (model.UsesReference)
            {
                if (_destination == "headphones") throw new InvalidOperationException("Stop the live preview before changing its voice.");
                await RunAsync("switch", $"Loading {profile.Name}…", token => _session.SwitchVirtualProfileAsync(model, profile, token));
            }
        }
        _selectedModelId = ModelId(model); _selectedProfileId = id.ToString();
        var profileSettings = profile.Effects ?? profile.Processing ?? new VoiceSettings(VoicePreset.Clean, 0, 0, false);
        _settings = profileSettings with { Muted = _settings.Muted };
        _session.SetSettings(EffectiveSettings()); SaveSelection();
        _message = "Voice profile selected.";
    }

    private async Task LoadClipAsync(string path, string? displayName = null)
    {
        RequireIdle(); ClearClip();
        var waveform = await RunAsync("analysis", "Reading the audio track…", token => VoiceProfiles.ReadWaveformAsync(path, token));
        if (waveform.Duration < TimeSpan.FromSeconds(1)) throw new InvalidDataException("Choose a clip with at least one second of audio.");
        _source = path; _clipName = displayName ?? Path.GetFileNameWithoutExtension(path); _clipId = Guid.NewGuid().ToString("N");
        if (waveform.Duration.TotalMilliseconds > int.MaxValue) throw new InvalidDataException("Choose an audio track shorter than 24 days.");
        _waveform = waveform;
        _startMs = 0; _endMs = (int)Math.Min(15_000, waveform.Duration.TotalMilliseconds);
        ResetPreview(); _message = "Select 1–30 seconds of clean speech, audition it, then save the reference voice.";
    }

    private async Task RunAsync(string name, string message, Func<CancellationToken, Task> action)
    {
        await RunAsync(name, message, async token => { await action(token); return true; });
    }

    private async Task<T> RunAsync<T>(string name, string message, Func<CancellationToken, Task<T>> action)
    {
        RequireIdle();
        using var operation = new CancellationTokenSource();
        _operation = operation; _operationName = name; _message = message;
        try
        {
            T result = await action(operation.Token);
            if ((operation.IsCancellationRequested || _disposed) && result is IDisposable disposable)
            {
                disposable.Dispose();
                operation.Token.ThrowIfCancellationRequested();
            }
            // A completed profile/stream transaction is committed. A late cancel must not
            // leave the selected profile describing the retired worker after a hot switch.
            ObjectDisposedException.ThrowIf(_disposed, this);
            return result;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation)) { _operation = null; _operationName = null; }
        }
    }

    private void ReloadProfiles(Guid? select = null)
    {
        _availableProfiles = _profiles.LoadAll();
        if (select is { } id) _selectedProfileId = id.ToString();
        if (_selectedProfileId is not null && !_availableProfiles.Any(profile => profile.Id.ToString() == _selectedProfileId))
            _selectedProfileId = null;
    }

    private VoiceProfile FindProfile(Guid id) => _availableProfiles.FirstOrDefault(profile => profile.Id == id)
        ?? throw new InvalidDataException("That voice profile is no longer available. Refresh the library.");
    private VoiceProfile? SelectedProfile => _availableProfiles.FirstOrDefault(profile => profile.Id.ToString() == _selectedProfileId);
    private VoiceModelOption SelectedModel => FindModel(_selectedModelId);
    private VoiceModelOption FindModel(string id) => _models.FirstOrDefault(model => ModelId(model) == id)
        ?? throw new InvalidDataException("Choose an installed voice model.");
    private static string ModelId(VoiceModelOption model) => model.SelectionKey ?? "builtin";
    private static VoiceEndpoint? Pick(VoiceEndpoint[] endpoints, string? id) => endpoints.FirstOrDefault(item => item.Id == id)
        ?? endpoints.FirstOrDefault(item => item.IsDefault) ?? endpoints.FirstOrDefault();
    private VoiceSettings EffectiveSettings() => _settings;
    private void SaveSelection() => _profiles.SaveSelection(Guid.TryParse(_selectedProfileId, out Guid id) ? id : null,
        _selectedModelId == "builtin" ? null : _selectedModelId);

    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_operation is not null) throw new InvalidOperationException("Finish or cancel the current voice operation first.");
    }
    private void RequireStopped()
    {
        RequireIdle();
        if (_session.Status.IsRunning) throw new InvalidOperationException("Stop voice processing before changing this setting.");
    }
    private void RequireClip()
    {
        if (_source is null || _waveform is null) throw new InvalidOperationException("Choose an audio or video clip first.");
    }
    private void ValidateRange(int start, int end, int maximum)
    {
        if (start < 0 || end <= start || end > (_waveform?.Duration.TotalMilliseconds ?? 0) || end - start < 1000 || end - start > maximum)
            throw new InvalidDataException(maximum == 30_000 ? "Choose between 1 and 30 seconds of speech to save a reference voice."
                : "Choose between 1 second and 5 minutes to audition or clean.");
    }
    private bool HasCleanedSelection => _cleaned is not null && _startMs >= _cleanedStartMs && _endMs <= _cleanedEndMs;
    private void ResetPreview()
    {
        _clipPreview?.Dispose();
        _clipPreview = _source is null ? null : new VoiceTrimPreview(_useCleaned && HasCleanedSelection ? _cleaned!.Path : _source);
    }
    private void ClearClip()
    {
        var preview = _clipPreview;
        var cleaned = _cleaned;
        // Detach/reset first so repeated cleanup remains safe even if a native device
        // or temporary-file cleanup reports an error.
        _clipPreview = null; _cleaned = null;
        _source = null; _waveform = null; _clipName = _clipId = "";
        _startMs = _endMs = _cleanedStartMs = _cleanedEndMs = 0; _useCleaned = false;
        try { preview?.Dispose(); }
        finally { cleaned?.Dispose(); }
    }
    private static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new InvalidDataException("Enter a voice name of 1–80 readable characters.");
        return name;
    }
    private static VoiceSettings ReadSettings(JsonElement args)
    {
        var settings = args.Read<VoiceSettings>();
        if (!settings.IsValid) throw new InvalidDataException("Choose voice effect values within the supported ranges.");
        return settings with { Effects = settings.Effects.Snapshot() };
    }

    private void UpdateMetadata(Guid id, string name, VoiceSettings? effects, VoiceSettings? processing = null)
    {
        _ = FindProfile(id);
        string directory = Path.Combine(_profiles.DirectoryPath, id.ToString("N"));
        string path = Path.Combine(directory, "profile.json");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Voice metadata cannot redirect outside its profile.");
        byte[] original = File.ReadAllBytes(path);
        if (original.Length is <= 0 or > 4096) throw new InvalidDataException("Voice metadata is invalid.");
        var node = JsonNode.Parse(original) as JsonObject ?? throw new InvalidDataException("Voice metadata is invalid.");
        node["name"] = name;
        if (effects is not null) node["effects"] = JsonSerializer.SerializeToNode(effects, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (processing is not null) node["processing"] = JsonSerializer.SerializeToNode(processing, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string temporary = Path.Combine(directory, ".edit-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (!SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
                throw new InvalidOperationException("This voice profile changed in another window. Refresh it before saving.");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void SetVisible(bool visible) { if (!visible) _clipPreview?.Pause(); }
    public DesktopMedia? ReadMedia(string path) => null;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCommands();
        try { _operation?.Cancel(); }
        finally
        {
            try { ClearClip(); }
            finally { _session.Dispose(); }
        }
    }
}
