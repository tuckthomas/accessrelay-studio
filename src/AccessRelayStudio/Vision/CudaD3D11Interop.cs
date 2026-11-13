using System.Runtime.InteropServices;

namespace EliteScriptStudio.Vision;

/// <summary>NVIDIA driver API only: no CUDA toolkit compiler or custom native DLL.
/// All allocations belong to CUDA device 0's retained primary context, also used by ORT.</summary>
internal sealed class CudaD3D11Interop : IDisposable
{
    private nint _context, _stream;
    private int _device, _disposed;
    private ulong _input, _scratch;
    private readonly nuint _bytes, _frameBytes;
    private readonly nint[] _oneResource = new nint[1];
    internal nint Pointer => unchecked((nint)_input);
    internal long Bytes => checked((long)_bytes);

    internal CudaD3D11Interop(int frameLength, int historyLength)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("D3D11/CUDA preprocessing requires Windows and an NVIDIA driver.");
        _frameBytes = checked((nuint)frameLength * 4); _bytes = checked(_frameBytes * (nuint)historyLength);
        if (frameLength <= 0 || historyLength is < 2 or > 16 || _bytes > 64u * 1024 * 1024)
            throw new InvalidDataException("GPU temporal input exceeds its 64 MiB tensor budget.");
        try
        {
            Check(cuInit(0), "initialize the NVIDIA driver"); Check(cuDeviceGet(out _device, 0), "select CUDA device 0");
            Check(cuDevicePrimaryCtxRetain(out _context, _device), "retain the inference device context");
            using var scope = Current();
            Check(cuStreamCreate(out _stream, 1), "create the preprocessing stream");
            Check(cuMemAlloc_v2(out _input, _bytes), "allocate the model tensor");
            Check(cuMemAlloc_v2(out _scratch, _bytes), "allocate bounded history-shift storage");
            Check(cuMemsetD8Async(_input, 0, _bytes, _stream), "initialize the model tensor");
            Check(cuStreamSynchronize(_stream), "finish tensor initialization");
        }
        catch (DllNotFoundException error) { Dispose(); throw new NotSupportedException("The NVIDIA CUDA driver is unavailable. Select CPU preprocessing or install a compatible NVIDIA driver.", error); }
        catch { Dispose(); throw; }
    }

    internal static nint FindAdapter()
    {
        try
        {
            Check(cuInit(0), "initialize the NVIDIA driver"); Check(cuDeviceGet(out int device, 0), "select CUDA device 0");
            Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in factoryId, out var factory));
            try
            {
                var enumerate = D3D11ComputeNative.Method<EnumAdapters>(factory, 12);
                for (uint i = 0; i < 32; i++)
                {
                    int result = enumerate(factory, i, out var adapter);
                    if (result == unchecked((int)0x887a0002)) break;
                    Marshal.ThrowExceptionForHR(result);
                    if (cuD3D11GetDevice(out int candidate, adapter) == 0 && candidate == device) return adapter;
                    Marshal.Release(adapter);
                }
            }
            finally { Marshal.Release(factory); }
        }
        catch (DllNotFoundException error) { throw new NotSupportedException("GPU preprocessing needs the NVIDIA driver and a D3D11 adapter matching CUDA device 0. Select CPU preprocessing.", error); }
        throw new NotSupportedException("No D3D11 capture adapter matches CUDA device 0. Select CPU preprocessing; no automatic adapter/backend fallback was used.");
    }

    internal nint Register(CaptureD3D11 graphics, nint buffer)
    {
        using var scope = Current();
        Guid dxgiId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(graphics.NativeDevice, in dxgiId, out var dxgi));
        nint adapter = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11ComputeNative.Method<GetAdapter>(dxgi, 7)(dxgi, out adapter));
            Check(cuD3D11GetDevice(out int device, adapter), "identify the capture adapter");
            if (device != _device) throw new NotSupportedException("The capture adapter differs from CUDA device 0. Restart with the matching adapter or select CPU preprocessing.");
            Check(cuGraphicsD3D11RegisterResource(out var resource, buffer, 0), "register the RGB buffer with CUDA");
            return resource;
        }
        finally { if (adapter != 0) Marshal.Release(adapter); Marshal.Release(dxgi); }
    }

    internal void Unregister(ref nint resource)
    {
        if (resource == 0) return;
        using var scope = Current(); nint old = resource; resource = 0;
        Check(cuGraphicsUnregisterResource(old), "release RGB buffer registration");
    }

    internal void StoreFrame(nint resource, int index, bool shift)
    {
        if (index < 0 || checked((nuint)(index + 1) * _frameBytes) > _bytes) throw new InvalidDataException("GPU temporal history index is invalid.");
        using var scope = Current(); _oneResource[0] = resource;
        Check(cuGraphicsMapResources(1, _oneResource, _stream), "acquire the prepared RGB buffer");
        try
        {
            Check(cuGraphicsResourceGetMappedPointer_v2(out ulong source, out nuint available, resource), "locate the RGB device allocation");
            if (available < _frameBytes) throw new InvalidDataException("The mapped RGB allocation is smaller than the model frame.");
            if (shift)
            {
                // CUDA memcpy does not allow overlapping ranges. Use the bounded
                // scratch allocation rather than relying on memmove semantics.
                Copy(_scratch, _input + (ulong)_frameBytes, _bytes - _frameBytes);
                Copy(_input, _scratch, _bytes - _frameBytes);
            }
            Copy(_input + checked((ulong)index * (ulong)_frameBytes), source, _frameBytes);
        }
        finally
        {
            try { Check(cuGraphicsUnmapResources(1, _oneResource, _stream), "return the RGB buffer to D3D11"); }
            finally { Check(cuStreamSynchronize(_stream), "complete GPU preprocessing transfers before inference"); }
        }
    }

    internal void RestartWithCurrent(int index)
    {
        if (index < 0 || checked((nuint)(index + 1) * _frameBytes) > _bytes) throw new InvalidDataException("GPU reacquisition index is invalid.");
        if (index == 0) return;
        using var scope = Current(); Copy(_input, _input + checked((ulong)index * (ulong)_frameBytes), _frameBytes);
        Check(cuStreamSynchronize(_stream), "restart temporal history after a frozen player");
    }

    internal void ReadTensor(float[] destination)
    {
        if (checked((nuint)destination.Length * 4) != _bytes) throw new ArgumentException("Probe tensor dimensions differ.");
        using var scope = Current(); var pinned = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try { Check(cuMemcpyDtoH_v2(pinned.AddrOfPinnedObject(), _input, _bytes), "read the verification tensor"); }
        finally { pinned.Free(); }
    }
    private void Copy(ulong destination, ulong source, nuint bytes) => Check(cuMemcpyDtoDAsync_v2(destination, source, bytes, _stream), "copy chronological GPU history");
    private ContextScope Current() => new(_context);
    private readonly struct ContextScope : IDisposable
    {
        private readonly nint _previous;
        internal ContextScope(nint context) { Check(cuCtxGetCurrent(out _previous), "read the current CUDA context"); Check(cuCtxSetCurrent(context), "select the inference context"); }
        public void Dispose() => Check(cuCtxSetCurrent(_previous), "restore the prior CUDA context");
    }
    private static void Check(int result, string action)
    { if (result != 0) throw new InvalidOperationException($"Could not {action} (CUDA driver result {result}). GPU preprocessing stopped; select CPU preprocessing to recover."); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _context == 0) return;
        try
        {
            using var scope = Current();
            if (_stream != 0) cuStreamSynchronize(_stream);
            if (_scratch != 0) { cuMemFree_v2(_scratch); _scratch = 0; }
            if (_input != 0) { cuMemFree_v2(_input); _input = 0; }
            if (_stream != 0) { cuStreamDestroy_v2(_stream); _stream = 0; }
        }
        finally { cuDevicePrimaryCtxRelease_v2(_device); _context = 0; }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(nint self, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapter(nint self, out nint adapter);
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuD3D11GetDevice(out int device, nint adapter);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuDevicePrimaryCtxRetain(out nint context, int device);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuDevicePrimaryCtxRelease_v2(int device);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuCtxGetCurrent(out nint context);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuCtxSetCurrent(nint context);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuStreamCreate(out nint stream, uint flags);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuStreamDestroy_v2(nint stream);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuStreamSynchronize(nint stream);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuMemAlloc_v2(out ulong pointer, nuint bytes);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuMemFree_v2(ulong pointer);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuMemsetD8Async(ulong pointer, byte value, nuint bytes, nint stream);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuMemcpyDtoDAsync_v2(ulong destination, ulong source, nuint bytes, nint stream);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuMemcpyDtoH_v2(nint destination, ulong source, nuint bytes);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuGraphicsD3D11RegisterResource(out nint resource, nint buffer, uint flags);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuGraphicsUnregisterResource(nint resource);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuGraphicsMapResources(uint count, [In] nint[] resources, nint stream);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuGraphicsUnmapResources(uint count, [In] nint[] resources, nint stream);
    [DllImport("nvcuda.dll", ExactSpelling = true)] private static extern int cuGraphicsResourceGetMappedPointer_v2(out ulong pointer, out nuint bytes, nint resource);
}
