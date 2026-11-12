# Xbox Remote Play vision: implementation plan

- Date: 2026-11-11
- Status: experimental software implemented; gameplay data, trained weights and Xbox release validation pending
- Repository: `tuckthomas/accessrelay-studio`
- Feature branch: `codex/remote-play-vision`

## 1. Objective and decisions

Add an optional vision-assisted release feature to AccessRelay Studio. Xbox Remote Play supplies console gameplay to a browser or supported Windows client. AccessRelay captures that window, analyzes the in-game player's shooting animation on the RTX 2070, predicts a release deadline, and modifies its existing virtual-controller output. Recognition must work with the shot meter disabled and without requiring a special player icon.

The production implementation stays in C#/.NET and uses ONNX Runtime with an explicit CPU or CUDA execution-provider selection; CUDA is the default. The Windows GPU runtime package supplies both providers. A Python production worker is not required. Python may be used offline for dataset preparation, model training, export, and reference evaluation. TensorRT remains a later execution-provider option if the actual model benefits from it and a compatible deployment is validated.

CPU work already includes crop/normalization, temporal bookkeeping, controller arbitration and scheduling. Compare explicitly selected CPU/CUDA inference using total frame-to-decision p95/p99 latency, copies, resource contention and deadline misses. Small models can favor CPU execution when GPU dispatch/copy costs outweigh computation savings; no backend winner is assumed for an untested task model. CUDA selection disables CPU fallback and fails visibly when its graph/dependencies cannot execute. Arming evidence must name the selected actual provider; changing providers invalidates the prior hardware association. Window capture/composition and Remote Play video decoding may still use graphics hardware even if neural inference runs on the CPU.

The next tracking revision should evaluate physical-input-gated candidate acquisition and bounded single-player tracking, with optional gamertag verification when readable. Continuous tracking of every player and mandatory OCR are not prerequisites. Physical input supplies intent and an attempt boundary, but cannot identify which visible body is ours: the displayed stream can predate the input reaching the Xbox. Acquire identity from ordinary gameplay evidence, associate it with a detected body, and maintain a stabilized moving player region through camera motion. Camera-specific tag offsets and box proportions can be acquisition priors, refined by body detection/scale and confidence checks; text recognition need not run on every frame. Define crop dimensions relative to source resolution and detected player scale, including at 2560×1440, rather than fixing a 160-pixel source box. Tag text may retain a fixed screen-space font size, so its height alone is not a proven player-depth estimate. Preserve hands/ball context, smooth crop motion, and invalidate deadlines on identity loss or reassignment. This is a proposed replacement for manual fixed-region selection; the current runtime still uses a fixed region. That revision needs full-scene recording or sufficient scene context, per-frame body/identity observations and transforms, track confidence, a versioned crop policy, and validation across the supported 2K, 2K Low and 2K Legacy views. The camera field added now records the selected view; it does not itself implement automatic player tracking.

The application remains WinUI 3 on Windows x64. Reuse its physical-input handling, profiles, controller state, virtual Xbox output, and existing signed USB/IP transport. The controller helper remains a separate native process; choosing in-process C# vision does not remove that existing process boundary.

This document records the implementation and the remaining acceptance gates. No validated gameplay animation model, trained-model RTX 2070 performance measurement, Remote Play compatibility result for our virtual device, or release-accuracy result currently exists for this feature. The untrained CUDA diagnostic establishes runtime compatibility only. See [implementation verification](remote-play-vision-validation.md) for the completed checks and their limits.

### Experimental branch implementation

The branch now contains actual window capture, bounded leased frame buffers, selectable CPU/CUDA ONNX integration, temporal preprocessing/tracking, the output arbiter and deadline scheduler, diagnostics, calibration helpers, settings persistence, and a Remote Play Vision page. Capture-only observation works without a model. Untrained or unvalidated models cannot arm automatic release.

The NBA 2K27 plugin supports multiple saved player/shot profiles with create, edit, revert, duplicate, select and delete operations. Each stores player height, three-point rating, custom base, upper release 1, upper release 2, meter on/off, meter-specific release type, one of four shot-speed levels, release blending, and camera view (2K, 2K Low or 2K Legacy). Release 2's percentage is the complement of release 1 so they always total 100%. Meter release type is not applicable when the meter is off. Gameplay fields feed a configuration fingerprint; changing them invalidates the prior model/calibration association. Older saved profiles missing camera view default to 2K. The fingerprint payload version advances so existing model/calibration bindings require review. Training metadata, model camera configuration, settings import and calibration review also check the camera selection.

Opt-in ten-second training takes store cropped 256x256 BGRA frames, actual PC capture timestamps and the full selected shot profile. Bounded buffering and file size limits protect the controller path. Human-reviewed shot labels are required; capture does not infer an optimal release label. Offline training/export/evaluation tools are separate from the C# production application.

