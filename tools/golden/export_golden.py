"""Exports golden baselines per fixture following scripts/model_utils.py exactly.

Usage: python export_golden.py <fixtures-dir> <golden-dir> <model-onnx> [tags-csv]
For each fixture writes:
  <name>.stage_ensure_rgb.npy  uint8 HxWx3  (exif_transpose + mode unify + alpha composite)
  <name>.stage_padded.npy      uint8 SxSx3  (white square pad)
  <name>.stage_resized.npy     uint8 448x448x3 (bicubic)
  <name>.stage_tensor.npy      float32 [1,3,448,448] (BGR, [-1,1], CHW)
For fixtures listed in BASELINE set also runs the ONNX model and writes:
  <name>.probs.npy             float32 [16473]
  <name>.baseline.json         top50 + threshold-selected summary
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps

from model_utils import ensure_rgb

BASELINE = {"landscape_rgb.png", "rgba_alpha.png", "photo_jpeg.jpg"}
THRESHOLD = 0.6094


def npy_write(path: Path, array: np.ndarray) -> None:
    np.save(path, array)


def run_model(onnx_path: str, tensor: np.ndarray, catalog: list[tuple[int, str, str]]):
    import onnxruntime as ort

    session = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    inputs = session.get_inputs()
    name = next((i.name for i in inputs if i.name == "images"), inputs[0].name)
    logits = session.run(None, {name: tensor})[0][0]
    probs = 1.0 / (1.0 + np.exp(-logits.astype(np.float64)))
    probs = probs.astype(np.float32)

    order = np.argsort(-probs, kind="stable")
    top50 = [
        {"id": int(i), "name": catalog[i][1], "group": catalog[i][2], "prob": float(probs[i])}
        for i in order[:50]
    ]
    selected = sorted(int(i) for i in np.nonzero(probs >= THRESHOLD)[0])
    digest = hashlib.sha256(
        ",".join(f"{i}:{catalog[i][1]}" for i in selected).encode()
    ).hexdigest()
    return probs, {
        "top50": top50,
        "threshold": THRESHOLD,
        "selectedCount": len(selected),
        "selectedIdsSha256": digest,
        "selectedTop10": [
            {"id": int(i), "name": catalog[i][1], "prob": float(probs[i])} for i in selected[:10]
        ],
    }


def read_catalog(path: Path) -> list[tuple[int, str, str]]:
    import csv

    with open(path, encoding="utf-8", newline="") as f:
        return [(int(r["id"]), r["name"], r["group"]) for r in csv.DictReader(f)]


def main(fixtures_dir: str, golden_dir: str, model_onnx: str, tags_csv: str | None) -> None:
    root = Path(fixtures_dir)
    out = Path(golden_dir)
    out.mkdir(parents=True, exist_ok=True)
    catalog = read_catalog(Path(tags_csv)) if tags_csv else None

    fixtures = sorted(p for p in root.iterdir() if p.is_file())
    for path in fixtures:
        name = path.stem
        with Image.open(path) as source:
            image = ensure_rgb(source)
            arr = np.asarray(image, dtype=np.uint8)
            npy_write(out / f"{name}.stage_ensure_rgb.npy", arr)

            width, height = image.size
            side = max(width, height)
            canvas = Image.new("RGB", (side, side), (255, 255, 255))
            canvas.paste(image, ((side - width) // 2, (side - height) // 2))
            padded = np.asarray(canvas, dtype=np.uint8)
            npy_write(out / f"{name}.stage_padded.npy", padded)

            resized_img = canvas.resize((448, 448), Image.Resampling.BICUBIC)
            resized = np.asarray(resized_img, dtype=np.uint8)
            npy_write(out / f"{name}.stage_resized.npy", resized)

        t = resized.astype(np.float32)
        t = t[:, :, ::-1].copy() / 255.0
        t = (t - 0.5) / 0.5
        tensor = np.transpose(t, (2, 0, 1))[None]
        npy_write(out / f"{name}.stage_tensor.npy", tensor)
        print(f"{path.name}: ensure_rgb {arr.shape}, padded {padded.shape}, tensor {tensor.shape}")

        if catalog is not None and path.name in BASELINE:
            probs, report = run_model(model_onnx, tensor, catalog)
            npy_write(out / f"{name}.probs.npy", probs)
            (out / f"{name}.baseline.json").write_text(
                json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
            )
            print(f"  model baseline: {report['selectedCount']} tags >= {THRESHOLD}, "
                  f"sha256 {report['selectedIdsSha256'][:12]}…, top1 {report['top50'][0]['name']} "
                  f"{report['top50'][0]['prob']:.4f}")


if __name__ == "__main__":
    main(*sys.argv[1:5])
