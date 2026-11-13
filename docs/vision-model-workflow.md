# Vision model, recorded data and validation workflow

The application has a C# capture/inference integration. It does **not** include a trained NBA 2K no-meter release model. A model, reviewed gameplay labels, independent accuracy evidence and Remote Play hardware trials remain required. Capture-only observation and dataset recording are useful before those artifacts exist.

Training groups must have disjoint capture session IDs. Evaluation also checks the
model's `training-run.json`: validation may reuse its recorded validation sessions,
but cannot use training sessions; final held-out evaluation excludes both groups.
Both provenance lists must be nonempty, unique, and mutually disjoint. Changing a
labels file's split cannot turn a training session into validation evidence. These
identity checks do not detect duplicated footage assigned a different session ID.
Run their dependency-free regression suite with
`python -S -m unittest discover -s tools/vision -p test_dataset_validation.py -v`.

The live application uses ONNX Runtime in C# with an explicit **CPU** or **CUDA** provider selection. CUDA is the default. Python below is an **offline** training/export/evaluation tool, not a running service used by AccessRelay.

The current evidence recorder adds meter-on supervision and distinct meter-off validation collection, including multiple synchronized image regions and controller trajectories. **Raw enriched clips cannot be passed directly to the trainer/evaluator.** The explicit reviewed converter below supports only player ROIs disjoint from marked meter/feedback overlays, preserves original evidence, and maps matching recorded teacher/student configurations. Unconverted or malformed evidence remains rejected. See [training evidence design](vision-training-evidence-design.md) for the capture format and unfinished learning workflow.

## Choose an installed model

The Configuration tab uses an installed-model dropdown. **No model — capture/record only** is a normal selection: live viewing and recording training clips do not require a model. No trained NBA 2K27 release model is bundled, and diagnostic/random models are not added to this library automatically.

**Import model package…** selects an exported model-package folder containing `manifest.json`, its referenced ONNX weights and any referenced validation report. Import copies the bounded, hash-checked package into the local model library; the manifest remains metadata describing the model, not the neural network itself. Ordinary use does not require typing a JSON filename. Model compatibility and supplied validation evidence remain separate from permission to arm live output.

The supported package uses one ONNX file with embedded weights, as produced by the included exporter (`external_data=False`); external ONNX weight shards are not imported.

## Current runtime contract

- One fixed-shape `float32` input named by the manifest, normally `frames`, with shape `[1,T,3,H,W]` (NTCHW).
- `T` is 2–16 temporal samples, oldest to newest. Their interval is declared in milliseconds. Large gaps reset history; source/session/shot changes reset it too.
- The operator explicitly selects the intended player's normalized region in the source window. The first version uses a fixed region plus model identity confidence. It does not promise automatic full-court player reidentification. Reacquire the player when framing changes. Identity loss during a shot invalidates that attempt.
- Preprocessing identifier: `bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2`. First sample a canonical 256×256 BGRA player crop: each grid pixel maps to `floor((origin + (gridIndex + 0.5) / 256 * extent) * sourceSize)`, clamped inside the source. Then bilinear pixel-center resize that canonical image with edge clamping; convert BGRA to planar RGB; divide by 255; subtract `rgbMean`; divide by `rgbStandardDeviation`. The live processor evaluates the virtual 256×256 grid directly without allocating it. Offline preprocessing uses the same explicit four-neighbor arithmetic, not Pillow antialiasing or an interchangeable framework resize.
- One `float32` output, normally `observation`, shape `[1,5]`: player identity confidence `[0,1]`, shot confidence `[0,1]`, time-to-target milliseconds `[0,maximumPredictionHorizonMs]`, uncertainty milliseconds in that range, and normalized shot phase `[0,1]`.
- Time-to-target is relative to the game state represented by the newest frame, not prediction completion time. The output arbiter accounts for measured local processing and calibrated video/input delay.
- Output uncertainty is a learned estimate that must be checked against observed held-out error. It is not a mathematically guaranteed error bound.
- `VisionModel` reuses `OrtValue` buffers. With CPU preparation, CUDA inference uploads/downloads host buffers and inference timing includes those transfers. Optional experimental GPU preparation binds D3D11/CUDA device memory directly, while the small result returns to CPU; it remains observation-only. See [GPU preparation and measured scopes](gpu-rgb-preprocessing.md).
- `ShotTracker` compares the actual preprocessed player-region pixels with one reusable previous-frame buffer. An unchanged region exceeding `MaximumFrameAgeMs` emits a negative player observation that cancels an existing release deadline, even if browser overlays outside the region keep repainting. Changed player pixels require a fresh temporal history; the controller arbiter still requires physical release/new press after a cancelled attempt. Exact equality is a conservative freeze heuristic, not proof that every changing image is current gameplay.
- Selecting CUDA requires CUDA registration and strict `session.disable_cpu_ep_fallback=1`. Unsupported GPU graphs fail visibly. Selecting CPU is a separate explicit mode and does not initialize CUDA. Capture-only operation remains available without a model. Validation evidence must name the actual selected provider as `CPUExecutionProvider` or `CUDAExecutionProvider`; missing legacy provider metadata cannot arm output.

