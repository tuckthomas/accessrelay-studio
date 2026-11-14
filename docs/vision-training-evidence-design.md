# NBA 2K27 training evidence and capture design

Current implementation (beta.18): the NBA plugin configures moving player/meter
anchors, four court-floor points and fixed feedback. Version-3 evidence records
per-frame tracked crops/provenance; tracking loss stops the take incomplete.
The plugin's offline workflow can train/export/evaluate explicitly reviewed data.
This is a calibrated appearance/floor-feature prototype, not a trained pose or
meter-fill detector. Gameplay acquisition, user-model training and hardware
acceptance remain open. See
[the current implementation plan](../.agents/plans/in-progress/training-plugin-and-player-tracking.md).
The original design notes below describe the earlier fixed-region acquisition stage.


- Updated: 2026-11-11
- Branch: `codex/remote-play-vision`
- Scope: synchronized evidence acquisition; model training and gameplay validation remain pending

## Purpose and current limits

Collect current-game examples from which a release model can later be trained and evaluated. Older NBA 2K projects provide useful acquisition and measurement ideas, but their weights, camera assumptions, HUD graphics and timing constants do not establish NBA 2K27 accuracy. The final no-meter objective remains unchanged.

Initial supervised collection requires the shot meter and shot feedback to be visible. Meter progression, its target geometry and post-shot feedback are candidate teacher evidence. Separate meter-off collection supplies validation footage representative of deployment. Recording does not train a model, automatically identify a correct release, or make an unvalidated model eligible for output.

The enriched recorder retains the original player-crop format and adds a `captureEvidence` envelope. The existing offline trainer/evaluator deliberately rejects any session containing that marker, including null or unknown versions. Reviewed conversion, a leakage-safe student transform and explicit teacher/student profile mapping must be implemented before these clips can produce a release model. The review dialog now lets the reviewer inspect the recorded image regions together on one timeline; it does not automatically extract teacher labels.

## Evidence to retain together

One source-frame lease supplies all image regions. This gives the regions the same source sequence and capture timestamp, avoids a second independent window capture, and keeps the controller/output path separate from image processing and disk writes.

| Evidence | Stored BGRA dimensions | Purpose and limits |
| --- | --- | --- |
| Primary player region | 256 × 256 | Canonical player-crop transform retained for compatibility; manual selection, not automatic tracking. |
| Gameplay context | 640 × 360 | Wider scene for identity/context review and future detector/ROI work. Downsampling can lose small text or fine finger/ball detail; this is not a full-resolution archival video. |
| Shot meter | 256 × 256, supervised clips only | Progression, target region and available tempo cue. The rectangular crop is manually selected and resized; raw pixels are not automatically converted into progress/target labels. |
| Shot feedback | 640 × 192 | Sequence covering available timing/tempo/coverage feedback during the recorded interval. The recorder does not parse it or guarantee that feedback appeared before the clip ended. |
| Controller trace | Timestamped structured samples | Physical state and requested processed output from the local polling/output path; preserves button/trigger/stick values rather than a rendered Parameters screenshot. |

The primary `frames.bgra` and schema-1 session fields remain readable for inspection. `captureEvidence` describes purpose, confirmed layout and stream metadata. Auxiliary files are `scene.bgra`, `meter.bgra` (supervised collection only) and `feedback.bgra`; the envelope also declares the primary `player` stream. Each common frame record retains its source sequence and capture ticks, plus an `evidence` array with the auxiliary region name, file, byte offset and length. All regions selected for a frame therefore share its recorded source timestamp. Auxiliary streams must not be reconstructed by assuming that array indices are a console frame clock. Source capture/queue drops are possible, and actual metadata is authoritative.

In **Review and label clip**, the **Capture region** selector offers Player, Court, Meter when available, and Shot feedback. Changing region preserves the current frame index; the shared slider can advance into the post-shot sequence. The reader validates supported envelope version/purpose, unique stream names, allowlisted filenames/dimensions/stride, crop bounds, common sequences, per-frame region counts/offsets/lengths and the total visual bound. Path containment and file redirect checks apply to every region. Only the selected stream is opened/read, with one reusable current-region pixel buffer. Legacy takes keep primary-only review.

