using System.Diagnostics;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace EliteScriptStudio.Desktop;

/// <summary>Desktop presentation adapter. Capture, inference and controller arbitration remain in their native workers.</summary>
internal sealed class DesktopVisionService : IDesktopModule
{
    private sealed record PreviewImage(DesktopMedia Media, long Sequence, int SourceWidth, int SourceHeight, long CapturedAt);
    private sealed record FrozenImage(string Token, PreviewImage Image, string SourceId, Guid ProfileId, string Fingerprint, VisionSettings Settings);
    private sealed record CalibrationProposal(string Token, VisionCalibrationImport Import, VisionCalibrationEstimate Estimate,
        VisionSettings Settings, string ManifestHash, VisionModelManifest Manifest, string SourceId, int Width, int Height);
    private readonly WindowId _owner;
    private readonly Func<StudioRuntime?> _runtime;
    private readonly Func<StudioProfile> _activeProfile;
    private readonly Func<Task> _connectOutput;
    private readonly Nba2K27ShotProfileStore _profiles = new(DesktopEnvironment.DataDirectory("ShotProfiles"));
    private readonly VisionSettingsStore _settingsStore = new(DesktopEnvironment.DataDirectory("Vision"));
    private readonly VisionModelLibrary _library = new(DesktopEnvironment.DataDirectory("VisionModels"));
    private readonly object _mediaGate = new();
    private Nba2K27ShotProfile[] _savedProfiles = [];
    private Nba2K27ShotProfile? _profile;
    private VisionSettings _settings = VisionSettings.Default;
    private Guid _settingsProfile;
    private CaptureSource[] _sources = [];
    private string _selectedSourceId = "", _lastObservedSourceId = "";
    private VisionModelLibraryEntry[] _models = [];
    private string[] _modelErrors = [];
    private bool _modelScanComplete, _visible, _disposed;
    private VisionSession? _session;
    private PreviewImage? _preview;
    private FrozenImage? _frozen;
    private DesktopVisionDataset? _review;
    private VisionPerformanceRun? _baseline;
    private CalibrationProposal? _calibration;
    private VisionPerformanceSnapshot? _performance;
    private string _notice = "Choose a player setup and a Remote Play window to begin.";
    private int _busy, _encodingPreview, _version;
    private long _sourcesRefreshedAt, _performanceRefreshedAt;
    private CancellationTokenSource _operation = new();
    internal bool HasActiveTrainingRecording => _session?.TrainingSnapshot.IsRecording == true;

    internal DesktopVisionService(WindowId owner, Func<StudioRuntime?> runtime, Func<StudioProfile> activeProfile,
        Func<Task> connectOutput)
    {
        _owner = owner; _runtime = runtime; _activeProfile = activeProfile; _connectOutput = connectOutput;
        ReloadProfiles();
        if (_profiles.LoadSelectedId() is Guid selected) _profile = _savedProfiles.FirstOrDefault(p => p.Id == selected);
        LoadSettings(); RefreshSources();
        var context = _settings;
        _ = Task.Run(() => _library.Discover(context)).ContinueWith(task =>
        {
            if (_disposed || context != _settings) return;
            if (task.IsCompletedSuccessfully)
            { _models = task.Result.Models.ToArray(); _modelErrors = task.Result.Errors.ToArray(); }
            else _modelErrors = ["The model library could not be read. Use Refresh models to try again."];
            _modelScanComplete = true;
        }, TaskScheduler.Default);
    }

    public object GetState()
    {
        if (_profile is null && _settingsProfile != _activeProfile().Id) LoadSettings();
        var selectedModel = _models.FirstOrDefault(m => m.ManifestPath == _settings.ModelManifestPath);
        return new
        {
            profiles = _savedProfiles, selectedProfile = _profile, selectedFingerprint = _profile?.ConfigurationFingerprint,
            sources = _sources.Select(s => new { id = SourceId(s), s.Title, s.ProcessId }), selectedSourceId = _selectedSourceId,
            settings = _settings, cameraViews = Nba2K27ShotProfile.CameraViews,
            models = _models.Select(m => new { id = ModelId(m), name = m.DisplayName, version = m.ModelVersion,
                compatible = m.IsCompatible, evidenceAccepted = m.EvidenceAccepted, m.Status }),
            selectedModelId = selectedModel is null ? "" : ModelId(selectedModel), modelScanComplete = _modelScanComplete,
            modelErrors = _modelErrors, review = _review?.Snapshot(),
            calibration = _calibration is { } c ? new { token = c.Token, estimate = c.Estimate, procedure = c.Import.ProcedureVersion } : null,
            baseline = _baseline is { } b ? new { b.Label, b.CreatedAtUtc, b.Snapshot.ExecutionProvider } : null,
            status = GetStatus()
        };
    }

