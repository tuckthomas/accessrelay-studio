# Installable training plugins and NBA 2K27 tracking

Requested 2026-11-13. This supplements the platform roadmap; it does not mark the
NBA model or physical validation complete.

## Requirement correction: automatic acquisition (beta.19)

The beta.18 manual player/meter/court calibration did **not** satisfy automatic
capture-area acquisition. Its appearance confidence was not an identity guarantee;
a confident match could switch to another player. The torso tracker did not
recognize shot types or green-window timing. Earlier completion language must not
be interpreted as acceptance of those requirements.

- [x] Add native pretrained RTMO-t body/17-joint detection and English PaddleOCR
      detection/recognition, with hash-pinned model assets on M:.
- [x] Add the exact displayed gamertag to the NBA profile. Acquire through two
      observations, case-sensitive OCR/body-foot association, periodic identity
      checks and ambiguity rejection. Initial player boxes are not required.
- [x] Detect capture areas and present an image for review. Keep manual setup as
      an explicit advanced alternative. Model/body detection is separate from
      meter-component and floor-color estimates; those are not trained NBA models.
- [x] Store identity, joints, body/crop, meter visibility/search area, court estimate
      and capture backend in version-4 evidence. Keep the full scene and actual
      controller trace. Reject missing/mismatched identity/pose in conversion.
- [x] Use an ordered 16-frame/50 ms temporal RGB model in the training workflow.
      Pose evidence is recorded for review; that network currently consumes RGB,
      not pose vectors, and does not output an explicit shot-type class.
- [x] Run native CPU/CUDA pose and OCR on upstream/synthetic reference images;
      test exact-case matching and ambiguous-body rejection. Train/export/evaluate
      actual code on synthetic v4 data with the new temporal history. These checks
      establish software execution, not gameplay recognition or timing accuracy.
- [ ] Verify automatic identity, pose, meter, feedback and floor proposals on the
      user's actual moving NBA gameplay, including similar jerseys and occlusion.
- [ ] Collect reviewed meter-on attempts, distinct meter-off reference/validation,
      shot-type/contest coverage and independent target-phase evidence. Fit and
      validate the release model. No trained user timing model exists yet.
- [ ] Validate timing/coverage separately for stationary, catch-and-shoot,
      off-dribble, fade/leaner and contested attempts, then held-out/hardware timing.

The native CUDA acquisition policy permits ORT CPU shape/NMS operators and records
that policy. It does not relax release-model acceptance. Automatic proposals remain
observation-only. Incorrect/missing detection is a failed acquisition to investigate,
not permission to silently use a fixed box or call training complete.

## Plugin boundary

- [x] Separate Marketplace types: game scripts, controller profiles, game plugins,
      model training plugins. Filter by type and game.
- [x] Publish a distinct NBA 2K27 Model Training package in the Marketplace repo.
      Installing its controller script does not install training.
- [x] Only installed training packages enable their reviewed adapter in Model
      Training. Keep NBA profiles, meter/cue/camera rules and evidence stream
      selection in `games/nba2k27` and its UI plugin.
- [x] Keep the host responsible for capture, storage, scheduling, device ownership,
      model execution and generic plugin selection. Downloaded manifests cannot
      execute arbitrary DLLs, Python or browser code.
- [x] Validate installation and selection in the installed desktop app (beta.17).

## Acquisition correction

- [x] New NBA capture layouts retain the full gameplay scene and fixed shot feedback
      instead of marking stationary player/meter crops. Keep court context AND the
      image above the visible floor so raised hands and the ball remain present.
- [x] Keep synchronized raw controller trajectory and capture timestamps. Record
      scene dimensions and mark the scene explicitly as untracked acquisition data.
      Bound scene dimensions to 1280 per axis and visual storage to 4 GiB/10 seconds.
- [x] Keep old fixed-region recordings readable. Reject scene overviews as player
      crops in release-model conversion and live analysis.
- [ ] Record and review actual new scene-first footage, including meter/feedback
      legibility and disk/recording overhead. No such recording has been made yet.

## Plugin-owned capture configuration and training (beta.18)

- [x] The NBA adapter owns the player, meter, court and feedback policies, initial
      regions, camera validation and next-shot reference trigger. The host owns
      the source window, frame leases, dataset folders, worker lifecycle and storage.
- [x] Calibrate the intended player's body, meter search region, four ordered court
      points and fixed feedback HUD on a paused reference. With the meter enabled,
      capture that reference after fresh manual shot input rather than before it.
- [x] Track torso appearance and floor features independently. Use the floor
      homography as a ground-position prior, not a pixel mask. Pad the moving body
      crop above the floor for raised hands. Regions use normalized source
      coordinates and observed player scale; stored tensor sizes are not screen boxes.
- [x] Translate/scale the meter search region relative to the player. This follows
      the calibrated region; it is not an implemented meter-outline/fill detector.
- [x] Persist per-frame crops, court projection, confidence, sequence, failure
      reason, raw controller trajectory and timestamps in version-3 evidence.
      Stop incomplete on low/ambiguous match scores; this legacy appearance prototype cannot guarantee identity.
- [x] Review tracked framing/overlay exclusion and target phases. Retain original
      evidence and hash-bound conversion records. Reject meter/feedback leakage
      into player-model crops. Scene-first version-2 data is still not player data.
- [x] Expose the NBA Train model workflow: reviewed meter-on training, independent
      meter-off validation and a separate meter-off configuration reference. Train,
      export ONNX and evaluate via the selected local Python environment. CPU is
      supported; CUDA training requires a CUDA-enabled environment. Candidates are
      observation-only; no hardware acceptance or automatic release is inferred.
