using System.Text.Json;

namespace EliteScriptStudio.Application;

public sealed record AssistantPermissions(bool Enabled = false, bool Drafts = false, bool Offline = false, int Minutes = 15);
public sealed record AssistantGrant(long Generation, CancellationToken Cancellation, bool Drafts, bool Offline);
public sealed record AssistantAccessStatus(bool Enabled, bool Drafts, bool Offline, DateTimeOffset? ExpiresAtUtc, long Generation);
public sealed record AssistantBlocker(string Code, string Message, string NextAction);

/// <summary>Application-owned authorization, independent of MCP protocol sessions or client hints.</summary>
public sealed class AssistantSession : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private CancellationTokenSource _epoch = new();
    private AssistantPermissions _permissions = new();
    private long _generation, _started;
    private DateTimeOffset? _expires;
    public AssistantSession(TimeProvider? time = null) => _time = time ?? TimeProvider.System;
    public void Configure(AssistantPermissions permissions)
    {
        if (permissions.Minutes is < 1 or > 60) throw new AssistantException("invalidDuration", "Choose 1–60 minutes of access.");
        CancellationTokenSource previous;
        lock (_gate)
        {
            previous = _epoch; _epoch = new(); _generation++;
            _permissions = permissions; _started = _time.GetTimestamp();
            _expires = permissions.Enabled ? _time.GetUtcNow().AddMinutes(permissions.Minutes) : null;
            if (permissions.Enabled) _epoch.CancelAfter(TimeSpan.FromMinutes(permissions.Minutes));
        }
        previous.Cancel(); previous.Dispose();
    }
    private bool Enabled => _permissions.Enabled && !_epoch.IsCancellationRequested && _time.GetElapsedTime(_started) < TimeSpan.FromMinutes(_permissions.Minutes);
    public AssistantAccessStatus Status
    { get { lock (_gate) { bool on = Enabled; return new(on, on && _permissions.Drafts, on && _permissions.Offline, on ? _expires : null, _generation); } } }
    public AssistantGrant Require(bool drafts = false, bool offline = false)
    {
        lock (_gate)
        {
            if (!Enabled) throw new AssistantException("accessDisabled", "Enable time-limited assistant access in Studio Settings.");
            if (drafts && !_permissions.Drafts) throw new AssistantException("draftsDisabled", "Allow configuration drafts in Studio Settings.");
            if (offline && !_permissions.Offline) throw new AssistantException("offlineDisabled", "Allow offline workflows in Studio Settings.");
            return new(_generation, _epoch.Token, _permissions.Drafts, _permissions.Offline);
        }
    }
    public void Check(AssistantGrant grant)
    {
        lock (_gate) if (!Enabled || grant.Generation != _generation || grant.Cancellation.IsCancellationRequested)
            throw new AssistantException("accessRevoked", "Assistant access expired or was revoked. The request was not applied.");
    }
    public void Dispose() { Configure(new()); lock (_gate) _epoch.Dispose(); }
}

