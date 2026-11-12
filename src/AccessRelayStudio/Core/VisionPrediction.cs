namespace EliteScriptStudio.Core;

/// <summary>All timestamps use the same monotonic Stopwatch clock. Tokens belong to the
/// observation, not to whichever session happens to be current when inference finishes.</summary>
internal readonly record struct VisionPrediction(long SessionGeneration, long ShotToken,
    long FrameSequence, long CaptureTimestamp, long CompletedTimestamp, double TimeToReleaseMs,
    double Confidence, double UncertaintyMs, bool PlayerTracked);

internal enum VisionRunState { Disabled, Observing, Ready, TrackingShot, ReleaseScheduled, ReleasedLatched, Suspended }

internal readonly record struct VisionStatusSnapshot(VisionRunState State, bool Armed, bool SourceReady,
    string Message, long SessionGeneration, long ShotToken, long ShotStartedTimestamp, bool ShootHeld,
    long? ReleaseDeadlineTimestamp, double LastConfidence, double LastUncertaintyMs,
    long AcceptedPredictions, long RejectedPredictions, long AutomatedReleases, long MissedDeadlines);
