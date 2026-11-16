# Gameplay diagnostic recordings

Studio beta.37 / NBA plugin 1.2.6 repair the failures reproduced from the retained
November 14 recordings: green feedback-box false acquisition, cap-fragment scale
collapse, and a fill scan whose measurable maximum did not match its timing target.
Stroke start uses the configured maximum frame age rather than a hidden 25 ms
limit. Negative tracking observations still cancel pending or active strokes.
The captured full-hold diagnostic now produces three completed action receipts
in native device-free replay. Skipped images remain skipped; footage is not
interpolated to manufacture cadence or acceptance. This establishes the replay
behavior, not console delivery time or in-game timing accuracy.

With Studio beta.36 and NBA plugin 1.2.5, the recorded input context includes the
physical hold-transition timestamp and the anchor of an actually started stroke.
The **Action phase** distinguishes a live forecast, an attempt cancellation and
completion of an already anchored stroke. A completion observation has no new
target estimate and cannot start or move a deadline. Missing/ambiguous tracking
before the anchor, and position/scale/progression failures at any phase, are
reported as cancellations rather than refreshed saved forecasts. Input state,
poll time and action context are read together under the runtime lock.

Hold/continuity verification uses simulated devices and scripted detector
observations, plus replay of retained footage. It does not establish live shot
accuracy. Run the focused checks with:

```powershell
dotnet run --project src/AccessRelayStudio/WindowsVerification/WindowsVerification.csproj -c Release -- --signal-context
dotnet run --project ../AccessRelayMarketplace-Source/plugins/nba2k27/tests/Core/PluginVerification.csproj -c Release -- --meter-continuity
```

Studio beta.35 and NBA plugin 1.2.4 add **Record diagnostics** beside the gameplay
preview. Start the preview, record diagnostics, then start a practice trial and
take a shot. A failed detection needs no completed action receipt. Stop recording
after the attempt, or let its 15-second limit finish it. Assistance keeps its
existing state; recording never arms or disarms it. Training clip recording and
diagnostic recording cannot run together.

**Open diagnostic folder** opens the saved evidence under
`Diagnostics/VisionRecordings/diagnostic-*` in the application installation.
Open `review.html`, then select that recording's folder. The local viewer shows
the fill graph, exact captured image and meter outline, measured velocity,
uncertainty, forecast and output decisions, physical/requested right-stick axes,
and processing/recording times. No server, Python or internet connection is needed.

`diagnostic.json` binds each image to its capture sequence, generation, local
capture/compositor timestamps, dimensions/stride, signal observation, physical
input context, requested output, controller arbiter state, last action receipt,
prediction submission/acceptance and capture-pipeline counters. Its context records
the selected profile/source, effective settings, app version and predictor version.
Images are native BGRA8 with no resize or lossy encoding. Each `.bgra.gz` file
decompresses to exactly `stride * height` bytes. The viewer converts BGRA to RGBA
for display and overlays the recorded meter region.

The diagnostic writer copies images into a three-image queue and compresses them
off the capture thread. If storage falls behind, the frame's detection/input record
is retained with `imageStatus=writerQueueFull`. `copyMs`, `queueMs`, `writeMs`, saved
and skipped counts expose recording overhead. Other image states distinguish write
failure and the size limit. Capture-pipeline replacements are separate from
diagnostic image skips. Metadata and stop status are finalized on manual stop,
timeout, preview stop, capture failure and normal application shutdown.

Limits are 15 seconds, 4,096 observations and 1 GiB compressed images plus at most
one final image. The initial manifest is explicitly incomplete; the final report
records finalization, errors and the stop reason. Disk failures are surfaced in the
page. A process crash or power failure can leave the initial incomplete manifest.
No recording is started on page load, and older recordings are not overwritten.

These are observations, not training labels or gameplay acceptance. Controller
readings are sampled alongside processed video frames, not every output report.
Local receipt timestamps do not measure Xbox rendering or applied input time.
Recording itself adds work, so compare cadence with unrecorded operation before
attributing a stall to Remote Play or recommending capture hardware.

Verification:

```powershell
dotnet run --project src/AccessRelayStudio/WindowsVerification/WindowsVerification.csproj -c Release -- --diagnostic-recording artifacts/DiagnosticRecordingEvidence
node tools/vision/verify-diagnostic-review.cjs
```
