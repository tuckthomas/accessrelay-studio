# GPU RGB preprocessing with the existing model contract

Approved: 2026-11-13. Baseline: `6969730064e7f547467b40fca36c2aed1fa24f6b`.

Retain the canonical v2 crop/nearest-256/bilinear RGB float normalization and
NTCHW temporal order. Optimize where preparation runs, not the learned input
semantics. Implement an explicit experimental D3D11/CUDA path beside the existing
CPU path. A direct Remote Play client, YUV-native models and new inference
providers are outside this plan. Do not promise an unmeasured latency reduction.

Software implementation complete; actual NVIDIA hardware acceptance remains open.
Final tested code: `d9dd7fb958ef9ab3147d1f868411d95b77388270`.

## 1. Delivery and compatibility

- [x] Push this checklist to GitHub main before implementation.
      Plan commit: `f3a26aa916441e1edfbe1a8021ba4e6c40a89499`.
- [x] Deliver independently reviewable implementation commits; record full commit
      IDs and software evidence here in subsequent checklist updates.
- [x] Preserve legacy settings/models, explicit CPU/CUDA provider selection,
      canonical transforms, cancellation, generation ownership and output arbitration.
- [x] Keep this plan in progress until actual hardware acceptance is complete.

## 2. Shared preprocessing and temporal input boundary

- [x] Extract reusable, bounded sampling coordinates and validation without
      changing source-index rounding, interpolation order, normalization or layout.
- [x] Separate temporal tensor preparation from tracking decisions so CPU and GPU
      implement the same history shift/reset, sampling and selected-player freeze policy.
- [x] Verify gradients, channel order, fractional/boundary crops, normalization,
      non-square dimensions, temporal order and frozen/reacquired player regions.

## 3. Native GPU preparation and capture ownership

- [x] Implement the actual v2 RGB transform as a D3D11 compute shader, using exact
      CPU-prepared source-index tables and explicit floating-point parity tolerance.
- [x] Publish bounded owned GPU frame leases instead of downloading every frame;
      protect native context use, source resize/change, replacement and shutdown.
- [x] Preserve content/freshness checks using small readbacks, with no full-image
      CPU readback in the inference-only GPU path. Keep explicit lazy CPU readback
      for preview/recording and account for it honestly.
- [x] Test the real shader against the CPU reference on Windows D3D11 WARP;
      WARP is correctness evidence, not GPU hardware or performance evidence.

## 4. CUDA interoperability and inference

- [x] Match the capture adapter to CUDA device 0, validate capabilities, and register
      owned D3D11 output buffers with the CUDA driver using bounded lifetimes.
- [x] Maintain chronological model history in CUDA memory and bind its device
      allocation to the existing ONNX Runtime session; do not upload CPU image tensors.
- [x] Synchronize D3D11/CUDA ownership explicitly; retain resources until consumers
      finish and reject incompatible adapters/drivers with actionable errors.
- [x] Preserve sampling, freeze rejection, Stop/disconnect/source generations and
      inference cancellation. Keep the existing CPU preprocessing mode available;
      never silently switch an explicitly requested backend.

## 5. Settings, evidence and observability

- [x] Add explicit CPU/GPU preprocessing selection, with legacy profiles defaulting
      to CPU preprocessing. Explain CUDA requirements and experimental status.
- [x] Keep GPU preprocessing observation-only until matching real hardware evidence
      is available; existing CPU-preprocessing reports must not authorize this path.
- [x] Identify the preprocessing path in diagnostics/performance runs and comparison
      compatibility. Distinguish GPU preparation, small synchronization readbacks,
      optional preview/recording copies and inference; report actual measured scopes.
- [x] Document supported formats, parity, resource ownership, failure/recovery and
      the absence of measured end-to-end speedup claims.

## 6. Software verification and physical acceptance

- [x] Pass portable preprocessing/tracker/model/settings tests, frontend checks and
      browser coverage for backend selection/observation-only behavior.
- [x] Pass Windows publishing, compiled boundaries, actual D3D11 WARP shader parity,
      native lease/lifetime tests and existing Windows runtime regression checks.
- [x] Provide an executable CUDA integration/parity probe and a concrete local
      hardware procedure, with explicit unavailable-device results rather than a pass.
- [ ] On the intended NVIDIA Windows setup, verify D3D11/CUDA registration, actual
      device-bound inference parity, resize/Stop/source changes and bounded cleanup.
- [ ] Measure matched CPU/GPU preprocessing runs, p95/p99 frame-to-decision time,
      transfers, preview/recording overhead and resource use. Retain CPU as the
      default until measurements justify promotion. Record physical evidence here.

## Evidence receipts

Shared sampling/temporal-input implementation:
`5fd615ad61fac685ba91d7caa0f142bff798a52f`. All 11 managed vision/model/performance
suites passed. New checks compare 144 source/target/crop combinations bit-for-bit
with the untouched canonical v2 reference, plus chronological history shift,
outside-ROI changes, content reset and frozen-region reacquisition.

