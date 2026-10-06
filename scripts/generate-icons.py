#!/usr/bin/env python3
# Copyright (c) 2023-2026 ktsu-dev contributors
"""Draws the NoSleep tray icons and the NuGet package icon.

The mark is an eye: open and amber while NoSleep is holding an inhibitor, closed and grey while the
machine is free to sleep. Tray panels come in every shade, so the shapes are solid colour on
transparency with no background tile, and the open/closed silhouette carries the state even where a
panel renders the icon in monochrome.

Run from the repository root:

    python3 scripts/generate-icons.py

Requires Pillow. Every shape is drawn at 4x and downsampled, which is what keeps the 16px tray
rendering legible.
"""

from __future__ import annotations

import math
import os

from PIL import Image, ImageChops, ImageDraw

SUPERSAMPLE = 4

ACTIVE_COLOUR = (245, 176, 65, 255)  # amber: holding an inhibitor
IDLE_COLOUR = (150, 155, 165, 255)  # grey: the machine may sleep
PACKAGE_BACKGROUND = (30, 36, 48, 255)
TRANSPARENT = (0, 0, 0, 0)

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TRAY_ASSETS = os.path.join(REPO_ROOT, "NoSleep.Tool", "Assets")


def lens_mask(size: int, half_width: float, half_height: float) -> Image.Image:
    """Builds a mask holding an almond - the overlap of two equal circles, one above the other.

    A circle through the two corners and one tip has radius ``(w^2 + h^2) / 2h`` and sits ``R - h``
    away from the centre. Intersecting that circle with its mirror image gives the eye outline.
    """
    radius = (half_width * half_width + half_height * half_height) / (2 * half_height)
    offset = radius - half_height
    centre = size / 2

    upper = Image.new("L", (size, size), 0)
    ImageDraw.Draw(upper).ellipse(
        [centre - radius, centre + offset - radius, centre + radius, centre + offset + radius],
        fill=255,
    )

    lower = Image.new("L", (size, size), 0)
    ImageDraw.Draw(lower).ellipse(
        [centre - radius, centre - offset - radius, centre + radius, centre - offset + radius],
        fill=255,
    )

    return ImageChops.darker(upper, lower)


def draw_open_eye(image: Image.Image, size: int, colour: tuple[int, int, int, int]) -> None:
    """Paints an open eye: an almond outline with a pupil at its centre."""
    stroke = size * 0.055

    outline = lens_mask(size, size * 0.40, size * 0.255)
    interior = lens_mask(size, size * 0.40 - stroke * 1.6, size * 0.255 - stroke)

    # Subtracting the interior from the outline leaves the lid stroke on its own.
    lids = ImageChops.subtract(outline, interior)

    pupil = Image.new("L", (size, size), 0)
    pupil_radius = size * 0.105
    centre = size / 2
    ImageDraw.Draw(pupil).ellipse(
        [centre - pupil_radius, centre - pupil_radius, centre + pupil_radius, centre + pupil_radius],
        fill=255,
    )

    mark = Image.new("RGBA", (size, size), colour)
    image.paste(mark, (0, 0), ImageChops.lighter(lids, pupil))


def draw_closed_eye(image: Image.Image, size: int, colour: tuple[int, int, int, int]) -> None:
    """Paints a closed eye: a shallow lower lid with three lashes hanging off it."""
    draw = ImageDraw.Draw(image)
    stroke = int(size * 0.075)
    centre = size / 2

    # The lid is a shallow arc: a chord half as wide as the icon sagging by a tenth of it. Taken from
    # a circle that wide, only the flat base of the circle shows, which reads as a lid rather than as
    # a smile. R = (w^2 + h^2) / 2h puts that circle through both chord ends and the low point.
    half_width = size * 0.34
    sag = size * 0.11
    radius = (half_width * half_width + sag * sag) / (2 * sag)
    circle_y = centre + size * 0.13 - radius
    span_sin = (centre + size * 0.02 - circle_y) / radius
    span = math.degrees(math.asin(min(1.0, span_sin)))

    draw.arc(
        [centre - radius, circle_y - radius, centre + radius, circle_y + radius],
        start=span,
        end=180 - span,
        fill=colour,
        width=stroke,
    )

    # Lashes sit on the lid and point straight out from it, so they stay attached however the arc is
    # tuned above.
    for angle in (span + 14, 90.0, 180 - span - 14):
        radians = math.radians(angle)
        direction = (math.cos(radians), math.sin(radians))
        inner = radius + stroke * 0.6
        outer = inner + size * 0.10
        draw.line(
            [
                (centre + direction[0] * inner, circle_y + direction[1] * inner),
                (centre + direction[0] * outer, circle_y + direction[1] * outer),
            ],
            fill=colour,
            width=stroke,
        )


def render_tray_icon(size: int, colour: tuple[int, int, int, int], *, open_eye: bool) -> Image.Image:
    """Renders one tray icon at the requested size."""
    canvas = size * SUPERSAMPLE
    image = Image.new("RGBA", (canvas, canvas), TRANSPARENT)

    if open_eye:
        draw_open_eye(image, canvas, colour)
    else:
        draw_closed_eye(image, canvas, colour)

    return image.resize((size, size), Image.LANCZOS)


def render_package_icon(size: int) -> Image.Image:
    """Renders the NuGet icon: the active mark on a rounded dark tile."""
    canvas = size * SUPERSAMPLE
    image = Image.new("RGBA", (canvas, canvas), TRANSPARENT)
    ImageDraw.Draw(image).rounded_rectangle(
        [0, 0, canvas - 1, canvas - 1], radius=canvas * 0.22, fill=PACKAGE_BACKGROUND
    )

    mark = Image.new("RGBA", (canvas, canvas), TRANSPARENT)
    draw_open_eye(mark, canvas, ACTIVE_COLOUR)

    # The mark has transparent pixels inside it, so composite rather than paste: the tile has to show
    # through the gap between the lids and the pupil.
    inset = int(canvas * 0.09)
    image.alpha_composite(mark.resize((canvas - inset * 2, canvas - inset * 2), Image.LANCZOS), (inset, inset))

    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    """Writes every icon the build needs."""
    os.makedirs(TRAY_ASSETS, exist_ok=True)

    render_tray_icon(128, ACTIVE_COLOUR, open_eye=True).save(os.path.join(TRAY_ASSETS, "tray-active.png"))
    render_tray_icon(128, IDLE_COLOUR, open_eye=False).save(os.path.join(TRAY_ASSETS, "tray-idle.png"))
    render_package_icon(256).save(os.path.join(REPO_ROOT, "icon.png"))

    print(f"Wrote tray icons to {TRAY_ASSETS} and icon.png to {REPO_ROOT}")


if __name__ == "__main__":
    main()
