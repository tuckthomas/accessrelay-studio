using System.Text;
using System.Text.RegularExpressions;

namespace EliteScriptStudio.Core;

internal sealed record OcrFeedbackRead(long ReceiptId, string RunId, string ShotType, string Timing, string Tempo,
    string RawText, long CaptureTicks, double ProcessingMs, string? Distance = null, string? Coverage = null, string? Disposition = null);

internal sealed record AdaptiveTimingStep(double PreviousCorrectionMs, double NextCorrectionMs, double DeltaMs, string Reason);

internal sealed record AdaptiveFeedbackResult(AdaptiveTimingStep Timing, AdaptiveTimingStep Tempo, bool TimingClampedSkipped,
    bool Changed, string Message);

internal static class ShotFeedbackAnalysis
{
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

    internal static VisionCrop ProjectCrop(VisionCrop parent, VisionCrop child)
    {
        double left = parent.Left + child.Left * parent.Width, top = parent.Top + child.Top * parent.Height;
        return new(left, top, Math.Min(1 - left, parent.Width * child.Width), Math.Min(1 - top, parent.Height * child.Height));
    }

    internal sealed record ParsedBanner(string ShotType, string Timing, string Tempo, string RawText, VisionCrop? BannerBounds,
        string? Distance = null, string? Coverage = null);

    internal static bool HasHeading(VisionTextRegion[] regions) => regions.Any(r =>
        Regex.IsMatch(NormalizeOcrLine(r.Text), @"\b(RHYTHM|TIMING|TEMPO)\b"));
    internal static string GradeIdentity(ParsedBanner banner) => string.Join("/", banner.ShotType, banner.Timing, banner.Tempo);
    internal static string Identity(ParsedBanner banner) => string.Join("/", banner.ShotType, banner.Timing, banner.Tempo, banner.Distance, banner.Coverage);

