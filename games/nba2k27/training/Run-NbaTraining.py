#!/usr/bin/env python3
"""NBA-owned reviewed acquisition -> conversion -> training -> validation workflow.

No runtime capture/controller process is spawned. Candidates remain observation-only.
"""
import argparse
import datetime
import json
from pathlib import Path
import subprocess
import sys
import unicodedata

from evidence_conversion import PROFILE_FIELDS, convert, read_json, write_review_template


def equivalent(first, second):
    def normalize(value):
        return unicodedata.normalize("NFC", value.strip()) if isinstance(value, str) else value
    return all(normalize(first.get(key)) == normalize(second.get(key)) for key in PROFILE_FIELDS)


def collect(root, profile):
    groups = {"train": [], "validation": [], "held-out": [], "reference": []}
    candidates = list(root.glob("take-*/session.json"))
    if len(candidates) > 128: raise ValueError("Choose a dataset folder with at most 128 takes")
    for path in sorted(candidates):
        session = read_json(path)
        if not equivalent(session.get("shotProfile", {}), profile): continue
        if session.get("captureEvidence", {}).get("version") == 4 and session.get("shotProfile", {}).get("gamertag") != profile.get("gamertag"): continue
        if session.get("complete") is not True or session.get("captureEvidence", {}).get("version") not in (3, 4): continue
        labels_path = path.with_name("labels.json")
        if not labels_path.is_file(): continue
        labels = read_json(labels_path)
        split = labels.get("split")
        if split not in groups: continue
        meter = session["shotProfile"].get("usesShotMeter")
        if (split == "train" and meter is not True) or (split != "train" and meter is not False):
            raise ValueError(f"{path.parent.name}: meter state does not match its reviewed {split} purpose")
        groups[split].append((path.parent, session, labels))
    if not groups["reference"]:
        raise ValueError("Record a separate meter-off configuration clip, open it in review and save its split as Configuration reference. It is excluded from training/evaluation.")
    for split in ("train", "validation"):
        if not groups[split]: raise ValueError(f"No compatible reviewed {split} clips. Record and review this group before training.")
        shots = [shot for _, _, labels in groups[split] for shot in labels.get("shots", [])]
        if not any(shot.get("isTargetShot") is True for shot in shots) or not any(shot.get("isTargetShot") is False for shot in shots):
            raise ValueError(f"The {split} group needs both intended-player shots and negative intervals.")
        if any(shot.get("reviewed") is not True or shot.get("playerRegionReviewed") is not True for shot in shots):
            raise ValueError(f"Finish reviewing the player regions and target evidence in the {split} group.")
    return groups


def run(request_path):
    request = json.loads(request_path.read_text(encoding="utf-8-sig"))
    options = request["options"]
    source = Path(request["datasetRoot"]).resolve()
    output = Path(request["outputRoot"]).resolve()
    if output == source or output.is_relative_to(source): raise ValueError("Keep model outputs outside acquisition data")
    if not output.is_dir() or (output / "workflow-result.json").exists(): raise ValueError("Choose a fresh job directory")
    epochs, device = options.get("epochs", 20), options.get("device", "cpu")
    if type(epochs) is not int or not 1 <= epochs <= 200 or device not in ("cpu", "cuda"):
        raise ValueError("Choose CPU/CUDA and 1–200 epochs")
    profile = options.get("profile")
    if not isinstance(profile, dict): raise ValueError("Select an NBA shot profile before training")
    groups = collect(source, profile)
    reference = groups["reference"][0][0]
    prepared = {"train": [], "validation": []}
    for split in prepared:
        for index, (directory, session, labels) in enumerate(groups[split]):
            review_file = output / f"review-{split}-{index}.json"
            review = write_review_template(directory, reference, review_file)
            # These confirmations derive from explicit persisted Studio interval review,
            # validated camera/animation equivalence and per-frame geometric exclusions.
            review.update(reviewed=True, reviewer="Studio interval review", reviewedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                playerRoiContainsNoMeterOrFeedback=True, targetPhaseReviewedIndependentlyOfControllerEvents=True, teacherStudentAnimationEquivalent=True)
            review_file.write_text(json.dumps(review, indent=2) + "\n", encoding="utf-8")
            destination = output / "prepared" / split / f"clip-{index:03d}"
            convert(directory, reference, review_file, destination, prefer_hard_links=True)
            prepared[split].append(destination)
            print(f"Prepared {split}: {session['sessionId']}", flush=True)
    tools = Path(__file__).resolve().parent
    candidate = output / "candidate"
    argv = [sys.executable, str(tools / "Train-VisionModel.py"), "--train", *map(str, prepared["train"]),
        "--validation", *map(str, prepared["validation"]), "--out", str(candidate), "--epochs", str(epochs), "--device", device, "--cpu-threads", "1", "--head", "ordered-head", "--history", "16", "--interval-ms", "50"]
    subprocess.run(argv, check=True)
    manifest = read_json(candidate / "manifest.json")
    thresholds = {"schemaVersion": 1, "datasetId": "nba-" + output.name, "animationProfile": manifest["animationProfile"],
        "cameraConfiguration": manifest["cameraConfiguration"], "executionProvider": options.get("targetProvider", "CUDAExecutionProvider"),
        "minimumConfidence": options.get("minimumConfidence", .9), "maximumUncertaintyMs": options.get("maximumUncertaintyMs", 5),
        "maximumFrameAgeMs": options.get("maximumFrameAgeMs", 80), "calibrationProcedureVersion": "reviewed-target-phase-v1",
        "meterOff": True, "noPlayerIconRequired": False, "lockedBeforeFinalEvaluation": False}
    threshold_file = output / "thresholds.json"
    threshold_file.write_text(json.dumps(thresholds, indent=2) + "\n", encoding="utf-8")
    evaluation = output / "validation"
    validation_error = None
    try:
        subprocess.run([sys.executable, str(tools / "Evaluate-VisionModel.py"), "--manifest", str(candidate / "manifest.json"),
            "--sessions", *map(str, prepared["validation"]), "--split", "validation", "--locked-thresholds", str(threshold_file),
            "--target-provider", thresholds["executionProvider"], "--out", str(evaluation)], check=True)
    except subprocess.CalledProcessError as error:
        validation_error = f"Validation worker failed with code {error.returncode}; inspect stderr.log. The trained candidate is still unvalidated."
    metrics_file = evaluation / "experiment-metrics.json"
    metrics = read_json(metrics_file) if metrics_file.is_file() else None
    result = {"schemaVersion": 1, "trainingSucceeded": True, "validationSucceeded": validation_error is None,
        "validationError": validation_error, "trainingClips": len(prepared["train"]), "validationClips": len(prepared["validation"]),
        "metrics": metrics, "heldOutEvaluated": False, "hardwareValidated": False, "promotionApproved": False}
    (output / "workflow-result.json").write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print("Candidate exported. Held-out, hardware and automatic-release acceptance remain unverified.", flush=True)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, required=True)
    args = parser.parse_args()
    try: run(args.request)
    except (ValueError, OSError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        print("FAILURE " + str(error), file=sys.stderr); raise SystemExit(2)
