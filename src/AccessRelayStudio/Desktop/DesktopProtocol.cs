using System.Text.Json;
using System.Text.Json.Serialization;

namespace EliteScriptStudio.Desktop;

internal sealed record DesktopMedia(byte[] Bytes, string ContentType);

internal interface IDesktopModule : IDisposable
{
    object GetState();
    object GetStatus();
    Task<object?> InvokeAsync(string action, JsonElement args);
    void SetVisible(bool visible);
    DesktopMedia? ReadMedia(string path);
}

internal static class DesktopJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static T Read<T>(this JsonElement args) => args.Deserialize<T>(Options)
        ?? throw new InvalidDataException("The command is missing its settings.");
    internal static string Text(this JsonElement args, string key, string fallback = "") =>
        args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : fallback;
    internal static bool Flag(this JsonElement args, string key, bool fallback = false) =>
        args.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    internal static int Number(this JsonElement args, string key, int fallback = 0) =>
        args.TryGetProperty(key, out var value) && value.TryGetInt32(out int result) ? result : fallback;
    internal static Guid Id(this JsonElement args, string key = "id") =>
        Guid.TryParse(args.Text(key), out var result) && result != Guid.Empty ? result
            : throw new InvalidDataException("Choose a saved item first.");
    internal static T EnumValue<T>(this JsonElement args, string key) where T : struct, Enum =>
        Enum.TryParse<T>(args.Text(key), true, out var result) && Enum.IsDefined(result) ? result
            : throw new InvalidDataException($"Choose a supported {key}.");
}