Windows capture and dataset lifecycle tests, portable controller/profile tests, model/preprocessing tests, and a strict CUDA diagnostic on the RTX 2070 are part of verification. Synthetic windows, random diagnostic weights and synthetic training samples establish software integration only. A gameplay dataset, trained task model, held-out accuracy, calibration and real Xbox Remote Play controller/release validation remain outstanding until actual gameplay is captured and reviewed.

## 2. Scope

### Required behavior

- Select a Remote Play window, display a modest preview, and configure the gameplay crop.
- Track the intended in-game player using ordinary gameplay imagery and temporal context. Ambiguous identity must disable assistance for that attempt.
- Recognize early animation phases and estimate time until the configured release target.
- Preserve normal physical control while allowing one explicitly selected shoot control to be released automatically.
- Keep all capture, inference, profiles, and calibration local to the PC.
- Measure local stage durations, deadline performance, uncertainty, and failures.
- Include a calibration mode, observation-only mode, explicit arm/disarm controls, and Stop all integration.
- Preserve existing macro, Lua, recording, voice, and profile behavior when vision is disabled.

### Deferred or excluded from the first version

- Capture-card forwarding through the router, VirtualHere, and network USB configuration. The selected video source is Remote Play over the existing 2.5 Gb/s LAN/MoCA path.
- Pi 4 or Coral deployment, router inference, and production Python services.
- Automatic Xbox login, credential storage, replacement of Microsoft's Remote Play client, or a new console streaming protocol implementation.
- Universal support for every jump shot, camera, court, game update, or animation family at launch.
- General webcam gesture control. The input imagery is the character in gameplay, not a person in front of a webcam.
- Automatic execution of untrusted model plugins or custom model code.
- Mandatory CPU-core affinity, automatic highest thread priority, a C++/Rust rewrite of the application, or a hard real-time guarantee.

## 3. Repository baseline and existing integration points

The initial published baseline inspected for this plan was commit `069d41abf379dd0a027b9302da4a00438f0cad33`; the first plan commit was `171d60d`. The feature baseline must also include the existing application changes: Lua scripting, NBA 2K tuning, recorder editing, background input, controller model/viewer updates, voice updates, and build guards. Publish those changes and this updated plan to `main` before advancing the feature branch. These existing application changes are prerequisites/context, not an implementation of the proposed vision feature.

Paths below are relative to the repository root. Names in the proposed-files section describe new components, not existing APIs.

| Existing component | Integration responsibility |
| --- | --- |
| `src/AccessRelayStudio/StudioRuntime.cs` | Owns controller sampling and currently writes virtual output. Its worker targets a 4 ms interval. Integrate output arbitration here without running capture or inference under `_sync`. |
| `src/AccessRelayStudio/Core/ControllerState.cs` | Carries the complete button, trigger, and stick state. Preserve channels the feature does not own. |
| `src/AccessRelayStudio/Core/MacroEngine.cs` | Produces macro/script state. Establish explicit ownership conflicts with vision rather than allowing competing writers. |
| `src/AccessRelayStudio/Input/EliteInputMonitor.cs` | Supplies the selected physical controller and excludes our virtual output from feedback. Maintain reconnect and selection-version handling. |
| `src/AccessRelayStudio/Output/VirtualOutput.cs` | Opens the existing virtual Xbox controller through `IControllerOutput`. Reuse it. |
| `src/AccessRelayStudio/Output/UsbipXboxOutput.cs` | Currently queues up to 32 reports and sends them to the native helper. Audit queue delay, report ordering, release durability, and failure handling. |
| `src/AccessRelayStudio/Audio/VirtualMicHost/` | Contains the native controller helper alongside microphone-related infrastructure. Preserve its ownership and packaging contracts. |
| `src/AccessRelayStudio/Core/StudioProfile.cs` and `ProfileStore.cs` | Coordinate profile compatibility. Keep vision configuration versioned and do not invalidate older profiles. |
| `src/AccessRelayStudio/Audio/DialogueSeparatorModel.cs` | Existing CPU ONNX integration is an example of dependency use, not a ready-made real-time vision engine. Preserve audio behavior when changing native runtime packaging. |
| `src/AccessRelayStudio/Verification/` | Extend deterministic verification for timing math, state ownership, cancellation, and profile validation. |
| `Core/LuaControllerScript.cs`, `Core/Nba2K27Tuning.cs` | Coordinate existing Lua/tuning behavior with vision ownership. Do not make deadline scheduling depend on Lua coroutine timing. |

