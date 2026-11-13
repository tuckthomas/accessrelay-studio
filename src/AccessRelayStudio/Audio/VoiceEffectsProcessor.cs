using NAudio.Dsp;

namespace EliteScriptStudio.Audio;

/// <summary>Stateful mono DSP, owned exclusively by the audio render thread.</summary>
internal sealed class VoiceEffectsProcessor
{
    private const int Rate = VoiceSignal.SampleRate;
    private readonly Equalizer _pre = new(), _post = new();
    private readonly SpectralVoiceFilter _spectral = new();
    private readonly Delay _echo = new(Rate + 2), _modulation = new(4096), _dry = new(8192), _harmonyDry = new(4096);
    private readonly Delay[] _room = [new(1499), new(1601), new(1747), new(1867)];
    private readonly float[] _allpass = new float[4];
    private BiQuadFilter? _lowCut, _hum, _humHarmonic;
    private readonly BiQuadFilter _sibilance = BiQuadFilter.HighPassFilter(Rate, 4500, .707f);
    private readonly BiQuadFilter _nasal = BiQuadFilter.PeakingEQ(Rate, 1400, 2, 12);
    private double _lastCut = -1;
    private int _lastHum = -1;
    private float _envelope, _gate = 1, _compressor = 1, _deEssEnvelope, _underwater, _held;
    private double _phase, _robotPhase, _tremoloPhase, _gapPhase, _modulationPhase, _vibratoPhase, _animalPhase, _crushPhase;
    private int _animalSamples;
    private float _diodeInput, _diodeOutput;
    private VoiceProsody? _prosody;
    private uint _noise = 0x1234567;
    private float[] _original = [], _harmony = [], _detuned = [];
    private SmbPitchShifter? _harmonizer, _detuner;
    private VoiceEffects? _current;
    private double _smoothedMix = 1;
    private bool _bypassed;

    internal void Prepare(float[] samples, int count, VoiceEffects effects, bool muted = false)
    {
        if (_original.Length < count) _original = new float[count];
        samples.AsSpan(0, count).CopyTo(_original);
        if (muted || effects.Bypass || effects.Mix == 0)
        {
            if (!_bypassed)
            {
                _echo.Clear(); _dry.Clear(); _harmonyDry.Clear(); _modulation.Clear();
                foreach (var line in _room) line.Clear();
                _spectral.Reset(); _harmonizer = _detuner = null;
                _prosody = null; _animalSamples = 0; _diodeInput = _diodeOutput = 0;
                Array.Clear(_allpass); _envelope = _underwater = _deEssEnvelope = 0; _gate = _compressor = 1;
                _bypassed = true;
            }
            return;
        }
        _bypassed = false;
        if (!ReferenceEquals(_current, effects))
        {
            _pre.Update(effects.PreEqDb); _post.Update(effects.PostEqDb);
            if (_lastCut != effects.LowCutHz)
            {
                _lowCut = effects.LowCutHz > 0 ? BiQuadFilter.HighPassFilter(Rate, (float)effects.LowCutHz, .707f) : null;
                _lastCut = effects.LowCutHz;
            }
            if (_lastHum != effects.HumHz)
            {
                _hum = effects.HumHz == 0 ? null : BiQuadFilter.NotchFilter(Rate, effects.HumHz, 12);
                _humHarmonic = effects.HumHz == 0 ? null : BiQuadFilter.NotchFilter(Rate, effects.HumHz * 2, 12);
                _lastHum = effects.HumHz;
            }
            _current = effects;
        }
        float threshold = Db(effects.GateThresholdDb);
        for (int i = 0; i < count; i++)
        {
            float value = _pre.Transform(samples[i]);
            value = _lowCut?.Transform(value) ?? value;
            value = _hum?.Transform(value) ?? value;
            value = _humHarmonic?.Transform(value) ?? value;
            float magnitude = Math.Abs(value);
            _envelope += (magnitude - _envelope) * (magnitude > _envelope ? .01f : .0003f);
            float target = effects.GateThresholdDb <= -80 || _envelope > threshold * (_gate > .5 ? .75f : 1) ? 1 : 0;
            _gate += (target - _gate) * (target > _gate ? .02f : .0006f);
            samples[i] = value * _gate;
        }
    }

