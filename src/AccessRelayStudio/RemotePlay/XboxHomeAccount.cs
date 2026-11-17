using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EliteScriptStudio.RemotePlay;

internal interface IXboxRefreshStore
{
    string? Read();
    void Save(string value);
    void Clear();
}

internal sealed record XboxDevicePrompt(string Code, Uri VerificationUri, DateTimeOffset Expires);
internal sealed record XboxConsole(string Id, string Name, string PowerState);

// Implements the interactive xHome path used by Greenlight/xal-node 1.1.6-beta2.
internal sealed class XboxHomeAccount(HttpClient http, IXboxRefreshStore store)
{
    private const string Login = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private const string ClientId = "1f907974-e22b-4810-a9de-d9647380c97e";
    private const string Scope = "xboxlive.signin openid profile offline_access";
    private string? _access, _streamToken;
    private DateTimeOffset _accessExpires, _streamExpires;
    private Uri? _region;

    internal bool HasAccount => store.Read() is not null;
    internal void SignOut()
    {
        store.Clear();
        _access = _streamToken = null;
        _region = null;
    }

    internal async Task SignInAsync(Action<XboxDevicePrompt?> show, CancellationToken ct)
    {
        var device = await FormAsync(Login + "devicecode", new() { ["client_id"] = ClientId, ["scope"] = Scope }, ct);
        int expires = Math.Clamp(device.GetProperty("expires_in").GetInt32(), 1, 1800);
        var url = new Uri(device.GetProperty("verification_uri").GetString()!);
        if (url.Scheme != Uri.UriSchemeHttps || !(url.Host == "microsoft.com" || url.Host.EndsWith(".microsoft.com", StringComparison.Ordinal)
            || url.Host == "login.microsoftonline.com")) throw new InvalidDataException("Microsoft returned an unexpected sign-in page.");
        string code = device.GetProperty("device_code").GetString()!;
        int interval = Math.Clamp(device.GetProperty("interval").GetInt32(), 1, 60);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(expires));
        try
        {
            show(new(device.GetProperty("user_code").GetString()!, url, DateTimeOffset.UtcNow.AddSeconds(expires)));
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), deadline.Token);
                try
                {
                    var token = await FormAsync(Login + "token", new() { ["client_id"] = ClientId,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = code }, deadline.Token);
                    ct.ThrowIfCancellationRequested();
                    AcceptToken(token, requireRefresh: true);
                    _region = null; _streamToken = null;
                    return;
                }
                catch (XboxHomeHttpException e) when (e.Code == "authorization_pending") { }
                catch (XboxHomeHttpException e) when (e.Code == "slow_down") { interval = Math.Min(90, interval + 5); }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The Microsoft sign-in code expired."); }
        finally { show(null); }
    }

    internal async Task<XboxHomeService> ServiceAsync(CancellationToken ct)
    {
        if (_region is not null && _streamToken is not null && _streamExpires > DateTimeOffset.UtcNow.AddMinutes(1))
            return new(http, _region, _streamToken, TransferTokenAsync);
        if (_access is null || _accessExpires <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            try
            {
                var token = await FormAsync(Login + "token", new() { ["client_id"] = ClientId, ["grant_type"] = "refresh_token",
                    ["refresh_token"] = Refresh(), ["scope"] = Scope }, ct);
                ct.ThrowIfCancellationRequested();
                AcceptToken(token, requireRefresh: false);
            }
            catch (XboxHomeHttpException e) when (e.Code == "invalid_grant")
            { SignOut(); throw new InvalidOperationException("Your Microsoft sign-in expired. Sign in again."); }
        }
        var user = await XboxPost("https://user.auth.xboxlive.com/user/authenticate", new { Properties = new {
            AuthMethod = "RPS", RpsTicket = "d=" + _access, SiteName = "user.auth.xboxlive.com" }, RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT" }, ct);
        var xsts = await XboxPost("https://xsts.auth.xboxlive.com/xsts/authorize", new { Properties = new {
            SandboxId = "RETAIL", UserTokens = new[] { user.GetProperty("Token").GetString() } }, RelyingParty = "http://gssv.xboxlive.com/", TokenType = "JWT" }, ct);
        using var login = new HttpRequestMessage(HttpMethod.Post, "https://xhome.gssv-play-prod.xboxlive.com/v2/login/user");
        login.Headers.Add("X-Gssv-Client", "XboxComBrowser");
        login.Content = JsonContent.Create(new { token = xsts.GetProperty("Token").GetString(), offeringId = "xhome" });
        var stream = await ReadAsync(http, login, ct);
        var region = stream.GetProperty("offeringSettings").GetProperty("regions").EnumerateArray()
            .FirstOrDefault(r => r.TryGetProperty("isDefault", out var flag) && flag.ValueKind == JsonValueKind.True);
        if (region.ValueKind != JsonValueKind.Object || !Uri.TryCreate(region.GetProperty("baseUri").GetString(), UriKind.Absolute, out var address)
            || address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo.Length != 0 || !address.Host.EndsWith(".xboxlive.com", StringComparison.Ordinal))
            throw new InvalidDataException("Xbox did not return a default home streaming region.");
        ct.ThrowIfCancellationRequested();
        _region = new Uri(address.GetLeftPart(UriPartial.Authority));
        _streamToken = stream.GetProperty("gsToken").GetString() ?? throw new InvalidDataException("Xbox did not return a home token.");
        _streamExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(stream.GetProperty("durationInSeconds").GetInt32(), 0, 86400));
        return new(http, _region, _streamToken, TransferTokenAsync);
    }

    private async Task<string> TransferTokenAsync(CancellationToken ct)
    {
        var result = await FormAsync("https://login.live.com/oauth20_token.srf", new() { ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token", ["refresh_token"] = Refresh(),
            ["scope"] = "service::http://Passport.NET/purpose::PURPOSE_XBOX_CLOUD_CONSOLE_TRANSFER_TOKEN" }, ct);
        return result.GetProperty("access_token").GetString()!;
    }

    private string Refresh() => store.Read() ?? throw new InvalidOperationException("Sign in to your Microsoft account first.");
    private void AcceptToken(JsonElement token, bool requireRefresh)
    {
        string? access = token.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidDataException("Microsoft returned an empty access token.");
        var expires = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(token.GetProperty("expires_in").GetInt32(), 1, 86400));
        if (token.TryGetProperty("refresh_token", out var refresh) && !string.IsNullOrEmpty(refresh.GetString())) store.Save(refresh.GetString()!);
        else if (requireRefresh) throw new InvalidDataException("Microsoft did not return a refresh token.");
        _access = access; _accessExpires = expires;
    }

    private async Task<JsonElement> FormAsync(string url, Dictionary<string,string> values, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(values) };
        return await ReadAsync(http, request, ct);
    }
    private async Task<JsonElement> XboxPost(string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: new JsonSerializerOptions()) };
        request.Headers.Add("x-xbl-contract-version", "1");
        return await ReadAsync(http, request, ct);
    }

    internal static async Task<JsonElement> ReadAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Xbox returned an oversized response.");
        await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string code = "";
            try { using var error = JsonDocument.Parse(bytes); if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out var field) && field.ValueKind == JsonValueKind.String) code = field.GetString() ?? ""; }
            catch (JsonException) { }
            throw new XboxHomeHttpException(response.StatusCode, code);
        }
        if (bytes.Length == 0) return default;
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.Clone();
    }
}

internal sealed class XboxHomeHttpException(HttpStatusCode status, string code)
    : HttpRequestException(status == HttpStatusCode.Conflict ? "Xbox reports another Remote Play session is active."
        : $"Xbox request failed (HTTP {(int)status}).", null, status)
{
    internal string Code { get; } = code;
}
