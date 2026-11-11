namespace EliteScriptStudio.Core;

/// <summary>Samples filtered physical input on the caller's monotonic clock.</summary>
public sealed class InputRecorder
{
    public const int MaxDurationMs = 15_000;
    private const int NeutralTailMs = 120;
    private const int StickIdleThreshold = 1600;
    private const int TriggerIdleThreshold = 3;
    private readonly Guid _id = Guid.NewGuid();
    private readonly RecordingChannels _channels;
    private readonly List<RecordedFrame> _frames = [];
    private long? _startedAt;
    private long? _endedAt;
    private int? _trailingIdleSince;

    public InputRecorder(RecordingChannels channels)
    {
        if (!RecordingFilter.IsValid(channels))
            throw new ArgumentOutOfRangeException(nameof(channels), "Select at least one recording channel.");
        _channels = channels;
    }

    public bool HasInput => _frames.Count != 0;
    public int ElapsedMs { get; private set; }

    /// <summary>Returns false when the capture limit is reached or the capture has stopped.</summary>
    public bool Capture(ControllerState input, long timestampMs)
    {
        if (_endedAt.HasValue)
            return false;
        var filtered = RecordingFilter.Overlay(_channels, default, input);
        bool active = IsActive(filtered);
        if (_startedAt is not long started)
        {
            if (!active)
                return true;
            _startedAt = started = timestampMs;
        }

        ElapsedMs = Math.Max(ElapsedMs, (int)Math.Clamp(timestampMs - started, 0, MaxDurationMs));
        if (ElapsedMs == MaxDurationMs)
        {
            _endedAt = started + MaxDurationMs;
            return false;
        }

        int time = ElapsedMs;
        _trailingIdleSince = active ? null : _trailingIdleSince ?? time;
        if (_frames.Count != 0 && _frames[^1].State == filtered)
            return true;
        if (_frames.Count != 0 && _frames[^1].TimeMs == time)
        {
            _frames[^1] = new(time, filtered);
            return true;
        }
        if (_frames.Count == MacroRecording.MaxFrames)
        {
            _endedAt = started + time;
            return false;
        }
        _frames.Add(new(time, filtered));
        return true;
    }

    public MacroRecording? Stop(string name, long timestampMs)
    {
        if (_startedAt is not long started || _frames.Count == 0)
        {
            _endedAt ??= timestampMs;
            return null;
        }

        _endedAt ??= started + Math.Max(ElapsedMs, Math.Clamp(timestampMs - started, 0, MaxDurationMs));
        int duration = Math.Clamp((int)(_endedAt.Value - started), _frames[^1].TimeMs + 1, MaxDurationMs);
        if (_trailingIdleSince is int idle)
            duration = Math.Min(duration, idle + NeutralTailMs);
        var recording = new MacroRecording(_id, name, _channels,
            [.. _frames.TakeWhile(frame => frame.TimeMs < duration)], duration);
        recording.Validate();
        return recording;
    }

    // Ignore small resting drift when locating the start and final pause. Once
    // armed by movement, capture the exact selected axis values.
    private static bool IsActive(ControllerState input) =>
        input.Buttons != 0 || input.LeftTrigger >= TriggerIdleThreshold || input.RightTrigger >= TriggerIdleThreshold
        || Math.Abs((int)input.LeftX) >= StickIdleThreshold || Math.Abs((int)input.LeftY) >= StickIdleThreshold
        || Math.Abs((int)input.RightX) >= StickIdleThreshold || Math.Abs((int)input.RightY) >= StickIdleThreshold;
}
