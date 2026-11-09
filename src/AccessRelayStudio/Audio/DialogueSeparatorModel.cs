using Microsoft.ML.OnnxRuntime;
using NAudio.Dsp;
using BitOperations = System.Numerics.BitOperations;

namespace EliteScriptStudio.Audio;

internal static class DialogueSeparatorModel
{
    internal const int SampleRate = 44_100;
    private const int Hop = 256;
    private const int SegmentSamples = 10 * SampleRate;
    private const int OverlapSamples = SampleRate / 2;
    private const int SpeechStem = 1;
    private static readonly AnalysisWindow[] Windows = [new(1024), new(2048), new(8192)];
    private static readonly string[] Inputs = ["mag_1024", "mag_2048", "mag_8192"];
    private static readonly string[] Outputs = ["mask_1024", "mask_2048", "mask_8192"];

    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "DialogueSeparation", "mrx.int8.onnx");
    internal static bool IsAvailable => File.Exists(ModelPath);

    internal static float[] Separate(float[] mono, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsAvailable)
            throw new InvalidOperationException("The bundled dialogue separation model is missing.");
        if (mono.Length <= Windows[^1].Size / 2)
            throw new InvalidDataException("The selection is too short for dialogue separation.");

        using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 4) };
        using var session = new InferenceSession(ModelPath, options);
        using var run = new RunOptions();
        using var cancelRun = cancellation.Register(() => run.Terminate = true);
        var result = new float[mono.Length];
        float gain = LoudnessGain(mono, cancellation);

        for (int start = 0; ; start += SegmentSamples - OverlapSamples)
        {
            int length = Math.Min(SegmentSamples, mono.Length - start);
            var segment = SeparateSegment(mono.AsSpan(start, length), gain, session, run, cancellation);
            int overlap = start == 0 ? 0 : OverlapSamples;
            for (int sample = 0; sample < overlap; sample++)
            {
                float weight = (sample + 1f) / (overlap + 1f);
                result[start + sample] += weight * (segment[sample] - result[start + sample]);
            }
            segment.AsSpan(overlap).CopyTo(result.AsSpan(start + overlap));
            if (start + length == mono.Length)
                return result;
        }
    }

    private static float[] SeparateSegment(ReadOnlySpan<float> samples, float gain, InferenceSession session,
        RunOptions run, CancellationToken cancellation)
    {
        int frames = samples.Length / Hop + 1;
        using var small = Windows[0].Measure(samples, frames, gain, cancellation);
        using var medium = Windows[1].Measure(samples, frames, gain, cancellation);
        using var large = Windows[2].Measure(samples, frames, gain, cancellation);
        cancellation.ThrowIfCancellationRequested();
        try
        {
            using var masks = session.Run(run, Inputs, [small, medium, large], Outputs);
            var result = new float[samples.Length];
            foreach (var (window, mask) in Windows.Zip(masks))
                window.AddSpeech(samples, frames, mask.GetTensorDataAsSpan<float>(), result, cancellation);
            return result;
        }
        catch (OnnxRuntimeException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation);
        }
    }

    // Match the reference inference's -27 LUFS input level with mono K-weighted gating.
    // Gain affects only the model magnitudes, leaving reconstructed audio at its original level.
    private static float LoudnessGain(float[] samples, CancellationToken cancellation)
    {
        const double offset = -0.691;
        const int stride = SampleRate / 10;
        const int block = 4 * stride;
        var shelf = BiQuadFilter.HighShelf(SampleRate, 1500, .7071068f, 4);
        var highPass = BiQuadFilter.HighPassFilter(SampleRate, 38, .5f);
        var energy = new double[(samples.Length + stride - 1) / stride];
        for (int index = 0; index < samples.Length; index++)
        {
            if ((index & 0x3fff) == 0)
                cancellation.ThrowIfCancellationRequested();
            float level = highPass.Transform(shelf.Transform(samples[index]));
            energy[index / stride] += (double)level * level / block;
        }

        double absoluteGate = Math.Pow(10, (-70 - offset) / 10);
        var blocks = new List<double>();
        for (int index = 0; index + 3 < energy.Length; index++)
        {
            double power = energy[index] + energy[index + 1] + energy[index + 2] + energy[index + 3];
            if (power >= absoluteGate)
                blocks.Add(power);
        }
        if (blocks.Count == 0)
            return 1;
        double relativeGate = blocks.Average() / 10;
        double mean = blocks.Where(power => power >= relativeGate).Average();
        double target = Math.Pow(10, (-27 - offset) / 10);
        return (float)Math.Clamp(Math.Sqrt(target / mean), .03, 30);
    }

    private sealed class AnalysisWindow(int size)
    {
        private readonly float[] _window = Enumerable.Range(0, size)
            .Select(index => .5f - .5f * MathF.Cos(2 * MathF.PI * index / size)).ToArray();
        private readonly int _exponent = BitOperations.Log2((uint)size);
        private readonly int _bins = size / 2 + 1;
        public int Size { get; } = size;

        public OrtValue Measure(ReadOnlySpan<float> samples, int frames, float gain, CancellationToken cancellation)
        {
            var fft = new Complex[Size];
            var magnitudes = new float[_bins * frames];
            // NAudio divides the forward FFT by N. MRX expects a 1/sqrt(N) scale.
            float scale = gain * MathF.Sqrt(Size);
            for (int frame = 0; frame < frames; frame++)
            {
                cancellation.ThrowIfCancellationRequested();
                Transform(samples, frame * Hop - Size / 2, fft);
                for (int bin = 0; bin < _bins; bin++)
                {
                    var value = fft[bin];
                    magnitudes[bin * frames + frame] = MathF.Sqrt(value.X * value.X + value.Y * value.Y) * scale;
                }
            }
            return OrtValue.CreateTensorValueFromMemory(magnitudes, [1, _bins, frames]);
        }

        public void AddSpeech(ReadOnlySpan<float> samples, int frames, ReadOnlySpan<float> masks,
            Span<float> result, CancellationToken cancellation)
        {
            int binsPerStem = _bins * frames;
            if (masks.Length != 3 * binsPerStem)
                throw new InvalidDataException("The dialogue model returned an unexpected mask size.");
            var speech = masks.Slice(SpeechStem * binsPerStem, binsPerStem);
            var fft = new Complex[Size];
            var reconstructed = new float[samples.Length];
            var normalization = new float[samples.Length];
            for (int frame = 0; frame < frames; frame++)
            {
                cancellation.ThrowIfCancellationRequested();
                int start = frame * Hop - Size / 2;
                Transform(samples, start, fft);
                for (int bin = 0; bin < _bins; bin++)
                {
                    float mask = speech[bin * frames + frame];
                    fft[bin].X *= mask;
                    fft[bin].Y *= mask;
                }
                for (int bin = _bins; bin < Size; bin++)
                {
                    fft[bin].X = fft[Size - bin].X;
                    fft[bin].Y = -fft[Size - bin].Y;
                }
                FastFourierTransform.FFT(false, _exponent, fft);
                int end = Math.Min(Size, samples.Length - start);
                for (int index = Math.Max(0, -start); index < end; index++)
                {
                    float weight = _window[index];
                    reconstructed[start + index] += fft[index].X * weight;
                    normalization[start + index] += weight * weight;
                }
            }
            for (int index = 0; index < samples.Length; index++)
                result[index] += reconstructed[index] / normalization[index];
        }

        private void Transform(ReadOnlySpan<float> samples, int start, Complex[] fft)
        {
            for (int index = 0; index < Size; index++)
            {
                int position = start + index;
                if (position < 0)
                    position = -position;
                else if (position >= samples.Length)
                    position = 2 * samples.Length - 2 - position;
                fft[index] = new Complex { X = samples[position] * _window[index] };
            }
            FastFourierTransform.FFT(true, _exponent, fft);
        }
    }
}
