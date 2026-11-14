using System.Collections.Immutable;

namespace EliteScriptStudio.Core;

public enum GamePackAssetKind { Lua, Model, VisionManifest, Runtime, Ui, Workflow, CaptureModel, Data }
public enum GamePackTuningKind { Range, Choice }
public sealed record GamePackAsset(string Path, GamePackAssetKind Kind, long Bytes, string Sha256);
public sealed record GamePackRuntime(string Assembly, string Factory, string UiEntry, string TrainingEntry, string ModelDirectory, int ContractVersion = 1, string AuditEntry = "", string ExperimentsEntry = "");
public sealed record GamePackChoice(string Label, double Value);
public sealed record GamePackTuning(string Id, string Label, GamePackTuningKind Kind, double Minimum,
    double Maximum, double Step, double Default, ImmutableArray<GamePackChoice> Choices);
public sealed record GamePackBinding(ControlId Control, string Script, bool Consume = false);
public sealed record GamePackPreset(string Name, ImmutableDictionary<string, double> Values);
public sealed record GamePackReference(string Id, string Version, string ManifestSha256, ImmutableDictionary<string, double> Tuning)
{
    internal void Validate()
    {
        if (!GamePackManifest.Identifier(Id, 60) || !GamePackManifest.Identifier(Version, 40) || !GamePackManifest.Hash(ManifestSha256))
            throw new InvalidDataException("Invalid game-pack profile reference.");
        GamePackManifest.ValidateNumbers(Tuning);
    }
}
public sealed record GamePackManifest
{
    public int SchemaVersion { get; init; } = 1;
    public int ApiVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string License { get; init; } = "";
    public string VisionAdapter { get; init; } = "none";
    public string GameId { get; init; } = "";
    public string TrainingAdapter { get; init; } = "none";
    public GamePackRuntime? Runtime { get; init; }
    public ImmutableArray<GamePackAsset> Assets { get; init; } = [];
    public ImmutableArray<GamePackBinding> Bindings { get; init; } = [];
    public ImmutableArray<GamePackTuning> Tuning { get; init; } = [];
    public ImmutableArray<GamePackPreset> Presets { get; init; } = [];
    internal static bool Identifier(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    internal static void ValidateNumbers(IReadOnlyDictionary<string, double>? values)
    {
        if (values is null || values.Count > 32 || values.Any(pair => !Identifier(pair.Key, 40) || !double.IsFinite(pair.Value) || Math.Abs(pair.Value) > 10000))
            throw new InvalidDataException("Game-pack tuning needs up to 32 bounded finite numeric values.");
    }
    internal void Validate()
    {
        if (!((SchemaVersion == 1 && ApiVersion == 1) || (SchemaVersion == 2 && ApiVersion == 2)) || !Identifier(Id, 60) || !Identifier(Version, 40) || !Text(Name, 80)
            || !Text(Description, 1000) || !Text(License, 300) || VisionAdapter is not ("none" or "temporal-release-v1"))
            throw new InvalidDataException("Unsupported game-pack schema/API, identity, metadata or typed vision adapter.");
        if (GameId is null || (GameId.Length != 0 && !Identifier(GameId, 60)) || !Identifier(TrainingAdapter, 80)
            || (TrainingAdapter != "none" && (GameId.Length == 0 || VisionAdapter == "none" || Bindings.IsDefaultOrEmpty == false)))
            throw new InvalidDataException("A training plugin must identify its game and adapter independently of controller bindings.");
        if ((SchemaVersion == 1 && Runtime is not null) || (SchemaVersion == 2 && (Runtime is null || GameId.Length == 0)))
            throw new InvalidDataException("An implementation plugin requires schema/API 2, a game identity and a runtime declaration.");
        if (Assets.IsDefault || Assets.Length > 40 || Assets.Any(asset => asset is null) || Assets.Select(asset => asset.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Assets.Length)
            throw new InvalidDataException("A game pack needs at most 40 uniquely named resources.");
        long total = 0;
        foreach (var asset in Assets)
        {
            var (prefix, extension, limit) = asset.Kind switch {
                GamePackAssetKind.Lua => ("scripts/", ".lua", 128L * 1024),
                GamePackAssetKind.Model => ("models/", ".onnx", 128L * 1024 * 1024),
                GamePackAssetKind.VisionManifest => ("models/", ".json", 256L * 1024),
                GamePackAssetKind.Runtime when SchemaVersion == 2 => ("runtime/", ".dll", 16L * 1024 * 1024),
                GamePackAssetKind.Ui when SchemaVersion == 2 => ("ui/", ".js", 1024L * 1024),
                GamePackAssetKind.Workflow when SchemaVersion == 2 => ("training/", ".py", 1024L * 1024),
                GamePackAssetKind.CaptureModel when SchemaVersion == 2 => ("capture-models/", ".onnx", 128L * 1024 * 1024),
                GamePackAssetKind.Data when SchemaVersion == 2 => ("data/", "", 512L * 1024),
                _ => throw new InvalidDataException("Unsupported resource kind for this package API.") };
            if (asset.Path is null || asset.Path.Length > 180 || !asset.Path.StartsWith(prefix, StringComparison.Ordinal)
                || !asset.Path.EndsWith(extension, StringComparison.Ordinal) || asset.Path.Split('/').Any(segment => !Identifier(SchemaVersion == 2 ? segment.ToLowerInvariant() : segment, 80)
                    || segment is "." or ".." || segment.EndsWith('.') || segment.Split('.')[0] is "con" or "prn" or "aux" or "nul"
                    || (segment.Length >= 4 && (segment.StartsWith("com") || segment.StartsWith("lpt")) && char.IsDigit(segment[3])))
                || (asset.Kind == GamePackAssetKind.Data && Path.GetExtension(asset.Path) is not (".json" or ".txt" or ".md"))
                || !Hash(asset.Sha256) || asset.Bytes <= 0 || asset.Bytes > limit)
                throw new InvalidDataException("Invalid resource path, size, type or SHA-256.");
            total += asset.Bytes;
        }
        if (total > 256L * 1024 * 1024 || Assets.Count(asset => asset.Kind == GamePackAssetKind.Lua) > 32
            || Assets.Count(asset => asset.Kind == GamePackAssetKind.Model) > 4 || Assets.Count(asset => asset.Kind == GamePackAssetKind.VisionManifest) > 4
            || Assets.Count(asset => asset.Kind == GamePackAssetKind.CaptureModel) > 8
            || (VisionAdapter == "none" && Assets.Any(asset => asset.Kind is GamePackAssetKind.Model or GamePackAssetKind.VisionManifest or GamePackAssetKind.CaptureModel)))
            throw new InvalidDataException("Game pack exceeds resource limits or supplies models without a vision adapter.");
        if (Runtime is { } runtime && (runtime.ContractVersion != 1 || !Text(runtime.Factory, 240)
            || runtime.Factory.Split('.').Any(part => part.Length == 0 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            || !Assets.Any(a => a.Kind == GamePackAssetKind.Runtime && a.Path == runtime.Assembly)
            || !Assets.Any(a => a.Kind == GamePackAssetKind.Ui && a.Path == runtime.UiEntry)
            || !Assets.Any(a => a.Kind == GamePackAssetKind.Workflow && a.Path == runtime.TrainingEntry)
            || new[] { runtime.AuditEntry, runtime.ExperimentsEntry }.Any(path => path.Length != 0 && !Assets.Any(a => a.Kind == GamePackAssetKind.Workflow && a.Path == path))
            || runtime.ModelDirectory != "capture-models"))
            throw new InvalidDataException("The runtime must declare a supported contract, factory and checked implementation resources.");
        if (Bindings.IsDefault || Bindings.Length > ControlMap.All.Count || Bindings.Any(binding => binding is null || !Enum.IsDefined(binding.Control)
                || !Assets.Any(asset => asset.Path == binding.Script && asset.Kind == GamePackAssetKind.Lua))
            || Bindings.Select(binding => binding.Control).Distinct().Count() != Bindings.Length)
            throw new InvalidDataException("Pack bindings need unique supported controls and declared Lua scripts.");
        if (Tuning.IsDefault || Tuning.Length > 32 || Tuning.Any(control => control is null) || Tuning.Select(control => control.Id).Distinct().Count() != Tuning.Length)
            throw new InvalidDataException("Pack tuning needs up to 32 unique controls.");
        foreach (var control in Tuning)
        {
            if (!Identifier(control.Id, 40) || !Text(control.Label, 80) || !Enum.IsDefined(control.Kind)
                || !double.IsFinite(control.Minimum) || !double.IsFinite(control.Maximum) || !double.IsFinite(control.Step)
                || control.Minimum < -10000 || control.Maximum > 10000 || control.Maximum < control.Minimum || control.Step <= 0
                || control.Choices.IsDefault || control.Choices.Length > 32
                || control.Choices.Any(choice => choice is null || !Text(choice.Label, 80) || !double.IsFinite(choice.Value) || choice.Value < control.Minimum || choice.Value > control.Maximum)
                || (control.Kind == GamePackTuningKind.Choice && (control.Choices.Length == 0 || control.Choices.Select(choice => choice.Value).Distinct().Count() != control.Choices.Length)))
                throw new InvalidDataException("Invalid declarative game-pack tuning control.");
            ValidateValue(control, control.Default);
        }
        if (Presets.IsDefault || Presets.Length > 16 || Presets.Any(preset => preset is null || !Text(preset.Name, 80)))
            throw new InvalidDataException("A pack can contain up to 16 named tuning presets.");
        foreach (var preset in Presets) _ = ResolveTuning(preset.Values);
    }
    internal ImmutableDictionary<string, double> ResolveTuning(IReadOnlyDictionary<string, double> overrides)
    {
        ValidateNumbers(overrides);
        if (overrides.Keys.Any(key => !Tuning.Any(control => control.Id == key))) throw new InvalidDataException("Unknown game-pack tuning control.");
        return Tuning.ToImmutableDictionary(control => control.Id, control =>
        { double value = overrides.TryGetValue(control.Id, out var selected) ? selected : control.Default; ValidateValue(control, value); return value; });
    }
    private static void ValidateValue(GamePackTuning control, double value)
    {
        if (!double.IsFinite(value) || value < control.Minimum || value > control.Maximum
            || (control.Kind == GamePackTuningKind.Choice ? !control.Choices.Any(choice => choice.Value == value)
                : Math.Abs((value - control.Minimum) / control.Step - Math.Round((value - control.Minimum) / control.Step)) > 1e-6))
            throw new InvalidDataException("Tuning value is outside the pack's declared range/choices.");
    }
}
