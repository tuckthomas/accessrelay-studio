# NBA 2K27 pack source

`src/` contains the built-in NBA shot configuration, persistence/tuning and temporal
tracker adapter. These C# sources are compiled into the trusted application, never
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

This extraction is the first game-pack migration step. It does not introduce a
marketplace, make native plugins loadable, distribute model weights, or declare
the existing five-value temporal-release model suitable for every game.
