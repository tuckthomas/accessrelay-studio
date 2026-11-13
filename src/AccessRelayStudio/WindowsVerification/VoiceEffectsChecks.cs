using System.Text.Json;
using EliteScriptStudio.Audio;

internal static class VoiceEffectsChecks
{
    internal static void Run()
    {
        var flat = new VoiceSettings(VoicePreset.Clean, 0, 0, false);
        var legacy = JsonSerializer.Deserialize<VoiceSettings>("{\"Preset\":0,\"PitchSemitones\":0,\"GainDb\":0,\"Muted\":false}")!;
        Check(legacy.IsValid && legacy.Effects.Mix == 1, "Old voices no longer load with neutral effects.");
        Check(!(flat with { Effects = new() { PreEqDb = [0] } }).IsValid, "Invalid EQ accepted.");
        Check(!(flat with { Effects = new() { EchoFeedback = double.NaN } }).IsValid, "Nonfinite effect accepted.");
        float[] input = Tone(220, 48000);
        float[] neutral = Render(input, flat);
        Check(Difference(input, neutral) < 1e-6, "Neutral effects change the original voice.");
        var effects = new Dictionary<string, VoiceEffects>
        {
            ["timbre"] = new() { FormantSemitones = 5 }, ["pre EQ"] = new() { PreEqDb = [0, 0, 0, 12, 0, 0, 0, 0, 0, 0] },
            ["post EQ"] = new() { PostEqDb = [0, 0, 0, -12, 0, 0, 0, 0, 0, 0] }, ["low cut"] = new() { LowCutHz = 350 },
            ["gate"] = new() { GateThresholdDb = -10 }, ["compressor"] = new() { CompressorThresholdDb = -35, CompressorRatio = 8 },
            ["noise reduction"] = new() { NoiseReduction = 1 }, ["robot"] = new() { Robot = 1 }, ["distortion"] = new() { Distortion = .8 },
            ["bitcrusher"] = new() { BitDepth = 4, DownsampleHz = 4000 }, ["chorus"] = new() { Chorus = .7 },
            ["phaser"] = new() { Phaser = .8 }, ["vibrato"] = new() { Vibrato = .8 }, ["tremolo"] = new() { Tremolo = .8 },
            ["gapper"] = new() { Gapper = 1 }, ["growl"] = new() { Growl = .8 }, ["nasal"] = new() { Nasal = .8 },
            ["breathy"] = new() { Breathy = .8 }, ["underwater"] = new() { Underwater = .8 },
            ["harmony"] = new() { Harmony = .8 }, ["detune"] = new() { Detune = .8 },
            ["echo"] = new() { Echo = .8 }, ["reverb"] = new() { Reverb = .8 },
            ["monotone"] = new() { Monotone = .8 }, ["expressive pitch"] = new() { Hypertone = .8 },
            ["diode"] = new() { Diode = .8 }, ["animal"] = new() { Animal = .8 }
        };
        foreach (var (name, effect) in effects)
        {
            Check(effect.IsValid, $"Default {name} configuration is invalid.");
            var rendered = Render(input, flat with { Effects = effect });
            Check(rendered.All(value => float.IsFinite(value) && Math.Abs(value) <= .981f), $"{name} produced invalid or unclipped audio.");
            Check(Difference(neutral, rendered) > 1e-4, $"{name} does not process audio.");
        }
        float[] sibilant = Tone(6500, 48000, .4f);
        Check(Energy(Render(sibilant, flat with { Effects = new() { DeEsser = 1 } })) < Energy(sibilant) * .8,
            "De-esser did not attenuate high-frequency speech.");
        float[] hum = Tone(60, 48000);
        Check(Energy(Render(hum, flat with { Effects = new() { HumHz = 60 } })) < Energy(hum) * .05,
            "Hum filter did not remove mains noise.");
        float[] pitched = Render(input, flat with { PitchSemitones = 12 });
        Check(PowerAt(pitched, 440) > PowerAt(pitched, 220) * 4, "Pitch does not shift the fundamental independently.");
        float[] formanted = Render(input, flat with { Effects = new() { FormantSemitones = 5 } });
        Check(PowerAt(formanted, 220) > PowerAt(formanted, 440) * 4, "Timbre control changes the fundamental pitch.");
        var noiseSource = new Random(17);
        float[] noise = Enumerable.Range(0, 144000).Select(_ => (noiseSource.NextSingle() * 2 - 1) * .03f).ToArray();
        Check(Energy(Render(noise, flat with { Effects = new() { NoiseReduction = 1 } })) < Energy(noise) * .9,
            "Adaptive reduction did not attenuate stationary noise.");
        float[] impulse = new float[24000]; impulse[0] = .4f;
        float[] echo = Render(impulse, flat with { Effects = new() { Echo = 1, EchoMs = 100, EchoFeedback = .5 } });
        Check(echo.Skip(4700).Take(300).Max(Math.Abs) > .1 && echo.Skip(9500).Take(300).Max(Math.Abs) > .05,
            "Echo does not produce timed, decaying repetitions.");
        float[] octave = Render(input, flat with { Effects = new() { Diode = 1, OctaveUp = true } });
        Check(PowerAt(octave, 440) > PowerAt(octave, 220) * 4, "Diode octave-up mode does not double the register.");
        var monotone = new VoiceProsody(); var expressive = new VoiceProsody();
        double monoCorrection = 0, expressiveCorrection = 0;
        double phase = 0;
        for (int i = 0; i < 100; i++)
        {
            float[] changingPitch = new float[480];
            for (int frame = 0; frame < changingPitch.Length; frame++)
            {
                changingPitch[frame] = .2f * (float)Math.Sin(phase);
                phase = (phase + Math.Tau * (i < 60 ? 180 : 270) / 48000) % Math.Tau;
            }
            float[] other = (float[])changingPitch.Clone();
            monoCorrection = monotone.Process(changingPitch, 480, 1, 0);
            expressiveCorrection = expressive.Process(other, 480, 0, 1);
        }
        Check(monoCorrection < -1 && expressiveCorrection > 1, "Pitch variation modes do not flatten/expand detected intonation.");

        using var samples = new VoiceTestBuffer(flat);
        var live = samples.Signal;
        live.SetSettings(flat with { Effects = new() { Robot = 1 } });
        Check(Difference(samples.Read(input), neutral) > .01, "Live edits require restarting the audio path.");
        live.SetSettings(flat with { Effects = new() { Bypass = true, Echo = 1, Reverb = 1, FormantSemitones = 9 }, PitchSemitones = 12 });
        Check(Difference(samples.Read(input), input) < 1e-6, "Bypass still processes the voice.");
        live.SetSettings(flat with { Muted = true, Effects = new() { Echo = 1, Reverb = 1 } });
        Check(samples.Read(input).All(value => value == 0), "Mute leaked audio.");
        live.SetSettings(flat with { Effects = new() { Echo = 1, Reverb = 1 } });
        Check(samples.Read(new float[48000]).All(value => value == 0), "Unmute replayed speech from the muted interval.");

        string directory = Path.Combine(Path.GetTempPath(), "AccessRelayVoiceEffects-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new VoiceProfiles(directory);
            var profile = store.SaveEffects("Live FX check", flat with { Effects = new() { Echo = .6, FormantSemitones = 3, PostEqDb = [1, 2, 3, 4, 5, -1, -2, -3, -4, -5] } });
            var restored = store.LoadAll().Single(item => item.Id == profile.Id).Effects!;
            Check(restored.Effects.Echo == .6 && restored.Effects.FormantSemitones == 3 && restored.Effects.PostEqDb[9] == -5,
                "Saved voices lost the effects chain.");
            store.Delete(profile.Id); File.Delete(Path.Combine(directory, "selection.json")); Directory.Delete(directory);
        }
        finally { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        Console.WriteLine("SUCCESS: live DSP, 27 effect configurations, independent pitch/timbre, noise attenuation, pitch variation, echo timing, bypass, mute, finite output, legacy voices and profile persistence.");
    }
    private sealed class VoiceTestBuffer(VoiceSettings settings) : IDisposable
    {
        public VoiceSignal Signal { get; } = new(settings, bufferMilliseconds: 80, primeMilliseconds: 0);
        public float[] Read(float[] input)
        {
            float[] output = new float[input.Length];
            for (int at = 0; at < input.Length; at += 480)
            {
                int count = Math.Min(480, input.Length - at);
                Signal.Write(input.AsSpan(at, count)); Signal.Read(output, at, count);
            }
            return output;
        }
        public void Dispose() { }
    }
    private static float[] Render(float[] input, VoiceSettings settings) { using var test = new VoiceTestBuffer(settings); return test.Read(input); }
    private static float[] Tone(double frequency, int count, float amplitude = .2f) => Enumerable.Range(0, count).Select(i => amplitude * (float)Math.Sin(Math.Tau * frequency * i / 48000)).ToArray();
    private static double Difference(float[] a, float[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
    private static double Energy(float[] samples) => samples.Skip(12000).Select(value => value * (double)value).Average();
    private static double PowerAt(float[] samples, double frequency)
    {
        double real = 0, imaginary = 0;
        for (int i = 12000; i < samples.Length; i++) { real += samples[i] * Math.Cos(Math.Tau * frequency * i / 48000); imaginary += samples[i] * Math.Sin(Math.Tau * frequency * i / 48000); }
        return real * real + imaginary * imaginary;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
