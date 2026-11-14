"""Explicit reviewed conversion. Original evidence and identities are never erased.

Only an overlay-disjoint player ROI is supported. There is no unversioned masking
or automatic teacher labeling, and converted artifacts remain experimental.
"""
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import tempfile
import unicodedata

MAX_VISUAL_BYTES = 2 * 1024 ** 3
PROFILE_FIELDS = ("heightInches", "threePointRating", "base", "upperRelease1", "upperRelease2", "shotSpeed", "release1Percent", "cameraView")
FILES = {"sourceMetadataFile": "source-session.json", "sourceLabelsFile": "source-labels.json",
         "studentReferenceFile": "student-reference.json", "reviewFile": "conversion-review.json"}


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path):
    if Path(path).stat().st_size > 16 * 1024 ** 2:
        raise ValueError("Evidence JSON exceeds 16 MiB")
    try:
        value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, RecursionError) as error:
        raise ValueError("Malformed evidence JSON") from error
    if not isinstance(value, dict) or type(value.get("schemaVersion")) is not int or value["schemaVersion"] != 1:
        raise ValueError("Evidence/review requires schemaVersion=1")
    return value


def child(root, name):
    path = (Path(root) / name).resolve()
    if not path.is_relative_to(Path(root).resolve()) or not path.is_file():
        raise ValueError("Evidence files must stay inside their session folder")
    return path


def integer(value, name, minimum=0):
    if type(value) is not int or not minimum <= value <= 2 ** 63 - 1:
        raise ValueError(f"Invalid evidence {name}")
    return value


def rectangle(value):
    if not isinstance(value, dict) or set(value) != {"left", "top", "width", "height"}:
        raise ValueError("Malformed evidence crop")
    parts = [value.get(key) for key in ("left", "top", "width", "height")]
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in parts):
        raise ValueError("Evidence crop values must be finite")
    left, top, width, height = parts
    if left < 0 or top < 0 or width <= 0 or height <= 0 or left + width > 1 or top + height > 1:
        raise ValueError("Evidence crop is outside the capture")
    return parts


def contains(outer, inner):
    x, y, w, h = rectangle(outer); ix, iy, iw, ih = rectangle(inner)
    return ix >= x and iy >= y and ix + iw <= x + w + 1e-9 and iy + ih <= y + h + 1e-9


def overlaps(player, overlay, width, height):
    x, y, w, h = rectangle(player); ox, oy, ow, oh = rectangle(overlay)
    # One original-capture pixel margin avoids ambiguous marked boundaries.
    return x < ox + ow + 1 / width and ox - 1 / width < x + w and y < oy + oh + 1 / height and oy - 1 / height < y + h


