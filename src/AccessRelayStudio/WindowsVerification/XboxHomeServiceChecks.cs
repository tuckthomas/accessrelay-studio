using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EliteScriptStudio.RemotePlay;

internal static class XboxHomeServiceChecks
{
    private sealed class Store : IXboxRefreshStore
    {
        private string? _token;
        public string? Read() => _token;
        public void Save(string token) => _token = token;
        public void Clear() => _token = null;
    }
    private sealed class ServiceHandler(Func<HttpRequestMessage, string, HttpResponseMessage> serve) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => serve(request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
    }
    private static HttpResponseMessage Reply(object? body = null, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = body is null ? new ByteArrayContent([]) : JsonContent.Create(body) };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run()
    {
        CheckIceRoutes();
        var store = new Store();
        int polls = 0, lists = 0, states = 0, tickets = 0, offers = 0, icePosts = 0, releases = 0, keepalives = 0;
        bool failProvision = false, conflict = false, revoke = false, unauthorizedPage = false, empty = false, missingConfiguration = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancelledDuringCreation = new CancellationTokenSource();
        bool cancelCreation = false;
        using var http = new HttpClient(new ServiceHandler((request, body) =>
        {
            string host = request.RequestUri!.Host, path = request.RequestUri.AbsolutePath;
            string method = request.Method.Method;
            if (host != "test.xboxlive.com") Check(request.Headers.Authorization is null, "Regional bearer left its service.");
            if (path.EndsWith("/devicecode")) return Reply(new { device_code = "opaque", user_code = "DISPLAY", interval = 1, expires_in = 30,
                verification_uri = unauthorizedPage ? "https://example.com" : "https://microsoft.com/link" });
            if (path.EndsWith("/token"))
            {
                if (body.Contains("refresh_token="))
                    return revoke ? Reply(new { error = "invalid_grant" }, HttpStatusCode.BadRequest)
                        : Reply(new { access_token = "access", expires_in = 3600, refresh_token = "rotated" });
                Check(body.Contains("device_code=opaque"), "Missing device grant.");
                return ++polls == 1 ? Reply(new { error = "authorization_pending" }, HttpStatusCode.BadRequest)
                    : Reply(new { access_token = "access", refresh_token = "refresh", expires_in = 3600 });
            }
            if (host == "user.auth.xboxlive.com")
            {
                using var data = JsonDocument.Parse(body);
                Check(data.RootElement.GetProperty("Properties").GetProperty("RpsTicket").GetString() == "d=access", "Xbox user grant casing or ticket changed.");
                return Reply(new Dictionary<string,string> { ["Token"] = "user" });
            }
            if (host == "xsts.auth.xboxlive.com") return Reply(new Dictionary<string,string> { ["Token"] = "gssv" });
            if (path == "/v2/login/user") return Reply(new { gsToken = "regional", durationInSeconds = 3600,
                offeringSettings = new { regions = new[] { new { isDefault = true, baseUri = "https://test.xboxlive.com" } } } });
            if (host == "login.live.com") { tickets++; return Reply(new { access_token = "transfer" }); }
            Check(host == "test.xboxlive.com" && request.Headers.Authorization?.Parameter == "regional", "Wrong regional route or authorization.");
            if (path == "/v6/servers/home")
            {
                if (++lists == 1) return Reply(null, HttpStatusCode.ServiceUnavailable);
                if (empty || request.RequestUri.Query.Length != 0) return Reply(new { results = Array.Empty<object>() });
                return Reply(new { results = new[] { new { serverId = "home", deviceName = "My Xbox", powerState = "On" } }, continuationToken = "next" });
            }
            if (path.EndsWith("/play"))
            {
                Check(body.Contains("\"serverId\":\"home\""), "Missing selected console.");
                if (cancelCreation) cancelledDuringCreation.Cancel();
                return conflict ? Reply(null, HttpStatusCode.Conflict) : Reply(new { sessionPath = "/v5/sessions/home/abc" });
            }
            Check(path.StartsWith("/v5/sessions/home/abc"), "Unexpected session path.");
            if (path.EndsWith("/state")) return Reply(new { state = failProvision ? "Error" : ++states == 1 ? "ReadyToConnect" : "Provisioned" });
            if (path.EndsWith("/connect")) { Check(body.Contains("transfer"), "Missing console transfer token."); return Reply(); }
            if (path.EndsWith("/sdp"))
            {
                if (method == "POST") { offers++; Check(body.Contains("offer-sdp"), "Offer not submitted."); return Reply(); }
                return Reply(new { exchangeResponse = """{"sdp":"answer-sdp"}""" });
            }
            if (path.EndsWith("/ice"))
            {
                if (method == "POST") { icePosts++; Check(body.Contains("\"sdpMLineIndex\":0"), "Missing ICE media index."); return Reply(); }
                return Reply(new { exchangeResponse = """[{"candidate":"a=candidate:1 1 UDP 100 2001:db8::1 9002 typ host","sdpMLineIndex":"0","sdpMid":0},{"candidate":"a=end-of-candidates","sdpMLineIndex":0}]""" });
            }
            if (path.EndsWith("/configuration")) return missingConfiguration ? Reply(null, HttpStatusCode.NotFound)
                : Reply(new { serverDetails = new { ipV4Address = "192.0.2.5", ipV4Port = 9002 } });
            if (path.EndsWith("/keepalive")) { keepalives++; return Reply(); }
            if (method == "DELETE") { releases++; return Reply(); }
            throw new InvalidOperationException("Unexpected fixture request.");
        }));
        var account = new XboxHomeAccount(http, store);
        List<string?> prompts = [];
        await account.SignInAsync(p => prompts.Add(p?.Code), stop.Token);
        Check(prompts.SequenceEqual(["DISPLAY", null]) && account.HasAccount && polls == 2, "Device sign-in did not finish.");
        var service = await new XboxHomeAccount(http, store).ServiceAsync(stop.Token);
        Check(store.Read() == "rotated", "Refresh token did not rotate.");
        var consoles = await service.ConsolesAsync(stop.Token);
        Check(lists == 3 && consoles.Single() is { Name: "My Xbox", PowerState: "On" }, "Discovery retry/pagination failed.");
        empty = true;
        Check((await service.ConsolesAsync(stop.Token)).Count == 0, "Empty console list failed.");
        var session = await service.OpenAsync("home", stop.Token);
        await using (session)
        {
            Check(await session.ExchangeSdpAsync("offer-sdp", stop.Token) == "answer-sdp", "SDP exchange failed.");
            var routes = await session.ExchangeIceAsync([new("candidate:local", 0, "audio0")], stop.Token);
            Check(routes.Length == 3 && routes[0].SdpMid == "0" && routes[1].Candidate.Contains("192.0.2.5 9002")
                && routes[2].Candidate == "", "Configured console address or end-of-candidates was lost.");
            missingConfiguration = true;
            var nativeRoutes = await session.ExchangeIceAsync([new("candidate:local", 0, "audio0")], stop.Token);
            Check(nativeRoutes.Length == 2 && nativeRoutes[0] == routes[0], "Missing optional configuration blocked the native ICE path.");
            await session.KeepAliveAsync(stop.Token);
        }
        await session.DisposeAsync();
        Check((tickets, offers, icePosts, keepalives, releases) == (1, 1, 2, 1, 1), "Session messages or idempotent close failed.");
        failProvision = true;
        try { await service.OpenAsync("home", stop.Token); throw new Exception("Failed provisioning was accepted."); }
        catch (InvalidOperationException) { }
        cancelCreation = true;
        try { await service.OpenAsync("home", cancelledDuringCreation.Token); throw new Exception("Late allocation survived Stop."); }
        catch (OperationCanceledException) { }
        Check(releases == 3, "A failed or cancelled allocation was leaked.");
        conflict = true;
        try { await service.OpenAsync("home", stop.Token); throw new Exception("Session conflict was accepted."); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { }
        Check(releases == 3, "A conflict attempted to release another user's session.");
        revoke = true;
        try { await new XboxHomeAccount(http, store).ServiceAsync(stop.Token); throw new Exception("Revoked sign-in was accepted."); }
        catch (InvalidOperationException) { }
        Check(!account.HasAccount, "Revoked sign-in was retained.");
        unauthorizedPage = true;
        try { await account.SignInAsync(_ => throw new Exception("Foreign browser page opened."), stop.Token); throw new Exception("Foreign sign-in URL accepted."); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS home sign-in, refresh, console discovery, session exchange, keepalive, failure and cancellation fixtures.");
    }

    private static void CheckIceRoutes()
    {
        XboxIce[] remote = [new("a=end-of-candidates", 0, "audio0"),
            new("a=candidate:1 1 UDP 100 2001:0:c000:201::3caf:3fff:fdf5 9002 typ host", 0, "audio0"),
            new("candidate:2 1 UDP 100 192.0.2.10 9002 typ host", 0, "audio0"),
            new("candidate:3 1 UDP 100 2001:db8::11 9002 typ host", 0, "audio0")];
        var routes = XboxIceCandidates.ForPeer(remote, []);
        Check(routes.Length == 5 && routes.Count(r => r.Candidate.Contains(" 192.0.2.10 9002 ")) == 1
            && routes[3].Candidate.Contains(" 192.0.2.10 50000 ") && routes[^1].Candidate == "",
            "Teredo fallback, native IPv6, deduplication or completion ordering failed.");
        var configuration = JsonSerializer.SerializeToElement(new { serverDetails = new { ipAddress = "192.0.2.5", port = 53000,
            ipV4Address = "192.0.2.5", ipV4Port = 9002, ipV6Address = "::", ipV6Port = 1 } });
        var endpoints = XboxIceCandidates.ConsoleEndpoints(configuration);
        Check(endpoints.Count == 2 && endpoints.Any(e => e.Port == 53000) && endpoints.Any(e => e.Port == 9002)
            && XboxIceCandidates.ConsoleEndpoints(default).Count == 0, "Console endpoint selection failed.");
    }
}
