namespace EliteScriptStudio.Core;

internal sealed record AcousticUtterance(short[] Pcm, long OnsetTicks, long EndTicks);
/// <summary>20 ms PCM16/16 kHz framing, bounded 1.5 s utterances and 120 ms end silence.</summary>
internal sealed class AcousticUtteranceSegmenter(double minimumRms)
{
    private readonly Queue<(short[] Pcm, long Ticks)> _preRoll = new(2);
    private readonly List<short[]> _frames = new(75);
    private int _silent, _lastVoiced;
    private bool _discarding;
    private long _onset, _end;
    internal AcousticUtterance? Push(short[] pcm, long capturedTicks)
    {
        if (pcm.Length != 320) throw new InvalidDataException("Acoustic input requires exact 20 ms PCM16 frames.");
        double square = 0; foreach (short value in pcm) square += (double)value * value;
        bool voiced = Math.Sqrt(square / 320) / 32768 >= minimumRms;
        _silent = voiced ? 0 : _silent + 1;
        if (_discarding) { if (_silent >= 6) { _discarding = false; _preRoll.Clear(); } return null; }
        if (_frames.Count == 0 && !voiced)
        {
            if (_preRoll.Count == 2) _preRoll.Dequeue();
            _preRoll.Enqueue((pcm, capturedTicks)); return null;
        }
        if (_frames.Count == 0)
        {
            _onset = _preRoll.TryPeek(out var first) ? first.Ticks : capturedTicks;
            foreach (var frame in _preRoll) _frames.Add(frame.Pcm); _preRoll.Clear();
        }
        if (_frames.Count < 81) _frames.Add(pcm);
        if (voiced) { _lastVoiced = _frames.Count; _end = capturedTicks; }
        if (_lastVoiced > 75) { _frames.Clear(); _lastVoiced = 0; _discarding = true; return null; }
        if (_silent < 6) return null;
        AcousticUtterance? result = _lastVoiced >= 3 ? new(_frames.Take(_lastVoiced).SelectMany(frame => frame).ToArray(), _onset, _end) : null;
        _frames.Clear(); _lastVoiced = 0; _preRoll.Clear(); return result;
    }
}