Collection is opt-in, bounded to ten seconds, with a queue capacity of eight frame bundles and a two-GiB recording limit. Files remain outside the repository. A dropped frame bundle drops its synchronized regions together; recording cannot wait for disk I/O on the controller thread. An unavailable source, resize/layout mismatch, storage failure or other interruption must remain visible and must not silently certify a complete usable take. These bounds protect acquisition resources; they do not prove that a slow disk can record every source frame.

## Confirm the source and HUD layout

Before recording, confirm a fresh preview and mark:

1. The gameplay content rectangle, excluding browser chrome, borders and letterboxing.
2. The intended player's region with sufficient hands, ball and body context.
3. The shot meter region for supervised collection.
4. The shot-feedback region, based on the actual HUD placement.

The layout stores normalized coordinates against the captured window and the confirmed source width/height. At 2560 × 1440 or another resolution, these are source-relative regions rather than an assumed 160-pixel screen box. The canonical stored dimensions above are resampling sizes, not fixed on-screen body sizes. Reconfirm the layout after a source-size change. A camera name does not prove that the player/meter remains inside a fixed rectangle during gameplay.

Save a HUD layout name, collection purpose, optional game-build description and shot category. The actual saved shot profile already supplies player height, three-point rating, base, both upper releases, release blend, shot speed, camera view, meter visibility and applicable release type. Optional unknown/mixed descriptions stay unknown; they must not become asserted per-shot classifications.

Supervised recording requires a meter-on profile plus meter/feedback regions. Meter-off validation requires a meter-off profile and feedback region. UI confirmation records what the operator selected; it does not independently inspect the game settings. A named HUD profile is local capture configuration, not a promise that the game offers a particular placement preset.

Manual regions are an initial evidence-collection tool. A permanent fixed crop may miss a moving player, meter or defender. A future tracker should acquire candidates broadly, track the selected body/ball and meter with padded regions, and reacquire when confidence falls or crop-edge contact indicates clipping. That future work requires per-frame transforms, confidence and identity provenance.

## Controller trajectory and timing meaning

`controller-trace.json` records physical input alongside the requested processed virtual-controller state for polling and deadline-triggered submissions. It uses the existing controller worker's nominal four-ms cadence, approximately 250 Hz. Actual QPC timestamps and recorded drops determine observed sampling; nominal frequency is not a hard guarantee. The trace is bounded and appended without per-sample file I/O. Lifecycle `Neutralize` calls do not return an acceptance result and are not represented as accepted submissions by this trace.

Retain sample time, before/after local submission times, connection/version state, the output-availability/acceptance result and the original controller values. A successful local output call may retain an already queued identical report. It does not necessarily enqueue a fresh report, and it does not establish helper consumption, Remote Play acceptance or Xbox receipt. Input and video are independently delayed observations on the PC timeline; equal local timestamps do not mean they represent the same Xbox instant.

These stored numeric samples are the actual raw/processed values observed during the clip. The Parameters display can instead show a generated profile curve or a separate macro recording; neither is evidence of the actual gameplay trajectory. The training trace is neither that chart's drawing points nor every electrical report generated by the physical controller. If output is unavailable, the trace must preserve that fact rather than label a requested state as a console-applied movement.

Raw traces stay unchanged. If a future experiment smooths a trajectory or meter estimate, retain a separate derived series with its method/version and quantify any timing/phase delay. No live random jitter is added by this design. Gaussian noise is a possible declared offline robustness perturbation; it does not supply human ground truth, prove green accuracy or establish that behavior will evade detection. Gaussian splatting refers to rendering, not a validated stick-timing method.

## Turn evidence into a model without leaking the answer

Meter-on supervision and meter-off prediction serve different roles:

