using System.Text.Json;
using EliteScriptStudio.Application;
using EliteScriptStudio.Core;

var clock = new TestClock();
using var access = new AssistantSession(clock);
var host = new FakeContext(); var service = new StudioAssistantService(host, access);
Reject(() => access.Require(), "accessDisabled");
access.Configure(new(true)); var grant = access.Require();
Reject(() => access.Require(drafts: true), "draftsDisabled");
var text = JsonSerializer.Serialize(await service.ReadStatusAsync(default), AssistantJson.Options);
Check(text.Contains("controllerDisconnected") && !text.Contains("luaScripts") && !text.Contains("C:\\"), "Bounded diagnostic projection leaked unrelated profile data.");
clock.Now += TimeSpan.FromMinutes(16); Reject(() => access.Require(), "accessDisabled");
access.Configure(new(true, true)); Reject(() => access.Check(grant), "accessRevoked");
host.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
var pending = service.ReadStatusAsync(default); access.Configure(new()); host.PendingRead.SetResult(host.State);
try { await pending; throw new Exception("Revoked read succeeded"); } catch (AssistantException error) { Check(error.Code == "accessRevoked", "Wrong read error"); }
Check(StudioAssistantService.LuaContract().Contains("output.button") && !StudioAssistantService.LuaContract().Contains("press_button"), "Wrong Lua contract.");
Console.WriteLine("PASS shared diagnostic projection, monotonic expiry, permission scopes, revocation and actual Lua contract.");

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action call, string code) { try { call(); } catch (AssistantException error) when (error.Code == code) { return; } throw new Exception("Expected " + code); }
sealed class TestClock : TimeProvider
{
    public TimeSpan Now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Now.Ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Now;
}
sealed class FakeContext : IStudioApplicationContext
{
    public StudioProfile Profile = StudioProfile.CreateDefault("Test");
    public TaskCompletionSource<StudioApplicationContext>? PendingRead;
    public StudioApplicationContext State => new(new(Guid.Empty, Profile.Id, 1, AssistantJson.ProfileDigest(Profile), 0), Profile,
        new(false, false, false, false, false, false, false, false, false, false, false, false, 5, 5, false, false, false));
    public Task<StudioApplicationContext> ReadAsync(CancellationToken cancellation) => PendingRead?.Task ?? Task.FromResult(State);
    public Task ApplyAsync(StudioContextToken expected, StudioProfile profile, CancellationToken cancellation) { Profile = profile; return Task.CompletedTask; }
}
