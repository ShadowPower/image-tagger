"""Generates preprocessing fixtures per DESIGN.md 3.3.

Usage: python gen_fixtures.py <fixtures-dir>
Outputs small deterministic images covering: landscape, portrait, odd sizes,
RGB/RGBA PNG, palette transparent PNG, grayscale, and 8 EXIF orientations.
"""

from pathlib import Path
import sys

from PIL import Image


def gradient(size, mode, seed_color=(30, 90, 160)):
    """Deterministic smooth gradient so resampling differences are detectable."""
    w, h = size
    img = Image.new(mode, size)
    px = img.load()
    for y in range(h):
        for x in range(w):
            r = (seed_color[0] + x * 2) % 256
            g = (seed_color[1] + y * 2) % 256
            b = (seed_color[2] + (x + y)) % 256
            if mode == "RGB":
                px[x, y] = (r, g, b)
            elif mode == "RGBA":
                a = (x * 255) // max(w - 1, 1)
                px[x, y] = (r, g, b, a)
            elif mode == "L":
                px[x, y] = (r + g + b) % 256
    return img


def checker_alpha(size):
    w, h = size
    img = Image.new("RGBA", size)
    px = img.load()
    for y in range(h):
        for x in range(w):
            on = (x // 8 + y // 8) % 2 == 0
            px[x, y] = (120, 40, 200, 255 if on else 0)
    return img


def jpeg_with_orientation(size, orientation, path):
    """Non-square content so orientation flips are visible; strips other EXIF."""
    w, h = size
    if orientation in (5, 6, 7, 8):
        content = gradient((h, w), "RGB")
    else:
        content = gradient((w, h), "RGB")
    exif = Image.Exif()
    exif[274] = orientation  # Orientation tag only
    content.save(path, "JPEG", quality=95, exif=exif)


def main(fixtures_dir: str) -> None:
    root = Path(fixtures_dir)
    root.mkdir(parents=True, exist_ok=True)

    gradient((600, 400), "RGB").save(root / "landscape_rgb.png")
    gradient((400, 600), "RGB").save(root / "portrait_rgb.png")
    gradient((201, 101), "RGB").save(root / "odd_rgb.png")

    # RGBA with a hard transparent band plus a soft alpha gradient.
    w, h = 300, 200
    rgba = gradient((w, h), "RGBA")
    px = rgba.load()
    for x in range(w):
        for y in range(0, 20):
            px[x, y] = (255, 255, 255, 0)
    rgba.save(root / "rgba_alpha.png")

    # Palette PNG with binary transparency.
    p_img = checker_alpha((256, 256)).convert("P", palette=Image.ADAPTIVE, colors=2)
    p_img.save(root / "palette_transparent.png")

    gradient((220, 140), "L").save(root / "grayscale.png")

    gradient((640, 480), "RGB").save(root / "photo_jpeg.jpg")

    for o in range(1, 9):
        jpeg_with_orientation((320, 240), o, root / f"exif_orientation_{o}.jpg")

    print(f"fixtures written to {root}")
    for f in sorted(root.iterdir()):
        print(f"  {f.name} ({f.stat().st_size} bytes)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "fixtures")
