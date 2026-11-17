namespace EliteScriptStudio.Vision;

internal readonly record struct CaptureStatus(bool IsRunning, string Message, long SourceGeneration,
    long LatestFrameTicks, long LatestSequence, long ContentChangedTicks, int Width, int Height, long DroppedFrames,
    CapturePipelineSnapshot? Pipeline = null);

/// <summary>The observation worker owns one source and disposes every acquired frame lease.</summary>
internal interface IVisionFrameSource : IDisposable
{
    WaitHandle FrameReady { get; }
    CaptureStatus Status { get; }
    bool TryAcquireLatest(out CapturedVisionFrame? frame);
}