The existing `RecentHandoffMs` metric measures local enqueue-to-helper-stdin-flush duration. It is not USB report consumption, browser acceptance, network transit, or Xbox input acknowledgement. The plan must retain that distinction in code, logs, and UI.

## 4. End-to-end architecture

```text
Xbox gameplay
  -> Remote Play encode / network / decode
  -> browser or supported Windows client window
  -> Windows Graphics Capture
  -> newest-frame buffer
  -> crop / resize / normalize
  -> ONNX Runtime inference using the explicitly selected CPU or CUDA provider
  -> player tracking + temporal animation state
  -> predicted release deadline + uncertainty
  -> controller output arbiter and deadline scheduler
  -> existing virtual Xbox controller helper
  -> Remote Play input transport
  -> Xbox

Physical controller -> existing input/macro processing -> output arbiter
UI / preview / logs <- bounded snapshots outside the critical path
```

The physical controller's outgoing shoot state must pass through AccessRelay. A second virtual pad's release cannot be assumed to cancel a button independently held on a controller connected directly to the console. Validate Remote Play recognition, player assignment, background physical input, and duplicate-input behavior before developing automatic release.

Keep a single authoritative output arbiter. Capture and inference publish observations and deadlines; they never call `TryWrite` independently. If a separate scheduler wakes between controller samples, it signals the arbiter to serialize a state update. It must not race a second controller-output writer.

## 5. Capture and frame freshness

Use Windows Graphics Capture with a D3D11-backed frame pool. Start with a user-selected window. Capture permissions, window closure, resizing, display changes, HDR/color handling, device loss, and graphics-adapter selection must be handled explicitly.

- Keep a bounded set of reusable frame resources. Maintain one pending newest observation, with any in-flight frame held separately until processing completes.
- Replacing a pending frame releases its resources safely. Never overwrite a surface that inference is still reading.
- Never build an unbounded FIFO of video frames. Skip obsolete frames when inference falls behind and count the skips separately from stream losses.
- Track frame sequence and monotonic capture timestamp. Detect frozen/repeated gameplay imagery separately from fresh window paints; overlays can update while the game image remains frozen.
- Crop to the configured gameplay area before expensive processing. Player tracking may reduce the inference region further, provided reacquisition remains possible.
- Preview has a separate update limit. Rendering, annotations, image encoding, clip recording, and disk writes cannot block capture or release scheduling.
- Pause on a minimized, unavailable, unsupported, or frozen source as established by actual capture behavior. Never treat an old frame as current merely because the window still exists.

Capture timestamps provide the PC capture timeline. They do not identify the original Xbox render time. Timestamp every local stage, but label upstream video age as an estimate unless an independently validated source-time measurement becomes available.

## 6. GPU memory and ONNX execution

Use a long-lived `InferenceSession` and reusable `OrtValue` inputs and outputs. Record the selected actual provider. For CUDA, select a compatible ONNX Runtime GPU, CUDA, cuDNN, driver, and model combination for the RTX 2070; record that matrix rather than assuming runtime packages can simply be exchanged without consequences. CPU mode is explicit, not a fallback from failed CUDA initialization, and requires its own matching live validation evidence.

1. Verify the chosen provider actually executes the intended model nodes. CUDA mode rejects CPU fallback rather than silently changing the selected backend.
2. Allocate reusable input/output resources for the supported fixed input size and known output shapes. Handle dynamic outputs with a documented bounded strategy.
3. Keep the distinction between pinned managed RAM and GPU VRAM explicit. Pinning host arrays does not place them on the GPU.
4. Implement and measure the D3D11 capture-to-CUDA transfer path. Cross-API resource sharing requires validated synchronization; do not describe it as zero-copy until demonstrated. A measured, bounded copy path is an acceptable initial implementation.
5. Use GPU preprocessing where practical. If CPU preprocessing is used first, reuse buffers and account for every upload, download, and color conversion.
6. Bind reusable device memory correctly, including updates for each new input. Binding a host buffer once must not accidentally leave inference reading an old device-side copy.
7. Warm the session and resources before enabling assistance. Startup compilation, algorithm selection, allocation, and optional graph capture stay outside active shot handling.
8. Consider CUDA Graphs only if the selected graph, shapes, memory addresses, provider assignment, and threading satisfy the documented constraints.
9. Bound resource use when live voice inference, browser hardware decoding, and preview rendering run concurrently. Handle GPU reset, allocation failure, and provider failure by disabling vision with a clear reason.

Do not introduce `DisposableNamedOnnxValue` as the new memory-binding design; it is a legacy API. Do not claim that CUDA automatically keeps all data in VRAM, that DirectML has a fixed millisecond penalty, or that CUDA and TensorRT execute an identical optimized graph. TensorRT evaluation must use the same model, precision/accuracy criteria, input data, and full-pipeline measurements.

