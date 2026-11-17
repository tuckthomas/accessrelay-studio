using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using EliteScriptStudio.Core;
using static EliteScriptStudio.RemotePlay.GStreamerNative;

namespace EliteScriptStudio.RemotePlay;

internal sealed record XboxTransportStats(long Timestamp, double? RttMs, double? JitterMs, double? DecodeMs,
    double? DecodedFps, int Width, int Height, double? JitterBufferMs, long? PacketsLost, long? FramesDropped, double? PacketLossPercent, string? RttSource)
{
    internal DirectStreamMetrics Reading => new(Timestamp, RttMs, RttSource, JitterMs, DecodeMs, DecodedFps,
        PacketLossPercent, JitterBufferMs, PacketsLost, FramesDropped, Width, Height);
}

internal sealed class XboxStatsCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<long,long> _decoderInput = [];
    private long _last = Stopwatch.GetTimestamp(), _decoded, _paired;
    private double _decodeMs;
    private int _width, _height;
    private string? _streamId;
    private double? _bufferSeconds, _bufferCount;

    private double? _received, _lost;
    private IPAddress? _probeAddress;
    private Task<double?>? _probe;
    private double? _probeMs;
    private long _probeTime;

    // Keep at most one echo request in flight. Only the numeric, selected LAN host candidate is eligible.
    private double? LanRtt(IPAddress? address)
    {
        if (!Equals(address, _probeAddress))
        {
            _probeMs = null;
            if (_probe is { IsCompleted: false }) return null;
            _probeAddress = address; _probe = null;
        }
        if (address is null) return null;
        if (_probe?.IsCompletedSuccessfully == true)
        {
            _probeMs = _probe.Result; _probeTime = Stopwatch.GetTimestamp(); _probe = null;
        }
        if (_probe is null) _probe = Echo(address);
        return Stopwatch.GetElapsedTime(_probeTime).TotalSeconds <= 3 ? _probeMs : null;
    }

    private static async Task<double?> Echo(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var response = await ping.SendPingAsync(address, 800).ConfigureAwait(false);
            return response.Status == IPStatus.Success ? response.RoundtripTime : null;
        }
        catch (Exception e) when (e is PingException or PlatformNotSupportedException or System.Net.Sockets.SocketException or UnauthorizedAccessException) { return null; }
    }

    internal static IPAddress? LanAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] bytes = ip.GetAddressBytes();
        return IPAddress.IsLoopback(ip) || (bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : (bytes[0] & 0xfe) == 0xfc || ip.IsIPv6LinkLocal) ? ip : null;
    }

    // Public GstBuffer ABI: GstMiniObject, pool pointer, then presentation timestamp.
    [StructLayout(LayoutKind.Sequential)]
    private struct BufferPrefix
    {
        public nuint Type;
        public int ReferenceCount, LockState;
        public uint Flags;
        public nint Copy, Dispose, Free;
        public uint PrivateUInt;
        public nint PrivatePointer, Pool;
        public ulong Pts;
    }
    private static readonly int PtsOffset = checked((int)Marshal.OffsetOf<BufferPrefix>(nameof(BufferPrefix.Pts)));

    internal void Frame(nint buffer, bool decoded)
    {
        long pts = Marshal.ReadInt64(buffer, PtsOffset), now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (decoded) _decoded++;
            if (pts == -1) return;
            if (decoded)
            {
                if (_decoderInput.Remove(pts, out long start))
                { _paired++; _decodeMs += Stopwatch.GetElapsedTime(start, now).TotalMilliseconds; }
            }
            else
            {
                if (_decoderInput.Count >= 128) _decoderInput.Clear();
                _decoderInput[pts] = now;
            }
        }
    }
    internal void Dimensions(int w, int h) { _width = w; _height = h; }

    internal XboxTransportStats Sample(nint reply)
    {
        var reports = new Dictionary<string,nint>(StringComparer.Ordinal);
        if (reply != 0)
            for (uint i = 0; i < gst_structure_n_fields(reply); i++)
            {
                string field = Text(gst_structure_nth_field_name(reply, i));
                var value = gst_structure_get_value(reply, field);
                if (value == 0 || g_type_check_value_holds(value, gst_structure_get_type()) == 0) continue;
                var report = g_value_get_boxed(value);
                if (report != 0) reports[field] = report;
            }
        double? rtt = null, jitter = null, bufferMs = null;
        long? lost = null, dropped = null;
        double? lossPercent = null;
        IPAddress? peer = null;
        foreach (var (key, report) in reports)
        {
            string type = Text(gst_structure_get_name(report));
            if (type == "transport")
            {
                string selected = Text(gst_structure_get_string(report, "selected-candidate-pair-id"));
                if (reports.TryGetValue(selected, out var pair))
                {
                    rtt = Number(pair, "current-round-trip-time") * 1000;
                    string remote = Text(gst_structure_get_string(pair, "remote-candidate-id"));
                    if (reports.TryGetValue(remote, out var candidate) && Text(gst_structure_get_string(candidate, "candidate-type")) == "host")
                        peer = LanAddress(Text(gst_structure_get_string(candidate, "address")));
                }
            }
            if (type != "inbound-rtp" || Text(gst_structure_get_string(report, "kind")) != "video") continue;
            jitter = Number(report, "jitter") * 1000;
            lost = (long?)Number(report, "packets-lost"); dropped = (long?)Number(report, "frames-dropped");
            var seconds = Number(report, "jitter-buffer-delay"); var count = Number(report, "jitter-buffer-emitted-count");
            if (_streamId == key && seconds >= _bufferSeconds && count > _bufferCount)
                bufferMs = 1000 * (seconds - _bufferSeconds) / (count - _bufferCount);
            var received = Number(report, "packets-received");
            if (_streamId == key && received >= _received && lost >= _lost)
            {
                double? total = received - _received + lost - _lost;
                if (total > 0) lossPercent = 100 * (lost - _lost) / total;
            }
            _received = received; _lost = lost;
            _streamId = key; _bufferSeconds = seconds; _bufferCount = count;
        }
        string? rttSource = rtt.HasValue ? "ICE selected pair" : null;
        if (rtt is null && LanRtt(peer) is double echo) { rtt = echo; rttSource = "ICMP to selected LAN peer"; }
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            var result = new XboxTransportStats(now, rtt, jitter, _paired > 0 ? _decodeMs / _paired : null,
                _decoded / Stopwatch.GetElapsedTime(_last, now).TotalSeconds, _width, _height, bufferMs, lost, dropped, lossPercent, rttSource);
            _last = now; _paired = _decoded = 0; _decodeMs = 0;
            return result;
        }
    }

    private static double? Number(nint report, string field)
    {
        if (gst_structure_get_double(report, field, out double value) != 0) return double.IsFinite(value) && value >= 0 ? value : null;
        if (gst_structure_get_int64(report, field, out long integer) != 0) return Math.Max(integer, 0);
        if (gst_structure_get_uint64(report, field, out ulong big) != 0) return big;
        if (gst_structure_get_uint(report, field, out uint number) != 0) return number;
        return gst_structure_get_int(report, field, out int signed) != 0 && signed >= 0 ? signed : null;
    }
}