- The teacher/annotation stage may read meter progression, tempo cue and post-shot feedback.
- A no-meter deployment model may only use evidence genuinely available before its release decision. It must not read post-shot feedback or hidden meter pixels during training/evaluation.
- If the meter overlaps the body, a manually cropped player region can still contain the answer. A reviewed transform/masking policy, paired no-meter examples where appropriate and actual meter-off evaluation are required before declaring the student independent of the meter.
- Meter-on and meter-off sessions must not be assumed to have identical gameplay behavior or scoring tolerances. Masking HUD pixels in a meter-on recording is not a substitute for actual meter-off held-out attempts; evaluate the deployed setting and preserve any observed mode differences.
- Meter-on and meter-off shot profiles have different honest fingerprints. Preserve both source configurations and record an explicit reviewed teacher-to-student mapping. Editing fingerprints or removing `captureEvidence` is not a migration.

Measure progression over a temporal sequence; do not assume every shot uses one linear fill rate. Record distinct timing/tempo cues, meter visibility, camera motion, source duplicates/gaps and detection confidence. Use context and labeled shot families to evaluate standing/catch-and-shoot, off-dribble, fades and other supported categories separately. A contest label obtained after release is teacher evidence, not a prospective input unless an independent pre-release estimate exists.

Record feedback until readable/stable or a declared timeout, while preserving raw frames and ambiguous cases. The initial recorder captures it only during the manually controlled ten-second interval; automatic post-shot extension and a stable-feedback detector are future work. Associate feedback with a reviewed shot identity so another player's shot or stale HUD text cannot silently label the attempt.

Early/late/green feedback is useful but does not specify an exact optimal release timestamp. A made basket alone is also insufficient. Labels require reviewed current-game evidence and a declared time basis; visible ball departure, physical release, local output submission and the displayed target phase are different events. Keep source-time uncertainty and stream/input latency separate from model prediction error.

Split by complete session/clip, with no related frames or overlapping attempts across development and held-out sets. Hold out real meter-off sessions, supported camera views/resolutions and representative shot/contest conditions. Keep game patches and build changes explicit. A final model must pass independent accuracy and hardware/controller timing gates; neither a trained detector nor a fast inference benchmark completes this work.

## Lessons checked against primary sources

| Source | What can be learned | What must not be assumed |
| --- | --- | --- |
| [NBA 2K27 gameplay announcement](https://newsroom.2k.com/news/nbar-2k27-gameplay-unleashes-complete-on-court-control-in-a-balanced-two-way-era) | Current animation/contest changes; Rhythm Shooting has a Tempo tick synchronized with Pro Stick while Timing must green. | A universal fill-rate formula, exact timing window, or current HUD placement coordinates. |
| [eendor meter runtime](https://github.com/eendor/nba2k26-shot-meter-holder-autoshoot1key/blob/main/k_meter/k2_runtime.py) | Wide acquisition, narrow padded tracking, edge/missing-frame reacquisition and meter-relative fill geometry. Post-release settling is treated separately from the live decision. | Its older-title hue/pixel thresholds, fixed latency, render-rate prior or reported timing accuracy applying to this pipeline. Its no-meter fallback is a fixed timer. |
| [eendor meter detector](https://github.com/eendor/nba2k26-shot-meter-holder-autoshoot1key/blob/main/k_meter/k2.py) | Merge occluded components; distinguish target window/outline; evaluate discrete rendered progression and sampling bias. | Its staircase estimator solving current-game varied shots or the Remote Play encode/decode cadence without validation. |
| [2k-perfect](https://universe.roboflow.com/a1marky-outlook-com/2k-perfect) | An example of release-related detector classes and a community annotation target. | Version 3 is dated July 2024; project activity is not evidence of NBA 2K27 retraining. Label names do not prove prospective no-meter animation recognition. |
| [NBA-Players demo](https://github.com/luyangzhu/NBA-Players/blob/master/docs/DEMO.md) | Separate localization/cropping from pose/reconstruction and state occlusion/camera assumptions. | Its broadcast-view constraints fitting 2K, 2K Low or 2K Legacy, or 3D body reconstruction predicting the correct controller release. |

The acquisition concepts above guide future tracking and annotation. This revision adds manually configured synchronized evidence and manual multi-region review. It does not implement those community trackers, a feedback parser, teacher/student training, or a validated gameplay model.
