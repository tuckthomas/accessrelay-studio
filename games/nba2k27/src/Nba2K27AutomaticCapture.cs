using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Packs.Nba2K27;

internal sealed record Nba2K27AutomaticCapture
{
    public int Version { get; init; } = 1;
    public string Policy { get; init; } = "gamertag-pose-v1";
    public string Gamertag { get; init; } = "";
    public string Camera { get; init; } = "2K";
    public bool MeterOn { get; init; }
    public VisionCrop? Feedback { get; init; }
    public VisionCrop? RelativeMeter { get; init; }
    public VisionCaptureModelSet ModelSet { get; init; } = Models;
    internal static readonly VisionCaptureModelSet Models = new("nba2k27-automatic-v1",
        "20aad6e2e42359cac1c5b4a0b2da00e29bfe91a72a782fdcf287d273a04c1b24",
        "ea07c15d38ac40cd69da3c493444ec75b44ff23840553ff8ba102c1219ed39c2",
        "e8770c967605983d1570cdf5352041dfb68fa0c21664f49f47b155abd3e0e318");
    internal static Nba2K27AutomaticCapture Read(JsonElement? value) => value?.Deserialize<Nba2K27AutomaticCapture>(Nba2K27CaptureConfiguration.Json)
        ?? throw new InvalidDataException("Detect the NBA capture areas first.");
    internal void Validate(Nba2K27ShotProfile profile, bool complete)
    {
        if (Version != 1 || Policy != "gamertag-pose-v1" || ModelSet != Models || Gamertag != profile.Gamertag || Camera != profile.CameraView || MeterOn != profile.UsesShotMeter
            || string.IsNullOrWhiteSpace(Gamertag)) throw new InvalidDataException("Set the exact displayed gamertag in the shot profile, then detect capture areas again.");
        Feedback?.Validate();
        if (RelativeMeter is { } meter && (!double.IsFinite(meter.Left) || !double.IsFinite(meter.Top) || !double.IsFinite(meter.Width)
            || !double.IsFinite(meter.Height) || Math.Abs(meter.Left) > 4 || Math.Abs(meter.Top) > 4 || meter.Width is <= 0 or > 4 || meter.Height is <= 0 or > 4))
            throw new InvalidDataException("The player-relative meter region is invalid.");
        if (complete && (Feedback is null || (MeterOn && RelativeMeter is null))) throw new InvalidDataException("Automatic capture setup has not detected the required HUD regions yet.");
    }
}

