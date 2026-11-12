# AccessRelay Studio accessibility and platform roadmap

Approved: 2026-11-12. Baseline: `854ca76d80b52f94437a6c84ba50e02f8bcece77`.

This checklist records the revised review of Gemini Argon's recommendations. Start
with confirmed accessibility defects, then add input accessibility, local recognition,
and game packs. Treat model and GPU changes as experiments until measurements justify
shipping them. Existing profiles, controller ownership, cancellation, and explicit
operator activation must remain compatible.

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
Native Windows/WebView2 and screen-reader listening checks remain separate from
the verified browser behavior.

## 2. Meaningful status announcements and menu regression coverage

- [ ] Announce controller connection changes, voice lifecycle/failures, and meaningful
      vision/calibration transitions through persistent polite/alert live regions.
- [ ] Avoid initial-state chatter, repeated messages, telemetry announcements, and
      duplicate error announcements; announcements must remain available across pages.
- [ ] Preserve announcements when a modal isolates background content.
- [ ] Add tests for transitions, repeated snapshots, failures, and modal interaction.
- [ ] Add browser regression assertions for existing Radix Home, End, typeahead,
      `aria-controls`, and `aria-labelledby`; do not replace working menu behavior.
- [ ] Run frontend unit tests, browser tests, and production build.
- [ ] Push implementation and record its commit SHA and validation here.

Implementation commit: pending. Validation: pending.

## 3. Sticky button accessibility

- [ ] Add opt-in per-button press-to-latch/press-to-release configuration and UI.
- [ ] Keep sticky intent separate from vision's forced-release suppression latch.
- [ ] Define precedence with manual input, macros, recording, and vision ownership.
- [ ] Clear state on Stop, disconnect, profile/device changes, and disabling the feature;
      require a fresh press before reactivation.
- [ ] Test edges, simultaneous controls, precedence, persistence compatibility, and resets.
- [ ] Validate portable logic and Windows bridge/output behavior before completion.
- [ ] Push implementation and record its commit SHA and validation here.

Implementation commit: pending. Validation: pending.

## 4. Tremor smoothing

- [ ] Add an opt-in stateful stick filter separate from stateless deadzone calculation.
- [ ] Use elapsed-time-based smoothing with configurable strength and clear UI guidance
      about the responsiveness tradeoff; preserve default input behavior.
- [ ] Reset history on Stop, disconnect, profile/device changes, and disabling;
      define prompt neutralization without residual stick output.
- [ ] Test varying sample rates, neutral input, bounds, independent sticks, and resets.
- [ ] Measure latency and validate the native input path before completion.
- [ ] Push implementation and record its commit SHA and validation here.

Implementation commit: pending. Validation: pending.

## 5. Switch and controller navigation

- [ ] Add opt-in one-switch automatic scanning and two-switch step/select modes with
      configurable timing, debounce, focus indication, and an accessible exit.
- [ ] Scope scanning to the active modal/menu or page; skip hidden/disabled controls
      and support editing sliders/selects without losing navigation.
- [ ] Route native physical input through the bridge; define input consumption so
      navigation does not accidentally activate gameplay. Keep browser preview useful.
- [ ] Test dynamic controls, dialogs, repeat edges, Stop, disconnect, and profile changes.
- [ ] Validate native gamepad/adaptive-switch behavior on Windows before completion.
- [ ] Push implementation and record its commit SHA and validation here.

Implementation commit: pending. Validation: pending.

## 6. Nonvisual feedback

- [ ] Add independently configurable earcons and haptic cues for meaningful profile and
      vision events, with rate limiting, volume/intensity controls, and a test action.
- [ ] Detect device capabilities and handle unavailable audio/rumble without changing
      controller output or blocking capture, audio callbacks, or deadline processing.
- [ ] Keep visual physical-input feedback separate from the new event feedback service.
- [ ] Test deduplication, cancellation, disabled behavior, and unsupported devices.
- [ ] Validate audio/rumble timing and comfort on real Windows devices.
- [ ] Push implementation and record its commit SHA and validation here.

Implementation commit: pending. Validation: pending.

## 7. Co-pilot input merging

- [ ] Add explicit device selection and per-control ownership/conflict rules for two
      or more physical inputs feeding one virtual output; exclude virtual feedback loops.
- [ ] Define analog priority and button ownership so disconnecting one participant
      does not release another participant's held controls or leave stale output.
- [ ] Integrate Stop, macros, vision, sticky buttons, and recording with one output owner.
- [ ] Test merging, disconnect/reconnect, conflicts, and persistence compatibility.
- [ ] Validate with multiple physical/adaptive devices on Windows before completion.
- [ ] Push implementation and record its commit SHA and validation here.