Changing ONNX packaging must preserve existing dialogue separation. Resolve native library version/load conflicts explicitly; avoid loading incompatible ONNX native builds into one process. Record native dependencies, licenses, model assets, and installation size. A self-contained .NET application still has GPU/runtime/model dependencies.

## 7. Animation model and training work

A reliable no-meter model is a required deliverable. A generic detector, pose-estimation demo, product marketing page, or fixed shot timer is not sufficient evidence.

### Dataset and labels

- Collect consented local gameplay clips with the meter off and no mandatory player icon. Keep raw clips and model weights out of Git.
- Start with a declared game version, camera configuration, and a small set of supported shot families. Record the selected animation and timing settings with each session.
- Label player identity, shot start, early animation phases, target release timing, interruptions/cancels, and visibility/confidence.
- Distinguish visible ball departure from the optimal input-release target. The latter needs controlled release experiments and game feedback; pose labels alone do not establish it.
- Include negative examples: dribbling, passing, pump fakes, other players shooting, camera transitions, occlusion, compression artifacts, menus, and stream stalls.
- Split training/validation/test data by complete session or clip. Keep related shots out of multiple splits and retain a final held-out set untouched by tuning.

### Runtime model contract

The model and temporal tracker should supply: tracked player identity/confidence, shot-family estimate, shot phase, time-to-target estimate, and prediction uncertainty. A compact temporal model or tracked-feature sequence is a candidate; select the architecture after accuracy and performance evaluation. Single-frame generic pose output is not assumed to solve release prediction.

Use physical shoot input as causal context and as permission to consider assistance. Provide a player/region selection and identity-acquisition step that uses ordinary gameplay imagery, then track and reacquire that player. Seeing another player shoot is not sufficient identity evidence. Track the animation across recent timestamped frames without waiting for a large batch of future frames. Declare the temporal history and warm-up behavior so its latency is visible.

A later optimization may keep lightweight identity tracking/history active and run expensive animation inference only within a bounded physical-input-triggered attempt. Input alone cannot establish player identity, possession or that a shot occurred: the same control can have defensive or off-ball actions, and the displayed stream represents delayed gameplay. Local button-down is not the Xbox's input-application timestamp; the first visible frame after it may show an earlier shooter or pre-gather state. Preserve a short timestamped history, keep the model warm, and allow bounded acquisition while waiting for matching visual evidence. If identity, timing or confidence remains insufficient by the decision deadline, skip assistance. Fresh acquisition avoids depending on a long-lived track but can still choose the wrong player; it does not eliminate identification errors. Keep visual identity/animation validation, cancellation and uncertainty gates. Do not require an unverified possession ring or custom player icon. Compare this optimization with continuous observation using the same model and workload, including acquisition delay, missed attempts and CPU/GPU duty cycle. Do not assume a universal 500 ms attempt, 95% idle time or sub-millisecond tracking cost. The current observation loop continues inference according to its temporal sampling policy; input gating is not implemented by this plan text.

Export a validated ONNX model with preprocessing, input/output schema, precision, model version/hash, supported environments, and license. Compare exported predictions with the training/reference implementation before hardware trials. Import models through a bounded, versioned manifest and report unsupported schemas/operators without enabling output.

## 8. Timing model and calibration

Use a common monotonic PC clock for capture, observations, prediction, controller samples, scheduler wake-ups, and local helper handoff. Convert timestamp units once and test the conversions.

At the time a prediction finishes, define:

- `H`: predicted duration from the game state represented by the observed frame to the intended target.
- `V`: estimated Xbox-render-to-PC-capture age for that frame, including stream encoding, transit, buffering, decoding, and capture sampling.
- `P`: measured elapsed time from that capture timestamp to the current prediction time.
- `U`: estimated time from sending the local release through the output path until it affects the Xbox.
- `C`: calibrated signed profile adjustment. Positive values delay sending; negative values advance it.

```text
waitUntilSend = H - V - P - U + C
sendDeadline = predictionCompletedAt + waitUntilSend
```

`H`, `V`, and `U` are not exact observations. Store their provenance and uncertainty. Initial calibration may estimate the combined `V + U` correction; do not present separate exact one-way measurements when only a combined offset is supported. A ping time is not the full video/input delay.

Calibration mode should record controlled attempts, physical input edges, predictions, local report timing, and reviewed game outcomes. Derive a robust offset and spread for each supported animation/profile. Keep automatic adjustment bounded and reviewable. Do not train on the application's own unverified predictions or infer a precise signed millisecond error from coarse early/late game feedback.

Track variation, not only the mean. If the prediction interval is too wide, the frame is stale, or the deadline is already beyond the configured lateness tolerance, skip assistance for that attempt. Never send an obsolete scheduled release into a later shot.