def validate_source(session, directory=None):
    if session.get("complete") is not True or not isinstance(session.get("sessionId"), str) or not session["sessionId"]:
        raise ValueError("Conversion requires a completed identified source session")
    if "trainingConversion" in session:
        raise ValueError("Convert original acquisition evidence, not an already converted session")
    profile, evidence = session.get("shotProfile"), session.get("captureEvidence")
    if isinstance(evidence, dict) and evidence.get("version") == 2:
        raise ValueError("Scene-first footage needs reviewed per-frame player/meter tracking before release-model conversion; a scene overview is not a player crop")
    if not isinstance(profile, dict) or not isinstance(evidence, dict) or evidence.get("version") not in (1, 3, 4):
        raise ValueError("Conversion requires original fixed-region or anchored evidence")
    automatic = evidence["version"] == 4
    anchored = evidence["version"] in (3, 4)
    if anchored and evidence.get("tracking") != ("identity-pose-v1" if automatic else "visual-anchors-v1"):
        raise ValueError("Anchored evidence has an unsupported region policy")
    purpose = evidence.get("purpose")
    if purpose not in ("meterSupervisedTraining", "meterOffValidation") or type(profile.get("usesShotMeter")) is not bool:
        raise ValueError("Unknown teacher/student capture purpose or meter state")
    if profile["usesShotMeter"] != (purpose == "meterSupervisedTraining"):
        raise ValueError("Capture purpose disagrees with the recorded shot-meter state")
    fingerprint = session.get("shotProfileFingerprint")
    if not isinstance(fingerprint, str) or len(fingerprint) != 64 or any(c not in "0123456789abcdef" for c in fingerprint):
        raise ValueError("Missing recorded shot-profile fingerprint")
    if profile.get("cameraView") not in ("2K", "2K Low", "2K Legacy") or session.get("cameraView") != profile["cameraView"]:
        raise ValueError("Recorded camera provenance disagrees")
    if any(field not in profile for field in PROFILE_FIELDS):
        raise ValueError("Incomplete recorded animation configuration")
    for name, low, high in (("heightInches", 36, 108), ("threePointRating", 0, 100), ("shotSpeed", 1, 4), ("release1Percent", 0, 100)):
        if type(profile[name]) is not int or not low <= profile[name] <= high: raise ValueError("Invalid recorded animation field: " + name)
    for name in ("base", "upperRelease1", "upperRelease2"):
        value = profile[name]
        if not isinstance(value, str) or not value.strip() or len(value) > 120 or any(unicodedata.category(char) == "Cc" for char in value):
            raise ValueError("Invalid recorded release name")
    if session.get("animationProfile") != "nba2k27:" + fingerprint: raise ValueError("Animation identity differs from the recorded shot profile")
    if session.get("pixelFormat") != "BGRA8" or (session.get("storedWidth"), session.get("storedHeight"), session.get("storedStride")) != (256, 256, 1024):
        raise ValueError("Conversion requires canonical 256x256 player BGRA")
    width, height = integer(session.get("captureWidth"), "captureWidth", 1), integer(session.get("captureHeight"), "captureHeight", 1)
    if width > 16384 or height > 16384: raise ValueError("Capture dimensions exceed the host contract")
    integer(session.get("stopwatchFrequency"), "frequency", 1)
    layout = evidence.get("layout")
    if (not isinstance(layout, dict) or layout.get("configured") is not True or layout.get("sourceWidth") != width
            or layout.get("sourceHeight") != height or layout.get("purpose") != purpose
            or not isinstance(layout.get("gameBuild"), str) or not layout["gameBuild"].strip()):
        raise ValueError("Confirm the captured layout, source size and explicit game build before conversion")
    player, scene, feedback = session.get("crop"), layout.get("gameplayRegion"), layout.get("feedbackRegion")
    if not contains(scene, player) or not contains(scene, feedback): raise ValueError("Player/feedback crops must be inside gameplay")
    for overlay in (layout.get("meterRegion"), feedback):
        if overlay is not None and (not contains(scene, overlay) or (not anchored and overlaps(player, overlay, width, height))):
            raise ValueError("Model player ROI overlaps a meter/feedback overlay; re-record a disjoint ROI rather than silently masking pixels")
    expected = {"player": ("frames.bgra", 256, 256, player), "scene": ("scene.bgra", 640, 360, scene), "feedback": ("feedback.bgra", 640, 192, feedback)}
    if anchored:
        config = layout.get("pluginConfiguration")
        if (not isinstance(config, dict) or config.get("version") != 1 or config.get("camera") != profile["cameraView"]
                or (automatic and (config.get("policy") != "gamertag-pose-v1" or not profile.get("gamertag") or config.get("gamertag") != profile["gamertag"] or config.get("meterOn") != profile["usesShotMeter"]))
                or (not automatic and (config.get("playerAnchor") != "torso-appearance-v1" or config.get("meterAnchor") != "player-relative-region-v1" or config.get("courtAnchor") != "floor-homography-v1"))):
            raise ValueError("Missing recorded NBA anchor configuration")
        _, _, vw, vh = rectangle(scene)
        scale = min(1, 1280 / max(width * vw, height * vh))
        expected["scene"] = ("scene.bgra", max(1, round(width * vw * scale)), max(1, round(height * vh * scale)), scene)
    if purpose == "meterSupervisedTraining":
        if layout.get("meterRegion") is None: raise ValueError("Meter teacher evidence needs a marked meter region")
        expected["meter"] = ("meter.bgra", 256, 256, layout["meterRegion"])
    streams = evidence.get("streams")
    if not isinstance(streams, list) or len(streams) != len(expected): raise ValueError("Missing/extra evidence streams")
    definitions = {}
    for stream in streams:
        if not isinstance(stream, dict) or stream.get("name") not in expected or stream["name"] in definitions:
            raise ValueError("Unknown/duplicate evidence stream")
        name = stream["name"]; filename, sw, sh, crop = expected[name]
        if (stream.get("file"), stream.get("width"), stream.get("height"), stream.get("stride"), stream.get("pixelFormat"), stream.get("crop")) != (filename, sw, sh, sw * 4, "BGRA8", crop):
            raise ValueError("Evidence stream geometry/crop differs from the recorded layout")
        definitions[name] = (filename, sw * sh * 4)
    frames = session.get("frames")
    if not isinstance(frames, list) or not 4 <= len(frames) <= 10000: raise ValueError("Conversion needs 4–10000 recorded frames")
    previous, previous_sequence, generations = -1, -1, set()
    for index, frame in enumerate(frames):
        if not isinstance(frame, dict): raise ValueError("Malformed evidence frame")
        ticks = integer(frame.get("captureTicks"), "captureTicks")
        integer(frame.get("offsetBytes"), "frame offset")
        sequence = integer(frame.get("sequence"), "sequence", 1)
        generations.add(integer(frame.get("sourceGeneration"), "sourceGeneration"))
        if anchored:
            tracking = frame.get("tracking")
            if not isinstance(tracking, dict) or tracking.get("valid") is not True or tracking.get("sequence") != sequence:
                raise ValueError("An anchored frame lost identity or sequence provenance")
            tracked_player, tracked_meter = tracking.get("player"), tracking.get("meter")
            if not contains(scene, tracked_player): raise ValueError("Tracked player region is outside gameplay")
            if purpose == "meterSupervisedTraining" and tracked_meter is None: raise ValueError("Missing player-associated meter region")
            for overlay in (tracked_meter, feedback):
                if overlay is not None and (not contains(scene, overlay) or overlaps(tracked_player, overlay, width, height)):
                    raise ValueError("Tracked player crop contains meter/feedback; review anchors and record overlay-disjoint evidence")
            confidence = tracking.get("confidence")
            if type(confidence) not in (int, float) or not math.isfinite(confidence) or not 0 <= confidence <= 1:
                raise ValueError("Invalid anchor confidence")
            court = tracking.get("court")
            if not isinstance(court, list) or (not 3 <= len(court) <= 64 if automatic else len(court) != 4) or any(not isinstance(p, dict) or any(type(p.get(axis)) not in (int, float) or not math.isfinite(p[axis]) or not -.2 <= p[axis] <= 1.2 for axis in ("x", "y")) for p in court):
                raise ValueError("Missing per-frame court projection")
            if automatic:
                identity, keypoints = tracking.get("identity"), tracking.get("keypoints")
                if (not isinstance(identity, dict) or identity.get("expectedTag") != profile.get("gamertag")
                        or identity.get("observedTag") != profile.get("gamertag") or identity.get("caseSensitive") is not True
                        or type(identity.get("confidence")) not in (int, float) or not math.isfinite(identity["confidence"]) or not 0 <= identity["confidence"] <= 1
                        or not 1 <= integer(identity.get("verifiedSequence"), "identity sequence", 1) <= sequence):
                    raise ValueError("Automatic capture lost exact player identity")
                if not isinstance(keypoints, list) or len(keypoints) != 17 or any(not isinstance(p, dict) or any(type(p.get(axis)) not in (int, float) or not math.isfinite(p[axis]) for axis in ("x", "y", "confidence")) or not 0 <= p["confidence"] <= 1 for p in keypoints):
                    raise ValueError("Automatic capture lacks finite 17-joint pose evidence")
        if ticks <= previous or sequence <= previous_sequence: raise ValueError("Evidence frame times/sequences must increase")
        previous, previous_sequence = ticks, sequence
        if (frame.get("file"), frame.get("offsetBytes"), frame.get("byteLength")) != ("frames.bgra", index * 256 * 256 * 4, 256 * 256 * 4):
            raise ValueError("Primary frame offset/length is inconsistent")
        regions = frame.get("evidence")
        if not isinstance(regions, list) or len(regions) != len(definitions) - 1: raise ValueError("Missing synchronized auxiliary evidence")
        seen = set()
        for region in regions:
            if not isinstance(region, dict) or region.get("name") not in definitions or region["name"] == "player" or region["name"] in seen:
                raise ValueError("Unknown/duplicate auxiliary frame")
            seen.add(region["name"]); filename, size = definitions[region["name"]]
            if (region.get("file"), region.get("offsetBytes"), region.get("byteLength")) != (filename, index * size, size):
                raise ValueError("Auxiliary evidence offset/length is inconsistent")
    if len(generations) != 1: raise ValueError("Source changed during the take")
    expected_files = {filename: size * len(frames) for filename, size in definitions.values()}
    if sum(expected_files.values()) > (2 * MAX_VISUAL_BYTES if anchored else MAX_VISUAL_BYTES): raise ValueError("Evidence exceeds the 2 GiB capture bound")
    if directory is not None:
        for filename, size in expected_files.items():
            if child(directory, filename).stat().st_size != size: raise ValueError("Truncated/extra evidence stream data")
    trace = evidence.get("controllerTraceFile")
    if trace is not None:
        if trace != "controller-trace.json": raise ValueError("Unexpected controller trace filename")
        expected_files[trace] = None
        if directory is not None and child(directory, trace).stat().st_size > 16 * 1024 ** 2: raise ValueError("Controller trace exceeds 16 MiB")
    preparation = session.get("preparation")
    if preparation is not None:
        if (not automatic or not isinstance(preparation, dict) or preparation.get("version") != 1
                or preparation.get("cropPolicy") != "pose-envelope-hud-disjoint-v1"
                or preparation.get("rawMetadataFile") != "acquisition-session.json" or preparation.get("frameReportFile") != "frame-results.json"
                or preparation.get("retainedFrameCount") != len(frames)):
            raise ValueError("Unsupported offline player preparation")
        expected_files.update({"acquisition-session.json": None, "frame-results.json": None})
        if directory is not None:
            raw_path = child(directory, "acquisition-session.json")
            raw, report = read_json(raw_path), read_json(child(directory, "frame-results.json"))
            if (digest(raw_path) != preparation.get("sourceSessionSha256") or raw.get("complete") is not True
                    or raw.get("sessionId") != preparation.get("sourceSessionId") or raw.get("captureEvidence", {}).get("version") != 2
                    or raw.get("shotProfile") != session["shotProfile"] or raw.get("stopwatchFrequency") != session["stopwatchFrequency"]):
                raise ValueError("Offline player crops lost their original acquisition identity")
            version_file = preparation.get("gameVersionFile")
            if version_file is not None:
                if version_file != "source-game-version.json": raise ValueError("Unexpected inspected game-version filename")
                version = read_json(child(directory, version_file))
                if (version.get("sessionSha256") != preparation["sourceSessionSha256"]
                        or layout["gameBuild"] != f"{version.get('installedGameVersion')} ({version.get('platform')})"):
                    raise ValueError("Inspected game build differs from the original recording's bound observation")
            elif raw.get("captureEvidence", {}).get("layout", {}).get("gameBuild") != layout["gameBuild"]:
                raise ValueError("Preparation changed the recorded game build without an inspected version")
            original_frames, results = raw.get("frames", []), report.get("frames", [])
            if len(original_frames) != preparation.get("sourceFrameCount") or len(results) != len(original_frames):
                raise ValueError("Offline tracking report does not account for every original frame")
            for frame in frames:
                index = integer(frame.get("sourceFrameIndex"), "original frame index")
                if index >= len(original_frames): raise ValueError("Prepared frame refers outside the raw recording")
                original, result = original_frames[index], results[index]
                if any(frame.get(key) != original.get(key) for key in ("sequence", "captureTicks", "sourceGeneration", "compositorTicks")):
                    raise ValueError("Prepared frame changed source sequence, clock or generation")
                observation = result.get("observation", {}) | {"textRegions": None, "detectedPeople": None}
                if result.get("sourceIndex") != index or result.get("usable") is not True or observation != frame["tracking"]:
                    raise ValueError("A rejected or mismatched observation entered the prepared clip")
        if preparation.get("gameVersionFile") is not None: expected_files["source-game-version.json"] = None
    return expected_files