Implementation commit: pending. Validation: pending.

## 8. Local voice commands and personalized vocalizations

- [ ] Select a licensed local recognition model and define latency measurements from
      acoustic onset/end through controller delivery; do not promise unmeasured 30/100 ms.
- [ ] Feed independent bounded recognition workers from microphone capture before
      voice conversion. MRX is currently imported-clip cleanup, not live microphone cleanup.
- [ ] Evaluate echo/noise handling without suppressing intended clicks/hums; measure
      false activations with game speech and dysarthric/personalized inputs.
- [ ] Add opt-in active-profile phrase/template bindings, recording/training UI,
      confidence/refractory settings, and explicit activation/status controls.
- [ ] Route command IDs through host-owned arbitration and cancellation, with bounded
      actions, Stop/disconnect/profile reset handling, and no direct worker output writes.
- [ ] Test queue overload, stale commands, recognition faults, and synthetic audio;
      validate real microphone/model accuracy, resource use, and latency separately.
- [ ] Push each independently usable feature and record section commit SHAs here.

Implementation commits: pending. Prerequisites: model selection and real audio/device evaluation.

## 9. Versioned game packs

- [ ] Define bounded/versioned manifest, host action requests, typed vision adapters,
      declarative tuning controls, compatibility rules, and resource/trust restrictions.
- [ ] Keep scheduling, freshness checks, generation tokens, cancellation, and final
      controller ownership in the host. Never expose unrestricted output writes to Lua.
- [ ] Extract NBA-specific code/training into an in-repository pack incrementally,
      updating build/test paths and preserving existing saved profiles and model imports.
- [ ] Add pack loading/import, tuning UI, bounded Lua behavior, and a second minimal
      pack proving the contract is not tied to NBA; reject unsupported native plugins.
- [ ] Test malformed manifests, path traversal, oversized assets, hashes, compatibility,
      cancellation, and backward migration. A hash is integrity, not publisher authenticity.
- [ ] Validate host integration before considering an `accessrelay-packs` repository;
      do not create/publish a marketplace or distribute third-party weights prematurely.
- [ ] Push migration features separately and record section commit SHAs here.

Implementation commits: pending. Marketplace split: deferred until the contract is proven.

## 10. Dataset provenance and training interoperability

- [ ] Add direct regression coverage for existing train/validation/held-out session
      separation and evaluate duplicate footage under distinct session IDs.
- [ ] Complete reviewed conversion of enriched recorded evidence, preserving source
      identity, teacher/student mapping, camera/profile provenance, and meter exclusion.
- [ ] Prove meter-on supervision cannot leak into meter-off evaluation and preserve
      strict held-out independence; never remove markers to bypass compatibility checks.
- [ ] Test conversion and rejection paths with synthetic fixtures, then exercise real
      reviewed clips before claiming usable gameplay training.
- [ ] Push independent improvements and record section commit SHAs here.

Implementation commits: pending. Prerequisites: reviewed source clips for functional training validation.

## 11. Measured model experiments

- [ ] Compare the existing temporal-convolution/global-pooling baseline with an
      order-preserving head using identical session splits and reproducible seeds.
- [ ] Compare residual uncertainty training with bounded Gaussian NLL or staged warm-up;
      measure held-out calibration coverage, false releases, timing error, and inference cost.
- [ ] Version changed model semantics and retain existing model/runtime compatibility.
- [ ] Promote only a measured improvement; retain observation-only behavior until
      independent accuracy and Windows hardware timing evidence permit arming.
- [ ] Push experiment tooling separately and record code commit SHAs and report paths.

Implementation commits: pending. Prerequisites: reviewed training/validation/held-out data and target hardware.

## 12. Profiled runtime optimization

- [ ] Measure capture readback, preprocessing, transfers, inference, and lock contention
      with representative Windows CPU/CUDA runs before choosing an optimization.
- [ ] Evaluate GPU preprocessing only with explicit D3D/ONNX interoperability and
      numerical parity; putting resize in ONNX alone does not remove CPU staging copies.
- [ ] Preserve the explicit CPU path, bounded memory, cancellation, and versioned
      preprocessing compatibility when introducing another backend.
- [ ] Replace synchronization only where measured contention warrants it; preserve
      atomic output validation and lease/resource ownership during stop/source changes.
- [ ] Test lifecycle races and compare end-to-end latency/resource reports before promotion.
- [ ] Push justified optimizations separately and record their commit SHAs and evidence.

Implementation commits: pending. Prerequisites: Windows capture/GPU and representative performance reports.
