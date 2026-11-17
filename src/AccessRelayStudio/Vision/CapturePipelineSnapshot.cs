namespace EliteScriptStudio.Vision;

/// <summary>Observed source deliveries and application loss counters.</summary>
internal readonly record struct CapturePipelineSnapshot(long SampleTicks, long FrameCallbacks, long FramesDequeued,
    long FramesSuperseded, long FramesPublished, long FailedPublications, long PendingReplacements,
    long BufferUnavailable, double? DefaultMinimumUpdateIntervalMs, double? EffectiveMinimumUpdateIntervalMs)
{
    internal const string WindowReceipt = "wgc-local-receipt-qpc-v1";
    internal const string DecoderReceipt = "xhome-decoder-callback-qpc-v1";
    public string CaptureTimeBasis { get; init; } = WindowReceipt;
}
