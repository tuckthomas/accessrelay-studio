using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Nodes;
using EliteScriptStudio.Core;

internal static class LuaVerification
{
    internal static void Run()
    {
        TimedOutputs();
        LiveInputsAndState();
        CancellationAndTakeover();
        InvalidPrograms();
        Persistence();
        Benchmark();
    }

    private static (MacroEngine Engine, StudioProfile Profile, LuaControllerScript Script) Setup(string source)
    {
        var script = new LuaControllerScript(Guid.NewGuid(), "Verification", source);
        var profile = (StudioProfile.CreateDefault() with { ScriptsEnabled = true }).WithLuaScript(script)
            .WithBinding(ControlId.LargeRight, new(ScriptKind.Lua, LuaScriptId: script.Id));
        var engine = new MacroEngine();
        engine.Apply(profile);
        engine.Tick(default, PaddleButtons.None, true, 0);
        return (engine, profile, script);
    }

    private static void TimedOutputs()
    {
        var (engine, _, _) = Setup("""
            output.button("A", true)
            output.axis("RightY", -100)
            output.axis("LeftTrigger", 50)
            wait(10)
            output.button("A", false)
            output.release("RightY")
            wait(10)
            """);
        var physical = new ControllerState((ushort)ControlId.B, 8, 31, 10000, -12000, 4321, 12345);
        var first = engine.Tick(physical, PaddleButtons.LargeRight, true, 1);
        Equal((ushort)((ushort)ControlId.A | (ushort)ControlId.B), first.Buttons);
        Equal((short)-32767, first.RightY);
        Equal((byte)128, first.LeftTrigger);
        Equal(physical.LeftX, first.LeftX);
        Equal(physical.RightX, first.RightX);
        Equal(first, engine.Tick(physical, PaddleButtons.None, true, 10));
        var second = engine.Tick(physical, PaddleButtons.None, true, 11);
        Equal(physical.Buttons, second.Buttons);
        Equal(physical.RightY, second.RightY);
        Equal((byte)128, second.LeftTrigger);
        Equal(physical, engine.Tick(physical, PaddleButtons.None, true, 21));
        Check(!engine.Status.IsActive && engine.Status.Error is null, "Completion releases channels.");
    }

    private static void LiveInputsAndState()
    {
        var (engine, _, _) = Setup("""
            state.set("count", state.get("count") + 1)
            local offsets = { 1, 2 }
            local sum = 0
            for _, value in ipairs(offsets) do sum = sum + value end
            local function movement()
                return math.min(100, state.get("count") * 10 + sum)
            end
            output.axis("RightX", movement())
            while input.down(input.trigger()) do
                output.axis("RightY", input.axis("LeftY"))
                output.button("X", input.down("B"))
                wait(0)
            end
            """);
        var physical = new ControllerState((ushort)ControlId.B, 0, 0, 0, 15000, 0, 0);
        var output = engine.Tick(physical, PaddleButtons.LargeRight, true, 1);
        Equal((short)Math.Round(32767 * .13), output.RightX);
        Equal(physical.LeftY, output.RightY);
        Check((output.Buttons & (ushort)ControlId.X) != 0, "Reads buttons.");
        physical = physical with { Buttons = 0, LeftY = -20000 };
        output = engine.Tick(physical, PaddleButtons.LargeRight, true, 2);
        Equal(physical.LeftY, output.RightY);
        Equal((ushort)0, output.Buttons);
        Equal(physical, engine.Tick(physical, PaddleButtons.None, true, 3));
        output = engine.Tick(physical, PaddleButtons.LargeRight, true, 4);
        Equal((short)Math.Round(32767 * .23), output.RightX);
        engine.Tick(default, PaddleButtons.None, false, 5);
        engine.Tick(default, PaddleButtons.None, true, 6);
        output = engine.Tick(default, PaddleButtons.LargeRight, true, 7);
        Equal((short)Math.Round(32767 * .13), output.RightX);
    }

