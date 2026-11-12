"""Conservative exact RGB footage reuse checks, independent of claimed session IDs.

This does not certify independence after re-encoding, crop changes or overlays.
Hashes are provenance, not authenticity. No optional ML packages are imported.
"""
from collections import deque
import hashlib
from pathlib import Path

MAX_HASHES = 50_000


def footage_index(sessions, window_length=4):
    if not isinstance(window_length, int) or not 2 <= window_length <= 4:
        raise ValueError("Footage comparison windows must contain 2–4 frames")
    frames, windows, ids, references = set(), set(), set(), set()
    owners = {}
    for directory, session in sessions:
        identity = session.get("sessionId")
        if not isinstance(identity, str) or not identity or identity in ids:
            raise ValueError("Footage provenance needs unique nonempty session IDs")
        ids.add(identity)
        conversion = session.get("trainingConversion")
        if isinstance(conversion, dict) and conversion.get("studentReferenceSessionId"):
            references.add(conversion["studentReferenceSessionId"])
        recent = deque(maxlen=window_length)
        for frame in session["frames"]:
            path = (directory / frame["file"]).resolve()
            if not path.is_relative_to(Path(directory).resolve()):
                raise ValueError("Footage file escapes its session directory")
            with path.open("rb") as source:
                source.seek(frame["offsetBytes"])
                raw = source.read(frame["byteLength"])
            if len(raw) != 256 * 256 * 4:
                raise ValueError("Footage fingerprints require complete canonical BGRA player frames")
            # Alpha is ignored by model preprocessing and must not hide reused RGB.
            digest = hashlib.sha256(raw[0::4] + raw[1::4] + raw[2::4]).hexdigest()
            frames.add(digest); recent.append(digest)
            if len(recent) == window_length:
                # Order-independent within each window also detects reversed copies.
                window = hashlib.sha256("".join(sorted(recent)).encode("ascii")).hexdigest()
                previous = owners.setdefault(window, identity)
                if previous != identity:
                    raise ValueError("Repeated footage window under different session IDs inside one dataset group")
                windows.add(window)
            if len(frames) + len(windows) > MAX_HASHES:
                raise ValueError("Footage provenance exceeds 50,000 hashes; split the audited corpus")
    if not windows:
        raise ValueError("Footage provenance needs at least one complete frame window")
    return {"schemaVersion": 1, "windowLength": window_length, "sessionIds": sorted(ids),
            "configurationSessionIds": sorted(references), "frameHashes": sorted(frames), "windowHashes": sorted(windows)}


def validate_index(index):
    if not isinstance(index, dict) or index.get("schemaVersion") != 1 or index.get("windowLength") not in (2, 3, 4):
        raise ValueError("Missing/unsupported footage provenance; re-export the candidate with audited source footage")
    for name in ("frameHashes", "windowHashes"):
        values = index.get(name)
        if not isinstance(values, list) or not values or len(values) > MAX_HASHES:
            raise ValueError("Malformed footage hash provenance")
        if any(not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdef" for c in value) for value in values):
            raise ValueError("Invalid RGB footage SHA-256")
        if len(set(values)) != len(values):
            raise ValueError("Duplicate footage provenance hashes")
    if len(index["frameHashes"]) + len(index["windowHashes"]) > MAX_HASHES:
        raise ValueError("Footage hash budget exceeded")
    for name in ("sessionIds", "configurationSessionIds"):
        values = index.get(name)
        if (not isinstance(values, list) or (name == "sessionIds" and not values)
                or any(not isinstance(value, str) or not value for value in values) or len(set(values)) != len(values)):
            raise ValueError("Malformed source/configuration session provenance")


def validate_independent(first, second):
    validate_index(first); validate_index(second)
    if first["windowLength"] != second["windowLength"]:
        raise ValueError("Footage comparison window policies differ")
    if set(first["sessionIds"]) & set(second["sessionIds"]):
        raise ValueError("Source footage session IDs overlap")
    if set(first["sessionIds"]) & set(second["configurationSessionIds"]) or set(second["sessionIds"]) & set(first["configurationSessionIds"]):
        raise ValueError("Dataset footage reuses another group's configuration reference")
    common_frames = set(first["frameHashes"]) & set(second["frameHashes"])
    if set(first["windowHashes"]) & set(second["windowHashes"]) or len(common_frames) >= first["windowLength"]:
        raise ValueError("Duplicate RGB footage crosses dataset groups despite different session IDs")


def validate_evaluation_footage(split, current, run):
    validate_index(current)
    excluded = ("trainingFootage", "validationFootage") if split == "held-out" else ("trainingFootage",)
    if split not in ("validation", "held-out"):
        raise ValueError("Unsupported evaluation split")
    for name in excluded:
        recorded = run.get(name)
        validate_index(recorded)
        expected_ids = run.get("trainingSessionIds" if name == "trainingFootage" else "validationSessionIds")
        if set(recorded["sessionIds"]) != set(expected_ids or []):
            raise ValueError("Footage and training session provenance disagree")
        if set(current["sessionIds"]) & set(recorded["configurationSessionIds"]):
            raise ValueError("Evaluation reuses a student configuration reference session")
        validate_independent(recorded, current)
