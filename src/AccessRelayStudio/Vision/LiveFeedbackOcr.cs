using System.Diagnostics;
using System.Text;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

internal sealed record OcrFeedbackRead(long ReceiptId, string RunId, string ShotType, string Timing, string Tempo,
    string RawText, long CaptureTicks, double ProcessingMs, string? Distance = null, string? Coverage = null);

internal sealed record AdaptiveTimingStep(double PreviousCorrectionMs, double NextCorrectionMs, double DeltaMs, string Reason);

internal sealed record AdaptiveFeedbackResult(AdaptiveTimingStep Timing, AdaptiveTimingStep Tempo, bool TimingClampedSkipped,
    bool Changed, string Message);

/// <summary>Reads the post-shot HUD feedback banner asynchronously on captured crop frames so the
/// 60 Hz meter tracker and release scheduler stay untouched.</summary>
internal sealed class LiveFeedbackOcr : IDisposable
{
    private readonly string _modelDirectory;
    private readonly VisionCaptureModelSet _modelSet;
    private readonly Action<OcrFeedbackRead> _onFeedback;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private VisionAnalysisModels? _models;
    private string _lastRunId = "";
    private long _lastReceiptId;
    private VisionCrop? _discoveredCrop;
    private int _discoveredMisses;
    private string _priorBannerText = "";
    private long _priorBannerAt;

    internal LiveFeedbackOcr(string modelDirectory, VisionCaptureModelSet modelSet, Action<OcrFeedbackRead> onFeedback)
    {
        _modelDirectory = modelDirectory;
        _modelSet = modelSet;
        _onFeedback = onFeedback;
    }

    internal void Start(Func<VisionSession?> getSession, Func<StudioRuntime?> getRuntime, Func<VisionSettings> getSettings)
    {
        lock (_gate)
        {
            if (_worker is { IsCompleted: false }) return;
            if (getRuntime()?.LastVisionAction is { } existing)
            {
                _lastRunId = existing.RunId;
                _lastReceiptId = existing.Id;
            }
            _cts?.Dispose();
            _cts = new();
            var ct = _cts.Token;
            _worker = Task.Run(() => RunLoopAsync(getSession, getRuntime, getSettings, ct), ct);
        }
    }

