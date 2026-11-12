using System.Collections.Immutable;

namespace EliteScriptStudio.Core;

/// <summary>Personalized local spectral templates, not a universal phoneme or dysarthria model.</summary>
public sealed record AcousticTemplate(int Version, int DurationMs, ImmutableArray<ImmutableArray<float>> Frames)
{
    internal const int Bands = 20, SamplesPerFrame = 320, SampleRate = 16000;
    internal void Validate()
    {
        if (Version != 1 || DurationMs is < 60 or > 1500 || Frames.IsDefault || Frames.Length is < 3 or > 75
            || DurationMs != Frames.Length * 20 || Frames.Any(frame => frame.IsDefault || frame.Length != Bands
                || frame.Any(value => !float.IsFinite(value) || value is < 0 or > 1.001f)
                || Math.Abs(frame.Sum(v => v * v) - 1) > .02))
            throw new InvalidDataException("Invalid or unsupported personalized acoustic template.");
    }
}
internal static class AcousticTemplates
{
    private static readonly (double[] Cos, double[] Sin)[] Basis = Enumerable.Range(0, AcousticTemplate.Bands).Select(band =>
    {
        double frequency = 120 * Math.Pow(7600d / 120, band / (AcousticTemplate.Bands - 1d));
        var cos = new double[320]; var sin = new double[320];
        for (int i = 0; i < 320; i++)
        { double window = .5 - .5 * Math.Cos(2 * Math.PI * i / 319); cos[i] = window * Math.Cos(2 * Math.PI * frequency * i / 16000); sin[i] = window * Math.Sin(2 * Math.PI * frequency * i / 16000); }
        return (cos, sin);
    }).ToArray();
    internal static AcousticTemplate Extract(ReadOnlySpan<short> pcm)
    {
        int count = pcm.Length / AcousticTemplate.SamplesPerFrame;
        if (count is < 3 or > 75 || pcm.Length != count * 320) throw new InvalidDataException("Record a sound lasting 60–1500 ms.");
        var frames = ImmutableArray.CreateBuilder<ImmutableArray<float>>(count);
        for (int frame = 0; frame < count; frame++)
        {
            var values = new float[20]; double norm = 0;
            for (int band = 0; band < 20; band++)
            {
                double real = 0, imaginary = 0;
                for (int i = 0; i < 320; i++) { double sample = pcm[frame * 320 + i] / 32768d; real += sample * Basis[band].Cos[i]; imaginary += sample * Basis[band].Sin[i]; }
                values[band] = (float)Math.Log(1 + Math.Sqrt(real * real + imaginary * imaginary)); norm += values[band] * values[band];
            }
            if (norm < 1e-12) { values[0] = 1; norm = 1; }
            for (int i = 0; i < 20; i++) values[i] /= (float)Math.Sqrt(norm);
            frames.Add([.. values]);
        }
        return new(1, count * 20, frames.MoveToImmutable());
    }
    internal static double Similarity(AcousticTemplate a, AcousticTemplate b)
    {
        a.Validate(); b.Validate();
        if (a.DurationMs > b.DurationMs * 1.8 || b.DurationMs > a.DurationMs * 1.8) return 0;
        var previous = Enumerable.Repeat(double.PositiveInfinity, b.Frames.Length + 1).ToArray(); previous[0] = 0;
        var current = new double[previous.Length];
        for (int i = 1; i <= a.Frames.Length; i++)
        {
            Array.Fill(current, double.PositiveInfinity);
            for (int j = Math.Max(1, i - 15); j <= Math.Min(b.Frames.Length, i + 15); j++)
            {
                double dot = 0; for (int k = 0; k < 20; k++) dot += a.Frames[i - 1][k] * b.Frames[j - 1][k];
                current[j] = 1 - Math.Clamp(dot, 0, 1) + Math.Min(previous[j - 1], Math.Min(previous[j], current[j - 1]));
            }
            (previous, current) = (current, previous);
        }
        return Math.Clamp(1 - previous[^1] / Math.Max(a.Frames.Length, b.Frames.Length)
            - .1 * Math.Abs(Math.Log((double)a.DurationMs / b.DurationMs)), 0, 1);
    }
    internal static (Guid Id, double Confidence)? Match(AcousticTemplate candidate, VoiceCommandSettings settings)
    {
        var scores = settings.Bindings.Where(binding => binding.Templates.Length != 0)
            .Select(binding => (binding.Id, binding.MinimumConfidence, Score: binding.Templates.Max(template => Similarity(candidate, template))))
            .OrderByDescending(item => item.Score).ToArray();
        if (scores.Length == 0 || scores[0].Score < scores[0].MinimumConfidence || (scores.Length > 1 && scores[0].Score - scores[1].Score < .05)) return null;
        return (scores[0].Id, scores[0].Score);
    }
}
