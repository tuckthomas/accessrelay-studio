# AccessRelay Studio accessibility and platform roadmap

Approved: 2026-11-12. Baseline: `854ca76d80b52f94437a6c84ba50e02f8bcece77`.

This checklist records the revised review of Gemini Argon's recommendations. Start
with confirmed accessibility defects, then add input accessibility, local recognition,
and game packs. Treat model and GPU changes as experiments until measurements justify
shipping them. Existing profiles, controller ownership, cancellation, and explicit
operator activation must remain compatible.

Current checkpoint: sections 1–2 are complete. Sections 3–9 have shipped code
and passing Windows CI with simulated devices; physical/adaptive-device, native
WebView and real microphone acceptance remains open. Section 10 has session/footage
provenance guards and reviewed conversion implemented; real clip review remains open.
Section 11 has experiment tooling implemented and synthetic/numerical validation;
real model comparisons remain open. Section 12 now has bounded readback/lock profiling implemented; actual Windows
CPU/CUDA measurements and any optimization decision remain open.

## Delivery and evidence

- [x] Push this plan to GitHub `main` before implementing any checklist item.
      Plan commit: `caf215d823ef1ef77d767207cdcd816b59a84366` (remote verified before implementation).
- [ ] Implement each feature in a separate commit containing its relevant tests.
- [ ] Update this checklist as requirements are completed; leave unverified items open.
- [ ] Record each completed section's full implementation commit SHA and validation
      below in a following checklist update commit. Do not amend published commits
      to try to insert their own SHA. Push feature and checklist commits to `main`
      without force-pushing, checking for concurrent remote changes first.
- [ ] Distinguish portable checks from Windows, hardware, real-user, and model/data
      validation. Record concrete blockers rather than claiming those checks passed.

## 1. Modal containment

- [x] Make background content inert while a modal is active, including nested dialogs.
- [x] Contain forward/reverse Tab navigation and recover escaped focus.
- [x] Preserve Escape dismissal, modal descriptions, action-menu-to-dialog focus,
      opener focus restoration, and existing page layout.
- [x] Add browser tests for keyboard wrapping, background isolation, nested dialogs,
      and restoration; preserve existing action-menu tests.
- [x] Run frontend unit tests, browser tests, and production build.
- [x] Push implementation and record its commit SHA and validation here.

Implementation commit: `a7bc6a32d988a8b1253420ed43eedcc9249babe9`.
Validation: 44 unit tests, 8 Chromium browser tests, TypeScript and Vite production
build passed. Browser checks cover nested dialogs, Tab/Shift+Tab wrapping, blocked
background focus, descriptions, Escape, and menu-trigger/opener restoration.
Windows CI also passed: https://github.com/tuckthomas/accessrelay-studio/actions/runs/43252380240.
Real-device/WebView2 interaction and screen-reader listening remain separate checks.

## 2. Meaningful status announcements and menu regression coverage

- [x] Announce controller connection changes, voice lifecycle/failures, and meaningful
      vision/calibration transitions through persistent polite/alert live regions.
- [x] Avoid initial-state chatter, repeated messages, telemetry announcements, and
      duplicate error announcements; announcements must remain available across pages.
- [x] Preserve announcements when a modal isolates background content.
- [x] Add tests for transitions, repeated snapshots, failures, and modal interaction.
- [x] Add browser regression assertions for existing Radix Home, End, typeahead,
      `aria-controls`, and `aria-labelledby`; do not replace working menu behavior.
- [x] Run frontend unit tests, browser tests, and production build.
- [x] Push implementation and record its commit SHA and validation here.

Implementation commit: `5484226d7e0fc39929b65a9865b82fbf7302bf1f`.
Validation: 49 unit tests, 9 Chromium browser tests, TypeScript/Vite build, and
isolated compilation of the real audio sources passed. Browser tests cover live
regions inside a modal and the existing menu keyboard/ARIA contract. The host now
reports asynchronous voice failures explicitly instead of masking them with old
service text. Windows CI passed: https://github.com/tuckthomas/accessrelay-studio/actions/runs/43253558902.
Real screen-reader/audio-device validation remains separate; no listening or
microphone hardware test is claimed.