A 60 FPS stream has about 16.67 ms between source frames. Faster inference does not recover missing observations or remove stream buffering. Stable delay can be compensated by prediction; unseen animation changes during a freeze cannot be reconstructed reliably.

## 9. Controller arbitration and release state machine

Start with one configurable digital shoot button. Rhythm-stick gestures and other multi-axis release sequences require separate ownership semantics and validation before enablement.

Suggested states: `Disabled`, `Observing`, `Ready`, `TrackingShot`, `ReleaseScheduled`, `ReleasedLatched`, and `Suspended`. These are proposed implementation states, not existing runtime APIs.

| Event | Required action |
| --- | --- |
| Enable observation | Start capture/inference and diagnostics. Send no automated controller changes. |
| Arm with valid source/model/input/output | Enter `Ready` only after warm-up and compatibility checks. |
| Physical shoot rising edge | Create a new shot/session token, preserve the initial manual press, and begin eligible tracking. |
| Reliable early prediction | Publish a deadline carrying shot token, profile generation, controller connection generation, source generation, confidence, and expiry. |
| Physical button released before deadline | Cancel the scheduled action and follow manual release immediately. |
| Scheduled deadline reached | Arbiter forces only the owned shoot channel released, preserves all other current channels, and records the actual local send/handoff timing. |
| Physical button remains held after automated release | Keep the channel forced released in `ReleasedLatched`; later polls/macros cannot press it again. |
| Physical button returns released | Clear the latch and require a new physical rising edge before rearming. |
| Tracking confidence or stream freshness fails | Cancel an unexecuted deadline and return shoot control to the player. Preserve any already-established release latch until physical release. |
| Stop all, disconnect, output failure, or application shutdown | Cancel all pending tokens, disarm vision, and neutralize output through existing shutdown semantics. Never replay queued shot actions after reconnection. |
| Profile/controller/source/model changes | Invalidate previous generations, cancel deadlines, and require a fresh released-to-pressed input cycle after readiness. |

Vision must not hold the shoot button past the user's physical release. Loss of confidence before an automated release must not manufacture a new hold or press. If a released latch exists when the profile/source/model changes, prevent the still-held physical button from being reasserted during reinitialization; require physical release before a fresh press can start another attempt. On stop/fault, report whether neutralization was locally accepted; do not claim Xbox acknowledgement if transport is gone.

Establish channel ownership between vision, macros, and Lua. The first version should reject or suspend assistance for conflicting shoot-channel automation, with a clear explanation. Unrelated buttons and sticks continue normally.

For the existing Lua implementation, `output.release(control)` relinquishes ownership to normal input; it does not force the physical button released. A vision release latch must explicitly force the outgoing channel off. Lua observations/events may be added later without putting Lua resumes or callbacks in the release-deadline path.

Audit the existing 32-report output FIFO. Coalescing must preserve meaningful press/release edges and never let an old held report follow a latched release. Define bounded backpressure and fault behavior. Do not reduce the queue blindly or discard all intermediate states: a short press must remain observable by the virtual device. Include the existing helper process and local transport in every latency measurement.

## 10. Scheduling, allocation, and GC

- Keep capture, inference, output arbitration, and UI responsibilities separate. The existing 4 ms input cadence is not a 1 ms or hard-deadline guarantee.
- Use deadline wake-ups that serialize through the output arbiter. Do not let an inference call or UI dispatcher callback own the timing of release.
- Reuse frame resources, tensor buffers, model sessions, and high-frequency diagnostics storage. Move formatting, percentile calculation, and file serialization off critical paths.
- Inspect current per-sample allocations and locks in `StudioRuntime`, including telemetry snapshots, together with Lua and voice behavior. An allocation-free vision loop alone does not prevent process-wide GC pauses.
- Measure allocation rates, GC pause events, scheduler lateness, lock contention, and GPU waits under realistic application load.
- Start with normal scheduling behavior and a dedicated worker where justified. Change priority or affinity only after measurements show a benefit to tail latency without harming input, decoding, audio, or UI responsiveness.
- Affinity limits eligible logical processors; it does not reserve a physical core. `Thread.CurrentThread.ProcessorAffinity` is not a valid .NET API. Do not implement examples using it.
- Keep all claims soft real time. No priority setting, pinned buffer, or zero-allocation rule eliminates Windows scheduling, driver, GPU, GC, or stream delays.

## 11. User interface and persistence

Add a Vision page/panel following existing navigation and accessible controls. Show:

