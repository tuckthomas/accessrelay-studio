"""Synthetic pixels prove reuse guards, never gameplay accuracy."""
import copy
from pathlib import Path
import sys
import tempfile
import unittest

TRAINING = Path(__file__).resolve().parents[2] / "games/nba2k27/training"
sys.path.insert(0, str(TRAINING))
from footage_integrity import footage_index, validate_independent, validate_evaluation_footage, validate_index


class FootageIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="accessrelay-footage-")
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def clip(self, identity, colors, alpha=255):
        colors = list(colors)
        directory = self.root / identity; directory.mkdir()
        raw = b"".join(bytes([color, color, color, alpha]) * 256 * 256 for color in colors)
        (directory / "frames.bgra").write_bytes(raw)
        size = 256 * 256 * 4
        return [(directory, {"sessionId": identity, "frames": [{"file": "frames.bgra", "offsetBytes": i * size, "byteLength": size} for i in range(len(colors))]})]

    def test_distinct_footage_with_different_session_ids_passes(self):
        validate_independent(footage_index(self.clip("train", range(12))), footage_index(self.clip("validation", range(20, 32))))

    def test_relabeling_session_timestamps_and_alpha_cannot_hide_reuse(self):
        first = footage_index(self.clip("original", range(12)))
        second = footage_index(self.clip("renamed", range(12), alpha=0))
        with self.assertRaisesRegex(ValueError, "Duplicate RGB footage"):
            validate_independent(first, second)

    def test_reversed_and_partially_overlapping_clips_are_rejected(self):
        first = footage_index(self.clip("train", range(12)))
        for name, pixels in [("reversed", reversed(range(12))), ("overlap", range(8, 20)), ("resampled", range(0, 12, 2))]:
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Duplicate RGB footage"):
                validate_independent(first, footage_index(self.clip(name, pixels)))

    def test_duplicate_sessions_inside_one_group_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "different session IDs"):
            footage_index(self.clip("one", range(12)) + self.clip("two", range(12)))

    def test_identical_static_windows_also_fail(self):
        with self.assertRaisesRegex(ValueError, "Duplicate RGB footage"):
            validate_independent(footage_index(self.clip("static-one", [0] * 8)), footage_index(self.clip("static-two", [0] * 8)))

    def test_held_out_excludes_training_validation_and_configuration_references(self):
        training = footage_index(self.clip("training", range(12)))
        validation = footage_index(self.clip("validation", range(20, 32)))
        held = footage_index(self.clip("held-out", range(40, 52)))
        run = {"trainingSessionIds": ["training"], "validationSessionIds": ["validation"], "trainingFootage": training, "validationFootage": validation}
        validate_evaluation_footage("held-out", held, run)
        renamed = footage_index(self.clip("validation-renamed", range(20, 32)))
        with self.assertRaisesRegex(ValueError, "Duplicate RGB footage"):
            validate_evaluation_footage("held-out", renamed, run)
        training["configurationSessionIds"] = ["held-out"]
        with self.assertRaisesRegex(ValueError, "configuration reference"):
            validate_evaluation_footage("held-out", held, run)

    def test_missing_or_malformed_fingerprints_cannot_certify_old_candidates(self):
        current = footage_index(self.clip("current", range(12)))
        with self.assertRaisesRegex(ValueError, "provenance"):
            validate_evaluation_footage("held-out", current, {})
        for patch in [{"frameHashes": [{}]}, {"windowHashes": ["x" * 64]}, {"windowLength": 1}, {"sessionIds": []}]:
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                validate_index(current | patch)
        corrupted = copy.deepcopy(current); corrupted["frameHashes"] *= 2
        with self.assertRaises(ValueError): validate_index(corrupted)


if __name__ == "__main__": unittest.main()
