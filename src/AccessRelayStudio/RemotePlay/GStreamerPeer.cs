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
    private readonly ElementHandler _elementAdded;
    private nuint _elementHandler;
    private string? _decoderName;
    private nint _before, _after, _decodedPad, _observationValve, _decodedAudio;
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
    private readonly Action<string>? _trace;
    private readonly BusSyncHandler? _windowHandler;
    private nint _renderer;
    private int _lastPeerState = -1;

    // decodebin chooses the installed decoder. These bounded queues keep presentation
    // and CPU analysis independent; local console audio uses the default audio sink.
    private static string PlaybackPipeline(bool present)
    {
        string download = "";
        if (OperatingSystem.IsWindows())
        {
            foreach (string name in new[] { "d3d12download", "d3d11download" })
            {
                nint factory = gst_element_factory_find(name);
                if (factory == 0) continue;
                gst_object_unref(factory);
                download = name + " ! ";
                break;
            }
        }
        string screen = present ? "autovideosink" : "fakesink", audio = present ? "autoaudiosink" : "fakesink";
        return "webrtcbin name=peer bundle-policy=max-bundle latency=25 " +
            "rtph264depay name=videoInput ! h264parse ! video/x-h264,alignment=au ! identity name=beforedecode ! decodebin ! identity name=afterdecode ! tee name=video " +
            "video. ! queue leaky=downstream max-size-buffers=1 ! valve name=observation drop=true drop-mode=forward-sticky-events ! " +
            download + "videoconvert ! video/x-raw,format=BGRA ! appsink name=frames max-buffers=1 drop=true sync=false async=false " +
            $"video. ! queue leaky=downstream max-size-buffers=1 ! videoconvert ! {screen} sync=false " +
            $"rtpopusdepay name=audioInput ! opusdec ! identity name=decodedAudio signal-handoffs=false ! audioconvert ! audioresample ! {audio} name=speakers sync=false";
    }

    internal GStreamerPeer(bool present = true, Action<string>? trace = null) : this(present, trace, 0) { }

    internal GStreamerPeer(bool present, Action<string>? trace, nint playbackWindow)
    {
        _trace = trace;
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
        _elementAdded = (_, _, element, _) =>
        {
            var factory = gst_element_get_factory(element);
            if (factory == 0) return;
            string kind = Text(gst_element_factory_get_metadata(factory, "klass"));
            bool decoder = kind.Contains("Decoder/Video", StringComparison.Ordinal) || kind.Contains("Decoder/Image", StringComparison.Ordinal);
            if (!decoder && !kind.Contains("Sink/Audio", StringComparison.Ordinal)) return;
            var name = gst_object_get_name(factory);
            try
            {
                string plugin = Text(name);
                if (decoder) Volatile.Write(ref _decoderName, plugin);
                else if (plugin is "wasapi2sink" or "wasapisink")
                {
                    gst_util_set_object_arg(element, "low-latency", "true");
                    gst_util_set_object_arg(element, "buffer-time", "20000");
                    gst_util_set_object_arg(element, "latency-time", "5000");
                }
            }
            finally { g_free(name); }
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
            _pipeline = gst_parse_launch(PlaybackPipeline(present), out var error);
            if (error != 0 || _pipeline == 0) throw Error(error, "GStreamer requires WebRTC, NICE, H.264, Opus, playback and app plugins. Install its complete x64 runtime.");
            _elementHandler = ConnectElement(_pipeline, "deep-element-added", _elementAdded, 0, 0, 0);
            _peer = gst_bin_get_by_name(_pipeline, "peer"); _sink = gst_bin_get_by_name(_pipeline, "frames"); _bus = gst_element_get_bus(_pipeline);
            if (playbackWindow != 0)
            {
                _windowHandler = (_, message, _) =>
                {
                    if (gst_is_video_overlay_prepare_window_handle_message(message) == 0) return 1; // GST_BUS_PASS
                    nint renderer = MessageSource(message);
                    gst_video_overlay_set_window_handle(renderer, (nuint)playbackWindow);
                    if (Interlocked.CompareExchange(ref _renderer, gst_object_ref(renderer), 0) != 0) gst_object_unref(renderer);
                    gst_mini_object_unref(message);
                    return 0; // GST_BUS_DROP
                };
                gst_bus_set_sync_handler(_bus, _windowHandler, 0, 0);
            }
            _videoPad = SinkPad("videoInput"); _audioPad = SinkPad("audioInput");
            _padHandler = g_signal_connect_data(_peer, "pad-added", _padAdded, 0, 0, 0);
            _before = gst_bin_get_by_name(_pipeline, "beforedecode"); _after = gst_bin_get_by_name(_pipeline, "afterdecode");
            _decodedPad = gst_element_get_static_pad(_after, "src"); _observationValve = gst_bin_get_by_name(_pipeline, "observation");
            _decodedAudio = gst_bin_get_by_name(_pipeline, "decodedAudio");
            _beforeHandler = g_signal_connect_data(_before, "handoff", _beforeDecode, 0, 0, 0);
            _afterHandler = g_signal_connect_data(_after, "handoff", _afterDecode, 0, 0, 0);
            if (gst_element_set_state(_pipeline, 2) == 0) throw new InvalidOperationException("GStreamer could not initialize its media devices.");
            // WebRTC Opus uses a two-channel RTP mapping even when the source plays mono (RFC 7587).
            AddTrack("application/x-rtp,media=audio,encoding-name=OPUS,clock-rate=48000,encoding-params=(string)2,payload=111");
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
            if ((line.Length == 0 || line.StartsWith("candidate:", StringComparison.Ordinal)) && candidate.SdpMLineIndex is >= 0 and < 32)
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
        if (!CheckTransport()) return false;
        if (!_handshakeSent && Open(_message))
        {
            Send(_message, new { type = "Handshake", version = "messageV1", id = Guid.NewGuid().ToString(), cv = "0" });
            _handshakeSent = true;
            _trace?.Invoke("Xbox message handshake sent.");
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

    internal bool CheckTransport()
    {
        if (Volatile.Read(ref _linkFailed) != 0) throw new IOException("Could not route the negotiated Xbox media track.");
        var error = gst_bus_timed_pop_filtered(_bus, 0, 3); // EOS or ERROR
        if (error != 0)
        {
            string reason = "Xbox audio/video playback ended.";
            try
            {
                var details = gst_message_get_structure(error);
                var value = details == 0 ? 0 : gst_structure_get_value(details, "gerror");
                var nativeError = value == 0 ? 0 : g_value_get_boxed(value);
                if (nativeError != 0)
                {
                    // GError has two 32-bit fields followed by the message pointer. Only fixed categories reach the log.
                    var message = Text(Marshal.ReadIntPtr(nativeError, 8));
                    reason = message.Contains("window", StringComparison.OrdinalIgnoreCase) && message.Contains("clos", StringComparison.OrdinalIgnoreCase)
                        ? "The Xbox playback window closed." : $"Xbox media playback failed ({Text(g_quark_to_string((uint)Marshal.ReadInt32(nativeError)))} {Marshal.ReadInt32(nativeError, 4)}).";
                    nint source = MessageSource(error);
                    nint factory = source != 0 && g_type_check_instance_is_a(source, gst_element_get_type()) != 0 ? gst_element_get_factory(source) : 0;
                    if (factory != 0)
                    {
                        nint name = gst_object_get_name(factory);
                        try { _trace?.Invoke($"Media error from {Text(name)}."); }
                        finally { g_free(name); }
                    }
                }
            }
            finally { gst_mini_object_unref(error); }
            _trace?.Invoke(reason);
            throw new IOException(reason);
        }
        Get(_peer, "connection-state", out int state, 0);
        if (state != _lastPeerState)
        {
            _lastPeerState = state;
            _trace?.Invoke(TransportDescription());
        }
        // While opening a session, ICE can still find a working pair after a provisional disconnect.
        // Once input is authorized, end a lost transport promptly so Studio disarms and releases it.
        if (state == 3 && !_authorized) return false;
        if (state >= 3) throw new IOException($"Xbox {(_authorized ? "Remote Play stopped" : "could not start Remote Play")}. {TransportDescription()}");
        return true;
    }

    internal string TransportDescription()
    {
        Get(_peer, "connection-state", out int peer, 0);
        Get(_peer, "ice-connection-state", out int ice, 0);
        GetPointer(_peer, "sctp-transport", out var sctp, 0);
        int dtls = -1;
        if (sctp != 0)
        {
            try
            {
                GetPointer(sctp, "transport", out var transport, 0);
                if (transport != 0)
                    try { Get(transport, "state", out dtls, 0); } finally { gst_object_unref(transport); }
            }
            finally { gst_object_unref(sctp); }
        }
        string peerState = peer switch { 0 => "new", 1 => "connecting", 2 => "connected", 3 => "disconnected", 4 => "failed", 5 => "closed", _ => "unknown" };
        string iceState = ice switch { 0 => "new", 1 => "checking", 2 => "connected", 3 => "completed", 4 => "failed", 5 => "disconnected", 6 => "closed", _ => "unknown" };
        string dtlsState = dtls switch { 0 => "new", 1 => "closed", 2 => "failed", 3 => "connecting", 4 => "connected", _ => "unavailable" };
        return $"WebRTC {peerState}, ICE {iceState}, DTLS {dtlsState}.";
    }

    internal IControllerOutput AttachController()
    {
        lock (_gate)
        {
            if (_disposed || !_authorized || _output is not null || _writer?.PrepareAttachment() != true) throw new InvalidOperationException("The Xbox controller channel is unavailable.");
            SendGamepad(true);
            return _output = new OutputLease(this);
        }
    }
    internal bool HasControllerOutput { get { lock (_gate) return _output is not null && _writer?.IsAvailable == true; } }
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
        string? videoMemory = null;
        var caps = gst_pad_get_current_caps(_decodedPad);
        if (caps != 0)
        {
            try
            {
                var format = gst_caps_get_structure(caps, 0);
                if (gst_structure_get_int(format, "width", out int width) != 0 && gst_structure_get_int(format, "height", out int height) != 0)
                    _stats.Dimensions(width, height);
                var features = gst_caps_get_features(caps, 0);
                videoMemory = gst_caps_features_contains(features, "memory:D3D12Memory") != 0 ? "D3D12" :
                    gst_caps_features_contains(features, "memory:D3D11Memory") != 0 ? "D3D11" :
                    gst_caps_features_contains(features, "memory:SystemMemory") != 0 ? "system" : "other";
            }
            finally { gst_mini_object_unref(caps); }
        }
        var stats = WithPromise("get-stats", 0, _stats.Sample, ct) with { VideoDecoder = Volatile.Read(ref _decoderName), VideoMemory = videoMemory,
            ControllerStateChanges = _writer?.StateChanges ?? 0 };
        nint renderer = Volatile.Read(ref _renderer);
        if (renderer != 0)
        {
            GetPointer(renderer, "stats", out var presentation, 0);
            if (presentation != 0)
            {
                try
                {
                    if (gst_structure_get_uint64(presentation, "rendered", out var rendered) != 0 &&
                        gst_structure_get_uint64(presentation, "dropped", out var dropped) != 0)
                        stats = stats with { RenderedFrames = rendered, RenderDroppedFrames = dropped };
                }
                finally { gst_structure_free(presentation); }
            }
        }
        GetPointer(_decodedAudio, "stats", out nint audio, 0);
        if (audio != 0)
        {
            try { if (gst_structure_get_uint64(audio, "num-buffers", out ulong buffers) != 0) stats = stats with { DecodedAudioBuffers = buffers }; }
            finally { gst_structure_free(audio); }
        }
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
            gst_util_set_object_arg(_observationValve, "drop", "false");
            return new Subscription(this, frame);
        }
    }
    private sealed class Subscription(GStreamerPeer owner, Action<DecodedBgraFrame> frame) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) if (owner._frame == frame)
            {
                gst_util_set_object_arg(owner._observationValve, "drop", "true");
                owner._frame = null; owner._ended = null;
            }
        }
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
        if (_windowHandler is not null && _bus != 0) gst_bus_set_sync_handler(_bus, null, 0, 0);
        if (_elementHandler != 0) g_signal_handler_disconnect(_pipeline, _elementHandler);
        if (_padHandler != 0) g_signal_handler_disconnect(_peer, _padHandler);
        if (_beforeHandler != 0) g_signal_handler_disconnect(_before, _beforeHandler);
        if (_afterHandler != 0) g_signal_handler_disconnect(_after, _afterHandler);
        if (_handler != 0) g_signal_handler_disconnect(_message, _handler);
        foreach (var obj in new[] { _renderer, _videoPad, _audioPad, _before, _after, _decodedPad, _observationValve, _decodedAudio, _bus, _control, _input, _message, _sink, _peer, _pipeline })
            if (obj != 0) gst_object_unref(obj);
    }
}