The native ONNX Runtime 1.31.1 Windows CUDA provider used during implementation imports CUDA **13** cuBLAS DLLs. Use the actual provider's dependency checks rather than older documentation assuming CUDA 12. The GPU Windows NuGet replaces the CPU-native package; do not load two incompatible `onnxruntime.dll` builds in one process. Existing CPU audio inference can use the same GPU runtime's CPU execution provider.

For an experimental publish with scoped NVIDIA redistributables already installed, `scripts/Start-VisionExperiment.ps1 -AppPath <published-exe> -CudaRuntimeRoot <redistributable-directory>` adds DLL directories to the child process's search path. It checks required cuBLAS/cuDNN DLL names and does not modify the global PATH, install a driver or start Python. Installed DLL names alone do not prove load/inference compatibility; the actual CUDA-only warm-up check must pass.

## Record clips and label them

Live viewing reads frames temporarily for preview/analysis. The live-frame counter does not mean those frames were written to disk, and timing-sample exclusions do not mean the video images were discarded. Only explicitly recording a training clip saves gameplay frames. The page labels viewing-only, recording, finalizing, saved and incomplete states separately, including the saved frame count and folder.

Record through AccessRelay's opt-in training controls. Choose the relevant shot profile before recording. Each take is bounded, stored outside the repository, and includes its shot-profile configuration fingerprint. A source change or incomplete write marks a take incomplete. Enriched captures are not accepted by the current trainer even when complete.

In **Plugins**, create and save the player/shot profile, including height, three-point rating, base, both upper releases, four-level shot speed, release blend and meter settings. Release percentages are linked to total 100%; release type is enabled only with the meter on. In **Remote Play Vision**, select the source window and set up the training-capture regions on a fresh live preview. Name the HUD layout and mark the gameplay area, player, meter and shot-feedback region. Browser chrome and letterboxing should be outside the gameplay rectangle; coordinates are normalized to the captured window and confirmation binds the current source size. These are manual regions, not automatic player/meter trackers.

Use **Meter-supervised training** for initial collection with the actual in-game meter and selected profile both on. Mark the meter and feedback regions; the application cannot verify in-game visibility merely from a checked setting. Use the separate **Meter-off validation** purpose with the actual meter and profile both off when collecting no-meter evaluation footage. Optional game-build and shot-category metadata help distinguish current-game conditions, including standing/catch-and-shoot, off-dribble and fades; unknown/mixed values do not establish support for every case.

The persistent **Record training clip…** button chooses a local dataset folder, starts live view automatically if needed, and records up to ten seconds with automatic release inhibited. A manually started recording session skips model loading/warm-up entirely. **Stop recording** finalizes a shorter clip. Capture continues across the manually recorded interval, so leave the clip running long enough to include the post-shot feedback. Feedback is a sequence from the selected region; there is no automatic shot-boundary/feedback parser or inferred teacher label. **Review and label clip…** offers a **Capture region** dropdown with Player, Court, Meter (when recorded) and Shot feedback, all tied to the same frame slider and capture timestamp. Scrub later in the take to inspect post-shot feedback. Reviewed interval labels can still be saved manually; review does not itself convert enriched evidence; use the integrity-bound offline conversion below after labeling. Viewing and recording do not require a model.

