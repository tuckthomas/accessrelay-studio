using NAudio.Dsp;
using NAudio.Wave;
using System.Threading.Channels;

namespace EliteScriptStudio.Audio;

internal sealed class VoiceSignal(VoiceSettings settings, int bufferMilliseconds = 60, int primeMilliseconds = 20) : ISampleProvider
{
    internal const int SampleRate = 48_000;
    internal const int ReferenceBufferMilliseconds = 900;
    private const int FrameSize = 2048;
    private const int Overlap = 8;
    private readonly object _queueGate = new();
    private readonly Channel<byte> _samplesReady = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private float[] _queue = new float[SampleRate * bufferMilliseconds / 1000];
    private int _primeSamples = SampleRate * primeMilliseconds / 1000;
    private int _head;
    private int _queued;
    private bool _primed;
    private float _inputPeak;
    private float _outputPeak;
    private VoiceSettings _settings = settings;
    private VoicePreset? _preset;
    private BiQuadFilter[] _filters = [];
    private SmbPitchShifter? _pitchShifter;
    private float[] _work = [];
    private float? _gain;
    private readonly VoiceEffectsProcessor _effects = new();

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
    public float InputPeak => Volatile.Read(ref _inputPeak);
    public float OutputPeak => Volatile.Read(ref _outputPeak);

    public int BufferedSamples
    {
        get { lock (_queueGate) return _queued; }
    }
    public int BufferedMilliseconds => BufferedSamples * 1000 / SampleRate;

    internal ValueTask<byte> WaitForSamplesAsync(CancellationToken cancellation) => _samplesReady.Reader.ReadAsync(cancellation);

    internal bool TryReadBlock(float[] buffer) => Read(buffer, 0, buffer.Length, requireFullBlock: true) != 0;

    public void SetSettings(VoiceSettings value) => Volatile.Write(ref _settings, value);

