# Xbox Remote Play as a Studio capture source

Studio will offer two choices in Live Gameplay. Window capture observes a selected
application through Windows Graphics Capture. Direct Xbox Remote Play connects
Studio to the user's own console, publishes decoded frames to the existing vision
session, and can send the composed controller state over that same connection.
Existing profiles open with window capture. The Marketplace continues to own
game-specific analysis and calibration policy.

## 1. Establish the capture and input boundaries

- [x] Trace the current paths. DesktopVisionService starts VisionSession with a
  window handle. The observation worker creates RemotePlayCapture and consumes
  a latest-frame lease. StudioRuntime already sends both normal controller
  samples and scheduled transitions through IControllerOutput.
- [x] Pin the original player as the protocol reference and Greenlight as the
  reference for sign-in, region selection and home-session lifecycle. Record
  source revisions and licenses alongside adapted code. Review individual
  XStreaming changes only for identified interoperability issues. [Player](https://github.com/unknownskl/xbox-xcloud-player/tree/a6511a645736f4988f77323284c6f0f36bf93aef) · [Greenlight](https://github.com/unknownskl/greenlight/tree/baa4072cbe1583e76b6271a82e8eda7d46fcf9b9)
- [x] Give the existing observation worker a source-independent frame interface.
  Route window startup and decoded video through the same worker. Preserve capture-only use, preview, recording, CPU predictors and
  WGC's D3D11/CUDA adapter selection.
- [x] Implement bounded decoded BGRA frame ingestion. Test padded rows, held
  leases, superseded frames, format changes, stale callbacks and shutdown.
  Timestamp receipt on the local monotonic clock and preserve clock provenance.
- [x] Adapt the player's single-gamepad reports to C#. Verify the handshake and
  report bytes against upstream vectors. Exercise the same output interface used
  by physical sampling and scheduled stick transitions, including failed sends
  and neutralization. The direct sender must have no JavaScript polling timer.

## 2. Sign in and connect to a home console

- [x] Port the device-code, refresh, Xbox Live, XSTS and xHome exchanges from the Greenlight authentication dependency. Launch the Microsoft verification page in the system browser. Keep the refresh credential in Windows Credential Locker and the access tokens in memory. Implement cancellation, expiry, sign-out and account switching.
- [x] Discover consoles in the account's default home region. Follow continuation tokens and show names, power states, empty results and conflicts.
- [x] Implement home play/state, transfer authorization, SDP and ICE exchange, keepalive and DELETE. Retry transient discovery/state reads, bound request and negotiation times, and close a session allocated after Stop. Verify failed provisioning, refresh revocation, rejected sign-in pages, session conflicts and cleanup with an HTTP fixture.
- [x] Select GStreamer WebRTC and pin the optional Windows installer to 1.26.10 by SHA-256. Exercise native ICE/DTLS, ordered data channels, H.264 reception and Opus decoding with two independent peers. The Windows job starts on a fresh hosted Windows 2025 runner.
- [ ] Connect to an actual Xbox using a Microsoft account on the same LAN. Record the chosen decoder on the user's GPU and confirm audible playback on their Windows endpoint.

## 3. Wire the live session to Studio

- [x] Negotiate control, message and input channels. Implement the Xbox handshake, configuration, controller registration, initial metadata and keyframe request. Send composed C# controller reports directly. Reject writes when the channel is unavailable or over its backlog limit and measure local send duration.
- [x] Attach real decoded frames to the shared vision worker. Keep native playback independent of WebView rendering. Handle padded BGRA rows, bounded buffers, format changes, decoder timestamps, stale subscriptions and shutdown. Supply preview, crops and diagnostic recordings through the existing entry points.
- [x] Expose the capture source and account/console controls on Live Gameplay, the game add-on and Model Training. Support observation with controller output off. Show connect, sign-in, cancel, retry and disconnect states. Update the workspace controller action for the direct route.
- [x] Disarm on source changes, detach the previous output, neutralize on shutdown and await session release on application exit. The input channel has one output owner. Track loss stops the observation and recording. The previous window can be selected again.
- [ ] Add a compatible D3D11 texture path from the decoder to GPU predictors and measure against BGRA readback on a Windows GPU. The current native engine selects an installed decoder and records its name. CPU analysis receives mapped BGRA.
- [ ] Verify Xbox controller rumble and any console-requested per-frame presentation feedback in a physical session. Initial metadata and controller reports are implemented. Upstream video feedback also uses the server RTP timestamp and local presentation timestamps.

## 4. Telemetry and calibration

- [x] Read selected-pair RTT where available, video RTP jitter, per-frame decoder time, decoded FPS, dimensions, and packet counters. Compute packet loss and jitter-buffer delay from deltas for the same RTP stream. Retain reported dropped-frame counts. Keep missing fields nullable.
- [x] If the selected pair omits RTT, attempt a bounded ICMP echo to its selected private host candidate. Distinguish LAN ping and ICE RTT in the UI and diagnostics. Windows loopback produced LAN ping. An unanswered echo leaves RTT blank.
- [x] Send native measurements into stream-health and per-attempt evidence. Preserve the window overlay OCR reader. Use observed jitter, decode variation, FPS and loss when RTT is missing. Pause adaptation on missing required measurements, stale samples or unstable streaming.
- [x] Keep the source and controller route in calibration identity and capture dimensions in the evidence. The paired plugin test switches an accepted shot profile between window and direct sources and checks the original window evidence on return.
- [x] Record local input handoff counts, rejections and median/p95 over the latest 256 accepted writes. Record the negotiated track size, chosen decoder and local frame clock. The native channel acknowledges local acceptance.

## 5. Verify and roll out on the existing draft branches

- [x] Run portable engine, desktop UI and Windows runtime checks. Exercise late allocation, output rejection, disconnection, cancellation, stale decoded sources, exact-frame crops and finalized recordings.
- [x] Exercise a paddle-only shot in the native peer test. Hold P4 with physical right X/Y at zero. Observe full-down before forecasting and full-up then neutral while P4 stays held. Verify the endpoints on the receiving peer and the shot receipt in Studio.
- [x] Publish the optional media installer and notices with Studio. Test the installer and dependency discovery on a clean Windows runner. Produce a Windows desktop artifact on the Studio PR and an importable, validated add-on on the Marketplace PR. [Windows setup and troubleshooting](../../../docs/xbox-remote-play.md)
- [ ] On a real console, verify P4 while the native player has focus, stop/restart, reconnect, hardware decoding, audible playback and an ordinary recording. Switch back to the Xbox app window, resume its calibration and check the separate shot logs.
- [ ] Compare matched window/direct runs on the same PC, console, network, resolution and shot profile. Collect median/p95 frame age and local handoff time, drops, CPU/GPU load and shot outcomes for each calibration. Hardware measurements are required for a latency or shot-accuracy comparison.

## Reproduce the automated checks

- [x] Run `dotnet run -c Release --project src/AccessRelayStudio/WindowsVerification -- --direct-stream` for the player packet vectors, captured pixels, vision worker, padded rows, held P4, rejected writes and service fixtures.
- [x] Install the optional runtime and run `dotnet run -c Release --project src/AccessRelayStudio/WindowsVerification -- --xbox-webrtc-loopback`. One peer sends color bars and Opus audio, the other decodes them. The return channel carries the shot generated by the Studio runtime. The test uses headless playback sinks.
- [x] Both existing draft PRs carry the implementation. The Marketplace contract workflow loads a previously released add-on and the new package against the paired Studio branch. [Studio PR](https://github.com/tuckthomas/accessrelay-studio/pull/2) · [Marketplace PR](https://github.com/tuckthomas/accessrelay-marketplace/pull/1)