    internal static ParsedBanner? ParseFeedback(VisionTextRegion[] regions)
    {
        var lines = regions.Where(r => r.Confidence >= .8 && !string.IsNullOrWhiteSpace(r.Text))
            .OrderBy(r => r.Bounds.Top).ThenBy(r => r.Bounds.Left)
            .Select(r => (Raw: CleanOcrLine(r.Text), Match: NormalizeOcrLine(r.Text), r.Bounds)).ToArray();
        bool Has(string text, string word) => Regex.IsMatch(text, @"\b(?:" + word + @")\b");
        var headings = lines.Where(l => Has(l.Match, "RHYTHM|TIMING|TEMPO")).ToArray();
        var roots = headings.Where(l => Has(l.Match, "RHYTHM|TIMING")).ToArray();
        if (roots.Length == 0) return null; // Never manufacture a RHYTHM heading from a grade.
        var root = roots[0];
        bool Nearby(VisionCrop a, VisionCrop b) => Math.Abs(a.Top - b.Top) <= Math.Max(.08, Math.Max(a.Height, b.Height) * 6)
            && a.Left <= b.Left + b.Width + .25 && b.Left <= a.Left + a.Width + .25;
        if (roots.Any(l => !Nearby(root.Bounds, l.Bounds))) return null; // More than one banner.
        var local = lines.Where(l => Nearby(root.Bounds, l.Bounds)).ToArray();
        bool rhythm = local.Any(l => Has(l.Match, "RHYTHM|TEMPO"));
        var timingGrades = new List<string>(); var tempoGrades = new List<string>();
        foreach (var line in local)
        {
            // Detectors can return several HUD columns as one OCR line. Bind each
            // grade to its own heading before classifying it.
            var labels = Regex.Matches(line.Match, @"\b(RHYTHM|TIMING|TEMPO)\b");
            string[] sections = labels.Count <= 1 ? [line.Match] : labels.Select((m, i) =>
                line.Match[m.Index..(i + 1 < labels.Count ? labels[i + 1].Index : line.Match.Length)]).ToArray();
            if (labels.Count > 1 && labels[0].Index > 0
                && (ParseTiming(line.Match[..labels[0].Index]) is not null || ParseTempo(line.Match[..labels[0].Index]) is not null)) return null;
            foreach (string section in sections)
            {
                var grades = Regex.Matches(section, @"\b(SLIGHTLY ?EARLY|SLIGHTLY ?LATE|EXCELLENT|GREEN|EARLY|LATE)\b")
                    .Select(m => ParseTiming(m.Value)).Distinct().ToArray();
                if (grades.Length > 1) return null;
                string? timing = ParseTiming(section), tempo = ParseTempo(section);
                if (Regex.IsMatch(section, @"\b(FAST|RUSHED)\b") && Regex.IsMatch(section, @"\bSLOW\b")) return null;
                if (tempo is not null) tempoGrades.Add(tempo);
                if (timing is null) continue;
                bool tempoRole = Has(section, "TEMPO") && !Has(section, "TIMING|RHYTHM");
                if (!tempoRole && !Has(section, "TIMING|RHYTHM"))
                {
                    var roles = local.Where(l => Has(l.Match, "TIMING|TEMPO") && l.Bounds.Top <= line.Bounds.Top + line.Bounds.Height)
                        .Select(l => (Tempo: Has(l.Match, "TEMPO") && !Has(l.Match, "TIMING"),
                            Score: Math.Abs(l.Bounds.Left - line.Bounds.Left) * 2 + Math.Abs(l.Bounds.Top - line.Bounds.Top)))
                        .OrderBy(l => l.Score).ToArray();
                    if (roles.Length > 1 && roles[0].Tempo != roles[1].Tempo && Math.Abs(roles[0].Score - roles[1].Score) < .02) return null;
                    if (roles.Length > 0) tempoRole = roles[0].Tempo;
                }
                if (tempoRole)
                {
                    if (timing != "excellent") return null;
                    tempoGrades.Add("excellent");
                }
                else timingGrades.Add(timing);
            }
        }
        if (timingGrades.Distinct().Count() != 1 || tempoGrades.Distinct().Count() > 1) return null;
        string grade = timingGrades[0], speed = tempoGrades.FirstOrDefault() ?? "none";
        if (!rhythm && speed != "none") return null;
        var feedback = local.Where(l => IsFeedbackToken(l.Match, l.Raw)).ToArray();
        var (distance, coverage) = ExtractShotContext(local);
        double left = feedback.Min(l => l.Bounds.Left), top = feedback.Min(l => l.Bounds.Top);
        double right = feedback.Max(l => l.Bounds.Left + l.Bounds.Width), bottom = feedback.Max(l => l.Bounds.Top + l.Bounds.Height);
        return new(rhythm ? "rhythm" : "timing", grade, speed, string.Join(" | ", local.Select(l => l.Raw)),
            new(left, top, Math.Min(1 - left, Math.Max(.01, right - left)), Math.Min(1 - top, Math.Max(.01, bottom - top))), distance, coverage);
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
        OcrFeedbackRead read, bool strokeClamped, ref int consecutiveTimingDir, ref int consecutiveTempoDir, ref int excellentStreak,
        bool confirmDirection = false, bool timingOnly = false)
    {
        bool timingAdjusted = false;
        bool heldAfterExcellent = false;
        AdaptiveTimingStep timingStep;
        if (strokeClamped && read.Timing != "excellent")
        {
            consecutiveTimingDir = 0;
            timingStep = new(currentCorrectionMs, currentCorrectionMs, 0,
                "Timing correction unchanged (stroke started behind schedule).");
        }
        else if (read.Timing == "excellent")
        {
            excellentStreak++;
            timingStep = ComputeAdaptiveStep(currentCorrectionMs, read.Timing, ref consecutiveTimingDir);
        }
        else
        {
            var candidateStep = ComputeAdaptiveStep(currentCorrectionMs, read.Timing, ref consecutiveTimingDir);
            if ((excellentStreak >= 2 || confirmDirection) && Math.Abs(consecutiveTimingDir) == 1 && candidateStep.DeltaMs != 0)
            {
                heldAfterExcellent = true;
                timingStep = new(currentCorrectionMs, currentCorrectionMs, 0,
                    $"Holding timing at {FormatMs(currentCorrectionMs)} ms pending a second {read.Timing} result.");
            }
            else
            {
                if (candidateStep.DeltaMs != 0) excellentStreak = 0;
                timingStep = candidateStep;
                timingAdjusted = candidateStep.DeltaMs != 0;
            }
        }

        bool hasTempo = read.ShotType == "rhythm" && read.Tempo is not ("none" or "");
        if (!hasTempo) consecutiveTempoDir = 0;
        var tempoStep = !hasTempo
            ? new AdaptiveTimingStep(currentTempoCorrectionMs, currentTempoCorrectionMs, 0, "Tempo unchanged.")
            : ComputeAdaptiveTempoStep(currentTempoCorrectionMs, read.Tempo, ref consecutiveTempoDir);
        if (tempoStep.DeltaMs != 0 && (timingOnly || (confirmDirection && Math.Abs(consecutiveTempoDir) < 2)))
            tempoStep = new(currentTempoCorrectionMs, currentTempoCorrectionMs, 0, timingOnly
                ? "The solo timing trial keeps the calibrated stroke duration." : "Waiting for a second matching tempo grade.");

        bool timingMoved = timingAdjusted && timingStep.DeltaMs != 0;
        bool tempoMoved = tempoStep.DeltaMs != 0;
        string summary;
        if (!timingMoved && !tempoMoved)
        {
            summary = timingStep.Reason;
        }
        else
        {
            var parts = new List<string>(2);
            if (timingMoved)
                parts.Add($"timing {FormatMs(timingStep.PreviousCorrectionMs)} → {FormatMs(timingStep.NextCorrectionMs)} ms");
            else if (strokeClamped && read.Timing != "excellent")
                parts.Add("timing held (stroke started late)");
            else if (heldAfterExcellent)
                parts.Add($"timing held after {excellentStreak} Excellents");
            if (tempoMoved)
                parts.Add($"tempo {FormatMs(tempoStep.PreviousCorrectionMs)} → {FormatMs(tempoStep.NextCorrectionMs)} ms");
            string tag = read.Tempo is not ("none" or "") && read.Tempo != read.Timing
                ? $"{read.Tempo} {read.Timing}"
                : read.Timing;
            summary = $"OCR {tag}: {string.Join("; ", parts)}.";
        }
        if (hasTempo && tempoStep.DeltaMs == 0 && read.Tempo != "excellent") summary += " " + tempoStep.Reason;
        // A changed setting needs its own two agreeing grades before another step.
        if (confirmDirection && timingMoved) consecutiveTimingDir = 0;
        if (confirmDirection && tempoMoved) consecutiveTempoDir = 0;
        return new(timingStep, tempoStep, strokeClamped && read.Timing != "excellent", timingMoved || tempoMoved, summary);
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
        double next = Math.Clamp(Math.Round((current + dir * magnitude) * 2) / 2, Math.Min(current, min), Math.Max(current, max));
        return new(current, next, next - current, stepReason(current, next));
    }