def validate_labels(labels, source):
    if labels.get("sessionId") != source["sessionId"] or labels.get("labelTimeBasis") != "captured-target-game-phase":
        raise ValueError("Reviewed labels must retain source identity and captured game-phase time basis")
    purpose, split = source["captureEvidence"]["purpose"], labels.get("split")
    if (purpose == "meterSupervisedTraining" and split != "train") or (purpose == "meterOffValidation" and split not in ("validation", "held-out")):
        raise ValueError("Meter-on supervision cannot be relabeled as meter-off validation/held-out data")
    shots = labels.get("shots")
    if not isinstance(shots, list) or not shots: raise ValueError("Conversion requires explicitly reviewed shot intervals")
    intervals, identities = [], set()
    first, last = source["frames"][0]["captureTicks"], source["frames"][-1]["captureTicks"]
    for shot in shots:
        if (not isinstance(shot, dict) or not isinstance(shot.get("shotId"), str) or not shot["shotId"] or shot["shotId"] in identities
                or shot.get("reviewed") is not True or type(shot.get("isTargetShot")) is not bool or type(shot.get("playerIdentityConfirmed")) is not bool):
            raise ValueError("Every shot needs unique identity, target status and explicit human review")
        if source["captureEvidence"]["version"] in (3, 4) and shot.get("playerRegionReviewed") is not True:
            raise ValueError("Review player identity, complete body framing and overlay exclusion for every anchored interval")
        identities.add(shot["shotId"])
        start, end = integer(shot.get("startTicks"), "shot start"), integer(shot.get("endTicks"), "shot end")
        if not first <= start < end <= last: raise ValueError("Reviewed interval exceeds recorded frames")
        if shot["isTargetShot"] and (not shot["playerIdentityConfirmed"] or not start <= integer(shot.get("targetReleaseTicks"), "target phase") <= end):
            raise ValueError("Target phases need reviewed player identity and an in-interval target timestamp")
        intervals.append((start, end))
    intervals.sort()
    if any(current[0] <= previous[1] for previous, current in zip(intervals, intervals[1:])): raise ValueError("Reviewed intervals overlap")