- source picker, crop/preview, model selection and compatibility;
- supported animation/profile settings and calibration state;
- separate Observe and Armed states, with a visible reason when assistance is unavailable;
- tracked-player confidence, stale/frozen-source indicators, and skipped/missed-deadline counts;
- local processing and output-handoff metrics, with upstream/input estimates labeled separately;
- start/stop controls integrated with Stop all and physical early-release behavior.

Preview overlays are optional application diagnostics, not an in-game player icon requirement. Use keyboard-accessible controls, meaningful automation names, readable status text, focus behavior, and text larger than XS. Do not encode status solely through color.

Store vision settings in a versioned profile structure. Validate crop bounds, finite numeric values, timing limits, model schema/hash, and supported shot configuration on load/import. Preserve older profiles and default vision to disabled. Imported profiles cannot silently start capture or arm output. Keep calibration bound to the source/model/game/animation configuration and invalidate it when those assumptions change.

## 12. Diagnostics and measurement contract

Record bounded timestamped events for capture arrival, frame replacement, preprocessing, inference completion, prediction, deadline publication/cancellation, scheduler wake-up, output enqueue, helper handoff, and failures. Include shot and generation tokens so stale work can be identified.

Report p50, p95, p99, maximum, sample count, measurement duration, and workload metadata. Separate:

1. capture sampling and local frame age;
2. preprocessing and CPU/GPU transfers;
3. inference and postprocessing;
4. prediction error on labeled held-out clips;
5. requested versus actual local send/handoff deadline;
6. estimated combined streaming/input correction and uncertainty;
7. independently observed gameplay outcomes where available.

The in-app performance panel reports bounded recent windows of up to 2,048 samples for local frame age at processing start, frame-to-model-decision latency, all-frame processing, preprocessing, and inference including transfers. Capture-only operation has no model decisions, so its decision/inference metrics remain unavailable. Rejection sentinels that run no inference count only as frame processing. Budget exceedances use the same recent model-decision window. Worker allocations within measured frame processing and process GC collection counts are diagnostics; counts are not pause durations. The existing helper handoff estimate remains a latest value only, without invented percentile history.

Save/load opt-in local JSON performance runs to compare matching model/source dimensions/crop/profile/camera and timing context. Backend differences are allowed deliberately; report them prominently. Require at least 100 decisions per run before reporting timing deltas. Different live gameplay inputs remain uncontrolled, so these comparisons show observed differences rather than proving a controlled CPU/GPU benchmark or Xbox-side accuracy.

Do not label helper stdin flush as Xbox receipt. Exact console-applied release error requires external validation or a trustworthy console acknowledgement that this capture/output design does not currently provide. When only local metrics or coarse game feedback are available, state that limit in reports.

Raw gameplay recording and detailed export are opt-in and asynchronous. Keep credentials, account identifiers, raw clips, weights, and machine-specific private paths out of repository commits and routine logs. Bound recording storage and handle full disks without blocking controller output.

## 13. Proposed files and boundaries

Confirm names against existing conventions during implementation. Keep new components under `src/AccessRelayStudio/`:

| Proposed component | Purpose |
| --- | --- |
| `Vision/RemotePlayCapture.cs` | Capture selection/session lifecycle and frame timestamps. |
| `Vision/LatestFrameBuffer.cs` | Bounded reusable resources with explicit ownership. |
| `Vision/VisionModel.cs` | Model manifest, validation, ONNX session, providers, warm-up, and reusable tensors. |
| `Vision/FramePreprocessor.cs` | Crop, resize, color conversion, normalization, and measured transfers. |
| `Vision/ShotTracker.cs` | Player identity, temporal state, model observations, confidence, and shot token. |
| `Vision/ReleasePredictor.cs` | Time-to-target estimate, calibrated deadline, and uncertainty checks. |
| `Vision/VisionCalibration.cs` | Versioned calibration records and bounded offset estimation. |
| `Vision/VisionDiagnostics.cs` | Bounded event records and off-path summaries/exports. |
| `Vision/VisionSettings.cs` | Profile settings, validation, and compatibility metadata. |
| `Core/ControllerOutputArbiter.cs` | Single-owner state merge, channel ownership, release latch, and token validation. |
| `Core/ReleaseScheduler.cs` | Cancellable monotonic deadlines routed through the arbiter. |
| `VisionPanel.xaml` and `.xaml.cs` | Accessible configuration, preview, observation/arm state, and diagnostics. |
| `Verification/VisionVerification.cs` | Deterministic timing, ownership, buffer lifecycle, and profile checks. |

Integrate with `StudioRuntime`, `MainWindow`, profile storage, output/helper diagnostics, and build/publish rules through focused changes. Reuse existing primitives where they satisfy the contract. Do not add a second virtual-controller stack such as Python `vgamepad`.

