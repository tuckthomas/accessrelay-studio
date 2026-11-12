"""Offline dataset integrity checks; synthetic metadata/pixels are never gameplay evidence."""
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
# This file is tools/vision/test_dataset_validation.py: repository root is parents[2].
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location("vision_training", SCRIPTS / "Train-VisionModel.py")
training = importlib.util.module_from_spec(spec)
spec.loader.exec_module(training)
evaluation_spec = importlib.util.spec_from_file_location("vision_evaluation", SCRIPTS / "Evaluate-VisionModel.py")
evaluation = importlib.util.module_from_spec(evaluation_spec)
evaluation_spec.loader.exec_module(evaluation)


class DatasetValidationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="accessrelay-vision-label-check-")
        self.directory = Path(self.temporary.name)
        raw = bytes([10, 20, 30, 255]) * 256 * 256
        (self.directory / "frames.bgra").write_bytes(raw * 12)
        frames = [{"sequence": index + 1, "captureTicks": 100000 + index * 10000, "file": "frames.bgra",
                   "offsetBytes": index * len(raw), "byteLength": len(raw)} for index in range(12)]
        self.session = {"schemaVersion": 1, "sessionId": "synthetic-integrity-check", "shotProfileFingerprint": "a" * 64,
                        "complete": True, "stopwatchFrequency": 1000000, "pixelFormat": "BGRA8", "storedWidth": 256,
                        "storedHeight": 256, "storedStride": 1024, "frames": frames,
                        "shotProfile": {"cameraView": "2K"}}
        self.target = {"shotId": "target", "reviewed": True, "startTicks": 100000, "endTicks": 150000,
                       "targetReleaseTicks": 140000, "isTargetShot": True, "playerIdentityConfirmed": True}
        self.negative = {"shotId": "negative", "reviewed": True, "startTicks": 160000, "endTicks": 210000,
                         "isTargetShot": False, "playerIdentityConfirmed": False}
        (self.directory / "session.json").write_text(json.dumps(self.session), encoding="utf-8")

    def tearDown(self):
        self.temporary.cleanup()

    def load(self, shots, basis="captured-target-game-phase", assume_legacy_camera_2k=False):
        labels = {"schemaVersion": 1, "sessionId": self.session["sessionId"], "split": "train",
                  "labelTimeBasis": basis, "shots": shots}
        (self.directory / "labels.json").write_text(json.dumps(labels), encoding="utf-8")
        return training.load_sessions([self.directory], "train", 2, 10, assume_legacy_camera_2k)

    def test_disjoint_reviewed_intervals_generate_temporal_samples(self):
        sessions, samples, ids, fingerprint, camera = self.load([self.negative, self.target])
        self.assertEqual(len(sessions), 1)
        self.assertGreater(len(samples), 0)
        self.assertEqual(ids, {self.session["sessionId"]})
        self.assertEqual(fingerprint, "a" * 64)
        self.assertEqual(camera, "2K")

    def test_target_negative_overlap_rejected_before_samples(self):
        negative = self.negative | {"startTicks": 130000}
        with self.assertRaisesRegex(ValueError, "overlap"):
            self.load([self.target, negative])

    def test_shared_inclusive_endpoint_rejected(self):
        negative = self.negative | {"startTicks": 150000}
        with self.assertRaisesRegex(ValueError, "inclusive endpoint"):
            self.load([negative, self.target])

    def test_nested_interval_rejected(self):
        nested = self.negative | {"startTicks": 110000, "endTicks": 120000}
        with self.assertRaisesRegex(ValueError, "overlap"):
            self.load([nested, self.target])

    def test_physical_input_time_basis_rejected(self):
        with self.assertRaisesRegex(ValueError, "captured target game phase"):
            self.load([self.target, self.negative], basis="physical-button-up")

    def test_unreviewed_interval_rejected(self):
        with self.assertRaisesRegex(ValueError, "human-reviewed"):
            self.load([self.target | {"reviewed": False}, self.negative])

    def test_all_three_supported_cameras_are_preserved(self):
        for camera in ("2K", "2K Low", "2K Legacy"):
            with self.subTest(camera=camera):
                self.session["shotProfile"]["cameraView"] = camera
                (self.directory / "session.json").write_text(json.dumps(self.session), encoding="utf-8")
                self.assertEqual(self.load([self.target, self.negative])[4], camera)

    def test_unsupported_and_malformed_camera_values_are_rejected(self):
        for camera in ("Broadcast", "2k", "2K low", "2K ", "", None, 1, []):
            with self.subTest(camera=camera):
                self.session["shotProfile"]["cameraView"] = camera
                (self.directory / "session.json").write_text(json.dumps(self.session), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "cameraView"):
                    self.load([self.target, self.negative])

    def test_legacy_camera_requires_explicit_reviewed_2k_assumption(self):
        del self.session["shotProfile"]["cameraView"]
        (self.directory / "session.json").write_text(json.dumps(self.session), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "assume-legacy-camera-2k"):
            self.load([self.target, self.negative])
        self.assertEqual(self.load([self.target, self.negative], assume_legacy_camera_2k=True)[4], "2K")

    def test_mixed_camera_sessions_are_rejected_even_with_same_claimed_fingerprint(self):
        self.load([self.target, self.negative])
        second = self.directory / "second"
        second.mkdir()
        shutil.copyfile(self.directory / "frames.bgra", second / "frames.bgra")
        metadata = self.session | {"sessionId": "other-camera", "shotProfile": {"cameraView": "2K Low"}}
        (second / "session.json").write_text(json.dumps(metadata), encoding="utf-8")
        labels = {"schemaVersion": 1, "sessionId": "other-camera", "split": "train",
                  "labelTimeBasis": "captured-target-game-phase", "shots": [self.target, self.negative]}
        (second / "labels.json").write_text(json.dumps(labels), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Mixed recorded camera"):
            training.load_sessions([self.directory, second], "train", 2, 10)

    def test_requested_camera_cannot_relabel_training_or_validation(self):
        self.assertEqual(training.select_training_camera("2K Low", "2K Low", None), "2K Low")
        self.assertEqual(training.select_training_camera("2K Legacy", "2K Legacy", "2K Legacy"), "2K Legacy")
        with self.assertRaisesRegex(ValueError, "--camera must match"):
            training.select_training_camera("2K Low", "2K Low", "2K")
        with self.assertRaisesRegex(ValueError, "same supported"):
            training.select_training_camera("2K Low", "2K Legacy", None)

    def test_conflicting_top_level_camera_metadata_rejected(self):
        self.session["cameraConfiguration"] = "2K Legacy"
        (self.directory / "session.json").write_text(json.dumps(self.session), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Top-level camera metadata differs"):
            self.load([self.target, self.negative])

    def test_top_level_camera_view_must_match_profile_camera(self):
        self.session["cameraView"] = "2K Low"
        (self.directory / "session.json").write_text(json.dumps(self.session), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Top-level camera metadata differs"):
            self.load([self.target, self.negative])

    def test_legacy_assumption_cannot_override_explicit_top_level_camera(self):
        del self.session["shotProfile"]["cameraView"]
        for field in ("cameraView", "cameraConfiguration"):
            with self.subTest(field=field):
                metadata = self.session | {field: "2K Legacy"}
                (self.directory / "session.json").write_text(json.dumps(metadata), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Top-level camera metadata differs"):
                    self.load([self.target, self.negative], assume_legacy_camera_2k=True)

    def test_both_live_provider_targets_must_match_locked_thresholds(self):
        for provider in ("CPUExecutionProvider", "CUDAExecutionProvider"):
            with self.subTest(provider=provider):
                self.assertEqual(evaluation.select_target_provider(provider, {"executionProvider": provider}), provider)

    def test_legacy_thresholds_without_provider_are_not_silently_accepted(self):
        with self.assertRaisesRegex(ValueError, "Locked thresholds must declare executionProvider"):
            evaluation.select_target_provider("CUDAExecutionProvider", {})

    def test_cpu_reference_cannot_relabel_a_locked_cuda_live_target(self):
        with self.assertRaisesRegex(ValueError, "matching --target-provider"):
            evaluation.select_target_provider("CPUExecutionProvider", {"executionProvider": "CUDAExecutionProvider"})

    def test_unknown_provider_alias_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Target provider"):
            evaluation.select_target_provider("cuda", {"executionProvider": "cuda"})


if __name__ == "__main__":
    unittest.main()
