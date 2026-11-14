#!/usr/bin/env python3
"""Offline ONNX evaluation on reviewed recorded sessions; never certifies hardware timing.

Uses CPU deliberately as an offline numerical reference. The declared live target is
bound to locked thresholds; these offline results do not certify either live backend's timing.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import sys
import time

from vision_preprocessing import PREPROCESSING_ID, resize_stored_bgra
from footage_integrity import footage_index, validate_evaluation_footage
from experiment_metrics import summarize
from dataset_provenance import source_files

EXECUTION_PROVIDERS = ("CPUExecutionProvider", "CUDAExecutionProvider")


def validate_evaluation_sessions(split: str, session_ids: set[str], run: dict) -> None:
    if split not in ("validation", "held-out") or not session_ids:
        raise ValueError("Evaluation requires nonempty validation or held-out capture session groups")
    groups = []
    for key in ("trainingSessionIds", "validationSessionIds"):
        values = run.get(key)
        if (not isinstance(values, list) or not values
                or any(not isinstance(value, str) or not value for value in values)
                or len(set(values)) != len(values)):
            raise ValueError("Training provenance needs nonempty unique trainingSessionIds and validationSessionIds")
        groups.append(set(values))
    training_ids, validation_ids = groups
    if training_ids & validation_ids:
        raise ValueError("Training provenance has overlapping training and validation sessions")
    excluded = training_ids | validation_ids if split == "held-out" else training_ids
    if session_ids & excluded:
        raise ValueError("Final held-out sessions overlap training or validation" if split == "held-out"
                         else "Validation evaluation sessions overlap training")


def select_target_provider(requested: str, thresholds: dict) -> str:
    if requested not in EXECUTION_PROVIDERS:
        raise ValueError("Target provider must be CPUExecutionProvider or CUDAExecutionProvider")
    if thresholds.get("executionProvider") != requested:
        raise ValueError("Locked thresholds must declare executionProvider matching --target-provider; this is the intended live hardware target, not the offline CPU reference")
    return requested


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--sessions", required=True, type=Path, nargs="+")
    parser.add_argument("--split", choices=("validation", "held-out"), required=True)
    parser.add_argument("--locked-thresholds", required=True, type=Path)
    parser.add_argument("--assume-legacy-camera-2k", action="store_true",
                        help="Explicitly allow reviewed legacy datasets missing cameraView as 2K")
    parser.add_argument("--target-provider", choices=EXECUTION_PROVIDERS, default="CUDAExecutionProvider",
                        help="Declared live hardware target bound to locked thresholds; offline numerical inference still uses CPU")
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--cpu-threads", type=int, default=1)
    args = parser.parse_args()
    if any((args.out / name).exists() for name in ("accuracy-report.json", "predictions.jsonl", "experiment-metrics.json")):
        raise ValueError("Choose a fresh evaluation directory to preserve prior reports")
    if not 1 <= args.cpu_threads <= 64: raise ValueError("CPU threads must be 1–64")
    import numpy as np
    import onnxruntime as ort

    spec = importlib.util.spec_from_file_location("vision_training", Path(__file__).with_name("Train-VisionModel.py"))
    training = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(training)
    manifest = training.read_json(args.manifest)
    if manifest.get("preprocessing") != PREPROCESSING_ID:
        raise ValueError("Evaluation requires the same canonical v2 preprocessing contract as the runtime")
    semantics = manifest.get("uncertaintySemantics", "absolute-error-estimate-v1")
    if semantics not in ("absolute-error-estimate-v1", "gaussian-95-half-width-v1") or manifest.get("outputContract", "temporal-release-v1") != "temporal-release-v1":
        raise ValueError("Unsupported versioned observation/uncertainty semantics")
    thresholds = training.read_json(args.locked_thresholds)
    target_provider = select_target_provider(args.target_provider, thresholds)
    confidence = training.finite(thresholds.get("minimumConfidence"), "minimumConfidence")
    uncertainty = training.finite(thresholds.get("maximumUncertaintyMs"), "maximumUncertaintyMs")
    frame_age_limit = training.finite(thresholds.get("maximumFrameAgeMs"), "maximumFrameAgeMs")
    if not 0 < confidence <= 1 or not 0 < uncertainty <= 500 or not 0 < frame_age_limit <= 1000:
        raise ValueError("Locked thresholds are outside the runtime contract")
    if thresholds.get("animationProfile") != manifest["animationProfile"] or not thresholds.get("datasetId") or not thresholds.get("calibrationProcedureVersion"):
        raise ValueError("Threshold file must bind the exact profile, dataset and calibration procedure")
    if manifest.get("cameraConfiguration") not in training.CAMERA_VIEWS or thresholds.get("cameraConfiguration") != manifest["cameraConfiguration"]:
        raise ValueError("Thresholds and model must bind the same supported cameraConfiguration")
    if args.split == "held-out" and thresholds.get("lockedBeforeFinalEvaluation") is not True:
        raise ValueError("Lock thresholds using validation/calibration before evaluating the final held-out set")
    model_path = training.child_file(args.manifest.parent, manifest["modelFile"])
    actual_hash = hashlib.sha256(model_path.read_bytes()).hexdigest()
    if actual_hash != manifest["modelSha256"]:
        raise ValueError("Model SHA-256 differs from the manifest")
    run = training.read_json(args.manifest.with_name("training-run.json"))
    if run.get("diagnosticFixture") or run.get("modelSha256") != actual_hash:
        raise ValueError("Diagnostic/untrained fixtures cannot produce validation evidence")
    if run.get("cameraConfiguration") != manifest["cameraConfiguration"]:
        raise ValueError("Training provenance camera differs from the model; legacy candidates need re-export with reviewed camera provenance")
    loaded = training.load_sessions(args.sessions, args.split, manifest["historyLength"], manifest["historyIntervalMs"], args.assume_legacy_camera_2k)
    sessions, samples, session_ids, fingerprint, recorded_camera = loaded
    if recorded_camera != manifest["cameraConfiguration"]:
        raise ValueError("Evaluation shotProfile.cameraView differs from the model cameraConfiguration")
    if manifest["animationProfile"] != "nba2k27:" + fingerprint:
        raise ValueError("Evaluation sessions use a different shot profile")
    validate_evaluation_sessions(args.split, session_ids, run)
    evaluation_footage = footage_index(sessions, min(4, manifest["historyLength"]))
    validate_evaluation_footage(args.split, evaluation_footage, run)
    evaluation_sources = source_files(loaded)
    options = ort.SessionOptions(); options.intra_op_num_threads = args.cpu_threads; options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
    expected = [1, manifest["historyLength"], 3, manifest["inputHeight"], manifest["inputWidth"]]
    if len(session.get_inputs()) != 1 or session.get_inputs()[0].shape != expected or session.get_outputs()[0].shape != [1, 5]:
        raise ValueError("Exported ONNX shapes differ from the fixed runtime contract")
    for _ in range(8): session.run([manifest["outputName"]], {manifest["inputName"]: np.zeros(expected, dtype=np.float32)})
    window_errors, window_widths, cpu_inference_ms = [], [], []
    accepted, last_times, identity_correct, observation_count = {}, {}, 0, 0
    for session_index, indices, target in samples:
        directory, recording = sessions[session_index]
        frames = []
        for index in indices:
            frame = recording["frames"][index]
            with training.child_file(directory, frame["file"]).open("rb") as source:
                source.seek(frame["offsetBytes"])
                raw = source.read(frame["byteLength"])
            bgra = np.frombuffer(raw, dtype=np.uint8).reshape(recording["storedHeight"], recording["storedWidth"], 4)
            frames.append(resize_stored_bgra(bgra, manifest["inputHeight"], manifest["inputWidth"],
                                            manifest["rgbMean"], manifest["rgbStandardDeviation"]))
        input_tensor = np.stack(frames)[None]
        began = time.perf_counter_ns()
        output = session.run([manifest["outputName"]], {manifest["inputName"]: input_tensor})[0].reshape(-1)
        cpu_inference_ms.append((time.perf_counter_ns() - began) / 1e6)
        if output.shape != (5,) or not np.isfinite(output).all() or np.any(output[[0, 1, 4]] < 0) or np.any(output[[0, 1, 4]] > 1) or np.any(output[2:4] < 0) or np.any(output[2:4] > manifest["maximumPredictionHorizonMs"]):
            raise ValueError("Model returned invalid observation values")
        if target[1] > .5:
            window_errors.append(float(output[2] - target[2])); window_widths.append(float(output[3]))
        observation_count += 1
        identity_correct += int((output[0] >= confidence) == (target[0] > .5))
        last = recording["frames"][indices[-1]]["captureTicks"]
        labels = training.read_json(directory / "labels.json")
        matching = [shot for shot in labels["shots"] if shot["startTicks"] <= last <= shot["endTicks"]]
        if len(matching) != 1:
            raise ValueError("Evaluation intervals must not overlap")
        shot = matching[0]
        key = (recording["sessionId"], shot["shotId"])
        last_times[key] = last
        # Offline frames have no live transport age. Live freshness/deadline compliance is evaluated separately.
        if key not in accepted and min(output[0], output[1]) >= confidence and output[3] <= uncertainty:
            error = float(output[2] - target[2]) if shot["isTargetShot"] else None
            accepted[key] = {"sessionId": key[0], "shotId": key[1], "captureTicks": last,
                             "signedPredictionErrorMs": error, "confidence": float(min(output[0], output[1])),
                             "uncertaintyMs": float(output[3]), "isTargetShot": shot["isTargetShot"]}
    target_shots, negative_shots, accepted_targets, false_releases = 0, 0, 0, 0
    errors = []
    capture_configuration = None
    tracked_inputs = manifest.get("inputRegionPolicy") in ("visual-anchors-v1", "identity-pose-v1")
    for directory, recording in sessions:
        configuration = (recording["captureWidth"], recording["captureHeight"], recording["crop"])
        if not tracked_inputs and capture_configuration is not None and configuration != capture_configuration:
            raise ValueError("One report must use a single capture size and player crop")
        capture_configuration = configuration
        labels = training.read_json(directory / "labels.json")
        for shot in labels["shots"]:
            key = (recording["sessionId"], shot["shotId"])
            if shot["isTargetShot"]:
                target_shots += 1
                if key in accepted:
                    accepted_targets += 1
                    errors.append(abs(accepted[key]["signedPredictionErrorMs"]))
            else:
                negative_shots += 1
                false_releases += int(key in accepted)
    if not target_shots or not negative_shots or not observation_count:
        raise ValueError("Evaluation requires target and negative intervals with usable temporal samples")
    errors.sort()
    if source_files(loaded) != evaluation_sources or footage_index(sessions, min(4, manifest["historyLength"])) != evaluation_footage:
        raise ValueError("Evaluation source files changed during inference; no report is saved")
    report = {
        "schemaVersion": 1, "datasetId": thresholds["datasetId"], "split": args.split,
        "executionProvider": target_provider,
        "modelSha256": actual_hash, "modelVersion": manifest["modelVersion"], "animationProfile": manifest["animationProfile"],
        "gameVersion": manifest["gameVersion"], "cameraConfiguration": manifest["cameraConfiguration"],
        "calibrationProcedureVersion": thresholds["calibrationProcedureVersion"],
        "meterOff": thresholds.get("meterOff") is True, "noPlayerIconRequired": thresholds.get("noPlayerIconRequired") is True,
        "thresholdsLockedBeforeEvaluation": thresholds.get("lockedBeforeFinalEvaluation") is True,
        "minimumConfidence": confidence, "maximumUncertaintyMs": uncertainty, "maximumFrameAgeMs": frame_age_limit,
        "targetShots": target_shots, "negativeShots": negative_shots, "evaluatedPredictions": accepted_targets,
        "targetCoverage": accepted_targets / target_shots, "identityAccuracy": identity_correct / observation_count,
        "predictionP99ErrorMs": errors[max(0, math.ceil(.99 * len(errors)) - 1)] if errors else None,
        "falseReleaseRate": false_releases / negative_shots,
        "captureWidth": capture_configuration[0], "captureHeight": capture_configuration[1], "playerCrop": None if tracked_inputs else capture_configuration[2], "inputRegionPolicy": manifest.get("inputRegionPolicy", "fixed-region-v1"),
        "hardwareValidated": False, "hardwareDescription": "", "remotePlayClient": "",
        "hardwareSessionMinutes": 0, "hardwareAttempts": 0, "deadlineMissRate": 0
    }
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "accuracy-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    provenance = {"schemaVersion": 1, "datasetId": thresholds["datasetId"], "cameraConfiguration": recorded_camera,
                  "targetExecutionProvider": target_provider, "referenceExecutionProvider": "CPUExecutionProvider",
                  "sessionIds": sorted(session_ids), "sourceFiles": evaluation_sources, "legacyCameraAssumptionSessionIds": sorted(
                      session["sessionId"] for _, session in sessions
                      if "shotProfile" not in session or "cameraView" not in session["shotProfile"])}
    (args.out / "evaluation-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    metrics = {"schemaVersion": 1, "split": args.split, "modelSha256": actual_hash,
               "modelVersion": manifest["modelVersion"], "cpuThreads": args.cpu_threads, "warmupInferences": 8,
               "modelBytes": model_path.stat().st_size, **summarize(window_errors, window_widths, cpu_inference_ms, semantics),
               "targetShotCoverage": report["targetCoverage"], "falseReleaseRate": report["falseReleaseRate"],
               "hardwareValidated": False, "promotionApproved": False,
               "limitations": "Overlapping windows are descriptive samples, not independent confidence intervals. CPU timing excludes preprocessing/capture/transfers and does not certify Windows GPU or console delivery."}
    (args.out / "experiment-metrics.json").write_text(json.dumps(metrics, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    with (args.out / "predictions.jsonl").open("w", encoding="utf-8") as output:
        for prediction in accepted.values():
            output.write(json.dumps(prediction) + "\n")
    print(json.dumps(report, indent=2))
    print(f"Offline CPU numerical reference only; declared live target={target_provider}. hardwareValidated=false; no live provider latency is certified.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, OSError, KeyError, ImportError) as error:
        print(f"FAILURE {error}", file=sys.stderr)
        raise SystemExit(2)
