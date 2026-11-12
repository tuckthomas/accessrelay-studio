using System.Buffers.Binary;
using System.Text.Json;
using EliteScriptStudio.Application;
using EliteScriptStudio.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

static class AssistantIntegrationChecks
{
    internal static async Task Run(string[] args)
    {
        int bridgeIndex = Array.IndexOf(args, "--bridge");
        string bridge = bridgeIndex >= 0 ? Path.GetFullPath(args[bridgeIndex+1]) : Path.GetFullPath("src/AccessRelayStudio.Mcp/bin/Release/net10.0/AccessRelayStudio.Mcp.dll");
        string command = bridge.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : bridge;
        // dotnet run can use an apphost; prefer the known dotnet host for framework-dependent helpers.
        if (bridge.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) command = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? (OperatingSystem.IsWindows() ? "dotnet" : "/workspace/.dotnet/dotnet");
        var rehearsal = new IsolatedLuaRehearsal(command, command == bridge ? null : bridge);
        var held = await rehearsal.RunAsync(new("output.button(\"A\", true)\nwait(5000)", DurationMs: 32), default);
        Check(held.Valid && held.Started && !held.SafetyCertified && held.StopPreservesManual && held.DisconnectNeutral
            && held.Trace.Any(point => (point.Output.Buttons & 0x1000) != 0), "Actual-engine hold, stop and disconnect failed.");
        var failure = await rehearsal.RunAsync(new("while true do end"), default);
        Check(!failure.Valid && failure.Error is not null, "Unyielding Lua was accepted.");
        var nonAligned = await rehearsal.RunAsync(new("output.button(\"B\", true)\nwait(40)", Events: [new(0, default), new(3, new(0x1000,0,0,0,0,0,0)), new(19, default)]), default);
        Check(nonAligned.Started && nonAligned.Valid, "Non-tick-aligned physical events were lost.");
        using var access = new AssistantSession(); access.Configure(new(true, true));
        var host = new FakeContext(); var service = new StudioAssistantService(host, access) { Rehearsal = rehearsal };
        var draft = await service.CreateDraftAsync(new([ControlId.A], 20), default);
        Check(host.Profile.StickyControls.Count == 0, "Draft mutated profile.");
        await service.ApplyDraftFromStudioAsync(draft.Id, default);
        Check(host.Profile.StickyControls.Contains(ControlId.A) && !host.Profile.ScriptsEnabled, "Reviewed draft was not applied inactive.");
        await service.UndoFromStudioAsync(default); Check(host.Profile.StickyControls.Count == 0, "Undo did not restore original.");
        var stale = await service.CreateDraftAsync(new([ControlId.B]), default); host.Revision++;
        try { await service.ApplyDraftFromStudioAsync(stale.Id, default); throw new Exception("Stale draft applied"); }
        catch (AssistantException error) when (error.Code == "staleContext") { }
        access.Configure(new()); Check(service.Drafts().Length == 0, "Revoked drafts survived.");
        byte[] badHeader = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(badHeader, AssistantWire.MaximumBytes + 1);
        try { await AssistantWire.ReadAsync(new MemoryStream(badHeader), default); throw new Exception("Oversized frame accepted"); }
        catch (AssistantException error) when (error.Code == "messageTooLarge") { }
        string pipe = "AccessRelayTest." + Guid.NewGuid().ToString("N");
        await using var server = new AssistantPipeServer(service.DispatchAsync, pipe);
        var startArgs = command == bridge ? new List<string>() : new List<string> { bridge };
        startArgs.AddRange(["--pipe", pipe]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = command, Arguments = startArgs,
            Name = "AccessRelay verification", InheritEnvironmentVariables = true }), cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Check(tools.Any(tool => tool.Name == "draft_controls") && tools.All(tool => !tool.Name.Contains("apply") && !tool.Name.Contains("output_start")), "Wrong MCP capabilities.");
        var disabled = await client.CallToolAsync("read_studio_status", cancellationToken: timeout.Token);
        Check(disabled.IsError == true, "Disabled MCP access succeeded.");
        access.Configure(new(true, true));
        var state = await client.CallToolAsync("read_studio_status", cancellationToken: timeout.Token);
        Check(state.IsError != true && state.StructuredContent is not null, "SDK structured status failed: " + Describe(state));
        var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);
        Check(resources.Any(item => item.Uri == "accessrelay://docs/lua-contract"), "Lua resource missing.");
        var prompts = await client.ListPromptsAsync(cancellationToken: timeout.Token); Check(prompts.Count >= 5, "Coaching prompts missing.");
        var rpcDraft = await client.CallToolAsync("draft_controls", new Dictionary<string, object?> { ["request"] = new { stickyControls = new[] { "a" }, leftSmoothingMs = 10, rightSmoothingMs = 0 } }, cancellationToken: timeout.Token);
        Check(rpcDraft.IsError != true && service.Drafts().Length == 1, "SDK draft creation failed.");
        var legacyArgs = command == bridge ? new List<string>() : new List<string> { bridge };
        legacyArgs.AddRange(["--pipe", pipe]);
        await using var legacy = await McpClient.CreateAsync(new StdioClientTransport(new() { Command = command, Arguments = legacyArgs,
            Name = "AccessRelay legacy verification", InheritEnvironmentVariables = true }),
            new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: timeout.Token);
        var legacyStatus = await legacy.CallToolAsync("read_studio_status", cancellationToken: timeout.Token);
        Check(legacyStatus.IsError != true, "Legacy client tool-call failed: " + Describe(legacyStatus));
        string cancelPipe = "AccessRelayCancel." + Guid.NewGuid().ToString("N");
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var waitingServer = new AssistantPipeServer(async (_, cancellation) =>
        { try { await Task.Delay(Timeout.Infinite, cancellation); } catch (OperationCanceledException) { observedCancellation.TrySetResult(); throw; } return null; }, cancelPipe);
        using var abandon = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await AssistantWire.CallAsync("wait", new { }, abandon.Token, cancelPipe); } catch (OperationCanceledException) { }
        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine("PASS isolated actual-engine Lua, hold/stop/disconnect/budgets, draft review/undo/staleness, bounded framing, real SDK stdio tools/resources/prompts and current-user IPC.");
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Describe(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
