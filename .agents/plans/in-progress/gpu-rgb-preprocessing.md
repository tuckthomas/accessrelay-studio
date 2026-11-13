# GPU RGB preprocessing with the existing model contract

Approved: 2026-11-13. Baseline: `6969730`.

Retain the canonical v2 crop/nearest-256/bilinear RGB float normalization and
NTCHW temporal order. Optimize where preparation runs, not the learned input
semantics. Implement an explicit experimental D3D11/CUDA path beside the existing
CPU path. A direct Remote Play client, YUV-native models and new inference
providers are outside this plan. Do not promise an unmeasured latency reduction.

## 1. Delivery and compatibility

- [ ] Push this checklist to GitHub main before implementation.
- [ ] Deliver independently reviewable implementation commits; record full commit
      IDs and software evidence here in subsequent checklist updates.
- [ ] Preserve legacy settings/models, explicit CPU/CUDA provider selection,
      canonical transforms, cancellation, generation ownership and output arbitration.
- [ ] Keep this plan in progress until actual hardware acceptance is complete.

## 2. Shared preprocessing and temporal input boundary

- [ ] Extract reusable, bounded sampling coordinates and validation without
      changing source-index rounding, interpolation order, normalization or layout.
- [ ] Separate temporal tensor preparation from tracking decisions so CPU and GPU
      implement the same history shift/reset, sampling and selected-player freeze policy.
- [ ] Verify gradients, channel order, fractional/boundary crops, normalization,
      non-square dimensions, temporal order and frozen/reacquired player regions.

## 3. Native GPU preparation and capture ownership

- [ ] Implement the actual v2 RGB transform as a D3D11 compute shader, using exact
      CPU-prepared source-index tables and explicit floating-point parity tolerance.
- [ ] Publish bounded owned GPU frame leases instead of downloading every frame;
      protect native context use, source resize/change, replacement and shutdown.
- [ ] Preserve content/freshness checks using small readbacks, with no full-image
      CPU readback in the inference-only GPU path. Keep explicit lazy CPU readback
      for preview/recording and account for it honestly.
- [ ] Test the real shader against the CPU reference on Windows D3D11 WARP;
      WARP is correctness evidence, not GPU hardware or performance evidence.

## 4. CUDA interoperability and inference

- [ ] Match the capture adapter to CUDA device 0, validate capabilities, and register
      owned D3D11 output buffers with the CUDA driver using bounded lifetimes.
- [ ] Maintain chronological model history in CUDA memory and bind its device
      allocation to the existing ONNX Runtime session; do not upload CPU image tensors.
- [ ] Synchronize D3D11/CUDA ownership explicitly; retain resources until consumers
      finish and reject incompatible adapters/drivers with actionable errors.
- [ ] Preserve sampling, freeze rejection, Stop/disconnect/source generations and
      inference cancellation. Keep the existing CPU preprocessing mode available;
      never silently switch an explicitly requested backend.

## 5. Settings, evidence and observability

- [ ] Add explicit CPU/GPU preprocessing selection, with legacy profiles defaulting
      to CPU preprocessing. Explain CUDA requirements and experimental status.
- [ ] Keep GPU preprocessing observation-only until matching real hardware evidence
      is available; existing CPU-preprocessing reports must not authorize this path.
- [ ] Identify the preprocessing path in diagnostics/performance runs and comparison
      compatibility. Distinguish GPU preparation, small synchronization readbacks,
      optional preview/recording copies and inference; report actual measured scopes.
- [ ] Document supported formats, parity, resource ownership, failure/recovery and
      the absence of measured end-to-end speedup claims.

## 6. Software verification and physical acceptance

- [ ] Pass portable preprocessing/tracker/model/settings tests, frontend checks and
      browser coverage for backend selection/observation-only behavior.
- [ ] Pass Windows publishing, compiled boundaries, actual D3D11 WARP shader parity,
      native lease/lifetime tests and existing Windows runtime regression checks.
- [ ] Provide an executable CUDA integration/parity probe and a concrete local
      hardware procedure, with explicit unavailable-device results rather than a pass.
- [ ] On the intended NVIDIA Windows setup, verify D3D11/CUDA registration, actual
      device-bound inference parity, resize/Stop/source changes and bounded cleanup.
- [ ] Measure matched CPU/GPU preprocessing runs, p95/p99 frame-to-decision time,
      transfers, preview/recording overhead and resource use. Retain CPU as the
      default until measurements justify promotion. Record physical evidence here.

## Evidence receipts

Pending implementation. Software checks and hardware measurements will be recorded
separately; cloud/WARP/synthetic checks do not establish console latency or GPU benefit.
