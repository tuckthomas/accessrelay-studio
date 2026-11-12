# Temporal model experiments

The default remains the existing global-pooling architecture and residual-error
uncertainty objective. Temporal Conv3d already encodes local frame order; global
pooling removes explicit feature positions, not all temporal information. The
alternative `ordered-head` pools only spatial dimensions, then flattens `24 × T`
features into the same five-value output contract. Neither is presumed superior.

Uncertainty objectives:

| Option | Training/manifest meaning |
| --- | --- |
| `residual` | Existing detached absolute residual target; `absolute-error-estimate-v1` |
| `staged-residual` | Timing/phase warm-up before enabling residual uncertainty learning; same output semantics |
| `gaussian-nll` | Joint mean/variance Gaussian NLL; bounded sigma ≥1 ms; exported uncertainty is a 95% half-width, `gaussian-95-half-width-v1` |

Candidate model versions include the head/objective and revision. New manifests
declare `outputContract` and `uncertaintySemantics`; the current C# runtime accepts
both documented uncertainty meanings and defaults legacy manifests to the residual
meaning. Unsupported semantics are rejected. Older apps can reject new manifests;
do not remove fields to conceal a changed uncertainty meaning. Existing model
weights, shapes and preprocessing remain compatible with the current runtime.

Install the optional offline dependencies from `tools/vision/requirements-offline.txt`
in a separate environment. No Python or training dependencies are shipped with the
desktop runtime. Choose reviewed converted/legacy train and validation sessions and
a single profile/camera. Create a validation threshold file using the existing
[evaluation workflow](vision-model-workflow.md). The comparison must use the same
threshold policy for every variant; final held-out data is reserved for later.

```powershell
python games/nba2k27/training/Run-VisionExperiments.py `
  --train datasets/converted-training-takes `
  --validation datasets/converted-validation-takes `
  --locked-thresholds experiments/validation-thresholds.json `
  --out experiments/model-matrix --seeds 20261111 20261112 --epochs 20 `
  --uncertainty-warmup-epochs 5 --cpu-threads 1 --plan-only
```

`--plan-only` verifies source/split/footage provenance and writes concrete argv lists
without importing PyTorch or training a model. To execute, omit it and choose a fresh
output folder. The default matrix is two heads × three objectives per seed, with at
most five seeds. It has no held-out input argument. Training and evaluation run in
separate bounded-duration processes. Commands use argv arrays, not a shell. Failures
retain job logs and appear in the summary rather than erasing completed jobs.

The trainer records seeds, deterministic-algorithm mode, dependency versions, thread
count, architecture/objective, parameter count, session groups, RGB-footage hashes
and exact metadata/label hashes. A common corpus digest binds every comparison job;
source changes during training/evaluation fail the job. Recorded game builds cannot
be relabeled by CLI arguments or mixed with unknown legacy build provenance.

Outputs are `experiment-plan.json`, per-job training/export/evaluation artifacts,
`experiment-results.json`, and `comparison.csv`. Descriptive metrics include timing
MAE/RMSE/percentiles, coverage within predicted uncertainty, target-shot coverage,
false-release rate, parameter/file size and warmed CPU inference percentiles with
fixed thread count. Gaussian runs also report one-sigma coverage. Residual estimates
are not statistical confidence intervals; overlapping windows are correlated and
their coverage is not an independent confidence estimate. Compare objectives using
the same validation data and quality criteria, not training-loss values.

CPU inference timings exclude capture, preprocessing, transfers, scheduling and
console delivery. They cannot certify Windows CPU/CUDA performance. Once architecture
and thresholds are selected using validation, lock them before a single independent
held-out evaluation and then collect actual Windows hardware timing evidence.
Exports retain `validation=null`; experiment summaries never promote/arm a candidate.

Verification includes finite-gradient/output tests for all variants, warm-up behavior,
seeded update reproducibility, controlled position-preservation and ONNX CPU numerical
parity. A six-job synthetic training/export/evaluation smoke run and actual C# CPU
inference also passed. Those synthetic results establish tooling/contract operation,
not an architecture winner, gameplay accuracy or hardware latency improvement.