A session directory retains `session.json` and `frames.bgra`. Frame metadata identifies a byte offset and size inside the packed BGRA file. The primary images remain nearest-neighbor player-cropped to the canonical 256×256 grid. Enriched sessions additionally identify their capture purpose, layout and auxiliary gameplay/meter/feedback streams in `captureEvidence`, and save `controller-trace.json`. Image regions from an acquired frame share its sequence/capture timestamp; controller polls carry independent timestamps on the same PC QPC clock. See the [evidence contract](vision-training-evidence-design.md) for sizes, bounds and timing limits.

From beta.16, new takes declare `captureTimeBasis="wgc-local-receipt-qpc-v1"`:
`captureTicks` marks local WGC frame receipt before image copies. The unmodified WGC
timestamp is retained separately as `compositorTicks`, which can lead receipt on
some sources. Local runtime timing uses receipt without clamping either clock.
Older takes retain their original compositor-based timing; account for that basis
when reviewing labels and comparing datasets. Neither basis measures Xbox render
time or removes the need for stream/input calibration.

The labeling/training examples accept **legacy player-crop sessions, verified reviewed conversions, and synthetic workflow fixtures**. Their offline preprocessing uses the full stored image and does not apply the source crop twice. Runtime and offline transforms use this same v2 contract. Models declaring the old v1 transform are rejected and require re-export/revalidation. Cross-language gradient/crop parity checks validate these transforms independently of training accuracy. Do not remove an enriched session's marker or rewrite its actual profile to force compatibility.

Add `labels.json` beside `session.json`:

```json
{
  "schemaVersion": 1,
  "sessionId": "COPY-THE-RECORDED-SESSION-ID",
  "labelTimeBasis": "captured-target-game-phase",
  "split": "train",
  "shots": [
    {
      "shotId": "take-001-shot-001",
      "reviewed": true,
      "startTicks": 100000,
      "endTicks": 900000,
      "targetReleaseTicks": 600000,
      "isTargetShot": true,
      "playerIdentityConfirmed": true
    }
  ]
}
```

The numbers above illustrate the format only. Replace them with the take's actual QPC timestamps and reviewed labels. **`targetReleaseTicks` is the capture timestamp of the frame representing the desired Xbox-side target game phase as it appears in the recording.** It is not the physical button-up timestamp or the PC virtual-controller send timestamp. `H` is learned as the interval from an observed game phase to that target game phase; the runtime separately subtracts video age, processing elapsed and input delay. Labeling physical/send time would encode those transport offsets into the target and then compensate them a second time.

Record `labelTimeBasis="captured-target-game-phase"` in new label documents. The loader rejects explicitly incompatible bases; schema-1 legacy labels without this field are accepted only under this documented meaning. Visible ball departure alone does not establish the optimal release target. Reviewed practice experiments and game feedback must justify which game phase is being targeted. Coarse feedback such as “early” or “late” does not supply an exact millisecond target.

Frame labeling has finite temporal resolution: a 60 FPS source provides observations about 16.67 ms apart. A reviewer selecting a captured frame is not providing exact sub-millisecond console ground truth. Record sampling/annotation uncertainty, stream irregularity and reviewer disagreement in the dataset/evaluation procedure, and evaluate the model's uncertainty against that evidence. Hardware timing remains a separate acceptance gate.

Use nonoverlapping reviewed intervals. Endpoints are inclusive, so one interval's end cannot also be the next interval's start. The review dialog rejects overlapping labels on save/load, and the offline loader rejects them before producing training samples. Negative intervals have `isTargetShot=false` and omit `targetReleaseTicks`. Include pump fakes, dribbles, passes, menus, stream artifacts, another player shooting, occlusions and camera movement. `playerIdentityConfirmed=false` labels wrong/unknown-player intervals. Target intervals require confirmed identity.

