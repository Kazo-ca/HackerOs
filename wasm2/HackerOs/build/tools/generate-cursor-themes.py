#!/usr/bin/env python3
"""Draws the PNG cursors of the built-in cursor themes (ADR 0205).

Every cursor is a 32 x 32 PNG: a shape filled with the theme's dark color, a light outline and
a soft glow, drawn at four times the size and scaled down for smooth edges. The hot spots used
here are the ones declared in Shared/HackerOs.Theming.Abstractions/CursorThemeCatalog.cs: change
both together.

Usage (from wasm2/HackerOs): python3 build/tools/generate-cursor-themes.py
Needs Pillow. The PNG files are committed; run this only to change the drawings.
"""

from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

SIZE = 32
SCALE = 4
BIG = SIZE * SCALE

OUTPUT = Path(__file__).resolve().parents[2] / "Platform/HackerOs.Theming.Blazor/wwwroot/cursors"

THEMES = {
    "hackeros-neon": {"fill": (8, 14, 8, 255), "line": (57, 255, 20, 255), "mark": (57, 255, 20, 255)},
    "gothic-crimson": {"fill": (18, 6, 8, 255), "line": (210, 31, 60, 255), "mark": (232, 220, 200, 255)},
}

ARROW = [(2, 1), (2, 24), (7, 19), (11, 28), (14.5, 26.5), (10.5, 18), (17.5, 18)]


def s(points):
    """Scales 32-unit coordinates to the drawing size."""
    return [(x * SCALE, y * SCALE) for x, y in points]


def box(x0, y0, x1, y1):
    return (x0 * SCALE, y0 * SCALE, x1 * SCALE, y1 * SCALE)


def new_mask():
    return Image.new("L", (BIG, BIG), 0)


def arrow_mask(draw):
    draw.polygon(s(ARROW), fill=255)


def hand_mask(draw, closed):
    """An open hand (grab) or a closed fist (grabbing)."""
    draw.rounded_rectangle(box(7, 13, 25, 28), radius=5 * SCALE, fill=255)
    tops = [14, 13, 13, 14] if closed else [11, 9, 10, 12]
    for index, top in enumerate(tops):
        left = 7.5 + index * 4.3
        draw.rounded_rectangle(box(left, top, left + 4, 19), radius=2 * SCALE, fill=255)
    thumb = [(7, 21), (5, 18), (7, 17), (9, 19)] if closed else [(7, 22), (3, 17), (5, 15), (9, 18)]
    draw.polygon(s(thumb), fill=255)


def pointer_mask(draw):
    draw.rounded_rectangle(box(9, 1, 14, 17), radius=2.5 * SCALE, fill=255)
    for left, top in [(14, 10), (18, 11), (22, 12.5)]:
        draw.rounded_rectangle(box(left - 0.5, top, left + 3.5, 19), radius=2 * SCALE, fill=255)
    draw.rounded_rectangle(box(6, 14, 25.5, 29), radius=5 * SCALE, fill=255)
    draw.polygon(s([(7, 23), (3, 18), (5, 16), (9, 19)]), fill=255)


def ibeam_mask(draw):
    draw.rectangle(box(14.5, 5, 17.5, 27), fill=255)
    draw.rounded_rectangle(box(10, 3, 22, 6.5), radius=1.5 * SCALE, fill=255)
    draw.rounded_rectangle(box(10, 25.5, 22, 29), radius=1.5 * SCALE, fill=255)


def hourglass_mask(draw, x0=7, y0=2, x1=25, y1=30):
    mid_x = (x0 + x1) / 2
    mid_y = (y0 + y1) / 2
    cap = (y1 - y0) / 10
    draw.rounded_rectangle(box(x0 - 1, y0, x1 + 1, y0 + cap), radius=SCALE, fill=255)
    draw.rounded_rectangle(box(x0 - 1, y1 - cap, x1 + 1, y1), radius=SCALE, fill=255)
    draw.polygon(s([(x0 + 1, y0 + cap), (x1 - 1, y0 + cap), (mid_x + 1.5, mid_y), (mid_x - 1.5, mid_y)]), fill=255)
    draw.polygon(s([(mid_x - 1.5, mid_y), (mid_x + 1.5, mid_y), (x1 - 1, y1 - cap), (x0 + 1, y1 - cap)]), fill=255)


def hourglass_sand(draw, color, x0=7, y0=2, x1=25, y1=30):
    mid_x = (x0 + x1) / 2
    mid_y = (y0 + y1) / 2
    height = y1 - y0
    draw.polygon(s([(mid_x - 3, y0 + height * 0.28), (mid_x + 3, y0 + height * 0.28), (mid_x, mid_y - 1)]), fill=color)
    draw.polygon(s([(mid_x, mid_y + height * 0.12), (x1 - 3.5, y1 - height * 0.14), (x0 + 3.5, y1 - height * 0.14)]), fill=color)


