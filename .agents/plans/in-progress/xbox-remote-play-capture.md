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

- [ ] Implement Microsoft sign-in with the system browser and the supported
  Xbox token flow used by the pinned Greenlight authentication dependency.
  Handle cancellation, expiration, refresh, sign-out and account switching.
  Keep credentials and bearer tokens out of React, plugin settings and traces.
  Use per-user protected Windows storage for persisted credentials.
- [ ] Discover the account's home consoles using its xHome token and returned
  regional service address. Show the console name and power state. Handle empty
  results, inaccessible consoles and session conflicts.
- [ ] Port home play/state, SDP, ICE, keepalive and DELETE from the pinned
  clients. Use cancellable requests, bounded deadlines, backoff and one session
  owner. Close a partially created session on cancellation. Exercise canned
  service responses before an authenticated Windows run.
- [ ] Select and pin a maintained native WebRTC engine after a Windows x64
  trial. Verify ordered binary data channels, H.264 negotiation, hardware
  decode, decoded-frame access, audio playback, stats, licensing and packaging.
  Keep the media clock and transport independent of WebView rendering.

## 3. Wire the live session to Studio

- [ ] Implement the player's control, message and input channel negotiation,
  controller registration, metadata, feedback and any required keepalives.
  Route writes from the C# arbiter to the active input channel. Measure handoff
  and bound backpressure. An unavailable channel must invalidate output.
- [ ] Attach native decoded frames to the shared vision worker. Use D3D11
  surfaces when compatible and measure BGRA conversion/readback for CPU
  predictors. Handle device loss, size changes and reconnect generations.
  Read the negotiated dimensions and cadence from the actual track.
- [ ] Add the capture source selector and account/console controls to the
  existing React Live Gameplay page. Preserve window enumeration, restoration,
  standalone capture, existing output choice and detached preview. Allow direct
  observation without enabling controller output. Present connecting, ready,
  disconnected and retry states from the real session.
- [ ] Coordinate changing sources, stopping, loss of focus/device, recording
  finalization and reconnect. Neutralize the old output and invalidate old
  predictions. Require fresh arming after session changes. Prevent duplicate
  physical input from the virtual device or a second controller owner.

## 4. Telemetry and calibration

- [ ] Sample the selected transport's RTT, inbound RTP jitter, jitter-buffer
  delay, decode time per decoded frame, decoded FPS, loss and dropped frames.
  Track report identity, counter resets, missing values and sample age. Keep
  units, local timestamps and metric definitions with the readings.
- [ ] Feed native samples through the existing stream-health and per-attempt
  diagnostics paths. Use overlay OCR for window capture when configured.
  Surface negotiated resolution and measured FPS in Studio.
- [ ] Bind calibration and performance runs to source kind, stream configuration
  and relevant controller route. Preserve previous window evidence. Exercise
  source switching with the same game and profile against the Marketplace plugin.

## 5. Verify and roll out on the existing draft branches

- [ ] Run Studio portable checks, Windows-targeted build, UI checks for changed
  screens and both source lifecycle tests. Cover failed authentication, failed
  negotiation, stale video, congestion, output disconnect, process shutdown
  and reconnect with fixture peers and recorded frames.
- [ ] On Windows, connect to a real console, play audio, preview and record
  frames, verify ordinary and scheduled controller transitions, switch back
  to the same window setup, and check stop/restart and resource cleanup.
- [ ] Compare matched window and direct runs on the same Windows PC, console,
  network, game and negotiated resolution. Record frame-age and send-handoff
  distributions (median/p95), drops, CPU/GPU costs and per-shot outcomes for
  each calibration. Label each timestamp by what Studio can observe.
- [ ] Ship the optional native dependencies and notices, verify installation on
  a clean machine, and document setup and troubleshooting. Keep Studio PR #2
  as the host integration branch. Use Marketplace PR #1 for plugin contract or
  calibration changes when required. Record evidence here before moving this
  checklist to completed.

## Implementation evidence

- [x] The existing window entry point uses the same WGC capture, adapter and
  model setup. The new internal direct entry point takes a decoded track. A
  synthetic track reached preview, exact-frame crops and a finalized diagnostic
  recording. Direct recordings identify the decoder callback clock. The
  window-capture marker used by existing takes is preserved.
- [x] Compared the C# sender with four reference packets generated by the
  original TypeScript writer at the pinned revision. The real StudioRuntime
  poller and scheduler sent full-down, full-up and neutral to a test channel
  from a held P4 paddle while the simulated physical right stick stayed centered. Stop sent neutral and closed the
  channel. Reattaching local USB output restored its input-device reservation.
- [x] Reproduce these checks with
  `dotnet run -c Release --project src/AccessRelayStudio/WindowsVerification -- --direct-stream`.
  The check uses an in-process decoded track and a recording data channel.
  The Studio portable verification suite also passed.
- [ ] Validate both entry points against live Windows inputs. The next session
  milestone is device-code sign-in, selecting the account's default xHome
  region and listing its consoles. Greenlight uses `xal-node` for sign-in.
  The original player's console directory request is `GET /v6/servers/home`.
  [Authentication](https://github.com/unknownskl/greenlight/blob/baa4072cbe1583e76b6271a82e8eda7d46fcf9b9/packages/desktop/main/authentication.ts) · [Console directory](https://github.com/unknownskl/xbox-xcloud-player/blob/a6511a645736f4988f77323284c6f0f36bf93aef/src/apiclient.ts)
