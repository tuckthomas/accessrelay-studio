import hashlib
import json


def source_files(loaded):
    def bounded_hash(path):
        with path.open("rb") as stream: data = stream.read(16 * 1024 ** 2 + 1)
        if len(data) > 16 * 1024 ** 2: raise ValueError("Source metadata/labels exceed 16 MiB")
        return hashlib.sha256(data).hexdigest()
    return [{"sessionId": session["sessionId"], "metadataSha256": bounded_hash(directory / "session.json"),
             "labelsSha256": bounded_hash(directory / "labels.json")} for directory, session in loaded[0]]


def corpus_sha(training_sources, validation_sources, training_footage, validation_footage):
    value = {"trainingSources": training_sources, "validationSources": validation_sources,
             "trainingFootage": training_footage, "validationFootage": validation_footage}
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")).hexdigest()
