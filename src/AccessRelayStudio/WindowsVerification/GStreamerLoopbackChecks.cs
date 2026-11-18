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
    internal static void CheckPlayerReconnect()
    {
        // Pump real Windows messages for the D3D renderer. The fixture stays off-screen and silent.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            nint window = Native.CreateWindowEx(0x08000080, "STATIC", "Xbox player test", 0x02CF0000,
                -20000, -20000, 640, 400, 0, 0, Native.GetModuleHandle(null), 0);
            if (window == 0) throw new InvalidOperationException("Could not create the verification window.");
            try
            {
                Native.ShowWindow(window, 4); // SW_SHOWNOACTIVATE
                var run = Task.Run(() => Run(window, attempt == 2 ? 65 : 1));
                while (!run.IsCompleted)
                {
                    while (Native.PeekMessage(out var message, 0, 0, 0, 1))
                    { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
                    Thread.Sleep(2);
                }
                run.GetAwaiter().GetResult();
            }
            finally { Native.DestroyWindow(window); }
        }
        Console.WriteLine("PASS closed the first player and rendered a fresh session to the second Windows player for over a minute.");
    }

    internal static async Task Run(nint window = 0, int renderSeconds = 1, bool forceCpu = false)
    {
        using var client = new GStreamerPeer(window != 0, null, window, forceCpu);
        // A repeated, independently encoded IDR frame keeps the test independent of optional H.264 encoders.
        var bars = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bars-320x180.h264").Replace('\\', '/');
        var server = gst_parse_launch("webrtcbin name=remote bundle-policy=max-bundle " +
            "audiotestsrc is-live=true wave=silence ! opusenc ! rtpopuspay pt=111 ! application/x-rtp,media=audio,encoding-name=OPUS,payload=111 ! remote. " +
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45 + renderSeconds));
            var ct = timeout.Token;
            gst_element_set_state(server, 4);
            string offer = client.CreateOffer(ct);
            if (!offer.Contains("a=rtpmap:111 OPUS/48000/2\r\n", StringComparison.OrdinalIgnoreCase))
                throw new Exception("The WebRTC offer must advertise the RFC 7587 Opus channel count.");
            GStreamerPeer.SetDescription(remote, "set-remote-description", 1, offer, ct);
            string answer = GStreamerPeer.WithPromise(remote, "create-answer", 0, reply =>
                GStreamerPeer.DescriptionText(g_value_get_boxed(gst_structure_get_value(reply, "answer"))), ct);
            GStreamerPeer.SetDescription(remote, "set-local-description", 3, answer, ct);
            client.AcceptAnswer(answer, ct);
            foreach (var candidate in await client.LocalIceAsync(ct))
                Candidate(remote, "add-ice-candidate", (uint)candidate.SdpMLineIndex, candidate.Candidate);
            var remoteIce = await GStreamerPeer.LocalIceAsync(remote, ct);
            var xboxIce = remoteIce.Select(c => c with { Candidate = "a=" + c.Candidate })
                .Append(remoteIce[0] with { Candidate = "a=end-of-candidates" });
            client.AddRemoteIce(XboxIceCandidates.ForPeer(xboxIce, []));
            while (!client.AdvanceHandshake()) await Task.Delay(10, ct);
            XboxTransportStats playback;
            do
            {
                await Task.Delay(100, ct);
                playback = client.ReadStats(ct);
                client.CheckTransport();
            } while (playback.Width == 0 || playback.DecodedAudioBuffers is not > 0 || window != 0 && playback.RenderedFrames is not > 0);
            if (playback.Width != 320 || playback.Height != 180 ||
                playback.VideoDecoder?.StartsWith("d3d12h264dec", StringComparison.Ordinal) == true && playback.VideoMemory != "D3D12")
                throw new Exception("Playback-only video lost its dimensions or Direct3D surface: " + playback);
            using var capture = new DecodedStreamCapture(client);
            do { ct.ThrowIfCancellationRequested(); client.PullFrame(); } while (capture.Status.Width == 0);
            if (!capture.TryAcquireLatest(out var first) || first is null) throw new Exception("Decoded H.264 frame missing.");
            using (first)
            {
                if (first.Width != 320 || first.Height != 180 || !first.Pixels.Span.Slice(0, 320*4).ContainsAnyExcept((byte)0))
                    throw new Exception("Decoded H.264 geometry or pixels are invalid.");
            }
            var captureStats = client.ReadStats(ct);
            if (captureStats.ColorConversion is null || (forceCpu && captureStats.ColorConversion != "CPU"))
                throw new Exception("Live capture did not respect the color conversion setting.");
            Console.WriteLine($"Live Xbox observation used {captureStats.ColorConversion} with {captureStats.VideoMemory} decoder memory.");
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < renderSeconds) { ct.ThrowIfCancellationRequested(); client.CheckTransport(); client.PullFrame(); }
            var telemetry = client.ReadStats(ct);
            if (window != 0 && !(telemetry.RenderedFrames > playback.RenderedFrames && telemetry.RenderDroppedFrames.HasValue))
                throw new Exception("The Windows player stopped presenting frames while the decoder was running.");
            if (telemetry.DecodedFps is not > 0 || telemetry.DecodeMs is null || telemetry.JitterMs is null || telemetry.PacketLossPercent is null || telemetry.DecodedAudioBuffers is not > 0 || string.IsNullOrEmpty(telemetry.VideoDecoder))
                throw new Exception("Missing measured FPS, decoder timing, video RTP jitter, packet counters or decoded Opus audio: " + telemetry);
            capture.Dispose();
            using (var resumed = new DecodedStreamCapture(client))
            {
                do { ct.ThrowIfCancellationRequested(); client.PullFrame(); } while (resumed.Status.Width == 0);
                if (resumed.Status.Width != 320) throw new Exception("Restarting observation lost the running Xbox picture.");
            }
            using var device = new FakeControllerInput();
            using var runtime = new StudioRuntime(device, StudioProfile.CreateDefault() with { ScriptsEnabled = false }, new(0, 0));
            runtime.Attach(client.AttachController());
            while (runtime.Latest is not { Input.IsConnected: true, OutputActive: true } || received.IsEmpty) await Task.Delay(5, ct);
            runtime.ConfigureVision(VisionSettings.Default with { MaximumFrameAgeMs = 1000, MaximumLatenessMs = 100 },
                new() { Kind = VisionActionKind.RightStickStroke, TriggerPaddle = PaddleButtons.LargeRight, HoldQualificationMs = 70, StrokeDurationMs = 56, AnchorFraction = .85 });
            runtime.BeginVisionSession();
            if (!runtime.SetVisionArmed(true)) throw new Exception("Could not arm simulated paddle output: " + runtime.VisionStatus.Message);
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
            runtime.StopOutput(stopScripts: true);
            if (client.HasControllerOutput) throw new Exception("Stopped controller still marked as routed to Xbox.");
            runtime.Attach(client.AttachController());
            if (!client.HasControllerOutput || runtime.VisionStatus.Armed) throw new Exception("Reattaching manual controller output changed the assistance state.");
            Console.WriteLine($"PASS native WebRTC peers exchanged ICE, DTLS, Xbox channels, H.264 {first.Width}x{first.Height}, decoded Opus and P4 down/up/neutral. Stats: {afterShot}");
        }
        finally
        {
            gst_element_set_state(server, 1); gst_object_unref(remote); gst_object_unref(server);
            GC.KeepAlive(onChannel); GC.KeepAlive(ack); GC.KeepAlive(input);
        }
    }
}
