using System.Text.Json;

namespace EliteScriptStudio.Application;

public sealed partial class StudioAssistantService
{
    public Func<AssistantRequest, CancellationToken, Task<object?>>? OfflineDispatch { get; set; }
    public async Task<object?> DispatchAsync(AssistantRequest request, CancellationToken cancellation)
    {
        switch (request.Operation)
        {
            case "status": return await ReadStatusAsync(cancellation);
            case "draftControls": return await CreateDraftAsync(AssistantJson.Read<ProfileDraftRequest>(request.Arguments), cancellation);
            case "simulateLua":
            {
                var grant = Access.Require(drafts: true);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, grant.Cancellation);
                if (Rehearsal is null) throw new AssistantException("workerUnavailable", "The isolated rehearsal worker is unavailable.");
                var result = await Rehearsal.RunAsync(AssistantJson.Read<LuaSimulationRequest>(request.Arguments), linked.Token);
                Access.Check(grant); return result;
            }
            case "trainingInputs" or "auditDataset" or "visionExperiment" or "trainingJob" or "cancelTrainingJob":
                Access.Require(offline: true);
                if (OfflineDispatch is null) throw new AssistantException("offlineUnavailable", "Register offline tools and inputs in Studio Settings.");
                return await OfflineDispatch(request, cancellation);
            default: throw new AssistantException("unknownOperation", "This assistant operation is not supported.");
        }
    }
}
