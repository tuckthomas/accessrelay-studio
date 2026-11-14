using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Packs.Nba2K27;

internal sealed class Nba2K27VisionPlugin : IVisionGamePlugin, IGameControllerPlugin
{
    private readonly Nba2K27ShotProfileStore _store;
    private Nba2K27ShotProfile[] _profiles;
    private Nba2K27ShotProfile? _selected;
    private static readonly JsonSerializerOptions Format = new() { PropertyNameCaseInsensitive = true, MaxDepth = 8 };

    public string Id => "nba2k27";
    public string Name => "NBA 2K27";
    public string OutputContract => "temporal-release-v1";
    public string TrainingWorkflowFile => "Run-NbaTraining.py";
    public IReadOnlyList<string> CameraViews => Nba2K27ShotProfile.CameraViews;
    public IReadOnlyList<IVisionCaptureProfile> Profiles => _profiles;
    public IVisionCaptureProfile? SelectedProfile => _selected;

    internal Nba2K27VisionPlugin(string? directory = null)
    {
        _store = new(directory);
        _profiles = _store.LoadAll().ToArray();
        if (_store.LoadSelectedId() is Guid id) _selected = _profiles.FirstOrDefault(profile => profile.Id == id);
    }

    public void SelectProfile(Guid id) { _selected = _store.Load(id); _store.Select(id); }

    public IVisionCaptureProfile SaveProfile(JsonElement data, string expectedFingerprint)
    {
        var profile = data.Deserialize<Nba2K27ShotProfile>(Format) ?? throw new InvalidDataException("The shot setup is empty.");
        profile = profile with { Gamertag = (profile.Gamertag ?? "").Trim().Normalize() };
        if (profile.Id == Guid.Empty) profile = profile with { Id = Guid.NewGuid() };
        profile.Validate();
        if (expectedFingerprint.Length != 0 && _profiles.Any(item => item.Id == profile.Id)
            && _store.Load(profile.Id).ConfigurationFingerprint != expectedFingerprint)
            throw new InvalidOperationException("This setup changed elsewhere. Reopen it before saving.");
        _store.Save(profile); Reload(); SelectProfile(profile.Id);
        return _selected!;
    }

    public IVisionCaptureProfile DuplicateProfile(Guid id, string name)
    {
        var copy = _store.Duplicate(id, name); Reload(); SelectProfile(copy.Id); return _selected!;
    }

    public void DeleteProfile(Guid id)
    {
        _store.Delete(id); Reload(); if (_selected?.Id == id) _selected = null;
    }

    private void Reload() => _profiles = _store.LoadAll().ToArray();

    public (StudioProfile Profile, object Result) ApplyControllerAction(StudioProfile profile, string action, JsonElement args)
    {
        if (profile.GamePack?.Id != Id || action != "feedback") throw new InvalidOperationException("This action requires an NBA 2K27 pack setup.");
        var adjustment = Nba2K27Tuning.Suggest(Choice<Nba2K27TimingFeedback>(args, "timingFeedback"), Choice<Nba2K27TempoFeedback>(args, "tempoFeedback"));
        if (args.TryGetProperty("id", out var savedId) && savedId.ValueKind == JsonValueKind.String && Guid.TryParse(savedId.GetString(), out var id))
        {
            if (!profile.Recordings.TryGetValue(id, out var recording)) throw new InvalidOperationException("That sequence no longer exists.");
            return (profile.WithRecording(Nba2K27Tuning.Apply(recording, adjustment)), adjustment);
        }
        var variant = Choice<ShotVariant>(args, "variant");
        return (profile.WithTiming(variant, Nba2K27Tuning.Apply(profile.GetTiming(variant), adjustment)), adjustment);
    }

    private static T Choice<T>(JsonElement args, string name) where T : struct, Enum =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && Enum.TryParse<T>(value.GetString(), true, out var result) && Enum.IsDefined(result)
            ? result : throw new InvalidDataException("Choose a supported " + name + ".");
}