Native shader implementation: `410bddc65698fb3ec6b9e41ea3d761ad53d4d25d` with SM5 arithmetic/capability follow-up
`df0fbd8cd9fa79cfca658eca6261f925ddde6fa3`. Actual Windows D3D11 WARP shader
execution passed all 18 gradient/crop/shape/normalization cases, source fingerprints
and player freeze/reset checks:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43442189645.
This validates actual shader arithmetic, not NVIDIA interop or GPU performance.

Owned GPU capture/lease implementation:
`d9e87f0bc9a88f9d673cc80723bd4370892e3166`. Actual WARP checks passed for no
automatic image download, outstanding consumer protection, pending replacement,
resize/source generations, lazy CPU readback, capture shutdown and final device
reference release. Full Windows publish/regression and focused native checks passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43443126713 and
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43443126621.

CUDA device-bound input and observation-only host/model gate implementation:
`0569cf7d805b63436f1ed803c696dbaca46a6de2`. Portable model/settings/arming checks
and warning-free Windows compilation passed. Full Windows CI and WARP regression
passed: https://github.com/tuckthomas/accessrelay-studio/actions/runs/43443397548.
These checks do not exercise NVIDIA calls; actual registration, tensor and
inference parity remain the physical acceptance items in section 6.

Backend controls and performance format 3:
`cf759bb0c8c2d65c25a66339508aeca1c24a0799`. All 11 managed vision/model/performance
suites, 84 frontend unit checks and TypeScript/Vite build passed. Browser coverage
verified all 24 workflows (23 in the final broad run plus the corrected profile-context
scanner case in the following five-case scanner run). New checks cover explicit
selection, disabled automatic release, CPU compatibility, unsaved drafts, bounded
readback accounting, cross-backend comparison and legacy performance formats 1/2.

The broad browser run exposed a pre-existing UI-cleanup regression: switch access
was no longer mounted, and shared dropdowns no longer supported its editing controls.
`39feafd39e2b843f71dd39f03cb07e1181f6c12c` restores persistent keyboard/controller
scanning with a Settings launcher, shared choice editing and grouped recorded/Lua
options, and updates retired-control assertions without restoring navbar controls.

The executable probe reports explicit unavailable status on Linux (exit 2), not a
passing CUDA result. The local Windows procedure and actual limitations are in
[docs/gpu-rgb-preprocessing.md](../../../docs/gpu-rgb-preprocessing.md). Final
probe source and complete Windows evidence are recorded below.

Probe, resource-lifetime/error follow-ups and validation guide:
`0e869884ae1db7ccc07d731a335e3a7a887a96a9`; complete GPU capture-drop accounting:
`d9dd7fb958ef9ab3147d1f868411d95b77388270`. The probe and PowerShell wrapper both
returned unavailable/exit 2 on Linux; the executable refused an existing evidence
file with exit 1 and preserved its bytes. Windows cross-compilation passed with
zero warnings/errors. The native probe can test actual NVIDIA tensor registration,
chronological history, resize/reacquisition and paired ORT inference when run with
the target GPU and a matching model/runtime. No NVIDIA pass is inferred here.

Final complete Windows publishing/boundaries/deployment, controller/profile, actual
SDK, dataset, desktop evidence, vision/performance, physical probe build, Pending
acceptance template, simulated runtime and native-helper checks passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43448388668.

Final actual D3D11 WARP shader/ownership suite and explicit CUDA capability reporting
passed: https://github.com/tuckthomas/accessrelay-studio/actions/runs/43448389813.
The suite also rejects nonfinite GPU normalization before inference. Its CUDA result
is **unavailable**, not NVIDIA integration acceptance. Both workflows tested
`d9dd7fb958ef9ab3147d1f868411d95b77388270`. The remaining unchecked requirements are
the two real hardware/parity/performance items in section 6. GPU mode remains
experimental/observation-only and CPU preparation remains the default.

Software checks and hardware measurements are recorded separately;
cloud/WARP/synthetic checks do not establish console latency or GPU benefit.

### Local RTX 2070 check — 2026-11-13

The actual Windows NVIDIA probe passed on an RTX 2070 (8 GB), driver 610.88,
using source `330c65b` and the installed ONNX Runtime 1.31.1 CUDA binaries.
Sixteen synthetic frames passed D3D11/CUDA registration, chronological history,
resize/reacquisition and cleanup; maximum tensor error was
`2.384185791015625e-7`. Preparation downloaded 16 bytes of freshness metadata per
sample and no CPU image.

Paired CUDA inference passed five decisions with maximum output difference zero.
The model was exported by the existing `--diagnostic-fixture` path (160×160,
four frames, global-pool/residual, seed 20261111); its SHA-256 is
`a3d4e7f167cdaad318ed964a2dd513edb8acc6cdf2405f68e00270e5fa4efed2`.
It remains untrained, outside the installed model library, with `validation=null`.

Raw reports and environment/binary identities are retained locally under
`artifacts/GpuHardwareEvidence/` in the original project folder. The complete
hardware items above remain open: this probe uses CPU-uploaded synthetic images;
live WGC source/Stop/restart behavior and matched gameplay latency/resource runs
still require observation on the intended source. No release gate or default is
changed by this result. Beta.15 packages the current GPU controls for that work.
