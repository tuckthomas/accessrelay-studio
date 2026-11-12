using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace EliteScriptStudio.Vision;

/// <summary>Small D3D11 ABI adapter. Slots are from the Windows SDK's d3d11.h interfaces.
/// All immediate-context calls are serialized by RemotePlayCapture.</summary>
internal sealed class CaptureD3D11 : IDisposable
{
    private nint _device, _context, _staging;
    private TextureDescription _stagingDescription;
    private CreateTexture2D? _createTexture;
    private CopyResource? _copy;
    private MapResource? _map;
    private UnmapResource? _unmap;
    private GetTextureDescription? _getDescription;
    private IDirect3DDevice? _winrtDevice;

    internal CaptureD3D11()
    {
        try
        {
            // D3D_DRIVER_TYPE_HARDWARE, D3D11_CREATE_DEVICE_BGRA_SUPPORT, SDK version 7.
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7,
                out _device, out _, out _context));
            Guid dxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_device, in dxgiDeviceId, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }

            Guid multithreadId = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
            if (Marshal.QueryInterface(_context, in multithreadId, out var multithread) >= 0)
            {
                try { Method<SetMultithreadProtected>(multithread, 5)(multithread, true); }
                finally { Marshal.Release(multithread); }
            }
            _createTexture = Method<CreateTexture2D>(_device, 5);
            _copy = Method<CopyResource>(_context, 47);
            _map = Method<MapResource>(_context, 14);
            _unmap = Method<UnmapResource>(_context, 15);
        }
        catch { Dispose(); throw; }
    }

    internal IDirect3DDevice Device => _winrtDevice ?? throw new ObjectDisposedException(nameof(CaptureD3D11));

    internal bool CopyFrame(Direct3D11CaptureFrame frame, LatestFrameBuffer destination,
        long ticks, long sequence, long generation)
    {
        nint surface = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        nint access = 0, texture = 0;
        try
        {
            Guid accessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessId, out access));
            Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Marshal.ThrowExceptionForHR(Method<GetInterface>(access, 3)(access, ref textureId, out texture));
            _getDescription ??= Method<GetTextureDescription>(texture, 10);
            _getDescription(texture, out var description);
            int width = frame.ContentSize.Width, height = frame.ContentSize.Height;
            if (width <= 0 || height <= 0 || width > description.Width || height > description.Height)
                return false; // The frame pool will be resized before processing the next frame.
            if (description.Format != 87 || description.SampleCount != 1)
                throw new InvalidDataException("The capture source did not provide a single-sample BGRA8 surface.");
            if (checked((long)width * height * 4) > LatestFrameBuffer.MaximumFrameBytes)
                throw new InvalidDataException("The capture window is larger than the supported 4K pixel budget. Resize it before starting vision.");

            long readbackStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_staging == 0 || description.Width != _stagingDescription.Width ||
                description.Height != _stagingDescription.Height || description.Format != _stagingDescription.Format)
            {
                Release(ref _staging);
                _stagingDescription = description;
                _stagingDescription.Usage = 3; // D3D11_USAGE_STAGING
                _stagingDescription.BindFlags = 0;
                _stagingDescription.CpuAccessFlags = 0x20000; // D3D11_CPU_ACCESS_READ
                _stagingDescription.MiscFlags = 0;
                Marshal.ThrowExceptionForHR(_createTexture!(_device, ref _stagingDescription, 0, out _staging));
            }
            _copy!(_context, _staging, texture);
            Marshal.ThrowExceptionForHR(_map!(_context, _staging, 0, 1, 0, out var mapped));
            try { return destination.PublishMapped(mapped.Data, mapped.RowPitch, width, height, ticks, sequence, generation, readbackStarted); }
            finally { _unmap!(_context, _staging, 0); }
        }
        finally
        {
            Release(ref texture);
            Release(ref access);
            Release(ref surface);
        }
    }

    public void Dispose()
    {
        Release(ref _staging);
        _winrtDevice?.Dispose();
        _winrtDevice = null;
        Release(ref _context);
        Release(ref _device);
    }

    internal static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var name));
        nint factory = 0, item = 0;
        try
        {
            Guid factoryId = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref factoryId, out factory));
            Guid itemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(Method<CreateForWindow>(factory, 3)(factory, hwnd, ref itemId, out item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            Release(ref item);
            Release(ref factory);
            WindowsDeleteString(name);
        }
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    private static void Release(ref nint instance)
    {
        nint previous = instance;
        instance = 0;
        if (previous != 0) Marshal.Release(previous);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDescription
    {
        internal uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality,
            Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedResource { internal nint Data; internal uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2D(nint self, ref TextureDescription description, nint initial, out nint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTextureDescription(nint self, out TextureDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResource(nint self, nint destination, nint source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapResource(nint self, nint resource, uint subresource, uint type, uint flags, out MappedResource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapResource(nint self, nint resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterface(nint self, ref Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindow(nint self, nint hwnd, ref Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetMultithreadProtected(nint self, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint featureLevels, uint count, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
}
