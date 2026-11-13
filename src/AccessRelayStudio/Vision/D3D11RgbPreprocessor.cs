using System.Runtime.InteropServices;
using static EliteScriptStudio.Vision.D3D11ComputeNative;

namespace EliteScriptStudio.Vision;

internal readonly record struct GpuRgbResult(nint Buffer, bool PlayerChanged, ulong SourceFingerprint);

/// <summary>GPU v2 RGB pixels remain in reusable structured buffers. Only 16 bytes
/// of content/freeze metadata are read back in the production inference path.</summary>
internal sealed class D3D11RgbPreprocessor : IDisposable
{
    private readonly CaptureD3D11 _graphics;
    private readonly IDisposable _deviceLease;
    private readonly RgbSamplingPlan _plan;
    private readonly nint[] _rgb = new nint[2], _rgbSrv = new nint[2], _rgbUav = new nint[2];
    private readonly nint[] _srvs = new nint[4], _uavs = new nint[2], _constants = new nint[1];
    private readonly nint[] _emptySrvs = new nint[4], _emptyUavs = new nint[2];
    private readonly uint[] _zero = new uint[4];
    private readonly int[] _metadata = new int[4];
    private nint _shader, _x, _y, _xSrv, _ySrv, _flags, _flagsUav, _flagsReadback, _constantData, _rgbReadback;
    private int _previous, _disposed;
    private bool _hasPrevious;
    internal int FrameLength => _plan.FrameLength;
    internal IReadOnlyList<nint> Buffers => _rgb;
    internal CaptureD3D11 Graphics => _graphics;

    internal D3D11RgbPreprocessor(CaptureD3D11 graphics, RgbSamplingPlan plan)
    {
        _graphics = graphics; _plan = plan; _deviceLease = graphics.Retain();
        try
        {
            if (graphics.FeatureLevel < 0xb000) throw new NotSupportedException("GPU RGB preprocessing requires D3D11 feature level 11 or later. Select CPU preprocessing.");
            lock (graphics.Sync)
            {
                nint device = graphics.NativeDevice;
                _shader = Compile(device, Shader);
                _x = Buffer(device, checked((uint)(plan.Width * 16)), 8, 16, MemoryMarshal.AsBytes(plan.Horizontal.AsSpan()).ToArray());
                _y = Buffer(device, checked((uint)(plan.Height * 16)), 8, 16, MemoryMarshal.AsBytes(plan.Vertical.AsSpan()).ToArray());
                _xSrv = BufferView(device, _x, (uint)plan.Width, false); _ySrv = BufferView(device, _y, (uint)plan.Height, false);
                for (int i = 0; i < 2; i++)
                {
                    _rgb[i] = Buffer(device, checked((uint)plan.FrameLength * 4), 8 | 128, 4);
                    _rgbSrv[i] = BufferView(device, _rgb[i], (uint)plan.FrameLength, false);
                    _rgbUav[i] = BufferView(device, _rgb[i], (uint)plan.FrameLength, true);
                }
                _flags = Buffer(device, 16, 128, 4); _flagsUav = BufferView(device, _flags, 4, true);
                _flagsReadback = Buffer(device, 16, 0, staging: true);
                _constants[0] = Buffer(device, 64, 4); _constantData = Marshal.AllocHGlobal(64);
            }
        }
        catch { Dispose(); throw; }
    }

    internal void ResetContent() => _hasPrevious = false;