    public object GetStatus()
    {
        if (!_disposed && _visible && _session?.Status.Running != true &&
            (_sourcesRefreshedAt == 0 || Stopwatch.GetElapsedTime(_sourcesRefreshedAt).TotalSeconds >= 2)) RefreshSources();
        UpdatePreview();
        if (_session is not null && (_performanceRefreshedAt == 0 || Stopwatch.GetElapsedTime(_performanceRefreshedAt).TotalSeconds >= 1))
        { _performance = _session.Performance; _performanceRefreshedAt = Stopwatch.GetTimestamp(); }
        var preview = Volatile.Read(ref _preview);
        VisionPerformanceComparison? comparison = _baseline is not null && _session is not null && _performance is { TotalFrames: > 0 }
            ? VisionPerformanceRuns.Compare(_baseline, new VisionPerformanceRun { Label = "Current observation", Snapshot = _performance }) : null;
        return new
        {
            notice = _notice, busy = Volatile.Read(ref _busy) != 0, session = _session?.Status,
            sources = _sources.Select(source => new { id = SourceId(source), source.Title, source.ProcessId }),
            selectedSourceId = _selectedSourceId, captureLayoutConfirmed = _settings.TrainingCapture.Configured,
            stopping = _session?.IsStopping == true, output = _runtime()?.VisionStatus,
            training = _session?.TrainingSnapshot, performance = _performance, comparison,
            preview = preview is null ? null : new { url = "https://media.accessrelay.test/vision/preview?v=" + preview.Sequence,
                sequence = preview.Sequence, sourceWidth = preview.SourceWidth, sourceHeight = preview.SourceHeight }
        };
    }

    public void SetVisible(bool visible)
    {
        _visible = visible && !_disposed;
        _session?.SetPreviewEnabled(_visible);
    }

