using System.Runtime.InteropServices;

namespace EliteScriptStudio.Vision;

/// <summary>Fixed Windows SDK D3D11 ABI declarations; no external plugin code.</summary>
internal static class D3D11ComputeNative
{
    internal static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    internal static void Release(ref nint instance) { nint old = instance; instance = 0; if (old != 0) Marshal.Release(old); }

    [StructLayout(LayoutKind.Sequential)] internal struct BufferDescription
    { internal uint Bytes, Usage, BindFlags, CpuAccess, Misc, Stride; }
    [StructLayout(LayoutKind.Sequential)] internal struct TextureDescription
    { internal uint Width, Height, Mips, ArraySize, Format, Samples, Quality, Usage, BindFlags, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)] internal struct SubresourceData
    { internal nint Data; internal uint RowPitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)] internal struct Mapped
    { internal nint Data; internal uint RowPitch, DepthPitch; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateBuffer(nint self, ref BufferDescription description, nint data, out nint buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateTexture(nint self, ref TextureDescription description, nint data, out nint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateView(nint self, nint resource, nint description, out nint view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int CreateComputeShader(nint self, nint data, nuint length, nint linkage, out nint shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void SetResources(nint self, uint start, uint count, [In] nint[] views);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void SetUavs(nint self, uint start, uint count, [In] nint[] views, nint counts);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void SetShader(nint self, nint shader, nint instances, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Dispatch(nint self, uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void CopyResource(nint self, nint destination, nint source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Update(nint self, nint destination, uint subresource, nint box, nint source, uint rowPitch, uint depthPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void ClearUint(nint self, nint view, [In] uint[] values);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Map(nint self, nint resource, uint subresource, uint type, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Unmap(nint self, nint resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Flush(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint BlobPointer(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nuint BlobSize(nint self);
    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] source, nuint length, string? sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags, uint effectFlags, out nint code, out nint errors);

    internal static nint Buffer(nint device, uint bytes, uint bind, uint stride = 0, byte[]? initial = null, bool staging = false)
    {
        var description = new BufferDescription { Bytes = bytes, BindFlags = bind, Stride = stride,
            Misc = stride != 0 ? 64u : 0, Usage = staging ? 3u : 0, CpuAccess = staging ? 0x20000u : 0 };
        nint data = 0; GCHandle pinned = default;
        try
        {
            if (initial is not null)
            {
                if (initial.Length != bytes) throw new ArgumentException("Initial GPU buffer size differs.");
                pinned = GCHandle.Alloc(initial, GCHandleType.Pinned);
                data = Marshal.AllocHGlobal(Marshal.SizeOf<SubresourceData>());
                Marshal.StructureToPtr(new SubresourceData { Data = pinned.AddrOfPinnedObject() }, data, false);
            }
            Marshal.ThrowExceptionForHR(Method<CreateBuffer>(device, 3)(device, ref description, data, out var buffer));
            return buffer;
        }
        finally { if (data != 0) Marshal.FreeHGlobal(data); if (pinned.IsAllocated) pinned.Free(); }
    }

    internal static nint BufferView(nint device, nint buffer, uint elements, bool unordered)
    {
        // SRV union is 16 bytes; UAV union is 12 bytes. Zero initialize the larger
        // allocation; both Buffer descriptors start with FirstElement/NumElements.
        nint description = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(new int[] { 0, 1, 0, checked((int)elements), 0, 0 }, 0, description, 6);
            Marshal.ThrowExceptionForHR(Method<CreateView>(device, unordered ? 8 : 7)(device, buffer, description, out var view));
            return view;
        }
        finally { Marshal.FreeHGlobal(description); }
    }

    internal static nint Compile(nint device, string source)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(source); nint code = 0, errors = 0;
        try
        {
            int result = D3DCompile(bytes, (nuint)bytes.Length, "AccessRelay.RgbV2", 0, 0, "main", "cs_5_0", 0x2000 | 0x800, 0, out code, out errors);
            if (result < 0)
            {
                string detail = errors != 0 ? Marshal.PtrToStringAnsi(Method<BlobPointer>(errors, 3)(errors),
                    checked((int)Math.Min(Method<BlobSize>(errors, 4)(errors), 8192))) ?? "" : "";
                throw new InvalidOperationException("The built-in RGB compute shader failed to compile: " + detail, Marshal.GetExceptionForHR(result));
            }
            Marshal.ThrowExceptionForHR(Method<CreateComputeShader>(device, 18)(device, Method<BlobPointer>(code, 3)(code),
                Method<BlobSize>(code, 4)(code), 0, out var shader));
            return shader;
        }
        finally { Release(ref errors); Release(ref code); }
    }
}
