#!/usr/bin/env python3
"""Compare the offline transform with a gradient/crop fixture exported by C# verification."""
import argparse
import json
from pathlib import Path
import numpy as np
from vision_preprocessing import PREPROCESSING_ID, resize_stored_bgra

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("fixture", type=Path)
args = parser.parse_args()
metadata = json.loads((args.fixture / "parity.json").read_text(encoding="utf-8-sig"))
if metadata["preprocessing"] != PREPROCESSING_ID:
    raise ValueError("Fixture preprocessing version differs from the offline code")
bgra = np.fromfile(args.fixture / "stored256.bgra", dtype=np.uint8).reshape(256, 256, 4)
expected = np.fromfile(args.fixture / "expected-rgb.f32", dtype="<f4").reshape(3, metadata["targetHeight"], metadata["targetWidth"])
actual = resize_stored_bgra(bgra, metadata["targetHeight"], metadata["targetWidth"], metadata["rgbMean"], metadata["rgbStandardDeviation"])
maximum = float(np.max(np.abs(expected - actual)))
if not np.allclose(expected, actual, rtol=0, atol=1e-6):
    raise AssertionError(f"C#/NumPy preprocessing differs: maximum absolute error {maximum}")
print(f"SUCCESS C# live source-crop / recorded256 / offline NumPy preprocessing parity; maximum absolute error {maximum:.9g}")
