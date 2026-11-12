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
    private IReadOnlyList<StudioProfile> _profiles = [];
    private StudioProfile _active = StudioProfile.CreateDefault(), _persisted;
    private StickDeadzone _deadzone;
    private CancellationTokenSource? _outputStart;
    private Task? _outputTask;
    private string _notice = "Ready. Controller output and scripts start only when you turn them on.";
    private bool _disposed;
    private int _revision;

    internal StudioProfile ActiveProfile => _active;
    internal StickDeadzone Deadzone => _deadzone;
    internal string ProfileDirectory => _store.DirectoryPath;

    internal DesktopControllerService(WindowId owner, Func<StudioRuntime?> runtime, Func<Task> stopAll)
    {
        _owner = owner; _runtime = runtime; _stopAll = stopAll;
        _store = new ProfileStore(DesktopEnvironment.DataDirectory("ControllerProfiles"));
        _deadzone = _store.LoadStickDeadzone();
        try
        {
            _profiles = _store.LoadAll();
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
            bindings = ControlMap.All.Select(control =>
            {
                var binding = _active.GetBinding(control);
                return new { control = EnumName(control), label = ControlLabel(control), binding.Script,
                    binding.Enabled, binding.Consume, binding.RecordingId, binding.LuaScriptId };
            }),
            timings = Enum.GetValues<ShotVariant>().Select(variant => new { variant, label = VariantLabel(variant), timing = _active.GetTiming(variant) }),
            recordings = _active.Recordings.Values.OrderBy(recording => recording.Name).Select(RecordingSummary),
            luaScripts = _active.LuaScripts.Values.OrderBy(script => script.Name).Select(script => new { script.Id, script.Name })
        },
        devices = _runtime()?.Latest?.Input.Devices ?? [], selectedDeviceId = _runtime()?.Latest?.Input.DeviceId,
        deadzone = _deadzone, controls = ControlMap.All.Select(control => new { id = EnumName(control), label = ControlLabel(control), mask = (uint)control }),
        scripts = Enum.GetValues<ScriptKind>().Select(script => new { id = EnumName(script), label = ScriptLabel(script) }),
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
            recording = new { isRecording = recording?.IsRecording == true, hasInput = recording?.HasInput == true,
                elapsedMs = recording?.ElapsedMs ?? 0, pending = recording?.Pending is { } pending ? RecordingSummary(pending) : null,
                note = recording?.Note }, message = _notice
        };
    }

    public async Task<object?> InvokeAsync(string action, JsonElement args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (action)
        {
            case "state": return GetState();
            case "deviceRefresh": return GetState();
            case "deviceSelect": Runtime.SelectController(args.Text("id")); _notice = "Input controller selected."; break;
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
                    if (!Runtime.HasOutput) throw new InvalidOperationException("Connect controller output before enabling sequences.");
                }
                Runtime.SetScriptsEnabled(args.Flag("enabled"));
                _notice = args.Flag("enabled") ? "Controller sequences are enabled." : "Controller sequences are paused.";
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
                Guid? recordingId = Guid.TryParse(args.Text("recordingId"), out var rid) ? rid : null;
                Guid? luaId = Guid.TryParse(args.Text("luaScriptId"), out var lid) ? lid : null;
                Save(_active.WithBinding(control, new(script, args.Flag("enabled", true), args.Flag("consume"), recordingId, luaId)));
                _notice = ControlLabel(control) + " mapping saved."; break;
            }
            case "timingSave":
            {
                var variant = args.EnumValue<ShotVariant>("variant");
                var timing = args.GetProperty("timing").Read<ScriptTiming>(); timing.Validate();
                Save(_active.WithTiming(variant, timing)); break;
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
                Runtime.StartRecording(channels); _notice = "Recording physical controller input."; break;
            }
            case "recordStop": Runtime.StopRecording(); _notice = "Recording stopped. Save it or discard it."; break;
            case "recordDiscard": Runtime.DiscardRecording(); _notice = "Unsaved recording discarded."; break;
            case "recordSave":
            {
                var pending = Runtime.Recording.Pending ?? throw new InvalidOperationException("Record and stop a sequence first.");
                if (_active.Recordings.Count >= MacroRecording.MaxRecordingsPerProfile)
                    throw new InvalidOperationException("This setup already has the maximum number of saved sequences.");
                var named = pending with { Name = args.Text("name", "Recorded sequence").Trim() };
                Save(_active.WithRecording(named), allowPending: true); Runtime.DiscardRecording(); break;
            }
            case "recordGet": return Recording(args.Id());
            case "recordUpdate":
            {
                var recording = args.GetProperty("recording").Read<MacroRecording>();
                if (!_active.Recordings.ContainsKey(recording.Id)) throw new InvalidOperationException("Choose a saved sequence to edit.");
                recording.Validate(); Save(_active.WithRecording(recording)); break;
            }
            case "recordFrame":
            {
                var recording = Recording(args.Id()).WithFrame(args.Number("index"), args.Number("timeMs"), args.GetProperty("state").Read<ControllerState>());
                Save(_active.WithRecording(recording)); return recording;
            }
            case "recordRetime":
            {
                var current = Recording(args.Id());
                var source = args.TryGetProperty("recording", out var draft) && draft.ValueKind == JsonValueKind.Object
                    ? draft.Read<MacroRecording>() : current;
                if (source.Id != current.Id) throw new InvalidDataException("The sequence changed. Reopen it before retiming.");
                source.Validate();
                var recording = source.WithIntervalDuration(args.Number("startFrame"), args.Number("endFrame"), args.Number("durationMs"));
                Save(_active.WithRecording(recording)); return recording;
            }
            case "recordDuplicate":
            {
                var result = _active.DuplicateRecording(args.Id(), args.Text("name", Recording(args.Id()).Name + " copy"));
                Save(result.Profile); return RecordingSummary(result.Copy);
            }
            case "recordDelete": _ = Recording(args.Id()); Save(_active.WithoutRecording(args.Id())); break;
            case "recordImport":
            {
                RequireEditable(); int revision = _revision;
                var picker = new FileOpenPicker(_owner); picker.FileTypeFilter.Add(".json");
                if (await picker.PickSingleFileAsync() is { } file)
                {
                    CheckRevision(revision);
                    using var stream = File.OpenRead(file.Path);
                    if (stream.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("The sequence file is empty or too large.");
                    var recording = JsonSerializer.Deserialize<MacroRecording>(stream, DesktopJson.Options)
                        ?? throw new InvalidDataException("The sequence file is invalid.");
                    recording = recording with { Id = Guid.NewGuid() }; recording.Validate();
                    Save(_active.WithRecording(recording));
                }
                break;
            }
            case "recordExport":
            {
                var recording = Recording(args.Id());
                var picker = new FileSavePicker(_owner) { SuggestedFileName = "AccessRelay-Sequence" };
                picker.FileTypeChoices.Add("Controller sequence", [".json"]);
                if (await picker.PickSaveFileAsync() is { } file) _store.ExportRecording(recording, file.Path);
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
            case "trajectory": return BuildTrajectory(args);
            case "nbaFeedback":
            {
                var timingFeedback = args.EnumValue<Nba2K27TimingFeedback>("timingFeedback");
                var tempoFeedback = args.EnumValue<Nba2K27TempoFeedback>("tempoFeedback");
                var adjustment = Nba2K27Tuning.Suggest(timingFeedback, tempoFeedback);
                if (Guid.TryParse(args.Text("id"), out var id)) Save(_active.WithRecording(Nba2K27Tuning.Apply(Recording(id), adjustment)));
                else { var variant = args.EnumValue<ShotVariant>("variant"); Save(_active.WithTiming(variant, Nba2K27Tuning.Apply(_active.GetTiming(variant), adjustment))); }
                return adjustment;
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
        _outputStart?.Cancel(); _runtime()?.StopOutput(stopScripts: true);
        _notice = "Controller output stopped.";
    }

    internal Task WaitForOutputAsync() => _outputTask ?? Task.CompletedTask;
    private StudioRuntime Runtime => _runtime() ?? throw new InvalidOperationException("The controller engine is not available.");
    private MacroRecording Recording(Guid id) => _active.Recordings.TryGetValue(id, out var recording) ? recording
        : throw new InvalidOperationException("That sequence no longer exists in the selected setup.");
    private static object RecordingSummary(MacroRecording recording) => new
    { recording.Id, recording.Name, recording.DurationMs, recording.Channels, frameCount = recording.Frames.Length };

    private void Save(StudioProfile profile, bool allowPending = false)
    {
        RequireEditable(allowPending); profile = profile with { ScriptsEnabled = _runtime()?.ScriptsEnabled == true };
        profile.Validate(); foreach (var script in profile.LuaScripts.Values) _ = new LuaControllerProgram(script);
        _store.SaveRecordingEdit(_persisted, profile);
        _runtime()?.Apply(profile); _active = _persisted = profile; _revision++;
        RefreshProfiles(); _notice = "Saved " + profile.Name + ".";
    }

    private void SelectProfile(StudioProfile profile)
    {
        RequireEditable(); profile.Validate();
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

    private object BuildTrajectory(JsonElement args)
    {
        if (Guid.TryParse(args.Text("id"), out var id))
        {
            var recording = Recording(id);
            return new { recording.DurationMs, normalized = true, frames = recording.Frames.Select(frame => new { timeMs = frame.TimeMs,
                leftX = frame.State.LeftX / 32767d, leftY = frame.State.LeftY / 32767d,
                rightX = frame.State.RightX / 32767d, rightY = frame.State.RightY / 32767d }), phases = Array.Empty<object>() };
        }
        var variant = args.TryGetProperty("variant", out _) ? args.EnumValue<ShotVariant>("variant") : ShotVariant.Rhythm;
        var phases = Trajectory.Build(_active, variant);
        var points = new List<object>(); int time = 0;
        foreach (var phase in phases)
        { points.Add(new { timeMs = time, rightX = (phase.RightX ?? 0) / 32767d, rightY = phase.RightY / 32767d }); time += phase.DurationMs; }
        points.Add(new { timeMs = time, rightX = 0d, rightY = 0d });
        return new { durationMs = time, normalized = true, frames = points, phases };
    }

    internal static string EnumName<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static string ControlLabel(ControlId control) => control switch
    {
        ControlId.SmallLeft => "Upper left paddle", ControlId.SmallRight => "Upper right paddle",
        ControlId.LargeLeft => "Lower left paddle", ControlId.LargeRight => "Lower right paddle",
        ControlId.LeftStick => "Left stick click", ControlId.RightStick => "Right stick click",
        _ => SplitLabel(control.ToString())
    };
    private static string VariantLabel(ShotVariant variant) => variant switch
    { ShotVariant.Rhythm => "Timed sequence", ShotVariant.MissEarly => "Early alternate", ShotVariant.MissLate => "Late alternate", ShotVariant.Moving => "Moving sequence", _ => "Two-stage sequence" };
    private static string ScriptLabel(ScriptKind script) => script switch
    { ScriptKind.Passthrough => "Normal input", ScriptKind.Rhythm => "Timed sequence", ScriptKind.IntentionalMiss => "Alternating sequence", ScriptKind.Moving => "Moving sequence", ScriptKind.Stepback => "Two-stage sequence", ScriptKind.Recorded => "Recorded sequence", _ => "Lua script" };
    private static string ChannelLabel(RecordingChannels value) => value switch
    { RecordingChannels.All => "All controls", RecordingChannels.Buttons => "All buttons", _ => SplitLabel(value.ToString()) };
    private static string SplitLabel(string value) => System.Text.RegularExpressions.Regex.Replace(value, "(?<=[a-z])(?=[A-Z])", " ");
    public void SetVisible(bool visible) { }
    public DesktopMedia? ReadMedia(string path) => null;
    public void Dispose() { _disposed = true; _outputStart?.Cancel(); }
}