def validate_mapping(source, student):
    if student["captureEvidence"]["purpose"] != "meterOffValidation" or student["shotProfile"]["usesShotMeter"] is not False:
        raise ValueError("Student reference must be an actual recorded meter-off configuration")
    for field in PROFILE_FIELDS:
        first, second = source["shotProfile"][field], student["shotProfile"][field]
        if isinstance(first, str) and isinstance(second, str):
            first, second = unicodedata.normalize("NFC", first.strip()), unicodedata.normalize("NFC", second.strip())
        if first != second: raise ValueError("Teacher/student animation or camera mismatch: " + field)
    if source["captureEvidence"]["version"] in (3, 4):
        if student["captureEvidence"]["version"] != source["captureEvidence"]["version"] or source["captureEvidence"]["layout"]["gameBuild"] != student["captureEvidence"]["layout"]["gameBuild"]:
            raise ValueError("Anchored teacher/student policy or game build differs")
        if source["captureEvidence"]["version"] == 4 and source["shotProfile"].get("gamertag") != student["shotProfile"].get("gamertag"):
            raise ValueError("Automatic teacher/student player identity differs")
        return
    if (source["captureWidth"], source["captureHeight"], source["crop"], source["captureEvidence"]["layout"]["gameBuild"]) != (student["captureWidth"], student["captureHeight"], student["crop"], student["captureEvidence"]["layout"]["gameBuild"]):
        raise ValueError("Teacher/student capture geometry or game build differs")


