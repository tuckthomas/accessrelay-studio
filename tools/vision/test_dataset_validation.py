"""Offline dataset integrity checks; synthetic metadata/pixels are never gameplay evidence."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
# This file is tools/vision/test_dataset_validation.py: repository root is parents[2].
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location("vision_training", SCRIPTS / "Train-VisionModel.py")
training = importlib.util.module_from_spec(spec)
spec.loader.exec_module(training)


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
                        "storedHeight": 256, "storedStride": 1024, "frames": frames}
        self.target = {"shotId": "target", "reviewed": True, "startTicks": 100000, "endTicks": 150000,
                       "targetReleaseTicks": 140000, "isTargetShot": True, "playerIdentityConfirmed": True}
        self.negative = {"shotId": "negative", "reviewed": True, "startTicks": 160000, "endTicks": 210000,
                         "isTargetShot": False, "playerIdentityConfirmed": False}
        (self.directory / "session.json").write_text(json.dumps(self.session), encoding="utf-8")

    def tearDown(self):
        self.temporary.cleanup()

    def load(self, shots, basis="captured-target-game-phase"):
        labels = {"schemaVersion": 1, "sessionId": self.session["sessionId"], "split": "train",
                  "labelTimeBasis": basis, "shots": shots}
        (self.directory / "labels.json").write_text(json.dumps(labels), encoding="utf-8")
        return training.load_sessions([self.directory], "train", 2, 10)

    def test_disjoint_reviewed_intervals_generate_temporal_samples(self):
        sessions, samples, ids, fingerprint = self.load([self.negative, self.target])
        self.assertEqual(len(sessions), 1)
        self.assertGreater(len(samples), 0)
        self.assertEqual(ids, {self.session["sessionId"]})
        self.assertEqual(fingerprint, "a" * 64)

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


if __name__ == "__main__":
    unittest.main()
