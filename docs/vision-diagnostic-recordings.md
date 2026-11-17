# Gameplay diagnostic recordings

## Auto-green reliability branch (runtime contract 4)

Shot receipts now distinguish `completed` from `cancelled`. `lastAction` retains the
last completed action for compatibility; `lastAttempt` is authoritative for feedback.
Receipts include successful local output submission timestamps, anchor lateness,
feedback eligibility, applied timing/tempo corrections, and stream-health evidence.
These timestamps measure Studio's device handoff, not receipt by the console.
A forecast must leave time for the full calibrated stroke. Its start is never moved
to now while retaining an already missed anchor.

A pre-stroke paddle cancellation keeps down held until physical release or the
configured maximum-shot watchdog. Stop, disarm, disconnect and conflicting automation
still neutralize output. Mid-stroke cancellation never reverses the stick back down.
Cancelled attempts remain visible in diagnostics and post-release OCR, but cannot
calibrate or tune timing. Older completed actions cannot supply their feedback.

Xbox statistics monitoring uses the selected window's existing capture, a configurable
normalized overlay region, and Windows' built-in OCR on a background task. It requests
at most one crop every 750 ms and requires all four labelled readings: ping/latency,
jitter, decode time and FPS. Packet loss is optional. Incomplete, conflicting or stale
readings are unavailable, never zero. No additional hardware or game OCR weights are
required; Windows must have an OCR language installed. Enable the Xbox overlay and
adjust **Advanced settings → Statistics region** to include its labels and values.
Monitoring can be disabled explicitly for sources without an overlay.

Per-frame diagnostics and each attempt retain the latest assessment. Readings older
than three seconds pause automatic learning. Jitter plus deviations of displayed
latency/decode time from a rolling median add forecast uncertainty. Reported loss,
excessive decode delay, frame-rate drops or variation beyond the configured timing
budget mark the stream unstable. Already anchored completion can finish; instability
cannot initiate a new stroke. Sampling cannot detect every short spike between reads.
Displayed ping/latency is never subtracted from the release deadline or assumed to be
controller-to-screen delay. The existing calibrated compensation is retained.

Automatic learning needs locally validated completion, consistent OCR over two distinct
frames, fresh stable stream evidence, and two reliable shots in the same profile,
five-foot distance band and coverage class. Unreadable context pauses learning.
Each run retains its first qualifying context; other contexts are diagnostic only,
so alternating distances/coverage cannot adjust the shared correction. Use separate
saved profiles for those contexts. A tempo adjustment disarms assistance and requires
fresh practice evidence for the changed effective stroke duration.

Further investigation identified an internal SDK statistics callback and historical
native-client ETW providers. See [native telemetry findings and discovery probe](xbox-native-stream-telemetry.md).
These routes take priority for investigation before promoting OCR to the permanent
source. The probe does not yet supply measurements to the timing loop.

Microsoft documents `XGameStreamingGetStreamAddedLatency` for the streaming game's
own connected client IDs. It does not document a supported external API for Studio
to query another process's Xbox PC Remote Play overlay. See the
[API reference](https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/system/xgamestreaming/functions/xgamestreaminggetstreamaddedlatency).
This branch therefore reads the displayed measurements instead of depending on private
Xbox app endpoints. The parser and health window accept source-labelled readings so
a documented provider can be added later.


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