def validate_review(review, source, hashes, data_hashes):
    for flag in ("reviewed", "playerRoiContainsNoMeterOrFeedback", "targetPhaseReviewedIndependentlyOfControllerEvents", "teacherStudentAnimationEquivalent"):
        if review.get(flag) is not True: raise ValueError("Explicit conversion review is required: " + flag)
    if not isinstance(review.get("reviewer"), str) or not review["reviewer"].strip() or len(review["reviewer"]) > 80:
        raise ValueError("Conversion review needs a reviewer name/identifier")
    try:
        reviewed = datetime.datetime.fromisoformat(review["reviewedAtUtc"].replace("Z", "+00:00"))
        if reviewed.utcoffset() != datetime.timedelta(0): raise ValueError()
    except (ValueError, KeyError, TypeError, AttributeError) as error: raise ValueError("Review needs a UTC timestamp") from error
    if review.get("sourceSessionId") != source["sessionId"]: raise ValueError("Review source identity differs")
    for name, value in hashes.items():
        if review.get(name) != value: raise ValueError("Review hash differs: " + name)
    if review.get("sourceDataSha256") != data_hashes: raise ValueError("Reviewed source visual/controller data hashes differ")


def converted_metadata(source, student, conversion):
    return source | {"shotProfile": student["shotProfile"], "shotProfileFingerprint": student["shotProfileFingerprint"],
                     "animationProfile": "nba2k27:" + student["shotProfileFingerprint"], "trainingConversion": conversion}


