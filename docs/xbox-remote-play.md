# Direct Xbox Remote Play

In **Live Gameplay → Capture source**, select an open window by title or choose **Direct Xbox Remote Play** to connect to a home console. Selecting a browser window captures its active tab.

## Connect on Windows

1. Enable remote features on the Xbox and use its Microsoft account. For the first run, put the Windows PC and Xbox on the same LAN and end any existing Remote Play session on the console.
2. Get the **AccessRelayStudio-win-x64** artifact from the Studio PR’s Windows build and extract the complete folder. For the updated 2K27 add-on, extract **nba2k27-package** from the paired Marketplace PR. In Studio choose **Controller → Plugins → Import package** and open its `pack.json`.
3. For direct playback, run the included `RemotePlay/Install-XboxRemotePlay.ps1` from an administrator PowerShell. It installs the complete official GStreamer 1.26.10 MSVC x64 runtime after checking the publisher’s SHA-256 digest. The same script is in `scripts/` in a source checkout. Restart Studio after installation.
4. Open **Live Gameplay → Capture source → Direct Xbox Remote Play**. Choose **Sign in to Xbox** and enter the displayed code on the Microsoft page opened by Studio. Choose the home console and **Connect Xbox**. Studio opens its **Xbox Remote Play** window and plays audio on the default Windows device. **Show Xbox player** brings the window back while connected. Closing the player ends the session. Choose **Connect Xbox** to reopen it.
5. Choose **Start capture** for preview, recording or live analysis. Select the physical controller in Studio and choose **Connect controller** in Live Gameplay to send its inputs to the console. Reconnecting to the same console restores previously enabled controller output. To use the 2K27 meter, enable the shot meter in the game, then open **Add-Ons → NBA 2K27 → Shot meter** in Studio. Select **P4 · Large right paddle**, confirm the PC Profile Numpad mapping in Xbox Accessories and run practice calibration for this source. Keep the physical right stick centered and hold P4 through the stroke. Studio sends full-down, the timed transition to full-up and neutral. Release P4 between attempts.
6. Stop capture and finish any recording before changing source. **Disconnect Xbox** ends the native session. **Sign out** also removes the account’s refresh credential from Windows Credential Locker. To resume window capture, select the window title under **Capture source**.

## Interpret the numbers

Direct playback requests 1920 × 1080. Studio displays the actual decoded dimensions and updates statistics about once per second. The shot diagnostics record the decoder’s factory name. On compatible Windows GPUs, video stays in Direct3D memory for display. Starting an observation opens a separate branch. With the D3D12 H.264 decoder, that branch downloads the decoded frame and converts it to full-resolution BGRA on the CPU for vision, preview, and recording. Windows audio uses the default output device with low-latency WASAPI settings. The internal preview has its own refresh cadence.

| Measurement | Source |
| --- | --- |
| ICE RTT / LAN ping | The selected WebRTC pair’s RTT where available. Otherwise Studio can time an ICMP echo to the selected private host candidate. The UI labels a successful echo **LAN ping**. A missing or blocked response produces a dash. |
| Jitter | Inbound video RTP jitter in milliseconds. |
| Decode | Mean time from a complete H.264 access unit entering the decoder to decoded output, paired by presentation timestamp. |
| Direct frame age at decision | Time on the PC from decoder output through readback, conversion, frame delivery and vision processing. Studio already subtracts this age when it schedules a release. The console's render and encode times are outside this measurement. |
| FPS | Count of decoded frames divided by local elapsed time. |
| Packet loss | Change in lost packets divided by the sum of received and lost packet changes for the same video stream. The first interval has no baseline. |
| Controller writes | Local channel acceptance and rejection counts, plus median and p95 handoff time over the latest 256 accepted writes. These describe the C# call into the transport. |

Per-shot diagnostics store the native sample, its local clock timestamp and its RTT source. Jitter, decoder variation, FPS and packet loss can pause learning even when RTT is unavailable. A missing decode/jitter/FPS sample pauses learning until recent evidence is available. The window source can read the Xbox app’s visible stats overlay with Windows OCR.

The `Diagnostics/Vision/attempt-*.json` files in the app folder carry the shot evidence. The add-on also displays the location of its separate `ShotFeedbackHistory.jsonl`. When comparing the two capture sources, use the same console, PC, controller, shot, resolution and network, calibrate each source and keep separate sets of logs. Compare frame age, handoff timing, drops and shot outcomes over repeated trials.

## Troubleshooting

- **No consoles listed.** Confirm the console has remote features enabled for the signed-in account, then choose **Refresh consoles**.
- **Another session is active.** End the existing session in the Xbox app, then reconnect from Studio.
- **Media runtime unavailable.** Studio recognizes the complete MSVC x64 runtime in `RemotePlay/GStreamer` beside the app, or in the standard Windows installation. A custom installation can supply its absolute root through `GSTREAMER_1_0_ROOT_MSVC_X86_64`.
- **A connection fails before the picture appears.** The latest attempt is recorded in `Diagnostics/XboxRemotePlay.log` beside Studio. It shows which audio, video and data tracks the Xbox accepted, how many network candidates Studio received, and whether ICE, DTLS and the Xbox channel handshake completed. It also records the first decoded picture and audio.
- **A connection drops during play.** Studio disarms assistance and ends the old observation. Reconnect, start capture and arm again.
- **Testing scope.** The automated player test exchanges audio, video and controller reports with a second native peer. Hardware decoder selection, audible playback, actual console sign-in and relative shot performance require a physical Windows/console run. CPU BGRA is the direct vision input in this build.

[Studio checklist](../.agents/plans/in-progress/xbox-remote-play-capture.md) · [Studio PR](https://github.com/tuckthomas/accessrelay-studio/pull/2) · [Marketplace PR](https://github.com/tuckthomas/accessrelay-marketplace/pull/1)