Assign complete sessions to `train`, `validation`, or `held-out`. Never divide frames from one take across those splits. Keep each training run bound to one exact shot-profile fingerprint. Changes in player height, three-point rating, base, either upper release, shot speed, release blend, meter mode, active meter release type or camera view create a new fingerprint and require a matching model/profile evaluation.

Meter-on teacher collection and meter-off student deployment consequently have different honest profile fingerprints. The reviewed converter records their explicit relationship and preserves both recorded profiles. The student fingerprint comes from an actual meter-off configuration capture; Python does not invent a replacement fingerprint. Distinct meter-off sessions are still required for validation. Meter progression or feedback pixels must not leak into a student that is expected to run without them; merely cropping near the player does not establish that guarantee when the meter overlaps the body.

### Camera provenance

The shot profile records `cameraView` as exactly **`2K`**, **`2K Low`**, or **`2K Legacy`**. Camera changes are part of the shot-profile configuration fingerprint and invalidate prior model/calibration associations. Set the profile to the actual in-game view before recording; metadata cannot independently prove which camera the game rendered.

Training derives the model's `cameraConfiguration` from recorded `session.json` → `shotProfile.cameraView`. It rejects mixed or unsupported camera views, including across the train/validation split. The optional `--camera` argument is only a consistency check and must match the recorded view; it cannot relabel footage. Evaluation requires the dataset camera, model, training provenance and locked-threshold camera to agree.

Legacy application profiles without a camera field default to `2K`. Legacy recorded datasets are different: their original camera cannot be inferred from missing metadata. Offline tools reject missing `shotProfile.cameraView` by default. After reviewing that the old footage actually used `2K`, the explicit `--assume-legacy-camera-2k` option allows that assumption and records the affected session IDs in training/evaluation provenance. It never overrides an explicitly unsupported or conflicting camera value. Older model candidates without camera-bound training provenance need a reviewed re-export/evaluation.

The camera-aware profile fingerprint format is version 3. The legacy-camera assumption flag does **not** migrate an older fingerprint, rewrite its provenance, or make a legacy exported model match a current profile. Reconcile the complete reviewed shot configuration with its current fingerprint and revalidate the data/model association, or record new takes. Merely asserting `2K` cannot unlock automatic release.

## Offline training and export

Use a separate Python environment with PyTorch, NumPy, ONNX and the matching dependencies. CPU training is supported; CUDA training is optional and distinct from production runtime installation. The current exporter explicitly uses the legacy TorchScript ONNX path (`dynamo=False`, opset 17), which PyTorch deprecates but still supports; a future exporter migration needs parity checks.

`tools/vision/requirements-offline.txt` records the package versions used for the synthetic workflow smoke check on Python 3.11. Choose an appropriate PyTorch CPU/CUDA wheel for the offline environment. The successful smoke check establishes tool operation only; synthetic data is not gameplay training or final evaluation evidence. Training and evaluation require fresh output directories and preserve previous model/report files.

```powershell
python ./scripts/Train-VisionModel.py `
  --train C:/VisionData/train-take-01 C:/VisionData/train-take-02 `
  --validation C:/VisionData/validation-take-01 `
  --game-version "NBA 2K27 build being evaluated" `
  --camera "2K" `
  --out C:/VisionModels/candidate-01
```

The script validates take completeness, labels, timestamp order, byte bounds and split separation. It trains a small temporal 3D-convolution model, exports fixed-shape ONNX, runs the ONNX structural checker and writes:

- `animation-model.onnx`;
- `manifest.json`, bound to `nba2k27:<shot-profile fingerprint>`;
- `training-run.json`, including session provenance and validation loss history.

This starter architecture is a functioning training path, not evidence that its capacity/data are sufficient for reliable release timing. Training completion does not prove accuracy. Exported manifests always contain `validation: null` and cannot arm output. Review model/data licenses before redistribution.

The shared loader rejects raw enriched `captureEvidence` sessions, including malformed/null markers. It accepts a verified conversion only after checking review/proof files, all source data hashes, purpose, overlay exclusion and student mapping. This also applies to evaluation because it imports the same loader. The current starter model does not yet learn meter progression, tempo or feedback-derived teacher targets, reuse community detector weights, or accept arbitrary controller traces as labels.

For GPU compatibility checks without training data:

```powershell
python ./scripts/Train-VisionModel.py --diagnostic-fixture `
  --width 64 --height 64 --out C:/VisionModels/untrained-diagnostic
```

