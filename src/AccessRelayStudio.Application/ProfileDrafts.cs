using System.Collections.Immutable;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Application;

public sealed record ProfileDraftRequest(ControlId[] StickyControls, double LeftSmoothingMs = 0, double RightSmoothingMs = 0,
    string? LuaSource = null, ControlId LuaControl = ControlId.A);
public sealed record ProfileDraftView(Guid Id, string ProfileName, DateTimeOffset ExpiresAtUtc, string[] Changes,
    LuaSimulationResult? Simulation, string State);
sealed record ProfileDraft(ProfileDraftView View, StudioContextToken Expected, StudioProfile Before, StudioProfile After, AssistantGrant Grant, long CreatedAt);
public interface ILuaRehearsal { Task<LuaSimulationResult> RunAsync(LuaSimulationRequest request, CancellationToken cancellation); }

public sealed partial class StudioAssistantService
{
    private readonly object _draftGate = new();
    private readonly Dictionary<Guid, ProfileDraft> _drafts = [];
    private readonly SemaphoreSlim _apply = new(1);
    private (StudioContextToken Expected, StudioProfile Original)? _undo;
    public ILuaRehearsal? Rehearsal { get; set; }
    public async Task<ProfileDraftView> CreateDraftAsync(ProfileDraftRequest request, CancellationToken cancellation)
    {
        var grant = Access.Require(drafts: true);
        if (request.StickyControls is null || request.StickyControls.Length > ControlMap.All.Count || request.StickyControls.Distinct().Count() != request.StickyControls.Length)
            throw new AssistantException("invalidStickyControls", "Choose unique supported sticky controls.");
        var state = await ReadContextAsync(grant, cancellation);
        var next = state.Profile with { StickyControls = request.StickyControls.ToImmutableHashSet(),
            Smoothing = new(request.LeftSmoothingMs, request.RightSmoothingMs), ScriptsEnabled = false };
        var changes = new List<string>();
        if (!state.Profile.StickyControls.SetEquals(next.StickyControls)) changes.Add("Sticky controls: " + string.Join(", ", next.StickyControls.OrderBy(item => (uint)item)));
        if (state.Profile.Smoothing != next.Smoothing) changes.Add($"Smoothing time constants: left {request.LeftSmoothingMs} ms, right {request.RightSmoothingMs} ms.");
        LuaSimulationResult? result = null;
        if (request.LuaSource is { } source)
        {
            if (Rehearsal is null) throw new AssistantException("workerUnavailable", "The isolated Lua rehearsal worker is unavailable.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, grant.Cancellation);
            result = await Rehearsal.RunAsync(new(source, request.LuaControl), linked.Token).ConfigureAwait(false);
            string sourceDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
            if (!result.Valid || !result.Started || result.SourceSha256 != sourceDigest) throw new AssistantException("rehearsalFailed", result.Error ?? "The production engine did not start this exact script in rehearsal.");
            var script = new LuaControllerScript(Guid.NewGuid(), "Assistant draft", source);
            next = next.WithLuaScript(script).WithBinding(request.LuaControl, new(ScriptKind.Lua, LuaScriptId: script.Id));
            changes.Add($"Add Lua mapping on {request.LuaControl}. Source SHA-256: {result.SourceSha256}. Existing mapping is replaced only on review/apply; sequences stay disabled.");
        }
        next.Validate(); cancellation.ThrowIfCancellationRequested(); Access.Check(grant);
        if (changes.Count == 0) throw new AssistantException("noChanges", "This draft does not change the selected setup.");
        var view = new ProfileDraftView(Guid.NewGuid(), state.Profile.Name, DateTimeOffset.UtcNow.AddMinutes(15), changes.ToArray(), result, "pending");
        lock (_draftGate)
        {
            Prune(); if (_drafts.Count >= 16) throw new AssistantException("draftLimit", "Review or discard existing drafts before creating more.");
            _drafts.Add(view.Id, new(view, state.Token, state.Profile, next, grant, System.Diagnostics.Stopwatch.GetTimestamp()));
        }
        return view;
    }
    public ProfileDraftView[] Drafts()
    { lock (_draftGate) { Prune(); return _drafts.Values.Select(draft => draft.View).ToArray(); } }
    private void Prune()
    {
        var status = Access.Status;
        foreach (var item in _drafts.Where(item => !status.Enabled || item.Value.Grant.Generation != status.Generation
            || System.Diagnostics.Stopwatch.GetElapsedTime(item.Value.CreatedAt) >= TimeSpan.FromMinutes(15)).ToArray()) _drafts.Remove(item.Key);
    }
    public object ReviewDraft(Guid id)
    {
        lock (_draftGate)
        {
            Prune(); if (!_drafts.TryGetValue(id, out var draft)) throw new AssistantException("draftExpired", "This draft expired or was revoked.");
            var changed = draft.After.Bindings.Keys.Where(control => draft.Before.GetBinding(control) != draft.After.GetBinding(control)).ToArray();
            object Projection(StudioProfile profile) => new { profile.Id, profile.Name, profile.StickyControls, profile.Smoothing,
                bindings = changed.Select(control => new { control, binding = profile.GetBinding(control),
                    luaSource = profile.GetBinding(control).LuaScriptId is { } scriptId && profile.LuaScripts.TryGetValue(scriptId, out var script) ? script.Source : null }) };
            return new { draft.View, original = Projection(draft.Before), proposed = Projection(draft.After), expected = draft.Expected,
                warning = "Rehearsal covers specified synthetic events, not hardware or general safety. Applying requires all processing stopped and does not activate sequences." };
        }
    }
    public void DiscardDraft(Guid id) { lock (_draftGate) _drafts.Remove(id); }
    public bool CanUndo { get { lock (_draftGate) return _undo is not null; } }
    public bool CanUndoFor(Guid instance, Guid profile, long revision, long inputGeneration)
    {
        lock (_draftGate) return _undo is { } undo && undo.Expected.InstanceId == instance && undo.Expected.ProfileId == profile
            && undo.Expected.Revision == revision && undo.Expected.InputGeneration == inputGeneration;
    }
    // Only the Studio UI calls apply/undo. Neither is exported as an MCP tool.
    public async Task ApplyDraftFromStudioAsync(Guid id, CancellationToken cancellation)
    {
        await _apply.WaitAsync(cancellation);
        try
        {
            ProfileDraft draft;
            lock (_draftGate) { Prune(); draft = _drafts.GetValueOrDefault(id) ?? throw new AssistantException("draftExpired", "This draft expired or was revoked."); }
            Access.Check(draft.Grant);
            var current = await ReadContextAsync(draft.Grant, cancellation);
            RequireMatch(draft.Expected, current.Token); RequireIdle(current.Runtime);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, draft.Grant.Cancellation);
            var updated = await context.ApplyAsync(draft.Expected, draft.After with { ScriptsEnabled = false }, linked.Token);
            lock (_draftGate) { _drafts.Remove(id); _undo = (updated.Token, draft.Before with { ScriptsEnabled = false }); }
        }
        finally { _apply.Release(); }
    }
    public async Task UndoFromStudioAsync(CancellationToken cancellation)
    {
        await _apply.WaitAsync(cancellation);
        try
        {
            (StudioContextToken Expected, StudioProfile Original) undo;
            lock (_draftGate) undo = _undo ?? throw new AssistantException("nothingToUndo", "There is no assistant change to undo.");
            var current = await context.ReadAsync(cancellation);
            RequireMatch(undo.Expected, current.Token); RequireIdle(current.Runtime);
            await context.ApplyAsync(undo.Expected, undo.Original, cancellation);
            lock (_draftGate) _undo = null;
        }
        finally { _apply.Release(); }
    }
    public static void RequireMatch(StudioContextToken expected, StudioContextToken current)
    { if (expected != current) throw new AssistantException("staleContext", "The instance, selected setup, revision or controller context changed. Create a fresh draft."); }
    public static void RequireIdle(StudioRuntimeFacts state)
    {
        if (state.ProcessingTransition || state.OutputActive || state.ScriptsEnabled || state.Recording || state.VisionRunning || state.VoiceRunning
            || state.VoiceCommandsRunning || state.HasUnsavedChanges)
            throw new AssistantException("processingActive", "Stop processing and save/discard interface edits before applying or undoing a draft.");
    }
}
