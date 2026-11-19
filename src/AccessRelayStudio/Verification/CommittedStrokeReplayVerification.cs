using System.Text.Json;
using EliteScriptStudio.Core;

internal static class CommittedStrokeReplayVerification
{
    internal static void Run(string directory, string output)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var reports = new List<object>();
        int recovered = 0, failures = 0;
        foreach (string path in Directory.GetFiles(directory, "attempt-*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var settings = root.GetProperty("settings").Deserialize<VisionSettings>(json)!;
            var action = root.GetProperty("predictor").GetProperty("action").Deserialize<VisionControllerAction>(json)!;
            long frequency = root.GetProperty("stopwatchFrequency").GetInt64();
            var arbiter = new ControllerOutputArbiter(frequency);
            arbiter.Configure(settings, action); arbiter.BeginSession();
            long timeline = 0, step = Math.Max(1, frequency / 500);
            ControllerState previous = default;
            var rows = new List<object>();
            foreach (var frame in root.GetProperty("frames").EnumerateArray())
            {
                var context = frame.GetProperty("physicalContext").Deserialize<VisionSignalContext>(json);
                var physical = context.Physical ?? default;
                var signal = frame.GetProperty("signal").Deserialize<VisionSignalObservation>(json)!;
                long now = Math.Max(frame.GetProperty("completedTicks").GetInt64(), context.InputTimestamp);
                if (timeline == 0)
                {
                    timeline = Math.Min(signal.CaptureTimestamp, context.HoldStartedTimestamp > 0 ? context.HoldStartedTimestamp : now) - step;
                    arbiter.Sample(default, default, true, true, false, timeline);
                    if (!arbiter.SetArmed(true)) throw new InvalidOperationException(arbiter.Status.Message);
                }
                while (timeline + step < now)
                {
                    timeline += step;
                    var input = context.HoldStartedTimestamp > 0 && timeline >= context.HoldStartedTimestamp ? physical : previous;
                    arbiter.Sample(input, input, true, true, false, timeline);
                }
                timeline = now;
                arbiter.Sample(physical, physical, true, true, false, now, context.HoldStartedTimestamp > 0 ? context.HoldStartedTimestamp : context.InputTimestamp);
                previous = physical;
                var before = arbiter.Status;
                bool accepted = false;
                if (signal.HasDecision)
                    accepted = arbiter.Submit(new(before.SessionGeneration, before.ShotToken,
                        signal.Sequence, signal.CaptureTimestamp, frame.GetProperty("completedTicks").GetInt64(),
                        signal.TimeToTargetMs ?? 0, signal.Confidence, signal.UncertaintyMs,
                        signal.Acquired && !signal.InvalidateAttempt)
                        { WithdrawPendingForecast = signal.WithdrawPendingForecast, ContinueCommittedAction = signal.ContinueCommittedAction && !signal.InvalidateAttempt }, now);
                string original = frame.GetProperty("output").GetProperty("message").GetString() ?? "";
                if (signal.TimeToTargetMs.HasValue
                    && ((original == "Prediction arrived after the allowed release deadline." && context.StrokeAnchorTimestamp > 0)
                        || original == "The complete stick stroke cannot start before its calibrated anchor."))
                {
                    failures++;
                    if (accepted) recovered++;
                }
                rows.Add(new { signal.Sequence, accepted, original, output = arbiter.Status, receipt = arbiter.LastAction });
            }
            reports.Add(new { file = Path.GetFileName(path), rows });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { scope = "Device-free replay of recorded observations and controller states; 2 ms simulated polling. No images or forecasts are invented and no output device is opened.",
            failures, recovered, reports }, json));
        if (failures == 0 || recovered != failures)
            throw new InvalidOperationException($"Recorded committed-stroke cancellation regression: {recovered}/{failures} formerly rejected valid updates recovered.");
        Console.WriteLine($"SUCCESS Recorded attempts: {recovered}/{failures} valid post-start forecasts recovered without changing stroke deadlines.");
    }
}