    private static void CancellationAndTakeover()
    {
        var (engine, profile, _) = Setup("output.axis('RightY', 100) wait(10000)");
        engine.Tick(default, PaddleButtons.LargeRight, true, 1);
        Check(engine.Status.IsActive, "Lua starts.");
        var takeover = engine.Tick(default, PaddleButtons.SmallLeft, true, 2);
        Equal(ScriptKind.Stepback, engine.Status.Script);
        Check(takeover.RightY < 0, "Built-in sequence takes over.");
        engine.Apply(profile);
        engine.Tick(default, PaddleButtons.None, true, 3);
        engine.Tick(default, PaddleButtons.LargeRight, true, 4);
        engine.Stop();
        Equal(default(ControllerState), engine.Tick(default, PaddleButtons.None, true, 5));
        engine.Apply(profile);
        engine.Tick(default, PaddleButtons.None, true, 6);
        engine.Tick(default, PaddleButtons.LargeRight, true, 7);
        Equal(default(ControllerState), engine.Tick(default, PaddleButtons.None, false, 8));
        engine.Tick(default, PaddleButtons.None, true, 9);
        Equal(default(ControllerState), engine.Tick(default, PaddleButtons.None, true, 10));
        engine.Tick(default, PaddleButtons.LargeRight, true, 11);
        engine.Apply(profile);
        Equal(default(ControllerState), engine.Tick(default, PaddleButtons.None, true, 12));

        var (repeatEngine, _, _) = Setup("while true do wait(1000) end");
        repeatEngine.Tick(default, PaddleButtons.LargeRight, true, 1);
        repeatEngine.Tick(default, PaddleButtons.None, true, 60001);
        Check(repeatEngine.Status.Error?.Contains("60 seconds") == true, "Long-running scripts stop.");
    }

    private static void InvalidPrograms()
    {
        foreach (string source in new[]
        {
            "output.axis('RightY', 80) while true do end",
            "output.axis('RightY', 80) output.axis('RightY', 101)",
            "output.axis('RightY', 80) wait(-1)",
            "output.button('A', 1)",
            "output.axis('Missing', 0)",
            "output.axis('RightY', 0/0)",
            "io.open('blocked.txt', 'w')",
            "os.execute('blocked')",
            "require('blocked')",
            "load('while true do end')()",
            "coroutine.create(function() end)",
            "collectgarbage()"
        })
        {
            var (engine, _, _) = Setup(source);
            Equal(default(ControllerState), engine.Tick(default, PaddleButtons.LargeRight, true, 1));
            Check(engine.Status.Error is not null && !engine.Status.IsActive, "Invalid scripts report an error and release outputs: " + source);
            engine.Tick(default, PaddleButtons.None, true, 2);
            Check(engine.Status.Error is not null, "Errors remain visible.");
            engine.SetEnabled(false);
            Check(engine.Status.Error is null, "Stop clears errors.");
        }
        bool rejected = false;
        try { Setup("if then"); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected, "Syntax errors are rejected without execution.");
    }

    private static void Persistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.Lua." + Guid.NewGuid().ToString("N"));
        try
        {
            var (_, profile, script) = Setup("output.button('A', true) wait(10)");
            var store = new ProfileStore(directory);
            store.Save(profile);
            string path = Path.Combine(directory, profile.Id.ToString("N") + ".json");
            Equal(3, JsonNode.Parse(File.ReadAllText(path))!["version"]!.GetValue<int>());
            var loaded = store.Load(profile.Id);
            Equal(script, loaded.LuaScripts[script.Id]);
            Equal(script.Id, loaded.GetBinding(ControlId.LargeRight).LuaScriptId!.Value);
            string export = Path.Combine(directory, "export.json");
            store.Export(loaded, export);
            var imported = store.Import(export);
            Equal(script, imported.LuaScripts[script.Id]);
            Check(imported.Id != loaded.Id, "Import uses a new profile ID.");
            var removed = loaded.WithoutLuaScript(script.Id);
            Equal(ScriptKind.Passthrough, removed.GetBinding(ControlId.LargeRight).Script);
            store.Save(removed);
            Equal(0, store.Load(profile.Id).LuaScripts.Count);
            bool rejected = false;
            try { (profile with { LuaScripts = ImmutableDictionary<Guid, LuaControllerScript>.Empty }).Validate(); }
            catch (System.IO.InvalidDataException) { rejected = true; }
            Check(rejected, "Missing bound script IDs are rejected.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private static void Benchmark()
    {
        var (engine, _, _) = Setup("while true do output.axis('RightY', input.axis('LeftY')) wait(0) end");
        var physical = new ControllerState(0, 0, 0, 0, 20000, 0, 0);
        engine.Tick(physical, PaddleButtons.LargeRight, true, 1);
        for (int i = 2; i < 2002; i++) engine.Tick(physical, PaddleButtons.LargeRight, true, i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 2002; i < 12002; i++) engine.Tick(physical, PaddleButtons.LargeRight, true, i);
        watch.Stop();
        Check(engine.Status.IsActive && engine.Status.Error is null, "Benchmark script stays active.");
        Console.WriteLine($"Lua CPU-only loop: {watch.Elapsed.TotalMilliseconds * 1000 / 10000:F2} us/tick, "
            + $"{(GC.GetAllocatedBytesForCurrentThread() - before) / 10000} allocated bytes/tick; excludes hardware, scheduling and output transport.");
    }

    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
