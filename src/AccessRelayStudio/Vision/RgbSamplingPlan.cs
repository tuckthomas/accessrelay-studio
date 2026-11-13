using System.Runtime.InteropServices;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

// This layout is shared with the compute shader. Source indices are computed in
// double precision here, rather than relying on optional shader double support.
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct RgbAxisSample(int First, int Second, float Fraction, int Padding = 0);

internal sealed class RgbSamplingPlan
{
    internal int SourceWidth { get; }
    internal int SourceHeight { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int FrameLength => checked(3 * Width * Height);
    internal RgbAxisSample[] Horizontal { get; }
    internal RgbAxisSample[] Vertical { get; }
    internal float[] Mean { get; }
    internal float[] StandardDeviation { get; }

    internal RgbSamplingPlan(int sourceWidth, int sourceHeight, VisionCrop crop, int width, int height,
        ReadOnlySpan<float> mean, ReadOnlySpan<float> standardDeviation)
    {
        crop.Validate();
        if (sourceWidth <= 0 || sourceHeight <= 0 || width is < 1 or > 512 || height is < 1 or > 512 || mean.Length != 3 || standardDeviation.Length != 3)
            throw new InvalidDataException("The RGB sampling dimensions or normalization are invalid.");
        for (int channel = 0; channel < 3; channel++)
            if (!float.IsFinite(mean[channel]) || !float.IsFinite(standardDeviation[channel]) || standardDeviation[channel] <= 0)
                throw new InvalidDataException("RGB normalization requires finite means and positive standard deviations.");
        SourceWidth = sourceWidth; SourceHeight = sourceHeight; Width = width; Height = height;
        Mean = mean.ToArray(); StandardDeviation = standardDeviation.ToArray();
        Horizontal = Axis(sourceWidth, crop.Left, crop.Width, width);
        Vertical = Axis(sourceHeight, crop.Top, crop.Height, height);
    }

    private static RgbAxisSample[] Axis(int sourceSize, double origin, double extent, int targetSize)
    {
        var result = new RgbAxisSample[targetSize];
        for (int index = 0; index < targetSize; index++)
        {
            double grid = Math.Clamp((index + .5) * 256 / targetSize - .5, 0, 255);
            int first = (int)grid, second = Math.Min(first + 1, 255);
            int Source(int value) => Math.Clamp((int)((origin + (value + .5) / 256 * extent) * sourceSize), 0, sourceSize - 1);
            result[index] = new(Source(first), Source(second), (float)(grid - first));
        }
        return result;
    }

    internal void Fill(ReadOnlySpan<byte> bgra, int stride, Span<float> destination)
    {
        if (stride < checked(SourceWidth * 4) || bgra.Length < checked(stride * SourceHeight) || destination.Length != FrameLength)
            throw new InvalidDataException("The RGB source or destination does not match its sampling plan.");
        int plane = Width * Height;
        for (int y = 0; y < Height; y++)
        {
            var sy = Vertical[y];
            for (int x = 0; x < Width; x++)
            {
                var sx = Horizontal[x];
                int p00 = sy.First * stride + sx.First * 4, p10 = sy.First * stride + sx.Second * 4;
                int p01 = sy.Second * stride + sx.First * 4, p11 = sy.Second * stride + sx.Second * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int bgr = 2 - channel;
                    float first = bgra[p00 + bgr] + (bgra[p10 + bgr] - bgra[p00 + bgr]) * sx.Fraction;
                    float second = bgra[p01 + bgr] + (bgra[p11 + bgr] - bgra[p01 + bgr]) * sx.Fraction;
                    float unit = (first + (second - first) * sy.Fraction) / 255f;
                    destination[channel * plane + y * Width + x] = (unit - Mean[channel]) / StandardDeviation[channel];
                }
            }
        }
    }
}