    public void ReplaceWithReference(ReadOnlySpan<float> samples, int primeMilliseconds)
    {
        lock (_queueGate)
        {
            int capacity = SampleRate * ReferenceBufferMilliseconds / 1000;
            if (_queue.Length != capacity)
                _queue = new float[capacity];
            _head = _queued = 0;
            _primed = false;
            _primeSamples = SampleRate * primeMilliseconds / 1000;
            Enqueue(samples);
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;

        lock (_queueGate)
            Enqueue(samples);
    }

    private void Enqueue(ReadOnlySpan<float> samples)
    {
        int capacity = _queue.Length;
        if (samples.Length > capacity)
            samples = samples[^capacity..];
        int excess = Math.Max(0, _queued + samples.Length - capacity);
        _head = (_head + excess) % capacity;
        _queued -= excess;

        float peak = 0;
        int tail = (_head + _queued) % capacity;
        foreach (float sample in samples)
        {
            float level = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
            peak = Math.Max(peak, Math.Abs(level));
            _queue[tail] = level;
            if (++tail == capacity)
                tail = 0;
        }
        _queued += samples.Length;
        Volatile.Write(ref _inputPeak, Math.Max(peak, InputPeak * .84f));
        if (_queued != 0)
            _samplesReady.Writer.TryWrite(0);
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer, offset, count, requireFullBlock: false);

    private int Read(float[] buffer, int offset, int count, bool requireFullBlock)
    {
        if (_work.Length < count)
            _work = new float[count];

        int captured = Dequeue(_work.AsSpan(0, count), requireFullBlock);
        if (requireFullBlock && captured == 0)
            return 0;
        _work.AsSpan(captured, count - captured).Clear();
        if (captured > 0 && captured < count)
        {
            // Fade into silence when an endpoint falls behind instead of replaying stale samples.
            int fade = Math.Min(captured, 32);
            for (int index = 0; index < fade; index++)
                _work[captured - fade + index] *= (float)(fade - index - 1) / fade;
        }

        VoiceSettings config = Volatile.Read(ref _settings);
        bool bypass = config.Muted || config.Effects.Bypass || config.Effects.Mix == 0;
        if (config.Muted) _work.AsSpan(0, count).Clear();
        _effects.Prepare(_work, count, config.Effects, config.Muted);
        if (_preset != config.Preset)
        {
            _preset = config.Preset;
            _filters = CreateFilters(config.Preset);
        }

        double presetPitch = config.Preset switch
        {
            VoicePreset.Bright => 2.5,
            VoicePreset.Deep => -4,
            _ => 0
        };
        double semitones = bypass ? 0 : Math.Clamp(Finite(config.PitchSemitones) + presetPitch, -12, 12);
        if (semitones != 0)
        {
            _pitchShifter ??= new();
            float ratio = (float)Math.Pow(2, semitones / 12);
            _pitchShifter.PitchShift(ratio, count, FrameSize, Overlap, SampleRate, _work);
        }
        else
        {
            _pitchShifter = null;
        }

        if (!bypass)
        {
            for (int index = 0; index < count; index++)
                foreach (var filter in _filters) _work[index] = filter.Transform(_work[index]);
            _effects.Process(_work, count, config.Effects, semitones);
        }

        float targetGain = (float)Math.Pow(10, Math.Clamp(Finite(config.GainDb), -24, 12) / 20);
        float gain = _gain ?? targetGain;
        float outputPeak = 0;
        for (int index = 0; index < count; index++)
        {
            float sample = _work[index];
            gain += (targetGain - gain) * .003f;
            float output = config.Muted ? 0 : SoftLimit(sample * gain);
            buffer[offset + index] = output;
            outputPeak = Math.Max(outputPeak, Math.Abs(output));
        }
        _gain = gain;
        Volatile.Write(ref _outputPeak, config.Muted ? 0 : Math.Max(outputPeak, OutputPeak * .84f));
        return count;
    }

    private int Dequeue(Span<float> destination, bool requireFullBlock)
    {
        lock (_queueGate)
        {
            if (requireFullBlock && _queued < destination.Length)
                return 0;
            if (!_primed)
            {
                if (_queued < Math.Min(_queue.Length, Math.Max(_primeSamples, destination.Length)))
                    return 0;
                _primed = true;
            }

            int count = Math.Min(_queued, destination.Length);
            int first = Math.Min(count, _queue.Length - _head);
            _queue.AsSpan(_head, first).CopyTo(destination);
            _queue.AsSpan(0, count - first).CopyTo(destination[first..]);
            _head = (_head + count) % _queue.Length;
            _queued -= count;
            _primed = count == destination.Length;
            return count;
        }
    }

    private static BiQuadFilter[] CreateFilters(VoicePreset preset) => preset switch
    {
        VoicePreset.Warm => [BiQuadFilter.HighPassFilter(SampleRate, 65, .707f),
            BiQuadFilter.LowShelf(SampleRate, 280, .75f, 3), BiQuadFilter.HighShelf(SampleRate, 4100, .75f, -3)],
        VoicePreset.Bright => [BiQuadFilter.HighPassFilter(SampleRate, 100, .707f),
            BiQuadFilter.HighShelf(SampleRate, 2600, .75f, 3)],
        VoicePreset.Deep => [BiQuadFilter.HighPassFilter(SampleRate, 55, .707f),
            BiQuadFilter.LowShelf(SampleRate, 230, .75f, 3), BiQuadFilter.HighShelf(SampleRate, 4600, .75f, -2)],
        VoicePreset.Radio => [BiQuadFilter.HighPassFilter(SampleRate, 300, .707f),
            BiQuadFilter.LowPassFilter(SampleRate, 3200, .707f)],
        _ => []
    };

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static float SoftLimit(float value)
    {
        const float threshold = .85f;
        const float headroom = .13f;
        float excess = Math.Abs(value) - threshold;
        if (excess <= 0)
            return value;

        return MathF.CopySign(threshold + headroom * excess / (headroom + excess), value);
    }
}
