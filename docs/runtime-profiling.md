# Measure before changing the runtime

Remote Play → Performance runs & diagnostics reports capture readback/copy,
preprocessing, inference plus transfers, frame age and frame-to-decision timing.
The existing CPU path and explicit CUDA selection remain available. No GPU resize
or lock-free output rewrite is introduced by this instrumentation.

Readback uses QPC around staging-texture preparation, D3D copy/map, managed row
copy, sparse fingerprinting and pixel-slot publication. It includes CPU scheduling,
map stalls, cold allocations and buffer-lock waits. It excludes WGC's upstream
delivery, native unmap/release and Xbox rendering. It is not isolated GPU kernel
time. Only processed frame leases contribute samples; capture drops/replacements
are counted separately. Missing readback measurements remain unavailable.

Controller lock measurements cover input polling, vision-state reads, observation
submission and scheduled release. Each path retains at most 256 acquisitions, plus
its total count and actual window span. Wait time runs from the request timestamp
to entry into the existing controller lock, so it includes thread scheduling.
Different paths have different time windows; do not sum their percentiles or infer
that every wait was caused by monitor contention. These measurements do not include
time spent holding the lock or console delivery.

Measurements start/reset with the capture context and freeze when observation ends.
Generation ownership rejects late samples/finish requests from old contexts. Sampling
allocates no objects; percentile work uses bounded copies outside the controller
lock. Existing generation validation, atomic output arbitration and frame leases are
preserved. There is no new lock-free ownership path.

Saved performance format 2 includes optional readback and runtime-lock measurements.
Format 1 remains readable with missing stages unavailable. Cross-format comparisons
require a fresh baseline so instrumentation changes cannot masquerade as improvement.
The existing comparison still requires matching model/settings/profile/camera/crop/
source size, the same assistance state, no training capture and at least 100 model
decisions per run. It describes observed live-run differences; gameplay can differ.

For representative Windows CPU/CUDA evaluation:

1. Use an unchanged model, capture source, resolution, player crop and settings.
   Stop unrelated workloads and record OS/GPU/driver/runtime versions.
2. Leave assistance unarmed while measuring. Warm up the selected backend, collect
   at least 100 decisions, then save a named run and diagnostics. Repeat enough runs
   to expose scheduling variance and record drops, allocations, GC and resource use.
3. Switch only the intended backend and repeat with the same procedure. Compare
   frame-to-decision p95/p99 and deadline behavior alongside individual stages.
4. Inference/transfer wall time remains an aggregate. Use an appropriate ONNX/GPU
   profiler with validated clock/fence interpretation before attributing it to GPU
   kernels versus host/device transfers. CPU launch timings alone cannot do this.
5. Consider GPU preprocessing only after measuring the bottleneck and designing
   explicit D3D/ONNX buffer interoperability. Resize inside ONNX does not remove the
   existing CPU staging copies. Require numerical parity with the canonical v2 CPU
   transform, bounded resources and stop/source-change race coverage before promotion.

Portable checks cover percentile/count integrity, missing stages, zero-allocation
recording, profiler ownership/freeze, readback metadata across reused frame slots,
and old/new run imports. Windows tests use simulated devices for runtime integration.
Actual Windows capture/GPU profiling and input-to-output timing remain outstanding;
synthetic CPU measurements are not a basis for claiming a live optimization.
