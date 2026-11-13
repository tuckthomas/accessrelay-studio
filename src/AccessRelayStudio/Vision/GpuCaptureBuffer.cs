using System.Diagnostics;
using System.Runtime.InteropServices;
using static EliteScriptStudio.Vision.D3D11ComputeNative;

namespace EliteScriptStudio.Vision;

/// <summary>Three owned GPU textures; pending replacement cannot overwrite a
/// consumer. Device references survive capture shutdown until leases are released.</summary>
internal sealed class GpuCaptureBuffer(CaptureD3D11 graphics) : IDisposable
{
    private readonly object _gate = new();
    private readonly GpuFrameSurface[] _slots = [new(graphics), new(graphics), new(graphics)];
    private GpuFrameSurface? _pending;
    private bool _disposed;
    private long _dropped, _contentTicks, _lastHashedSequence, _generation = -1;
    private ulong _fingerprint;
    internal long DroppedFrames => Interlocked.Read(ref _dropped);
    internal void NoteDroppedFrame() => Interlocked.Increment(ref _dropped);
    internal long ContentChangedTicks => Interlocked.Read(ref _contentTicks);

    internal bool Publish(nint source, int width, int height, long ticks, long sequence, long generation)
    {
        GpuFrameSurface? slot;
        lock (_gate)
        {
            if (_disposed) return false;
            slot = _slots.FirstOrDefault(item => item.State == 0);
            if (slot is null && _pending is { } replaced) { slot = replaced; _pending = null; NoteDroppedFrame(); }
            if (slot is null) { NoteDroppedFrame(); return false; }
            slot.State = 1;
        }
        try
        {
            slot.Copy(source, width, height); slot.Ticks = ticks; slot.Sequence = sequence; slot.Generation = generation;
            lock (_gate)
            {
                if (_disposed) { slot.State = 0; slot.Dispose(); return false; }
                if (_pending is { } old) { old.State = 0; NoteDroppedFrame(); }
                slot.State = 2; _pending = slot; _generation = generation; return true;
            }
        }
        catch { lock (_gate) { slot.State = 0; if (_disposed) slot.Dispose(); } throw; }
    }

    internal bool TryAcquireLatest(out CapturedVisionFrame? frame)
    {
        lock (_gate)
        {
            if (_disposed || _pending is not { } slot) { frame = null; return false; }
            _pending = null; slot.State = 3;
            slot.ReportFingerprint = hash => Report(slot.Sequence, slot.Ticks, slot.Generation, hash);
            frame = new(slot, slot.Width, slot.Height, slot.Ticks, slot.Sequence, slot.Generation, () => Release(slot));
            return true;
        }
    }
    private void Report(long sequence, long ticks, long generation, ulong fingerprint)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || sequence <= _lastHashedSequence) return;
            if (_lastHashedSequence == 0 || fingerprint != _fingerprint) _contentTicks = ticks;
            _lastHashedSequence = sequence; _fingerprint = fingerprint;
        }
    }
    private void Release(GpuFrameSurface slot)
    { lock (_gate) { slot.State = 0; slot.ReportFingerprint = null; if (_disposed) slot.Dispose(); } }
    internal void Clear()
    { lock (_gate) { if (_pending is { } slot) { slot.State = 0; _pending = null; } _contentTicks = _lastHashedSequence = 0; _fingerprint = 0; _generation = -1; } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _pending = null;
            foreach (var slot in _slots) if (slot.State != 3 && slot.State != 1) slot.Dispose();
        }
    }
}

internal sealed class GpuFrameSurface(CaptureD3D11 graphics) : ICapturedFrameSurface, IDisposable
{
    private readonly IDisposable _deviceLease = graphics.Retain();
    private nint _texture, _view;
    private byte[]? _cpu;
    private int _disposed;
    internal int State, Width, Height;
    internal long Ticks, Sequence, Generation;
    internal Action<ulong>? ReportFingerprint;
    internal CaptureD3D11 Graphics => graphics;
    internal nint View => _view;
    internal nint Texture => _texture;
    public long CpuReadbackBytes { get; private set; }
    public double CpuReadbackMs { get; private set; }

    internal void Copy(nint source, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (width <= 0 || height <= 0 || checked((long)width * height * 4) > LatestFrameBuffer.MaximumFrameBytes)
            throw new InvalidDataException("GPU capture supports nonempty BGRA frames up to 4K.");
        lock (graphics.Sync)
        {
            if (_texture == 0 || width != Width || height != Height)
            {
                Release(ref _view); Release(ref _texture);
                var description = new TextureDescription { Width = (uint)width, Height = (uint)height, Mips = 1, ArraySize = 1, Format = 87, Samples = 1, BindFlags = 8 };
                Marshal.ThrowExceptionForHR(Method<CreateTexture>(graphics.NativeDevice, 5)(graphics.NativeDevice, ref description, 0, out _texture));
                Marshal.ThrowExceptionForHR(Method<CreateView>(graphics.NativeDevice, 7)(graphics.NativeDevice, _texture, 0, out _view));
                Width = width; Height = height; _cpu = null;
            }
            Method<CopyResource>(graphics.NativeContext, 47)(graphics.NativeContext, _texture, source);
            Method<Flush>(graphics.NativeContext, 111)(graphics.NativeContext);
            CpuReadbackBytes = 0; CpuReadbackMs = 0;
        }
    }

    public byte[] ReadPixels()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (CpuReadbackBytes != 0) return _cpu!;
        long started = Stopwatch.GetTimestamp();
        lock (graphics.Sync)
        {
            _cpu ??= new byte[checked(Width * Height * 4)];
            graphics.ReadTexture(_texture, Width, Height, _cpu);
            CpuReadbackBytes = _cpu.Length; CpuReadbackMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        ulong hash = 14695981039346656037;
        for (int y = 0; y < Height; y += Math.Max(1, Height / 36))
            for (int x = 0; x < Width; x += Math.Max(1, Width / 64))
                for (int c = 0; c < 3; c++) hash = unchecked((hash ^ _cpu[(y * Width + x) * 4 + c]) * 1099511628211);
        ReportFingerprint?.Invoke(hash);
        return _cpu;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (graphics.Sync) { Release(ref _view); Release(ref _texture); _cpu = null; }
        _deviceLease.Dispose();
    }
}
