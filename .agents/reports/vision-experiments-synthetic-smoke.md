# Synthetic model experiment verification

All inputs are uniformly colored synthetic synchronized streams created by
`tools/vision/test_evidence_conversion.py`. Their review flags identify a synthetic
test fixture. They are not gameplay, dysarthric/user data or hardware measurements.

- Python 3.12; PyTorch 2.10.0+cpu; NumPy 2.2.4; ONNX 1.23.2;
  ONNX Runtime 1.31.1; protobuf 7.36.2; ml_dtypes 0.6.0.
- 51 Python checks passed with the optional ML environment. The stdlib-only suite
  runs 46 checks and skips five optional numerical/export checks.
- The complete matrix ran both heads × three objectives with seed 20261111,
  one epoch, batch size 2, T=4, 32×32 input, 10 ms interval and one CPU thread.
  The smoke run set uncertainty warm-up to zero; separate numerical tests verify
  the early/late warm-up gradient transition.
- All six jobs trained, exported valid ONNX and completed validation-only evaluation
  with the same corpus digest. Every model retained `validation=null`; summaries
  kept `heldOutEvaluated=false`, `hardwareValidated=false`, `promotionApproved=false`.
- The ordered head has 15,005 parameters versus 14,645 for global pooling at T=4.
  All six exported variants passed PyTorch/ONNX CPU numerical comparison.
- An ordered-head/Gaussian export also loaded, warmed up and produced 17 temporal
  observations through the actual C# CPU adapter with `CanArm=False`.
- Existing C# vision, preprocessing, freshness/cancellation, model-library,
  calibration/arming and performance checks passed with the matching Linux CPU
  ONNX runtime. Generic camera validation is now tested separately from NBA's
  supported-camera restriction, following the game-pack extraction.

Local ephemeral artifacts (not distributed model weights):
`/workspace/.cloud-setup/vision-experiment-matrix/experiment-results.json` and
`comparison.csv`. The fixture generation and CLI commands can be reproduced from
the converter tests and `docs/vision-model-experiments.md`.

No variant is selected/promoted. Real reviewed train/validation/held-out gameplay,
calibration coverage and Windows capture/CPU/CUDA timing remain outstanding.
