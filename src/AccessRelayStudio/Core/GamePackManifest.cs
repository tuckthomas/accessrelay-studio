using System.Collections.Immutable;

namespace EliteScriptStudio.Core;

public enum GamePackAssetKind { Lua, Model, VisionManifest }
public enum GamePackTuningKind { Range, Choice }
public sealed record GamePackAsset(string Path, GamePackAssetKind Kind, long Bytes, string Sha256);
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
        if (SchemaVersion != 1 || ApiVersion != 1 || !Identifier(Id, 60) || !Identifier(Version, 40) || !Text(Name, 80)
            || !Text(Description, 1000) || !Text(License, 300) || VisionAdapter is not ("none" or "temporal-release-v1"))
            throw new InvalidDataException("Unsupported game-pack schema/API, identity, metadata or typed vision adapter.");
        if (GameId is null || (GameId.Length != 0 && !Identifier(GameId, 60)) || !Identifier(TrainingAdapter, 80)
            || (TrainingAdapter != "none" && (GameId.Length == 0 || VisionAdapter == "none" || Bindings.IsDefaultOrEmpty == false)))
            throw new InvalidDataException("A training plugin must identify its game and reviewed adapter, independently of controller bindings.");
        if (Assets.IsDefault || Assets.Length > 40 || Assets.Any(asset => asset is null) || Assets.Select(asset => asset.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Assets.Length)
            throw new InvalidDataException("A game pack needs at most 40 uniquely named resources.");
        long total = 0;
        foreach (var asset in Assets)
        {
            string extension = asset.Kind switch { GamePackAssetKind.Lua => ".lua", GamePackAssetKind.Model => ".onnx", GamePackAssetKind.VisionManifest => ".json", _ => throw new InvalidDataException("Only Lua, ONNX and JSON vision manifests are allowed.") };
            string prefix = asset.Kind == GamePackAssetKind.Lua ? "scripts/" : "models/";
            if (asset.Path is null || asset.Path.Length > 180 || !asset.Path.StartsWith(prefix, StringComparison.Ordinal)
                || !asset.Path.EndsWith(extension, StringComparison.Ordinal) || asset.Path.Split('/').Any(segment => !Identifier(segment, 80)
                    || segment is "." or ".." || segment.EndsWith('.') || segment.Split('.')[0] is "con" or "prn" or "aux" or "nul"
                    || (segment.Length >= 4 && (segment.StartsWith("com") || segment.StartsWith("lpt")) && char.IsDigit(segment[3])))
                || !Hash(asset.Sha256) || asset.Bytes <= 0 || asset.Bytes > (asset.Kind == GamePackAssetKind.Model ? 128L * 1024 * 1024 : asset.Kind == GamePackAssetKind.Lua ? 128 * 1024 : 256 * 1024))
                throw new InvalidDataException("Invalid resource path, size, type or SHA-256. Native plugins and traversal paths are unsupported.");
            total += asset.Bytes;
        }
        if (total > 256L * 1024 * 1024 || Assets.Count(asset => asset.Kind == GamePackAssetKind.Lua) > 32
            || Assets.Count(asset => asset.Kind == GamePackAssetKind.Model) > 4 || Assets.Count(asset => asset.Kind == GamePackAssetKind.VisionManifest) > 4
            || (VisionAdapter == "none" && Assets.Any(asset => asset.Kind != GamePackAssetKind.Lua)))
            throw new InvalidDataException("Game pack exceeds resource limits or supplies models without a vision adapter.");
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
