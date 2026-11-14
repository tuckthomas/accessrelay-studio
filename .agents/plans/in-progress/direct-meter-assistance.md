# Direct meter assistance

Requested: independent CPU meter acquisition, calibrated rhythm-stick deadlines,
game policy in the separately shipped NBA plugin, build/install/push both repos.

- [x] Add generic frame-predictor and controller-action contracts to the host SDK.
- [x] Extend the single controller owner with qualified axis holds, anchor-correct
      strokes, cancellation/manual override, freshness and completed-action receipts.
- [x] Implement NBA meter acquisition across the viewport, moving ROI, separation
      of meter progression from the stick marker, causal rolling fits and uncertainty.
- [x] Add plugin-owned calibration trials/results and shared-component controls.
- [x] Verify actual saved gameplay detection, edge positions, ambiguity, variable
      cadence/speed and scheduler cancellation; measure CPU costs without inventing targets.
- [x] Build and install a new beta and immutable plugin version; preserve profiles,
      recordings, optional runtime junctions and existing sequence-editor updates.
- [x] Push and verify both main branches; report remaining physical acceptance honestly.

The optional under-foot indicator is a possible acquisition priority/movement hint,
not a mandatory anchor. Three screen-space displacements include camera movement
and do not by themselves classify catch-and-shoot, pull-up or fadeaway. Direct
meter acquisition must continue to work when that indicator is unavailable.

Timing uses frame-receipt QPC plus predicted target interval minus estimated video
and input delays. A linear stroke starts anchorFraction * duration before its
chosen timing anchor. Holding input qualifies assistance and never buffers initial
passthrough. Calibration trials are explicit; installation starts no automation.

Verification covered 1,117 retained original scene frames across three recorded
shots, at 1280×720 saved resolution from a 2560×1440 capture source. Native CPU
input-gated detector/predictor processing measured 1.62/1.68 ms median and
1.81/1.93 ms p95 on the two clips. These are offline processing costs, not live
capture throughput, applied Xbox timing, native-1440p costs or shot accuracy.
Real-frame augmentations passed at both viewport edges, with a mirrored curve,
and rejected two identical competing meters. Calibration provenance/resolution
and scheduler qualification, anchor, refresh, neutral and cancellation checks pass.

Original physical-input replay produced five accepted-quality forecast frames in
one clip and zero in the other: the player had already manually reversed before
the later, more stable visual readings. A separately labelled continued-hold
simulation produced forecasts after that point; it is not physical acceptance.
No Excellent calibration results were invented. Actual practice calibration,
render/input delay measurement and held-out gameplay accuracy remain unverified.

Delivered Studio 1.9.0-beta.31 and NBA package 1.2.0 (manifest SHA-256
`3af6dcfa616e71a7ba23c4fac312996898e43be9279cd8ee80fcc5a36364cadb`).
Installation is under the project root; the usual Run junction points to it.
All 25 plugin assets and 23 user JSON settings files were checked during install;
recordings were not modified. The installed package loaded against the installed
SDK in a separate verification process without loading ONNX or opening output.
The actual beta.31 window launched with controller, capture and voice off.
Subsequent mouse input prevented UI navigation verification; no live gameplay or
calibration result is claimed. The scheduler also checks a zero-up-hold late wake:
it sends the full-up endpoint before a later neutral report (minimum 2 ms hold).

Installation/replay receipts remain local under
`artifacts/RecordedClipPreparation/direct-meter/`. Marketplace implementation and
versioned assets are on that repository's main at `998a0c9`; host implementation
and generic SDK documentation are on the application main. Existing beta.30
sequence editor changes were retained.

## 2026-11-14: native-resolution fill interruption

Live calibration reported no automatic reversal, unreliable velocity and fill
readings around 49%. No feedback from those failed actions counts as calibration.
A retained real-shot fixture, enlarged from 720p to 1440p and given a two-row
dark interruption, reproduced a 76.4% fill being reported as 47.1%. The detector
scaled cap acquisition but used a fixed fill-component bridge radius.

NBA plugin 1.2.3 scales that radius with capture height. The same interrupted
fixture now reports 76.4%; a larger gap still keeps separate bright regions
disconnected. Geometry score remains visible before velocity becomes reliable.
The changed detector policy invalidates old calibration matches without erasing
their receipts. The application binary stays beta.33; all game code remains in
the separate Marketplace repository.

A device-free predictor/host-arbiter replay of the retained 598-frame shot,
enlarged to 1440p with a counterfactual continued physical hold, produces an up
endpoint and one completed action receipt. This verifies software integration,
not Xbox timing. It neither proves the injected gap caused the user's live
failure nor establishes successful live reversal; that remains to be checked
after Studio loads the updated plugin. No screen control, focus change,
controller input, or user recording was used for this diagnosis.

Plugin 1.2.3 is installed side by side under `InstalledPacks`; Studio must reload
it on its next start. All 25 assets match manifest SHA-256
`4474bd97dc8eb41cb8ff99519ab3edd08858b6fa8530d30a04a33ce0666e4842`.
Its committed implementation/package is Marketplace `3e15105`. A separate
headless load against the installed beta.33 SDK reports assembly 1.2.3.0 without
loading ONNX or activating a controller. The running app was not replaced or
restarted, and its settings and recordings were not edited.

## 2026-11-14: unresolved reversal failure and automatic attempt traces

The user reports the same reversal failure after 1.2.3. The previously quoted
fill reading was one example across different runs, not an established ceiling.
Neither the injected-gap fixture nor replaying manually completed shot images
with a simulated continued hold establishes the cause of the live failure.
Automatic reversal remains unresolved; do not adjust calibration from failed
actions or present those checks as live acceptance.

Studio beta.34 retains eight recent predictor attempts automatically under
`Diagnostics/Vision/attempt-0.json` through `attempt-7.json` in the installed app
folder. Reports include every retained processed frame's physical input and
timestamp, actual loaded predictor assembly/version, signal/forecast, submission
and acceptance flags, source freshness, native arbiter status/cancellation and
requested output state. They also capture current settings and the last native
action receipt. These are local diagnostic reports, not training recordings;
no video or audio is saved. Serialization and file writes run outside capture
and controller threads. No thresholds, release deadlines or output ownership
rules are changed by this instrumentation.

The update must be loaded before these reports can explain a new ordinary
calibration attempt. Studio is left running until the user closes it themselves;
screen control and controller actions remain prohibited.
