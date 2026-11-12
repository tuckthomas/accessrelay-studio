#!/usr/bin/env python3
"""Reproducible validation-only matrix. Never selects thresholds using final held-out data or promotes a model."""
import argparse
import csv
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys
from dataset_provenance import source_files, corpus_sha
from footage_integrity import footage_index, validate_independent
from temporal_experiments import HEADS, UNCERTAINTIES


def job_specs(heads, uncertainties, seeds):
    if (not heads or not uncertainties or not seeds or len(seeds) > 5 or len(set(heads)) != len(heads)
            or len(set(uncertainties)) != len(uncertainties) or len(set(seeds)) != len(seeds)
            or any(head not in HEADS for head in heads) or any(mode not in UNCERTAINTIES for mode in uncertainties)
            or any(type(seed) is not int or not 0 <= seed < 2 ** 31 for seed in seeds)):
        raise ValueError("Choose unique supported heads/losses and 1–5 reproducible nonnegative seeds")
    return [{"name": f"{head}-{mode}-seed{seed}", "head": head, "uncertainty": mode, "seed": seed}
            for seed in seeds for head in heads for mode in uncertainties]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--train", type=Path, nargs="+", required=True)
    parser.add_argument("--validation", type=Path, nargs="+", required=True)
    parser.add_argument("--locked-thresholds", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--heads", choices=HEADS, nargs="+", default=list(HEADS))
    parser.add_argument("--uncertainties", choices=UNCERTAINTIES, nargs="+", default=list(UNCERTAINTIES))
    parser.add_argument("--seeds", type=int, nargs="+", default=[20261111])
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--width", type=int, default=160); parser.add_argument("--height", type=int, default=160)
    parser.add_argument("--history", type=int, default=4); parser.add_argument("--interval-ms", type=float, default=16.6666667)
    parser.add_argument("--uncertainty-warmup-epochs", type=int, default=5)
    parser.add_argument("--cpu-threads", type=int, default=1)
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cpu")
    parser.add_argument("--target-provider", choices=("CPUExecutionProvider", "CUDAExecutionProvider"), default="CPUExecutionProvider")
    parser.add_argument("--max-run-seconds", type=int, default=3600)
    parser.add_argument("--plan-only", action="store_true")
    args = parser.parse_args()
    jobs = job_specs(args.heads, args.uncertainties, args.seeds)
    if (not 1 <= args.epochs <= 1000 or not 1 <= args.batch_size <= 128 or not 32 <= args.width <= 512 or not 32 <= args.height <= 512
            or not 2 <= args.history <= 16 or not math.isfinite(args.interval_ms) or not 4 <= args.interval_ms <= 100
            or not 1 <= args.cpu_threads <= 64 or not 0 <= args.uncertainty_warmup_epochs <= 1000 or not 60 <= args.max_run_seconds <= 86400):
        raise ValueError("Experiment shape, training or resource limits exceeded")
    if args.out.exists(): raise ValueError("Choose a fresh experiment directory; prior plans/results are never overwritten")
    root = Path(__file__).resolve().parent
    spec = importlib.util.spec_from_file_location("experiment_training", root / "Train-VisionModel.py")
    training = importlib.util.module_from_spec(spec); spec.loader.exec_module(training)
    train = training.load_sessions(args.train, "train", args.history, args.interval_ms)
    validation = training.load_sessions(args.validation, "validation", args.history, args.interval_ms)
    training.validate_session_splits(train[2], validation[2]); training.select_training_camera(train[4], validation[4], None)
    if train[3] != validation[3]: raise ValueError("Experiment groups use different animation configurations")
    train_index, validation_index = footage_index(train[0], min(4, args.history)), footage_index(validation[0], min(4, args.history))
    validate_independent(train_index, validation_index)
    expected_corpus = corpus_sha(source_files(train), source_files(validation), train_index, validation_index)
    thresholds = training.read_json(args.locked_thresholds)
    if thresholds.get("executionProvider") != args.target_provider or thresholds.get("animationProfile") != "nba2k27:" + train[3] or thresholds.get("cameraConfiguration") != train[4]:
        raise ValueError("Validation thresholds must bind the exact profile/camera and declared live target")
    if not args.plan_only:
        import torch
        if args.device == "cuda" and not torch.cuda.is_available(): raise ValueError("No CUDA training device is available")
    for job in jobs:
        output = args.out.resolve() / job["name"]
        job["trainingCommand"] = [sys.executable, str(root / "Train-VisionModel.py"), "--train", *map(str, args.train), "--validation", *map(str, args.validation),
            "--out", str(output), "--head", job["head"], "--uncertainty", job["uncertainty"], "--seed", str(job["seed"]),
            "--epochs", str(args.epochs), "--batch-size", str(args.batch_size), "--width", str(args.width), "--height", str(args.height),
            "--history", str(args.history), "--interval-ms", str(args.interval_ms), "--cpu-threads", str(args.cpu_threads),
            "--device", args.device, "--uncertainty-warmup-epochs", str(args.uncertainty_warmup_epochs)]
        job["evaluationCommand"] = [sys.executable, str(root / "Evaluate-VisionModel.py"), "--manifest", str(output / "manifest.json"), "--sessions", *map(str, args.validation),
            "--split", "validation", "--locked-thresholds", str(args.locked_thresholds), "--target-provider", args.target_provider,
            "--cpu-threads", str(args.cpu_threads), "--out", str(output / "validation")]
    args.out.mkdir(parents=True)
    (args.out / "experiment-plan.json").write_text(json.dumps({"schemaVersion": 1, "sourceCorpusSha256": expected_corpus,
        "selectionSplit": "validation", "heldOutEvaluated": False, "promotionApproved": False, "jobs": jobs}, indent=2) + "\n", encoding="utf-8")
    if args.plan_only:
        print(f"Prepared {len(jobs)} validation-only jobs. No model was trained or promoted."); return 0
    results = []
    for job in jobs:
        output = args.out.resolve() / job["name"]; output.mkdir()
        result = {key: job[key] for key in ("name", "head", "uncertainty", "seed")}
        try:
            for phase in ("training", "evaluation"):
                with (output / f"{phase}.log").open("w", encoding="utf-8") as log:
                    subprocess.run(job[phase + "Command"], check=True, stdout=log, stderr=subprocess.STDOUT, timeout=args.max_run_seconds)
                if phase == "training":
                    run = training.read_json(output / "training-run.json")
                    if run.get("sourceCorpusSha256") != expected_corpus: raise ValueError("Source corpus changed between experiment jobs")
            metrics = training.read_json(output / "validation/experiment-metrics.json")
            if corpus_sha(source_files(train), source_files(validation), footage_index(train[0], min(4, args.history)), footage_index(validation[0], min(4, args.history))) != expected_corpus:
                raise ValueError("Source corpus changed during experiment evaluation")
            result.update(metrics); result["parameterCount"] = run["parameterCount"]; result["succeeded"] = True
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError, OSError, KeyError) as error:
            result.update(succeeded=False, error=str(error))
        results.append(result); print(f"{job['name']}: {'completed' if result['succeeded'] else 'failed; inspect job logs'}", flush=True)
        (args.out / "experiment-results.json").write_text(json.dumps({"schemaVersion": 1, "sourceCorpusSha256": expected_corpus,
            "heldOutEvaluated": False, "hardwareValidated": False, "promotionApproved": False, "results": results}, indent=2) + "\n", encoding="utf-8")
    columns = ["name", "succeeded", "timingMAEms", "timingP99ms", "coverageWithinPredictedUncertainty", "targetShotCoverage", "falseReleaseRate", "cpuInferenceP50ms", "cpuInferenceP95ms", "parameterCount", "modelBytes"]
    with (args.out / "comparison.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns, extrasaction="ignore"); writer.writeheader(); writer.writerows(results)
    return 0 if all(result["succeeded"] for result in results) else 2


if __name__ == "__main__":
    try: raise SystemExit(main())
    except (ValueError, OSError, KeyError, ImportError) as error:
        print(f"FAILURE {error}", file=sys.stderr); raise SystemExit(2)
