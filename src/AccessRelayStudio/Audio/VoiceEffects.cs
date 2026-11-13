namespace EliteScriptStudio.Audio;

public sealed record VoiceEffects
{
    public bool Bypass { get; init; }
    public double Mix { get; init; } = 1;
    public double FormantSemitones { get; init; }
    public double FormantStrength { get; init; } = 1;
    public double[] PreEqDb { get; init; } = new double[10];
    public double[] PostEqDb { get; init; } = new double[10];
    public double LowCutHz { get; init; }
    public int HumHz { get; init; }
    public double GateThresholdDb { get; init; } = -80;
    public double CompressorThresholdDb { get; init; }
    public double CompressorRatio { get; init; } = 3;
    public double DeEsser { get; init; }
    public double NoiseReduction { get; init; }
    public double Robot { get; init; }
    public double RobotHz { get; init; } = 80;
    public double Distortion { get; init; }
    public double Drive { get; init; } = 4;
    public int BitDepth { get; init; } = 16;
    public int DownsampleHz { get; init; } = 48000;
    public double Chorus { get; init; }
    public double ChorusHz { get; init; } = 1.2;
    public double Phaser { get; init; }
    public double Vibrato { get; init; }
    public double Tremolo { get; init; }
    public double TremoloHz { get; init; } = 5;
    public double Gapper { get; init; }
    public double GapperHz { get; init; } = 8;
    public double Growl { get; init; }
    public double Nasal { get; init; }
    public double Breathy { get; init; }
    public double Underwater { get; init; }
    public double Monotone { get; init; }
    public double Hypertone { get; init; }
    public double Diode { get; init; }
    public bool OctaveUp { get; init; }
    public double Animal { get; init; }
    public double AnimalDelayMs { get; init; } = 300;
    public double Harmony { get; init; }
    public double HarmonySemitones { get; init; } = 7;
    public double Detune { get; init; }
    public double Echo { get; init; }
    public double EchoMs { get; init; } = 220;
    public double EchoFeedback { get; init; } = .3;
    public double Reverb { get; init; }
    public double ReverbSize { get; init; } = .5;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Unit(Mix) && Range(FormantSemitones, -12, 12) && Unit(FormantStrength)
        && Eq(PreEqDb) && Eq(PostEqDb) && (LowCutHz == 0 || Range(LowCutHz, 20, 400))
        && HumHz is 0 or 50 or 60 && Range(GateThresholdDb, -80, -10)
        && Range(CompressorThresholdDb, -48, 0) && Range(CompressorRatio, 1, 12)
        && Unit(DeEsser) && Unit(NoiseReduction) && Unit(Robot) && Range(RobotHz, 20, 400)
        && Unit(Distortion) && Range(Drive, 1, 20) && BitDepth is >= 4 and <= 16
        && DownsampleHz is >= 2000 and <= 48000 && Unit(Chorus) && Range(ChorusHz, .1, 5)
        && Unit(Phaser) && Unit(Vibrato) && Unit(Tremolo) && Range(TremoloHz, .1, 20)
        && Unit(Gapper) && Range(GapperHz, .5, 30) && Unit(Growl) && Unit(Nasal)
        && Unit(Breathy) && Unit(Underwater) && Unit(Harmony) && Range(HarmonySemitones, -12, 12)
        && Unit(Monotone) && Unit(Hypertone) && (Monotone == 0 || Hypertone == 0) && Unit(Diode)
        && Unit(Animal) && Range(AnimalDelayMs, 50, 1500)
        && Unit(Detune) && Unit(Echo) && Range(EchoMs, 30, 1000) && Range(EchoFeedback, 0, .85)
        && Unit(Reverb) && Unit(ReverbSize);

    public VoiceEffects Snapshot() => this with { PreEqDb = (double[])PreEqDb.Clone(), PostEqDb = (double[])PostEqDb.Clone() };
    private static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static bool Unit(double value) => Range(value, 0, 1);
    private static bool Eq(double[]? values) => values is { Length: 10 } && values.All(value => Range(value, -20, 20));
}