public sealed partial class StudioAssistantService(IStudioApplicationContext context, AssistantSession access)
{
    public AssistantSession Access { get; } = access;
    public async Task<StudioApplicationContext> ReadContextAsync(AssistantGrant grant, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, grant.Cancellation);
        var state = await context.ReadAsync(linked.Token).ConfigureAwait(false);
        Access.Check(grant);
        if (state.Profile.Id != state.Token.ProfileId || AssistantJson.ProfileDigest(state.Profile) != state.Token.ProfileSha256)
            throw new AssistantException("inconsistentContext", "Studio changed during the read. Request a fresh context.");
        return state;
    }
    public async Task<object> ReadStatusAsync(CancellationToken cancellation)
    {
        var state = await ReadContextAsync(Access.Require(), cancellation);
        return new { schemaVersion = 1, context = state.Token, runtime = state.Runtime, blockers = Explain(state.Runtime),
            configuration = new { state.Profile.Name, state.Profile.Smoothing,
                stickyControls = state.Profile.StickyControls.OrderBy(control => (uint)control),
                bindings = state.Profile.Bindings.Select(item => new { control = item.Key, item.Value.Script, item.Value.Enabled, item.Value.Consume }),
                luaScriptCount = state.Profile.LuaScripts.Count, state.Profile.GamePack },
            limitations = new[] { "No controller, microphone or capture activation tools.", "Configuration changes require review in Studio.",
                "No gameplay accuracy or physical timing is inferred from diagnostics.", "External clients choose their own local/cloud AI; tool results can leave this computer through that client." } };
    }
    public static AssistantBlocker[] Explain(StudioRuntimeFacts status)
    {
        var result = new List<AssistantBlocker>();
        void Add(bool when, string code, string message, string action) { if (when) result.Add(new(code, message, action)); }
        Add(!status.ControllerConnected, "controllerDisconnected", "No selected physical controller is connected.", "Check the selected physical device in Controller.");
        Add(!status.OutputActive, "outputOff", "Virtual output is off.", "Start it yourself in Controller when ready.");
        Add(!status.ScriptsEnabled, "scriptsOff", "Sequences are disabled.", "Review mappings before explicitly enabling sequences.");
        Add(status.Recording, "recordingActive", "A clip or controller sequence is recording or awaiting review.", "Finish and save or discard the recording before editing.");
        Add(!status.VisionRunning, "visionOff", "Vision observation is off.", "Choose and review a source in Remote Play.");
        Add(!status.ModelReady, "modelUnavailable", "No model is ready for this vision session.", "Inspect model compatibility and provider status in Remote Play.");
        Add(!status.VisionCanArm, "visionEvidenceUnavailable", "This session cannot currently arm automatic assistance.", "Inspect matching model, profile/camera, crop, provider, held-out and hardware evidence in Remote Play; do not bypass its gates.");
        Add(status.NavigationActive, "navigationActive", "Physical controls are navigating Studio.", "Exit navigation and release controls before gameplay.");
        Add(status.HasUnsavedChanges, "unsavedChanges", "The interface has unsaved edits.", "Save or discard them before applying an assistant draft.");
        Add(status.CoPilotEnabled && !status.CompanionConnected, "companionDisconnected", "The selected companion input is disconnected.", "Check co-pilot selection and ownership; the primary can still contribute.");
        return result.ToArray();
    }
    public static IReadOnlyDictionary<string, string> Prompts { get; } = new Dictionary<string, string>
    {
        ["accommodation_planner"] = "Ask which movements and controls are comfortable and the user's task. Read current capabilities/status. Propose small reversible sticky/smoothing or Lua drafts, explain responsiveness tradeoffs, then ask the user to review in Studio. Never diagnose a condition, infer a universal tremor frequency, enable output, or claim a physical test passed.",
        ["diagnose_studio"] = "Read Studio status and use returned blocker codes as evidence. Distinguish disabled output, stale/context mismatches, missing model evidence, recording and navigation. Ask for the exact action when evidence is missing. Never manufacture a root cause from a screenshot or recommend bypassing model gates.",
        ["dataset_coverage_coach"] = "Audit explicitly registered training/validation sessions. Suggest the next useful independently recorded session based on missing camera/build/scene coverage and user effort. Preserve held-out isolation. AI labels and user corrections are review candidates, not measured timing or ground truth.",
        ["personal_sound_coach"] = "Ask about intended clicks/hums/phrases, comfortable effort and microphone conditions. Suggest a small repeatable trial and the smallest useful additional sample for confused commands. Existing DTW scores are not probabilities; no diagnostic/accuracy claim. Do not request or transmit raw audio through this release.",
        ["review_model_experiment"] = "Compare reproducible validation results for the same corpus and thresholds. Explain timing-error/coverage/false-release/resource tradeoffs and missing evidence. Keep candidates observation-only, never tune on held-out results or certify hardware timing, and never promote/arm a model."
    };
    public static string LuaContract()
    {
        using var stream = typeof(StudioAssistantService).Assembly.GetManifestResourceStream("LuaContract.md")!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}