This deliberately exports random, **untrained** weights. It is useful for loading, provider, tensor, preprocessing and memory-lifecycle checks only. It cannot produce validation evidence or enable automatic release.

## Independent offline evaluation

Install the Python ONNX Runtime CPU package in the offline environment. Evaluation deliberately uses CPU inference as a numerical reference, independently of the selected live target. It does not certify live CPU or CUDA latency, frame freshness or controller timing.

Choose thresholds from development/validation/calibration results. Save `locked-thresholds.json` with schema 1, `datasetId`, the exact `animationProfile`, matching `cameraConfiguration` (`2K`, `2K Low`, or `2K Legacy`), `executionProvider` (`CPUExecutionProvider` or `CUDAExecutionProvider`), `calibrationProcedureVersion`, `minimumConfidence`, `maximumUncertaintyMs`, `maximumFrameAgeMs`, `meterOff`, `noPlayerIconRequired`, and `lockedBeforeFinalEvaluation`. Set the last flag only after freezing these choices for the final held-out run. Maintain the dated configuration and dataset provenance; the flag alone cannot prove that process was followed.

```powershell
python ./scripts/Evaluate-VisionModel.py `
  --manifest C:/VisionModels/candidate-01/manifest.json `
  --sessions C:/VisionData/heldout-take-01 C:/VisionData/heldout-take-02 `
  --split held-out --locked-thresholds C:/VisionData/locked-thresholds.json `
  --target-provider CUDAExecutionProvider `
  --out C:/VisionEvaluation/final-01
