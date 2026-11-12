using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Application;

public sealed record StudioContextToken(Guid InstanceId, Guid ProfileId, long Revision, string ProfileSha256, long InputGeneration);
public sealed record StudioRuntimeFacts(bool ControllerConnected, bool OutputActive, bool ScriptsEnabled,
    bool Recording, bool VisionRunning, bool ModelReady, bool VisionCanArm, bool VisionArmed,
    bool VoiceRunning, bool VoiceCommandsRunning, bool NavigationActive, bool HasUnsavedChanges,
    double LeftDeadzonePercent, double RightDeadzonePercent, bool CoPilotEnabled, bool PrimaryConnected, bool CompanionConnected,
    bool ProcessingTransition = false, bool VisionSourceReady = false, bool VisionSourceStale = false,
    long RejectedPredictions = 0, long MissedDeadlines = 0);
public sealed record StudioApplicationContext(StudioContextToken Token, StudioProfile Profile, StudioRuntimeFacts Runtime);

/// <summary>Implemented by the host. Its mutations run on the owner thread and recheck context atomically.</summary>
public interface IStudioApplicationContext
{
    Task<StudioApplicationContext> ReadAsync(CancellationToken cancellation);
    Task<StudioApplicationContext> ApplyAsync(StudioContextToken expected, StudioProfile profile, CancellationToken cancellation);
}

public sealed class AssistantException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

public static class AssistantJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    public static string ProfileDigest(StudioProfile profile)
    {
        JsonNode Sort(JsonNode? value) => value switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
                KeyValuePair.Create<string, JsonNode?>(entry.Key, entry.Value is null ? null : Sort(entry.Value))).ToArray()),
            JsonArray array => new JsonArray(array.Select(item => item is null ? null : Sort(item)).ToArray()),
            _ => value?.DeepClone() ?? JsonValue.Create(0)!
        };
        // StickyControls is a set; normalize it before canonical property ordering.
        var node = JsonSerializer.SerializeToNode(profile with { ScriptsEnabled = false }, Options)!.AsObject();
        node["stickyControls"] = JsonSerializer.SerializeToNode(profile.StickyControls.OrderBy(control => (uint)control), Options);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Sort(node), Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    public static T Read<T>(JsonElement element) => element.Deserialize<T>(Options)
        ?? throw new AssistantException("invalidArguments", "The request requires structured arguments.");
}
