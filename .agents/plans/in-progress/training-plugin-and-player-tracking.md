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

Automatic acquisition runs as a cancellable background UI operation (120-second
limit), allowing time to return to gameplay and finish an attempt without the
30-second command timeout. A completed proposal opens once through status updates;
cancelling it consumes that token and restores the prior configuration. This does
not activate controller output. Beta.21 additionally retains a bounded raw
calibration clip when shot input is observed.


Beta.20 installed at the existing project-local executable from `9a512f0`.
Both saved NBA shot profiles now contain the observed `DrakulaVlad2` gamertag;
original profile files were backed up under `artifacts/AutoCaptureModels`.
Actual live acquisition then produced exact-case identity evidence, 17 joints and
an estimated 13-vertex court polygon, on CUDA with CPU operator support. This is
initial live acquisition only: moving/occluded identity, court accuracy, shot meter
and feedback correctness have not passed acceptance. No user training take or
trained user timing model has been produced. A physical shot/feedback result is
pending. Receipt: `artifacts/AutoCaptureModels/installation20.json`.


The user confirmed the manual shot and feedback finished. Saved beta.20 diagnostics
contained only identity/pose/floor acquisition, with no meter or feedback detection
and no full take; this did not pass HUD acquisition. The timing of the shot relative
to the acquisition timeout is not established. Beta.21 adds one bounded full-scene
calibration recording triggered by physical shot input or the NBA raised-hands pose
policy. A failed region detector cannot discard or crop that raw scene. Unknown
HUD regions are absent, not fabricated; calibration-only scene data stays rejected
by release-model conversion until reviewed per-frame regions are generated.
Native writer/reader checks cover preservation through detector failure. Live
recording and actual meter/feedback diagnosis remain pending.

Setup also retains up to 300 timestamped preview reference frames at 2.5 Hz under
the M: model-output acquisition directory. This bounded reference sequence remains
available even if the physical/pose recording trigger misses. Beta.22 rotates
these slots, preserving the most recent setup interval while waiting. It is diagnostic
acquisition footage, not a high-rate timing dataset or an accepted player crop.

## First retained user shot and beta.22 correction

The user completed the next shot. Beta.21 saved
`take-20261114-015719-ed694749`: 134 full-scene frames over 9.794 seconds and 2,416
physical/controller trace samples, with no writer-channel drops. This is raw
calibration evidence, not a labeled or trainable release-model dataset. Native
OCR read `RHYTHM`, `LATE`, `DISTANCE` and `28'8"`; the original feedback keyword
policy did not support that layout. The meter was a neutral-gray curved ribbon
with a small green target, which the original bright-component policy discarded.

Beta.22 recognizes those feedback headings and the connected gray/green meter.
The native HUD policy was run on the actual recorded pixels and stored OCR results.
Visual inspection confirmed both sample meter crops and the feedback crop, with
no meter proposed in the checked idle frame. This is one-shot, saved-evidence
verification, not broad camera/movement, occlusion, meter-style or gameplay acceptance.

The same recording exposed capture gaps: median 18.98 ms, p95 255.57 ms and maximum
296.82 ms. Its writer reported no drops because synchronous OCR blocked the
upstream consumer. Unconfigured automatic setup now runs one bounded analysis job
on an owned pixel copy while the raw scene writer continues independently. Its
preview and observation retain matching sequence, dimensions and source generation;
stale analysis is never attached to a different recorded frame. Raw calibration
frames are deliberately unannotated; sampled analysis remains in setup diagnostics.
Configured dynamic-crop recording continues to require exact-frame tracking.
No queued history or second capture session is added. A focused blocked-analyzer
check confirmed that the raw writer advances, the analysis queue stays at one,
and its returned pixels/sequence/generation remain associated. Actual post-change
capture cadence still requires a fresh live recording.

After exact player acquisition, setup now waits for the user's first shot until
they stop it; the former two-minute pre-shot timeout could expire during a voice
or chat handoff. Missing initial identity still times out after two minutes, and
missing regions after a triggered shot time out after 30 seconds. Reference slots
remain bounded at 300 while waiting, and the raw clip remains bounded at ten seconds.

## Beta.22 live recording result

The user completed the next manual attempt. The installed beta retained
`take-20261114-021533-00487316`: 538 raw full-scene frames over 9.970 seconds
(53.86 frames/s), no writer-channel drops and 2,415 controller trace samples.
Capture-gap median was 18.23 ms, p95 21.68 ms, p99 25.25 ms and maximum 41.38 ms.
These measure recording cadence, not original Xbox frame age or applied release
latency. The clip remains unannotated calibration evidence, not a trained model.

