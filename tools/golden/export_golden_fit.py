"""Exports golden baselines for the generic `resize-fit` + `pad-to-size` pipeline.

Semantics (generic, not model specific):
  ensure_rgb  = exif transpose + mode unify + alpha composite over white
  fit_resize  = aspect preserving fit into a square box, Pillow BILINEAR, floor rounding
  pad_to_size = floor-center pad to the box with black background
  tensor      = /255 then (x - 0.5) / 0.5, HWC RGB -> CHW float32 (no channel reorder)

The C# `pillow-bilinear-v1` sampler is a bit-exact port of Pillow 12.3
Resample.c (triangle filter, support scaled while downscaling), so lossless
fixtures compare exactly; JPEG fixtures are intentionally excluded.

Usage: python export_golden_fit.py <fixtures-dir> <golden-dir> <size> <name> [<name>...]
For each PNG fixture writes:
  <name>.fit_resized.npy  uint8 HxWx3  (fit resize only)
  <name>.fit_tensor.npy   float32 [3,size,size] (padded + normalized)
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps


def ensure_rgb(source: Image.Image) -> Image.Image:
    image = ImageOps.exif_transpose(source)
    if image.mode != "RGB":
        rgba = image.convert("RGBA")
        canvas = Image.new("RGBA", rgba.size, (255, 255, 255, 255))
        canvas.alpha_composite(rgba)
        image = canvas.convert("RGB")
    else:
        image = image.convert("RGB")
    return image


def fit_size(width: int, height: int, box: int) -> tuple[int, int]:
    scale = min(box / width, box / height)
    return max(1, int(width * scale)), max(1, int(height * scale))


def main(fixtures_dir: str, golden_dir: str, size: int, names: list[str]) -> None:
    root = Path(fixtures_dir)
    out = Path(golden_dir)
    out.mkdir(parents=True, exist_ok=True)

    for name in names:
        path = root / name
        with Image.open(path) as source:
            image = ensure_rgb(source)

            width, height = image.size
            fitted_w, fitted_h = fit_size(width, height, size)
            fitted = image.resize((fitted_w, fitted_h), Image.Resampling.BILINEAR)
            np.save(out / f"{Path(name).stem}.fit_resized.npy", np.asarray(fitted, dtype=np.uint8))

            canvas = Image.new("RGB", (size, size), (0, 0, 0))
            canvas.paste(fitted, ((size - fitted_w) // 2, (size - fitted_h) // 2))
            padded = np.asarray(canvas, dtype=np.uint8)

            tensor = padded.astype(np.float32) / 255.0
            tensor = (tensor - 0.5) / 0.5
            tensor = np.transpose(tensor, (2, 0, 1))
            np.save(out / f"{Path(name).stem}.fit_tensor.npy", tensor)
            print(f"{name}: {width}x{height} -> {fitted_w}x{fitted_h} -> {size}x{size}")


if __name__ == "__main__":
    if len(sys.argv) < 5:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4:])