## 3. Sticky button accessibility

- [x] Add opt-in per-button press-to-latch/press-to-release configuration and UI.
- [x] Keep sticky intent separate from vision's forced-release suppression latch.
- [x] Define precedence with manual input, macros, recording, and vision ownership.
- [x] Clear state on Stop, disconnect, profile/device changes, and disabling the feature;
      require a fresh press before reactivation.
- [x] Test edges, simultaneous controls, precedence, persistence compatibility, and resets.
- [x] Validate portable logic and Windows runtime/output behavior with simulated devices.
- [ ] Validate real physical controller/trigger behavior and native UI bridge interaction.
- [x] Push implementation and record its commit SHA and validation here.

Implementation commit: `14fd7f40f8262df58ce9b60d8d48f03cbecd7d57`. Portable controller verification, 51 frontend
unit tests, TypeScript/Vite build, existing browser checks, and Windows verification
cross-compilation passed. Native runtime tests use simulated devices and are added
to Windows CI and passed: https://github.com/tuckthomas/accessrelay-studio/actions/runs/43255058465.
Real physical device validation remains open. See
`docs/controller-accessibility.md` for precedence, trigger behavior and profile v4 compatibility.

## 4. Tremor smoothing

- [x] Add an opt-in stateful stick filter separate from stateless deadzone calculation.
- [x] Use elapsed-time-based smoothing with configurable strength and clear UI guidance
      about the responsiveness tradeoff; preserve default input behavior.
- [x] Reset history on Stop, disconnect, profile/device changes, and disabling;
      define prompt neutralization without residual stick output.
- [x] Test varying sample rates, neutral input, bounds, independent sticks, and resets.
- [x] Validate filter response and the Windows runtime path with simulated devices.
- [ ] Measure physical input-to-output latency and comfort on target devices.
- [x] Push implementation and record its commit SHA and validation here.

Implementation commit: `0d621148bb225dea2e6966ea35f612d4bfe46e26`. Portable filter/controller checks, 52 frontend
unit tests, TypeScript/Vite build, browser suite and smoothing workflow, and Windows
verification cross-compilation passed. Deterministic response checks verify the
20 ms time constant and sample-rate invariance; these are not device latency measurements.
Windows CI, including the native runtime checks with simulated devices, passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43255997488.
Real physical comfort/latency validation remains open.

## 5. Switch and controller navigation

- [x] Add opt-in one-switch automatic scanning and two-switch step/select modes with
      configurable timing, debounce, focus indication, and an accessible exit.
- [x] Scope scanning to the active modal/menu or page; skip hidden/disabled controls
      and support editing sliders/selects without losing navigation.
- [x] Route native physical input through the bridge; define input consumption so
      navigation does not accidentally activate gameplay. Keep browser preview useful.
- [x] Test dynamic controls, dialogs, repeat edges, Stop, disconnect, and profile changes.
- [ ] Validate native gamepad/adaptive-switch behavior on Windows before completion.
- [x] Push implementation and record its commit SHA and validation here.

Implementation commit: `0b27dc0f67d65c3a50efbb5713cef249be421cce`. Validation: 54 frontend unit tests, 16 Chromium
browser tests, portable controller checks, and Windows verification cross-compilation
passed. Tests exercise keyboard scanning, native bridge ordering, stale pre-activation
snapshots, lost history, sliders/selects, nested scope, repeat suppression and exits.
New Windows runtime tests verify physical edges and neutral gameplay with simulated
devices. Windows CI passed: https://github.com/tuckthomas/accessrelay-studio/actions/runs/43272516594.
Real adaptive-switch/gamepad acceptance remains pending.

## 6. Nonvisual feedback

- [x] Add independently configurable earcons and haptic cues for meaningful profile and
      vision events, with rate limiting, volume/intensity controls, and a test action.
- [x] Handle missing selected gamepads and unavailable audio/rumble APIs without changing
      controller output or blocking capture, audio callbacks, or deadline processing.