Live acquisition produced exact-case DrakulaVlad2 identity, 17 joints, the gray
curved meter and the RHYTHM/DISTANCE feedback panel. Review exposed two proposal
issues: waiting for the full recording could replace the first observed meter
with a later, pose-scaled cached projection, and a floor-color flood could climb
the similarly colored back wall. Setup now preserves the first complete image
with an actually observed meter. Floor estimation additionally requires a broad
dark-to-light floor transition and cannot grow above it. On the retained actual
frame 654, the native policy returns the meter, feedback and a four-vertex floor
outline; its rendered overlay was reviewed. This is a corrected saved-frame
proposal, not movement/occlusion or multi-camera court acceptance. The user's
exact game build/patch is pending before saving the capture profile. Artifacts:
`artifacts/AutoCaptureModels/live22-reviewed-regions.json` and
`AccessRelayStudio-Beta/TrainingResults/acquisition-20261114-021524-e8f690a3`.

## Bundled models and numbered game revision

The user rejected substituting a dated "latest online update" label for a game
version. No such label was saved. The official NBA 2K27 Steam announcement lists
patch v2.0 for Xbox Series X|S; its source is retained separately from the console's
installed package version, which must be read from File info. Published patch and
observed package build must not be silently treated as the same measurement.

Beta.23 makes normal `dotnet publish` include the existing, hash-pinned RTMO-t and
English PP-OCRv3/v4 ONNX weights, approximately 35.7 MiB. The app distribution also
includes the Apache-2.0 license, model-specific notices and a source/hash manifest.
The publishing machine uses the existing M: cache; end users receive the models
inside the application. These remain generic acquisition models and are not a
trained NBA release model. No game-specific controls move into the global shell.

Beta.23 installed from `ce45770` at the existing project-local executable. The
normal publish hook and installed files both verified all three hashes and the
license/notices manifest. The Windows PowerShell publish child explicitly loads
its own utility module so an inherited PowerShell 7 module path cannot hide hashing
commands. Evidence: `artifacts/ModelRedistribution/installation23.json`.

The user exposed the Xbox management screen, which directly showed **Version
2.0.0.12** for NBA 2K27 (Xbox Series X|S). The exact observed version was saved in
the meter-on capture profile through the native configuration validator/store.
Both retained raw calibration clips have a `game-version.json` sidecar binding
this later inspection to the original session metadata hash; the original session
files were not relabeled or rewritten. The profile was restored with the host
source selected before loading the NBA plugin; the installed UI showed all four
saved capture-area checks and an enabled recording control. No trained shot model
or full-motion tracking acceptance is implied by that saved setup. The interface
needed one recovery click on relaunch, after which navigation succeeded.

## Full-scene recording and offline crop preparation

Capture-only automatic sessions now retain the complete scene independently of
sampled live pose/OCR analysis, including after the capture layout is configured.
The recording API selects the plugin's scene-first policy using the current
source dimensions. Opening a raw clip runs native exact-frame annotation offline
and creates a separate tracked take; original footage and controller samples are
unchanged. Failure still permits review of the raw scene. No Python live worker
or controller activation is introduced.

OCR cadence and identity expiry use original captured timestamps during replay.
A single missed OCR result can retain a uniquely associated, recently verified
pose, but the 700 ms identity expiry and ambiguous-body rejection remain. Player
crop trimming preserves confident joints with limb/head margins; it never masks
HUD pixels. Frames with lost identity, insufficient regions or HUD overlap are
excluded from the derived take and retained in the raw clip and per-frame report.
Both raw source identity and exact frame/timestamp mapping are checked by the
offline converter; inspected build 2.0.0.12 remains bound to the original metadata.

Native replay of `take-20261114-021533-00487316` produced **207 of 538** usable
player crops. The longest contiguous run is 44 source frames. The report retains
144 HUD-overlap exclusions and 187 unconfirmed/expired-identity frames. Sample
raised-hand/ball crops were visually inspected. Modified source timestamps,
acquisition hashes and source indices were rejected by the actual converter.
Focused identity-clock/ambiguity checks and the interface build passed. These
are region-preparation results, not target labels, model accuracy or full-motion
tracking acceptance. New installed-path recording validation remains to be done.

Prepared evidence:
`AccessRelayStudio-Beta/TrainingData/take-20261114-021533-00487316-tracked-v1-84192f9a`.
Review images and integrity results are under `artifacts/AutoCaptureModels`.

Beta.24 is installed from `cd2be64` at the existing project-local executable and
pushed to main. Publish completed with the existing unrelated voice nullability
warning. Startup opened normally. Through the installed interface, opening the
original 538-frame raw clip reused its prepared evidence and displayed 207 usable
frames, all four synchronized streams and a player image on the review timeline.
No shot labels were saved and no candidate was trained. The next manual take is
pending the user's shooting readiness; capture and controller output remain off.