    internal GpuRgbResult Process(nint sourceView)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_graphics.Sync)
        {
            nint context = _graphics.NativeContext; int next = 1 - _previous;
            var data = new Constants { Width = (uint)_plan.Width, Height = (uint)_plan.Height,
                SourceWidth = (uint)_plan.SourceWidth, SourceHeight = (uint)_plan.SourceHeight, HasPrevious = _hasPrevious ? 1u : 0,
                MeanR = _plan.Mean[0], MeanG = _plan.Mean[1], MeanB = _plan.Mean[2],
                StdR = _plan.StandardDeviation[0], StdG = _plan.StandardDeviation[1], StdB = _plan.StandardDeviation[2] };
            Marshal.StructureToPtr(data, _constantData, false);
            Method<Update>(context, 48)(context, _constants[0], 0, 0, _constantData, 0, 0);
            Method<ClearUint>(context, 51)(context, _flagsUav, _zero);
            _srvs[0] = sourceView; _srvs[1] = _xSrv; _srvs[2] = _ySrv; _srvs[3] = _rgbSrv[_previous];
            _uavs[0] = _rgbUav[next]; _uavs[1] = _flagsUav;
            Method<SetResources>(context, 67)(context, 0, 4, _srvs);
            Method<SetUavs>(context, 68)(context, 0, 2, _uavs, 0);
            Method<SetResources>(context, 71)(context, 0, 1, _constants);
            Method<SetShader>(context, 69)(context, _shader, 0, 0);
            try { Method<Dispatch>(context, 41)(context, (uint)(_plan.Width + 7) / 8, (uint)(_plan.Height + 7) / 8, 1); }
            finally
            {
                Method<SetResources>(context, 67)(context, 0, 4, _emptySrvs);
                Method<SetUavs>(context, 68)(context, 0, 2, _emptyUavs, 0);
                Method<SetShader>(context, 69)(context, 0, 0, 0);
            }
            Method<CopyResource>(context, 47)(context, _flagsReadback, _flags);
            Marshal.ThrowExceptionForHR(Method<Map>(context, 14)(context, _flagsReadback, 0, 1, 0, out var mapped));
            try { Marshal.Copy(mapped.Data, _metadata, 0, 4); }
            finally { Method<Unmap>(context, 15)(context, _flagsReadback, 0); }
            _previous = next; _hasPrevious = true;
            return new(_rgb[next], _metadata[0] != 0, (uint)_metadata[1] | ((ulong)(uint)_metadata[2] << 32));
        }
    }

    // Verification only. Production CUDA input consumes Buffer directly.
    internal void ReadCurrent(Span<float> destination)
    {
        if (destination.Length != FrameLength || !_hasPrevious) throw new InvalidOperationException("No matching GPU RGB result is available.");
        lock (_graphics.Sync)
        {
            _rgbReadback = _rgbReadback != 0 ? _rgbReadback : Buffer(_graphics.NativeDevice, checked((uint)FrameLength * 4), 0, staging: true);
            nint context = _graphics.NativeContext;
            Method<CopyResource>(context, 47)(context, _rgbReadback, _rgb[_previous]);
            Marshal.ThrowExceptionForHR(Method<Map>(context, 14)(context, _rgbReadback, 0, 1, 0, out var mapped));
            try { var copy = new float[FrameLength]; Marshal.Copy(mapped.Data, copy, 0, copy.Length); copy.CopyTo(destination); }
            finally { Method<Unmap>(context, 15)(context, _rgbReadback, 0); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_graphics.Sync)
        {
            Release(ref _rgbReadback); Release(ref _flagsReadback); Release(ref _flagsUav); Release(ref _flags);
            for (int i = 0; i < 2; i++) { Release(ref _rgbUav[i]); Release(ref _rgbSrv[i]); Release(ref _rgb[i]); }
            Release(ref _xSrv); Release(ref _ySrv); Release(ref _x); Release(ref _y);
            Release(ref _constants[0]); Release(ref _shader);
            if (_constantData != 0) { Marshal.FreeHGlobal(_constantData); _constantData = 0; }
        }
        _deviceLease.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        internal uint Width, Height, SourceWidth, SourceHeight, HasPrevious, Pad0, Pad1, Pad2;
        internal float MeanR, MeanG, MeanB, Pad3, StdR, StdG, StdB, Pad4;
    }

    private const string Shader = """
struct AxisSample { uint first; uint second; float fraction; uint padding; };
Texture2D<float4> Source : register(t0);
StructuredBuffer<AxisSample> Horizontal : register(t1);
StructuredBuffer<AxisSample> Vertical : register(t2);
StructuredBuffer<float> Previous : register(t3);
RWStructuredBuffer<float> Output : register(u0);
RWStructuredBuffer<uint> Flags : register(u1);
cbuffer Settings : register(b0) {
    uint Width; uint Height; uint SourceWidth; uint SourceHeight;
    uint HasPrevious; uint Pad0; uint Pad1; uint Pad2;
    float4 Mean; float4 StandardDeviation;
};
float3 Bytes(uint x, uint y) { return round(Source.Load(int3(x, y, 0)).rgb * 255.0f); }
uint2 HashByte(uint2 hash, uint value) {
    hash.x ^= value;
    // Split the small multiplier into 16-bit limbs: FXC/SM5 has no GLSL
    // umulExtended intrinsic, and each intermediate fits in a uint.
    uint lowWord = (hash.x & 65535u) * 435u;
    uint highWord = (hash.x >> 16u) * 435u + (lowWord >> 16u);
    uint low = (highWord << 16u) | (lowWord & 65535u), high = highWord >> 16u;
    hash.y = hash.y * 435u + hash.x * 256u + high;
    hash.x = low;
    return hash;
}
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (id.x >= Width || id.y >= Height) return;
    AxisSample sx = Horizontal[id.x], sy = Vertical[id.y];
    precise float3 p00 = Bytes(sx.first, sy.first), p10 = Bytes(sx.second, sy.first);
    precise float3 p01 = Bytes(sx.first, sy.second), p11 = Bytes(sx.second, sy.second);
    precise float3 first = p00 + (p10 - p00) * sx.fraction;
    precise float3 second = p01 + (p11 - p01) * sx.fraction;
    precise float3 unit = (first + (second - first) * sy.fraction) / 255.0f;
    precise float3 rgb = (unit - Mean.rgb) / StandardDeviation.rgb;
    uint offset = id.y * Width + id.x, plane = Width * Height;
    Output[offset] = rgb.r; Output[plane + offset] = rgb.g; Output[2 * plane + offset] = rgb.b;
    if (!HasPrevious || rgb.r != Previous[offset] || rgb.g != Previous[plane + offset] || rgb.b != Previous[2 * plane + offset]) {
        uint ignored; InterlockedOr(Flags[0], 1u, ignored);
    }
    // Same sparse full-window FNV hint as the CPU source. Selected-player freeze
    // comparison above remains exact and independent of animated outside overlays.
    if (id.x == 0 && id.y == 0) {
        uint2 hash = uint2(0x84222325u, 0xcbf29ce4u);
        for (uint y = 0; y < SourceHeight; y += max(1u, SourceHeight / 36u)) {
            for (uint x = 0; x < SourceWidth; x += max(1u, SourceWidth / 64u)) {
                uint3 pixel = (uint3)Bytes(x, y);
                hash = HashByte(hash, pixel.b); hash = HashByte(hash, pixel.g); hash = HashByte(hash, pixel.r);
            }
        }
        Flags[1] = hash.x; Flags[2] = hash.y;
    }
}
""";
}
