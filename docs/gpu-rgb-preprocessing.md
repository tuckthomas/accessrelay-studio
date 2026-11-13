# Experimental GPU image preparation

Studio can prepare the existing v2 RGB model input on D3D11 and pass chronological
history directly to CUDA inference. CPU preparation remains the compatible default.
This implementation does not establish a speedup, gameplay accuracy or physical
timing acceptance. GPU preparation is observation-only in both the model-evidence
check and the host controller arbiter. Existing reports cannot authorize it.

## Select the path

In **Live Gameplay → NBA plugin → Analysis settings & measured calibration**, choose
NVIDIA GPU/CUDA inference and **Image preparation → GPU · experimental**, then save
while capture is stopped. Selecting CPU inference explicitly selects CPU preparation.
Legacy settings with no preparation field continue using CPU. JSON stores this
selection as `preprocessingBackend: "d3D11Cuda"`; `"cpu"` selects the existing path.

Start analysis with an already compatible ONNX model. No model, output, capture or
controller activation happens when changing/saving the setting. Unsupported drivers,
adapters and interop operations produce errors; Studio never silently selects CPU
preparation or another inference provider. Select CPU preparation and restart to
recover. Missing/incompatible models remain explicitly capture-only or stopped.

The capture device is selected to match NVIDIA CUDA device 0. On a hybrid-GPU PC,
Windows may copy between a different display/decoder adapter and NVIDIA; measure
that cost. This path currently receives WGC **BGRA8** frames. It does not add a
Remote Play client, direct stream decoding, NV12/YUV input or a new model format.

## Preserved RGB contract and ownership

The model's manifest, ONNX weights, shapes, temporal order and preprocessing ID stay
unchanged. Double-precision CPU coordinate tables reproduce the canonical nearest
256 crop and pixel-center bilinear source indices. The shader performs interpolation,
RGB unit scaling, mean subtraction and standard-deviation division with strict
floating-point operations. Verification tolerance is `2e-6 + 2e-6 * abs(reference)`;
this is numerical compatibility, not gameplay validation. CPU sampling tables are
also checked bit-for-bit against the untouched canonical implementation.

WGC frames are copied into three bounded owned GPU textures. A leased texture cannot
be replaced; slow consumers cause drops. Capture resize/source generations reset
history. Device references survive capture shutdown until the last consumer releases
them. One native context lock serializes capture, shader and lazy readback operations.
It does not hold controller ownership or UI/Stop state locks while waiting on D3D.

Two reusable D3D11 RGB buffers alternate, with exact per-element comparison against
the previous prepared player region. Animated pixels outside the crop cannot conceal
a frozen player. A separate sparse full-window FNV fingerprint preserves the existing
source-freeze hint. Only **16 bytes** of metadata are downloaded per prepared sample.

The NVIDIA driver registers those RGB buffers. Mapping transfers ownership from
D3D11 to a dedicated CUDA stream; device-to-device copies update chronological model
history. Overlap-safe shifts use a separate bounded scratch allocation. Unmapping
and stream synchronization finish before ORT consumes the input. ORT I/O binding
references the CUDA allocation directly; the five-value model result returns to the
CPU. External allocations outlive the native inference session. Stop remains
cooperative: software cannot impose a hard deadline on a stalled driver/kernel.

## Image downloads and measurements

Inference-only GPU preparation does not download full images or upload CPU image
tensors. Preview and training recording still need CPU images. Their downloads are
lazy, cached once per leased frame and reported separately. A visible preview can
request one every 200 ms; recording requests images according to its capture policy.
Hide/minimize the main and detached preview windows, or leave capture pages, to
measure the inference-only case. This does not stop observation.

Performance format 3 identifies inference and preparation backends and records:

- Preparation wall time, including dispatch, small metadata readback/synchronization
  and CUDA history transfers; this is not isolated GPU kernel time.
- Optional preview/recording image-readback latency and bytes in the bounded window.
- Freshness metadata bytes separately from image downloads. CPU capture readback is
  unavailable for the GPU capture path, rather than a manufactured zero sample.

Preview download happens after the model decision and can delay processing of the
next frame. Recording download happens before the decision. These scopes overlap
other stages; do not add percentile values together. Formats 1/2 remain readable
with unavailable new stages. Cross-format comparisons require fresh baselines.
Matching format-3 runs may compare preparation/provider choices while retaining
model, camera, crop, source dimensions, timing settings, assistance and recording
compatibility. Gameplay can differ between runs; differences are descriptive.

## Run the NVIDIA integration probe

From a checkout with the .NET 10 SDK, use a complete published app (including its
matching ONNX Runtime and optional CUDA redistributables) and a compatible manifest:

```powershell
./scripts/Test-GpuPreprocessing.ps1 `
  -AppDirectory 'D:/AccessRelayStudio/Run' `
  -ModelManifest 'D:/VisionModels/example/manifest.json' `
  -OutFile 'D:/VisionChecks/gpu-preparation-probe.json'
```

The output parent directory must exist; existing evidence is never overwritten.
The executable checks actual NVIDIA registration/map/unmap, GPU temporal history,
resize/reacquisition, numerical tensor parity and paired device-bound ONNX inference.
It uses uploaded synthetic source images and explicit verification tensor downloads.
It creates no Studio runtime, physical controller output or capture window. It never
generates model validation, arming or promotion certificates.

Exit codes: **0** synthetic tensor/inference parity passed; **1** failed; **2** device/
platform capability unavailable; **3** tensor checks only, model inference not requested.
Omitting `-ModelManifest` produces the partial result. Missing CUDA on a cloud/WARP
runner is explicitly unavailable, never a passing NVIDIA test.

## Hardware acceptance still required

Retain probe output with app/model hashes and Windows/GPU/driver versions. Then run
matched, warmed CPU- and GPU-preparation observations on the intended capture source,
same model/crop/settings, with automatic release off. Collect at least 100 actual
model decisions per run, repeat runs, and compare p95/p99 frame-to-decision timing,
replacements, preparation/inference, memory and readbacks. Repeat with preview on/off
and recording separately; recording runs do not qualify for normal performance
comparison. Exercise Stop, source close/minimize/restore, resize and restart. Verify
cleanup and unavailable-device errors. Real gameplay and independent model accuracy
remain separate acceptance requirements. Keep CPU as default until evidence supports
a promotion decision; this release provides no GPU arming override.
