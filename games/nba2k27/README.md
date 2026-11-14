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
