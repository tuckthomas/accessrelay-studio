using System.Runtime.InteropServices;

namespace EliteScriptStudio.Vision;

/// <summary>Three reusable pixel slots: at most one pending frame, with outstanding consumers
/// retaining exclusive slots. A slow consumer causes drops rather than unbounded allocations.</summary>
internal sealed class LatestFrameBuffer : IDisposable
{
    internal const int MaximumFrameBytes = 3840 * 2160 * 4;
    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private Slot? _pending;
    private bool _disposed;
    private long _dropped;
    private ulong _latestFingerprint;

    internal LatestFrameBuffer()
    {
        _slots = [new(this), new(this), new(this)];
    }

    public long DroppedFrames => Interlocked.Read(ref _dropped);
    internal ulong LatestFingerprint { get { lock (_gate) return _latestFingerprint; } }
    internal void NoteDroppedFrame() => Interlocked.Increment(ref _dropped);

    internal bool PublishMapped(nint pixels, uint rowPitch, int width, int height,
        long captureTicks, long sequence, long sourceGeneration)
    {
        int stride = checked(width * 4), bytes = checked(stride * height);
        if (width <= 0 || height <= 0 || bytes > MaximumFrameBytes || rowPitch < stride || pixels == 0)
            throw new InvalidDataException("The capture frame has an unsupported size or row layout. Resize the source window to 4K or smaller.");

        Slot? slot;
        lock (_gate)
        {
            if (_disposed)
                return false;
            slot = null;
            foreach (var candidate in _slots)
                if (candidate.State == SlotState.Free) { slot = candidate; break; }
            if (slot is null && _pending is { } replaced)
            {
                slot = replaced;
                _pending = null;
                Interlocked.Increment(ref _dropped);
            }
            if (slot is null)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            slot.State = SlotState.Writing;
        }

        try
        {
            if (slot.Buffer.Length != bytes)
                slot.Buffer = GC.AllocateUninitializedArray<byte>(bytes);
            for (int row = 0; row < height; row++)
                Marshal.Copy(pixels + checked((nint)((long)row * rowPitch)), slot.Buffer, row * stride, stride);
            // Sparse window-image fingerprint is a freeze hint, not proof that gameplay advanced.
            ulong fingerprint = 14695981039346656037;
            for (int row = 0; row < height; row += Math.Max(1, height / 36))
                for (int column = 0; column < width; column += Math.Max(1, width / 64))
                {
                    int offset = row * stride + column * 4;
                    fingerprint = unchecked((fingerprint ^ slot.Buffer[offset]) * 1099511628211);
                    fingerprint = unchecked((fingerprint ^ slot.Buffer[offset + 1]) * 1099511628211);
                    fingerprint = unchecked((fingerprint ^ slot.Buffer[offset + 2]) * 1099511628211);
                }

            lock (_gate)
            {
                if (_disposed)
                {
                    slot.State = SlotState.Free;
                    slot.Buffer = [];
                    return false;
                }
                if (_pending is { } previous)
                {
                    previous.State = SlotState.Free;
                    Interlocked.Increment(ref _dropped);
                }
                slot.Width = width;
                slot.Height = height;
                slot.CaptureTicks = captureTicks;
                slot.Sequence = sequence;
                slot.Generation = sourceGeneration;
                slot.State = SlotState.Pending;
                _pending = slot;
                _latestFingerprint = fingerprint;
                return true;
            }
        }
        catch
        {
            lock (_gate) slot.State = SlotState.Free;
            throw;
        }
    }

    public bool TryAcquireLatest(out CapturedVisionFrame? frame)
    {
        lock (_gate)
        {
            if (_disposed || _pending is not { } slot)
            {
                frame = null;
                return false;
            }
            _pending = null;
            slot.State = SlotState.Leased;
            frame = new(slot.Buffer, slot.Width, slot.Height, slot.CaptureTicks,
                slot.Sequence, slot.Generation, slot.Release);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_pending is { } slot)
            {
                slot.State = SlotState.Free;
                _pending = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = null;
            foreach (var slot in _slots)
                if (slot.State is SlotState.Free or SlotState.Pending)
                {
                    slot.State = SlotState.Free;
                    slot.Buffer = [];
                }
        }
    }

    private void Release(Slot slot)
    {
        lock (_gate)
        {
            slot.State = SlotState.Free;
            if (_disposed)
                slot.Buffer = [];
        }
    }

    private enum SlotState { Free, Writing, Pending, Leased }
    private sealed class Slot
    {
        internal Slot(LatestFrameBuffer owner) => Release = () => owner.Release(this);
        internal byte[] Buffer = [];
        internal SlotState State;
        internal int Width, Height;
        internal long CaptureTicks, Sequence, Generation;
        internal Action Release { get; }
    }
}
