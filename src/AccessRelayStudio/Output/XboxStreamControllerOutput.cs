using System.Buffers.Binary;
using System.Diagnostics;
using EliteScriptStudio.Core;

namespace EliteScriptStudio.Output;

/// <summary>An open, ordered WebRTC 'input' channel with protocol 1.0. TrySend must copy
/// or accept the bytes before returning and report backpressure without blocking the arbiter.
/// Disposal closes only this channel. The session owns the peer and video track.</summary>
internal interface IXboxInputChannel : IDisposable
{
    bool IsOpen { get; }
    bool TrySend(ReadOnlySpan<byte> report);
}

/// <summary>One owner per negotiated input channel. The runtime calls TryWrite directly
/// from physical sampling and scheduled output. Packet layout follows xbox-xcloud-player.</summary>
internal sealed class XboxStreamControllerOutput : IControllerOutput
{
    private readonly object _gate = new();
    private readonly IXboxInputChannel _channel;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private uint _sequence;
    private bool _failed, _disposed, _neutral;
    private ControllerState _lastSent;
    private long _stateChanges;
    internal long StateChanges => Interlocked.Read(ref _stateChanges);

    internal XboxStreamControllerOutput(IXboxInputChannel channel)
    {
        _channel = channel;
        try
        {
            Span<byte> metadata = stackalloc byte[15];
            WriteHeader(metadata, 8, 0);
            metadata[14] = 1; // Upstream client metadata advertises one touch point.
            _failed = !channel.IsOpen || !channel.TrySend(metadata);
        }
        catch { channel.Dispose(); throw; }
    }

    public bool UsesVirtualDevice => false;
    public bool IsAvailable { get { lock (_gate) return !_disposed && !_failed && _channel.IsOpen; } }
    public string Status => IsAvailable ? "Direct Xbox controller connected." : "The Xbox input channel is unavailable.";

    // A failed write revokes the old lease. Only an explicit new attachment can resume it.
    internal bool PrepareAttachment()
    {
        lock (_gate)
        {
            if (_disposed || !_channel.IsOpen) return false;
            if (!_failed) return true;
            if (!SendGamepad(default)) return false;
            _neutral = true;
            _failed = false;
            return true;
        }
    }

    public bool TryWrite(ControllerState state)
    {
        lock (_gate)
        {
            if (_disposed || _failed || !_channel.IsOpen) return false;
            _neutral = false;
            if (!SendGamepad(state)) { _failed = true; return false; }
            _neutral = state == default;
            return true;
        }
    }

    private bool SendGamepad(ControllerState state)
    {
        Span<byte> report = stackalloc byte[38];
        WriteHeader(report, 2, unchecked(++_sequence));
        report[14] = 1; // One gamepad report for controller zero.
        report[15] = 0;
        int buttons = state.Buttons;
        int wireButtons = ((buttons & 0x000F) << 8)  // D-pad
            | ((buttons & 0x0030) >> 2)              // Menu, View
            | ((buttons & 0x00C0) << 8)              // Thumb clicks
            | ((buttons & 0x0300) << 4)              // Bumpers
            | ((buttons & 0x0400) >> 9)              // Guide/Nexus
            | ((buttons & 0xF000) >> 8);             // A, B, X, Y
        BinaryPrimitives.WriteUInt16LittleEndian(report[16..], (ushort)wireButtons);
        // Studio and the wire both use positive-up Y. Upstream clamps axes at +/-32767.
        WriteAxis(report[18..], state.LeftX);
        WriteAxis(report[20..], state.LeftY);
        WriteAxis(report[22..], state.RightX);
        WriteAxis(report[24..], state.RightY);
        BinaryPrimitives.WriteUInt16LittleEndian(report[26..], (ushort)(257 * state.LeftTrigger));
        BinaryPrimitives.WriteUInt16LittleEndian(report[28..], (ushort)(257 * state.RightTrigger));
        BinaryPrimitives.WriteUInt32LittleEndian(report[30..], 1); // PhysicalPhysicality
        BinaryPrimitives.WriteUInt32BigEndian(report[34..], 1);    // VirtualPhysicality
        if (!_channel.TrySend(report)) return false;
        if (state != _lastSent) Interlocked.Increment(ref _stateChanges);
        _lastSent = state;
        return true;
    }

    private void WriteHeader(Span<byte> report, ushort type, uint sequence)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(report, type);
        BinaryPrimitives.WriteUInt32LittleEndian(report[2..], sequence);
        BinaryPrimitives.WriteDoubleLittleEndian(report[6..], Stopwatch.GetElapsedTime(_origin).TotalMilliseconds);
    }

    private static void WriteAxis(Span<byte> destination, short axis) =>
        BinaryPrimitives.WriteInt16LittleEndian(destination, axis == short.MinValue ? (short)-32767 : axis);

    public void Neutralize()
    {
        lock (_gate)
            if (!_disposed) SendNeutralIfNeeded();
    }

    private void SendNeutralIfNeeded()
    {
        if (_neutral || !_channel.IsOpen) return;
        if (SendGamepad(default)) _neutral = true;
        else _failed = true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { SendNeutralIfNeeded(); }
        finally { _channel.Dispose(); }
    }
}
