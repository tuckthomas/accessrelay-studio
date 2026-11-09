namespace EliteScriptStudio.Core;

public readonly record struct StickPhase(string Label, int DurationMs, short RightY, short? RightX = null);

public static class Trajectory
{
    private static readonly short[] DownPoints = [-8192, -16384, -24575];
    private static readonly short[] UpPoints = [-24575, -8192, 8192, 24575];

    public static IReadOnlyList<StickPhase> Build(StudioProfile profile, ScriptKind script, bool lateMiss = false) =>
        script == ScriptKind.Passthrough ? [] : Build(profile, VariantFor(script, lateMiss));

    public static IReadOnlyList<StickPhase> Build(StudioProfile profile, ShotVariant variant)
    {
        var timing = profile.GetTiming(variant);
        timing.Validate();
        var phases = new List<StickPhase>();
        if (timing.StartDelay > 0)
            phases.Add(new("Start delay", timing.StartDelay, 0));
        if (variant == ShotVariant.Stepback)
        {
            short? x = timing.FlickX == 0 ? null : timing.FlickX;
            phases.Add(new("Stepback flick", timing.Flick, timing.FlickY, x));
            phases.Add(new("Stepback settle", timing.Settle, 0, x.HasValue ? (short)0 : null));
        }
        if (timing.DownSweep > 0)
            AddSweep(phases, "Down sweep", timing.DownSweep, DownPoints);
        phases.Add(new("Down hold", timing.DownHold, -32767));
        AddSweep(phases, "Up sweep", timing.UpSweep, UpPoints);
        phases.Add(new("Up hold", timing.UpHold, 32767));
        phases.Add(new("Center hold", timing.CenterHold, 0));
        return phases.AsReadOnly();
    }

    public static ShotVariant VariantFor(ScriptKind script, bool lateMiss = false) => script switch
    {
        ScriptKind.Rhythm => ShotVariant.Rhythm,
        ScriptKind.IntentionalMiss => lateMiss ? ShotVariant.MissLate : ShotVariant.MissEarly,
        ScriptKind.Moving => ShotVariant.Moving,
        ScriptKind.Stepback => ShotVariant.Stepback,
        _ => throw new ArgumentOutOfRangeException(nameof(script))
    };

    private static void AddSweep(List<StickPhase> phases, string label, int duration, short[] points)
    {
        int baseTime = duration / points.Length;
        int remainder = duration % points.Length;
        for (int i = 0; i < points.Length; i++)
            phases.Add(new(label, baseTime + (i < remainder ? 1 : 0), points[i]));
    }
}