def verify_conversion(directory, session):
    conversion = session.get("trainingConversion")
    if not isinstance(conversion, dict) or conversion.get("version") != 1 or conversion.get("pixelPolicy") not in ("player-roi-disjoint-overlay-v1", "tracked-player-roi-disjoint-overlay-v1") or conversion.get("validationPassed") is not False:
        raise ValueError("Unsupported reviewed training conversion")
    if any(conversion.get(key) != filename for key, filename in FILES.items()): raise ValueError("Conversion proof filenames are invalid")
    paths = {key: child(directory, filename) for key, filename in FILES.items()}
    source, labels, student, review = [read_json(paths[key]) for key in FILES]
    expected_files = validate_source(source, directory); validate_source(student)
    expected_policy = "tracked-player-roi-disjoint-overlay-v1" if source["captureEvidence"]["version"] in (3, 4) else "player-roi-disjoint-overlay-v1"
    if conversion["pixelPolicy"] != expected_policy: raise ValueError("Converted pixel policy differs from acquisition")
    validate_mapping(source, student); validate_labels(labels, source)
    hashes = {"sourceSessionSha256": digest(paths["sourceMetadataFile"]), "labelsSha256": digest(paths["sourceLabelsFile"]), "studentReferenceSha256": digest(paths["studentReferenceFile"])}
    data_hashes = conversion.get("sourceDataSha256")
    if not isinstance(data_hashes, dict) or set(data_hashes) != set(expected_files): raise ValueError("Missing source data integrity map")
    validate_review(review, source, hashes, data_hashes)
    if any(conversion.get(key) != value for key, value in hashes.items()) or conversion.get("reviewSha256") != digest(paths["reviewFile"]):
        raise ValueError("Conversion proof bytes changed")
    if conversion.get("sourceSessionId") != source["sessionId"] or conversion.get("studentReferenceSessionId") != student["sessionId"]:
        raise ValueError("Conversion cannot relabel source/reference session identity")
    if session != converted_metadata(source, student, conversion) or digest(child(directory, "labels.json")) != hashes["labelsSha256"]:
        raise ValueError("Converted metadata/labels differ from reviewed original evidence")
    for filename, expected_hash in data_hashes.items():
        if digest(child(directory, filename)) != expected_hash: raise ValueError("Converted source pixels/controller evidence changed")


