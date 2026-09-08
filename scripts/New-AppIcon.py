"""Generates the UseNotch mark: an SVG logo and the multi-size application .ico.

The mark is a quota ring with a notch cut clean through its top, sitting on the overlay's own surface
colour, ending in a head in the second provider accent. The ring is what the overlay shows and the notch
is where it lives, so the icon and the running application read as one idea. Nothing here is copied from
other artwork.

Both outputs come from the geometry below, so the vector logo and the raster icon cannot drift apart.
Run with: python scripts/New-AppIcon.py
"""

from __future__ import annotations

import math
import os

from PIL import Image, ImageDraw

# Brand tokens. The tile is the overlay surface and the accents are the two provider colours the
# application already uses, so the icon is built from the running palette rather than a separate one.
TILE = (11, 13, 16, 255)
ARC = (31, 214, 159, 255)       # OpenAI accent
HEAD = (255, 122, 77, 255)      # Anthropic accent

# Geometry in a 0..1 canvas. Every value is a fraction of the icon's edge, so one definition scales to
# every raster size and to the vector.
INSET = 0.050                   # keeps the tile off the canvas edge so it survives Windows padding
CORNER = 0.205                  # tile corner radius
RING_CENTRE = (0.5, 0.5)
RING_RADIUS = 0.205             # centreline of the stroke
RING_STROKE = 0.098
RING_START_DEGREES = -58.0      # leaves the ring at the notch's right wall
RING_SWEEP_DEGREES = 268.0
HEAD_RADIUS = 0.056
NOTCH_HALF_WIDTH = 0.105        # half the flat span of the notch cut through the ring
NOTCH_FLOOR = 0.380             # reaches past the inner edge of the ring, so the cut is complete
NOTCH_CORNER = 0.040            # convex rounding at the notch floor

SIZES = (256, 128, 64, 48, 32, 24, 16)
SUPERSAMPLE = 8
CURVE_STEPS = 24


def quadratic(start, control, end, steps=CURVE_STEPS):
    """Samples a quadratic Bezier, used for the concave flares and the notch floor corners."""
    points = []
    for step in range(1, steps + 1):
        t = step / steps
        u = 1.0 - t
        points.append((
            u * u * start[0] + 2 * u * t * control[0] + t * t * end[0],
            u * u * start[1] + 2 * u * t * control[1] + t * t * end[1],
        ))
    return points


def corner(centre, radius, from_degrees, to_degrees, steps=CURVE_STEPS):
    """Samples one circular corner arc of the tile."""
    points = []
    for step in range(steps + 1):
        angle = math.radians(from_degrees + (to_degrees - from_degrees) * step / steps)
        points.append((centre[0] + radius * math.cos(angle), centre[1] + radius * math.sin(angle)))
    return points


def tile_outline():
    """The tile: a plain rounded square. The notch belongs to the ring, not to this edge."""
    left, top = INSET, INSET
    right, bottom = 1.0 - INSET, 1.0 - INSET
    r = CORNER

    points = [(left + r, top), (right - r, top)]
    points += corner((right - r, top + r), r, -90, 0)
    points.append((right, bottom - r))
    points += corner((right - r, bottom - r), r, 0, 90)
    points.append((left + r, bottom))
    points += corner((left + r, bottom - r), r, 90, 180)
    points.append((left, top + r))
    points += corner((left + r, top + r), r, 180, 270)
    return points


def notch_outline():
    """The notch that interrupts the ring, drawn in the tile colour so it reads as a cut.

    It is a flat-floored bite with rounded floor corners, the same silhouette the overlay draws. Its
    walls run past the top of the tile so only the floor and the two walls ever meet the ring.
    """
    left = 0.5 - NOTCH_HALF_WIDTH
    right = 0.5 + NOTCH_HALF_WIDTH
    top = INSET  # the tile edge, so the notch never draws outside the tile it is cut into
    floor = NOTCH_FLOOR
    c = NOTCH_CORNER

    points = [(left, top), (left, floor - c)]
    points += quadratic((left, floor - c), (left, floor), (left + c, floor))
    points.append((right - c, floor))
    points += quadratic((right - c, floor), (right, floor), (right, floor - c))
    points.append((right, top))
    return points


