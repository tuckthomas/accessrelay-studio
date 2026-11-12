#!/usr/bin/env python3
"""Stdlib-only audit of explicitly chosen train/validation sessions. No label edits or quality certification."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
from dataset_provenance import source_files
from footage_integrity import footage_index, validate_independent


def audit(directories):
    if not 1 <= len(directories) <= 8:
        raise ValueError("Choose 1-8 registered sessions per audit")
    root = Path(__file__).resolve().parent
    spec = importlib.util.spec_from_file_location("audit_training", root / "Train-VisionModel.py")
    training = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(training)
    groups = {"train": [], "validation": []}
    sources, cameras, builds = [], set(), set()
    target, negative, windows, frame_bytes = 0, 0, 0, 0
    for directory in directories:
        labels = training.read_json(directory / "labels.json")
        split = labels.get("split")
        if split not in groups:
            raise ValueError("Assistant audits accept train/validation only; held-out data is reserved")
        loaded = training.load_sessions([directory], split, 4, 16.6666667)
        groups[split].extend(loaded[0]); sources.extend(source_files(loaded)); cameras.add(loaded[4])
        windows += len(loaded[1])
        for _, session in loaded[0]:
            frame_bytes += sum(frame["byteLength"] for frame in session["frames"])
            if frame_bytes > 1024 ** 3:
                raise ValueError("Audit canonical player footage exceeds the 1 GiB read budget")
            builds.add(str(session.get("recordedGameBuild", "unknown"))[:120])
        target += sum(shot.get("isTargetShot") is True for shot in labels.get("shots", []))
        negative += sum(shot.get("isTargetShot") is not True for shot in labels.get("shots", []))
    indexes = {split: footage_index(sessions) for split, sessions in groups.items() if sessions}
    if "train" in indexes and "validation" in indexes:
        validate_independent(indexes["train"], indexes["validation"])
    identity = hashlib.sha256(json.dumps({"sources": sorted(sources, key=lambda item: item["sessionId"]), "footage": indexes}, sort_keys=True).encode()).hexdigest()
    return {"schemaVersion": 1, "sessionCount": len(sources), "splitCounts": {split: len(items) for split, items in groups.items()},
            "temporalWindows": windows, "targetIntervals": target, "negativeIntervals": negative,
            "cameraViews": sorted(cameras), "gameBuilds": sorted(builds), "sourceCorpusSha256": identity,
            "labelsModified": False, "heldOutEvaluated": False, "hardwareValidated": False, "promotionApproved": False,
            "limitations": ["Human-reviewed labels are checked structurally, not certified as correct.",
                            "Exact RGB reuse guards do not prove independence after transforms.",
                            "Coverage describes these sessions; no universal missing-condition or accuracy claim is inferred."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sessions", type=Path, nargs="+", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.out.exists():
        raise ValueError("Choose a fresh audit output directory")
    report = audit(args.sessions)
    args.out.mkdir(parents=True)
    (args.out / "audit.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("Audit completed. No labels changed and no model/hardware acceptance was certified.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, ImportError) as error:
        print("FAILURE", str(error))
        raise SystemExit(2)
