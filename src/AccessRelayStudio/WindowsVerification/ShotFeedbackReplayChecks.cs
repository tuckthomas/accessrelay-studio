using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

internal static class ShotFeedbackReplayChecks
{
    internal static void Run(string library, string recording, string report)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(recording, "diagnostic.json")));
        var root = doc.RootElement;
        var settings = root.GetProperty("context").GetProperty("settings").Deserialize<VisionSettings>(json)!;
        var loader = new InstalledGamePlugins(Path.GetFullPath(library), _ => Path.GetDirectoryName(Path.GetFullPath(report))!);
        loader.Reload();
        var set = loader.FeedbackModels("nba2k27") ?? throw new InvalidDataException("The packaged OCR model set is missing.");
        var path = loader.ModelDirectory("nba2k27");
        CudaPreprocessingProbe.ConfigureOrt(Directory.GetParent(Path.GetFullPath(library))!.FullName);
        using var models = new VisionAnalysisModels(set, VisionExecutionProvider.Cpu, path, loadPose: false);
        try
        {
            using var bad = new VisionAnalysisModels(set with { TextDetectorSha256 = new string('0', 64) }, VisionExecutionProvider.Cpu, path, loadPose: false);
            throw new Exception("Incorrect detector hash was accepted.");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("text-det.onnx", StringComparison.Ordinal)) { }

        var sequence = new ShotFeedbackBannerSequence();
        var primary = ShotFeedbackAnalysis.BuildBannerSearchCrop(settings.TrainingCapture.FeedbackRegion);
        long frequency = root.GetProperty("stopwatchFrequency").GetInt64(), cursor = 0, nextOcr = 0, nextProbe = 0;
        long start = root.GetProperty("startedTicks").GetInt64();
        VisionActionReceipt? pending = null;
        long after = 0;
        FeedbackHeadingProbe? probe = null;
        int wipes = 0, inferences = 0;
        var reads = new List<(VisionActionReceipt Receipt, OcrFeedbackRead Read)>();
        var costs = new List<double>();
        void End(string reason)
        {
            if (pending is not { } r) return;
            reads.Add((r, new(r.Id, r.RunId, "unrecognized", "none", "none", "", 0, 0, Disposition: reason)));
            pending = null;
        }
        foreach (var frame in root.GetProperty("frames").EnumerateArray())
        {
            long ticks = frame.GetProperty("captureTicks").GetInt64();
            if (frame.TryGetProperty("lastAttempt", out var latest) && latest.ValueKind == JsonValueKind.Object
                && latest.GetProperty("id").GetInt64() > cursor && latest.GetProperty("completedTimestamp").GetInt64() <= ticks)
            {
                cursor = latest.GetProperty("id").GetInt64();
                if (latest.GetProperty("completedTimestamp").GetInt64() >= start)
                {
                    End("Superseded"); pending = latest.Deserialize<VisionActionReceipt>(json)!; after = pending.CompletedTimestamp;
                }
            }
            if (pending?.Outcome == VisionActionOutcome.Cancelled && frame.GetProperty("output").TryGetProperty("holdingCancelledTriggerShot", out var held) && held.GetBoolean())
                after = ticks;
            if (pending is not null && ticks - after > 3 * frequency) End("No fresh grade");
            if (!frame.TryGetProperty("file", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var file = Path.Combine(recording, name.GetString()!);
            int w = frame.GetProperty("width").GetInt32(), h = frame.GetProperty("height").GetInt32(), stride = frame.GetProperty("stride").GetInt32();
            if (probe is not null && ticks >= nextProbe)
            {
                if (probe.Cleared(Crop(file, w, h, stride, probe.Region, ticks))) { sequence.ConfirmClear(ticks - 1); wipes++; }
                nextProbe = ticks + frequency / 50;
            }
            if (ticks < nextOcr) continue;
            var crop = Crop(file, w, h, stride, primary, ticks);
            long began = Stopwatch.GetTimestamp();
            var lines = models.ReadText(crop.Pixels, crop.Width, crop.Height, crop.Stride);
            var banner = ShotFeedbackAnalysis.ParseFeedback(lines);
            var app = sequence.Observe(ticks, banner, ShotFeedbackAnalysis.HasHeading(lines), true);
            if (banner is not null && (probe is null || app is not null) && lines.FirstOrDefault(r => r.Confidence >= .8 && ShotFeedbackAnalysis.HasHeading([r])) is { } heading)
                probe = FeedbackHeadingProbe.Create(crop, primary, heading);
            double cost = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            costs.Add(cost); inferences++;
            if (pending is { } shot && app is { } fresh && fresh.StartedAt > after)
            {
                var b = fresh.Banner;
                reads.Add((shot, new(shot.Id, shot.RunId, b.ShotType, b.Timing, b.Tempo, b.RawText, ticks, cost, b.Distance, b.Coverage)));
                pending = null;
            }
            nextOcr = ticks + (long)((cost + (pending is null ? 90 : 10)) * frequency / 1000);
        }
        End("Recording ended");
        var trials = new List<object>();
        foreach (int range in new[] { 20, 25 })
        {
            double correction = settings.CorrectionMs;
            int direction = 0, tempo = 0, excellent = 0;
            var gate = new ShotFeedbackLearningGate();
            foreach (var (shot, read) in reads)
            {
                string? hold = read.ShotType == "timing" ? "Plain TIMING" : gate.Check(shot, read, "recorded-profile", range);
                double before = correction;
                if (hold is null)
                {
                    var step = ShotFeedbackAnalysis.ComputeAdaptiveFeedback(correction, settings.TempoCorrectionMs, read, false,
                        ref direction, ref tempo, ref excellent, confirmDirection: true, timingOnly: true);
                    correction = step.Timing.NextCorrectionMs; hold = step.Message;
                    if (step.Tempo.DeltaMs != 0) throw new Exception("Solo timing trial changed tempo.");
                }
                trials.Add(new { range, shot.ShotToken, before, correction, hold });
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { modelSet = set.Id, invalidHashRejected = true, inferences, wipes,
            medianOcrMs = costs.Order().ElementAt(costs.Count / 2), grades = reads.Select(r => new { r.Receipt.ShotToken, r.Receipt.Outcome, r.Read }), trials }, json));
        Console.WriteLine($"Read {reads.Count(r => r.Read.Disposition == null)} fresh grades over {reads.Count} receipts, {wipes} heading wipes. {report}");
    }

    private static CapturedCropFrame Crop(string file, int w, int h, int stride, VisionCrop crop, long ticks)
    {
        int x = (int)Math.Floor(crop.Left * w), y = (int)Math.Floor(crop.Top * h);
        int cw = Math.Clamp((int)Math.Ceiling((crop.Left + crop.Width) * w) - x, 1, w - x);
        int ch = Math.Clamp((int)Math.Ceiling((crop.Top + crop.Height) * h) - y, 1, h - y);
        var bytes = new byte[cw * ch * 4];
        using var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        var row = new byte[stride];
        for (int n = 0; n < y; n++) gzip.ReadExactly(row);
        for (int n = 0; n < ch; n++) { gzip.ReadExactly(row); row.AsSpan(x * 4, cw * 4).CopyTo(bytes.AsSpan(n * cw * 4)); }
        return new(bytes, cw, ch, cw * 4, ticks);
    }
}
