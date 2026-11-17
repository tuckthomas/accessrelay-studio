using System.Collections.Immutable;
using System.Text.Json;
using EliteScriptStudio.Audio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Output;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace EliteScriptStudio.Desktop;

/// <summary>Application commands for the web interface; real-time input stays in StudioRuntime.</summary>
internal sealed class DesktopControllerService : IDesktopModule
{
    private readonly WindowId _owner;
    private readonly Func<StudioRuntime?> _runtime;
    private readonly Func<Task> _stopAll;
    private readonly ProfileStore _store;
    private readonly Func<IReadOnlyList<IGameControllerPlugin>> _gamePlugins;
    private IReadOnlyList<StudioProfile> _profiles = [];
    private StudioProfile _active = StudioProfile.CreateDefault(), _persisted;
    private StickDeadzone _deadzone;
    private FeedbackSettings _feedbackSettings;
    private CoPilotSettings _coPilotSettings;
    private CancellationTokenSource? _outputStart;
    private Task? _outputTask;
    private string _notice = "Ready. Controller output and scripts start only when you turn them on.";
    private bool _disposed;
    private int _revision;
    private readonly GamePackLibrary _packLibrary = new(DesktopEnvironment.GamePackDirectory);
    private readonly Func<string, Task>? _importPackModel;
    private IReadOnlyList<GamePackDefinition> _packs = [];
    private string[] _packErrors = [];
    private CancellationTokenSource? _packOperation;
    private sealed record TrainerRecording(string Key, Guid ProfileId, string ShotId, string ShotInput);
    private TrainerRecording? _trainerRecording;