    public async Task<object?> InvokeAsync(string action, JsonElement args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Stop never waits behind model import, native pickers or startup waits.
        if (action is "stop" or "recordStop")
        {
            CancelOperation();
            if (action == "recordStop") { _session?.StopTraining(); _notice = "Saving your clip. Keep the window open until it is saved."; }
            else { _session?.StopTraining(); _session?.Stop(); _runtime()?.SetVisionArmed(false); _notice = "Live view stopped. Saved clip status is shown below."; }
            return null;
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Another vision action is finishing. Stop remains available.");
        int version = _version;
        CancellationToken cancellation = _operation.Token;
        try
        {
            switch (action)
            {
                case "sourceRefresh": RefreshSources(); return null;
                case "sourceSelect":
                    RequireStopped();
                    string sourceId = args.Text("id");
                    RefreshSources();
                    if (sourceId.Length != 0 && !_sources.Any(s => SourceId(s) == sourceId))
                        throw new InvalidOperationException("That window closed. Choose another Remote Play window.");
                    if (sourceId != _selectedSourceId)
                    {
                        _version++; _selectedSourceId = sourceId; ClearPreview();
                        _settings = _settings with { CorrectionMs = 0, TrainingCapture = _settings.TrainingCapture with { Configured = false } };
                        SaveSettings(); _calibration = null;
                    }
                    return null;
                case "profileSelect":
                    RequireStopped(); SelectProfile(args.Id()); await RefreshModelsAsync(); return null;
                case "profileSave":
                {
                    RequireStopped();
                    var profile = args.GetProperty("profile").Read<Nba2K27ShotProfile>();
                    if (profile.Id == Guid.Empty) profile = profile with { Id = Guid.NewGuid() };
                    profile.Validate();
                    string expected = args.Text("expectedFingerprint");
                    if (expected.Length != 0 && _savedProfiles.Any(p => p.Id == profile.Id)
                        && _profiles.Load(profile.Id).ConfigurationFingerprint != expected)
                        throw new InvalidOperationException("This setup changed elsewhere. Reopen it before saving.");
                    _profiles.Save(profile); ReloadProfiles(); SelectProfile(profile.Id); await RefreshModelsAsync();
                    _notice = "Shot setup saved. Its current camera, animation and meter settings travel with each clip.";
                    return profile;
                }
                case "profileDuplicate":
                {
                    RequireStopped(); var copy = _profiles.Duplicate(args.Id(), args.Text("name")); ReloadProfiles(); SelectProfile(copy.Id);
                    await RefreshModelsAsync(); _notice = "Shot setup duplicated."; return copy;
                }
                case "profileDelete":
                    RequireStopped();
                    var deleteId = args.Id(); _profiles.Delete(deleteId); ReloadProfiles();
                    if (_profile?.Id == deleteId) { _profile = null; LoadSettings(); ClearPreview(); }
                    _notice = "Shot setup deleted. Recorded clips are unchanged."; return null;
                case "settingsSave":
                    RequireStopped();
                    var edited = args.GetProperty("settings").Read<VisionSettings>();
                    edited = edited with { ModelManifestPath = _settings.ModelManifestPath,
                        AnimationProfile = _settings.AnimationProfile, CameraView = _settings.CameraView };
                    edited.Validate();
                    if ((edited with { CorrectionMs = 0 }) != (_settings with { CorrectionMs = 0 }))
                        edited = edited with { CorrectionMs = 0 };
                    _settings = edited; _calibration = null; SaveSettings(); _version++;
                    await RefreshModelsAsync(); _notice = "Vision settings saved."; return null;
                case "settingsExport": return await ExportSettingsAsync();
                case "settingsImport": return await ImportSettingsAsync(version, cancellation);
                case "modelRefresh": await RefreshModelsAsync(); return null;
                case "modelImport":
                {
                    RequireStopped(); var folder = await new FolderPicker(_owner).PickSingleFolderAsync();
                    CheckOperation(version, cancellation); if (folder is null) return null;
                    var context = _settings;
                    var model = await Task.Run(() => _library.Import(Path.Combine(folder.Path, "manifest.json"), context, cancellation), cancellation);
                    CheckOperation(version, cancellation);
                    await RefreshModelsAsync();
                    _settings = _settings with { ModelManifestPath = model.ManifestPath, CorrectionMs = 0 }; SaveSettings();
                    _notice = "Model package imported. " + model.Status; return null;
                }
                case "modelSelect":
                {
                    RequireStopped(); string id = args.Text("id");
                    var model = id.Length == 0 ? null : _models.FirstOrDefault(m => ModelId(m) == id)
                        ?? throw new InvalidOperationException("Import this model package before selecting it.");
                    _settings = _settings with { ModelManifestPath = model?.ManifestPath ?? "", CorrectionMs = 0 };
                    SaveSettings(); _calibration = null; return null;
                }
                case "start": Start(args.Flag("loadModel", true)); return null;
                case "connectOutput": await _connectOutput(); return null;
                case "armedSet":
                    bool armed = args.Flag("armed");
                    if (armed && _session?.Status.CanArm != true)
                        throw new InvalidOperationException("A compatible model with accepted accuracy and hardware evidence must be running before assistance can be armed.");
                    if (_runtime()?.SetVisionArmed(armed) != true && armed)
                        throw new InvalidOperationException(_runtime()?.VisionStatus.Message ?? "Controller output is unavailable.");
                    return null;
                case "freeze": return await FreezeAsync(version, cancellation);
                case "layoutSave": return SaveLayout(args);
                case "recordStart": return await RecordAsync(version, cancellation);
                case "reviewOpen": return await ReviewOpenAsync(args, version, cancellation);
                case "reviewSave":
                    if (_review is null || args.Text("token") != _review.Token) throw new InvalidOperationException("Open the clip before saving its review.");
                    _review.Save(args.GetProperty("labels").Read<DesktopVisionDataset.LabelDocument>());
                    _notice = "Reviewed labels saved with this clip. Automatic meter-derived training is still pending.";
                    return _review.Snapshot();
                case "reviewClose": _review?.Dispose(); _review = null; return null;
                case "openClipFolder":
                    string clipFolder = _session?.TrainingSnapshot.DirectoryPath ?? "";
                    if (clipFolder.Length == 0 || !Directory.Exists(clipFolder)) throw new InvalidOperationException("Record a clip first.");
                    await Windows.System.Launcher.LaunchFolderPathAsync(clipFolder); return null;
                case "performanceSave": return await SavePerformanceAsync(args.Text("label", "Vision performance run"), version, cancellation);
                case "performanceLoad": return await LoadPerformanceAsync(version, cancellation);
                case "performanceClear": _baseline = null; return null;
                case "diagnosticsExport": return await ExportDiagnosticsAsync(version, cancellation);
                case "calibrationExport": return await ExportCalibrationAsync(version, cancellation);
                case "calibrationImport": return await ImportCalibrationAsync(version, cancellation);
                case "calibrationApply": return ApplyCalibration(args);
                case "calibrationCancel": _calibration = null; return null;
                default: throw new InvalidOperationException("This vision action is not supported.");
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void ReloadProfiles() => _savedProfiles = _profiles.LoadAll().ToArray();
    private void SelectProfile(Guid id)
    {
        var previous = _profile;
        _profile = _profiles.Load(id); _profiles.Select(id); LoadSettings();
        if (previous?.ConfigurationFingerprint != _profile.ConfigurationFingerprint) { ClearPreview(); _calibration = null; }
        _version++;
    }
    private void LoadSettings()
    {
        _settingsProfile = _profile?.Id ?? _activeProfile().Id;
        var loaded = _settingsStore.Load(_settingsProfile);
        if (_profile is { } profile)
        {
            string animation = "nba2k27:" + profile.ConfigurationFingerprint;
            if (loaded.CameraView != profile.CameraView)
                loaded = loaded with { TrainingCapture = loaded.TrainingCapture with { Configured = false } };
            if (loaded.AnimationProfile != animation) loaded = loaded with { ModelManifestPath = "", CorrectionMs = 0 };
            loaded = loaded with { AnimationProfile = animation, CameraView = profile.CameraView };
        }
        _settings = loaded;
    }
    private void SaveSettings() { _settings.Validate(); _settingsStore.Save(_settingsProfile, _settings); _runtime()?.ConfigureVision(_settings); }
    private async Task RefreshModelsAsync()
    {
        var context = _settings;
        var found = await Task.Run(() => _library.Discover(context));
        if (_disposed || context != _settings) return;
        _models = found.Models.ToArray(); _modelErrors = found.Errors.ToArray(); _modelScanComplete = true;
    }
    private static string SourceId(CaptureSource source) => source.Hwnd.ToInt64().ToString("x") + "-" + source.ProcessId;
    private static string ModelId(VisionModelLibraryEntry model) => Path.GetFileName(Path.GetDirectoryName(model.ManifestPath))!;
    private CaptureSource SelectedSource() => _sources.FirstOrDefault(s => SourceId(s) == _selectedSourceId)
        ?? throw new InvalidOperationException("Choose the window showing Xbox Remote Play.");
    private void RefreshSources()
    {
        _sourcesRefreshedAt = Stopwatch.GetTimestamp();
        var sources = RemotePlayCapture.EnumerateWindows().ToArray();
        if (_selectedSourceId.Length != 0 && !sources.Any(s => SourceId(s) == _selectedSourceId))
        {
            _version++; _selectedSourceId = ""; _calibration = null; ClearPreview();
            _settings = _settings with { CorrectionMs = 0, TrainingCapture = _settings.TrainingCapture with { Configured = false } };
            _notice = "The selected window closed. Choose the window showing Remote Play.";
        }
        _sources = sources;
    }
    private void RequireStopped()
    {
        if (_session?.TrainingSnapshot is { HasTake: true, IsFinalized: false })
            throw new InvalidOperationException("Finish saving the current clip before changing its setup.");
        if (_session?.Status.Running == true || _session?.IsStopping == true)
            throw new InvalidOperationException("Stop live view before changing its player, source, model or timing settings.");
    }
    private void Start(bool loadModel)
    {
        RequireStopped(); RefreshSources(); var source = SelectedSource();
        var runtime = _runtime() ?? throw new InvalidOperationException("The controller engine is unavailable.");
        SaveSettings(); ClearPreview();
        _session ??= new VisionSession(runtime); _session.SetPreviewEnabled(_visible);
        _session.Start(source.Hwnd, _settings, loadModel); _lastObservedSourceId = _selectedSourceId;
        _notice = loadModel && _settings.ModelManifestPath.Length != 0
            ? "Preparing live analysis. Assistance remains disarmed until you arm it."
            : "Preview is live. Record a clip to save footage; viewing alone does not save it.";
    }
    private void UpdatePreview()
    {
        if (_disposed || !_visible || _session is null || Volatile.Read(ref _encodingPreview) != 0) return;
        if (_session.TakePreview() is not { } preview) return;
        if (Interlocked.CompareExchange(ref _encodingPreview, 1, 0) != 0) { preview.Dispose(); return; }
        int version = _version;
        _ = Task.Run(() =>
        {
            using (preview)
            {
                try
                {
                    var media = DesktopVisionBitmap.Encode(preview.Pixels, preview.Width, preview.Height, preview.Stride);
                    lock (_mediaGate)
                        if (!_disposed && version == _version)
                            Volatile.Write(ref _preview, new(media, preview.Sequence, preview.Width, preview.Height, Stopwatch.GetTimestamp()));
                }
                finally { Interlocked.Exchange(ref _encodingPreview, 0); }
            }
        });
    }
    private void ClearPreview() { lock (_mediaGate) { Volatile.Write(ref _preview, null); _frozen = null; } }
    private async Task<object> FreezeAsync(int version, CancellationToken cancellation)
    {
        if (_profile is null) throw new InvalidOperationException("Save a player shot setup before marking capture areas.");
        if (_session?.TrainingSnapshot is { HasTake: true, IsFinalized: false })
            throw new InvalidOperationException("Finish the current clip before changing its capture areas.");
        if (_session?.Status.Running != true) Start(loadModel: false);
        _session!.SetPreviewEnabled(true);
        long began = Stopwatch.GetTimestamp();
        PreviewImage? image;
        while ((image = Volatile.Read(ref _preview)) is null || Stopwatch.GetElapsedTime(image.CapturedAt).TotalSeconds > 1)
        {
            CheckOperation(version, cancellation); UpdatePreview();
            if (_session.Status.CaptureFailed || Stopwatch.GetElapsedTime(began).TotalSeconds > 4)
                throw new InvalidOperationException("No fresh gameplay preview arrived. Restore the selected window and try again.");
            await Task.Delay(40, cancellation);
        }
        CheckOperation(version, cancellation);
        _runtime()?.SetVisionArmed(false); _session.Stop();
        await WaitStoppedAsync(cancellation);
        var frozen = new FrozenImage(Guid.NewGuid().ToString("N"), image, _selectedSourceId, _profile.Id,
            _profile.ConfigurationFingerprint, _settings);
        lock (_mediaGate) _frozen = frozen;
        _notice = "Preview paused for marking. Mark the gameplay, player, meter and feedback areas, then save the layout.";
        return new { token = frozen.Token, url = "https://media.accessrelay.test/vision/frozen/" + frozen.Token,
            sourceWidth = image.SourceWidth, sourceHeight = image.SourceHeight, sequence = image.Sequence,
            player = _settings.Crop, layout = _settings.TrainingCapture };
    }
    private object SaveLayout(JsonElement args)
    {
        RequireStopped(); FrozenImage frozen;
        lock (_mediaGate) frozen = _frozen ?? throw new InvalidOperationException("Open capture area setup on a fresh preview first.");
        if (args.Text("token") != frozen.Token || frozen.SourceId != _selectedSourceId || _profile is null ||
            frozen.ProfileId != _profile.Id || frozen.Fingerprint != _profile.ConfigurationFingerprint || frozen.Settings != _settings)
            throw new InvalidOperationException("The capture context changed. Open a fresh preview to mark the areas again.");
        var layout = args.GetProperty("layout").Read<VisionTrainingCaptureSettings>() with
        { Configured = true, SourceWidth = frozen.Image.SourceWidth, SourceHeight = frozen.Image.SourceHeight };
        var crop = args.GetProperty("player").Read<VisionCrop>(); crop.Validate(); layout.ValidateForRecording(_profile);
        if (!VisionTrainingCaptureSettings.Contains(layout.GameplayRegion, crop))
            throw new InvalidDataException("Keep the player area inside the gameplay area.");
        _settings = _settings with { Crop = crop, TrainingCapture = layout, CorrectionMs = 0 }; SaveSettings(); _calibration = null;
        _notice = "Capture areas saved. Keep playing through the shot feedback when recording your next clip.";
        return null!;
    }
    private async Task<object?> RecordAsync(int version, CancellationToken cancellation)
    {
        var profile = _profile ?? throw new InvalidOperationException("Save a player shot setup first.");
        _settings.TrainingCapture.ValidateForRecording(profile);
        var runtime = _runtime() ?? throw new InvalidOperationException("The controller engine is unavailable.");
        if (runtime.ScriptsEnabled || runtime.Recording.IsRecording)
            throw new InvalidOperationException("Turn off macro scripts and finish macro recording before collecting manual gameplay clips.");
        if (_session?.TrainingSnapshot is { HasTake: true, IsFinalized: false })
            throw new InvalidOperationException("Wait for the current clip to finish saving.");
        var settings = _settings; string sourceId = _selectedSourceId;
        var folder = await new FolderPicker(_owner).PickSingleFolderAsync();
        CheckOperation(version, cancellation); if (folder is null) return null;
        if (_settings != settings || _profile?.ConfigurationFingerprint != profile.ConfigurationFingerprint || sourceId != _selectedSourceId)
            throw new InvalidOperationException("The shot or source changed while selecting a folder. Review the setup and try again.");
        RefreshSources(); var source = SelectedSource();
        if (_session?.Status.Running != true) Start(loadModel: false);
        long began = Stopwatch.GetTimestamp();
        while (_session!.Status is not { Frames: > 0, CaptureWidth: > 0, CaptureHeight: > 0 })
        {
            CheckOperation(version, cancellation);
            if (!_session.Status.Running || _session.Status.CaptureFailed || Stopwatch.GetElapsedTime(began).TotalSeconds > 4)
                throw new InvalidOperationException("The gameplay window could not be captured. Check the preview before recording.");
            await Task.Delay(40, cancellation);
        }
        CheckOperation(version, cancellation);
        _session.StartTraining(folder.Path, profile, source.Title);
        _notice = "Recording up to 10 seconds. Return focus to Remote Play and keep playing until shot feedback appears.";
        return null;
    }
    private async Task<object?> ReviewOpenAsync(JsonElement args, int version, CancellationToken cancellation)
    {
        string? folder = null;
        if (args.Flag("latest"))
        {
            var latest = _session?.TrainingSnapshot;
            if (latest is not { IsFinalized: true, IsComplete: true }) throw new InvalidOperationException("Wait for a completed saved clip before opening its review.");
            folder = latest.Value.DirectoryPath;
        }
        else folder = (await new FolderPicker(_owner).PickSingleFolderAsync())?.Path;
        CheckOperation(version, cancellation); if (folder is null) return null;
        var dataset = await Task.Run(() => new DesktopVisionDataset(folder), cancellation);
        try { CheckOperation(version, cancellation); }
        catch { dataset.Dispose(); throw; }
        var previous = _review; _review = dataset; previous?.Dispose(); _notice = "Clip opened. Review each shot and its feedback on the shared timeline.";
        return dataset.Snapshot();
    }
    private async Task<object?> SavePerformanceAsync(string label, int version, CancellationToken cancellation)
    {
        var run = _session?.SavePerformanceRun(label) ?? throw new InvalidOperationException("Start a preview before saving performance measurements.");
        var picker = SavePicker("AccessRelay-Vision-Performance", "Performance run");
        var file = await picker.PickSaveFileAsync(); CheckOperation(version, cancellation); if (file is null) return null;
        await Task.Run(() => VisionPerformanceRuns.Write(file.Path, run), cancellation);
        _notice = "Performance run saved. It measures the PC pipeline, not Xbox frame age or receipt."; return null;
    }
    private async Task<object?> LoadPerformanceAsync(int version, CancellationToken cancellation)
    {
        var file = await OpenJsonPicker().PickSingleFileAsync(); CheckOperation(version, cancellation); if (file is null) return null;
        _baseline = await Task.Run(() => VisionPerformanceRuns.Read(file.Path), cancellation);
        _notice = "Performance baseline loaded. Matching configuration and 100 model decisions per run are required for comparison."; return null;
    }
    private async Task<object?> ExportDiagnosticsAsync(int version, CancellationToken cancellation)
    {
        if (_session is null) throw new InvalidOperationException("Start a preview before exporting diagnostics.");
        var file = await SavePicker("AccessRelay-Vision-Diagnostics", "Vision diagnostics").PickSaveFileAsync();
        CheckOperation(version, cancellation); if (file is null) return null;
        await Task.Run(() => _session.ExportDiagnostics(file.Path), cancellation); _notice = "Vision diagnostics exported."; return null;
    }
    private (VisionSessionStatus Source, VisionModelManifest Manifest, string Hash) CalibrationContext()
    {
        RequireStopped(); var source = _session?.Status;
        if (source is not { CaptureWidth: > 0, CaptureHeight: > 0 } || _selectedSourceId != _lastObservedSourceId)
            throw new InvalidOperationException("Preview the selected source, then stop it before measuring calibration.");
        var (manifest, hash) = VisionModelManifest.Read(_settings.ModelManifestPath);
        if (manifest.AnimationProfile != _settings.AnimationProfile ||
            (_settings.CameraView.Length != 0 && manifest.CameraConfiguration != _settings.CameraView))
            throw new InvalidDataException("Choose the model for this player and camera first.");
        return (source.Value, manifest, hash);
    }
    private async Task<object?> ExportCalibrationAsync(int version, CancellationToken cancellation)
    {
        var (source, manifest, hash) = CalibrationContext();
        var template = new VisionCalibrationImport
        {
            ModelManifestHash = hash, AnimationProfile = _settings.AnimationProfile, GameVersion = manifest.GameVersion,
            CameraConfiguration = manifest.CameraConfiguration, ExecutionProvider = _settings.ExecutionProviderName,
            CaptureWidth = source.CaptureWidth, CaptureHeight = source.CaptureHeight, Crop = _settings.Crop,
            PreviousCorrectionMs = _settings.CorrectionMs, EstimatedVideoDelayMs = _settings.EstimatedVideoDelayMs,
            EstimatedInputDelayMs = _settings.EstimatedInputDelayMs
        };
        var file = await SavePicker("AccessRelay-Measured-Calibration", "Measured calibration").PickSaveFileAsync();
        CheckOperation(version, cancellation); if (file is null) return null;
        await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(template, VisionModelManifest.Json), cancellation);
        _notice = "Calibration template exported. Add reviewed measured millisecond errors; early/late feedback alone is insufficient."; return null;
    }
    private async Task<object?> ImportCalibrationAsync(int version, CancellationToken cancellation)
    {
        var (source, manifest, hash) = CalibrationContext();
        var file = await OpenJsonPicker().PickSingleFileAsync(); CheckOperation(version, cancellation); if (file is null) return null;
        var imported = JsonSerializer.Deserialize<VisionCalibrationImport>(
            await Task.Run(() => VisionModelManifest.ReadBounded(file.Path, 256 * 1024), cancellation), VisionModelManifest.Json)
            ?? throw new InvalidDataException("The measured calibration file is empty.");
        imported.ValidateContext(hash, manifest, _settings, source.CaptureWidth, source.CaptureHeight);
        var estimate = VisionCalibration.Estimate(imported.Attempts, _settings.CorrectionMs);
        _calibration = new(Guid.NewGuid().ToString("N"), imported, estimate, _settings, hash, manifest,
            _selectedSourceId, source.CaptureWidth, source.CaptureHeight);
        _notice = "Measured calibration is ready for your review."; return new { token = _calibration.Token, estimate };
    }
    private object? ApplyCalibration(JsonElement args)
    {
        RequireStopped(); var proposal = _calibration ?? throw new InvalidOperationException("Import measured calibration first.");
        if (args.Text("token") != proposal.Token || proposal.Settings != _settings || proposal.SourceId != _selectedSourceId)
            throw new InvalidOperationException("Calibration context changed. Import the measurements again.");
        var (_, currentHash) = VisionModelManifest.Read(_settings.ModelManifestPath);
        if (currentHash != proposal.ManifestHash) throw new InvalidDataException("The selected model changed since calibration review.");
        _settings = _settings with { CorrectionMs = proposal.Estimate.RecommendedCorrectionMs }; SaveSettings();
        string directory = DesktopEnvironment.DataDirectory("Calibration") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessRelayStudio", "Vision", "Calibration");
        VisionCalibration.Save(Path.Combine(directory, _settingsProfile.ToString("N") + ".json"), new VisionCalibrationRecord
        {
            ModelManifestHash = proposal.ManifestHash, AnimationProfile = _settings.AnimationProfile,
            GameVersion = proposal.Manifest.GameVersion, CameraConfiguration = proposal.Manifest.CameraConfiguration,
            ExecutionProvider = _settings.ExecutionProviderName, ProcedureVersion = proposal.Import.ProcedureVersion,
            CaptureWidth = proposal.Width, CaptureHeight = proposal.Height, Crop = _settings.Crop,
            CreatedAtUtc = DateTimeOffset.UtcNow, Estimate = proposal.Estimate
        });
        _calibration = null; _notice = "Measured correction saved. Validate it in a new practice session."; return null;
    }
    private async Task<object?> ExportSettingsAsync()
    {
        RequireStopped(); var file = await SavePicker("AccessRelay-Vision-Settings", "Vision settings").PickSaveFileAsync();
        if (file is not null) _settingsStore.Export(file.Path, _settingsProfile, _settings); return null;
    }
    private async Task<object?> ImportSettingsAsync(int version, CancellationToken cancellation)
    {
        RequireStopped(); var file = await OpenJsonPicker().PickSingleFileAsync(); CheckOperation(version, cancellation); if (file is null) return null;
        var imported = _settingsStore.Import(file.Path);
        if (imported.AnimationProfile != _settings.AnimationProfile || imported.CameraView != _settings.CameraView)
            throw new InvalidDataException("Choose the player/camera matching these settings before importing them.");
        if (imported.ModelManifestPath.Length != 0 && !_models.Any(m => m.ManifestPath == imported.ModelManifestPath))
            throw new InvalidDataException("Import the model package into the library before importing its settings.");
        _settings = imported with { CorrectionMs = 0, TrainingCapture = imported.TrainingCapture with { Configured = false } };
        SaveSettings(); _calibration = null; await RefreshModelsAsync(); _notice = "Settings imported. Confirm capture areas on this PC."; return null;
    }
    private FileSavePicker SavePicker(string name, string label)
    { var picker = new FileSavePicker(_owner) { SuggestedFileName = name }; picker.FileTypeChoices.Add(label, [".json"]); return picker; }
    private FileOpenPicker OpenJsonPicker()
    { var picker = new FileOpenPicker(_owner); picker.FileTypeFilter.Add(".json"); return picker; }
    private void CheckOperation(int version, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (version != _version) throw new OperationCanceledException("The vision action was stopped or its context changed.");
    }
    private void CancelOperation()
    {
        _version++; var old = _operation; _operation = new(); old.Cancel(); old.Dispose();
    }
    private async Task WaitStoppedAsync(CancellationToken cancellation)
    {
        long began = Stopwatch.GetTimestamp();
        while (_session?.IsStopping == true)
        {
            if (Stopwatch.GetElapsedTime(began).TotalSeconds > 4)
                throw new InvalidOperationException("Live view is still stopping. Try again when it has stopped.");
            await Task.Delay(40, cancellation);
        }
    }
    public DesktopMedia? ReadMedia(string path)
    {
        if (_disposed) return null;
        if (path == "preview") return Volatile.Read(ref _preview)?.Media;
        if (path.StartsWith("frozen/", StringComparison.Ordinal))
        { lock (_mediaGate) return _frozen is { } image && path == "frozen/" + image.Token ? image.Image.Media : null; }
        var parts = path.Split('/');
        if (parts is ["review", var token, var region, var frame] && _review is { } review && review.Token == token
            && int.TryParse(frame, out int index)) return review.ReadFrame(region, index);
        return null;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; CancelOperation();
        _session?.Dispose(); _review?.Dispose(); _operation.Dispose(); ClearPreview();
    }
}
