using EliteScriptStudio.Core;

namespace EliteScriptStudio.Packs.Nba2K27;

/// <summary>Short-range visual anchor tracker for acquisition. Ambiguity loses the lock;
/// a confident wrong match can still change identity. This is independent of the release model.</summary>
internal sealed class Nba2K27RegionTracker : IVisionRegionTracker
{
    private readonly Nba2K27CaptureConfiguration _configuration;
    private readonly int _width, _height;
    private readonly byte[] _current, _previous;
    private readonly Patch _playerPatch;
    private readonly List<Feature> _features = [];
    private readonly List<(VisionCrop Box, double Score)> _candidates = new(13000);
    private VisionCrop _body;
    private VisionPoint[] _court;
    private long _generation = -1, _sequence = -1;
    private bool _lost;
    private int _frames, _airborneFrames;
    private sealed record Patch(float[] Values, int Side);
    private readonly record struct Feature(VisionPoint Position, Patch Patch);
    private readonly record struct Match(VisionCrop Box, double Score, double Margin);

    internal Nba2K27RegionTracker(Nba2K27CaptureConfiguration configuration)
    {
        _configuration = configuration; _width = configuration.ReferenceWidth; _height = configuration.ReferenceHeight;
        _current = new byte[_width * _height]; _previous = configuration.ReferenceGray.ToArray();
        _body = configuration.Player; _court = configuration.Court.ToArray();
        _playerPatch = MakePatch(_previous, Torso(_body), 16);
        SeedCourtFeatures(_previous);
    }

