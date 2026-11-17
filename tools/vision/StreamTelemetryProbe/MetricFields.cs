using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Discovery only. Names and values retain their original units; nothing here supplies
// a timing correction. String payloads, identifiers and addresses are never exported.
internal static class MetricFields
{
    private static readonly Regex Metric = new(@"rtt|round.?trip|jitter|latency|decode|fps|frame|loss|lost|bitrate|bandwidth|delay|buffer|received|dropped|render|present|duration", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Private = new(@"token|secret|password|auth|address|endpoint|account|(?:user|session|connection|correlation)(?:id|key|name)|(?:id|handle|pointer|ptr|hash)$|xuid|ssrc|timestamp", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static Dictionary<string, double> Read(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in values.Take(128))
        {
            if (Private.IsMatch(name)) continue;
            if (value is string text && text.Length <= 65536 && text.TrimStart().StartsWith('{'))
            {
                try { using var json = JsonDocument.Parse(text, new() { MaxDepth = 8 }); Walk(name, json.RootElement, 0); }
                catch (JsonException) { }
            }
            else if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            { Add(name, Convert.ToDouble(value, CultureInfo.InvariantCulture)); }
        }
        return result;

        void Add(string name, double value)
        {
            if (result.Count < 128 && name.Length <= 256 && !Private.IsMatch(name) && Metric.IsMatch(name) && double.IsFinite(value))
                result[name] = value;
        }
        void Walk(string path, JsonElement element, int depth)
        {
            if (depth > 6 || result.Count >= 128 || Private.IsMatch(path)) return;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number)) Add(path, number);
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject().Take(128)) Walk(path + "." + property.Name, property.Value, depth + 1);
        }
    }

    internal static void Verify()
    {
        var fields = Read(new Dictionary<string, object?> {
            ["currRtt"] = 12.5, ["decodeTime"] = 2u, ["SessionId"] = 123, ["serverAddress"] = "private",
            ["message"] = "{\"video\":{\"framesDecoded\":60,\"totalDecodeTime\":0.12},\"jitterMs\":1.5,\"sessionId\":999,\"token\":\"private\",\"buffer\":{\"userId\":555}}",
            ["connectionStats"] = "{\"rtt\":12,\"connectionId\":888}",
            ["malformed"] = "{bad", ["fps"] = "60", ["decodeNaN"] = double.NaN,
        });
        if (fields.Count != 6 || fields["currRtt"] != 12.5 || fields["connectionStats.rtt"] != 12 || fields["message.video.framesDecoded"] != 60
            || fields.Keys.Any(k => k.Contains("session", StringComparison.OrdinalIgnoreCase) || k.Contains("user", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Numeric discovery filtering failed.");
        Console.WriteLine("PASS Numeric metric discovery, nested JSON, original values, malformed data, identifier/string filtering and nonfinite rejection.");
    }
}