- [ ] Verify motor capability and actual actuation on physical target devices; exposing
      the WinRT vibration interface does not prove a driver has working motors.
- [x] Keep visual physical-input feedback separate from the new event feedback service.
- [x] Test deduplication, cancellation, disabled behavior, and unsupported devices.
- [ ] Validate audio/rumble timing and comfort on real Windows devices.
- [x] Push implementation and record its commit SHA and validation here.

Implementation commit: `01ade2ede11f0dcd591d72baf0f73cd569e23838`. Validation: portable feedback/controller tests,
56 frontend unit tests, the existing 16 browser workflows plus the new feedback
workflow, Windows verification cross-compilation, and compilation of real audio
sources against NAudio passed. Windows CI passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43275807798. The bounded worker is tested with fake blocked/failing
devices; Windows runtime event-hook tests use simulated cue outputs. Actual sound,
rumble capability, timing and comfort require real Windows devices.

## 7. Co-pilot input merging

- [x] Add explicit device selection and per-control ownership/conflict rules for two
      or more physical inputs feeding one virtual output; exclude virtual feedback loops.
- [x] Define analog priority and button ownership so disconnecting one participant
      does not release another participant's held controls or leave stale output.
- [x] Integrate Stop, macros, vision, sticky buttons, and recording with one output owner.
- [x] Test merging, disconnect/reconnect, conflicts, and persistence compatibility.
- [ ] Validate with multiple physical/adaptive devices on Windows before completion.
- [x] Push implementation and record its commit SHA and validation here.

Implementation commit: `41c713870067036430bf6decab73e4614113d678`. Validation: portable
merge/ownership/disconnect/conflict/persistence/provenance checks, 56 frontend unit
tests, TypeScript/Vite build and 18 browser workflows (17 unchanged workflows plus
the co-pilot workflow) passed. Actual Windows input/audio sources and runtime tests
were cross-compiled. Native runtime tests with simulated contributors are queued for
Windows CI, which passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43280232084.
Physical multi-device acceptance remains open.

## 8. Local voice commands and personalized vocalizations

- [x] Select a licensed local recognition model and define latency measurements from
      acoustic onset/end through controller delivery; do not promise unmeasured 30/100 ms.
- [x] Feed independent bounded recognition workers from microphone capture before
      voice conversion. MRX is currently imported-clip cleanup, not live microphone cleanup.
- [ ] Evaluate echo/noise handling without suppressing intended clicks/hums; measure
      false activations with game speech and dysarthric/personalized inputs.
- [x] Add opt-in active-profile phrase/template bindings, recording/training UI,
      confidence/refractory settings, and explicit activation/status controls.
- [x] Route command IDs through host-owned arbitration and cancellation, with bounded
      actions, Stop/disconnect/profile reset handling, and no direct worker output writes.
- [x] Test queue overload, stale commands, recognition faults, and synthetic audio.
- [ ] Validate real microphone/model accuracy, resource use, and latency separately.
- [x] Push each independently usable feature and record section commit SHAs here.

Implementation commit: `f9efb64e4ca57d2543bb5381bb98b8d29b06cd96`. Backend choice:
installed Windows desktop speech engines (OS-licensed models; MIT System.Speech
adapter) with an explicit phrase grammar, plus local spectral/DTW templates. The
first phrase backend processes bounded utterances rather than making an unmeasured
streaming ONNX latency promise. See `docs/voice-commands.md` for licensing, host
ownership, raw microphone routing, queue/age limits, startup behavior and limitations.
Portable synthetic-audio/fault/overload/cancellation checks, 57 frontend unit tests,
19 Chromium browser workflows, TypeScript/Vite build, Windows runtime cross-compile
and actual audio-source compilation passed. Windows runtime integration tests use
simulated devices. The Windows route scope fix is `a16dec9f42083b24b0c6f014a32d50a0802426d4`;
Windows publish and simulated-device runtime CI passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43284553379.
Real microphone/SAPI operation, echo/noise/dysarthric accuracy and
physical delivery latency remain open; no such measurement is claimed.

