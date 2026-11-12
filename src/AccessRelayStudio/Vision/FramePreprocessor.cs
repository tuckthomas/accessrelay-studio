using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

/// <summary>Allocation-free BGRA8 to planar RGB preprocessing over an explicitly selected player region.</summary>
internal static class FramePreprocessor
{
    internal static void FillRgb(ReadOnlySpan<byte> bgra, int width, int height, int stride,
        VisionCrop crop, int targetWidth, int targetHeight, ReadOnlySpan<float> mean,
        ReadOnlySpan<float> standardDeviation, Span<float> destination)
    {
        if (width <= 0 || height <= 0 || stride < checked(width * 4) ||
            bgra.Length < checked(stride * height) || targetWidth <= 0 || targetHeight <= 0 ||
            destination.Length != checked(3 * targetWidth * targetHeight) || mean.Length != 3 || standardDeviation.Length != 3)
            throw new InvalidDataException("The vision frame or model preprocessing dimensions are invalid.");
        if (!double.IsFinite(crop.Left) || !double.IsFinite(crop.Top) || !double.IsFinite(crop.Width) ||
            !double.IsFinite(crop.Height) || crop.Left < 0 || crop.Top < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            crop.Left + crop.Width > 1 || crop.Top + crop.Height > 1)
            throw new InvalidDataException("Select a player region inside the captured window.");
        for (int channel = 0; channel < 3; channel++)
            if (!float.IsFinite(mean[channel]) || !float.IsFinite(standardDeviation[channel]) || standardDeviation[channel] <= 0)
                throw new InvalidDataException("Model normalization must use finite means and positive standard deviations.");

        const int canonicalSize = 256;
        int plane = checked(targetWidth * targetHeight);
        // v2 matches recorded data exactly: nearest-neighbor source crop to a virtual 256x256
        // BGRA image, then pixel-center four-neighbor bilinear RGB sampling. Evaluate that
        // intermediate image mathematically so the live path needs no additional frame allocation.
        for (int y = 0; y < targetHeight; y++)
        {
            double gridY = Math.Clamp((y + .5) * canonicalSize / targetHeight - .5, 0, canonicalSize - 1);
            int gridY0 = (int)gridY, gridY1 = Math.Min(gridY0 + 1, canonicalSize - 1);
            float fy = (float)(gridY - gridY0);
            int y0 = SourceIndex(crop.Top, crop.Height, gridY0, height);
            int y1 = SourceIndex(crop.Top, crop.Height, gridY1, height);
            for (int x = 0; x < targetWidth; x++)
            {
                double gridX = Math.Clamp((x + .5) * canonicalSize / targetWidth - .5, 0, canonicalSize - 1);
                int gridX0 = (int)gridX, gridX1 = Math.Min(gridX0 + 1, canonicalSize - 1);
                float fx = (float)(gridX - gridX0);
                int x0 = SourceIndex(crop.Left, crop.Width, gridX0, width);
                int x1 = SourceIndex(crop.Left, crop.Width, gridX1, width);
                int p00 = y0 * stride + x0 * 4;
                int p10 = y0 * stride + x1 * 4;
                int p01 = y1 * stride + x0 * 4;
                int p11 = y1 * stride + x1 * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int bgrChannel = 2 - channel;
                    float first = bgra[p00 + bgrChannel] + (bgra[p10 + bgrChannel] - bgra[p00 + bgrChannel]) * fx;
                    float second = bgra[p01 + bgrChannel] + (bgra[p11 + bgrChannel] - bgra[p01 + bgrChannel]) * fx;
                    float unit = (first + (second - first) * fy) / 255f;
                    destination[channel * plane + y * targetWidth + x] = (unit - mean[channel]) / standardDeviation[channel];
                }
            }
        }
    }

    private static int SourceIndex(double origin, double extent, int gridIndex, int sourceSize) =>
        Math.Clamp((int)((origin + (gridIndex + .5) / 256 * extent) * sourceSize), 0, sourceSize - 1);
}
