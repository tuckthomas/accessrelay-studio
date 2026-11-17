using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteScriptStudio.Core;
using EliteScriptStudio.Output;
using EliteScriptStudio.Vision;
using static EliteScriptStudio.RemotePlay.GStreamerNative;

namespace EliteScriptStudio.RemotePlay;

internal sealed class GStreamerPeer : IDecodedVideoTrack, IXboxInputChannel
{
    private readonly object _gate = new();
    private readonly MessageHandler _messageHandler;
    private nint _pipeline, _peer, _sink, _bus, _control, _input, _message;
    private nuint _handler, _beforeHandler, _afterHandler, _padHandler;
    private nint _videoPad, _audioPad;
    private readonly MessageHandler _padAdded;
    private int _linkFailed;
    private nint _before, _after;
    private readonly XboxStatsCollector _stats = new();
    private readonly MessageHandler _beforeDecode, _afterDecode;
    private int _ack;
    private long _acceptedWrites, _rejectedWrites;
    private readonly double[] _writeMs = new double[256];
    private bool _disposed;
    private Action<DecodedBgraFrame>? _frame;
    private Action? _ended;
    private XboxStreamControllerOutput? _writer;
    private OutputLease? _output;
    private bool _handshakeSent, _authorized;

    // decodebin chooses the installed decoder. These bounded queues keep presentation
    // and CPU analysis independent; local console audio uses the default audio sink.
    private const string Pipeline = "webrtcbin name=peer bundle-policy=max-bundle latency=25 " +
        "rtph264depay name=videoInput ! h264parse ! video/x-h264,alignment=au ! identity name=beforedecode ! decodebin ! identity name=afterdecode ! tee name=video " +
        "video. ! queue leaky=downstream max-size-buffers=1 ! videoconvert ! video/x-raw,format=BGRA ! appsink name=frames max-buffers=1 drop=true sync=false async=false " +
        "video. ! queue leaky=downstream max-size-buffers=2 ! videoconvert ! autovideosink sync=false " +
        "rtpopusdepay name=audioInput ! opusdec ! audioconvert ! audioresample ! autoaudiosink sync=false";

