using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

// Compare the ink edges of a recognized heading. A small crop can observe the HUD
// wipe between two slower ONNX reads, even when the next grade and distance repeat.
internal sealed class FeedbackHeadingProbe(VisionCrop region, (double X, double Y, double Dx, double Dy, int Sign)[] edges)
{
    internal VisionCrop Region { get; } = region;
    private static int Light(CapturedCropFrame f, double x, double y)
    {
        int offset = Math.Clamp((int)(y * f.Height), 0, f.Height - 1) * f.Stride
            + Math.Clamp((int)(x * f.Width), 0, f.Width - 1) * 4;
        return (f.Pixels[offset] + 2 * f.Pixels[offset + 1] + f.Pixels[offset + 2]) / 4;
    }
    internal static FeedbackHeadingProbe? Create(CapturedCropFrame frame, VisionCrop parent, VisionTextRegion heading)
    {
        var box = heading.Bounds;
        var edges = new List<(double, double, double, double, int)>();
        for (int y = 1; y < 12; y++)
        for (int x = 1; x < 39; x++)
        {
            double u = x / 40d, v = y / 13d;
            Add(1 / 40d, 0); Add(0, 1 / 13d);
            void Add(double du, double dv)
            {
                int contrast = Light(frame, box.Left + u * box.Width, box.Top + v * box.Height)
                    - Light(frame, box.Left + (u + du) * box.Width, box.Top + (v + dv) * box.Height);
                if (Math.Abs(contrast) >= 85) edges.Add((u, v, du, dv, Math.Sign(contrast)));
            }
        }
        return edges.Count < 24 ? null : new(ShotFeedbackAnalysis.ProjectCrop(parent, box), edges.ToArray());
    }
    private bool _seen;
    private int _missing;
    private long _last;
    internal bool Cleared(CapturedCropFrame frame)
    {
        if (frame.CaptureTicks <= _last) return false;
        _last = frame.CaptureTicks;
        double agreement = Agreement(frame);
        if (agreement >= .50) _seen = true;
        if (agreement >= .18) _missing = 0;
        else if (_seen && ++_missing >= 2) { _seen = false; return true; }
        return false;
    }

    private double Agreement(CapturedCropFrame frame)
    {
        int present = 0;
        foreach (var (x, y, dx, dy, sign) in edges)
            if (sign * (Light(frame, x, y) - Light(frame, x + dx, y + dy)) >= 35) present++;
        return (double)present / edges.Length;
    }
}
