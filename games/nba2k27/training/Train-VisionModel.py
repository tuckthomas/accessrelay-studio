#!/usr/bin/env python3
"""Offline only: train/export the fixed temporal contract or create an untrained diagnostic fixture.

No Python process is used by AccessRelay's production capture/inference loop.
An exported model always has validation=null and cannot arm automatic release.
targetReleaseTicks labels the captured target GAME PHASE, never physical button-up
or virtual-controller send time: production compensates video/input delays separately.
Raw enriched captureEvidence sessions are acquisition artifacts. Only the explicit
reviewed conversion workflow admits overlay-disjoint player crops and recorded
teacher/student configuration mapping; original evidence remains integrity-bound.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import random
import sys

from vision_preprocessing import PREPROCESSING_ID, resize_stored_bgra
from footage_integrity import footage_index, validate_independent
from evidence_conversion import verify_conversion

CAMERA_VIEWS = ("2K", "2K Low", "2K Legacy")


def validate_session_splits(training_ids: set[str], validation_ids: set[str]) -> None:
    if not training_ids or not validation_ids:
        raise ValueError("Training and validation require nonempty capture session groups")
    if training_ids & validation_ids:
        raise ValueError("Train/validation capture sessions overlap")


def read_json(path: Path) -> dict:
    if path.stat().st_size > 16 * 1024 * 1024:
        raise ValueError(f"JSON metadata is too large: {path}")
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict) or value.get("schemaVersion") != 1:
        raise ValueError(f"Expected a schemaVersion=1 object: {path}")
    return value


def finite(value, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value):
        raise ValueError(f"{name} must be a finite number")
    return float(value)


def child_file(directory: Path, relative: str) -> Path:
    path = (directory / relative).resolve()
    if not path.is_relative_to(directory.resolve()):
        raise ValueError("Dataset files must stay inside their session folder")
    return path


def validate_non_overlapping_intervals(shots: list[dict]) -> None:
    intervals = []
    for shot in shots:
        start = finite(shot.get("startTicks"), "startTicks")
        end = finite(shot.get("endTicks"), "endTicks")
        if end <= start:
            raise ValueError("A shot interval must have positive duration")
        intervals.append((start, end, shot.get("shotId", "unnamed")))
    intervals.sort(key=lambda interval: (interval[0], interval[1]))
    for previous, current in zip(intervals, intervals[1:]):
        # Interval endpoints are inclusive in both the review UI and evaluation policy.
        if current[0] <= previous[1]:
            raise ValueError(f"Reviewed intervals overlap or share an inclusive endpoint: {previous[2]} / {current[2]}")


def recorded_camera(session: dict, assume_legacy_camera_2k: bool = False) -> str:
    profile = session.get("shotProfile")
    if "shotProfile" not in session or (isinstance(profile, dict) and "cameraView" not in profile):
        if assume_legacy_camera_2k:
            camera = "2K"
        else:
            raise ValueError("Legacy dataset has no shotProfile.cameraView; review it and explicitly pass --assume-legacy-camera-2k only if it used 2K")
    else:
        if not isinstance(profile, dict) or profile.get("cameraView") not in CAMERA_VIEWS:
            raise ValueError("Recorded cameraView must be exactly 2K, 2K Low, or 2K Legacy")
        camera = profile["cameraView"]
    for field in ("cameraView", "cameraConfiguration"):
        if field in session and session[field] != camera:
            raise ValueError("Top-level camera metadata differs from shotProfile.cameraView or the explicit legacy 2K assumption")
    return camera


def select_training_camera(training_camera: str, validation_camera: str, requested_camera: str | None) -> str:
    if training_camera not in CAMERA_VIEWS or validation_camera not in CAMERA_VIEWS or training_camera != validation_camera:
        raise ValueError("Training and validation must use the same supported recorded camera view")
    if requested_camera is not None and requested_camera != training_camera:
        raise ValueError("--camera must match the recorded shotProfile.cameraView; it cannot relabel the data")
    return training_camera


def load_sessions(directories: list[Path], split: str, history: int, interval_ms: float,
                  assume_legacy_camera_2k: bool = False):
    sessions, samples, ids = [], [], set()
    fingerprint = None
    camera = None
    for directory in directories:
        session = read_json(directory / "session.json")
        # Conversion keeps the marker and verifies original bytes, purpose, review,
        # overlay exclusion and configuration mapping. Raw evidence still fails closed.
        if "trainingConversion" in session:
            verify_conversion(directory, session)
        elif any((directory / name).exists() for name in ("source-session.json", "conversion-review.json")):
            raise ValueError("Converted proof files exist but conversion provenance markers were removed")
        elif "captureEvidence" in session:
            raise ValueError(
                "Enriched captureEvidence sessions are not supported by the current trainer/evaluator: "
                "they need reviewed label conversion, meter-leakage-safe preprocessing and explicit "
                "teacher/student profile mapping. Preserve the original evidence; do not remove its marker."
            )
        labels = read_json(directory / "labels.json")
        if labels.get("labelTimeBasis", "captured-target-game-phase") != "captured-target-game-phase":
            raise ValueError("Target labels must mark the captured target game phase, not physical/virtual input timestamps")
        if not session.get("complete"):
            raise ValueError(f"Refusing incomplete take: {directory}")
        if session.get("sessionId") != labels.get("sessionId") or labels.get("split") != split:
            raise ValueError(f"Session identity/split differs from labels: {directory}")
        if session["sessionId"] in ids:
            raise ValueError("A session was provided twice")
        ids.add(session["sessionId"])
        current_camera = recorded_camera(session, assume_legacy_camera_2k)
        if camera is not None and current_camera != camera:
            raise ValueError("Mixed recorded camera views are not allowed in one dataset/model")
        camera = current_camera
        current = session.get("shotProfileFingerprint")
        if not current or (fingerprint is not None and fingerprint != current):
            raise ValueError("Each exported model must use one exact shot-profile configuration")
        fingerprint = current
        frequency = finite(session.get("stopwatchFrequency"), "stopwatchFrequency")
        if frequency <= 0 or session.get("pixelFormat") != "BGRA8":
            raise ValueError("Expected QPC timestamps and packed BGRA8 data")
        width, height = session.get("storedWidth"), session.get("storedHeight")
        stride = session.get("storedStride")
        if width != 256 or height != 256 or stride != 1024:
            raise ValueError("Canonical v2 training requires recorded 256x256 BGRA frames with stride 1024")
        frames = session.get("frames", [])
        if not frames or len(frames) > 10000:
            raise ValueError("A training take needs 1–10000 recorded frames")
        previous = -1
        for frame in frames:
            timestamp = finite(frame.get("captureTicks"), "captureTicks")
            if timestamp <= previous:
                raise ValueError("Recorded frame timestamps must increase")
            previous = timestamp
            path = child_file(directory, frame.get("file", ""))
            offset, count = frame.get("offsetBytes"), frame.get("byteLength")
            if not isinstance(offset, int) or offset < 0 or count != stride * height or offset + count > path.stat().st_size:
                raise ValueError("A recorded frame points outside its BGRA data file")
        session_index = len(sessions)
        sessions.append((directory, session))
        validate_non_overlapping_intervals(labels.get("shots", []))
        shot_ids = set()
        for shot in labels.get("shots", []):
            shot_id = shot.get("shotId")
            if not shot_id or shot_id in shot_ids:
                raise ValueError("Each labeled shot needs a unique shotId")
            shot_ids.add(shot_id)
            if shot.get("reviewed") is not True:
                raise ValueError("Every training interval must be human-reviewed")
            start, end = finite(shot.get("startTicks"), "startTicks"), finite(shot.get("endTicks"), "endTicks")
            if end <= start:
                raise ValueError("A shot interval must have positive duration")
            is_target = shot.get("isTargetShot") is True
            identity = shot.get("playerIdentityConfirmed") is True
            target = finite(shot.get("targetReleaseTicks"), "targetReleaseTicks") if is_target else None
            if is_target and (not identity or not start <= target <= end):
                raise ValueError("Target shots need confirmed identity and a reviewed target inside the interval")
            for last_index in range(history - 1, len(frames)):
                last = frames[last_index]["captureTicks"]
                if not start <= last <= end or (is_target and last > target):
                    continue
                selected = [last_index]
                cursor = last_index
                for _ in range(history - 1):
                    wanted = frames[cursor]["captureTicks"] - interval_ms * frequency / 1000
                    while cursor > 0 and frames[cursor - 1]["captureTicks"] > wanted:
                        cursor -= 1
                    cursor -= 1
                    if cursor < 0:
                        break
                    selected.append(cursor)
                if len(selected) != history:
                    continue
                selected.reverse()
                intervals = [(frames[b]["captureTicks"] - frames[a]["captureTicks"]) * 1000 / frequency for a, b in zip(selected, selected[1:])]
                if any(gap < .85 * interval_ms or gap > 2.5 * interval_ms for gap in intervals):
                    continue
                horizon = (target - last) * 1000 / frequency if is_target else 0.0
                if horizon > 2000:
                    continue
                phase = (last - start) / max(1, target - start) if is_target else 0.0
                samples.append((session_index, selected, [float(identity), float(is_target), horizon, 0.0, min(1.0, phase)]))
    if not samples:
        raise ValueError(f"No usable reviewed temporal windows in {split} data")
    return sessions, samples, ids, fingerprint, camera


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--train", type=Path, nargs="+")
    parser.add_argument("--validation", type=Path, nargs="+")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--diagnostic-fixture", action="store_true", help="Export random, untrained weights for GPU compatibility checks; cannot arm")
    parser.add_argument("--animation-profile", default="diagnostic-untrained")
    parser.add_argument("--game-version", default="unvalidated")
    parser.add_argument("--camera", choices=CAMERA_VIEWS, default=None,
                        help="Optional consistency check; trained models derive this from recorded shotProfile.cameraView")
    parser.add_argument("--assume-legacy-camera-2k", action="store_true",
                        help="Explicitly treat old datasets missing cameraView as 2K after reviewing their camera provenance")
    parser.add_argument("--width", type=int, default=160)
    parser.add_argument("--height", type=int, default=160)
    parser.add_argument("--history", type=int, default=4)
    parser.add_argument("--interval-ms", type=float, default=16.6666667)
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--seed", type=int, default=20261111)
    parser.add_argument("--device", default="cpu", choices=("cpu", "cuda"))
    args = parser.parse_args()
    if any((args.out / name).exists() for name in ("animation-model.onnx", "manifest.json", "training-run.json")):
        raise ValueError("Choose a fresh output directory; existing model/provenance files are never overwritten")
    if not 32 <= args.width <= 512 or not 32 <= args.height <= 512 or not 2 <= args.history <= 16 or not 4 <= args.interval_ms <= 100:
        raise ValueError("Unsupported model shape/history interval")
    if not 1 <= args.epochs <= 1000 or not 1 <= args.batch_size <= 128:
        raise ValueError("Epoch/batch limits exceeded")
    import numpy as np
    import torch
    from torch import nn
    from torch.nn import functional as functional
    import onnx

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    if args.device == "cuda" and not torch.cuda.is_available():
        raise ValueError("This offline PyTorch installation has no available CUDA device; choose cpu or install a matching offline training environment")

    class TemporalReleaseModel(nn.Module):
        def __init__(self):
            super().__init__()
            self.features = nn.Sequential(
                nn.Conv3d(3, 8, 3, stride=(1, 2, 2), padding=1), nn.ReLU(),
                nn.Conv3d(8, 16, 3, stride=(1, 2, 2), padding=1), nn.ReLU(),
                nn.Conv3d(16, 24, 3, stride=(1, 2, 2), padding=1), nn.ReLU())
            self.head = nn.Linear(24, 5)

        def forward(self, frames):
            features = self.features(frames.permute(0, 2, 1, 3, 4)).mean(dim=(2, 3, 4))
            output = self.head(features).sigmoid()
            # Fixed NTCHW float32 input; [player, shot, horizon-ms, uncertainty-ms, phase].
            return torch.cat((output[:, :2], output[:, 2:4] * 2000.0, output[:, 4:5]), dim=1)

    model = TemporalReleaseModel().to(args.device)
    training_log = []
    dataset_fingerprint = None
    training_session_ids, validation_session_ids = [], []
    training_footage, validation_footage = None, None
    legacy_camera_session_ids = []
    resolved_camera = args.camera or "2K"
    if not args.diagnostic_fixture:
        if not args.train or not args.validation:
            raise ValueError("Training requires distinct --train and --validation session directories")
        train = load_sessions(args.train, "train", args.history, args.interval_ms, args.assume_legacy_camera_2k)
        validation = load_sessions(args.validation, "validation", args.history, args.interval_ms, args.assume_legacy_camera_2k)
        resolved_camera = select_training_camera(train[4], validation[4], args.camera)
        validate_session_splits(train[2], validation[2])
        training_footage = footage_index(train[0], min(4, args.history))
        validation_footage = footage_index(validation[0], min(4, args.history))
        validate_independent(training_footage, validation_footage)
        if train[3] != validation[3]:
            raise ValueError("Train/validation sessions use different shot-profile configurations")
        dataset_fingerprint = train[3]
        training_session_ids, validation_session_ids = sorted(train[2]), sorted(validation[2])
        legacy_camera_session_ids = sorted(session["sessionId"] for _, session in train[0] + validation[0]
                                           if "shotProfile" not in session or "cameraView" not in session["shotProfile"])
        args.animation_profile = "nba2k27:" + dataset_fingerprint

        class Windows(torch.utils.data.Dataset):
            def __init__(self, loaded):
                self.sessions, self.samples = loaded[:2]

            def __len__(self):
                return len(self.samples)

            def __getitem__(self, index):
                session_index, indices, target = self.samples[index]
                directory, session = self.sessions[session_index]
                tensors = []
                for frame_index in indices:
                    frame = session["frames"][frame_index]
                    with child_file(directory, frame["file"]).open("rb") as source:
                        source.seek(frame["offsetBytes"])
                        raw = source.read(frame["byteLength"])
                    bgra = np.frombuffer(raw, dtype=np.uint8).reshape(session["storedHeight"], session["storedWidth"], 4)
                    # Stored recorder frames are already player-cropped. Never crop them a second time.
                    tensors.append(torch.from_numpy(resize_stored_bgra(bgra, args.height, args.width)))
                return torch.stack(tensors), torch.tensor(target, dtype=torch.float32)

        loader = torch.utils.data.DataLoader(Windows(train), batch_size=args.batch_size, shuffle=True, num_workers=0)
        validation_loader = torch.utils.data.DataLoader(Windows(validation), batch_size=args.batch_size, shuffle=False, num_workers=0)
        optimizer = torch.optim.AdamW(model.parameters(), lr=.001)
        for epoch in range(args.epochs):
            model.train()
            total = 0.0
            for frames, target in loader:
                frames, target = frames.to(args.device), target.to(args.device)
                output = model(frames)
                positive = target[:, 1]
                confidence_loss = functional.binary_cross_entropy(output[:, :2], target[:, :2])
                timing = functional.smooth_l1_loss(output[:, 2] / 1000, target[:, 2] / 1000, reduction="none")
                uncertainty_target = (output[:, 2].detach() - target[:, 2]).abs()
                uncertainty = functional.smooth_l1_loss(output[:, 3] / 1000, uncertainty_target / 1000, reduction="none")
                phase = functional.smooth_l1_loss(output[:, 4], target[:, 4], reduction="none")
                loss = confidence_loss + ((timing * 4 + uncertainty + phase) * positive).sum() / positive.sum().clamp_min(1)
                optimizer.zero_grad(set_to_none=True)
                loss.backward()
                optimizer.step()
                total += loss.item() * len(frames)
            model.eval()
            errors = []
            with torch.inference_mode():
                for frames, target in validation_loader:
                    output = model(frames.to(args.device)).cpu()
                    mask = target[:, 1] > .5
                    errors.extend((output[mask, 2] - target[mask, 2]).abs().tolist())
            entry = {"epoch": epoch + 1, "trainingLoss": total / len(loader.dataset),
                     "validationTimingMAEms": float(np.mean(errors)) if errors else None}
            training_log.append(entry)
            print(json.dumps(entry), flush=True)

    args.out.mkdir(parents=True, exist_ok=True)
    model = model.cpu().eval()
    model_path = args.out / "animation-model.onnx"
    example = torch.zeros(1, args.history, 3, args.height, args.width, dtype=torch.float32)
    with torch.inference_mode():
        torch.onnx.export(model, (example,), str(model_path), input_names=["frames"], output_names=["observation"],
                          opset_version=17, dynamo=False, external_data=False)
    onnx.checker.check_model(onnx.load(str(model_path)))
    model_hash = hashlib.sha256(model_path.read_bytes()).hexdigest()
    manifest = {
        "schemaVersion": 1, "displayName": "UNTRAINED diagnostic fixture" if args.diagnostic_fixture else "Experimental temporal release candidate",
        "modelVersion": "diagnostic-untrained-v1" if args.diagnostic_fixture else "experimental-candidate-v1",
        "modelFile": model_path.name, "modelSha256": model_hash, "inputName": "frames", "outputName": "observation",
        "preprocessing": PREPROCESSING_ID, "inputWidth": args.width, "inputHeight": args.height,
        "historyLength": args.history, "historyIntervalMs": args.interval_ms, "maximumPredictionHorizonMs": 2000,
        "rgbMean": [0, 0, 0], "rgbStandardDeviation": [1, 1, 1], "animationProfile": args.animation_profile,
        "gameVersion": args.game_version, "cameraConfiguration": resolved_camera, "license": "0BSD (architecture code); recorded-data and trained-weight rights must be reviewed",
        "validation": None
    }
    (args.out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (args.out / "training-run.json").write_text(json.dumps({"schemaVersion": 1, "seed": args.seed,
        "diagnosticFixture": args.diagnostic_fixture, "validationPassed": False, "datasetFingerprint": dataset_fingerprint,
        "trainingSessionIds": training_session_ids, "validationSessionIds": validation_session_ids,
        "trainingFootage": training_footage, "validationFootage": validation_footage,
        "labelTimeBasis": "captured-target-game-phase",
        "cameraConfiguration": resolved_camera, "legacyCameraAssumptionSessionIds": legacy_camera_session_ids,
        "training": training_log, "modelSha256": model_hash,
        "limitations": "No held-out accuracy or Remote Play hardware timing result is supplied. This model cannot arm automatic release."}, indent=2) + "\n", encoding="utf-8")
    print(f"Exported {model_path}. validation=null; automatic release remains disabled.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, OSError, KeyError, ImportError) as error:
        print(f"FAILURE {error}", file=sys.stderr)
        raise SystemExit(2)