def draw_icon(size: int) -> Image.Image:
    """Draws at a multiple of the target size and downsamples, so small icons keep clean edges."""
    scale = size * SUPERSAMPLE
    image = Image.new("RGBA", (scale, scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.polygon([(x * scale, y * scale) for x, y in tile_outline()], fill=TILE)

    centre = (RING_CENTRE[0] * scale, RING_CENTRE[1] * scale)
    radius = RING_RADIUS * scale
    stroke = max(1, int(round(RING_STROKE * scale)))
    box = [centre[0] - radius, centre[1] - radius, centre[0] + radius, centre[1] + radius]
    # No unfilled track behind the sweep. At sixteen pixels a second grey ring only muddies
    # the shape, and the notch already carries the identity.
    draw.arc(box, start=RING_START_DEGREES, end=RING_START_DEGREES + RING_SWEEP_DEGREES,
             fill=ARC, width=stroke)

    # The head marks where the sweep ends, in the second accent, so both providers are present.
    end = math.radians(RING_START_DEGREES + RING_SWEEP_DEGREES)
    head = (centre[0] + radius * math.cos(end), centre[1] + radius * math.sin(end))
    head_radius = HEAD_RADIUS * scale
    draw.ellipse([head[0] - head_radius, head[1] - head_radius,
                  head[0] + head_radius, head[1] + head_radius], fill=HEAD)

    # The notch is drawn last, in the tile colour, so it cuts the ring rather than sitting on it.
    draw.polygon([(x * scale, y * scale) for x, y in notch_outline()], fill=TILE)

    return image.resize((size, size), Image.LANCZOS)


def to_hex(colour) -> str:
    return "#{:02X}{:02X}{:02X}".format(*colour[:3])


def build_svg(size: int = 256) -> str:
    """Emits the same geometry as a vector, for documentation and any future scalable use.

    The tile and the notch are written as real primitives rather than as the sampled polygon the
    raster path uses, so the file stays small and stays editable while the numbers still come from
    the constants above.
    """
    inset = INSET * size
    side = (1.0 - 2 * INSET) * size
    centre = (RING_CENTRE[0] * size, RING_CENTRE[1] * size)
    radius = RING_RADIUS * size
    start = math.radians(RING_START_DEGREES)
    end = math.radians(RING_START_DEGREES + RING_SWEEP_DEGREES)
    start_point = (centre[0] + radius * math.cos(start), centre[1] + radius * math.sin(start))
    end_point = (centre[0] + radius * math.cos(end), centre[1] + radius * math.sin(end))
    large = 1 if RING_SWEEP_DEGREES > 180 else 0

    notch_left = (0.5 - NOTCH_HALF_WIDTH) * size
    notch_right = (0.5 + NOTCH_HALF_WIDTH) * size
    floor = NOTCH_FLOOR * size
    c = NOTCH_CORNER * size
    notch = (
        "M {left:.3f},{top:.3f} L {left:.3f},{floor_start:.3f} "
        "Q {left:.3f},{floor:.3f} {left_c:.3f},{floor:.3f} "
        "L {right_c:.3f},{floor:.3f} Q {right:.3f},{floor:.3f} {right:.3f},{floor_start:.3f} "
        "L {right:.3f},{top:.3f} Z"
    ).format(left=notch_left, right=notch_right, top=inset, floor=floor,
             floor_start=floor - c, left_c=notch_left + c, right_c=notch_right - c)

    lines = [
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}"'
        ' height="{size}" role="img" aria-label="UseNotch">',
        "  <title>UseNotch</title>",
        '  <rect x="{inset:.3f}" y="{inset:.3f}" width="{side:.3f}" height="{side:.3f}"'
        ' rx="{corner:.3f}" fill="{tile}"/>',
        '  <path d="M {sx:.3f},{sy:.3f} A {r:.3f},{r:.3f} 0 {large} 1 {ex:.3f},{ey:.3f}"'
        ' fill="none" stroke="{arc}" stroke-width="{stroke:.3f}"/>',
        '  <circle cx="{ex:.3f}" cy="{ey:.3f}" r="{head:.3f}" fill="{headfill}"/>',
        '  <path d="{notch}" fill="{tile}"/>',
        "</svg>",
        "",
    ]

    return chr(10).join(lines).format(
        size=size, inset=inset, side=side, corner=CORNER * size, tile=to_hex(TILE),
        arc=to_hex(ARC), headfill=to_hex(HEAD), r=radius, stroke=RING_STROKE * size,
        sx=start_point[0], sy=start_point[1], ex=end_point[0], ey=end_point[1],
        large=large, head=HEAD_RADIUS * size, notch=notch,
    )

def main() -> None:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "src", "UseNotch.App", "Assets")

    icon = os.path.join(assets, "usenotch.ico")
    frames = [draw_icon(size) for size in SIZES]
    frames[0].save(icon, format="ICO", sizes=[(size, size) for size in SIZES])
    print("wrote {} ({} bytes, sizes {})".format(icon, os.path.getsize(icon), SIZES))

    svg = os.path.join(assets, "usenotch.svg")
    with open(svg, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(build_svg())
    print("wrote {}".format(svg))

    for name, size in (("usenotch-512.png", 512), ("usenotch-128.png", 128)):
        target = os.path.join(root, "docs", "brand", name)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        draw_icon(size).save(target)
        print("wrote {}".format(target))


if __name__ == "__main__":
    main()
