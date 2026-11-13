using EliteScriptStudio.Core;
using EliteScriptStudio.Vision;

internal static class RgbSamplingChecks
{
    internal static void Run()
    {
        foreach (var size in new[] { (1, 1), (2, 3), (53, 41), (337, 193) })
        foreach (var target in new[] { (1, 1), (7, 3), (37, 43), (160, 160) })
        foreach (var crop in new[] { new VisionCrop(0, 0, 1, 1), new(.137, .213, .681, .537), new(.999, .999, .001, .001) })
        {
            int stride = size.Item1 * 4 + 12;
            var bytes = new byte[stride * size.Item2];
            for (int y = 0; y < size.Item2; y++)
                for (int x = 0; x < size.Item1; x++)
                {
                    int offset = y * stride + 4 * x;
                    bytes[offset] = (byte)((x * 11 + y * 17) % 256);
                    bytes[offset + 1] = (byte)((x * 29 + y * 7) % 256);
                    bytes[offset + 2] = (byte)((x * 3 + y * 43) % 256);
                    bytes[offset + 3] = (byte)((x + y) % 256);
                }
            float[] mean = [.13f, -.21f, .37f], deviation = [.31f, .53f, 1.71f];
            var plan = new RgbSamplingPlan(size.Item1, size.Item2, crop, target.Item1, target.Item2, mean, deviation);
            var expected = new float[plan.FrameLength]; var actual = new float[plan.FrameLength];
            FramePreprocessor.FillRgb(bytes, size.Item1, size.Item2, stride, crop, target.Item1, target.Item2, mean, deviation, expected);
            plan.Fill(bytes, stride, actual);
            Require(actual.AsSpan().SequenceEqual(expected), $"Cached sampling differs from canonical v2 at {size}/{target}/{crop}.");
        }
        try { _ = new RgbSamplingPlan(1, 1, new(0, 0, 1, 1), 513, 1, [0, 0, 0], [1, 1, 1]); throw new Exception("Unbounded sampling accepted."); }
        catch (InvalidDataException) { }

        var manifest = new VisionModelManifest { InputWidth = 2, InputHeight = 1, HistoryLength = 3 };
        var settings = VisionSettings.Default with { Crop = new(0, 0, .5, 1) };
        var history = new float[18]; var input = new CpuTemporalFrameInput(manifest, settings, history);
        CapturedVisionFrame Frame(byte red, byte outside) => new([0, 0, red, 255, outside, outside, outside, 255], 2, 1, 1, 1, 1, () => { });
        using var first = Frame(20, 10); using var outside = Frame(20, 200); using var second = Frame(40, 10); using var third = Frame(60, 10); using var fourth = Frame(80, 10);
        Require(input.PrepareFrame(first, 0, false), "First player image was not new.");
        Require(!input.PrepareFrame(outside, 0, false), "Outside pixels changed selected-player identity.");
        Require(input.PrepareFrame(second, 1, false) && input.PrepareFrame(third, 2, false), "Changed player images were not detected.");
        Require(history[0] == 20 / 255f && history[6] == 40 / 255f && history[12] == 60 / 255f, "Temporal RGB order differs.");
        input.PrepareFrame(fourth, 2, true);
        Require(history[0] == 40 / 255f && history[6] == 60 / 255f && history[12] == 80 / 255f, "Temporal shift corrupted frame order.");
        input.RestartWithCurrentFrame(2);
        Require(history[0] == 80 / 255f, "Frozen-region reacquisition did not restart with the current image.");
        input.ResetContent(); Require(input.PrepareFrame(fourth, 0, false), "Source reset reused prior content identity.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