## 9. Versioned game packs

- [x] Define bounded/versioned manifest, host action requests, typed vision adapters,
      declarative tuning controls, compatibility rules, and resource/trust restrictions.
- [x] Keep scheduling, freshness checks, generation tokens, cancellation, and final
      controller ownership in the host. Never expose unrestricted output writes to Lua.
- [x] Extract NBA-specific code/training into an in-repository pack incrementally,
      updating build/test paths and preserving existing saved profiles and model imports.
- [x] Add pack loading/import, tuning UI, bounded Lua behavior, and a second minimal
      pack proving the contract is not tied to NBA; reject unsupported native plugins.
- [x] Test malformed manifests, path traversal, oversized assets, hashes, compatibility,
      cancellation, and backward migration. A hash is integrity, not publisher authenticity.
- [ ] Validate real Windows pack import/UI/model integration before completion.
- [x] Validate host integration before considering an `accessrelay-packs` repository;
      do not create/publish a marketplace or distribute third-party weights prematurely.
- [x] Push migration features separately and record section commit SHAs here.

Implementation commits: NBA source/training extraction `ce2eee28aa3831ea8bb0e31488afbf797d26261f`. Validation: portable controller/profile/Lua tests,
26 dataset tests, existing trainer/evaluator entry points, desktop evidence tests
and Windows/vision verification compilation passed. Core contains no NBA-specific
type references. Legacy profile JSON/fingerprints and model contracts are retained;
release scheduling stays host-owned. Pack SDK/import/tuning commit: `d82bad724fe02662077e76916c7304cfcc76754f`.
Validation: pack schema/path/hash/size/version/link/cancellation tests, real Lua
output/tuning and profile-v5/legacy persistence checks, portable author CLI, 57
frontend unit tests, 20 browser workflows, TypeScript/Vite build, desktop evidence
checks and Windows/vision verification compilation passed. The second pack has no
NBA/vision dependency. See `docs/game-packs.md` for exact bounds, typed adapter
compatibility, trust limits and activation behavior. Windows publish and simulated-device runtime CI passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43286208711.
Real Windows UI/model/device acceptance remains open.
Marketplace split: deferred until the contract is proven.

## 10. Dataset provenance and training interoperability

- [x] Add direct regression coverage for existing train/validation/held-out session
      separation. Validation evaluation now rejects training IDs even if labels are relabeled.
- [x] Evaluate duplicate footage under distinct session IDs; identity checks alone
      do not establish footage independence.
- [x] Complete reviewed conversion of enriched recorded evidence, preserving source
      identity, teacher/student mapping, camera/profile provenance, and meter exclusion.
- [x] Enforce source purpose, disjoint overlay ROI, source/reference identities and
      integrity-bound reviews; never remove markers to bypass compatibility checks.
- [ ] Verify actual meter exclusion/label quality and held-out independence on reviewed gameplay.
- [x] Test conversion and rejection paths with synthetic fixtures and the actual CLI.
- [ ] Exercise real reviewed clips before claiming usable gameplay training.
- [ ] Push independent improvements and record section commit SHAs here.

Implementation commits: provenance guard `26866553388b539a973c541c91d4307f76f1b04f`;
optional-NumPy import follow-up `0dd0e35a47f17d6a229076d4d980fa50fb2ef9ec`. Validation: 26 dataset/provenance tests
passed with Python site packages disabled (`-S`); image preprocessing also retained
its expected RGB tensor behavior with NumPy installed. Windows CI now runs the
stdlib-only suite and passed on Windows:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43257634331. Enriched
reviewed conversion commit: `be45856e747052d3b674e389b78e07ccdd175704`.
Validation: 43 stdlib-only dataset/footage/conversion tests and teacher/validation
CLI conversions on explicitly synthetic streams passed. Raw evidence remains
rejected until review proof binds source/labels/reference and all data hashes.
The converter preserves original evidence and source IDs; it rejects overlapping
model/overlay ROIs and meter-on evaluation relabeling. No pixel mask or preprocessing
contract is silently changed. See `docs/vision-model-workflow.md` for the workflow.
Windows CI passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43289322222.
Real clips, label quality and physical model/timing acceptance remain open. Footage reuse guard commit: `855717a127df3fa175438319b3f1daf91ad83cdc`. Validation: all 33 stdlib-only dataset/footage tests passed; Windows CI also passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43287225043.
The trainer and evaluator record/check RGB frame/window hashes independently of
session IDs and reject reused configuration-reference footage. These exact checks
cannot detect every transformation and do not replace source review. Prerequisites: reviewed
source clips for functional training validation.

