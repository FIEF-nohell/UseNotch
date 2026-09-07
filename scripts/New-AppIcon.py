"""Draws the UseNotch application icon and writes a multi-size .ico.

The mark is a usage ring with a notch bitten out of its top edge: the ring is what the overlay shows,
and the notch is where it lives. Nothing here is copied from another project's artwork.

Run with: python scripts/New-AppIcon.py
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

BACKGROUND = (16, 18, 22, 255)      # the overlay surface colour
RING = (103, 214, 163, 255)         # the "normal" severity token
RING_TRACK = (52, 59, 70, 255)      # the ring track token
SIZES = (256, 128, 64, 48, 32, 24, 16)
SUPERSAMPLE = 8


def draw_icon(size: int) -> Image.Image:
    """Draws at a large size and downsamples, so small icons keep clean edges."""
    scale = size * SUPERSAMPLE
    image = Image.new("RGBA", (scale, scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # Rounded square body.
    inset = scale * 0.06
    radius = scale * 0.22
    draw.rounded_rectangle(
        [inset, inset, scale - inset, scale - inset],
        radius=radius,
        fill=BACKGROUND,
    )

    # The notch: a rounded bite out of the top edge, drawn as a hole in the body.
    notch_width = scale * 0.40
    notch_height = scale * 0.115
    notch_left = (scale - notch_width) / 2
    draw.rounded_rectangle(
        [notch_left, inset - notch_height, notch_left + notch_width, inset + notch_height],
        radius=notch_height,
        fill=(0, 0, 0, 0),
    )

    # Usage ring: a full track with an arc over roughly three quarters of it.
    stroke = scale * 0.085
    margin = scale * 0.30
    box = [margin, margin, scale - margin, scale - margin]
    draw.arc(box, start=0, end=360, fill=RING_TRACK, width=int(stroke))
    draw.arc(box, start=-90, end=170, fill=RING, width=int(stroke))

    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = os.path.join(root, "src", "UseNotch.App", "Assets", "usenotch.ico")
    frames = [draw_icon(size) for size in SIZES]
    frames[0].save(target, format="ICO", sizes=[(size, size) for size in SIZES])
    print(f"wrote {target} ({os.path.getsize(target)} bytes, sizes {SIZES})")

    preview = os.path.join(root, "artifacts", "icon-preview.png")
    os.makedirs(os.path.dirname(preview), exist_ok=True)
    draw_icon(256).save(preview)
    print(f"wrote {preview}")


if __name__ == "__main__":
    main()
