using EliteScriptStudio.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args is ["--lua-worker"])
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var request = AssistantJson.Read<LuaSimulationRequest>(await AssistantWire.ReadAsync(Console.OpenStandardInput(), limit.Token));
    var result = LuaSimulation.Run(request);
    await AssistantWire.WriteAsync(Console.OpenStandardOutput(), result, limit.Token);
    return;
}
if (args is ["--pipe", var pipe]) Bridge.Pipe = pipe;
else if (args.Length != 0) { Console.Error.WriteLine("Usage: AccessRelayStudio.Mcp [--pipe app-pipe-name]"); return; }
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<StudioTools>(AssistantJson.Options)
    .WithResources<StudioResources>().WithPrompts<StudioPrompts>();
await builder.Build().RunAsync();