## 11. Measured model experiments

- [x] Add a reproducible validation-only matrix, corpus binding and descriptive
      timing/coverage/false-release/resource metrics; verify it using synthetic data.

- [ ] Compare the existing temporal-convolution/global-pooling baseline with an
      order-preserving head using identical session splits and reproducible seeds.
- [ ] Compare residual uncertainty training with bounded Gaussian NLL or staged warm-up;
      measure held-out calibration coverage, false releases, timing error, and inference cost.
- [x] Version changed model semantics and retain existing model/runtime compatibility.
- [x] Keep all candidates observation-only; summaries never select/promote a model.
- [ ] Collect independent accuracy and Windows hardware timing evidence before promotion.
- [x] Push experiment tooling separately and record code commit SHAs and report paths.

Implementation commit: `bb831d72fe4ec8f37a94a0e2aa5971c9d6ca8b1f`. Validation: 51
Python checks passed with optional ML dependencies; the stdlib-only Windows suite
runs 46 checks and skips five ML numerical/export tests. Six seeded synthetic
training/export/validation jobs and a plan-only CLI run passed. All six variants
passed ONNX CPU parity; the Gaussian/ordered model also ran through the real C#
CPU adapter, producing 17 observations with `CanArm=False`. C# vision/model-library/
preprocessing/arming/performance tests and Windows runtime compilation passed.
See `docs/vision-model-experiments.md` and
`.agents/reports/vision-experiments-synthetic-smoke.md`. No architecture winner,
accuracy improvement or Windows GPU/console latency result is claimed.
Prerequisites for actual comparisons: reviewed train/validation/held-out gameplay and target hardware.

## 12. Profiled runtime optimization

- [x] Add bounded readback/copy and controller-lock measurements, profiler generation
      ownership/freeze, diagnostics UI and versioned performance imports.

- [ ] Measure capture readback, preprocessing, transfers, inference, and lock contention
      with representative Windows CPU/CUDA runs before choosing an optimization.
- [ ] Evaluate GPU preprocessing only with explicit D3D/ONNX interoperability and
      numerical parity; putting resize in ONNX alone does not remove CPU staging copies.
- [x] Preserve the CPU path, bounded memory, cancellation and canonical preprocessing.
      No additional backend is introduced without interoperability/parity evidence.
- [x] Preserve atomic output validation and lease/resource ownership. Keep current
      synchronization until measured contention justifies a specific replacement.
- [x] Test profiling generation races, frozen runs, reused frame leases, optional
      stages, zero-allocation recording and legacy/current saved-run handling.
- [ ] Compare actual end-to-end latency/resource reports before optimization promotion.
- [ ] Push justified optimizations separately and record their commit SHAs and evidence.

Profiling implementation commit: `a3de4a2a3915bcd312c9ec03660beaec08968ab5`.
Validation: portable controller/measurement tests, complete managed vision/model/
performance suite, 57 frontend unit tests, 20 browser workflows, TypeScript/Vite
build, physical-input probe and Windows runtime cross-compilation passed. New
Windows runtime tests exercise actual profiling integration with simulated devices.
Windows CI now also runs the standalone vision/performance suite and builds the
physical input probe. See `docs/runtime-profiling.md` for measurement scopes and
format-2 compatibility. Inference/transfer time remains an honest aggregate; no
GPU shader, buffer interoperability or lock-free optimization is claimed.
Prerequisites for remaining measurements: Windows capture/GPU and representative runs.