    internal GStreamerPeer(bool present = true)
    {
        Initialize();
        _padAdded = (_, pad, _) =>
        {
            if (gst_pad_get_direction(pad) != 1) return;
            GetPointer(pad, "transceiver", out var transceiver, 0);
            if (transceiver == 0) return;
            try
            {
                Get(transceiver, "kind", out int kind, 0);
                nint sink = kind == 1 ? _audioPad : kind == 2 ? _videoPad : 0;
                if (sink == 0 || gst_pad_link(pad, sink) != 0) Volatile.Write(ref _linkFailed, 1);
            }
            finally { gst_object_unref(transceiver); }
        };
        _beforeDecode = (_, buffer, _) => _stats.Frame(buffer, false);
        _afterDecode = (_, buffer, _) => _stats.Frame(buffer, true);
        _messageHandler = (_, text, _) =>
        {
            try
            {
                string message = Text(text);
                if (message.Length > 16384) return;
                using var json = JsonDocument.Parse(message);
                if (json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "HandshakeAck") Volatile.Write(ref _ack, 1);
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        };
        try
        {
            var pipeline = present ? Pipeline : Pipeline.Replace("autovideosink", "fakesink").Replace("autoaudiosink", "fakesink");
            _pipeline = gst_parse_launch(pipeline, out var error);
            if (error != 0 || _pipeline == 0) throw Error(error, "GStreamer requires WebRTC, NICE, H.264, Opus, playback and app plugins. Install its complete x64 runtime.");
            _peer = gst_bin_get_by_name(_pipeline, "peer"); _sink = gst_bin_get_by_name(_pipeline, "frames"); _bus = gst_element_get_bus(_pipeline);
            _videoPad = SinkPad("videoInput"); _audioPad = SinkPad("audioInput");
            _padHandler = g_signal_connect_data(_peer, "pad-added", _padAdded, 0, 0, 0);
            _before = gst_bin_get_by_name(_pipeline, "beforedecode"); _after = gst_bin_get_by_name(_pipeline, "afterdecode");
            _beforeHandler = g_signal_connect_data(_before, "handoff", _beforeDecode, 0, 0, 0);
            _afterHandler = g_signal_connect_data(_after, "handoff", _afterDecode, 0, 0, 0);
            if (gst_element_set_state(_pipeline, 2) == 0) throw new InvalidOperationException("GStreamer could not initialize its media devices.");
            AddTrack("application/x-rtp,media=audio,encoding-name=OPUS,clock-rate=48000,payload=111");
            AddTrack("application/x-rtp,media=video,encoding-name=H264,clock-rate=90000,payload=96,packetization-mode=(string)1,profile-level-id=(string)42e02a,level-asymmetry-allowed=(string)1");
            _control = AddChannel("control", "controlV1");
            _input = AddChannel("input", "1.0");
            _message = AddChannel("message", "messageV1");
            _handler = g_signal_connect_data(_message, "on-message-string", _messageHandler, 0, 0, 0);
            if (gst_element_set_state(_pipeline, 4) == 0) throw new InvalidOperationException("The WebRTC pipeline could not start.");
        }
        catch { Dispose(); throw; }
    }

    private nint SinkPad(string name)
    {
        var element = gst_bin_get_by_name(_pipeline, name);
        try { return gst_element_get_static_pad(element, "sink"); }
        finally { gst_object_unref(element); }
    }

    private void AddTrack(string format)
    {
        var caps = gst_caps_from_string(format);
        try
        {
            Transceiver(_peer, "add-transceiver", 3, caps, out var transceiver);
            if (transceiver == 0) throw new InvalidOperationException("Could not negotiate an Xbox media track.");
            gst_object_unref(transceiver);
        }
        finally { gst_mini_object_unref(caps); }
    }
    private nint AddChannel(string label, string protocol)
    {
        var options = gst_structure_from_string($"application/data-channel,ordered=(boolean)true,protocol=(string){protocol}", 0);
        try
        {
            Channel(_peer, "create-data-channel", label, options, out var channel);
            return channel != 0 ? channel : throw new InvalidOperationException("Could not open the Xbox " + label + " channel.");
        }
        finally { gst_structure_free(options); }
    }
    internal string CreateOffer(CancellationToken ct) => WithPromise("create-offer", 0, reply =>
    {
        var value = gst_structure_get_value(reply, "offer");
        if (value == 0) throw new InvalidOperationException("The Xbox SDP offer is missing.");
        nint description = g_value_get_boxed(value);
        string sdp = DescriptionText(description);
        WithPromise("set-local-description", description, _ => true, ct);
        return sdp;
    }, ct);

    internal static string DescriptionText(nint description)
    {
        var text = gst_sdp_message_as_text(Marshal.ReadIntPtr(description, IntPtr.Size));
        try { return Text(text); } finally { g_free(text); }
    }

    internal void AcceptAnswer(string answer, CancellationToken ct)
        => SetDescription(_peer, "set-remote-description", 3, answer, ct);

    internal static void SetDescription(nint peer, string signal, int type, string answer, CancellationToken ct)
    {
        if (gst_sdp_message_new_from_text(answer, out var sdp) != 0) throw new InvalidDataException("Xbox returned invalid SDP.");
        var description = gst_webrtc_session_description_new(type, sdp);
        try { WithPromise(peer, signal, description, _ => true, ct); }
        finally { gst_webrtc_session_description_free(description); }
    }

    internal T WithPromise<T>(string signal, nint argument, Func<nint,T> read, CancellationToken ct)
        => WithPromise(_peer, signal, argument, read, ct);

    internal static T WithPromise<T>(nint peer, string signal, nint argument, Func<nint,T> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var promise = gst_promise_new();
        try
        {
            Promise(peer, signal, argument, promise);
            using var registration = deadline.Token.Register(() => gst_promise_interrupt(promise));
            if (gst_promise_wait(promise) != 2) { deadline.Token.ThrowIfCancellationRequested(); throw new InvalidOperationException("WebRTC could not finish " + signal + "."); }
            var reply = gst_promise_get_reply(promise);
            if (reply != 0 && gst_structure_get_value(reply, "error") != 0) throw new InvalidOperationException("WebRTC rejected " + signal + ".");
            return read(reply);
        }
        finally { gst_mini_object_unref(promise); }
    }

    internal Task<XboxIce[]> LocalIceAsync(CancellationToken ct) => LocalIceAsync(_peer, ct);
    internal static async Task<XboxIce[]> LocalIceAsync(nint peer, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 8)
        {
            Get(peer, "ice-gathering-state", out int state, 0);
            if (state == 2) break;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        GetPointer(peer, "local-description", out var desc, 0);
        if (desc == 0) throw new InvalidOperationException("Local ICE description is unavailable.");
        string offer;
        try { offer = DescriptionText(desc); } finally { gst_webrtc_session_description_free(desc); }
        List<XboxIce> candidates = [];
        int index = -1;
        foreach (string section in offer.Split("\nm=", StringSplitOptions.None))
        {
            var lines = section.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries);
            string? mid = lines.FirstOrDefault(l => l.StartsWith("a=mid:", StringComparison.Ordinal))?[6..];
            if (index >= 0) foreach (string line in lines.Where(l => l.StartsWith("a=candidate:", StringComparison.Ordinal)))
                candidates.Add(new(line[2..], index, mid));
            index++;
        }
        if (candidates.Count == 0) throw new InvalidOperationException("WebRTC could not gather any local ICE candidates.");
        return candidates.ToArray();
    }