    internal StudioProfile ActiveProfile => _active;
    internal void SaveCalibratedLua(Guid profileId, ControlId control, string expectedKey, LuaControllerScript script)
    {
        if (_active.Id != profileId || _active.GetBinding(control).LuaScriptId != script.Id
            || !_active.LuaScripts.TryGetValue(script.Id, out var current)
            || LuaShotCalibration.ConfigurationKey(profileId, control, current) != expectedKey)
            throw new InvalidOperationException("The controller profile or Lua changed. Run a new practice trial before saving feedback.");
        Save(_active.WithLuaScript(script));
    }
    internal long AssistantRevision => _revision;
    internal bool AssistantStarting => _outputTask is { IsCompleted: false } || _packOperation is not null;
    internal void ApplyAssistantProfile(StudioProfile profile)
    {
        if (profile.Id != _active.Id) throw new InvalidOperationException("The selected setup changed.");
        if (_runtime()?.HasOutput == true || _runtime()?.ScriptsEnabled == true) throw new InvalidOperationException("Stop output and sequences first.");
        Save(profile with { ScriptsEnabled = false });
    }
    internal StickDeadzone Deadzone => _deadzone;
    internal FeedbackSettings FeedbackSettings => _feedbackSettings;
    internal string ProfileDirectory => _store.DirectoryPath;
    internal bool HasMarketplacePack(string id, string version, string hash) => _packs.Any(pack => pack.Manifest.Id == id && pack.Manifest.Version == version && pack.ManifestSha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
    internal async Task ImportMarketplacePackAsync(string path, CancellationToken cancellation)
    {
        RequireEditable();
        if (_packOperation is not null) throw new InvalidOperationException("A game plugin is already being imported.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation); _packOperation = operation;
        int revision = _revision;
        try
        {
            var pack = await Task.Run(() =>
            {
                var checkedPack = GamePackDefinition.Read(path);
                foreach (var asset in checkedPack.Manifest.Assets.Where(asset => asset.Kind == GamePackAssetKind.VisionManifest))
                    _ = EliteScriptStudio.Vision.VisionModelManifest.Read(checkedPack.ResourcePath(asset));
                return _packLibrary.Import(path, operation.Token);
            }, operation.Token);
            CheckRevision(revision); operation.Token.ThrowIfCancellationRequested();
            _packs = [.. _packs.Where(item => item.Manifest.Id != pack.Manifest.Id || item.Manifest.Version != pack.Manifest.Version), pack];
            _notice = "Installed " + pack.Manifest.Name + ".";
        }
        finally { _packOperation = null; }
    }

    internal DesktopControllerService(WindowId owner, Func<StudioRuntime?> runtime, Func<Task> stopAll, Func<string, Task>? importPackModel = null, Func<IReadOnlyList<IGameControllerPlugin>>? gamePlugins = null)
    {
        _owner = owner; _runtime = runtime; _stopAll = stopAll; _importPackModel = importPackModel; _gamePlugins = gamePlugins ?? (() => []);
        _store = new ProfileStore(DesktopEnvironment.DataDirectory("ControllerProfiles"));
        _deadzone = _store.LoadStickDeadzone();
        _feedbackSettings = _store.LoadFeedbackSettings();
        _coPilotSettings = _store.LoadCoPilotSettings();
        try { _packs = _packLibrary.LoadAll(out _packErrors); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { _packErrors = [error.Message]; }
        try
        {
            _profiles = _store.MigrateLuaProfiles();
            if (_profiles.Count == 0) { _store.Save(_active); _profiles = [_active]; }
            _persisted = _profiles.FirstOrDefault(profile => profile.Id == _store.LoadSelectedId()) ?? _profiles[0];
            _active = _persisted with { ScriptsEnabled = false };
        }
        catch (Exception error)
        {
            _persisted = _active;
            _notice = "Some saved setups could not be read. Existing files were kept: " + error.Message;
        }
    }

    public object GetState() => new
    {
        profiles = _profiles.Select(profile => new { profile.Id, profile.Name }),
        activeProfile = new
        {
            _active.Id, _active.Name,
            _active.GamePack,
            stickyControls = _active.StickyControls.OrderBy(control => (uint)control),
            _active.Smoothing,
            bindings = ControlMap.All.Select(control =>
            {
                var binding = _active.GetBinding(control);
                return new { control = EnumName(control), label = ControlLabel(control), binding.Script,
                    binding.Enabled, binding.Consume, binding.RecordingId, binding.LuaScriptId };
            }),
            luaScripts = _active.LuaScripts.Values.OrderBy(script => script.Name).Select(script => new { script.Id, script.Name,
                training = script.Training?.Summary(script.Source) })
        },
        devices = _runtime()?.Latest?.Input.Devices ?? [], selectedDeviceId = _runtime()?.Latest?.Input.DeviceId,
        deadzone = _deadzone, feedback = _feedbackSettings,
        coPilot = _coPilotSettings with { Enabled = _runtime()?.CoPilotSettings.Enabled == true },
        gamePacks = _packs.Where(pack => pack.Manifest.TrainingAdapter == "none" && string.IsNullOrEmpty(pack.Manifest.GameId)).Select(pack => new { key = pack.Manifest.Id + "@" + pack.Manifest.Version, manifest = pack.Manifest,
            pack.ManifestSha256, models = pack.Manifest.Assets.Where(asset => asset.Kind == GamePackAssetKind.VisionManifest).Select(asset => asset.Path) }),
        trainerPacks = _packs.Where(pack => pack.Manifest.ControllerTrainer is not null).Select(pack => new { key = pack.Manifest.Id + "@" + pack.Manifest.Version, manifest = pack.Manifest,
            pack.ManifestSha256 }),
        gamePackErrors = _packErrors,
        controls = ControlMap.All.Select(control => new { id = EnumName(control), label = ControlLabel(control), mask = (uint)control }),
        scripts = new[] { new { id = "passthrough", label = "Normal input" }, new { id = "lua", label = "Lua script" } },
        recordingChannels = Enum.GetValues<RecordingChannels>().Where(value => value != RecordingChannels.None)
            .Select(value => new { id = EnumName(value), label = ChannelLabel(value) }),
        driverAvailable = VirtualMicDriver.IsAvailable, profileDirectory = _store.DirectoryPath, status = GetStatus(),
        limits = new { maximumRecordings = MacroRecording.MaxRecordingsPerProfile, maximumRecordingMs = InputRecorder.MaxDurationMs,
            maximumLuaScripts = LuaControllerScript.MaxScripts, maximumSourceLength = LuaControllerScript.MaxSourceLength }
    };

    public object GetStatus()
    {
        var runtime = _runtime();
        var sample = runtime?.Latest;
        var recording = runtime?.Recording;
        return new
        {
            connected = sample?.Input.IsConnected == true, deviceName = sample?.Input.Device?.Name ?? "No controller connected",
            devices = sample?.Input.Devices ?? [], selectedDeviceId = sample?.Input.DeviceId,
            input = sample?.Input.State ?? default, output = sample?.Output ?? default,
            outputActive = runtime?.HasOutput == true, scriptsEnabled = runtime?.ScriptsEnabled == true,
            isStarting = _outputTask is { IsCompleted: false }, outputStatus = sample?.OutputStatus ?? "Controller output is off.",
            macro = sample?.Macro, meanPollMs = sample?.MeanPollMs, p95PollMs = sample?.P95PollMs, p99PollMs = sample?.P99PollMs,
            paddles = sample?.Input.HeldPaddles ?? PaddleButtons.None, paddlePulse = sample?.PaddlePulse ?? 0,
            lastPaddles = sample?.LastPaddles ?? PaddleButtons.None, supportsEliteKeypad = sample?.Input.IsPaddleInterfacePresent == true,
            visibleControls = sample?.VisibleControls ?? 0,
            supportsBackgroundInput = sample?.Input.SupportsBackgroundInput == true,
            training = runtime?.IsVisionTrainingRecording == true,
            navigation = runtime?.NavigationStatus,
            feedback = runtime?.FeedbackStatus,
            coPilot = new { enabled = runtime?.CoPilotSettings.Enabled == true,
                primaryConnected = sample?.Input.PrimaryConnected == true,
                companionConnected = sample?.Input.CompanionConnected == true },
            recording = new { isRecording = recording?.IsRecording == true, hasInput = recording?.HasInput == true,
                elapsedMs = recording?.ElapsedMs ?? 0, pending = recording?.Pending is { } pending ? RecordingSummary(pending) : null,
                note = recording?.Note }, trainerRecording = _trainerRecording, message = _notice
        };
    }

    public async Task<object?> InvokeAsync(string action, JsonElement args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (action)
        {
            case "state": return GetState();
            case "deviceRefresh": return GetState();
            case "navigationSet":
                Runtime.ConfigureNavigation(new(args.Flag("enabled"), args.EnumValue<ControlId>("step"),
                    args.EnumValue<ControlId>("activate"), args.GetProperty("debounceMs").GetInt32()));
                _notice = args.Flag("enabled") ? "Controller input is navigating Studio; gameplay output is neutral." : "Controller navigation is off. Release controls before returning to the game.";
                return Runtime.NavigationStatus;
            case "feedbackSave":
            {
                var settings = args.GetProperty("settings").Read<FeedbackSettings>(); settings.Validate();
                var runtime = Runtime;
                if (runtime.FeedbackStatus is null) throw new InvalidOperationException("Feedback devices are unavailable.");
                _store.SaveFeedbackSettings(settings); runtime.ConfigureFeedback(settings); _feedbackSettings = settings;
                _notice = "Feedback preferences saved."; break;
            }
            case "feedbackTest":
                return new { queued = Runtime.TestFeedback(), status = Runtime.FeedbackStatus };
            case "coPilotSave":
            {
                RequireEditable();
                var settings = args.GetProperty("settings").Read<CoPilotSettings>(); settings.Validate();
                var runtime = Runtime;
                var previous = runtime.CoPilotSettings;
                string primaryBefore = runtime.Latest?.Input.DeviceId ?? previous.PrimaryDeviceId;
                bool apply = settings.Enabled || previous.Enabled;
                try
                {
                    if (apply) runtime.ConfigureCoPilot(settings);
                    _store.SaveCoPilotSettings(settings); _coPilotSettings = settings;
                }
                catch
                {
                    if (apply)
                    {
                        try { runtime.ConfigureCoPilot(previous with { PrimaryDeviceId = previous.Enabled ? previous.PrimaryDeviceId : primaryBefore }); }
                        catch { runtime.ConfigureCoPilot(new CoPilotSettings { PrimaryDeviceId = primaryBefore }); }
                    }
                    throw;
                }
                _notice = settings.Enabled ? "Co-pilot inputs connected. Release controls before returning to gameplay." : "Co-pilot input is off.";
                break;
            }
            case "deviceSelect": Runtime.SelectController(args.Text("id")); _notice = "Input controller selected."; break;
            case "packImport":
            {
                RequireEditable(); if (_packOperation is not null) throw new InvalidOperationException("A game pack is already being imported.");
                using var packCancellation = new CancellationTokenSource(); _packOperation = packCancellation; int packRevision = _revision;
                try
                {
                    var picker = new FileOpenPicker(_owner); picker.FileTypeFilter.Add(".json");
                    var chosen = await picker.PickSingleFileAsync(); CheckRevision(packRevision); packCancellation.Token.ThrowIfCancellationRequested();
                    if (chosen is null) break;
                    var importedPack = await Task.Run(() =>
                    {
                        var validated = GamePackDefinition.Read(chosen.Path);
                        if (validated.Manifest.Runtime is not null) throw new InvalidOperationException("Install implementation plugins through Marketplace. This importer accepts controller packs.");
                        var installed = _packs.FirstOrDefault(pack => pack.Manifest.Id == validated.Manifest.Id && pack.Manifest.Version == validated.Manifest.Version);
                        if (installed is not null)
                        {
                            if (installed.ManifestSha256 != validated.ManifestSha256) throw new InvalidDataException("This pack/version already exists with another hash. Use a new version.");
                            return installed;
                        }
                        foreach (var asset in validated.Manifest.Assets.Where(asset => asset.Kind == GamePackAssetKind.VisionManifest))
                            _ = EliteScriptStudio.Vision.VisionModelManifest.Read(validated.ResourcePath(asset));
                        return _packLibrary.Import(chosen.Path, packCancellation.Token);
                    }, packCancellation.Token);
                    CheckRevision(packRevision); packCancellation.Token.ThrowIfCancellationRequested();
                    _packs = [.. _packs.Where(pack => pack.Manifest.Id != importedPack.Manifest.Id || pack.Manifest.Version != importedPack.Manifest.Version), importedPack];
                    _notice = "Game pack imported. Review its resources and tuning before creating a setup. Nothing was activated.";
                }
                finally { _packOperation = null; }
                break;
            }
            case "packCreateSetup":
            {
                RequireEditable(); var pack = FindPack(args.Text("key"));
                var values = args.GetProperty("values").Read<Dictionary<string, double>>();
                var setup = pack.CreateProfile(values); _store.Save(setup); Runtime.SetScriptsEnabled(false); SelectProfile(setup);
                _notice = "Pack setup created. Sequences, vision and voice commands remain off until explicitly enabled."; break;
            }
            case "packTune":
            {
                RequireEditable(); var pack = FindPack(args.Text("key"));
                var values = args.GetProperty("values").Read<Dictionary<string, double>>();
                Runtime.SetScriptsEnabled(false); Save(pack.Retune(_active, values));
                _notice = "Pack tuning saved. Sequences are paused; enable them explicitly when ready."; break;
            }
            case "trainerStart":
            {
                RequireEditable();
                var pack = RequireTrainer(args.Text("key"));
                string shotId = args.Text("shotId"), input = args.Text("shotInput");
                _ = TrainingShot(pack, shotId);
                if (input is not ("rightStick" or "a" or "b" or "x" or "y")) throw new InvalidDataException("Choose the recorded shot input.");
                Runtime.SetScriptsEnabled(false);
                Runtime.StartRecording(RecordedInputTraining.ChannelsFor(input));
                _trainerRecording = new(args.Text("key"), _active.Id, shotId, input);
                _notice = "Recording one physical attempt. Release the shot input before stopping.";
                break;
            }
            case "trainerConvert":
            {
                RequireEditable(allowPending: true);
                var pack = RequireTrainer(args.Text("key"));
                var context = _trainerRecording;
                if (context is null || context.ProfileId != _active.Id || context.Key != args.Text("key") || context.ShotId != args.Text("shotId")
                    || Runtime.Recording.IsRecording)
                    throw new InvalidOperationException("Record and stop a baseline for this shot first.");
                var pending = Runtime.Recording.Pending ?? throw new InvalidOperationException("No physical attempt is available. Record a new baseline.");
                if (!string.IsNullOrEmpty(Runtime.Recording.Note)) throw new InvalidOperationException("This recording was interrupted. Discard it and record a complete attempt.");
                var trainer = pack.Manifest.ControllerTrainer!;
                string timing = args.Text("timingFeedback"), tempo = context.ShotInput == "rightStick" ? args.Text("tempoFeedback") : "none";
                _ = trainer.Adjustment(timing); _ = trainer.Adjustment(tempo, tempo: true);
                var shot = TrainingShot(pack, context.ShotId);
                var previous = _active.LuaScripts.Values.FirstOrDefault(script => script.Training is { } training && training.PackId == pack.Manifest.Id && training.ShotId == shot.Id);
                var training = RecordedInputTraining.Create(pack.Manifest.Id, shot.Id, context.ShotInput, pending, timing, tempo);
                // Conversion preserves the measured attempt. Corrections happen only after a subsequent practice attempt.
                var script = training.ToScript(previous?.Id ?? Guid.NewGuid(), shot.Label);
                Runtime.SetScriptsEnabled(false); Save(_active.WithLuaScript(script), allowPending: true);
                Runtime.DiscardRecording(); _trainerRecording = null;
                _notice = "Baseline converted to Lua. Assign an input, then practice and tune from feedback.";
                return script;
            }
            case "trainerTune":
            {
                RequireEditable();
                var pack = RequireTrainer(args.Text("key"));
                var script = TrainerScript(pack, args.Id());
                var training = script.Training!;
                if (args.Flag("feedback"))
                {
                    string timing = args.Text("timingFeedback"), tempo = args.Text("tempoFeedback");
                    int holdAdjustment = pack.Manifest.ControllerTrainer!.Adjustment(timing);
                    int tempoAdjustment = pack.Manifest.ControllerTrainer.Adjustment(tempo, tempo: true);
                    if (training.TempoMs is null && tempo != "none") throw new InvalidDataException("This recording has no measured stick stroke to tune. Choose No tempo feedback.");
                    training = training with { HoldMs = training.HoldMs + holdAdjustment,
                        TempoMs = training.TempoMs is int duration ? duration + tempoAdjustment : null, TimingFeedback = timing, TempoFeedback = tempo };
                }
                else training = training with { HoldMs = args.GetProperty("holdMs").GetInt32(),
                    TempoMs = training.TempoMs is null ? null : args.GetProperty("tempoMs").GetInt32() };
                var updated = training.ToScript(script.Id, script.Name);
                Save(_active.WithLuaScript(updated));
                _notice = "Shot Lua updated. Run scripts keeps its current state.";
                return updated;
            }
            case "trainerAssign":
            {
                RequireEditable();
                var pack = RequireTrainer(args.Text("key"));
                var script = TrainerScript(pack, args.Id());
                var control = args.EnumValue<ControlId>("control");
                var existing = _active.GetBinding(control);
                if (existing.Script != ScriptKind.Passthrough && existing.LuaScriptId != script.Id)
                    throw new InvalidOperationException("That input already has a mapping. Choose another input or change its mapping on Button mapping first.");
                if (control is ControlId.SmallLeft or ControlId.SmallRight or ControlId.LargeLeft or ControlId.LargeRight
                    && Runtime.Latest?.Input.IsPaddleInterfacePresent != true)
                    throw new InvalidOperationException("Paddle input is unavailable on the selected controller. Choose a button or connect a supported paddle controller.");
                var profile = _active with { Bindings = _active.Bindings.RemoveRange(_active.Bindings.Where(pair => pair.Value.LuaScriptId == script.Id).Select(pair => pair.Key)) };
                Save(profile.WithBinding(control, new(ScriptKind.Lua, true, true, null, script.Id)));
                _notice = "Shot assigned. Start controller and enable Run scripts when ready to practice.";
                break;
            }
            case "packModelImport":
            {
                RequireEditable(); if (_packOperation is not null) throw new InvalidOperationException("Another game-pack operation is finishing.");
                using var packCancellation = new CancellationTokenSource(); _packOperation = packCancellation; int packRevision = _revision;
                try
                {
                    var selectedPack = FindPack(args.Text("key"));
                    var pack = await Task.Run(() => GamePackDefinition.Read(Path.Combine(selectedPack.DirectoryPath, "pack.json")), packCancellation.Token);
                    CheckRevision(packRevision); packCancellation.Token.ThrowIfCancellationRequested();
                    if (pack.ManifestSha256 != selectedPack.ManifestSha256) throw new InvalidDataException("The pack manifest changed. Re-import a new version before using its models.");
                    var asset = pack.Manifest.Assets.FirstOrDefault(asset => asset.Path == args.Text("path") && asset.Kind == GamePackAssetKind.VisionManifest)
                        ?? throw new InvalidDataException("Choose a declared model manifest from the pack.");
                    if (_importPackModel is null) throw new InvalidOperationException("Vision model import is unavailable.");
                    await _importPackModel(pack.ResourcePath(asset)); _notice = "Pack model imported into Remote Play. Existing validation/calibration gates still apply.";
                }
                finally { _packOperation = null; }
                break;
            }
            case "outputStart": await StartOutputAsync(); break;
            case "outputStop": StopOutput(); break;
            case "stopAll": await _stopAll(); break;
            case "scriptsSet":
                if (args.Flag("enabled"))
                {
                    RequireNoTraining();
                    await StartOutputAsync();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    RequireNoTraining();
                    if (!Runtime.HasOutput) throw new InvalidOperationException("Controller output could not connect. Scripts remain paused.");
                }
                Runtime.SetScriptsEnabled(args.Flag("enabled"));
                _notice = args.Flag("enabled") ? "Controller output is connected and mapped scripts are enabled." : "Scripts are paused. Manual controller input still passes through.";
                break;
            case "profileSelect":
                RequireEditable(); SelectProfile(_store.Load(args.Id())); break;
            case "profileCreate":
                RequireEditable();
                var created = StudioProfile.CreateDefault(args.Text("name", "New controller setup").Trim());
                _store.Save(created); SelectProfile(created); break;
            case "profileDuplicate":
                RequireEditable();
                var duplicate = _active with { Id = Guid.NewGuid(), Name = args.Text("name", _active.Name + " copy").Trim(), ScriptsEnabled = false };
                _store.Save(duplicate); SelectProfile(duplicate); break;
            case "profileRename": Save(_active with { Name = args.Text("name").Trim() }); break;
            case "profileDelete":
                RequireEditable();
                Guid deleteId = args.Id();
                if (_profiles.Count <= 1) throw new InvalidOperationException("Keep at least one controller setup.");
                if (!_profiles.Any(profile => profile.Id == deleteId)) throw new InvalidOperationException("That setup no longer exists.");
                if (_active.Id == deleteId) SelectProfile(_store.Load(_profiles.First(profile => profile.Id != deleteId).Id));
                _store.Delete(deleteId); RefreshProfiles(); _notice = "Controller setup deleted."; break;
            case "profileImport":
            {
                RequireEditable(); int revision = _revision;
                var picker = new FileOpenPicker(_owner); picker.FileTypeFilter.Add(".json");
                if (await picker.PickSingleFileAsync() is { } file)
                { CheckRevision(revision); SelectProfile(_store.Import(file.Path)); }
                break;
            }
            case "profileExport":
            {
                var profile = _active;
                var picker = new FileSavePicker(_owner) { SuggestedFileName = "AccessRelay-Setup" };
                picker.FileTypeChoices.Add("Controller setup", [".json"]);
                if (await picker.PickSaveFileAsync() is { } file) _store.Export(profile, file.Path);
                _notice = "Controller setup export finished."; break;
            }
            case "bindingSave":
            {
                var control = args.EnumValue<ControlId>("control");
                var script = args.EnumValue<ScriptKind>("script");
                if (script is not (ScriptKind.Passthrough or ScriptKind.Lua)) throw new InvalidDataException("Choose normal input or a Lua script.");
                Guid? recordingId = Guid.TryParse(args.Text("recordingId"), out var rid) ? rid : null;
                Guid? luaId = Guid.TryParse(args.Text("luaScriptId"), out var lid) ? lid : null;
                Save(_active.WithBinding(control, new(script, args.Flag("enabled", true), args.Flag("consume"), recordingId, luaId)));
                _notice = ControlLabel(control) + " mapping saved."; break;
            }
            case "stickySave":
            {
                var controls = args.GetProperty("controls").Read<ControlId[]>();
                if (controls is null || controls.Length > 16 || controls.Any(control => !StickyButtons.Supports(control)))
                    throw new InvalidDataException("Choose gamepad buttons or triggers for sticky input.");
                Save(_active with { StickyControls = controls.ToImmutableHashSet() });
                _notice = "Sticky controls saved. Release the controls before pressing to latch.";
                break;
            }
            case "smoothingSave":
            {
                var settings = args.GetProperty("settings").Read<StickSmoothing>();
                settings.Validate(); Save(_active with { Smoothing = settings });
                _notice = "Stick smoothing saved. Stronger smoothing adds response delay.";
                break;
            }
            case "deadzoneSave":
            {
                double left = args.GetProperty("leftPercent").GetDouble(), right = args.GetProperty("rightPercent").GetDouble();
                var settings = new StickDeadzone(left, right);
                if (!settings.IsValid) throw new InvalidDataException("Choose a valid stick deadzone.");
                RequireNoTraining(); _store.SaveStickDeadzone(settings); Runtime.SetStickDeadzone(settings); _deadzone = settings;
                _notice = "Stick deadzones saved."; break;
            }
            case "recordStart":
            {
                RecordingChannels channels = RecordingChannels.None;
                if (args.TryGetProperty("channels", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray())
                    {
                        if (!Enum.TryParse<RecordingChannels>(item.GetString(), true, out var value) || !Enum.IsDefined(value))
                            throw new InvalidDataException("Choose supported recording controls.");
                        channels |= value;
                    }
                if (channels == RecordingChannels.None) throw new InvalidDataException("Choose at least one control to record.");
                Runtime.StartRecording(channels); _trainerRecording = null; _notice = "Recording physical controller input."; break;
            }
            case "recordStop": Runtime.StopRecording(); _notice = "Recording stopped. Save it or discard it."; break;
            case "recordDiscard": Runtime.DiscardRecording(); _trainerRecording = null; _notice = "Unsaved recording discarded."; break;
            case "recordSave":
            {
                var pending = Runtime.Recording.Pending ?? throw new InvalidOperationException("Record and stop controller input first.");
                var script = RecordedLua.Convert(pending with { Name = args.Text("name", "Recorded input").Trim() });
                Save(_active.WithLuaScript(script), allowPending: true);
                Runtime.DiscardRecording(); _trainerRecording = null; _notice = "Recording saved as a Lua script."; return script;
            }
            case "recordImport":
            case "luaImport":
            {
                RequireEditable(); int revision = _revision;
                var picker = new FileOpenPicker(_owner); picker.FileTypeFilter.Add(".lua"); picker.FileTypeFilter.Add(".json");
                if (await picker.PickSingleFileAsync() is not { } file) return null;
                CheckRevision(revision);
                using var stream = File.OpenRead(file.Path);
                if (stream.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("The script file is empty or too large.");
                LuaControllerScript script;
                if (Path.GetExtension(file.Path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var recording = JsonSerializer.Deserialize<MacroRecording>(stream, DesktopJson.Options)
                        ?? throw new InvalidDataException("The recording file is invalid.");
                    script = RecordedLua.Convert(recording with { Id = Guid.NewGuid() });
                }
                else
                {
                    using var reader = new StreamReader(stream);
                    script = new(Guid.NewGuid(), Path.GetFileNameWithoutExtension(file.Path), await reader.ReadToEndAsync());
                }
                _ = new LuaControllerProgram(script); Save(_active.WithLuaScript(script)); return script;
            }
            case "luaExport":
            {
                var script = _active.LuaScripts.TryGetValue(args.Id(), out var found) ? found : throw new InvalidOperationException("Choose a saved Lua script.");
                var picker = new FileSavePicker(_owner) { SuggestedFileName = "AccessRelay-Script" };
                picker.FileTypeChoices.Add("Lua script", [".lua"]);
                if (await picker.PickSaveFileAsync() is { } file) await File.WriteAllTextAsync(file.Path, script.Source);
                break;
            }
            case "luaGet":
                return _active.LuaScripts.TryGetValue(args.Id(), out var existingLua) ? existingLua : throw new InvalidOperationException("That Lua script no longer exists.");
            case "luaNew": return LuaControllerScript.Create();
            case "luaValidate":
            {
                var candidate = new LuaControllerScript(Guid.NewGuid(), "Syntax check", args.Text("source"));
                _ = new LuaControllerProgram(candidate); return new { valid = true, message = "Lua syntax and controller APIs checked. Runtime behavior still needs testing." };
            }
            case "luaSave":
            {
                var source = args.GetProperty("script").Read<LuaControllerScript>();
                if (source.Id == Guid.Empty) source = source with { Id = Guid.NewGuid() };
                _ = new LuaControllerProgram(source); Save(_active.WithLuaScript(source)); break;
            }
            case "luaDelete":
                if (!_active.LuaScripts.ContainsKey(args.Id())) throw new InvalidOperationException("That Lua script no longer exists.");
                Save(_active.WithoutLuaScript(args.Id())); break;
            case "nbaFeedback": // Compatibility alias for older clients; still requires the linked pack.
            case "packAction":
            {
                var plugin = _gamePlugins().FirstOrDefault(item => item.Id == _active.GamePack?.Id)
                    ?? throw new InvalidOperationException("This action requires a matching game pack setup.");
                var result = plugin.ApplyControllerAction(_active, action == "nbaFeedback" ? "feedback" : args.Text("action"), args);
                Save(result.Profile); return result.Result;
            }
            default: throw new InvalidOperationException("This controller action is not supported: " + action);
        }
        return new { message = _notice };
    }

    internal Task StartOutputAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Runtime.HasOutput) return Task.CompletedTask;
        if (_outputTask is { IsCompleted: false }) return _outputTask;
        _outputStart?.Dispose();
        var cancellation = _outputStart = new CancellationTokenSource();
        return _outputTask = OpenOutputAsync(cancellation);
    }

    private async Task OpenOutputAsync(CancellationTokenSource cancellation)
    {
        var runtime = Runtime;
        IControllerOutput? output = null;
        _notice = "Connecting the virtual controller…";
        try
        {
            runtime.ReserveVirtualOutput();
            output = await VirtualOutput.TryOpenAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!output.IsAvailable) throw new InvalidOperationException(output.Status);
            _notice = output.Status; runtime.Attach(output); output = null;
        }
        finally
        {
            output?.Dispose(); runtime.ReleaseVirtualOutputReservation();
        }
    }

    internal void StopOutput()
    {
        _packOperation?.Cancel();
        _outputStart?.Cancel(); _runtime()?.StopOutput(stopScripts: true);
        _notice = "Controller output stopped.";
    }

    internal Task WaitForOutputAsync() => _outputTask ?? Task.CompletedTask;
    private StudioRuntime Runtime => _runtime() ?? throw new InvalidOperationException("The controller engine is not available.");
    private GamePackDefinition FindPack(string key) => _packs.FirstOrDefault(pack => pack.Manifest.Id + "@" + pack.Manifest.Version == key)
        ?? throw new InvalidOperationException("Choose an installed game pack.");
    private GamePackDefinition RequireTrainer(string key)
    {
        var pack = FindPack(key);
        if (pack.Manifest.ControllerTrainer is null || _active.GamePack is not { } reference || reference.Id != pack.Manifest.Id
            || reference.Version != pack.Manifest.Version || reference.ManifestSha256 != pack.ManifestSha256)
            throw new InvalidOperationException("Create a controller profile from this trainer first.");
        return pack;
    }
    private static ControllerTrainingShot TrainingShot(GamePackDefinition pack, string shotId) => pack.Manifest.ControllerTrainer!.Shots.FirstOrDefault(shot => shot.Id == shotId)
        ?? throw new InvalidDataException("Choose a shot from this trainer.");
    private LuaControllerScript TrainerScript(GamePackDefinition pack, Guid id)
    {
        if (!_active.LuaScripts.TryGetValue(id, out var script) || script.Training is not { } training || training.PackId != pack.Manifest.Id)
            throw new InvalidOperationException("Record a baseline for this shot first.");
        _ = TrainingShot(pack, training.ShotId);
        if (!training.MatchesGeneratedSource(script.Source))
            throw new InvalidOperationException("This Lua source was edited outside the trainer. Record a new baseline to tune it without overwriting custom code.");
        return script;
    }
    private static object RecordingSummary(MacroRecording recording) => new
    { recording.Id, recording.Name, recording.DurationMs, recording.Channels, frameCount = recording.Frames.Length };

    private void Save(StudioProfile profile, bool allowPending = false)
    {
        RequireEditable(allowPending); profile = LuaProfileMigration.Convert(profile) with { ScriptsEnabled = _runtime()?.ScriptsEnabled == true };
        profile.Validate(); foreach (var script in profile.LuaScripts.Values) _ = new LuaControllerProgram(script);
        _store.SaveRecordingEdit(_persisted, profile);
        _runtime()?.Apply(profile); _active = _persisted = profile; _revision++;
        RefreshProfiles(); _notice = "Saved " + profile.Name + ".";
    }

    private void SelectProfile(StudioProfile profile)
    {
        RequireEditable(); var migrated = LuaProfileMigration.Convert(profile); if (!ReferenceEquals(migrated, profile)) _store.Save(migrated); profile = migrated; profile.Validate();
        _persisted = profile;
        _active = profile with { ScriptsEnabled = _runtime()?.ScriptsEnabled == true };
        _runtime()?.Apply(_active); _store.SaveSelectedId(profile.Id); _revision++;
        RefreshProfiles(); _notice = "Using " + profile.Name + ".";
    }

    private void RefreshProfiles() => _profiles = _store.LoadAll();
    private void RequireNoTraining()
    { if (_runtime()?.IsVisionTrainingRecording == true) throw new InvalidOperationException("Finish the gameplay training clip before changing controller processing."); }
    private void RequireEditable(bool allowPending = false)
    {
        RequireNoTraining();
        if (_runtime()?.Recording is { } recording && (recording.IsRecording || (!allowPending && recording.Pending is not null)))
            throw new InvalidOperationException("Finish and save or discard the recorded sequence before changing this setup.");
    }
    private void CheckRevision(int revision)
    { RequireEditable(); if (_disposed || _revision != revision) throw new InvalidOperationException("The selected setup changed while choosing a file. Try again."); }

    internal static string EnumName<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static string ControlLabel(ControlId control) => control switch
    {
        ControlId.SmallLeft => "Upper left paddle", ControlId.SmallRight => "Upper right paddle",
        ControlId.LargeLeft => "Lower left paddle", ControlId.LargeRight => "Lower right paddle",
        ControlId.LeftStick => "Left stick click", ControlId.RightStick => "Right stick click",
        _ => SplitLabel(control.ToString())
    };
    private static string ChannelLabel(RecordingChannels value) => value switch
    { RecordingChannels.All => "All controls", RecordingChannels.Buttons => "All buttons", _ => SplitLabel(value.ToString()) };
    private static string SplitLabel(string value) => System.Text.RegularExpressions.Regex.Replace(value, "(?<=[a-z])(?=[A-Z])", " ");
    public void SetVisible(bool visible) { }
    public DesktopMedia? ReadMedia(string path) => null;
    public void Dispose() { _disposed = true; _outputStart?.Cancel(); _packOperation?.Cancel(); }
}
