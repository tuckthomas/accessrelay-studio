using System.Diagnostics;

namespace EliteScriptStudio.Vision;

internal enum VisionDiagnosticKind { HistoryReset, Preprocessed, InferenceCompleted, ObservationRejected, PredictionReady, Stopped }
internal readonly record struct VisionDiagnosticEvent(long Timestamp, VisionDiagnosticKind Kind, long FrameSequence,
    long SessionGeneration, long ShotToken, double DurationMs, double Confidence, double UncertaintyMs,
    long CaptureTimestamp, double TimeToTargetMs);
internal readonly record struct VisionDiagnosticsSnapshot(long FramesSeen, long Inferences, long RejectedObservations,
    double LastPreprocessingMs, double LastInferenceWithTransfersMs, double LastPlayerConfidence,
    double LastShotConfidence, double LastUncertaintyMs, double LastPhase, string Status);

/// <summary>Bounded numeric telemetry. UI snapshots/export allocate outside the inference loop.</summary>
internal sealed class VisionDiagnostics
{
    private readonly object _gate = new();
    private readonly VisionDiagnosticEvent[] _events = new VisionDiagnosticEvent[512];
    private int _next, _count;
    private long _frames, _inferences, _rejected;
    private long _preprocessingSequence = -1, _inferenceSequence = -1;
    private double _preprocessing, _inference, _player, _shot, _uncertainty, _phase;
    private string _status = "Waiting for temporal history";

    internal void FrameSeen() { lock (_gate) _frames++; }
    internal void Record(VisionDiagnosticKind kind, long sequence, long generation, long shot,
        double duration = 0, double confidence = 0, double uncertainty = 0,
        long captureTimestamp = 0, double timeToTargetMs = 0)
    {
        lock (_gate)
        {
            _events[_next] = new(Stopwatch.GetTimestamp(), kind, sequence, generation, shot, duration, confidence, uncertainty,
                captureTimestamp, timeToTargetMs);
            _next = (_next + 1) % _events.Length;
            _count = Math.Min(_count + 1, _events.Length);
            if (kind == VisionDiagnosticKind.Preprocessed) { _preprocessing = duration; _preprocessingSequence = sequence; }
            if (kind == VisionDiagnosticKind.InferenceCompleted) _inferenceSequence = sequence;
            if (kind == VisionDiagnosticKind.ObservationRejected) _rejected++;
        }
    }

    internal void InferenceCompleted(double duration, double player, double shot, double uncertainty, double phase)
    {
        lock (_gate)
        {
            _inferences++;
            _inference = duration;
            _player = player;
            _shot = shot;
            _uncertainty = uncertainty;
            _phase = phase;
        }
    }

    internal void SetStatus(string status) { lock (_gate) _status = status; }
    internal void FrameTimings(long sequence, out double? preprocessingMs, out double? inferenceMs)
    {
        lock (_gate)
        {
            preprocessingMs = _preprocessingSequence == sequence ? _preprocessing : null;
            inferenceMs = _inferenceSequence == sequence ? _inference : null;
        }
    }
    internal VisionDiagnosticsSnapshot Snapshot()
    {
        lock (_gate) return new(_frames, _inferences, _rejected, _preprocessing, _inference,
            _player, _shot, _uncertainty, _phase, _status);
    }

    internal VisionDiagnosticEvent[] ExportEvents()
    {
        lock (_gate)
        {
            var copy = new VisionDiagnosticEvent[_count];
            int first = (_next - _count + _events.Length) % _events.Length;
            for (int index = 0; index < copy.Length; index++) copy[index] = _events[(first + index) % _events.Length];
            return copy;
        }
    }
}
