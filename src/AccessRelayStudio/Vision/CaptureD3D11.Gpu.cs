using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace EliteScriptStudio.Vision;

internal sealed partial class CaptureD3D11
{
    internal bool CopyGpuFrame(Direct3D11CaptureFrame frame, GpuCaptureBuffer destination, long ticks, long sequence, long generation)
    {
        nint surface = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface), access = 0, texture = 0;
        try
        {
            Guid accessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"), textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessId, out access));
            Marshal.ThrowExceptionForHR(Method<GetInterface>(access, 3)(access, ref textureId, out texture));
            Method<GetTextureDescription>(texture, 10)(texture, out var description);
            int width = frame.ContentSize.Width, height = frame.ContentSize.Height;
            if (width != description.Width || height != description.Height) return false;
            if (description.Format != 87 || description.SampleCount != 1) throw new InvalidDataException("GPU capture requires a BGRA8 single-sample source.");
            return destination.Publish(texture, width, height, ticks, sequence, generation);
        }
        finally { Release(ref texture); Release(ref access); Release(ref surface); }
    }

    internal void ReadTexture(nint texture, int width, int height, byte[] destination)
    {
        lock (Sync)
        {
            Method<GetTextureDescription>(texture, 10)(texture, out var description);
            if (destination.Length != checked(width * height * 4)) throw new InvalidDataException("CPU readback size differs from the GPU lease.");
            if (_staging == 0 || description.Width != _stagingDescription.Width || description.Height != _stagingDescription.Height || description.Format != _stagingDescription.Format)
            {
                Release(ref _staging); _stagingDescription = description;
                _stagingDescription.Usage = 3; _stagingDescription.BindFlags = 0; _stagingDescription.CpuAccessFlags = 0x20000; _stagingDescription.MiscFlags = 0;
                Marshal.ThrowExceptionForHR(_createTexture!(_device, ref _stagingDescription, 0, out _staging));
            }
            _copy!(_context, _staging, texture);
            Marshal.ThrowExceptionForHR(_map!(_context, _staging, 0, 1, 0, out var mapped));
            try
            {
                int stride = width * 4;
                for (int row = 0; row < height; row++) Marshal.Copy(mapped.Data + checked((nint)((long)row * mapped.RowPitch)), destination, row * stride, stride);
            }
            finally { _unmap!(_context, _staging, 0); }
        }
    }
}
