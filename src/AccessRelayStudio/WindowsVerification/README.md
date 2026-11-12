# Windows capture verification

This independent Windows project exercises the real WGC/D3D11 capture path with a temporary synthetic application window. It does not connect to Xbox Remote Play or establish model accuracy or console latency.

```powershell
dotnet run --project ./src/AccessRelayStudio/WindowsVerification/WindowsVerification.csproj -c Release
```

Run it in an interactive Windows desktop session. It verifies nonempty BGRA readback, local QPC timestamps, resize generation changes, minimize/restore eligibility, window closure, row-pitch handling, bounded newest-frame replacement, outstanding lease ownership, and disposal. The test window closes after the checks. Dataset checks use an owned temporary directory to verify cropped pixel data, frame index offsets, shot-profile provenance, incomplete source changes and metadata-finalization failures. They remove their own test data afterwards.

The project references the same compiled engine/runtime libraries as the app and
implements their real input/output contracts with explicitly named test-only fake
devices. It verifies atomic training-entry conflicts with script activation and
macro recording, profile-change guards, physical passthrough, allowed disable/stop
actions and explicit resumption after training. The fake devices never enumerate
controllers or access a virtual-controller driver. No worker sources or duplicate
controller contracts are compiled into the verification executable.

For headless buffer, dataset and simulated-runtime checks, append `-- --no-capture`. To check buffer ownership alone, append `-- --buffer-only`. Neither mode certifies screen capture. Dataset checks require at least 512 MiB free in the Windows temporary directory because they exercise the production free-space guard.

For the focused preview-visibility regression, append `-- --preview-only`. It runs one real capture-only session and checks default-hidden capture, preview enable/disable, pending and consumer-owned buffers, continued training recording and manual passthrough, and stopping. It does not certify inference or console timing. `-- --preview-source` instead opens an animated synthetic window for selecting in Studio's capture picker; it starts no capture or controller and exits when closed or after four minutes.

`-- --preview-diagnostics <absolute-output.json>` runs that focused regression and exports the session's raw capture/completion timestamps and processing durations for investigating host-clock behavior. It preserves the production timestamp and freshness checks.

When workspace disk space is limited, pass `--artifacts-path <absolute C: directory>`
before the `--` separator. The SDK separates engine, runtime and verification output
by project. Avoid a shared BaseIntermediateOutputPath across these project references.
