# MCP assistant workflows

Approved: 2026-11-12. Implement the reviewed first release: useful diagnostics,
configuration drafts, production-engine Lua simulation, and bounded offline
dataset/experiment workflows. Keep React and MCP as adapters to shared application
services. Native timing, output ownership and model acceptance remain in the runtime.

## 1. Delivery and scope

- [x] Create and push this checklist before implementation.
- [ ] Deliver independent features in separate commits on `main`, updating this
      checklist with full implementation SHAs and concrete validation evidence.
- [ ] Preserve the completed desktop separation and the active accessibility roadmap;
      do not invent hardware, microphone, screen-reader or gameplay validation.

This release uses the official C# MCP SDK with a console stdio bridge to the running
app over a current-user-only named pipe. No HTTP listener or cloud model is required.
The app owns an expiring, opt-in authorization window. External assistants cannot
start controller output, microphone/capture, speak to party chat, arm models, edit
labels, or apply a draft without the user reviewing it in Studio.

## 2. Shared application services and diagnostic resources

- [ ] Add a portable application project referencing the engine, with typed host
      context and serialized, UI-independent use cases; no desktop/WinUI dependency.
- [ ] Expose bounded, redacted effective configuration, connection/runtime status,
      actionable blocker explanations and versioned capability/contract resources.
- [ ] Add assistant prompts for accommodation planning, diagnosis, dataset coverage,
      personal-sound coaching and experimental evidence review, grounded in actual APIs.
- [ ] Test missing/stale contexts, redaction, bounds and dependency direction.

## 3. Configuration drafts and real-engine simulation

- [ ] Create bounded, expiring drafts for sticky controls, elapsed-time smoothing
      and optional Lua mappings; show an exact diff and retain a reversible original.
- [ ] Bind drafts to the app instance, profile identity/revision and source digest;
      refuse stale changes and keep output/scripts inactive on apply/undo.
- [ ] Run Lua using the actual engine/API and deterministic physical event timelines,
      including instruction budgets, stop/disconnect behavior and manual composition.
- [ ] Isolate script execution in a bounded worker process with timeout/cancellation
      and bounded input/output; do not substitute an invented Lua contract.
- [ ] Test normal holds, termination/errors, stale drafts, cancellation, quota breaches
      and preservation of existing profile settings and runtime arbitration.

## 4. MCP transport and accessible Studio integration

- [ ] Use a pinned official SDK, publish a separate console bridge and support tools,
      resources and prompts with schemas, cancellation and actionable errors.
- [ ] Add bounded, current-user-only IPC to the existing app instance; no duplicate
      hardware monitors, automatic output activation or non-protocol stdout logging.
- [ ] Add Settings controls for time-limited access, draft/offline permissions,
      review/apply/discard/undo, client configuration and immediate revocation.
- [ ] Serialize requests onto the host context; Stop all/close/revoke cancels jobs,
      expires drafts and prevents old requests from changing a new context.
- [ ] Test real SDK client interoperability, framing/size limits, revocation, context
      changes, UI keyboard/ARIA flows and published bridge/worker availability.

## 5. Bounded training workflows

- [ ] Add explicit Studio registration of dataset folders, threshold files, output
      workspace and a trusted existing Python interpreter; MCP receives opaque IDs,
      not arbitrary executable paths, filesystem access or shell commands.
- [ ] Provide stdlib dataset auditing using actual provenance validators and explain
      coverage/gaps without converting guesses into ground truth or changing labels.
- [ ] Wrap the real experiment runner in asynchronous jobs with presets, bounded
      CPU/time/storage/log output, cancellation and immutable source identities.
- [ ] Expose job status and bounded metrics/reports. Preserve reviewed train/validation
      separation, keep held-out data outside tuning, and never promote or arm a model.
- [ ] Test with synthetic data, a fake bounded worker and real audit/plan-only jobs;
      record that these do not establish gameplay accuracy or hardware timing.

## 6. Validation and completion

- [ ] Run shared-service, simulator/worker, SDK/IPC, engine/runtime, dataset/vision and
      frontend checks, browser workflows and production build.
- [ ] Pass Windows publishing, deployment/boundary checks and CI; document client
      setup, permissions, data flow, recovery and actual limitations.
- [ ] Record implementation SHAs and evidence, then move this checklist to `completed`.

## Follow-up designs outside this first release

Guided physical calibration requires normalized per-device sampling and real comfort/
latency trials. Replay capture requires a separate opt-in recorder; LatestFrameBuffer
is not history. HUD narration requires an explicit image-sharing/OCR path. Party
callouts require local TTS, an audio-owner integration and user-controlled broadcast.
Semantic preference transfer requires game-pack action metadata. Training a local
assistant requires opt-in reviewed demonstrations and a separate evaluation corpus.
These are documented expansion directions, not capabilities claimed by this release.
