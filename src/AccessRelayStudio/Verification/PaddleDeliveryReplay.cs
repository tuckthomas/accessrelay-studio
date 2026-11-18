using System.Globalization;
using System.Text.Json;
using EliteScriptStudio.Core;

// Sensitivity check for a physical P4 attempt whose images are unavailable. The
// recorded visual decisions stay fixed. Only their delivery to the native arbiter moves.
internal static class PaddleDeliveryReplay
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Entry(long CaptureTicks, long CompletedTicks, VisionSignalContext PhysicalContext,
        VisionSignalObservation? Signal, bool SourceFresh, StreamTelemetryAssessment? StreamTelemetry);

    internal static void Run(string gainText, string output, string[] attempts)
    {
        double gain = double.Parse(gainText, CultureInfo.InvariantCulture);
        if (!double.IsFinite(gain) || gain < 0) throw new ArgumentOutOfRangeException(nameof(gainText));
        var results = new List<object>();
        foreach (string file in attempts)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            var input = root.GetProperty("frames").Deserialize<Entry[]>(Json)!;
            var settings = root.GetProperty("settings").Deserialize<VisionSettings>(Json)!;
            var action = root.GetProperty("predictor").GetProperty("action").Deserialize<VisionControllerAction>(Json)!;
            var receipt = root.GetProperty("lastAttempt").Deserialize<VisionActionReceipt>(Json)!;
            long frequency = root.GetProperty("stopwatchFrequency").GetInt64();
            var starts = input.Where(e => e.PhysicalContext.HeldPaddles.HasFlag(PaddleButtons.LargeRight))
                .Select(e => e.PhysicalContext.HoldStartedTimestamp).Where(t => t > 0).Distinct().ToArray();
            if (!action.HasAssignedTrigger || !action.IsTriggerHeld(default, PaddleButtons.LargeRight) || starts.Length != 1)
                throw new InvalidDataException("Use one recorded P4 hold per attempt.");
            long down = starts[0];
            var release = input.FirstOrDefault(e => e.PhysicalContext.InputTimestamp > down &&
                !e.PhysicalContext.HeldPaddles.HasFlag(PaddleButtons.LargeRight));
            long end = release?.PhysicalContext.InputTimestamp ?? input[^1].CompletedTicks;
            object Replay(double earlierMs)
            {
                var arbiter = new ControllerOutputArbiter(frequency);
                arbiter.Configure(settings, action); arbiter.BeginSession();
                long step = Math.Max(1, frequency / 500);
                long tick = Math.Min(input[0].CaptureTicks, down) - 2 * step;
                long? fullUp = null;
                arbiter.Sample(default, default, true, true, false, tick);
                if (!arbiter.SetArmed(true)) throw new InvalidOperationException(arbiter.Status.Message);
                ControllerState physical = default;
                void Poll(long through)
                {
                    for (; tick <= through; tick += step)
                    {
                        bool held = tick >= down && tick < end;
                        var sent = arbiter.Sample(physical, physical, true, true, false, tick, tick,
                            held ? PaddleButtons.LargeRight : PaddleButtons.None);
                        arbiter.RecordOutputSubmitted(sent, tick);
                        if (held && arbiter.StrokeAnchorTimestamp > 0 && sent.RightY == short.MaxValue) fullUp ??= tick;
                    }
                }
                foreach (var e in input)
                {
                    long now = Math.Max(e.CaptureTicks, e.CompletedTicks - (long)Math.Round(earlierMs * frequency / 1000));
                    Poll(now);
                    physical = e.PhysicalContext.Physical ?? default;
                    if (e.StreamTelemetry?.Direct is { } direct) arbiter.ObserveStreamTelemetry(direct);
                    var s = e.Signal; var before = arbiter.Status;
                    if (s == null || !e.PhysicalContext.Armed || !e.SourceFresh || before.ShotToken == 0 ||
                        !(s.TimeToTargetMs.HasValue || s.InvalidateAttempt || s.ContinueCommittedAction)) continue;
                    arbiter.Submit(new(before.SessionGeneration, before.ShotToken, s.Sequence, e.CaptureTicks, now, s.TimeToTargetMs ?? 0,
                        s.Confidence, s.UncertaintyMs, s.Acquired && !s.InvalidateAttempt)
                        { ContinueCommittedAction = s.ContinueCommittedAction && !s.InvalidateAttempt, TrackingFailure = s.Message }, now);
                }
                Poll(input[^1].CompletedTicks);
                var r = arbiter.LastAttempt;
                return new { fullUpFromPressMs = fullUp.HasValue ? (fullUp.Value - down) * 1000d / frequency : (double?)null,
                    anchorVsRecordedMs = r?.ActualAnchorTimestamp > 0 && receipt.AnchorTimestamp > 0
                        ? (r.ActualAnchorTimestamp - receipt.AnchorTimestamp) * 1000d / frequency : (double?)null,
                    outcome = r?.Outcome.ToString(), reason = r?.CancellationReason };
            }
            results.Add(new { file = Path.GetFileName(file), releaseObserved = release != null, allowedLatenessMs = settings.MaximumLatenessMs,
                originalLocalAnchorSlipMs = receipt.AnchorLatenessMs, original = Replay(0), earlierDelivery = Replay(gain) });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { gainMs = gain, results }, Json));
    }
}
