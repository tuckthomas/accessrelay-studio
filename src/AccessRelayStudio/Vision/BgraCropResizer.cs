using System.Runtime.InteropServices;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Vision;

/// <summary>Exact nearest-neighbour crop with cached source indices. Does not change pixel policy.</summary>
internal sealed class BgraCropResizer(int targetWidth, int targetHeight)
{
    private readonly int[] _columns = new int[targetWidth], _rows = new int[targetHeight];
    private int _width, _height, _stride;
    private VisionCrop _crop;

    internal void Resize(ReadOnlySpan<byte> sourceBytes, int width, int height, int stride,
        Span<byte> targetBytes, VisionCrop crop)
    {
        if (width <= 0 || height <= 0 || targetWidth <= 0 || targetHeight <= 0 || stride < width * 4 || stride % 4 != 0
            || sourceBytes.Length < (long)stride * height || targetBytes.Length != (long)targetWidth * targetHeight * 4)
            throw new InvalidDataException("Invalid BGRA crop geometry.");
        if (width != _width || height != _height || stride != _stride || crop != _crop)
        {
            crop.Validate();
            for (int x = 0; x < targetWidth; x++)
                _columns[x] = Math.Min(width - 1, (int)((crop.Left + (x + .5) / targetWidth * crop.Width) * width));
            for (int y = 0; y < targetHeight; y++)
                _rows[y] = Math.Min(height - 1, (int)((crop.Top + (y + .5) / targetHeight * crop.Height) * height)) * (stride / 4);
            _width = width; _height = height; _stride = stride; _crop = crop;
        }
        if (crop == new VisionCrop(0, 0, 1, 1) && width == targetWidth && height == targetHeight && stride == width * 4)
        { sourceBytes[..targetBytes.Length].CopyTo(targetBytes); return; }
        var source = MemoryMarshal.Cast<byte, uint>(sourceBytes);
        var target = MemoryMarshal.Cast<byte, uint>(targetBytes);
        for (int y = 0; y < targetHeight; y++)
        {
            var sourceRow = source.Slice(_rows[y], width);
            var targetRow = target.Slice(y * targetWidth, targetWidth);
            for (int x = 0; x < targetWidth; x++) targetRow[x] = sourceRow[_columns[x]];
        }
    }
}