- [x] Exercise moving player/meter geometry, independent camera/floor motion,
      dynamic recording pixels, preprocessing parity, persistence and loss handling
      with synthetic software checks. Execute real train/export/evaluate code on
      synthetic data without importing that test candidate into the user's library.
- [ ] Validate acquisition and tracking on actual NBA 2K27 gameplay: stationary
      and moving attempts, camera pan/zoom, 1080p/1440p, occlusion, raised hands,
      meter visibility and spectators. Measure processing p95/p99 with recording.
- [ ] Record and review actual user training, validation and reference takes.
      No such completed takes or trained user model exist yet.
- [ ] Improve or replace short-range appearance tracking where gameplay exposes
      failures. A trained detector/keypoint/identity model and actual meter/fill
      detection remain future work; none is bundled or claimed here.
- [ ] Pass independent held-out, hardware and controller timing acceptance before
      enabling release. The visual-anchor policy is explicitly observation-only.

The calibrated appearance tracker is a prototype independent of the shot model.
It can lose lock on pose changes, similar jerseys, occlusion, textureless floor,
large camera changes or source resizing. Stop/reacquire is required after loss.
The court outline follows planar features; it is not semantic court segmentation.

## 2026-11-13 delivery evidence

Implementation: `70c367b`. Marketplace: `4b13eef` (training package and separate
rhythm script; committed LF manifest/resource bytes checked against catalog hashes).
Beta.17 was published from the feature working tree and installed in the existing
project-local `AccessRelayStudio-Beta` directory. Its embedded base SHA is `1bbccb3`;
the build preceded the implementation commit. Existing local page-label edits
were retained in the installed UI and left uncommitted.

Affected UI checks passed across focused runs (33 cases), as did the native
installation-gating/scene-writer check, bounded desktop reader including 1280×720
scene-first data, explicit scene-to-crop conversion rejection, TypeScript/Vite
build and Windows publication. Native evidence is retained in
`artifacts/PluginTrainingSeparation/bf01fb1e0e334dd7bbe37df2491fb3ab`.

Observed in the user's installed beta: no NBA training UI before installation;
separate training/script cards in the live Marketplace; training package installed
through its Install button; Open training selected the installed NBA adapter and
loaded the saved Stretch Big meter-on profile. New setup exposes Gameplay and Shot
feedback only. A fresh preview captured actual gameplay at 2560×1439; no take was
started. The user had changed Remote Play to fullscreen; fresh region confirmation
and a stable source size remain required before recording. No tracker, shot-model
accuracy, real recording throughput or Xbox end-to-end latency is claimed.

## Beta.18 software evidence

Focused UI checks: 14 cases; conversion checks: 2 cases. Native verification
round-trips tracked pixels and provenance, checks dynamic CPU preprocessing and
saved references larger than the former 32 KiB settings limit, then loses tracking
and confirms an incomplete take. See `artifacts/AnchorRecorder` and
`artifacts/AnchorSmoke`. The latter executes actual optimization, ONNX export and
CPU evaluation on explicitly synthetic clips. It is not gameplay accuracy evidence.
No synthetic candidate is installed in the user's model library.

The local interpreter stays on M: in `artifacts/GpuProbePython`; the installation's
`TrainingPython` junction references that environment. Capture data and training
results default beside the installed app on M:. Python is used only for offline
training/evaluation; live inference remains native ONNX Runtime in the C# app.
Assistant MCP facts do not include capture settings or the raw reference image.


Beta.18 installed from `ac9d38d` at the existing project-local executable. Native
UI verification confirmed the installed NBA adapter, saved meter-on profile,
host-owned Chrome source, four anchor/feedback controls and Train model tab. The
training interpreter resolves to the M: environment. The first WebView launch
reported an interface failure; one Retry recovered it and subsequent navigation
succeeded. The cause is unconfirmed. Installation receipt:
`artifacts/AnchorSmoke/installation.json`. No gameplay reference calibration or
user take was recorded during this installation check.


Beta.19 software evidence: native CPU and CUDA RTMO/PaddleOCR execution passed on
upstream/synthetic reference images, including exact-case OCR. Focused UI checks
passed 15 cases; conversion checks passed 5 cases. Automatic identity association
and ambiguity checks passed. The actual native recorder produced a completed
synthetic v4 take, read through the desktop dataset reader with matching pixels and
17 joints. The real optimization/export/evaluation workflow ran on synthetic v4
footage using ordered 16-frame history. The synthetic candidate remains outside
the user's model library. Evidence: `artifacts/AutoCaptureModels`,
`artifacts/AutoCaptureRecorder` and `artifacts/AutoCaptureWorkflow`.
Actual NBA gameplay acquisition and user-model timing remain unverified.


First actual acquisition attempt (beta.19): exact OCR read `DrakulaVlad2` at
0.974 model confidence, but full-window RTMO detected no body. This is a failed
acquisition, not gameplay acceptance. The corrective beta.20 uses the tag to
locate a broad, higher-resolution pose search and follows detected body bounds
thereafter; final player regions still come from body/joint detections. The native
probe found one body on the saved gameplay frame with this correction. A single
saved-frame probe is not live tracking, meter/floor detection or timing acceptance.

Automatic acquisition runs as a cancellable background UI operation (90-second
limit), allowing time to return to gameplay and finish an attempt without the
30-second command timeout. A completed proposal opens once through status updates;
cancelling it consumes that token and restores the prior configuration. This does
not start a training take or controller output.