    internal void Process(float[] samples, int count, VoiceEffects effects, double pitchSemitones)
    {
        if (effects.Bypass || effects.Mix == 0) return;
        bool prosody = effects.Monotone > 0 || effects.Hypertone > 0;
        double correction = 0;
        if (prosody) correction = (_prosody ??= new()).Process(samples, count, effects.Monotone, effects.Hypertone);
        else _prosody = null;
        double formant = (effects.FormantSemitones - pitchSemitones - correction) * effects.FormantStrength;
        bool spectral = Math.Abs(formant) > .001 || effects.NoiseReduction > 0;
        if (spectral)
            for (int i = 0; i < count; i++) samples[i] = _spectral.Process(samples[i], formant, effects.NoiseReduction);
        else _spectral.Reset();
        if (effects.Harmony > 0)
        {
            Copy(samples, count, ref _harmony);
            (_harmonizer ??= new()).PitchShift((float)Math.Pow(2, effects.HarmonySemitones / 12), count, 2048, 8, Rate, _harmony);
        }
        else _harmonizer = null;
        if (effects.Detune > 0)
        {
            Copy(samples, count, ref _detuned);
            (_detuner ??= new()).PitchShift(1.0093f, count, 2048, 8, Rate, _detuned);
        }
        else _detuner = null;
        int dryDelay = (pitchSemitones != 0 ? 2048 : 0) + (spectral ? SpectralVoiceFilter.LatencySamples : 0)
            + (effects.Harmony > 0 || effects.Detune > 0 ? 2048 : 0) + (prosody ? 2048 : 0);
        float compressionThreshold = Db(effects.CompressorThresholdDb);
        float crusherSteps = (1 << (effects.BitDepth - 1)) - 1;
        for (int i = 0; i < count; i++)
        {
            float sample = samples[i];
            float original = _original[i];
            _dry.Write(original);
            float dry = _dry.Read(dryDelay);
            if (effects.Harmony > 0 || effects.Detune > 0)
            {
                _harmonyDry.Write(sample);
                sample = _harmonyDry.Read(2048);
                if (effects.Harmony > 0) sample = (sample + _harmony[i] * (float)effects.Harmony) / (1 + (float)effects.Harmony);
                if (effects.Detune > 0) sample = (sample + _detuned[i] * (float)effects.Detune) / (1 + (float)effects.Detune);
            }
            float absolute = Math.Abs(sample);
            float desiredCompression = effects.CompressorThresholdDb < 0 && absolute > compressionThreshold
                ? (float)Math.Pow(absolute / compressionThreshold, 1 / effects.CompressorRatio - 1) : 1;
            _compressor += (desiredCompression - _compressor) * (desiredCompression < _compressor ? .005f : .0002f);
            sample *= _compressor;
            float high = _sibilance.Transform(sample);
            _deEssEnvelope += (Math.Abs(high) - _deEssEnvelope) * .005f;
            if (effects.DeEsser > 0) sample *= 1 - (float)effects.DeEsser * Math.Clamp(_deEssEnvelope * 8, 0, .8f);
            if (effects.Nasal > 0) sample += (_nasal.Transform(sample) - sample) * (float)effects.Nasal;
            if (effects.Breathy > 0)
            {
                _noise ^= _noise << 13; _noise ^= _noise >> 17; _noise ^= _noise << 5;
                sample += ((float)_noise / uint.MaxValue * 2 - 1) * _envelope * (float)effects.Breathy * .45f;
            }
            _phase = Advance(_phase, 32);
            if (effects.Growl > 0) sample *= 1 - (float)effects.Growl * .65f * (1 + (float)Math.Sin(_phase));
            _robotPhase = Advance(_robotPhase, effects.RobotHz);
            if (effects.Robot > 0) sample *= 1 - (float)effects.Robot + (float)effects.Robot * (float)Math.Sin(_robotPhase);
            if (effects.Distortion > 0)
                sample += ((float)(Math.Tanh(sample * effects.Drive) / Math.Tanh(effects.Drive)) - sample) * (float)effects.Distortion;
            if (effects.Diode > 0)
            {
                float rectified = effects.OctaveUp ? Math.Abs(sample) * 2 : Math.Max(0, sample) * 2;
                float ac = rectified - _diodeInput + .995f * _diodeOutput;
                _diodeInput = rectified; _diodeOutput = ac;
                sample += (ac - sample) * (float)effects.Diode;
            }
            else _diodeInput = _diodeOutput = 0;
            _animalPhase = Advance(_animalPhase, 24);
            _animalSamples = _envelope > .008f ? Math.Min(_animalSamples + 1, Rate * 10) : 0;
            if (effects.Animal > 0 && _animalSamples > effects.AnimalDelayMs * Rate / 1000)
                sample *= 1 - (float)effects.Animal * .4f * (1 + (float)Math.Sin(_animalPhase));
            if (effects.BitDepth < 16 || effects.DownsampleHz < Rate)
            {
                _crushPhase += effects.DownsampleHz / (double)Rate;
                if (_crushPhase >= 1) { _crushPhase -= 1; _held = MathF.Round(sample * crusherSteps) / crusherSteps; }
                sample = _held;
            }
            _modulationPhase = Advance(_modulationPhase, effects.ChorusHz);
            _vibratoPhase = Advance(_vibratoPhase, 5);
            _modulation.Write(sample);
            if (effects.Vibrato > 0)
                sample = _modulation.Read(160 + 120 * effects.Vibrato * Math.Sin(_vibratoPhase));
            if (effects.Chorus > 0)
                sample = (sample + _modulation.Read(720 + 240 * Math.Sin(_modulationPhase)) * (float)effects.Chorus) / (1 + (float)effects.Chorus);
            if (effects.Phaser > 0)
            {
                float phased = sample;
                float coefficient = .25f + .65f * (float)(.5 + .5 * Math.Sin(_modulationPhase));
                for (int stage = 0; stage < _allpass.Length; stage++)
                {
                    float next = -coefficient * phased + _allpass[stage];
                    _allpass[stage] = phased + coefficient * next; phased = next;
                }
                sample += (phased - sample) * (float)effects.Phaser * .5f;
            }
            _tremoloPhase = Advance(_tremoloPhase, effects.TremoloHz);
            sample *= 1 - (float)effects.Tremolo * .5f * (1 + (float)Math.Sin(_tremoloPhase));
            _gapPhase = Advance(_gapPhase, effects.GapperHz);
            if (_gapPhase > Math.PI) sample *= 1 - (float)effects.Gapper;
            if (effects.Underwater > 0)
            {
                _underwater += (sample - _underwater) * (.02f + .08f * (float)(.5 + .5 * Math.Sin(_modulationPhase)));
                sample += (_underwater - sample) * (float)effects.Underwater;
            }
            if (effects.Echo > 0)
            {
                float delayed = _echo.Read(effects.EchoMs * Rate / 1000);
                _echo.Write(Math.Clamp(sample + delayed * (float)effects.EchoFeedback, -4, 4));
                sample += delayed * (float)effects.Echo;
            }
            else _echo.Clear();
            if (effects.Reverb > 0)
            {
                float reflected = 0;
                foreach (var line in _room)
                {
                    float delayed = line.Read(line.Capacity - 1);
                    line.Write(Math.Clamp(sample + delayed * (float)(.35 + effects.ReverbSize * .5), -4, 4));
                    reflected += delayed * .25f;
                }
                sample += reflected * (float)effects.Reverb;
            }
            else foreach (var line in _room) line.Clear();
            sample = _post.Transform(sample);
            _smoothedMix += (effects.Mix - _smoothedMix) * .003;
            samples[i] = float.IsFinite(sample) ? dry + (sample - dry) * (float)_smoothedMix : 0;
        }
    }

