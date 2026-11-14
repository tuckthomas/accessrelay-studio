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
- [ ] Build and install a new beta and immutable plugin version; preserve profiles,
      recordings, optional runtime junctions and existing sequence-editor updates.
- [ ] Push and verify both main branches; report remaining physical acceptance honestly.

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
