using System.Collections.Immutable;
using System.IO;

namespace EliteScriptStudio.Core;

public enum ScriptKind { Passthrough, Rhythm, IntentionalMiss, Moving, Stepback, Recorded }
public enum ShotVariant { Rhythm, MissEarly, MissLate, Moving, Stepback }

public sealed record ScriptBinding(ScriptKind Script, bool Enabled = true, bool Consume = false, Guid? RecordingId = null);

public sealed record ScriptTiming(
    int StartDelay = 0, int DownSweep = 0, int DownHold = 512, int UpSweep = 80,
    int UpHold = 48, int CenterHold = 120, int Flick = 64, int Settle = 240,
    short FlickX = 0, short FlickY = -32767)
{
    public const int MaxMs = 10000;

    public void Validate()
    {
        Check(nameof(StartDelay), StartDelay, 0);
        if (DownSweep != 0)
            Check(nameof(DownSweep), DownSweep, 3);
        Check(nameof(DownHold), DownHold, 1);
        Check(nameof(UpSweep), UpSweep, 4);
        Check(nameof(UpHold), UpHold, 1);
        Check(nameof(CenterHold), CenterHold, 1);
        Check(nameof(Flick), Flick, 1);
        Check(nameof(Settle), Settle, 1);
    }

    private static void Check(string field, int value, int min)
    {
        if (value < min || value > MaxMs)
            throw new InvalidDataException($"{field} must be between {min} and {MaxMs} ms.");
    }
}

public sealed record StudioProfile
{
    private static readonly ScriptBinding Passthrough = new(ScriptKind.Passthrough);
    private static readonly ImmutableDictionary<ShotVariant, ScriptTiming> Defaults =
        new Dictionary<ShotVariant, ScriptTiming>
        {
            [ShotVariant.Rhythm] = new(),
            [ShotVariant.MissEarly] = new(DownHold: 300, UpSweep: 96),
            [ShotVariant.MissLate] = new(DownHold: 660, UpSweep: 96),
            [ShotVariant.Moving] = new(StartDelay: 32, DownHold: 430),
            [ShotVariant.Stepback] = new(DownHold: 464, UpSweep: 112)
        }.ToImmutableDictionary();

    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public bool ScriptsEnabled { get; init; }
    public ImmutableDictionary<ControlId, ScriptBinding> Bindings { get; init; } =
        ImmutableDictionary<ControlId, ScriptBinding>.Empty;
    public ImmutableDictionary<ShotVariant, ScriptTiming> Timings { get; init; } = Defaults;
    public ImmutableDictionary<Guid, MacroRecording> Recordings { get; init; } =
        ImmutableDictionary<Guid, MacroRecording>.Empty;

    public static StudioProfile CreateDefault(string name = "PC Profile") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Bindings = new Dictionary<ControlId, ScriptBinding>
        {
            [ControlId.LargeRight] = new(ScriptKind.Rhythm),
            [ControlId.LargeLeft] = new(ScriptKind.IntentionalMiss),
            [ControlId.SmallRight] = new(ScriptKind.Moving),
            [ControlId.SmallLeft] = new(ScriptKind.Stepback)
        }.ToImmutableDictionary()
    };

    public ScriptBinding GetBinding(ControlId control) => Bindings.GetValueOrDefault(control, Passthrough);

    public StudioProfile WithBinding(ControlId control, ScriptBinding binding) =>
        this with { Bindings = Bindings.SetItem(control, binding) };

    public ScriptTiming GetTiming(ShotVariant variant) => Timings[variant];

    public StudioProfile WithTiming(ShotVariant variant, ScriptTiming timing) =>
        this with { Timings = Timings.SetItem(variant, timing) };

    public StudioProfile WithRecording(MacroRecording recording) =>
        this with { Recordings = Recordings.SetItem(recording.Id, recording) };

    public void Validate()
    {
        if (Id == Guid.Empty)
            throw new InvalidDataException("The profile needs an ID.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl))
            throw new InvalidDataException("The profile needs a name of up to 80 characters.");
        if (Recordings is null || Recordings.Count > MacroRecording.MaxRecordingsPerProfile)
            throw new InvalidDataException($"A profile can contain up to {MacroRecording.MaxRecordingsPerProfile} recordings.");
        foreach (var (id, recording) in Recordings)
        {
            if (recording is null || recording.Id != id)
                throw new InvalidDataException("The profile contains an invalid recording ID.");
            recording.Validate();
        }
        if (Bindings is null || Bindings.Any(entry => !Enum.IsDefined(entry.Key) || entry.Value is null
            || !Enum.IsDefined(entry.Value.Script)
            || (entry.Value.Script == ScriptKind.Recorded
                && (entry.Value.RecordingId is not Guid id || !Recordings.ContainsKey(id)))))
            throw new InvalidDataException("The profile contains an invalid control or script binding.");
        if (Timings is null || Timings.Count != Defaults.Count)
            throw new InvalidDataException("The profile needs timing for all five shot variants.");
        foreach (var variant in Defaults.Keys)
        {
            if (!Timings.TryGetValue(variant, out var timing) || timing is null)
                throw new InvalidDataException($"The profile has no {variant} timing.");
            timing.Validate();
        }
    }
}