```

The evaluator rejects diagnostic fixtures, model-hash changes, overlapping training/held-out sessions, profile mismatches, overlapping shot intervals and inconsistent source crops. It evaluates the first threshold-accepted prediction per interval and reports target coverage, frame-level identity classification, absolute prediction-error p99, negative-interval false-release rate, and raw per-shot predictions. Inspect raw results and failures; these metrics describe the specified offline policy, not console-side timing or a success guarantee.

`--target-provider` declares the intended live hardware target and must match the locked `executionProvider`; it defaults to `CUDAExecutionProvider`. Use `CPUExecutionProvider` for a CPU deployment. The report records this target, while `evaluation-provenance.json` separately identifies `CPUExecutionProvider` as the offline numerical reference. Changing the declaration does not turn CPU reference measurements into CUDA measurements or validate either live backend.

The generated `accuracy-report.json` always has `hardwareValidated=false`. Offline evaluation cannot certify live frame freshness, local deadlines, Remote Play input acceptance, or when the Xbox applied the release.

## Arming evidence and hardware trials

A deployment manifest's optional `validation` object declares **locked** report thresholds and sample-count requirements, `datasetId`, calibration procedure, report filename and SHA-256. `VisionValidationEvidence` and `VisionValidationReport` in `VisionModelManifest.cs` are the authoritative schema-1 field definitions. The example manifest intentionally omits validation.

Runtime acceptance requires all of the following:

1. Model SHA-256 and report SHA-256 match the files.
2. Model version, profile fingerprint, game/camera configuration, actual selected execution provider, dataset and calibration procedure match. Manifest evidence and report must both explicitly name the same provider. Old reports missing this field remain observation-only.
3. The report is a final held-out evaluation with meter off, no mandatory player icon, locked matching thresholds, sufficient target/negative samples, acceptable coverage, identity accuracy, prediction error and false-release rate.
4. A separately reviewed actual hardware report establishes the virtual-controller route, the configured local-deadline miss rate, sufficient attempts and at least the declared hardware session duration (minimum 30 minutes).
5. The source dimensions and selected player crop match the validated report. Runtime settings cannot weaken its confidence, uncertainty or freshness thresholds.

Merge verified hardware measurements into the report only after actual trials, then recalculate the report hash and update the manifest. Keep the underlying evidence and measurement scope. Do not mark `hardwareValidated=true` merely to unlock the UI. Hashes detect mismatched/changed files; they do not authenticate the truth of self-authored accuracy claims or replace human review.

Treat CPU and CUDA as separate deployment contexts. Switching the UI backend must not inherit the other provider's live hardware acceptance; collect and bind matching evidence before arming again. Numerical equivalence on offline clips is useful but does not prove equal live latency or jitter.

Calibration helper `VisionCalibration.Estimate` accepts reviewed **measured signed millisecond errors**, where positive means late. It proposes `newCorrection = previousCorrection - medianError` and reports residual p95. Five samples produce only a provisional estimate, not a release gate. Coarse early/late labels are rejected. Bind saved calibration to manifest hash, source size/crop, animation, game/camera and procedure; invalidate after those change.

In the UI, observe the selected source with the candidate manifest, then stop observation and use **Export calibration template**. The JSON envelope contains `schemaVersion`, `modelManifestHash`, `animationProfile`, `gameVersion`, `cameraConfiguration`, `executionProvider`, `captureWidth`, `captureHeight`, `crop`, `previousCorrectionMs`, `estimatedVideoDelayMs`, `estimatedInputDelayMs`, `procedureVersion` and `attempts`. Fill in the actual measurement procedure and reviewed attempts such as `{ "shotId": "unique-id", "reviewed": true, "hasMeasuredSignedError": true, "signedReleaseErrorMs": 12 }`. **Review measured calibration** rejects mismatched context, including a missing or different provider, and requires confirmation of the resulting estimate before saving. Raw unbound attempt arrays are not accepted. Changing the source, model, provider, crop, shoot control or delay settings clears the previous correction; manually entered correction values are tuning settings, not validated accuracy evidence.

## In-app performance measurements

Start observation to populate the **Live vision performance** panel. It shows the actual inference provider and model context, processed frames/rate, model decisions/inferences, replaced capture frames, automated releases and missed deadlines. Capture-only mode can measure local capture/processing; model-decision and inference statistics remain unavailable without their respective samples. A stale/frozen-region policy rejection that runs no inference counts only as frame processing, not a model decision or a sample toward the 100-decision comparison gate.

Five timing rows report samples, latest, p50, p95, p99 and maximum milliseconds over bounded recent windows of up to 2,048 samples: local frame age when processing starts, frame to model decision, processing of all frames, player preprocessing, and inference including transfers. Frame age begins at the PC's capture timestamp, not the Xbox render time. Budget exceedances use the same recent decision window. Worker allocated bytes and observed process GC counts help investigate jitter; collection counts do not measure GC pause lengths. Helper output handoff remains a latest queue-to-pipe-flush estimate with no percentile sample history, and does not establish Xbox receipt.

Use **Save performance run** to write a local JSON snapshot with configuration context, and **Load performance run** to select a baseline. Comparisons require at least 100 model decisions in each run and matching non-provider context; selecting a different backend is allowed for comparison. Preserve the same model, source/crop, shot profile, camera and timing settings. Different live scenes remain uncontrolled inputs, so reported p95/p99 changes are observed differences, not a controlled benchmark or gameplay-accuracy result. Capture-only snapshots cannot establish model performance. Raw gameplay frames are not part of a performance snapshot.

The planned gamertag-assisted moving crop is not implemented by these metrics. Current inference and training use the manually selected fixed crop. A future camera-aware tracker needs resolution/scale-relative body regions, temporal stabilization, identity-loss cancellation and matching per-frame dataset provenance before its performance or accuracy can be compared fairly.

## Verification

Portable model/preprocessing checks do not require a GPU or native ORT session:

```powershell
dotnet run --project ./tools/vision/Verification/VisionModelVerification.csproj -c Release
python -m unittest discover -s tools/vision -p test_dataset_validation.py -v
```

They cover channel/crop transforms, invalid outputs, arming evidence integrity, temporal history/tokens, calibration sign and bounded diagnostics. Actual CUDA load/inference and Windows capture still require separate hardware validation. Missing data, labels, trained weights and hardware evidence must remain visibly pending.

Export an irregular gradient/noninteger player-crop fixture from the actual C# transform and compare its values with the offline NumPy transform:

```powershell
dotnet run --project ./tools/vision/Verification/VisionModelVerification.csproj -c Release -- `
  --preprocessing-fixture C:/VisionChecks/preprocessing-v2
python ./scripts/Check-VisionPreprocessing.py C:/VisionChecks/preprocessing-v2
```