def crosshair_mask(draw):
    draw.rectangle(box(15, 2, 17, 12), fill=255)
    draw.rectangle(box(15, 20, 17, 30), fill=255)
    draw.rectangle(box(2, 15, 12, 17), fill=255)
    draw.rectangle(box(20, 15, 30, 17), fill=255)
    draw.ellipse(box(14.5, 14.5, 17.5, 17.5), fill=255)


def double_arrow_mask(draw):
    """A vertical two-headed arrow; the other resize cursors rotate it."""
    draw.rectangle(box(14.5, 7, 17.5, 25), fill=255)
    draw.polygon(s([(9, 10), (16, 2), (23, 10)]), fill=255)
    draw.polygon(s([(9, 22), (16, 30), (23, 22)]), fill=255)


def move_mask(draw):
    draw.rectangle(box(14.5, 6, 17.5, 26), fill=255)
    draw.rectangle(box(6, 14.5, 26, 17.5), fill=255)
    draw.polygon(s([(11, 8), (16, 2), (21, 8)]), fill=255)
    draw.polygon(s([(11, 24), (16, 30), (21, 24)]), fill=255)
    draw.polygon(s([(8, 11), (2, 16), (8, 21)]), fill=255)
    draw.polygon(s([(24, 11), (30, 16), (24, 21)]), fill=255)


def not_allowed_mask(draw):
    draw.ellipse(box(4, 4, 28, 28), fill=255)
    draw.ellipse(box(8, 8, 24, 24), fill=0)
    draw.polygon(s([(9, 7), (25, 23), (23, 25), (7, 9)]), fill=255)


def outline(mask):
    return ImageChops.subtract(mask.filter(ImageFilter.MaxFilter(2 * SCALE + 3)), mask)


def render(mask, palette, marks=None):
    """Fills the mask, outlines it and adds the glow; marks draws symbols over the result."""
    ring = outline(mask)
    glow = ImageChops.add(mask, ring).filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.GaussianBlur(SCALE * 1.2))

    image = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    glow_layer = Image.new("RGBA", (BIG, BIG), palette["line"][:3] + (0,))
    glow_layer.putalpha(glow.point(lambda value: int(value * 0.55)))
    image = Image.alpha_composite(image, glow_layer)
    image.paste(palette["fill"], (0, 0), mask)
    image.paste(palette["line"], (0, 0), ring)
    if marks is not None:
        marks(ImageDraw.Draw(image), palette)
    return image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def masked(*drawers):
    mask = new_mask()
    draw = ImageDraw.Draw(mask)
    for drawer in drawers:
        drawer(draw)
    return mask


def question_mark(draw, palette):
    font = ImageFont.load_default(size=19 * SCALE)
    draw.text((17 * SCALE, 7 * SCALE), "?", font=font, fill=palette["line"], stroke_width=SCALE, stroke_fill=palette["fill"])


def knuckles(draw, palette):
    for index in range(1, 4):
        x = (7.5 + index * 4.3 - 0.15) * SCALE
        draw.line([(x, 14 * SCALE), (x, 19 * SCALE)], fill=palette["line"], width=SCALE)


def small_hourglass(draw, palette):
    glass = masked(lambda d: hourglass_mask(d, 18, 15, 28, 30))
    draw.bitmap((0, 0), outline(glass), fill=palette["line"])
    draw.bitmap((0, 0), glass, fill=palette["fill"])
    hourglass_sand(draw, palette["mark"], 18, 15, 28, 30)


def build(palette):
    def rotated(angle):
        return masked(double_arrow_mask).rotate(angle, resample=Image.Resampling.BICUBIC, center=(BIG / 2, BIG / 2))

    return {
        "default": render(masked(arrow_mask), palette),
        "pointer": render(masked(pointer_mask), palette),
        "text": render(masked(ibeam_mask), palette),
        "wait": render(masked(hourglass_mask), palette, lambda d, p: hourglass_sand(d, p["mark"])),
        "progress": render(masked(arrow_mask), palette, small_hourglass),
        "help": render(masked(arrow_mask), palette, question_mark),
        "crosshair": render(masked(crosshair_mask), palette),
        "move": render(masked(move_mask), palette),
        "not-allowed": render(masked(not_allowed_mask), palette),
        "grab": render(masked(lambda d: hand_mask(d, closed=False)), palette),
        "grabbing": render(masked(lambda d: hand_mask(d, closed=True)), palette, knuckles),
        "ns-resize": render(rotated(0), palette),
        "ew-resize": render(rotated(90), palette),
        "nesw-resize": render(rotated(-45), palette),
        "nwse-resize": render(rotated(45), palette),
    }


def main():
    for theme_id, palette in THEMES.items():
        folder = OUTPUT / theme_id
        folder.mkdir(parents=True, exist_ok=True)
        for name, image in build(palette).items():
            image.save(folder / f"{name}.png", optimize=True)
        print(f"{folder}: {len(list(folder.glob('*.png')))} cursors")


if __name__ == "__main__":
    main()