    private static string FormatMs(double value) => value.ToString("+0.#;-0.#;0");

    internal static string CleanOcrLine(string raw)
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
            else if (ch is '3') sb.Append('E');
            else if (ch is '4') sb.Append('A');
            else if (ch is '5') sb.Append('S');
            else if (ch is '8') sb.Append('B');
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
            string rawTok = tokens[i].TrimEnd('.');
            int quoteIdx = rawTok.IndexOf('\'');
            if (quoteIdx > 0 && int.TryParse(rawTok[..quoteIdx], out int ftWithInches) && ftWithInches is >= 1 and <= 94)
            {
                string inchesPart = rawTok[(quoteIdx + 1)..];
                if (inchesPart.Length == 0) return $"{ftWithInches} ft";
                if (int.TryParse(inchesPart, out int inches) && inches is >= 0 and <= 11)
                    return $"{ftWithInches}'{inches}";
            }
            string tok = rawTok.TrimEnd('\'');
            if (tok.EndsWith("FT", StringComparison.Ordinal) && tok.Length > 2 && int.TryParse(tok[..^2], out int attachedFt) && attachedFt is >= 1 and <= 94)
                return $"{attachedFt} ft";
            if (int.TryParse(tok, out int feet) && feet is >= 1 and <= 94)
            {
                bool hasFt = (i + 1 < tokens.Length && tokens[i + 1].StartsWith("FT", StringComparison.Ordinal)) || rawTok.EndsWith('\'');
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
        if (Regex.IsMatch(text, @"\b(EXCELLENT|GREEN)\b"))
            return "excellent";
        if (Regex.IsMatch(text, @"\bEARLY\b"))
            return "early";
        if (Regex.IsMatch(text, @"\bLATE\b"))
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

}