    internal void AddRemoteIce(IEnumerable<XboxIce> candidates)
    {
        foreach (var candidate in candidates)
        {
            string line = candidate.Candidate.Trim();
            if (line.StartsWith("a=", StringComparison.Ordinal)) line = line[2..];
            if (line.StartsWith("candidate:", StringComparison.Ordinal) && candidate.SdpMLineIndex is >= 0 and < 32)
                Candidate(_peer, "add-ice-candidate", (uint)candidate.SdpMLineIndex, line);
        }
    }

    private static bool Open(nint channel)
    {
        if (channel == 0) return false;
        Get(channel, "ready-state", out int state, 0); return state == 2;
    }

    private static void Send(nint channel, object message)
    {
        if (!Open(channel) || gst_webrtc_data_channel_send_string_full(channel, JsonSerializer.Serialize(message), 0) == 0)
            throw new IOException("The Xbox control channel could not accept a message.");
    }

    internal bool AdvanceHandshake()
    {
        CheckTransport();
        if (!_handshakeSent && Open(_message))
        {
            Send(_message, new { type = "Handshake", version = "messageV1", id = Guid.NewGuid().ToString(), cv = "0" });
            _handshakeSent = true;
        }
        if (!_authorized && Volatile.Read(ref _ack) != 0 && Open(_control) && Open(_input))
        {
            Send(_control, new { message = "authorizationRequest", accessKey = "4BDB3609-C1F1-4195-9B37-FEFF45DA8B8E" });
            SendGamepad(false);
            Config("/streaming/systemUi/configuration", new { version = new[] { 0, 2, 0 }, systemUis = Array.Empty<int>() });
            Config("/streaming/characteristics/orientationchanged", new { orientation = 0 });
            Config("/streaming/characteristics/touchinputenabledchanged", new { touchInputEnabled = false });
            Config("/streaming/characteristics/clientdevicecapabilities", new { });
            Config("/streaming/characteristics/dimensionschanged", new { horizontal = 1920, vertical = 1080, preferredWidth = 1920, preferredHeight = 1080,
                safeAreaLeft = 0, safeAreaTop = 0, safeAreaRight = 1920, safeAreaBottom = 1080, supportsCustomResolution = true });
            _writer = new XboxStreamControllerOutput(this);
            if (!_writer.IsAvailable) throw new IOException("The Xbox input channel rejected setup.");
            Send(_control, new { message = "videoKeyframeRequested", ifrRequested = true });
            _authorized = true;
        }
        return _authorized;
    }
    private void Config(string target, object value) => Send(_message, new { type = "Message", target, id = Guid.NewGuid().ToString(), cv = "", content = JsonSerializer.Serialize(value) });
    private void SendGamepad(bool added) => Send(_control, new { message = "gamepadChanged", gamepadIndex = 0, wasAdded = added });

