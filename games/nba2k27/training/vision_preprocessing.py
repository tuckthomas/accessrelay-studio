"""Offline reference for bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2.

The recorder already produced the nearest-neighbor 256x256 player crop.
No Pillow antialiasing or framework-specific image resizing is used here.
"""
from __future__ import annotations

PREPROCESSING_ID = "bgra8-player-crop-nearest256-bilinear-rgb-unit-ntchw-v2"


def resize_stored_bgra(bgra, target_height: int, target_width: int, mean=(0, 0, 0), std=(1, 1, 1)):
    # Metadata/provenance validation does not resize pixels and must remain usable
    # without the optional offline ML environment.
    import numpy as np

    image = np.asarray(bgra)
    if image.shape != (256, 256, 4) or image.dtype != np.uint8:
        raise ValueError("Canonical v2 recorded inputs must be 256x256 packed BGRA8")
    if target_height <= 0 or target_width <= 0:
        raise ValueError("Target dimensions must be positive")
    mean = np.asarray(mean, dtype=np.float32).reshape(1, 1, 3)
    std = np.asarray(std, dtype=np.float32).reshape(1, 1, 3)
    if not np.isfinite(mean).all() or not np.isfinite(std).all() or np.any(std <= 0):
        raise ValueError("Normalization must be finite with positive standard deviations")
    # Coordinate arithmetic uses double; interpolation/normalization uses float32, as in C#.
    source_y = np.clip((np.arange(target_height, dtype=np.float64) + .5) * 256 / target_height - .5, 0, 255)
    source_x = np.clip((np.arange(target_width, dtype=np.float64) + .5) * 256 / target_width - .5, 0, 255)
    y0, x0 = source_y.astype(np.int64), source_x.astype(np.int64)
    y1, x1 = np.minimum(y0 + 1, 255), np.minimum(x0 + 1, 255)
    fy = (source_y - y0).astype(np.float32)[:, None, None]
    fx = (source_x - x0).astype(np.float32)[None, :, None]
    rgb = image[:, :, [2, 1, 0]].astype(np.float32)
    a = rgb[y0[:, None], x0[None, :]]
    b = rgb[y0[:, None], x1[None, :]]
    c = rgb[y1[:, None], x0[None, :]]
    d = rgb[y1[:, None], x1[None, :]]
    first = a + (b - a) * fx
    second = c + (d - c) * fx
    unit = (first + (second - first) * fy) / np.float32(255)
    return np.ascontiguousarray(((unit - mean) / std).transpose(2, 0, 1))