    public VisionRegionObservation Track(ReadOnlySpan<byte> bgra, int width, int height, int stride, long sequence, long generation)
    {
        if (_lost) return Lost(sequence, "Visual anchor lock was lost. Stop preview and reacquire the intended player.");
        if ((_generation >= 0 && _generation != generation) || sequence <= _sequence || bgra.Length < (long)stride * height)
            return Lose(sequence, "The source generation or frame order changed.");
        _generation = generation; _sequence = sequence;
        for (int y = 0; y < _height; y++) for (int x = 0; x < _width; x++)
        {
            int index = Math.Min(height - 1, (int)((y + .5) * height / _height)) * stride
                + Math.Min(width - 1, (int)((x + .5) * width / _width)) * 4;
            _current[y * _width + x] = (byte)((bgra[index] * 29 + bgra[index + 1] * 150 + bgra[index + 2] * 77) >> 8);
        }
        var pairs = new List<(VisionPoint From, VisionPoint To)>();
        foreach (var feature in _features)
        {
            var seed = new VisionCrop(feature.Position.X - 3d / _width, feature.Position.Y - 3d / _height, 6d / _width, 6d / _height);
            var match = Find(feature.Patch, seed, 10, scaleSearch: false);
            if (match.Score >= .82 && match.Margin >= .015)
                pairs.Add((feature.Position, new(match.Box.Left + match.Box.Width / 2, match.Box.Top + match.Box.Height / 2)));
        }
        if (!Homography(pairs, out var transform, out double courtConfidence))
            return Lose(sequence, "Court markings could not establish camera motion. Reacquire the court reference.");
        var nextCourt = _court.Select(p => Project(transform, p)).ToArray();
        if (nextCourt.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < -.2 || p.Y < -.2 || p.X > 1.2 || p.Y > 1.2))
            return Lose(sequence, "The court projection left the tracked camera view.");
        var predicted = WarpBox(transform, Torso(_body));
        var player = Find(_playerPatch, predicted, _frames == 0 ? 32 : 20, scaleSearch: true);
        if (player.Score < .78 || player.Margin < .025)
            return Lose(sequence, "The intended player's appearance is missing or ambiguous.");
        var torso = player.Box;
        var body = new VisionCrop(torso.Left - torso.Width * .3 / .4, torso.Top - torso.Height * .32 / .38,
            torso.Width / .4, torso.Height / .38);
        if (body.Width < _configuration.Player.Width * .4 || body.Width > _configuration.Player.Width * 2.5
            || body.Height < _configuration.Player.Height * .4 || body.Height > _configuration.Player.Height * 2.5)
            return Lose(sequence, "Player scale changed beyond the acquired identity reference.");
        var ground = new VisionPoint(body.Left + body.Width / 2, body.Top + body.Height);
        if (Nba2K27CaptureConfiguration.Inside(nextCourt, ground)) _airborneFrames = 0;
        else if (++_airborneFrames > 120)
            return Lose(sequence, "The tracked ground position no longer agrees with the court.");
        _body = body; _court = nextCourt;
        // Preserve body above the floor; the floor is an identity prior, never a pixel mask.
        var crop = Clip(new(body.Left - body.Width * .1, body.Top - body.Height * .3, body.Width * 1.2, body.Height * 1.3));
        VisionCrop? meter = null;
        if (_configuration.Meter is { } initial)
        {
            var reference = _configuration.Player;
            meter = Clip(new(body.Left + (initial.Left - reference.Left) / reference.Width * body.Width,
                body.Top + (initial.Top - reference.Top) / reference.Height * body.Height,
                initial.Width / reference.Width * body.Width, initial.Height / reference.Height * body.Height));
        }
        _current.CopyTo(_previous, 0);
        _frames++;
        SeedCourtFeatures(_previous);
        return new(sequence, true, crop, meter, nextCourt, Math.Min(player.Score, courtConfidence), "Visual anchors acquired.");
    }

    private VisionRegionObservation Lose(long sequence, string message) { _lost = true; return Lost(sequence, message); }
    private VisionRegionObservation Lost(long sequence, string message) => new(sequence, false, _body, null, _court.ToArray(), 0, message);
    private static VisionCrop Torso(VisionCrop body) => new(body.Left + body.Width * .3, body.Top + body.Height * .32, body.Width * .4, body.Height * .38);
    private VisionCrop Clip(VisionCrop box)
    {
        var view = _configuration.Viewport;
        double right = view.Left + view.Width, bottom = view.Top + view.Height;
        double left = Math.Clamp(box.Left, view.Left, right - .00001), top = Math.Clamp(box.Top, view.Top, bottom - .00001);
        return new(left, top, Math.Max(.00001, Math.Min(right, box.Left + box.Width) - left), Math.Max(.00001, Math.Min(bottom, box.Top + box.Height) - top));
    }

    private Patch MakePatch(byte[] image, VisionCrop box, int side)
    {
        var values = new float[side * side]; double mean = 0, energy = 0;
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
        { float value = Sample(image, box.Left + (x + .5) / side * box.Width, box.Top + (y + .5) / side * box.Height); values[y * side + x] = value; mean += value; }
        mean /= values.Length;
        for (int i = 0; i < values.Length; i++) { values[i] -= (float)mean; energy += values[i] * values[i]; }
        double norm = Math.Sqrt(energy);
        if (norm >= 1) for (int i = 0; i < values.Length; i++) values[i] /= (float)norm;
        else Array.Clear(values);
        return new(values, side);
    }
    private float Sample(byte[] image, double x, double y) => image[Math.Clamp((int)(y * _height), 0, _height - 1) * _width + Math.Clamp((int)(x * _width), 0, _width - 1)];
    private double Score(Patch patch, VisionCrop box)
    {
        if (box.Left < 0 || box.Top < 0 || box.Left + box.Width > 1 || box.Top + box.Height > 1) return -1;
        Span<int> xs = stackalloc int[16]; Span<int> ys = stackalloc int[16];
        for (int x = 0; x < patch.Side; x++) xs[x] = Math.Clamp((int)((box.Left + (x + .5) / patch.Side * box.Width) * _width), 0, _width - 1);
        for (int y = 0; y < patch.Side; y++) ys[y] = Math.Clamp((int)((box.Top + (y + .5) / patch.Side * box.Height) * _height), 0, _height - 1) * _width;
        double sum = 0, square = 0, dot = 0;
        for (int y = 0; y < patch.Side; y++) for (int x = 0; x < patch.Side; x++)
        {
            double value = _current[ys[y] + xs[x]];
            sum += value; square += value * value; dot += value * patch.Values[y * patch.Side + x];
        }
        double energy = square - sum * sum / patch.Values.Length;
        return energy < patch.Values.Length * 4 ? -1 : dot / Math.Sqrt(energy);
    }
    private Match Find(Patch patch, VisionCrop seed, int radius, bool scaleSearch)
    {
        var candidates = _candidates; candidates.Clear();
        double best = -1; var winner = seed;
        foreach (double scale in scaleSearch ? new[] { 1d, .97, 1.03 } : new[] { 1d })
            for (int dy = -radius; dy <= radius; dy++) for (int dx = -radius; dx <= radius; dx++)
            {
                var box = new VisionCrop(seed.Left + dx / (double)_width - seed.Width * (scale - 1) / 2,
                    seed.Top + dy / (double)_height - seed.Height * (scale - 1) / 2, seed.Width * scale, seed.Height * scale);
                double score = Score(patch, box); candidates.Add((box, score));
                if (score > best + 1e-6) { best = score; winner = box; }
            }
        var coarse = winner;
        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
        {
            var box = coarse with { Left = coarse.Left + dx / (double)_width, Top = coarse.Top + dy / (double)_height };
            double score = Score(patch, box); if (score > best) { best = score; winner = box; }
        }
        double second = -1;
        foreach (var candidate in candidates)
            if (Math.Abs(candidate.Box.Left - winner.Left) > Math.Max(3d / _width, winner.Width * .4)
                || Math.Abs(candidate.Box.Top - winner.Top) > Math.Max(3d / _height, winner.Height * .4)) second = Math.Max(second, candidate.Score);
        return new(winner, best, best - second);
    }

    private void SeedCourtFeatures(byte[] image)
    {
        _features.Clear();
        var candidates = new List<(VisionPoint Point, double Variance)>();
        for (int y = 8; y < _height - 8; y += 8) for (int x = 8; x < _width - 8; x += 8)
        {
            var point = new VisionPoint(x / (double)_width, y / (double)_height);
            if (!Nba2K27CaptureConfiguration.Inside(_court, point) || (point.X > _body.Left - .03 && point.X < _body.Left + _body.Width + .03
                && point.Y > _body.Top && point.Y < _body.Top + _body.Height + .03)) continue;
            double sum = 0, square = 0;
            for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++) { double value = image[(y + dy) * _width + x + dx]; sum += value; square += value * value; }
            double variance = square / 49 - sum * sum / (49 * 49);
            if (variance > 100) candidates.Add((point, variance));
        }
        foreach (var candidate in candidates.OrderByDescending(item => item.Variance))
        {
            if (_features.Any(f => Math.Abs(f.Position.X - candidate.Point.X) * _width < 16 && Math.Abs(f.Position.Y - candidate.Point.Y) * _height < 16)) continue;
            _features.Add(new(candidate.Point, MakePatch(image, new(candidate.Point.X - 3d / _width, candidate.Point.Y - 3d / _height, 6d / _width, 6d / _height), 7)));
            if (_features.Count == 24) break;
        }
    }

    private static VisionPoint Project(double[] h, VisionPoint p)
    { double denominator = h[6] * p.X + h[7] * p.Y + 1; return new((h[0] * p.X + h[1] * p.Y + h[2]) / denominator, (h[3] * p.X + h[4] * p.Y + h[5]) / denominator); }
    private static VisionCrop WarpBox(double[] h, VisionCrop box)
    {
        var a = Project(h, new(box.Left, box.Top)); var b = Project(h, new(box.Left + box.Width, box.Top + box.Height));
        return new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }
    private bool Homography(List<(VisionPoint From, VisionPoint To)> pairs, out double[] transform, out double confidence)
    {
        transform = []; confidence = 0;
        if (pairs.Count < 6) return false;
        var random = new Random(17); int best = 0;
        for (int trial = 0; trial < 48; trial++)
        {
            var indices = Enumerable.Range(0, pairs.Count).OrderBy(_ => random.Next()).Take(4).ToArray();
            var matrix = new double[8, 9];
            for (int i = 0; i < 4; i++)
            {
                var (a, b) = pairs[indices[i]];
                matrix[i * 2, 0] = a.X; matrix[i * 2, 1] = a.Y; matrix[i * 2, 2] = 1;
                matrix[i * 2, 6] = -b.X * a.X; matrix[i * 2, 7] = -b.X * a.Y; matrix[i * 2, 8] = b.X;
                matrix[i * 2 + 1, 3] = a.X; matrix[i * 2 + 1, 4] = a.Y; matrix[i * 2 + 1, 5] = 1;
                matrix[i * 2 + 1, 6] = -b.Y * a.X; matrix[i * 2 + 1, 7] = -b.Y * a.Y; matrix[i * 2 + 1, 8] = b.Y;
            }
            if (!Solve(matrix, out var candidate)) continue;
            int inliers = pairs.Count(pair => { var point = Project(candidate, pair.From); return Math.Abs(point.X - pair.To.X) * _width <= 2 && Math.Abs(point.Y - pair.To.Y) * _height <= 2; });
            if (inliers > best) { best = inliers; transform = candidate; }
        }
        confidence = best / (double)Math.Max(1, pairs.Count);
        return best >= 6 && confidence >= .65;
    }
    private static bool Solve(double[,] matrix, out double[] result)
    {
        result = new double[8];
        for (int column = 0; column < 8; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 8; row++) if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-10) return false;
            for (int k = column; k < 9; k++) (matrix[column, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[column, k]);
            double divisor = matrix[column, column];
            for (int k = column; k < 9; k++) matrix[column, k] /= divisor;
            for (int row = 0; row < 8; row++) if (row != column)
            { double factor = matrix[row, column]; for (int k = column; k < 9; k++) matrix[row, k] -= factor * matrix[column, k]; }
        }
        for (int i = 0; i < 8; i++) { result[i] = matrix[i, 8]; if (!double.IsFinite(result[i])) return false; }
        return true;
    }
}