    internal void CheckTransport()
    {
        if (Volatile.Read(ref _linkFailed) != 0) throw new IOException("Could not route the negotiated Xbox media track.");
        var error = gst_bus_timed_pop_filtered(_bus, 0, 3); // EOS or ERROR
        if (error != 0) { gst_mini_object_unref(error); throw new IOException("Xbox audio/video playback ended or the decoder failed."); }
        Get(_peer, "connection-state", out int state, 0);
        if (state >= 3) throw new IOException("The Xbox WebRTC connection was interrupted.");
    }

    internal IControllerOutput AttachController()
    {
        lock (_gate)
        {
            if (_disposed || !_authorized || _output is not null || _writer?.IsAvailable != true) throw new InvalidOperationException("The Xbox controller channel is unavailable.");
            SendGamepad(true);
            return _output = new OutputLease(this);
        }
    }
    private sealed class OutputLease(GStreamerPeer owner) : IControllerOutput
    {
        public bool UsesVirtualDevice => false;
        public string Status => IsAvailable ? "Direct Xbox controller connected." : "Direct Xbox controller disconnected.";
        public bool IsAvailable { get { lock (owner._gate) return owner._output == this && owner._writer?.IsAvailable == true; } }
        public bool TryWrite(ControllerState state) { lock (owner._gate) return owner._output == this && owner._writer!.TryWrite(state); }
        public void Neutralize() { lock (owner._gate) { if (owner._output == this) owner._writer?.Neutralize(); } }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (owner._output != this) return;
                owner._output = null;
                owner._writer?.Neutralize();
                if (!owner._disposed && Open(owner._control))
                    try { owner.SendGamepad(false); } catch (IOException) { }
            }
        }
    }
    bool IXboxInputChannel.IsOpen => !_disposed && Open(_input);
    bool IXboxInputChannel.TrySend(ReadOnlySpan<byte> report)
    {
        long start = Stopwatch.GetTimestamp();
        bool accepted = false;
        try
        {
            if (_disposed || !Open(_input) || report.IsEmpty) return false;
            GetSize(_input, "buffered-amount", out ulong pending, 0);
            if (pending > 4096) return false;
            var bytes = g_bytes_new(in MemoryMarshal.GetReference(report), (nuint)report.Length);
            try { return accepted = gst_webrtc_data_channel_send_data_full(_input, bytes, 0) != 0; }
            finally { g_bytes_unref(bytes); }
        }
        finally
        {
            lock (_writeMs)
            {
                if (accepted) _writeMs[_acceptedWrites++ % _writeMs.Length] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                else _rejectedWrites++;
            }
        }
    }

    private DirectStreamMetrics? _latest;
    public DirectStreamMetrics? LatestTelemetry => Volatile.Read(ref _latest);
    internal XboxTransportStats ReadStats(CancellationToken ct)
    {
        var stats = WithPromise("get-stats", 0, _stats.Sample, ct);
        long accepted, rejected;
        double[] timings;
        lock (_writeMs)
        {
            accepted = _acceptedWrites; rejected = _rejectedWrites;
            timings = _writeMs[..(int)Math.Min(_writeMs.Length, accepted)];
        }
        if (timings.Length != 0)
        {
            Array.Sort(timings);
            stats = stats with { ControllerWrites = new(accepted, rejected, timings.Length,
                timings[(timings.Length - 1) / 2], timings[(int)Math.Ceiling(timings.Length * .95) - 1]) };
        }
        Volatile.Write(ref _latest, stats.Reading);
        return stats;
    }

    internal void PullFrame()
    {
        var sample = gst_app_sink_try_pull_sample(_sink, 10_000_000);
        if (sample == 0) return;
        try
        {
            var caps = gst_caps_get_structure(gst_sample_get_caps(sample), 0);
            if (gst_structure_get_int(caps, "width", out int w) == 0 || gst_structure_get_int(caps, "height", out int h) == 0 ||
                w is <= 0 or > 3840 || h is <= 0 or > 2160) throw new IOException("The Xbox decoder returned unsupported video dimensions.");
            _stats.Dimensions(w, h);
            var buffer = gst_sample_get_buffer(sample);
            Action<DecodedBgraFrame>? callback;
            lock (_gate) callback = _frame;
            if (callback is null) return;
            long? receipt = _stats.DecoderTimestamp(buffer);
            nint meta = gst_buffer_get_video_meta(buffer), pixels;
            Map map;
            int stride;
            if (meta != 0)
            {
                if (gst_video_meta_map(meta, 0, out map, out pixels, out stride, 1) == 0) return;
            }
            else
            {
                if (gst_buffer_map(buffer, out map, 1) == 0) return;
                pixels = map.Data; stride = checked(w * 4);
            }
            try
            {
                long offset = pixels - map.Data;
                long length = checked((long)map.Size - offset);
                if (offset < 0 || stride < w * 4 || length < (long)(h - 1) * stride + w * 4 || length > int.MaxValue
                    || meta == 0 && length != (long)stride * h) throw new IOException("The decoder returned an unsupported BGRA row layout.");
                callback(new(pixels, (int)length, stride, w, h, receipt));
            }
            finally
            {
                if (meta != 0) gst_video_meta_unmap(meta, 0, ref map);
                else gst_buffer_unmap(buffer, ref map);
            }
        }
        finally { gst_mini_object_unref(sample); }
    }

    public IDisposable Subscribe(Action<DecodedBgraFrame> frame, Action ended)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frame is not null) throw new InvalidOperationException("An Xbox observation is already active.");
            _frame = frame; _ended = ended;
            return new Subscription(this, frame);
        }
    }
    private sealed class Subscription(GStreamerPeer owner, Action<DecodedBgraFrame> frame) : IDisposable
    {
        public void Dispose() { lock (owner._gate) if (owner._frame == frame) { owner._frame = null; owner._ended = null; } }
    }

    // The media/session worker calls this after its last native operation. Controller
    // leases are revoked under the same lock used for synchronous channel writes.
    public void Dispose()
    {
        Action? ended;
        lock (_gate)
        {
            if (_disposed) return;
            _output?.Dispose(); _disposed = true;
            ended = _ended; _frame = null; _ended = null;
        }
        ended?.Invoke();
        if (_pipeline != 0) gst_element_set_state(_pipeline, 1);
        if (_padHandler != 0) g_signal_handler_disconnect(_peer, _padHandler);
        if (_beforeHandler != 0) g_signal_handler_disconnect(_before, _beforeHandler);
        if (_afterHandler != 0) g_signal_handler_disconnect(_after, _afterHandler);
        if (_handler != 0) g_signal_handler_disconnect(_message, _handler);
        foreach (var obj in new[] { _videoPad, _audioPad, _before, _after, _bus, _control, _input, _message, _sink, _peer, _pipeline })
            if (obj != 0) gst_object_unref(obj);
    }
}
