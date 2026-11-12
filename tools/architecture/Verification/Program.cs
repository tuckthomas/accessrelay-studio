using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length is < 2 or > 4)
    throw new ArgumentException("Supply Engine.dll Runtime.dll [AccessRelayStudio.dll Application.dll] from the same build or publish folder.");

var engine = Read(args[0], "AccessRelayStudio.Engine");
var runtime = Read(args[1], "AccessRelayStudio.Runtime");
Require(engine.References.All(name => name.StartsWith("System.", StringComparison.Ordinal)
    || name is "System" or "netstandard" or "mscorlib" or "Microsoft.CSharp" or "MoonSharp.Interpreter"),
    "The portable engine acquired a dependency outside the BCL and Lua interpreter.");
Require(engine.Types.All(type => type.Namespace.StartsWith("EliteScriptStudio.Core", StringComparison.Ordinal)),
    "The engine contains platform or presentation implementation types.");
Require(runtime.References.Contains("AccessRelayStudio.Engine"), "The runtime must reference the compiled engine.");
Require(runtime.References.All(name => name is not ("AccessRelayStudio" or "AccessRelayStudio.Application" or "AccessRelayStudio.Mcp") && !name.StartsWith("Microsoft.UI", StringComparison.Ordinal)
    && !name.StartsWith("Microsoft.WindowsAppSDK", StringComparison.Ordinal)), "The runtime acquired a host/WinUI dependency.");
Require(runtime.TypeReferences.All(type => !Presentation(type.Namespace)), "The runtime references presentation types.");
Require(runtime.Types.All(type => !Presentation(type.Namespace) && !type.Namespace.StartsWith("EliteScriptStudio.Core", StringComparison.Ordinal)),
    "The runtime contains presentation code or duplicate engine types.");
Require(runtime.Types.Any(type => type.Name == "StudioRuntime"), "The runtime does not own StudioRuntime.");
Console.WriteLine("PASS portable engine and Windows runtime assembly boundaries (metadata inspection; no native code executed).");

if (args.Length >= 3)
{
    var host = Read(args[2], "AccessRelayStudio");
    Require(host.References.Contains("AccessRelayStudio.Engine") && host.References.Contains("AccessRelayStudio.Runtime"),
        "The desktop host must reference the compiled engine and runtime.");
    Require(host.Types.Any(type => type.Name == "DesktopWindow") && host.Types.All(type => type.Name != "MainWindow"),
        "The desktop host must launch only the current window implementation.");
    Require(host.Types.All(type => !new[] { "EliteScriptStudio.Core", "EliteScriptStudio.Audio", "EliteScriptStudio.Input",
        "EliteScriptStudio.Output", "EliteScriptStudio.Vision", "EliteScriptStudio.Packs" }.Any(prefix => type.Namespace.StartsWith(prefix, StringComparison.Ordinal))
        && type.Name is not ("StudioRuntime" or "PrecisionTimer" or "HighResolutionReleaseTimer")),
        "The desktop host compiled engine/runtime implementation sources.");
    Console.WriteLine("PASS published desktop host references the libraries and contains no legacy screen or duplicate worker types.");
}
else Console.WriteLine("Host metadata was not supplied; published host verification remains a Windows CI check.");

if (args.Length == 4)
{
    var application = Read(args[3], "AccessRelayStudio.Application");
    Require(application.References.Contains("AccessRelayStudio.Engine") && application.References.All(name => name == "AccessRelayStudio.Engine"
        || name.StartsWith("System.", StringComparison.Ordinal) || name is "System" or "netstandard" or "mscorlib"),
        "Application use cases acquired a runtime, MCP SDK or presentation dependency.");
    Require(application.TypeReferences.All(type => !Presentation(type.Namespace)), "Application use cases reference presentation types.");
    Require(application.Types.All(type => type.Namespace.StartsWith("EliteScriptStudio.Application", StringComparison.Ordinal)), "Application contains implementation types outside its boundary.");
    Console.WriteLine("PASS shared Application references only the portable engine/BCL; runtime remains independent of application/MCP adapters.");
}

static bool Presentation(string ns) => ns.StartsWith("Microsoft.UI", StringComparison.Ordinal)
    || ns.StartsWith("Microsoft.Windows.Storage.Pickers", StringComparison.Ordinal)
    || ns.StartsWith("Windows.UI.Xaml", StringComparison.Ordinal)
    || ns.StartsWith("EliteScriptStudio.Desktop", StringComparison.Ordinal);

static AssemblyShape Read(string path, string expectedName)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    Require(metadata.GetString(metadata.GetAssemblyDefinition().Name) == expectedName, "Unexpected assembly identity: " + path);
    var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToHashSet(StringComparer.Ordinal);
    var types = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle))
        .Select(type => new TypeShape(metadata.GetString(type.Namespace), metadata.GetString(type.Name)))
        .Where(type => type.Namespace.StartsWith("EliteScriptStudio", StringComparison.Ordinal)).ToArray();
    var typeReferences = metadata.TypeReferences.Select(handle => metadata.GetTypeReference(handle))
        .Select(type => new TypeShape(metadata.GetString(type.Namespace), metadata.GetString(type.Name))).ToArray();
    Require(types.Length != 0, "Expected implementation types were not found: " + path);
    return new(references, types, typeReferences);
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
sealed record TypeShape(string Namespace, string Name);
sealed record AssemblyShape(HashSet<string> References, TypeShape[] Types, TypeShape[] TypeReferences);
