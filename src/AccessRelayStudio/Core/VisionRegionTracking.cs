namespace EliteScriptStudio.Core;

public readonly record struct VisionPoint(double X, double Y);
public readonly record struct VisionKeypoint(double X, double Y, double Confidence);
public sealed record VisionPersonPose(VisionCrop Bounds, double Confidence, VisionKeypoint[] Keypoints);
public sealed record VisionTextRegion(string Text, double Confidence, VisionCrop Bounds);
public sealed record VisionCaptureModelSet(string Id, string PoseSha256, string TextDetectorSha256, string TextRecognizerSha256);
public interface IVisionAnalysisModels : IDisposable
{
    string ExecutionProvider { get; }
    VisionPersonPose[] DetectPeople(ReadOnlySpan<byte> bgra, int width, int height, int stride, VisionCrop? searchRegion = null);
    VisionTextRegion[] ReadText(ReadOnlySpan<byte> bgra, int width, int height, int stride);
    void RequestStop();
}
public sealed record VisionPlayerIdentity(string ExpectedTag, string ObservedTag, bool CaseSensitive, double Confidence, long VerifiedSequence, string RecognizedLine = "");
public sealed record VisionRegionObservation(long Sequence, bool Valid, VisionCrop Player, VisionCrop? Meter,
    VisionPoint[] Court, double Confidence, string Message,
    VisionPlayerIdentity? Identity = null, VisionKeypoint[]? Keypoints = null, VisionCrop? Feedback = null,
    bool MeterDetected = false, double CourtConfidence = 0, bool AcquisitionComplete = false, VisionCrop? BodyBounds = null, string CaptureBackend = "",
    VisionTextRegion[]? TextRegions = null, VisionPersonPose[]? DetectedPeople = null);

/// <summary>The game adapter resolves regions; the host owns the frame lease and capture lifecycle.</summary>
public interface IVisionRegionTracker : IDisposable
{
    VisionRegionObservation Track(ReadOnlySpan<byte> bgra, int width, int height, int stride, long sequence, long generation, long captureTicks = 0);
    void RequestStop() { }
    void IDisposable.Dispose() { }
}
