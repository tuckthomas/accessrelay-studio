using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace EliteScriptStudio.RemotePlay;

internal sealed record XboxIce(string Candidate, int SdpMLineIndex, string? SdpMid);
internal sealed class XboxHomeService(HttpClient http, Uri region, string bearer, Func<CancellationToken, Task<string>> transferToken)
{
    private const string DeviceInfo = """{"appInfo":{"env":{"clientAppId":"www.xbox.com","clientAppType":"browser","clientAppVersion":"21.1.98","clientSdkVersion":"8.5.3","httpEnvironment":"prod","sdkInstallId":""}},"dev":{"hw":{"make":"Microsoft","model":"unknown","sdktype":"web"},"os":{"name":"windows","platform":"desktop"},"displayInfo":{"dimensions":{"widthInPixels":1920,"heightInPixels":1080},"pixelDensity":{"dpiX":1,"dpiY":1}}}}""";

    internal async Task<IReadOnlyList<XboxConsole>> ConsolesAsync(CancellationToken ct)
    {
        List<XboxConsole> consoles = [];
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string path = "/v6/servers/home";
        while (true)
        {
            var page = await RequestAsync(HttpMethod.Get, path, null, ct);
            foreach (var item in page.GetProperty("results").EnumerateArray())
                consoles.Add(new(item.GetProperty("serverId").GetString()!, item.GetProperty("deviceName").GetString()!,
                    item.TryGetProperty("powerState", out var power) ? power.GetString() ?? "Unknown" : "Unknown"));
            if (!page.TryGetProperty("continuationToken", out var token) || string.IsNullOrEmpty(token.GetString())) return consoles;
            string cursor = token.GetString()!;
            if (!cursors.Add(cursor) || cursors.Count > 64) throw new InvalidDataException("Xbox returned inconsistent console pages.");
            path = "/v6/servers/home?continuationToken=" + Uri.EscapeDataString(cursor);
        }
    }

    internal async Task<XboxHomeSession> OpenAsync(string console, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Finish the creation request so a concurrent Stop can release the allocated session by ID.
        using var creation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var response = await RequestAsync(HttpMethod.Post, "/v5/sessions/home/play", new
        {
            clientSessionId = Guid.NewGuid().ToString(), titleId = "", serverId = console, systemUpdateGroup = "", fallbackRegionNames = Array.Empty<string>(),
            settings = new { nanoVersion = "V3;WebrtcTransport.dll", enableOptionalDataCollection = false, enableTextToSpeech = false, highContrast = 0,
                locale = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : "en-US",
                useIceConnection = false, timezoneOffsetMinutes = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes, sdkType = "web", osName = "windows" }
        }, creation.Token);
        string id = response.TryGetProperty("sessionId", out var field) ? field.GetString() ?? "" : "";
        if (id.Length == 0 && response.TryGetProperty("sessionPath", out var p)) id = p.GetString()?.TrimEnd('/').Split('/')[^1] ?? "";
        if (id.Length is 0 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Xbox returned an invalid home session ID.");
        var session = new XboxHomeSession(this, "/v5/sessions/home/" + id);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            bool connected = false;
            while (true)
            {
                var state = await RequestAsync(HttpMethod.Get, session.Path + "/state", null, timeout.Token);
                switch (state.GetProperty("state").GetString())
                {
                    case "Provisioned": return session;
                    case "ReadyToConnect" when !connected:
                        string ticket = await transferToken(timeout.Token);
                        await RequestAsync(HttpMethod.Post, session.Path + "/connect", new { userToken = ticket }, timeout.Token);
                        connected = true;
                        break;
                    case "New": case "Provisioning": case "WaitingForResources": case "ReadyToConnect": break;
                    default: throw new InvalidOperationException("Xbox could not provision the home session.");
                }
                await Task.Delay(500, timeout.Token);
            }
        }
        catch
        {
            try { await session.DisposeAsync(); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { /* Preserve the provisioning failure. */ }
            throw;
        }
    }

    internal async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await SendAsync(method, path, body, ct); }
            catch (HttpRequestException e) when (method == HttpMethod.Get && attempt < 2 &&
                (e.StatusCode is null or System.Net.HttpStatusCode.TooManyRequests || (int)e.StatusCode >= 500))
            { await Task.Delay(250 << attempt, ct); }
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(region, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Gssv-Client", "XboxComBrowser");
        request.Headers.Add("X-MS-Device-Info", DeviceInfo);
        request.Headers.Accept.Add(new("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body);
        else if (method == HttpMethod.Post) request.Content = new ByteArrayContent([]);
        return await XboxHomeAccount.ReadAsync(http, request, ct);
    }
}

internal sealed class XboxHomeSession(XboxHomeService service, string path) : IAsyncDisposable
{
    private int _closed;
    internal string Path { get; } = path;

    internal async Task<string> ExchangeSdpAsync(string offer, CancellationToken ct)
    {
        var v1 = new { minVersion = 1, maxVersion = 1 };
        var answer = await ExchangeAsync("sdp", new { messageType = "offer", requestId = "1", sdp = offer,
            configuration = new { chat = v1, control = new { minVersion = 1, maxVersion = 3 },
                input = new { minVersion = 1, maxVersion = 8 }, message = v1,
                chatConfiguration = new { bytesPerSample = 2, expectedClipDurationMs = 20, format = new { codec = "opus", container = "webm" }, numChannels = 1, sampleFrequencyHz = 24000 } } }, ct);
        return answer.GetProperty("sdp").GetString() ?? throw new InvalidDataException("Xbox returned an empty SDP answer.");
    }

    internal async Task<XboxIce[]> ExchangeIceAsync(IReadOnlyList<XboxIce> candidates, CancellationToken ct)
    {
        var result = await ExchangeAsync("ice", new { messageType = "iceCandidate", candidate = candidates }, ct);
        return result.EnumerateArray().Where(v => v.TryGetProperty("candidate", out var s) && s.ValueKind == JsonValueKind.String)
            .Select(v => new XboxIce(v.GetProperty("candidate").GetString()!, v.TryGetProperty("sdpMLineIndex", out var m) && int.TryParse(m.ToString(), out int index) ? index : 0,
                v.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null)).ToArray();
    }

    private async Task<JsonElement> ExchangeAsync(string name, object body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string endpoint = Path + "/" + name;
        await service.RequestAsync(HttpMethod.Post, endpoint, body, deadline.Token);
        while (true)
        {
            var json = await service.RequestAsync(HttpMethod.Get, endpoint, null, deadline.Token);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("exchangeResponse", out var response) && !string.IsNullOrEmpty(response.GetString()))
            {
                using var inner = JsonDocument.Parse(response.GetString()!);
                return inner.RootElement.Clone();
            }
            await Task.Delay(400, deadline.Token);
        }
    }

    internal Task KeepAliveAsync(CancellationToken ct) => service.RequestAsync(HttpMethod.Post, Path + "/keepalive", null, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await service.RequestAsync(HttpMethod.Delete, Path, null, timeout.Token); }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }
}
