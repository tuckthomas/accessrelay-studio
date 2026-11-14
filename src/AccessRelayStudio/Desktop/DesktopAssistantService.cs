using System.Text.Json;
using EliteScriptStudio.Application;
using EliteScriptStudio.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.Storage.Pickers;

namespace EliteScriptStudio.Desktop;

/// <summary>UI ownership and native pickers only. Shared application services own assistant use cases.</summary>
internal sealed class DesktopAssistantService : IStudioApplicationContext, IAsyncDisposable
{
    private readonly Guid _instance = Guid.NewGuid();
    private readonly WindowId _owner;
    private readonly DispatcherQueue _dispatcher;
    private readonly DesktopControllerService _controller;
    private readonly DesktopVisionService _vision;
    private readonly DesktopVoiceService _voice;
    private readonly Func<StudioRuntime?> _runtime;
    private readonly Func<bool> _dirty;
    private readonly AssistantSession _access = new();
    private readonly OfflineJobManager _offline;
    private AssistantPipeServer? _pipe;
    private bool _closing;
    private readonly string _bridgePath = Path.Combine(AppContext.BaseDirectory, "Mcp", "AccessRelayStudio.Mcp.exe");
    internal StudioAssistantService Service { get; }
    internal DesktopAssistantService(WindowId owner, DispatcherQueue dispatcher, DesktopControllerService controller,
        DesktopVisionService vision, DesktopVoiceService voice, Func<StudioRuntime?> runtime, Func<bool> dirty)
    {
        _owner = owner; _dispatcher = dispatcher; _controller = controller; _vision = vision; _voice = voice; _runtime = runtime; _dirty = dirty;
        Service = new(this, _access) { Rehearsal = File.Exists(_bridgePath) ? new IsolatedLuaRehearsal(_bridgePath) : null };
        _offline = new(_access, "", _vision.OfflineWorkflow);
        Service.OfflineDispatch = async (request, cancellation) =>
        {
            long foreground = _offline.ForegroundGeneration;
            if (request.Operation is "auditDataset" or "visionExperiment")
                StudioAssistantService.RequireIdle((await ReadAsync(cancellation)).Runtime);
            return await _offline.DispatchAsync(request, cancellation, foreground);
        };
    }
    internal object UiState() => new
    {
        access = _access.Status, bridgeAvailable = File.Exists(_bridgePath), drafts = Service.Drafts(),
        canUndo = Service.CanUndoFor(_instance, _controller.ActiveProfile.Id, _controller.AssistantRevision, _runtime()?.Latest?.Input.ConnectionVersion ?? 0),
        registration = _offline.RegistrationState(includePaths: true), jobs = _offline.Jobs(),
        clientConfiguration = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> {
            ["accessrelay"] = new { command = _bridgePath, args = Array.Empty<string>() } } }, new JsonSerializerOptions { WriteIndented = true })
    };
    internal async Task<object?> InvokeAsync(string action, JsonElement args)
    {
        switch (action)
        {
            case "assistantState": return UiState();
            case "assistantConfigure":
                var permissions = AssistantJson.Read<AssistantPermissions>(args);
                if (permissions.Enabled && !File.Exists(_bridgePath)) throw new InvalidOperationException("Publish the complete app to include its MCP bridge and rehearsal worker.");
                if (permissions.Enabled && _pipe is null) _pipe = new AssistantPipeServer(Service.DispatchAsync);
                _access.Configure(permissions); return UiState();
            case "assistantReview": return Service.ReviewDraft(args.Id());
            case "assistantApply": await Service.ApplyDraftFromStudioAsync(args.Id(), default); return UiState();
            case "assistantDiscard": Service.DiscardDraft(args.Id()); return UiState();
            case "assistantUndo": await Service.UndoFromStudioAsync(default); return UiState();
            case "assistantCancelJob":
                await _offline.DispatchAsync(new("cancelTrainingJob", JsonSerializer.SerializeToElement(new { jobId = args.Text("id") })), default); return UiState();
            case "assistantRegister":
            {
                string kind = args.Text("kind"); string? path;
                if (kind is "dataset" or "workspace") path = (await new FolderPicker(_owner).PickSingleFolderAsync())?.Path;
                else if (kind is "python" or "thresholds")
                {
                    var picker = new FileOpenPicker(_owner); picker.FileTypeFilter.Add(kind == "python" ? ".exe" : ".json");
                    path = (await picker.PickSingleFileAsync())?.Path;
                }
                else throw new InvalidOperationException("Choose a supported offline input.");
                if (path is not null) await Task.Run(() => _offline.Register(kind, path)); return UiState();
            }
            default: throw new InvalidOperationException("This assistant interface action is not supported.");
        }
    }
    internal void Stop() { _access.Configure(new()); _offline.CancelAll(); }
    internal void BeginClosing() { _closing = true; Stop(); }
    internal void ForegroundCommand() => _offline.CancelAll();
    private sealed record Captured(StudioProfile Profile, long Revision, long InputGeneration, StudioRuntimeFacts Facts);
    private Captured Capture()
    {
        var runtime = _runtime(); var sample = runtime?.Latest; var vision = _vision.AssistantFacts;
        return new(_controller.ActiveProfile, _controller.AssistantRevision, sample?.Input.ConnectionVersion ?? 0,
            new(sample?.Input.IsConnected == true, runtime?.HasOutput == true, runtime?.ScriptsEnabled == true,
                runtime?.Recording is { IsRecording: true } or { Pending: not null } || runtime?.IsVisionTrainingRecording == true || _vision.HasActiveTrainingRecording,
                vision.Running, vision.ModelReady, vision.CanArm, runtime?.VisionStatus.Armed == true,
                _voice.AssistantVoiceActive, _voice.AssistantCommandsActive, runtime?.NavigationStatus.Enabled == true, _dirty(),
                _controller.Deadzone.LeftPercent, _controller.Deadzone.RightPercent, runtime?.CoPilotSettings.Enabled == true,
                sample?.Input.PrimaryConnected == true, sample?.Input.CompanionConnected == true, _controller.AssistantStarting || _vision.AssistantBusy,
                runtime?.VisionStatus.SourceReady == true, vision.SourceStale, runtime?.VisionStatus.RejectedPredictions ?? 0, runtime?.VisionStatus.MissedDeadlines ?? 0));
    }
    private async Task<StudioApplicationContext> Bind(Captured state)
    {
        string digest = await Task.Run(() => AssistantJson.ProfileDigest(state.Profile));
        return new(new(_instance, state.Profile.Id, state.Revision, digest, state.InputGeneration), state.Profile, state.Facts);
    }
    public async Task<StudioApplicationContext> ReadAsync(CancellationToken cancellation) => await Bind(await OnUi(Capture, cancellation));
    public async Task<StudioApplicationContext> ApplyAsync(StudioContextToken expected, StudioProfile profile, CancellationToken cancellation)
    {
        var current = await ReadAsync(cancellation); StudioAssistantService.RequireMatch(expected, current.Token);
        if (profile.Id != expected.ProfileId) throw new AssistantException("staleContext", "The draft belongs to another setup.");
        var applied = await OnUi(() =>
        {
            var fresh = Capture();
            if (expected.InstanceId != _instance || expected.Revision != fresh.Revision || expected.ProfileId != fresh.Profile.Id || expected.InputGeneration != fresh.InputGeneration)
                throw new AssistantException("staleContext", "The selected setup or input context changed before apply.");
            StudioAssistantService.RequireIdle(fresh.Facts); _controller.ApplyAssistantProfile(profile with { ScriptsEnabled = false });
            return Capture();
        }, cancellation);
        // Once committed, return its exact snapshot even if the caller cancels; retain a reliable undo record.
        return await Bind(applied);
    }
    private Task<T> OnUi<T>(Func<T> action, CancellationToken cancellation)
    {
        if (_closing) throw new AssistantException("studioClosing", "Studio is closing.");
        if (_dispatcher.HasThreadAccess) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(action()); }
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (_closing) { result.TrySetException(new AssistantException("studioClosing", "Studio is closing.")); return; }
            if (cancellation.IsCancellationRequested) { result.TrySetCanceled(cancellation); return; }
            try { result.TrySetResult(action()); } catch (Exception error) { result.TrySetException(error); }
        })) result.TrySetException(new AssistantException("studioClosing", "Studio is closing."));
        return result.Task;
    }
    public async ValueTask DisposeAsync()
    { BeginClosing(); if (_pipe is not null) await _pipe.DisposeAsync(); await _offline.DisposeAsync(); _access.Dispose(); }
}
