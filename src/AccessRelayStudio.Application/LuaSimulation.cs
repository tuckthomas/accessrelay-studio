using System.Collections.Immutable;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Application;

public sealed record SimulationEvent(int TimeMs, ControllerState Input, PaddleButtons Paddles = PaddleButtons.None, bool Connected = true);
public sealed record LuaSimulationRequest(string Source, ControlId Trigger = ControlId.A, int DurationMs = 1000, SimulationEvent[]? Events = null);
public sealed record SimulationPoint(int TimeMs, ControllerState Input, ControllerState Output, bool SequenceActive);
public sealed record LuaSimulationResult(bool Valid, string SourceSha256, bool Started, bool StopPreservesManual,
    bool DisconnectNeutral, bool SafetyCertified, SimulationPoint[] Trace, string? Error, bool TraceTruncated = false);

/// <summary>Call only inside the isolated rehearsal worker. This is the production engine, not a second Lua API.</summary>
public static class LuaSimulation
{
    public static LuaSimulationResult Run(LuaSimulationRequest request)
    {
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Source)));
        if (!Enum.IsDefined(request.Trigger) || request.DurationMs is < 32 or > 5000 || request.Source.Length is < 1 or > LuaControllerScript.MaxSourceLength)
            throw new AssistantException("simulationBounds", "Choose an actual control, 32–5000 ms and bounded Lua source.");
        var events = request.Events ?? DefaultEvents(request.Trigger);
        if (events.Length is < 1 or > 512 || events[0].TimeMs != 0 || events.Any(item => item.TimeMs < 0 || item.TimeMs > request.DurationMs)
            || events.Zip(events.Skip(1)).Any(pair => pair.First.TimeMs >= pair.Second.TimeMs))
            throw new AssistantException("eventBounds", "Supply 1–512 strictly ordered physical events beginning at zero and inside the simulation window.");
        try
        {
            var script = new LuaControllerScript(Guid.NewGuid(), "Assistant rehearsal", request.Source);
            var profile = StudioProfile.CreateDefault("Assistant rehearsal") with { ScriptsEnabled = true,
                Bindings = ImmutableDictionary<ControlId, ScriptBinding>.Empty.Add(request.Trigger, new(ScriptKind.Lua, LuaScriptId: script.Id)),
                LuaScripts = ImmutableDictionary<Guid, LuaControllerScript>.Empty.Add(script.Id, script) };
            var engine = new MacroEngine(); engine.Apply(profile);
            var trace = new List<SimulationPoint>(); int index = 0; bool started = false, truncated = false; ControllerState? previous = null; string? error = null;
            for (int now = 0; now <= request.DurationMs; now += 4)
            {
                while (index + 1 < events.Length && events[index + 1].TimeMs <= now) index++;
                var input = events[index]; var output = engine.Tick(input.Input, input.Paddles, input.Connected, now);
                started |= engine.Status.Script == ScriptKind.Lua;
                if (engine.Status.Error is { } failure) { error = failure[..Math.Min(512, failure.Length)]; break; }
                if (previous != output) { if (trace.Count < 512) trace.Add(new(now, input.Input, output, engine.Status.IsActive)); else truncated = true; }
                previous = output;
            }
            var manual = new ControllerState(0x2000, 0, 0, 1234, -2345, 0, 0);
            engine.Stop(); bool stopped = engine.Tick(manual, default, true, request.DurationMs + 4) == manual;
            bool disconnected = engine.Tick(manual, default, false, request.DurationMs + 8) == default;
            return new(error is null && stopped && disconnected, digest, started, stopped, disconnected, false, trace.ToArray(), error, truncated);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(false, digest, false, false, false, false, [], "The production Lua engine rejected the source: " + error.Message[..Math.Min(512, error.Message.Length)]); }
    }
    private static SimulationEvent[] DefaultEvents(ControlId trigger)
    {
        ControllerState input = default; PaddleButtons paddles = default;
        if ((uint)trigger < (uint)ControlId.LeftTrigger) input = input with { Buttons = (ushort)trigger };
        else if (trigger == ControlId.LeftTrigger) input = input with { LeftTrigger = 255 };
        else if (trigger == ControlId.RightTrigger) input = input with { RightTrigger = 255 };
        else paddles = (PaddleButtons)((uint)trigger >> 18);
        return [new(0, default), new(8, input, paddles), new(24, default)];
    }
}