The current verification project links `Core/*.cs` into a portable `net10.0` executable. Keep proposed Core state/clock/arbiter contracts platform-neutral and inject a clock/output interface. Put Windows capture, GPU resources, OS timer/affinity adapters, and production output wiring outside that linked Core set, or introduce a separate Windows integration project deliberately. New Core files must not silently break the existing portable verification build.

## 14. Implementation sequence and gates

| Phase | Deliverable and exit evidence |
| --- | --- |
| 0. Establish the source baseline | Publish the existing application changes and updated plan to `main`, validate the managed verification and Windows Release publish, then advance the feature branch to that baseline. Keep optional native-host and physical hardware checks distinct from managed build results. |
| 1. Prove the controller route | Existing virtual output controls the selected Remote Play session; press/hold/release, player assignment, manual passthrough, duplicates, cancellation, and reconnect behavior are recorded. No vision automation yet. |
| 2. Capture and instrument | User-selected window capture, bounded newest-frame handling, timestamps, resizing/freeze/loss handling, and observation-only diagnostics work. Measure the path on the actual Remote Play stream. |
| 3. Implement arbitration and deadlines | Deterministic verification proves channel ownership, cancellation, release latching, queue ordering, and generation invalidation. Synthetic observations exercise the path without a trained model; mark them as synthetic. |
| 4. Establish the model | Reviewed dataset, declared supported environments, held-out accuracy results, export agreement, model manifest, and initial inference measurements exist. No generic-model placeholder is treated as a finished detector. |
| 5. Integrate selected inference backend | Reusable memory and warm-up, explicit CPU/CUDA verification, bounded processing, and timestamps work on the target PC. For CUDA, verify the RTX 2070 path with fallback disabled. Record transfers, actual provider, memory use and tails separately for each backend. |
| 6. Calibrate and run controlled trials | Combined-offset calibration, confidence thresholds, prediction deadlines, and manual override operate in a controlled practice environment. Record outcomes and failure cases across sessions. |
| 7. Harden and publish | Accessible UI, persistence/import, packaged dependencies, audio coexistence, soak/fault tests, regression checks, and honest limitations are complete. Publish as experimental until the acceptance gates are supported by evidence. |

Do not enable automated release merely because capture reaches 60 FPS or an inference benchmark looks fast. Observation-only operation is a useful intermediate deliverable.

## 15. Verification and acceptance

### Deterministic verification

- Clock conversion and deadline arithmetic, including negative/expired horizons, nonfinite values, calibration sign, and overflow boundaries.
- One scheduled action per shot; replacements do not double-release; expired source/profile/controller/model generations cannot affect a new shot.
- Early physical release cancels automation; unrelated channels remain correct; the release latch survives subsequent held physical and macro samples and clears only on the defined rearm edge.
- Stop all, source closure, tracking failure, profile changes, and disconnects follow the state table. No action resumes after reconnection without a fresh eligible press.
- Bounded latest-frame replacement, disposal, resize/device-loss lifecycle, and no overwrite of an in-flight buffer.
- Output FIFO/backpressure ordering preserves button edges, cancellation, and neutralization. Include forced helper failure and delayed handoff.
- Profile round trips and old-profile compatibility; malformed settings, incompatible manifests, and imported armed states are rejected or disabled.
- Deterministic fake clocks and injected capture/output delays drive scheduler tests. Wall-clock sleep tests alone are insufficient for correctness.

### Windows/backend integration and performance

- Exercise actual browser/client capture and our virtual device together on the RTX 2070 system. Include focus changes, physical-controller reconnects, window resize/minimize, stream reconnection, and output-driver failure.
- Verify current-frame inference after repeated buffer updates; compare both CPU and CUDA outputs against the exported reference. Test actual device loss and orderly resource cleanup where practical. Hardware acceptance and calibration must match the selected provider.
- Benchmark capture-only, inference-only, and the full path separately, both warm and cold. Cold work must complete before arming.
- At a selected 60 FPS source, sustained full-frame analysis targets at most 16.67 ms per processed frame. This is an engineering throughput target, not a promised result or an accuracy guarantee. Falling behind must drop obsolete observations and suspend when freshness exceeds the validated limit.
- Report deadline tails and misses under idle conditions and with UI activity, Lua/macros, live voice, recording, GPU contention, and network disturbances. Include a documented sustained session, initially at least 30 minutes.
- Establish the permissible prediction/deadline error and stale-frame/confidence limits using the supported animation's measured tolerance. Do not assume a universal 35 ms green window or invent universal accuracy thresholds.
- Set explicit confidence, freshness, deadline, accuracy, and false-release gates using training, validation, and calibration data. Lock the model, preprocessing, calibration procedure, and thresholds before the final held-out evaluation. Before arming by default, document final held-out target/identity accuracy, false releases, prediction-error distribution, local deadline misses, calibration repeatability, and gameplay outcomes for each supported configuration. Retain the evaluation artifacts. A failed final evaluation requires a new development cycle and a fresh untouched final test set; do not tune against the failed final set and claim it remains held out.
- Hardware-only or model-dependent checks that cannot run in CI remain visibly pending. Unit tests cannot certify Remote Play acceptance, console timing, or hardware inference speed.

