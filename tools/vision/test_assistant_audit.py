"""Assistant audits reuse production provenance checks; synthetic pixels establish no model quality."""
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import unittest
import test_dataset_validation as fixtures

ROOT = Path(__file__).resolve().parents[2] / "games/nba2k27/training"
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location("assistant_audit", ROOT / "Audit-VisionDataset.py")
audit_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit_module)


class AssistantAuditTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.DatasetValidationTests()
        self.fixture.setUp()
        self.fixture.load([self.fixture.target, self.fixture.negative])

    def tearDown(self):
        self.fixture.tearDown()

    def test_report_is_descriptive_and_does_not_rewrite_labels(self):
        labels = self.fixture.directory / "labels.json"
        original = labels.read_bytes()
        report = audit_module.audit([self.fixture.directory])
        self.assertEqual(report["sessionCount"], 1)
        self.assertGreater(report["temporalWindows"], 0)
        self.assertFalse(report["hardwareValidated"])
        self.assertFalse(report["promotionApproved"])
        self.assertEqual(labels.read_bytes(), original)

    def test_held_out_is_not_exposed_for_assistant_tuning(self):
        path = self.fixture.directory / "labels.json"
        labels = json.loads(path.read_text()); labels["split"] = "held-out"
        path.write_text(json.dumps(labels))
        with self.assertRaisesRegex(ValueError, "held-out.*reserved"):
            audit_module.audit([self.fixture.directory])

    def test_unreviewed_labels_keep_the_existing_guard(self):
        path = self.fixture.directory / "labels.json"
        labels = json.loads(path.read_text()); labels["shots"][0]["reviewed"] = False
        path.write_text(json.dumps(labels))
        with self.assertRaisesRegex(ValueError, "human-reviewed"):
            audit_module.audit([self.fixture.directory])

    def test_renamed_cross_split_footage_is_rejected(self):
        other = self.fixture.directory / "copied-session"
        other.mkdir()
        for name in ("session.json", "labels.json", "frames.bgra"):
            shutil.copyfile(self.fixture.directory / name, other / name)
        for name in ("session.json", "labels.json"):
            path = other / name; data = json.loads(path.read_text()); data["sessionId"] = "renamed"
            if name == "labels.json": data["split"] = "validation"
            path.write_text(json.dumps(data))
        with self.assertRaises(ValueError):
            audit_module.audit([self.fixture.directory, other])
