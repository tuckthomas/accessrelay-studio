using System.Security.Cryptography;
using System.Text.Json;
using EliteScriptStudio.Core;

internal static class InstalledPluginVerification
{
    internal static void Run(string manifest, string evidence, string? profileFile = null)
    {
        evidence = Path.GetFullPath(evidence); Directory.CreateDirectory(evidence);
        string library = Path.Combine(evidence, "installed-" + Guid.NewGuid().ToString("N"));
        var loader = new InstalledGamePlugins(library, component => Path.Combine(evidence, component));
        loader.Reload(); Check(loader.Plugins.Count == 0, "An absent plugin exposed an implementation.");
        var imported = new GamePackLibrary(library).Import(Path.GetFullPath(manifest), CancellationToken.None);
        loader.Reload(); Check(loader.Errors.Length == 0, string.Join("\n", loader.Errors));
        Check(loader.Plugins.Count == 1, "An installed implementation did not load.");
        var plugin = loader.Plugins.Single();
        Check(plugin.Id == imported.Manifest.GameId, "Game identity changed.");
        Check(plugin.GetType().Assembly != typeof(IGamePluginFactory).Assembly, "Game code was compiled into the shared contract assembly.");
        Check(plugin.GetType().GetInterfaces().Contains(typeof(IVisionGamePlugin)), "The extension loaded a second copy of the shared contract.");
        Check(loader.ControllerAdapters.Count == (plugin is IGameControllerPlugin ? 1 : 0), "Controller extension registration changed.");
        string workflow = loader.WorkflowPath(plugin.Id);
        Check(File.Exists(workflow) && workflow.StartsWith(imported.DirectoryPath + Path.DirectorySeparatorChar), "Workflow did not resolve inside the installed package.");
        string prefix = "https://plugins.accessrelay.test/plugins/" + imported.Manifest.Id + "/" + imported.ManifestSha256 + "/";
        foreach (var asset in imported.Manifest.Assets.Where(a => a.Kind == GamePackAssetKind.Ui))
            Check(loader.ReadUi(new Uri(prefix + asset.Path)) is { } bytes && Convert.ToHexStringLower(SHA256.HashData(bytes)) == asset.Sha256, "UI resource did not match its declared hash.");
        Check(loader.ReadUi(new Uri(prefix + "../pack.json")) is null, "UI transport exposed undeclared resources.");
        var script = imported.Manifest.Assets.First(a => a.Kind == GamePackAssetKind.Ui);
        string scriptPath = imported.ResourcePath(script); byte[] original = File.ReadAllBytes(scriptPath);
        try
        {
            byte[] corrupt = original.ToArray(); corrupt[0] ^= 1; File.WriteAllBytes(scriptPath, corrupt);
            try { _ = loader.ReadUi(new Uri(prefix + script.Path)); throw new Exception("A changed UI resource was accepted."); }
            catch (InvalidDataException) { }
        }
        finally { File.WriteAllBytes(scriptPath, original); }
        string? fingerprint = null;
        if (profileFile is not null)
        {
            using var profile = JsonDocument.Parse(File.ReadAllBytes(profileFile));
            var document = profile.RootElement.TryGetProperty("profile", out var wrapped) ? wrapped : profile.RootElement;
            var saved = plugin.SaveProfile(document, ""); fingerprint = saved.ConfigurationFingerprint;
            var factory = (IGamePluginFactory)Activator.CreateInstance(plugin.GetType().Assembly.GetType(imported.Manifest.Runtime!.Factory)!)!;
            var restored = factory.Create(new(imported.DirectoryPath, Path.Combine(evidence, "Plugins", plugin.Id, "Profiles")));
            Check(restored.Profiles.Single(p => p.Id == saved.Id).ConfigurationFingerprint == fingerprint, "Existing profile fields/fingerprint did not survive the installed runtime round trip.");
        }
        loader.Reload(); Check(ReferenceEquals(plugin, loader.Plugins.Single()), "A library refresh replaced active plugin state.");
        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { success = true, imported.Manifest.Id, imported.Manifest.Version,
            imported.ManifestSha256, implementationAssembly = plugin.GetType().Assembly.GetName().Name,
            resourceCount = imported.Manifest.Assets.Length, profileFingerprint = fingerprint }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("SUCCESS installed extension loaded from its own assembly, shared the host contract and served only checked UI resources."
            + (fingerprint is null ? "" : " Existing profile fingerprint retained: " + fingerprint));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
