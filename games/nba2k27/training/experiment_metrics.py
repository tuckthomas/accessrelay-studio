"""Descriptive metrics only. No promotion decision or hardware certification."""
import math


def percentile(values, probability):
    if not values: return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(probability * len(ordered)) - 1)]


def summarize(errors, widths, inference_ms, semantics):
    if len(errors) != len(widths) or semantics not in ("absolute-error-estimate-v1", "gaussian-95-half-width-v1"):
        raise ValueError("Unsupported uncertainty summary")
    if any(not math.isfinite(v) for group in (errors, widths, inference_ms) for v in group) or any(v < 0 for v in widths + inference_ms):
        raise ValueError("Metrics must be finite with nonnegative widths/timings")
    absolute = [abs(value) for value in errors]
    result = {"positiveWindows": len(errors), "timingMAEms": sum(absolute) / len(absolute) if absolute else None,
              "timingRMSEms": math.sqrt(sum(v * v for v in errors) / len(errors)) if errors else None,
              "timingP50ms": percentile(absolute, .5), "timingP95ms": percentile(absolute, .95), "timingP99ms": percentile(absolute, .99),
              "uncertaintySemantics": semantics,
              "coverageWithinPredictedUncertainty": sum(abs(error) <= width for error, width in zip(errors, widths)) / len(errors) if errors else None,
              "cpuInferenceSamples": len(inference_ms), "cpuInferenceP50ms": percentile(inference_ms, .5), "cpuInferenceP95ms": percentile(inference_ms, .95)}
    if semantics == "gaussian-95-half-width-v1":
        result["coverageWithinOneSigma"] = sum(abs(error) <= width / 1.96 for error, width in zip(errors, widths)) / len(errors) if errors else None
    return result
