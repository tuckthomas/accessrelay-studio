using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteScriptStudio;
using EliteScriptStudio.Core;
using EliteScriptStudio.Input;
using EliteScriptStudio.RemotePlay;
using EliteScriptStudio.Vision;
using static EliteScriptStudio.RemotePlay.GStreamerNative;

internal static class GStreamerLoopbackChecks
{
    // Two independent ICE/DTLS/SRTP peers. The sender generates H.264 video and Opus audio.
    internal static async Task Run()
    {
        using var client = new GStreamerPeer(present: false);
        // A repeated, independently encoded IDR frame keeps the test independent of optional H.264 encoders.
        var bars = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bars-320x180.h264").Replace('\\', '/');
        var server = gst_parse_launch("webrtcbin name=remote bundle-policy=max-bundle " +
            "audiotestsrc is-live=true ! opusenc ! rtpopuspay pt=111 ! application/x-rtp,media=audio,encoding-name=OPUS,payload=111 ! remote. " +
            $"multifilesrc location=\"{bars}\" start-index=0 stop-index=0 loop=true do-timestamp=true ! identity sleep-time=33333 ! video/x-h264,stream-format=byte-stream,alignment=au,framerate=30/1 ! h264parse ! rtph264pay config-interval=-1 pt=96 ! application/x-rtp,media=video,encoding-name=H264,payload=96 ! remote.", out var error);
        if (server == 0 || error != 0) throw Error(error, "Install the full GStreamer fixture encoder and audio plugins.");
        var remote = gst_bin_get_by_name(server, "remote");
        ConcurrentQueue<short> received = new();
        MessageHandler ack = (channel, payload, _) =>
        {
            if (Text(payload).Contains("\"Handshake\"", StringComparison.Ordinal))
                gst_webrtc_data_channel_send_string_full(channel, """{"type":"HandshakeAck"}""", 0);
        };
        MessageHandler input = (_, payload, _) =>
        {
            var bytes = g_bytes_get_data(payload, out nuint length);
            if (length == 38) received.Enqueue(Marshal.ReadInt16(bytes, 24));
        };
        MessageHandler onChannel = (_, channel, _) =>
        {
            GetPointer(channel, "label", out var label, 0);
            string name = Text(label); g_free(label);
            if (name == "message") g_signal_connect_data(channel, "on-message-string", ack, 0, 0, 0);
            if (name == "input") g_signal_connect_data(channel, "on-message-data", input, 0, 0, 0);
        };
        g_signal_connect_data(remote, "on-data-channel", onChannel, 0, 0, 0);
        try
        {
            // A clean Windows installation scans all codec DLLs on first use, before negotiation.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ct = timeout.Token;
            gst_element_set_state(server, 4);
            string offer = client.CreateOffer(ct);
            GStreamerPeer.SetDescription(remote, "set-remote-description", 1, offer, ct);
            string answer = GStreamerPeer.WithPromise(remote, "create-answer", 0, reply =>
                GStreamerPeer.DescriptionText(g_value_get_boxed(gst_structure_get_value(reply, "answer"))), ct);
            GStreamerPeer.SetDescription(remote, "set-local-description", 3, answer, ct);
            client.AcceptAnswer(answer, ct);
            foreach (var candidate in await client.LocalIceAsync(ct))
                Candidate(remote, "add-ice-candidate", (uint)candidate.SdpMLineIndex, candidate.Candidate);
            client.AddRemoteIce(await GStreamerPeer.LocalIceAsync(remote, ct));
            while (!client.AdvanceHandshake()) await Task.Delay(10, ct);
            using var capture = new DecodedStreamCapture(client);
            do { ct.ThrowIfCancellationRequested(); client.PullFrame(); } while (capture.Status.Width == 0);
            if (!capture.TryAcquireLatest(out var first) || first is null) throw new Exception("Decoded H.264 frame missing.");
            using (first)
            {
                if (first.Width != 320 || first.Height != 180 || !first.Pixels.Span.Slice(0, 320*4).ContainsAnyExcept((byte)0))
                    throw new Exception("Decoded H.264 geometry or pixels are invalid.");
            }
            _ = client.ReadStats(ct);
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < 1) { ct.ThrowIfCancellationRequested(); client.PullFrame(); }
            var telemetry = client.ReadStats(ct);
            if (telemetry.DecodedFps is not > 0 || telemetry.DecodeMs is null || telemetry.JitterMs is null || telemetry.PacketLossPercent is null || telemetry.DecodedAudioBuffers is not > 0 || string.IsNullOrEmpty(telemetry.VideoDecoder))
                throw new Exception("Missing measured FPS, decoder timing, video RTP jitter, packet counters or decoded Opus audio: " + telemetry);
            using var device = new FakeControllerInput();
            using var runtime = new StudioRuntime(device, StudioProfile.CreateDefault() with { ScriptsEnabled = false }, new(0, 0));
            runtime.Attach(client.AttachController());
            while (runtime.Latest?.Input.IsConnected != true) await Task.Delay(5, ct);
            runtime.ConfigureVision(VisionSettings.Default with { MaximumFrameAgeMs = 1000, MaximumLatenessMs = 100 },
                new() { Kind = VisionActionKind.RightStickStroke, TriggerPaddle = PaddleButtons.LargeRight, HoldQualificationMs = 70, StrokeDurationMs = 56, AnchorFraction = .85 });
            runtime.BeginVisionSession();
            if (!runtime.SetVisionArmed(true)) throw new Exception("Could not arm paddle output.");
            device.SetState(default, PaddleButtons.LargeRight);
            while (runtime.VisionStatus.ShotToken == 0 || !received.Contains((short)-32767)) await Task.Delay(5, ct);
            if (runtime.Latest?.Input is not { State.RightX: 0, State.RightY: 0, HeldPaddles: PaddleButtons.LargeRight })
                throw new Exception("The downstroke did not originate from P4 alone.");
            var shot = runtime.VisionStatus;
            long now = Stopwatch.GetTimestamp();
            if (!runtime.SubmitVisionPrediction(new(shot.SessionGeneration, shot.ShotToken, 1, now, now, 100, 1, 0, true)))
                throw new Exception("Paddle forecast rejected.");
            while (runtime.LastVisionAction is null || !received.Contains(short.MaxValue) || received.LastOrDefault() != 0) await Task.Delay(5, ct);
            short[] reports = received.ToArray();
            int down = Array.IndexOf(reports, (short)-32767), up = Array.IndexOf(reports, short.MaxValue);
            if (down < 0 || up <= down || Array.IndexOf(reports, (short)0, up) <= up
                || runtime.Latest?.Input is not { State.RightX: 0, State.RightY: 0, HeldPaddles: PaddleButtons.LargeRight }) throw new Exception("Peer missed the P4 down/up/neutral sequence.");
            var afterShot = client.ReadStats(ct);
            if (afterShot.ControllerWrites is not { Accepted: > 3, Rejected: 0, Samples: > 3 })
                throw new Exception("Native controller handoff counters missed the shot.");
            Console.WriteLine($"PASS native WebRTC peers exchanged ICE, DTLS, Xbox channels, H.264 {first.Width}x{first.Height}, decoded Opus and P4 down/up/neutral. Stats: {afterShot}");
        }
        finally
        {
            gst_element_set_state(server, 1); gst_object_unref(remote); gst_object_unref(server);
            GC.KeepAlive(onChannel); GC.KeepAlive(ack); GC.KeepAlive(input);
        }
    }
}
