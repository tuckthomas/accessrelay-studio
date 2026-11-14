namespace EliteScriptStudio.Core;

internal readonly record struct VisionPoint(double X, double Y);
internal sealed record VisionRegionObservation(long Sequence, bool Valid, VisionCrop Player, VisionCrop? Meter,
    VisionPoint[] Court, double Confidence, string Message);

/// <summary>The game adapter resolves regions; the host owns the frame lease and capture lifecycle.</summary>
internal interface IVisionRegionTracker
{
    VisionRegionObservation Track(ReadOnlySpan<byte> bgra, int width, int height, int stride, long sequence, long generation);
}
