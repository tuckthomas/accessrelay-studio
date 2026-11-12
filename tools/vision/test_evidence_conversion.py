"""Synthetic synchronized streams prove conversion integrity, not label quality."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

TRAINING = Path(__file__).resolve().parents[2] / "games/nba2k27/training"
sys.path.insert(0, str(TRAINING))
from evidence_conversion import convert, digest, read_json, verify_conversion, write_review_template
from footage_integrity import footage_index, validate_independent
spec = importlib.util.spec_from_file_location("conversion_training", TRAINING / "Train-VisionModel.py")
training = importlib.util.module_from_spec(spec); spec.loader.exec_module(training)


class EvidenceConversionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="accessrelay-conversion-")
        self.root = Path(self.temporary.name)
        self.teacher = self.make_clip("teacher", True, 10)
        self.student = self.make_clip("student-configuration", False, 100)
        self.review = self.root / "review.json"
        self.approve(self.teacher, self.student, self.review)
        self.out = self.root / "converted"

    def tearDown(self): self.temporary.cleanup()

    def make_clip(self, identity, meter, color):
        directory = self.root / identity; directory.mkdir()
        rect = lambda x, y, w, h: dict(left=x, top=y, width=w, height=h)
        player, scene, meter_roi, feedback = rect(.05, .1, .3, .5), rect(0, 0, 1, 1), rect(.8, .1, .1, .4), rect(.5, .8, .4, .1)
        purpose = "meterSupervisedTraining" if meter else "meterOffValidation"
        layout = dict(configured=True, sourceWidth=1920, sourceHeight=1080, gameBuild="synthetic-build", purpose=purpose,
                      gameplayRegion=scene, meterRegion=meter_roi, feedbackRegion=feedback)
        streams = [dict(name="player", file="frames.bgra", width=256, height=256, stride=1024, pixelFormat="BGRA8", crop=player),
                   dict(name="scene", file="scene.bgra", width=640, height=360, stride=2560, pixelFormat="BGRA8", crop=scene),
                   dict(name="feedback", file="feedback.bgra", width=640, height=192, stride=2560, pixelFormat="BGRA8", crop=feedback)]
        if meter: streams.append(dict(name="meter", file="meter.bgra", width=256, height=256, stride=1024, pixelFormat="BGRA8", crop=meter_roi))
        for stream in streams:
            pixels = b"".join(bytes([color + i, 20, 30, 255]) * stream["width"] * stream["height"] for i in range(8))
            (directory / stream["file"]).write_bytes(pixels)
        frames = [dict(sequence=i + 1, captureTicks=100000 + i * 10000, observedTicks=100100 + i * 10000,
                       sourceGeneration=1, file="frames.bgra", offsetBytes=i * 256 * 256 * 4, byteLength=256 * 256 * 4,
                       evidence=[dict(name=s["name"], file=s["file"], offsetBytes=i * s["width"] * s["height"] * 4, byteLength=s["width"] * s["height"] * 4) for s in streams if s["name"] != "player"]) for i in range(8)]
        fingerprint = ("a" if meter else "b") * 64
        profile = dict(id=identity, name="Synthetic shot", heightInches=78, threePointRating=80, base="Base", upperRelease1="Upper one", upperRelease2="Upper two",
                       shotSpeed=4, release1Percent=50, cameraView="2K", usesShotMeter=meter, meterReleaseType="Synthetic target" if meter else "")
        session = dict(schemaVersion=1, sessionId=identity, complete=True, stopwatchFrequency=1000000, pixelFormat="BGRA8",
                       storedWidth=256, storedHeight=256, storedStride=1024, captureWidth=1920, captureHeight=1080,
                       shotProfile=profile, shotProfileFingerprint=fingerprint, animationProfile="nba2k27:" + fingerprint,
                       crop=player, cameraView="2K", frames=frames, captureEvidence=dict(version=1, purpose=purpose, layout=layout, streams=streams))
        self.write(directory / "session.json", session)
        labels = dict(schemaVersion=1, sessionId=identity, split="train" if meter else "validation", labelTimeBasis="captured-target-game-phase", shots=[
            dict(shotId="target", reviewed=True, startTicks=100000, endTicks=140000, targetReleaseTicks=140000, isTargetShot=True, playerIdentityConfirmed=True),
            dict(shotId="negative", reviewed=True, startTicks=150000, endTicks=170000, isTargetShot=False, playerIdentityConfirmed=False)])
        self.write(directory / "labels.json", labels)
        return directory

    def approve(self, source, reference, path):
        review = write_review_template(source, reference, path)
        review.update(reviewed=True, reviewer="Synthetic test fixture", reviewedAtUtc="2026-11-12T00:00:00Z",
                      playerRoiContainsNoMeterOrFeedback=True, targetPhaseReviewedIndependentlyOfControllerEvents=True, teacherStudentAnimationEquivalent=True)
        self.write(path, review)

    @staticmethod
    def write(path, value): path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def test_reviewed_teacher_conversion_preserves_evidence_and_loads_student_profile(self):
        original_hash = digest(self.teacher / "session.json")
        session = convert(self.teacher, self.student, self.review, self.out)
        self.assertEqual(digest(self.teacher / "session.json"), original_hash)
        self.assertEqual(digest(self.out / "source-session.json"), original_hash)
        self.assertEqual(session["sessionId"], "teacher")
        self.assertEqual(session["shotProfileFingerprint"], "b" * 64)
        self.assertEqual(session["captureEvidence"]["purpose"], "meterSupervisedTraining")
        self.assertFalse(session["trainingConversion"]["validationPassed"])
        loaded = training.load_sessions([self.out], "train", 4, 10)
        self.assertEqual(loaded[2], {"teacher"}); self.assertGreater(len(loaded[1]), 0)
        self.assertEqual(footage_index(loaded[0])["configurationSessionIds"], ["student-configuration"])
        self.assertEqual((self.teacher / "meter.bgra").read_bytes(), (self.out / "meter.bgra").read_bytes())

    def test_meter_off_validation_conversion_stays_independent(self):
        train_session = convert(self.teacher, self.student, self.review, self.out)
        validation = self.make_clip("validation", False, 180)
        review = self.root / "validation-review.json"; self.approve(validation, validation, review)
        converted = self.root / "converted-validation"; session = convert(validation, validation, review, converted)
        loaded = training.load_sessions([converted], "validation", 4, 10)
        self.assertFalse(session["shotProfile"]["usesShotMeter"])
        validate_independent(footage_index([(self.out, train_session)]), footage_index(loaded[0]))

    def test_unapproved_template_never_converts(self):
        template = self.root / "unapproved.json"; write_review_template(self.teacher, self.student, template)
        with self.assertRaisesRegex(ValueError, "Explicit conversion review"):
            convert(self.teacher, self.student, template, self.out)
        self.assertFalse(self.out.exists())

    def test_wrong_review_hash_or_changed_source_pixels_fail(self):
        review = read_json(self.review); review["labelsSha256"] = "0" * 64; self.write(self.review, review)
        with self.assertRaisesRegex(ValueError, "Review hash"):
            convert(self.teacher, self.student, self.review, self.out)
        review["labelsSha256"] = digest(self.teacher / "labels.json"); self.write(self.review, review)
        with (self.teacher / "frames.bgra").open("r+b") as pixels: pixels.write(bytes([255]))
        with self.assertRaisesRegex(ValueError, "data hashes"):
            convert(self.teacher, self.student, self.review, self.out)

    def test_meter_or_feedback_overlap_is_rejected_before_conversion(self):
        session = read_json(self.teacher / "session.json")
        for field in ("meterRegion", "feedbackRegion"):
            altered = copy.deepcopy(session); altered["captureEvidence"]["layout"][field] = session["crop"]
            self.write(self.teacher / "session.json", altered)
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "overlaps"):
                convert(self.teacher, self.student, self.review, self.out)

    def test_teacher_student_camera_animation_and_game_build_must_match(self):
        session = read_json(self.student / "session.json")
        for field, value in [("base", "Different"), ("cameraView", "2K Low")]:
            altered = copy.deepcopy(session); altered["shotProfile"][field] = value
            if field == "cameraView": altered["cameraView"] = value
            self.write(self.student / "session.json", altered)
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "mismatch"):
                convert(self.teacher, self.student, self.review, self.out)
        altered = copy.deepcopy(session); altered["captureEvidence"]["layout"]["gameBuild"] = "Another build"
        self.write(self.student / "session.json", altered)
        with self.assertRaisesRegex(ValueError, "game build differs"):
            convert(self.teacher, self.student, self.review, self.out)

    def test_meter_on_supervision_cannot_become_final_evaluation(self):
        labels = read_json(self.teacher / "labels.json"); labels["split"] = "held-out"; self.write(self.teacher / "labels.json", labels)
        with self.assertRaisesRegex(ValueError, "Meter-on supervision"):
            convert(self.teacher, self.student, self.review, self.out)

    def test_truncated_stream_foreign_offset_and_source_changes_fail(self):
        session = read_json(self.teacher / "session.json")
        for alter in (lambda s: s["frames"][1]["evidence"][0].update(offsetBytes=0), lambda s: s["frames"][1].update(sourceGeneration=2)):
            changed = copy.deepcopy(session); alter(changed); self.write(self.teacher / "session.json", changed)
            with self.assertRaises(ValueError): convert(self.teacher, self.student, self.review, self.out)
        self.write(self.teacher / "session.json", session)
        with (self.teacher / "meter.bgra").open("r+b") as stream: stream.truncate(10)
        with self.assertRaisesRegex(ValueError, "Truncated"):
            convert(self.teacher, self.student, self.review, self.out)

    def test_edited_conversion_labels_pixels_or_markers_cannot_bypass_review(self):
        metadata = convert(self.teacher, self.student, self.review, self.out)
        labels = read_json(self.out / "labels.json"); labels["shots"][0]["targetReleaseTicks"] += 1; self.write(self.out / "labels.json", labels)
        with self.assertRaisesRegex(ValueError, "metadata/labels"):
            training.load_sessions([self.out], "train", 4, 10)
        (self.out / "labels.json").write_bytes((self.teacher / "labels.json").read_bytes())
        changed = copy.deepcopy(metadata); del changed["captureEvidence"]; self.write(self.out / "session.json", changed)
        with self.assertRaisesRegex(ValueError, "metadata/labels"):
            training.load_sessions([self.out], "train", 4, 10)
        self.write(self.out / "session.json", metadata)
        stripped = copy.deepcopy(metadata); del stripped["trainingConversion"]; del stripped["captureEvidence"]
        self.write(self.out / "session.json", stripped)
        with self.assertRaisesRegex(ValueError, "provenance markers were removed"):
            training.load_sessions([self.out], "train", 4, 10)
        self.write(self.out / "session.json", metadata)
        with (self.out / "frames.bgra").open("r+b") as stream: stream.write(bytes([255]))
        with self.assertRaisesRegex(ValueError, "pixels/controller evidence changed"):
            verify_conversion(self.out, metadata)

    def test_no_overwrite_or_nested_output_and_no_auto_reconversion(self):
        convert(self.teacher, self.student, self.review, self.out)
        with self.assertRaisesRegex(ValueError, "never overwrites"):
            convert(self.teacher, self.student, self.review, self.out)
        with self.assertRaisesRegex(ValueError, "outside original"):
            convert(self.teacher, self.student, self.review, self.teacher / "nested")
        with self.assertRaisesRegex(ValueError, "already converted"):
            convert(self.out, self.student, self.review, self.root / "again")


if __name__ == "__main__": unittest.main()
