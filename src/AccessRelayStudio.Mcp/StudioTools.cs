using System.ComponentModel;
using System.Text.Json;
using EliteScriptStudio.Application;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

static class Bridge
{
    internal static string? Pipe;
    internal static async Task<CallToolResult> Call(string operation, object args, CancellationToken cancellation)
    {
        AssistantReply reply;
        try { reply = await AssistantWire.CallAsync(operation, args, cancellation, Pipe); }
        catch (AssistantException error) { reply = new(false, null, error.Code, error.Message); }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        { reply = new(false, null, "studioUnavailable", "Studio is unavailable or the request was cancelled. Check Settings before retrying."); }
        var data = reply.Ok ? reply.Result!.Value : JsonSerializer.SerializeToElement(new { code = reply.Code, message = reply.Message }, AssistantJson.Options);
        return new() { IsError = !reply.Ok, StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }
    internal static async Task<string> Resource(string operation, CancellationToken cancellation)
    {
        var result = await Call(operation, new { }, cancellation);
        return ((TextContentBlock)result.Content[0]).Text;
    }
}

[McpServerToolType]
public sealed class StudioTools
{
    [McpServerTool(Name = "read_studio_status", ReadOnly = true, OpenWorld = false), Description("Read redacted runtime/effective configuration and evidence-based blocker explanations. Requires Studio access; never activates devices.")]
    public static Task<CallToolResult> Status(CancellationToken cancellation) => Bridge.Call("status", new { }, cancellation);

    [McpServerTool(Name = "draft_controls", OpenWorld = false), Description("Propose sticky/smoothing controls and optional Lua mapping. Requires draft permission. Returns an expiring draft; user reviews/applies in Studio. No activation or direct apply.")]
    public static Task<CallToolResult> Draft(ProfileDraftRequest request, CancellationToken cancellation) => Bridge.Call("draftControls", request, cancellation);

    [McpServerTool(Name = "simulate_lua", ReadOnly = true, OpenWorld = false), Description("Rehearse the actual Lua engine in a bounded isolated process using synthetic physical events. No hardware execution or general safety certification.")]
    public static Task<CallToolResult> Simulate(LuaSimulationRequest request, CancellationToken cancellation) => Bridge.Call("simulateLua", request, cancellation);

    [McpServerTool(Name = "list_training_inputs", ReadOnly = true, OpenWorld = false), Description("List opaque IDs for dataset folders, threshold files and workspace explicitly registered in Studio. Requires offline permission; no arbitrary paths.")]
    public static Task<CallToolResult> Inputs(CancellationToken cancellation) => Bridge.Call("trainingInputs", new { }, cancellation);

    [McpServerTool(Name = "audit_dataset", OpenWorld = false), Description("Start a bounded local audit of registered sessions with actual provenance validators. Does not edit labels, certify data quality or access held-out sessions for tuning.")]
    public static Task<CallToolResult> Audit(string[] datasetIds, CancellationToken cancellation) => Bridge.Call("auditDataset", new { datasetIds }, cancellation);

    [McpServerTool(Name = "run_vision_experiment", OpenWorld = false), Description("Start a CPU-only, bounded experiment on registered train/validation IDs. Default planOnly=true. Requires registered interpreter/workspace/thresholds. Never evaluates held-out data or promotes/arms a model.")]
    public static Task<CallToolResult> Experiment(string[] trainingIds, string[] validationIds, string thresholdsId,
        CancellationToken cancellation, bool planOnly = true, int epochs = 1, int seed = 20261112)
        => Bridge.Call("visionExperiment", new { trainingIds, validationIds, thresholdsId, planOnly, epochs, seed }, cancellation);

    [McpServerTool(Name = "read_training_job", ReadOnly = true, OpenWorld = false), Description("Read bounded status and metrics for this access window's offline job. Raw clips, labels and private paths are excluded.")]
    public static Task<CallToolResult> Job(string jobId, CancellationToken cancellation) => Bridge.Call("trainingJob", new { jobId }, cancellation);

    [McpServerTool(Name = "cancel_training_job", OpenWorld = false, Idempotent = true), Description("Cancel an offline job and terminate its owned process tree. Existing files remain for user review in Studio.")]
    public static Task<CallToolResult> Cancel(string jobId, CancellationToken cancellation) => Bridge.Call("cancelTrainingJob", new { jobId }, cancellation);
}

[McpServerResourceType]
public sealed class StudioResources
{
    [McpServerResource(UriTemplate = "accessrelay://studio/status", Name = "studio_status", MimeType = "application/json"), Description("Redacted status, effective configuration and blocker codes.")]
    public static Task<string> Status(CancellationToken cancellation) => Bridge.Resource("status", cancellation);
    [McpServerResource(UriTemplate = "accessrelay://docs/lua-contract", Name = "lua_contract", MimeType = "text/markdown"), Description("The actual shipped Lua engine contract and limits.")]
    public static string Lua() => StudioAssistantService.LuaContract();
    [McpServerResource(UriTemplate = "accessrelay://studio/capabilities", Name = "capabilities", MimeType = "application/json"), Description("Actual assistant capabilities and excluded device/deployment operations.")]
    public static string Capabilities() => JsonSerializer.Serialize(new { schemaVersion = 1,
        transport = "stdio to current-user-only app pipe", activation = "Settings opt-in, time-limited",
        canApplyDraftsFromMcp = false, canActivateDevices = false, canPromoteModels = false, canReadRawMedia = false,
        controlFlow = "inspect -> draft/rehearse -> review in Studio -> apply -> observe -> undo",
        dataFlow = "The external MCP client can send returned context to its own AI provider." }, AssistantJson.Options);
}

[McpServerPromptType]
public sealed class StudioPrompts
{
    [McpServerPrompt(Name = "accommodation_planner"), Description("Plan a small reversible change around comfortable controls and user goals.")]
    public static string Accommodation() => StudioAssistantService.Prompts["accommodation_planner"];
    [McpServerPrompt(Name = "diagnose_studio"), Description("Explain actual runtime blockers without guessing or bypassing gates.")]
    public static string Diagnose() => StudioAssistantService.Prompts["diagnose_studio"];
    [McpServerPrompt(Name = "dataset_coverage_coach"), Description("Identify useful independent recordings and retain label review/held-out isolation.")]
    public static string Coverage() => StudioAssistantService.Prompts["dataset_coverage_coach"];
    [McpServerPrompt(Name = "personal_sound_coach"), Description("Plan low-effort sound recognition trials without exporting audio or making clinical claims.")]
    public static string Sounds() => StudioAssistantService.Prompts["personal_sound_coach"];
    [McpServerPrompt(Name = "review_model_experiment"), Description("Review reproducible validation evidence without model promotion.")]
    public static string Experiments() => StudioAssistantService.Prompts["review_model_experiment"];
}
