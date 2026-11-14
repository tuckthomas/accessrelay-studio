# Live predictor and controller actions (runtime contract 2)

An installed game implementation may create `IVisionLivePredictor` from its
selected profile. The host passes a captured BGRA frame, monotonic receipt QPC,
source generation and a physical-input snapshot. The plugin returns an acquired
region, optional target interval, quality/uncertainty and explicit invalidation.
Game rules and opaque `PluginAssistance` settings remain in the package.
Source-window selection, capture, device lifecycle and output remain in Studio.

This path bypasses ONNX loading, player-region tracking, OCR and GPU preprocessing.
It runs in the existing native capture worker, not the WebView or a Python process.
Preview images carry the matching observation rather than drawing current regions
over an older image. Detached previews reuse the same native session.

`VisionControllerAction` selects a qualified button release or a right-axis stroke.
Qualification never buffers initial passthrough. Only a fresh physical hold opens
an attempt; no prediction creates a physical input. Source/session/shot tokens,
frame age, input freshness, deadlines and conflicting automation are checked by
the existing single controller arbiter.

Timing is `frameReceipt + targetInterval - videoEstimate - inputEstimate + correction`.
The arbiter subtracts local frame age once, at submission. A stroke starts the
curve-inverted anchor fraction of its duration before the chosen anchor. Updates
can replace a pending plan; once movement begins, its absolute clock is fixed.
At start, a valid forecast must be no older than 25 ms (or the stricter configured
frame-age limit). Subsequent output steps retain freshness/lateness guards.
Completion returns neutral and latches the axis until physical neutral. Stop,
disconnect, manual override, stale input/capture or an active competing sequence
cancels the plan; an interrupted ramp returns neutral. The scheduler wakes the
runtime and never writes the controller itself.

An up endpoint is submitted before neutral even when the configured end hold is
zero or a late wake crosses the nominal end. A minimum 2 ms up report separates
those writes; configured longer holds start from the actual up submission.

Native action receipts support plugin calibration. Configuration edits cannot
manufacture receipts; the host supplies the latest native receipt and observation
to `ApplyLiveCalibration`. Failed/disconnected output clears the receipt. A receipt
records local scheduling, not the time Xbox applied the report. Plugin-owned
validation must distinguish user feedback, measured latency and accuracy evidence.

Runtime contracts 1 and 2 remain supported. New packages declare contract 2;
older hosts reject it before loading native code. The React/UI API stays 1, uses
the host singleton and shared components, and contains no output-writing API.

Focused verification: `tools/vision/Verification --meter-scheduler`. Game detection
and calibration checks belong to the separate plugin repository.
