# NBA 2K27 pack source

`src/` contains the built-in NBA shot configuration, persistence, tuning and the
`Nba2K27VisionPlugin` adapter. The shared temporal tracker is in
`src/AccessRelayStudio/Vision/ShotTracker.cs`. These C# sources are compiled into the trusted application, never
loaded as community DLLs. Namespace: `EliteScriptStudio.Packs.Nba2K27`.

`training/` owns the offline trainer, evaluator and preprocessing reference. Existing
`scripts/Train-VisionModel.py`, `scripts/Evaluate-VisionModel.py` and
`scripts/vision_preprocessing.py` remain compatibility entry points. Saved shot
profile JSON, configuration fingerprints, model manifest semantics and release
ownership are preserved. The host release scheduler and final output arbiter remain
in Core.

Core no longer imports NBA profile types: training capture accepts a small generic
profile interface, while the NBA adapter still validates the supported camera and
shot choices. Generic camera configuration text is bounded in Core; NBA profile
validation retains the exact 2K/2K Low/2K Legacy choices.

`pack.json` and `scripts/` add an optional declarative rhythm sequence using the
[game-pack API](../../docs/game-packs.md). Its tuning is separate from saved player
shot profiles and does not certify vision accuracy. The migration does not introduce
a marketplace, load native plugins, distribute model weights, or declare the existing
five-value temporal-release model suitable for every game.

The desktop-specific NBA workspace and sequence feedback controls are isolated in
`src/AccessRelayStudio/DesktopUi/src/game-plugins/nba2k27/`. The generic Remote Play
page contains neither shot fields nor NBA defaults and works with no plugin selected.
The host delegates profile editing and training validation through typed contracts;
NBA-specific capture policy stays with this adapter. Registration requires the reviewed
bundled manifest and does not authorize loading third-party native or UI code.


## Calibrated capture and training

The Model Training package enables this reviewed adapter. Its capture configuration
lives in `Nba2K27CaptureConfiguration.cs`, and `Nba2K27RegionTracker.cs` follows the
calibrated torso appearance, relative meter search region and floor homography.
The fixed shot-feedback panel remains independent. Court geometry constrains the
player's ground position without masking body pixels above the sideline. Tracking
is a short-range prototype; it requires gameplay validation and reacquisition after
loss. It does not include a trained person/pose detector or a meter-fill parser.

The NBA workspace configures those anchors. Source window and data/output folders
are global Model Training settings. Version-3 evidence retains the dynamic crops,
full scene, meter/feedback and controller trajectory. `training/Run-NbaTraining.py`
prepares explicitly reviewed train/validation clips using a separate meter-off
configuration reference, then runs the existing trainer/exporter/evaluator. A local
Python environment is needed only for offline work. Live analysis uses the native
C# pipeline; visual-anchor candidates cannot arm automatic release before gameplay
and hardware acceptance. No trained user model or bundled model weights are claimed.


## Automatic acquisition (beta.19)

`Nba2K27AutomaticCapture.cs` owns exact-gamertag/body association, body/joint-based
regions, meter component proposals, floor estimates and fixed feedback detection.
The host provides generic native ONNX pose/OCR inference. `Detect capture areas`
acquires a reviewable proposal without drawing initial player/meter/court boxes.
The displayed gamertag belongs to the saved NBA profile and matching is case
sensitive. Two identity observations, periodic rechecks and ambiguity rejection
replace jersey appearance as the automatic acquisition identity source.

Run `scripts/Install-NbaCaptureModels.ps1` when installing this beta; see
`docs/capture-models.md` for sources, hashes and notices. Weights are project-local,
ignored files. Live capture uses no Python service. Version-4 recordings preserve
17 joints, exact-tag evidence, meter visibility, floor estimate, source sequences
and the capture backend. Bounded acquisition diagnostics go to the configured
model-output directory; they are not reviewed training takes or exposed via MCP.

The offline release network now uses ordered RGB history (16 frames, 50 ms spacing).
It can be trained on reviewed movement examples; the acquisition pose network is
not itself a shot-type classifier or green-window predictor. No trained NBA 2K27
release weights are supplied. Meter/floor proposals and gameplay pose/identity
accuracy still require actual-game validation, followed by diverse shot/contest
training and held-out timing checks. The old manual calibration remains available
as an explicitly selected alternative, rather than the automatic setup path.