    internal void Stop()
    {
        Task? worker;
        lock (_gate)
        {
            _cts?.Cancel();
            worker = _worker;
            _models?.RequestStop();
        }
        try { worker?.Wait(TimeSpan.FromSeconds(1)); } catch { /* cancellation / shutdown timeout */ }
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _worker = null;
        }
    }

    private async Task RunLoopAsync(Func<VisionSession?> getSession, Func<StudioRuntime?> getRuntime,
        Func<VisionSettings> getSettings, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                var session = getSession();
                var runtime = getRuntime();
                if (session is null || runtime is null || !session.Status.Running) continue;
                var receipt = runtime.LastVisionAction;
                if (receipt is null || (receipt.RunId == _lastRunId && receipt.Id == _lastReceiptId)) continue;
                _lastRunId = receipt.RunId;
                _lastReceiptId = receipt.Id;

                var settings = getSettings();
                if (!ShouldRead(settings)) continue;
                // Only inspect shots triggered by a bound paddle action (never manual stick/button shots).
                if (runtime.VisionAction.TriggerPaddle == PaddleButtons.None) continue;

                // Ensure OCR reader models are loaded lazily on this background worker (CPU-only, skip pose).
                _models ??= new(_modelSet, VisionExecutionProvider.Cpu, _modelDirectory, loadPose: false);

                var result = await WatchShotFeedbackAsync(session, receipt, settings,
                    () => getRuntime()?.LastVisionAction?.Id ?? 0, ct).ConfigureAwait(false);
                if (result is not null && (getRuntime()?.LastVisionAction?.Id ?? 0) == receipt.Id)
                    _onFeedback(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* Never crash live session on background OCR read */ }
        }
    }

    private static bool ShouldRead(VisionSettings settings)
    {
        if (settings.PluginAssistance is not { } json) return false;
        if (json.TryGetProperty("enabled", out var en) && !en.GetBoolean()) return false;
        if (json.TryGetProperty("autoOcrTiming", out var auto) && auto.ValueKind is System.Text.Json.JsonValueKind.False)
            return false;
        return true;
    }

    private async Task<OcrFeedbackRead?> WatchShotFeedbackAsync(VisionSession session, VisionActionReceipt receipt,
        VisionSettings settings, Func<long> getLatestReceiptId, CancellationToken ct)
    {
        double elapsedSinceCompleteMs = Stopwatch.GetElapsedTime(receipt.CompletedTimestamp).TotalMilliseconds;
        if (elapsedSinceCompleteMs < 180)
            await Task.Delay((int)Math.Ceiling(180 - elapsedSinceCompleteMs), ct).ConfigureAwait(false);

        var preferredSourceCrop = _discoveredCrop ?? settings.TrainingCapture.FeedbackRegion;
        var crop = BuildBannerSearchCrop(preferredSourceCrop);
        bool sawClearedSincePrevious = _priorBannerAt == 0 ||
            Stopwatch.GetElapsedTime(_priorBannerAt, receipt.CompletedTimestamp).TotalMilliseconds >= 3500;

        for (int attempt = 0; attempt < 14 && !ct.IsCancellationRequested; attempt++)
        {
            if (getLatestReceiptId() != receipt.Id) return null;
            if (Stopwatch.GetElapsedTime(receipt.CompletedTimestamp).TotalMilliseconds > 2600) break;
            // Cycle across configured/discovered crop, top band, middle above-player band (y=0.24..0.72), and lower band
            // so custom HUD or above-player banners are never stranded in a vertical blind spot.
            var activeCrop = SelectSearchCrop(attempt, crop);
            var frame = await session.CaptureRegionOnceAsync(activeCrop, ct).ConfigureAwait(false);
            if (frame is null || getLatestReceiptId() != receipt.Id) return null;

            long t0 = Stopwatch.GetTimestamp();
            var regions = _models!.ReadText(frame.Pixels, frame.Width, frame.Height, frame.Stride);
            double procMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var parsed = ParseFeedback(regions);
            if (parsed is null)
            {
                // Only scanning the primary banner crop proves the previous shot's banner disappeared.
                if (activeCrop == crop)
                    sawClearedSincePrevious = true;
            }
            else
            {
                // Previous-shot banners commonly remain visible 750-950 ms into quick follow-up shots.
                if (!sawClearedSincePrevious && parsed.RawText == _priorBannerText
                    && Stopwatch.GetElapsedTime(receipt.CompletedTimestamp).TotalMilliseconds < 1350)
                {
                    await Task.Delay(170, ct).ConfigureAwait(false);
                    continue;
                }
                if (parsed.BannerBounds is { } localBounds)
                {
                    _discoveredCrop = ProjectCrop(activeCrop, localBounds);
                    _discoveredMisses = 0;
                }
                _priorBannerText = parsed.RawText;
                _priorBannerAt = frame.CaptureTicks;
                return new(receipt.Id, receipt.RunId, parsed.ShotType, parsed.Timing, parsed.Tempo,
                    parsed.RawText, frame.CaptureTicks, procMs, parsed.Distance, parsed.Coverage);
            }
            await Task.Delay(175, ct).ConfigureAwait(false);
        }
        if (_discoveredCrop is not null && ++_discoveredMisses >= 2)
        {
            _discoveredCrop = null;
            _discoveredMisses = 0;
        }
        return null;
    }

    internal static VisionCrop SelectSearchCrop(int attempt, VisionCrop configuredOrDiscovered) =>
        attempt <= 1 ? configuredOrDiscovered
        : (attempt % 4) switch
        {
            2 => new VisionCrop(0, 0, 1, .42),
            3 => new VisionCrop(0, .24, 1, .48),
            1 => new VisionCrop(.10, .54, .80, .46),
            _ => configuredOrDiscovered
        };

    internal static VisionCrop BuildBannerSearchCrop(VisionCrop? savedFeedback)
    {
        if (savedFeedback is { } r)
        {
            double cx = r.Left + r.Width / 2;
            double w = Math.Clamp(Math.Max(r.Width + .16, .42), .30, 1);
            double l = Math.Clamp(cx - w / 2, 0, 1 - w);
            double top = Math.Clamp(r.Top - .03, 0, .62);
            // Text detector drops lines > 0.12 of crop height, so keep crop height >= 0.36.
            double h = Math.Clamp(Math.Max(r.Height + .14, .36), .36, 1 - top);
            if (top + h > 1) top = Math.Max(0, 1 - h);
            return new(l, top, w, h);
        }
        return new(.22, 0, .56, .38);
    }

    private static VisionCrop ProjectCrop(VisionCrop parent, VisionCrop child) =>
        new(Math.Clamp(parent.Left + child.Left * parent.Width, 0, 1),
            Math.Clamp(parent.Top + child.Top * parent.Height, 0, 1),
            Math.Clamp(child.Width * parent.Width, .01, 1),
            Math.Clamp(child.Height * parent.Height, .01, 1));

    internal sealed record ParsedBanner(string ShotType, string Timing, string Tempo, string RawText, VisionCrop? BannerBounds,
        string? Distance = null, string? Coverage = null);

    internal static ParsedBanner? ParseFeedback(VisionTextRegion[] regions)
    {
        if (regions.Length == 0) return null;
        var lines = regions
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .OrderBy(r => r.Bounds.Top)
            .ThenBy(r => r.Bounds.Left)
            .Select(r => (Raw: CleanOcrLine(r.Text), Match: NormalizeOcrLine(r.Text), r.Bounds))
            .Where(l => l.Raw.Length > 0)
            .ToArray();
        if (lines.Length == 0) return null;

        string joinedRaw = string.Join(" | ", lines.Select(l => l.Raw));
        string joinedMatch = string.Join(" | ", lines.Select(l => l.Match));
        string shotType = "";
        if (ContainsKeyword(joinedMatch, "RHYTHM") || ContainsKeyword(joinedMatch, "TEMPO")) shotType = "rhythm";
        else if (ContainsKeyword(joinedMatch, "TIMING")) shotType = "timing";

        string timing = "";
        string tempo = "none";
        int excellentCount = 0;

        // First scan lines tagged with timing/rhythm or unmarked feedback lines,
        // separating TEMPO grade if present.
        var feedbackLines = lines.Where(l => IsFeedbackToken(l.Match, l.Raw)).ToArray();
        if (feedbackLines.Length == 0) return null;

        foreach (var line in feedbackLines)
        {
            if (ParseTempo(line.Match) is { } parsedTempo)
            {
                if (tempo == "none") tempo = parsedTempo;
                continue;
            }
            if (ParseTiming(line.Match) is { } parsedTiming)
            {
                if (parsedTiming == "excellent") excellentCount++;
                if (timing == "") timing = parsedTiming;
                else if (parsedTiming == "excellent" && tempo == "none" && shotType == "rhythm")
                    tempo = "excellent";
            }
        }

        // If "TIMING" and "TEMPO" headings didn't explicitly contain the grade on the same line, check joined text.
        if (timing == "")
            timing = ParseTiming(joinedMatch) ?? "";
        if (timing == "") return null;

        if (shotType == "" && tempo != "none") shotType = "rhythm";
        if (shotType == "") shotType = ContainsKeyword(joinedMatch, "TEMPO") ? "rhythm" : "rhythm";
        if (shotType == "timing") tempo = "none";
        if (excellentCount >= 2 && shotType == "rhythm" && tempo == "none") tempo = "excellent";

        var (distance, coverage) = ExtractShotContext(lines);
        double minL = feedbackLines.Min(l => l.Bounds.Left);
        double minT = feedbackLines.Min(l => l.Bounds.Top);
        double maxR = feedbackLines.Max(l => l.Bounds.Left + l.Bounds.Width);
        double maxB = feedbackLines.Max(l => l.Bounds.Top + l.Bounds.Height);
        var bounds = new VisionCrop(Math.Clamp(minL, 0, 1), Math.Clamp(minT, 0, 1),
            Math.Clamp(maxR - minL, .01, 1), Math.Clamp(maxB - minT, .01, 1));
        return new(shotType, timing, tempo, joinedRaw, bounds, distance, coverage);
    }

    internal static AdaptiveTimingStep ComputeAdaptiveStep(double currentCorrectionMs, string timing, ref int consecutiveDirection)
    {
        int dir = timing switch
        {
            "early" or "slightlyEarly" => 1,
            "late" or "slightlyLate" => -1,
            _ => 0
        };
        bool slight = timing is "slightlyEarly" or "slightlyLate";
        return ComputeAdaptiveStepCore(currentCorrectionMs, dir, slight, ref consecutiveDirection,
            firstMagnitude: slight ? 1.5 : 2.5, repeatedMagnitude: slight ? 2.5 : 4.0, -100, 100,
            unchangedReason: "Excellent timing confirmed; correction unchanged.",
            stepReason: (prev, next) => $"OCR {timing}: timing correction {FormatMs(prev)} → {FormatMs(next)} ms.");
    }

    internal static AdaptiveTimingStep ComputeAdaptiveTempoStep(double currentTempoCorrectionMs, string tempo, ref int consecutiveDirection)
    {
        int dir = tempo switch
        {
            "fast" or "slightlyFast" => 1,
            "slow" or "slightlySlow" => -1,
            _ => 0
        };
        bool slight = tempo is "slightlyFast" or "slightlySlow";
        return ComputeAdaptiveStepCore(currentTempoCorrectionMs, dir, slight, ref consecutiveDirection,
            firstMagnitude: slight ? 2.0 : 4.0, repeatedMagnitude: slight ? 3.0 : 6.0, -100, 100,
            unchangedReason: "Tempo unchanged.",
            stepReason: (prev, next) => $"tempo correction {FormatMs(prev)} → {FormatMs(next)} ms.");
    }

    internal static AdaptiveFeedbackResult ComputeAdaptiveFeedback(double currentCorrectionMs, double currentTempoCorrectionMs,
        OcrFeedbackRead read, bool strokeClamped, ref int consecutiveTimingDir, ref int consecutiveTempoDir)
    {
        bool suppressLateClamp = strokeClamped && read.Timing is "late" or "slightlyLate";
        AdaptiveTimingStep timingStep;
        if (suppressLateClamp)
        {
            consecutiveTimingDir = 0;
            timingStep = new(currentCorrectionMs, currentCorrectionMs, 0,
                "Timing correction unchanged (stroke started behind schedule).");
        }
        else
        {
            timingStep = ComputeAdaptiveStep(currentCorrectionMs, read.Timing, ref consecutiveTimingDir);
        }

        var tempoStep = read.ShotType == "timing" || read.Tempo is "none" or ""
            ? new AdaptiveTimingStep(currentTempoCorrectionMs, currentTempoCorrectionMs, 0, "Tempo unchanged.")
            : ComputeAdaptiveTempoStep(currentTempoCorrectionMs, read.Tempo, ref consecutiveTempoDir);

        bool timingMoved = timingStep.DeltaMs != 0;
        bool tempoMoved = tempoStep.DeltaMs != 0;
        string summary;
        if (!timingMoved && !tempoMoved)
        {
            summary = suppressLateClamp
                ? timingStep.Reason
                : timingStep.Reason;
        }
        else
        {
            var parts = new List<string>(2);
            if (timingMoved)
                parts.Add($"timing {FormatMs(timingStep.PreviousCorrectionMs)} → {FormatMs(timingStep.NextCorrectionMs)} ms");
            else if (suppressLateClamp)
                parts.Add("timing held (stroke started late)");
            if (tempoMoved)
                parts.Add($"tempo {FormatMs(tempoStep.PreviousCorrectionMs)} → {FormatMs(tempoStep.NextCorrectionMs)} ms");
            string tag = read.Tempo is not ("none" or "") && read.Tempo != read.Timing
                ? $"{read.Tempo} {read.Timing}"
                : read.Timing;
            summary = $"OCR {tag}: {string.Join("; ", parts)}.";
        }
        return new(timingStep, tempoStep, suppressLateClamp, timingMoved || tempoMoved, summary);
    }

    private static AdaptiveTimingStep ComputeAdaptiveStepCore(double current, int dir, bool _, ref int consecutiveDirection,
        double firstMagnitude, double repeatedMagnitude, double min, double max, string unchangedReason,
        Func<double, double, string> stepReason)
    {
        if (dir == 0)
        {
            consecutiveDirection = 0;
            return new(current, current, 0, unchangedReason);
        }
        consecutiveDirection = (Math.Sign(consecutiveDirection) == dir)
            ? consecutiveDirection + dir
            : dir;
        double magnitude = Math.Abs(consecutiveDirection) >= 2 ? repeatedMagnitude : firstMagnitude;
        double next = Math.Clamp(Math.Round((current + dir * magnitude) * 2) / 2, min, max);
        return new(current, next, next - current, stepReason(current, next));
    }

    private static string FormatMs(double value) => value.ToString("+0.#;-0.#;0");

    private static string CleanOcrLine(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Normalize(NormalizationForm.FormKC).ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '.' or '\'') sb.Append(ch);
            else if (ch is '-' or '_' or '/' or ':') sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeOcrLine(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Normalize(NormalizationForm.FormKC).ToUpperInvariant())
        {
            if (char.IsAsciiLetter(ch) || ch == ' ') sb.Append(ch);
            else if (ch is '0') sb.Append('O');
            else if (ch is '1' or '|') sb.Append('I');
            else if (ch is '5') sb.Append('S');
            else if (ch is '-' or '_' or '/' or ':') sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsKeyword(string text, string keyword)
        => text.Contains(keyword, StringComparison.Ordinal);

    private static bool IsFeedbackToken(string matchLine, string rawLine) =>
        matchLine.Contains("RHYTHM", StringComparison.Ordinal) || matchLine.Contains("TIMING", StringComparison.Ordinal)
        || matchLine.Contains("TEMPO", StringComparison.Ordinal) || matchLine.Contains("DISTANCE", StringComparison.Ordinal)
        || ParseTiming(matchLine) is not null || ParseTempo(matchLine) is not null
        || ParseCoverage(matchLine) is not null || ExtractDistanceToken(rawLine) is not null;

    private static (string? Distance, string? Coverage) ExtractShotContext((string Raw, string Match, VisionCrop Bounds)[] lines)
    {
        string? distance = null, coverage = null;
        bool sawDistanceHeading = lines.Any(l => ContainsKeyword(l.Match, "DISTANCE"));
        for (int i = 0; i < lines.Length; i++)
        {
            coverage ??= ParseCoverage(lines[i].Match);
            distance ??= ExtractDistanceToken(lines[i].Raw, allowBareNumber: sawDistanceHeading && (i > 0 && ContainsKeyword(lines[i - 1].Match, "DISTANCE") || (lines[i].Raw.All(ch => char.IsAsciiDigit(ch) || ch == '\'' || ch == ' ') && lines[i].Raw.Length <= 4)));
        }
        return (distance, coverage);
    }

    private static string? ExtractDistanceToken(string raw, bool allowBareNumber = false)
    {
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string tok = tokens[i].TrimEnd('\'', '.');
            if (tok.EndsWith("FT", StringComparison.Ordinal) && tok.Length > 2 && int.TryParse(tok[..^2], out int attachedFt) && attachedFt is >= 1 and <= 94)
                return $"{attachedFt} ft";
            if (int.TryParse(tok, out int feet) && feet is >= 1 and <= 94)
            {
                bool hasFt = (i + 1 < tokens.Length && tokens[i + 1].StartsWith("FT", StringComparison.Ordinal)) || tokens[i].EndsWith('\'');
                if (hasFt || allowBareNumber) return $"{feet} ft";
            }
        }
        return null;
    }

    private static string? ParseCoverage(string text)
    {
        if (text.Contains("WIDE OPEN", StringComparison.Ordinal)) return "Wide Open";
        if (text.Contains("LIGHTLY CONTESTED", StringComparison.Ordinal)) return "Lightly Contested";
        if (text.Contains("SMOTHERED", StringComparison.Ordinal)) return "Smothered";
        if (text.Contains("TIGHT", StringComparison.Ordinal)) return "Tight";
        if (text.Contains("CONTESTED", StringComparison.Ordinal)) return "Contested";
        if (text.Contains("OPEN", StringComparison.Ordinal)) return "Open";
        return null;
    }

    private static string? ParseTiming(string text)
    {
        if (text.Contains("SLIGHTLY EARLY", StringComparison.Ordinal) || text.Contains("SLIGHTLYEAR", StringComparison.Ordinal))
            return "slightlyEarly";
        if (text.Contains("SLIGHTLY LATE", StringComparison.Ordinal) || text.Contains("SLIGHTLYLAT", StringComparison.Ordinal))
            return "slightlyLate";
        if (text.Contains("EXCELLENT", StringComparison.Ordinal) || text.Contains("GREEN", StringComparison.Ordinal))
            return "excellent";
        if (text.Contains("EARLY", StringComparison.Ordinal))
            return "early";
        if (text.Contains("LATE", StringComparison.Ordinal))
            return "late";
        return null;
    }

    private static string? ParseTempo(string text)
    {
        if (text.Contains("SLIGHTLY FAST", StringComparison.Ordinal) || text.Contains("SLIGHTLY RUSHED", StringComparison.Ordinal))
            return "slightlyFast";
        if (text.Contains("SLIGHTLY SLOW", StringComparison.Ordinal))
            return "slightlySlow";
        if (text.Contains("FAST", StringComparison.Ordinal) || text.Contains("RUSHED", StringComparison.Ordinal))
            return "fast";
        if (text.Contains("SLOW", StringComparison.Ordinal))
            return "slow";
        return null;
    }

    public void Dispose() => Stop();
}