    private static void Copy(float[] input, int count, ref float[] destination)
    { if (destination.Length < count) destination = new float[count]; input.AsSpan(0, count).CopyTo(destination); }
    private static float Db(double value) => (float)Math.Pow(10, value / 20);
    private static double Advance(double phase, double hz) => (phase + Math.Tau * hz / Rate) % Math.Tau;

    private sealed class Equalizer
    {
        private static readonly float[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        private readonly BiQuadFilter?[] _filters = new BiQuadFilter?[10];
        private readonly double[] _gains = new double[10];
        public void Update(double[] gains)
        {
            for (int i = 0; i < 10; i++)
            {
                if (_gains[i] == gains[i]) continue;
                if (gains[i] == 0) _filters[i] = null;
                else if (_filters[i] is { } filter) filter.SetPeakingEq(Rate, Frequencies[i], 1.4f, (float)gains[i]);
                else _filters[i] = BiQuadFilter.PeakingEQ(Rate, Frequencies[i], 1.4f, (float)gains[i]);
                _gains[i] = gains[i];
            }
        }
        public float Transform(float sample)
        { foreach (var filter in _filters) if (filter is not null) sample = filter.Transform(sample); return sample; }
    }

    private sealed class Delay(int capacity)
    {
        private readonly float[] _samples = new float[capacity];
        private int _position;
        private bool _empty = true;
        public int Capacity => capacity;
        public void Write(float value) { _samples[_position] = value; _position = (_position + 1) % capacity; _empty = false; }
        public float Read(double distance)
        {
            double position = (_position - 1 - Math.Clamp(distance, 0, capacity - 2) + capacity * 2) % capacity;
            int first = (int)position;
            return _samples[first] + (_samples[(first + 1) % capacity] - _samples[first]) * (float)(position - first);
        }
        public void Clear() { if (!_empty) { Array.Clear(_samples); _empty = true; _position = 0; } }
    }
}

/// <summary>Voiced autocorrelation tracking controls pitch variation around a slowly learned centre.</summary>
internal sealed class VoiceProsody
{
    private readonly float[] _history = new float[512];
    private readonly double[] _correlation = new double[203];
    private readonly SmbPitchShifter _shifter = new();
    private int _head, _filled, _decimate;
    private double? _centre;
    private double _correction;
    public double Process(float[] samples, int count, double monotone, double hypertone)
    {
        for (int i = 0; i < count; i++)
        {
            if (++_decimate < 4) continue;
            _decimate = 0; _history[_head] = samples[i]; _head = (_head + 1) % _history.Length; _filled = Math.Min(_filled + 1, _history.Length);
        }
        double target = 0;
        if (_filled >= _history.Length)
        {
            for (int lag = 23; lag <= 201; lag++)
            {
                double cross = 0, left = 0, right = 0;
                for (int i = 0; i < 310; i++)
                {
                    double a = _history[(_head + i) % 512], b = _history[(_head + i + lag) % 512];
                    cross += a * b; left += a * a; right += b * b;
                }
                _correlation[lag] = left > 1e-6 && right > 1e-6 ? cross / Math.Sqrt(left * right) : 0;
            }
            for (int lag = 24; lag <= 200; lag++)
            {
                if (_correlation[lag] < .8 || _correlation[lag] < _correlation[lag - 1] || _correlation[lag] < _correlation[lag + 1]) continue;
                double curvature = _correlation[lag - 1] - 2 * _correlation[lag] + _correlation[lag + 1];
                double offset = Math.Abs(curvature) < 1e-8 ? 0 : .5 * (_correlation[lag - 1] - _correlation[lag + 1]) / curvature;
                double pitch = Math.Log2(12000 / (lag + Math.Clamp(offset, -.5, .5)));
                _centre ??= pitch;
                // The centre tracks natural register slowly; short variations can be flattened or expanded.
                _centre += (pitch - _centre.Value) * Math.Min(.04, count / 96000.0);
                target = Math.Clamp((_centre.Value - pitch) * 12 * (monotone - hypertone), -12, 12);
                break;
            }
        }
        _correction += (target - _correction) * .25;
        _shifter.PitchShift((float)Math.Pow(2, _correction / 12), count, 2048, 8, 48000, samples);
        return _correction;
    }
}

/// <summary>Overlap-add spectral envelope shifting and adaptive spectral attenuation.</summary>
internal sealed class SpectralVoiceFilter
{
    private const int Size = 1024, Hop = 256;
    internal const int LatencySamples = Size;
    private readonly float[] _input = new float[Size], _output = new float[Hop], _overlap = new float[Size];
    private readonly Complex[] _spectrum = new Complex[Size], _envelope = new Complex[Size];
    private readonly float[] _floor = new float[Size / 2 + 1];
    private readonly float[] _window = Enumerable.Range(0, Size).Select(i => (float)(.5 - .5 * Math.Cos(Math.Tau * i / Size))).ToArray();
    private int _rover = Size - Hop;
    private bool _active;
    public float Process(float sample, double shift, double reduction)
    {
        _active = true;
        _input[_rover] = sample;
        float result = _output[_rover - (Size - Hop)];
        if (++_rover != Size) return result;
        _rover = Size - Hop;
        for (int i = 0; i < Size; i++) _spectrum[i] = new() { X = _input[i] * _window[i] };
        FastFourierTransform.FFT(true, 10, _spectrum);
        for (int i = 0; i < Size; i++)
            _envelope[i] = new() { X = MathF.Log(Math.Max(1e-8f, MathF.Sqrt(_spectrum[i].X * _spectrum[i].X + _spectrum[i].Y * _spectrum[i].Y))) };
        // Cepstral liftering retains the broad vocal-tract envelope, rather than shifting harmonics.
        FastFourierTransform.FFT(false, 10, _envelope);
        for (int i = 24; i <= Size - 24; i++) _envelope[i] = default;
        FastFourierTransform.FFT(true, 10, _envelope);
        double ratio = Math.Pow(2, shift / 12);
        for (int i = 0; i <= Size / 2; i++)
        {
            double source = Math.Clamp(i / ratio, 0, Size / 2);
            int bin = (int)source;
            float target = _envelope[bin].X + (_envelope[Math.Min(bin + 1, Size / 2)].X - _envelope[bin].X) * (float)(source - bin);
            float gain = MathF.Exp(Math.Clamp(target - _envelope[i].X, -2, 2));
            float power = _spectrum[i].X * _spectrum[i].X + _spectrum[i].Y * _spectrum[i].Y;
            _floor[i] += (power - _floor[i]) * (power < _floor[i] ? .15f : .0005f);
            gain *= 1 - (float)reduction * Math.Clamp(_floor[i] * 2 / Math.Max(power, 1e-12f), 0, .9f);
            _spectrum[i].X *= gain; _spectrum[i].Y *= gain;
            if (i > 0 && i < Size / 2) _spectrum[Size - i] = new() { X = _spectrum[i].X, Y = -_spectrum[i].Y };
        }
        FastFourierTransform.FFT(false, 10, _spectrum);
        for (int i = 0; i < Size; i++) _overlap[i] += _spectrum[i].X * _window[i] / 1.5f;
        _overlap.AsSpan(0, Hop).CopyTo(_output);
        Array.Copy(_overlap, Hop, _overlap, 0, Size - Hop); Array.Clear(_overlap, Size - Hop, Hop);
        Array.Copy(_input, Hop, _input, 0, Size - Hop);
        return result;
    }
    public void Reset()
    {
        if (!_active) return;
        Array.Clear(_input); Array.Clear(_output); Array.Clear(_overlap); Array.Clear(_floor);
        _rover = Size - Hop; _active = false;
    }
}