Use the repository's controller verification command for applicable managed checks:

```powershell
dotnet run --project ./src/AccessRelayStudio/Verification/Verification.csproj -c Release
dotnet publish ./src/AccessRelayStudio/AccessRelayStudio.csproj -c Release -o ./artifacts/Run
```

Extend the verification project deliberately with explicit compile includes or a portable library for pure Vision logic; its existing Core wildcard does not automatically include the proposed Vision components. Put Windows/GPU checks in a separate integration-test boundary so ordinary verification does not require live hardware. Update third-party notices and GPU setup instructions with the selected dependency versions before packaging.

## 16. Risks and explicit limits

| Risk | Mitigation and remaining limit |
| --- | --- |
| Remote Play video/input variability | Predict from early phases, calibrate combined offset, monitor stalls and uncertainty. An unseen animation change cannot be repaired after the fact. |
| Wrong player or animation | Temporal identity tracking, physical-input context, supported shot families, confidence checks, and negative examples. Skip ambiguous attempts. |
| Compression, occlusion, or game updates | Representative data, held-out sessions, configuration/version binding, reacquisition, and explicit unsupported states. |
| GC, scheduling, or GPU contention | Reusable buffers, short locks, bounded queues, warmed sessions, independent output arbitration, and tail measurements. No hard real-time guarantee. |
| Output reassertion or stale command | One arbiter, release latch, shot/generation tokens, expiry, and FIFO/backpressure tests. |
| Native runtime and model packaging | Pin compatible dependency sets, verify licenses/provider execution, validate a clean-machine publish, and preserve audio inference. |
| Missing or poor model | Treat dataset/training/export/validation as required implementation work. Keep assistance disabled until evidence supports the selected configuration. |

## 17. Documentation delivery and branch workflow

Publish the existing application source/documentation changes together with the updated implementation plan to `origin/main`. Include the tracked edits, intentional deletion, and new source/documentation files that were already present when the plan was requested. Review the publication scope and run the relevant existing verification and Release publish checks first. Keep ignored build outputs, models, recordings, credentials, and machine-specific configuration outside the source commit.

The initial plan was already pushed separately and the feature branch was created. Complete the requested baseline by pushing the application changes normally to `main`, then fast-forwarding `codex/remote-play-vision` to that exact baseline commit. Push the feature branch, verify both remote refs and published content, and leave it checked out with its upstream set. Do not reset, stash, discard, or force-push existing work. Completion means the authorized application changes are committed and published, not left uncommitted in the working tree. The experimental software is implemented; gameplay-model and Xbox-side acceptance remain separate unfinished gates.

## 18. Technical references

References checked during the design discussion. Recheck compatibility against the dependency versions actually selected during implementation.

- [Xbox Remote Play](https://www.xbox.com/en-US/consoles/remote-play): console streaming to supported devices/browsers.
- [Windows application/window capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture): frame pool, surfaces, timestamps, and lifecycle.
- [ONNX Runtime C# tutorial](https://onnxruntime.ai/docs/tutorials/csharp/basic_csharp.html): preferred `OrtValue` API and legacy wrappers.
- [C# buffer reuse](https://onnxruntime.ai/docs/get-started/with-csharp.html#reuse-inputoutput-tensor-buffers): reusable input/output buffers and disposal.
- [C# I/O binding](https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.OrtIoBinding.html): memory placement, binding, and copies.
- [ONNX I/O binding](https://onnxruntime.ai/docs/performance/tune-performance/iobinding.html): device versus host allocation and transfer behavior.
- [CUDA execution provider](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html): dependencies, tuning, synchronization, and CUDA Graph constraints.
- [TensorRT execution provider](https://onnxruntime.ai/docs/execution-providers/TensorRT-ExecutionProvider.html): optional backend and compatibility requirements.
- [C# session options](https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.SessionOptions.html): supported CUDA/TensorRT configuration APIs.
- [.NET background/foreground GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/background-gc): managed-thread suspension remains possible.
- [Windows scheduling priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities): scheduling and preemption behavior.
- [.NET process-thread affinity](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processthread.processoraffinity): actual affinity API and scope.
- [NVIDIA benchmarking guidance](https://docs.nvidia.com/deeplearning/tensorrt/latest/performance/benchmarking.html): separate GPU, transfer, host, and full-pipeline measurements.