/// <summary>Detect bodies/keypoints, acquire by exact displayed identity and periodically reverify.
/// Court and meter proposals are image-derived estimates, not certified game detections.</summary>
internal sealed class Nba2K27AutomaticTracker(Nba2K27AutomaticCapture configuration, Func<IVisionAnalysisModels> createModels) : IVisionRegionTracker
{
    private IVisionAnalysisModels? _models;
    private readonly bool _diagnostic = configuration.Feedback is null;
    private VisionPersonPose[] _people = [];
    private VisionTextRegion[] _text = [];
    private long _lastOcr, _verifiedAt, _verifiedSequence, _generation = -1, _sequence = -1;
    private int _width, _height, _confirmations;
    private VisionPersonPose? _previous;
    private VisionPlayerIdentity? _identity;
    private VisionCrop? _relativeMeter = configuration.RelativeMeter, _feedback = configuration.Feedback;
    private volatile bool _stopping;
    public VisionRegionObservation Track(ReadOnlySpan<byte> bgra, int width, int height, int stride, long sequence, long generation)
    {
        if (_stopping) throw new OperationCanceledException();
        if ((_generation >= 0 && (generation != _generation || width != _width || height != _height)) || sequence <= _sequence)
        { ResetIdentity(); _feedback = null; _relativeMeter = null; }
        _generation = generation; _sequence = sequence; _width = width; _height = height;
        _models ??= createModels(); if (_stopping) { _models.RequestStop(); throw new OperationCanceledException(); }
        bool freshText = _lastOcr == 0 || Stopwatch.GetElapsedTime(_lastOcr).TotalMilliseconds >= 350;
        if (freshText)
        {
            _text = _models.ReadText(bgra, width, height, stride); _lastOcr = Stopwatch.GetTimestamp();
            _feedback ??= FindFeedback(_text);
        }
        var namedTags = _text.Where(t => t.Confidence >= .8 && MatchesTag(t.Text, configuration.Gamertag)).ToArray();
        VisionCrop? search = null;
        if (_previous is { } prior)
            search = Clip(prior.Bounds.Left - prior.Bounds.Width, prior.Bounds.Top - prior.Bounds.Height * .5,
                prior.Bounds.Left + prior.Bounds.Width * 2, prior.Bounds.Top + prior.Bounds.Height * 1.5);
        else if (namedTags.Length == 1)
        {
            // The tag defines a broad acquisition search, not the final body crop.
            // Pose inference resolves actual limbs/bounds at higher effective resolution.
            var tag = namedTags[0].Bounds; double cx = tag.Left + tag.Width / 2;
            double h = Math.Max(tag.Height * 12, tag.Width * width / height * 1.6), w = Math.Max(tag.Width * 2.2, h * height / width * 1.25);
            search = Clip(cx - w / 2, tag.Top - h * 1.15, cx + w / 2, tag.Top + h * .15);
        }
        var people = _people = _models.DetectPeople(bgra, width, height, stride, search);
        VisionPersonPose? selected;
        if (freshText || _identity is null)
        {
            var tags = namedTags;
            if (tags.Length != 1) return Missing(sequence, tags.Length > 1 ? "The displayed gamertag is ambiguous." : "Looking for the exact displayed gamertag: " + configuration.Gamertag);
            var ranked = people.Select(person => (Person: person, Distance: AssociationDistance(person, tags[0].Bounds)))
                .Where(item => double.IsFinite(item.Distance)).OrderBy(item => item.Distance).ToArray();
            if (ranked.Length == 0 || ranked[0].Distance > 1.25 || (ranked.Length > 1 && ranked[1].Distance - ranked[0].Distance < .3))
                return Missing(sequence, "The gamertag cannot be assigned uniquely to a detected player's feet.");
            selected = ranked[0].Person;
            if (_previous is not null && CenterDistance(selected.Bounds, _previous.Bounds) > .8) _confirmations = 0;
            _confirmations++; _verifiedAt = Stopwatch.GetTimestamp(); _verifiedSequence = sequence;
            string recognized = tags[0].Text.Normalize(NormalizationForm.FormC).Trim();
            int tagStart = recognized.IndexOf(configuration.Gamertag, StringComparison.Ordinal);
            _identity = new(configuration.Gamertag, recognized.Substring(tagStart, configuration.Gamertag.Length), true, tags[0].Confidence, sequence, tags[0].Text);
        }
        else
        {
            if (_previous is null || Stopwatch.GetElapsedTime(_verifiedAt).TotalMilliseconds > 700) return Missing(sequence, "Player identity needs a fresh gamertag confirmation.");
            var ranked = people.Select(person => (Person: person, Distance: CenterDistance(person.Bounds, _previous.Bounds)))
                .OrderBy(item => item.Distance).ToArray();
            if (ranked.Length == 0 || ranked[0].Distance > .5 || (ranked.Length > 1 && ranked[1].Distance - ranked[0].Distance < .3))
                return Missing(sequence, "Player continuity is ambiguous. Waiting for a fresh identity check.");
            selected = ranked[0].Person;
        }
        _previous = selected;
        if (_confirmations < 2) return Missing(sequence, "Confirming the gamertag/body association on a second observation.", reset: false);
        if (selected.Keypoints.Count(p => p.Confidence >= .35) < 8 || selected.Keypoints[5].Confidence < .25 || selected.Keypoints[6].Confidence < .25)
            return Missing(sequence, "The player's body joints are occluded or insufficiently confident.");
        var body = selected.Bounds;
        var visible = selected.Keypoints.Where(p => p.Confidence >= .35).ToArray();
        if (visible.Any(p => p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)) return Missing(sequence, "The visible player joints extend beyond the captured image.");
        double left = Math.Min(body.Left, visible.Min(p => p.X)), right = Math.Max(body.Left + body.Width, visible.Max(p => p.X));
        double top = Math.Min(body.Top, visible.Min(p => p.Y)), bottom = Math.Max(body.Top + body.Height, visible.Max(p => p.Y));
        var crop = Clip(left - body.Width * .12, top - body.Height * .15, right + body.Width * .12, bottom + body.Height * .08);
        var meter = configuration.MeterOn ? FindMeter(bgra, width, height, stride, body) : null;
        if (meter is { } found) _relativeMeter = new((found.Left - body.Left) / body.Width, (found.Top - body.Top) / body.Height, found.Width / body.Width, found.Height / body.Height);
        VisionCrop? meterArea = _relativeMeter is { } relative ? Clip(body.Left + relative.Left * body.Width, body.Top + relative.Top * body.Height,
            body.Left + (relative.Left + relative.Width) * body.Width, body.Top + (relative.Top + relative.Height) * body.Height) : null;
        var (court, courtConfidence) = FindCourt(bgra, width, height, stride, body);
        bool complete = _feedback is not null && (!configuration.MeterOn || meterArea is not null) && court.Length >= 3;
        var missing = new List<string>();
        if (_feedback is null) missing.Add("shot feedback");
        if (configuration.MeterOn && meterArea is null) missing.Add("visible shot meter");
        if (court.Length < 3) missing.Add("court floor");
        return new(sequence, true, crop, meterArea, court, Math.Min(selected.Confidence, _identity!.Confidence),
            complete ? "Player identity and pose acquired. Detected capture areas are ready for review."
                : "Player identity and pose acquired. Waiting for " + string.Join(", ", missing) + ".",
            _identity with { VerifiedSequence = _verifiedSequence }, selected.Keypoints, _feedback, meter is not null, courtConfidence, complete, body, _models.ExecutionProvider,
            _diagnostic ? _text : null, _diagnostic ? people : null);
    }
    private void ResetIdentity() { _identity = null; _previous = null; _confirmations = 0; _lastOcr = 0; }
    private VisionRegionObservation Missing(long sequence, string message, bool reset = true)
    { var previous = _previous?.Bounds ?? new(0, 0, 1, 1); if (reset) ResetIdentity();
        return new(sequence, false, previous, null, [], 0, message, TextRegions: _diagnostic ? _text : null, DetectedPeople: _diagnostic ? _people : null); }
    internal static bool MatchesTag(string observed, string expected)
    {
        observed = observed.Normalize(NormalizationForm.FormC).Trim(); expected = expected.Normalize(NormalizationForm.FormC).Trim();
        if (expected.Length == 0) return false;
        if (string.Equals(observed, expected, StringComparison.Ordinal)) return true;
        int start = observed.IndexOf(expected, StringComparison.Ordinal);
        return start >= 0 && (start == 0 || !char.IsLetterOrDigit(observed[start - 1]))
            && (start + expected.Length == observed.Length || !char.IsLetterOrDigit(observed[start + expected.Length]));
    }
    private static double AssociationDistance(VisionPersonPose person, VisionCrop tag)
    {
        if (person.Keypoints.Length != 17 || person.Keypoints[15].Confidence < .25 || person.Keypoints[16].Confidence < .25) return double.PositiveInfinity;
        double feetX = (person.Keypoints[15].X + person.Keypoints[16].X) / 2, feetY = Math.Max(person.Keypoints[15].Y, person.Keypoints[16].Y);
        double tx = tag.Left + tag.Width / 2, ty = tag.Top;
        double horizontal = Math.Abs(feetX - tx) / person.Bounds.Width, vertical = (ty - feetY) / person.Bounds.Height;
        if (horizontal > 1 || vertical < -.2 || vertical > 1) return double.PositiveInfinity;
        return horizontal + Math.Abs(vertical - .12) * .7;
    }
    private static double CenterDistance(VisionCrop a, VisionCrop b) =>
        Math.Sqrt(Math.Pow((a.Left + a.Width / 2 - b.Left - b.Width / 2) / Math.Max(.01, b.Width), 2)
            + Math.Pow((a.Top + a.Height / 2 - b.Top - b.Height / 2) / Math.Max(.01, b.Height), 2));
    private static VisionCrop Clip(double l, double t, double r, double b)
    { l = Math.Clamp(l, 0, .99999); t = Math.Clamp(t, 0, .99999); return new(l, t, Math.Max(.00001, Math.Min(1, r) - l), Math.Max(.00001, Math.Min(1, b) - t)); }
    private static VisionCrop? FindFeedback(VisionTextRegion[] text)
    {
        string[] words = ["TIMING", "TEMPO", "COVERAGE", "CONTEST", "RELEASE"];
        var candidates = text.Where(t => words.Any(word => t.Text.Contains(word, StringComparison.OrdinalIgnoreCase)) && (t.Bounds.Top < .3 || t.Bounds.Top > .65)).ToArray();
        foreach (var first in candidates)
        {
            var group = candidates.Where(t => Math.Abs(t.Bounds.Top - first.Bounds.Top) < .16 && Math.Abs(t.Bounds.Left - first.Bounds.Left) < .35).ToArray();
            if (words.Count(word => group.Any(line => line.Text.Contains(word, StringComparison.OrdinalIgnoreCase))) < 2) continue;
            return Clip(group.Min(t => t.Bounds.Left) - .025, group.Min(t => t.Bounds.Top) - .025,
                group.Max(t => t.Bounds.Left + t.Bounds.Width) + .04, group.Max(t => t.Bounds.Top + t.Bounds.Height) + .07);
        }
        return null;
    }
    private static VisionCrop? FindMeter(ReadOnlySpan<byte> bgra, int width, int height, int stride, VisionCrop body)
    {
        // Search beside the detected body; require a compact vertical high-contrast HUD component.
        const int gw = 120, gh = 160;
        var region = Clip(body.Left - body.Width * .8, body.Top - body.Height * .25, body.Left + body.Width * 1.8, body.Top + body.Height * 1.1);
        var mask = new bool[gw * gh]; var colored = new bool[mask.Length];
        for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++)
        {
            double nx = region.Left + (x + .5) / gw * region.Width, ny = region.Top + (y + .5) / gh * region.Height;
            if (nx > body.Left + body.Width * .2 && nx < body.Left + body.Width * .8) continue;
            int i = Math.Clamp((int)(ny * height), 0, height - 1) * stride + Math.Clamp((int)(nx * width), 0, width - 1) * 4;
            int lo = Math.Min(bgra[i], Math.Min(bgra[i + 1], bgra[i + 2])), hi = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
            colored[y * gw + x] = hi > 150 && hi - lo > 80;
            mask[y * gw + x] = colored[y * gw + x] || lo > 195;
        }
        var candidates = new List<VisionCrop>(); var queue = new int[mask.Length];
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start]) continue;
            int head = 0, tail = 1, lx = start % gw, rx = lx, ty = start / gw, by = ty, colors = 0;
            queue[0] = start; mask[start] = false;
            while (head < tail)
            {
                int p = queue[head++], x = p % gw, y = p / gw; if (colored[p]) colors++;
                lx = Math.Min(lx, x); rx = Math.Max(rx, x); ty = Math.Min(ty, y); by = Math.Max(by, y);
                for (int d = 0; d < 4; d++) for (int step = 1; step <= 2; step++) { int n = p + (d == 0 ? -step : d == 1 ? step : d == 2 ? -gw * step : gw * step);
                    if (n >= 0 && n < mask.Length && (d >= 2 || n / gw == y) && mask[n]) { mask[n] = false; queue[tail++] = n; } }
            }
            double h = (by - ty + 1d) / gh * region.Height, w = (rx - lx + 1d) / gw * region.Width;
            if (colors < 4 || h < body.Height * .18 || h > body.Height * 1.1 || w > body.Width * .55 || h * height / (w * width) < 1.6) continue;
            candidates.Add(Clip(region.Left + lx / (double)gw * region.Width - .003, region.Top + ty / (double)gh * region.Height - .008,
                region.Left + (rx + 1d) / gw * region.Width + .003, region.Top + (by + 1d) / gh * region.Height + .008));
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }
    private static (VisionPoint[] Court, double Confidence) FindCourt(ReadOnlySpan<byte> bgra, int width, int height, int stride, VisionCrop body)
    {
        const int gw = 160, gh = 90;
        int sy = Math.Clamp((int)((body.Top + body.Height * .98) * gh), 0, gh - 1);
        int sx = Math.Clamp((int)((body.Left + body.Width * 1.1) * gw), 0, gw - 1);
        (int Rg, int Bg, int Luma) Pixel(int x, int y, ReadOnlySpan<byte> pixels)
        { int i = Math.Min(height - 1, (int)((y + .5) * height / gh)) * stride + Math.Min(width - 1, (int)((x + .5) * width / gw)) * 4;
            return (pixels[i + 2] - pixels[i + 1], pixels[i] - pixels[i + 1], (pixels[i + 2] * 77 + pixels[i + 1] * 150 + pixels[i] * 29) >> 8); }
        var seed = Pixel(sx, sy, bgra); var mask = new bool[gw * gh];
        for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++)
        { var value = Pixel(x, y, bgra); mask[y * gw + x] = y >= Math.Max(0, (int)(body.Top * gh) - 12) && value.Luma > 35
                && Math.Abs(value.Rg - seed.Rg) < 28 && Math.Abs(value.Bg - seed.Bg) < 28 && Math.Abs(value.Luma - seed.Luma) < 65; }
        if (!mask[sy * gw + sx]) return ([], 0);
        var queue = new int[mask.Length]; int head = 0, tail = 1; queue[0] = sy * gw + sx; mask[queue[0]] = false;
        var points = new List<VisionPoint>();
        while (head < tail)
        {
            int p = queue[head++], x = p % gw, y = p / gw;
            points.Add(new((x + .5) / gw, (y + .5) / gh));
            for (int d = 0; d < 4; d++) for (int step = 1; step <= 2; step++)
            { int n = p + (d == 0 ? -step : d == 1 ? step : d == 2 ? -gw * step : gw * step);
                if (n >= 0 && n < mask.Length && (d >= 2 || n / gw == y) && mask[n]) { mask[n] = false; queue[tail++] = n; } }
        }
        if (tail < mask.Length * .06 || tail > mask.Length * .85) return ([], 0);
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToArray(); var hull = new List<VisionPoint>();
        static double Cross(VisionPoint a, VisionPoint b, VisionPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        foreach (var point in sorted) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
        int lower = hull.Count;
        foreach (var point in sorted.Reverse()) { while (hull.Count > lower && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
        if (hull.Count > 0) hull.RemoveAt(hull.Count - 1);
        return (hull.Take(64).ToArray(), Math.Min(.95, tail / (double)mask.Length * 2));
    }
    public void RequestStop() { _stopping = true; _models?.RequestStop(); }
    public void Dispose() { RequestStop(); _models?.Dispose(); }
}
