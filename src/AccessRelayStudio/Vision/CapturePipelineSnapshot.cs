namespace EliteScriptStudio.Vision;

/// <summary>Observed WGC deliveries and application loss counters; not a count of Xbox renders.</summary>
internal readonly record struct CapturePipelineSnapshot(long SampleTicks, long FrameCallbacks, long FramesDequeued,
    long FramesSuperseded, long FramesPublished, long FailedPublications, long PendingReplacements,
    long BufferUnavailable, double? DefaultMinimumUpdateIntervalMs, double? EffectiveMinimumUpdateIntervalMs);