def convert(source_directory, student_directory, review_file, destination, prefer_hard_links=False):
    source_directory, student_directory, destination = map(Path, (source_directory, student_directory, destination))
    if destination.exists(): raise ValueError("Conversion never overwrites a session; choose a fresh destination")
    if destination.resolve().is_relative_to(source_directory.resolve()) or destination.resolve().is_relative_to(student_directory.resolve()):
        raise ValueError("Keep converted artifacts outside original acquisition folders")
    source_path, student_path = child(source_directory, "session.json"), child(student_directory, "session.json")
    labels_path = child(source_directory, "labels.json")
    source, student, labels, review = read_json(source_path), read_json(student_path), read_json(labels_path), read_json(review_file)
    files = validate_source(source, source_directory); validate_source(student, student_directory)
    validate_labels(labels, source); validate_mapping(source, student)
    hashes = {"sourceSessionSha256": digest(source_path), "labelsSha256": digest(labels_path), "studentReferenceSha256": digest(student_path)}
    data_hashes = {filename: digest(child(source_directory, filename)) for filename in files}
    validate_review(review, source, hashes, data_hashes)
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".conversion-", dir=destination.parent))
    try:
        for filename in files:
            original = child(source_directory, filename)
            if prefer_hard_links:
                try: os.link(original, staging / filename)
                except OSError: shutil.copyfile(original, staging / filename)
            else: shutil.copyfile(original, staging / filename)
        for original, filename in [(source_path, FILES["sourceMetadataFile"]), (labels_path, FILES["sourceLabelsFile"]), (student_path, FILES["studentReferenceFile"]), (Path(review_file), FILES["reviewFile"]), (labels_path, "labels.json")]:
            shutil.copyfile(original, staging / filename)
        conversion = {"version": 1, "sourceSessionId": source["sessionId"], "studentReferenceSessionId": student["sessionId"],
                      "pixelPolicy": "tracked-player-roi-disjoint-overlay-v1" if source["captureEvidence"]["version"] in (3, 4) else "player-roi-disjoint-overlay-v1", "validationPassed": False, **FILES, **hashes,
                      "reviewSha256": digest(review_file), "sourceDataSha256": data_hashes}
        metadata = converted_metadata(source, student, conversion)
        (staging / "session.json").write_text(json.dumps(metadata, indent=2, allow_nan=False) + "\n", encoding="utf-8")
        verify_conversion(staging, metadata)
        if destination.exists(): raise ValueError("Conversion destination was created by another process")
        staging.rename(destination)
        return metadata
    finally:
        if staging.exists(): shutil.rmtree(staging)


def write_review_template(source_directory, student_directory, destination):
    source_path, labels_path = child(source_directory, "session.json"), child(source_directory, "labels.json")
    student_path = child(student_directory, "session.json")
    source, labels, student = read_json(source_path), read_json(labels_path), read_json(student_path)
    files = validate_source(source, source_directory); validate_source(student, student_directory)
    validate_labels(labels, source); validate_mapping(source, student)
    template = {"schemaVersion": 1, "reviewed": False, "reviewer": "", "reviewedAtUtc": "",
                "sourceSessionId": source["sessionId"], "sourceSessionSha256": digest(source_path), "labelsSha256": digest(labels_path),
                "studentReferenceSha256": digest(student_path), "sourceDataSha256": {name: digest(child(source_directory, name)) for name in files},
                "playerRoiContainsNoMeterOrFeedback": False, "targetPhaseReviewedIndependentlyOfControllerEvents": False,
                "teacherStudentAnimationEquivalent": False}
    with Path(destination).open("x", encoding="utf-8") as stream:
        json.dump(template, stream, indent=2); stream.write("\n")
    return template
