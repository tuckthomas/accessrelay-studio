"""Synthetic protocol checks. These do not establish gameplay or release accuracy."""
import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import test_evidence_conversion as fixtures
from evidence_conversion import convert, read_json, validate_source, verify_conversion


def upgrade_anchored(directory):
    metadata = read_json(directory / "session.json")
    evidence = metadata["captureEvidence"]
    evidence.update(version=3, tracking="visual-anchors-v1")
    layout = evidence["layout"]
    layout.update(evidenceMode="anchored", pluginConfiguration=dict(version=1, camera="2K", playerAnchor="torso-appearance-v1",
        meterAnchor="player-relative-region-v1", courtAnchor="floor-homography-v1", player=metadata["crop"]))
    scene = next(stream for stream in evidence["streams"] if stream["name"] == "scene")
    scene.update(width=1280, height=720, stride=5120)
    color = (directory / "frames.bgra").read_bytes()[0]
    with (directory / "scene.bgra").open("wb") as output:
        for i in range(len(metadata["frames"])): output.write(bytes([color + i, 20, 30, 255]) * 1280 * 720)
    for i, frame in enumerate(metadata["frames"]):
        entry = next(item for item in frame["evidence"] if item["name"] == "scene")
        entry.update(offsetBytes=i * 1280 * 720 * 4, byteLength=1280 * 720 * 4)
        frame["tracking"] = dict(sequence=frame["sequence"], valid=True, confidence=.95, player=metadata["crop"] | {"left": metadata["crop"]["left"] + i * .002},
            meter=layout["meterRegion"] if metadata["shotProfile"]["usesShotMeter"] else None,
            court=[dict(x=.01 + i * .0005, y=.45), dict(x=.99, y=.45), dict(x=.99, y=.99), dict(x=.01, y=.99)])
    fixtures.EvidenceConversionTests.write(directory / "session.json", metadata)
    labels = read_json(directory / "labels.json")
    for shot in labels["shots"]: shot["playerRegionReviewed"] = True
    fixtures.EvidenceConversionTests.write(directory / "labels.json", labels)


class AnchoredConversionTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.EvidenceConversionTests(methodName="runTest")
        self.fixture.setUp()
        upgrade_anchored(self.fixture.teacher); upgrade_anchored(self.fixture.student)
        self.fixture.review.unlink()
        self.fixture.approve(self.fixture.teacher, self.fixture.student, self.fixture.review)

    def tearDown(self): self.fixture.tearDown()

    def test_actual_trainer_accepts_verified_dynamic_crops_and_retains_tracking(self):
        fixture = self.fixture
        metadata = convert(fixture.teacher, fixture.student, fixture.review, fixture.out, prefer_hard_links=True)
        verify_conversion(fixture.out, metadata)
        self.assertEqual("tracked-player-roi-disjoint-overlay-v1", metadata["trainingConversion"]["pixelPolicy"])
        self.assertNotEqual(metadata["frames"][0]["tracking"]["player"], metadata["frames"][-1]["tracking"]["player"])
        from test_evidence_conversion import training
        loaded = training.load_sessions([fixture.out], "train", 2, 10)
        self.assertGreater(len(loaded[1]), 0)

    def test_lost_identity_wrong_sequence_and_meter_leakage_are_rejected(self):
        source = read_json(self.fixture.teacher / "session.json")
        for mutate in (lambda frame: frame["tracking"].update(valid=False), lambda frame: frame["tracking"].update(sequence=999),
                       lambda frame: frame["tracking"].update(meter=frame["tracking"]["player"])):
            invalid = copy.deepcopy(source); mutate(invalid["frames"][1])
            with self.assertRaises(ValueError): validate_source(invalid)


if __name__ == "__main__": unittest.main()