The C# check also compares live-source processing with explicitly recorded canonical 256×256 BGRA data. This detects crop-coordinate, channel-order, interpolation, normalization and antialiasing differences before training. It establishes transform parity, not release prediction accuracy.
# Footage independence

Training now fingerprints canonical player RGB frames and 2–4-frame windows (alpha
and claimed timestamps are ignored). It rejects reused footage across train/validation,
including renamed sessions, overlapping/reversed clips and resampling that preserves
enough original frames. Repeated windows under different IDs within one group also
fail. Exact static/loading frames can trigger this conservative check; review and
trim noninformative footage rather than relabeling it to bypass the guard.

Training provenance records separate training/validation footage indexes. Validation
evaluation checks the training index; final held-out evaluation checks both indexes
and excludes configuration-reference sessions. Older candidates lacking these indexes
must be re-exported from audited sources before producing current validation reports.
The bounded stdlib-only index permits 50,000 frame/window hashes per group.

These are exact RGB reuse checks, not proof of statistical independence. Re-encoding,
different crops, overlays and other transformations can evade exact hashes. Human
source-footage review and independently collected held-out sessions remain required.

## Reviewed evidence conversion

This workflow performs **no automatic teacher labeling or accuracy certification**.
First review the original synchronized clip in the app and save explicit target and
negative intervals. Target timestamps identify captured game phase, independently
of physical releases or virtual output. Meter-on supervised clips remain training
only; meter-off validation/held-out clips must actually have been recorded meter-off.

Use a separately recorded, predeclared meter-off configuration capture as the student
reference for teacher training. It must match camera, animation fields, source size,
player crop and explicit game build; only meter-specific fields may differ. Do not
use final held-out footage as that configuration reference. Footage provenance later
excludes configuration-reference IDs from evaluation. For an actual meter-off
validation/held-out take, the take itself may be its reference.

```powershell
python games/nba2k27/training/Convert-VisionEvidence.py `
  --source datasets/teacher-take --student-reference datasets/student-configuration `
  --write-review-template datasets/teacher-review.json
```

The template binds source metadata, labels, student metadata and every original
visual/controller file by SHA-256. Its confirmation flags start **false**. Review the
actual clip and configuration, fill reviewer/UTC date, and confirm the no-overlay ROI,
independent target-phase labeling and teacher/student animation equivalence only
when supported by that review. Changed data or labels require a new review template.

```powershell
python games/nba2k27/training/Convert-VisionEvidence.py `
  --source datasets/teacher-take --student-reference datasets/student-configuration `
  --review datasets/teacher-review.json --out datasets/converted-teacher-take
```

The output is a fresh folder outside the originals. It retains `captureEvidence`,
original session/labels, student-reference metadata, reviewed proof, every auxiliary
stream and optional controller trace. It preserves the original source session ID;
only the explicit model configuration maps to the student's recorded fingerprint.
`trainingConversion.validationPassed` remains false. The trainer/evaluator verify
all proof/data hashes, source purpose, geometry, offsets, source generations and
reviewed labels before consuming player frames. Edits require explicit re-conversion.

The player ROI must avoid both meter and feedback regions with a one-source-pixel
margin. The converter rejects overlaps; it does not silently mask pixels or change
the versioned runtime preprocessing contract. Re-record unsuitable crops. Incorrectly
marked overlays can still undermine a human review, so the rules do not establish
real pixel exclusion or label accuracy without inspecting actual footage. The
current verification uses synthetic streams only; reviewed gameplay and physical
Windows model/timing acceptance remain open.
