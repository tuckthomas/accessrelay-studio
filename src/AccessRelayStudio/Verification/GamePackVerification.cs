using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;

internal static class GamePackVerification
{
    internal static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "games", "switch-demo", "pack.json"))) root = root.Parent;
        if (root is null) throw new Exception("Cannot locate bundled game-pack fixtures.");
        var nba = GamePackDefinition.Read(Path.Combine(root.FullName, "games/nba2k27/pack.json"));
        var demo = GamePackDefinition.Read(Path.Combine(root.FullName, "games/switch-demo/pack.json"));
        if (nba.Manifest.VisionAdapter != "temporal-release-v1" || demo.Manifest.VisionAdapter != "none") throw new Exception("Typed vision adapter identities changed.");
        string directory = Path.Combine(Path.GetTempPath(), "AccessRelay.Packs." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var library = new GamePackLibrary(Path.Combine(directory, "library"));
            var installed = library.Import(Path.Combine(demo.DirectoryPath, "pack.json"), CancellationToken.None);
            Equal(demo.ManifestSha256, installed.ManifestSha256);
            Equal(1, library.LoadAll(out var errors).Count); Equal(0, errors.Length);
            Equal(installed.DirectoryPath, library.Import(Path.Combine(demo.DirectoryPath, "pack.json"), CancellationToken.None).DirectoryPath);
            var setup = installed.CreateProfile(new Dictionary<string, double> { ["press-ms"] = 40 });
            Equal(false, setup.ScriptsEnabled); Equal(1, setup.Bindings.Count); Equal("switch-demo", setup.GamePack!.Id);
            var store = new ProfileStore(Path.Combine(directory, "profiles")); store.Save(setup);
            Equal(5, JsonDocument.Parse(File.ReadAllBytes(Path.Combine(store.DirectoryPath, setup.Id.ToString("N") + ".json"))).RootElement.GetProperty("version").GetInt32());
            var restored = store.Load(setup.Id); Equal(40d, restored.GamePack!.Tuning["press-ms"]);
            var engine = new MacroEngine(); engine.Apply(restored); engine.SetEnabled(true); engine.Tick(default, PaddleButtons.None, true, 0);
            var pressed = default(ControllerState) with { Buttons = (ushort)ControlId.RightBumper };
            Equal((ushort)ControlId.A, engine.Tick(pressed, PaddleButtons.None, true, 10).Buttons);
            Equal((ushort)ControlId.A, engine.Tick(default, PaddleButtons.None, true, 49).Buttons);
            Equal((ushort)0, engine.Tick(default, PaddleButtons.None, true, 50).Buttons);
            var tuned = installed.Retune(restored, new Dictionary<string, double> { ["press-ms"] = 300 });
            Equal(restored.Id, tuned.Id); Equal(300d, tuned.LuaScripts.Values.Single().Tuning["press-ms"]);
            var edited = restored.WithLuaScript(restored.LuaScripts.Values.Single() with { Source = "wait(1)" });
            Expect<InvalidOperationException>(() => installed.Retune(edited, new Dictionary<string, double>()));
            Expect<InvalidDataException>(() => installed.Retune(StudioProfile.CreateDefault(), new Dictionary<string, double>()));
            Expect<InvalidDataException>(() => installed.CreateProfile(new Dictionary<string, double> { ["press-ms"] = 41 }));
            Expect<InvalidDataException>(() => installed.CreateProfile(new Dictionary<string, double> { ["unknown"] = 40 }));
            var script = installed.Manifest.Assets.Single();
            var candidates = new[]
            {
                demo.Manifest with { ApiVersion = 999 }, demo.Manifest with { SchemaVersion = 2 },
                demo.Manifest with { VisionAdapter = "custom-native-dll" }, demo.Manifest with { Id = "../escape" },
                demo.Manifest with { Assets = [script with { Path = "scripts/../escape.lua" }] },
                demo.Manifest with { Assets = [script with { Path = "scripts/code.dll" }] },
                demo.Manifest with { Assets = [script with { Path = "scripts/c:\\escape.lua" }] },
                demo.Manifest with { Assets = [script with { Path = "scripts/con.lua" }] },
                demo.Manifest with { Assets = [script with { Bytes = 1000000 }] },
                demo.Manifest with { Assets = [script, script] },
                demo.Manifest with { Bindings = [demo.Manifest.Bindings[0], demo.Manifest.Bindings[0]] },
                demo.Manifest with { Tuning = [demo.Manifest.Tuning[0] with { Default = double.NaN }] },
                demo.Manifest with { Presets = [new("Invalid", ImmutableDictionary<string, double>.Empty.Add("press-ms", 42))] }
            };
            foreach (var candidate in candidates) Expect<InvalidDataException>(candidate.Validate);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Expect<OperationCanceledException>(() => library.Import(Path.Combine(nba.DirectoryPath, "pack.json"), canceled.Token));
            Equal(false, Directory.EnumerateDirectories(Path.Combine(directory, "library"), ".import-*").Any());
            string modifiedRoot = Path.Combine(directory, "modified"); Directory.CreateDirectory(modifiedRoot); demo.CopyTo(modifiedRoot, CancellationToken.None);
            File.AppendAllText(Path.Combine(modifiedRoot, script.Path), "\n-- changed");
            Expect<InvalidDataException>(() => GamePackDefinition.Read(Path.Combine(modifiedRoot, "pack.json")));
            string mutable = Path.Combine(directory, "mutable"); Directory.CreateDirectory(mutable); demo.CopyTo(mutable, CancellationToken.None);
            var changed = demo.Manifest with { Description = "Same identity, changed metadata." };
            File.WriteAllText(Path.Combine(mutable, "pack.json"), JsonSerializer.Serialize(changed, GamePackDefinition.Json));
            Expect<InvalidDataException>(() => library.Import(Path.Combine(mutable, "pack.json"), CancellationToken.None));
            string linked = Path.Combine(directory, "linked"); Directory.CreateDirectory(linked); demo.CopyTo(linked, CancellationToken.None);
            string linkedScript = Path.Combine(linked, script.Path); File.Delete(linkedScript);
            try { File.CreateSymbolicLink(linkedScript, demo.ResourcePath(script)); Expect<InvalidDataException>(() => GamePackDefinition.Read(Path.Combine(linked, "pack.json"))); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP OS symlink creation is not permitted on this Windows runner; traversal/resource tests still run."); }
            // Model metadata is integrity-bound and explicitly typed, never an opaque dynamic tensor dictionary.
            string modeled = Path.Combine(directory, "modeled"); Directory.CreateDirectory(Path.Combine(modeled, "models"));
            byte[] raw = [1, 2, 3]; File.WriteAllBytes(Path.Combine(modeled, "models/test.onnx"), raw);
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, modelFile = "test.onnx", modelSha256 = Convert.ToHexStringLower(SHA256.HashData(raw)), outputContract = "unsupported" });
            File.WriteAllBytes(Path.Combine(modeled, "models/manifest.json"), metadata);
            var modeledManifest = demo.Manifest with { VisionAdapter = "temporal-release-v1", Bindings = [], Tuning = [], Presets = [], Assets = [
                new("models/test.onnx", GamePackAssetKind.Model, raw.Length, Convert.ToHexStringLower(SHA256.HashData(raw))),
                new("models/manifest.json", GamePackAssetKind.VisionManifest, metadata.Length, Convert.ToHexStringLower(SHA256.HashData(metadata)))] };
            File.WriteAllText(Path.Combine(modeled, "pack.json"), JsonSerializer.Serialize(modeledManifest, GamePackDefinition.Json));
            Expect<InvalidDataException>(() => GamePackDefinition.Read(Path.Combine(modeled, "pack.json")));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
