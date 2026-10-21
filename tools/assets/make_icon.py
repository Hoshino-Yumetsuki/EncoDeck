# SPDX-License-Identifier: GPL-3.0-only
# Generates EncoDeck's original app icon (no third-party artwork): one earbud with two sound
# waves on a rounded, bright blue tile in the flat style of phone earbud apps (solid inner wave,
# translucent outer wave), drawn at 4096 px and downsampled for clean edges.
# Usage: python -I tools/assets/make_icon.py src/EncoDeck.App/Assets  (requires Pillow)
import math
import sys
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw, ImageFilter

out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
S = 4096


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def diagonal_gradient(size, start, end):
    """Top-left → bottom-right gradient."""
    small = Image.new("RGBA", (256, 256))
    px = small.load()
    for y in range(256):
        for x in range(256):
            px[x, y] = lerp(start, end, (x + y) / 510)
    return small.resize((size, size), Image.BICUBIC)


def tile_mask(margin, radius):
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, S - margin, S - margin], radius=radius, fill=255)
    return mask


def ellipse_layer(cx, cy, rx, ry, angle=0):
    layer = Image.new("L", (S, S), 0)
    ImageDraw.Draw(layer).ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=255)
    return layer.rotate(angle, resample=Image.BICUBIC, center=(cx, cy)) if angle else layer


def earbud():
    """Side view of an in-ear earbud: rounded body, silicone tip towards the ear, stem hanging down."""
    body = ellipse_layer(1820, 1600, 600, 540, angle=18)
    stem = Image.new("L", (S, S), 0)
    ImageDraw.Draw(stem).rounded_rectangle([1820 - 210, 1600, 1820 + 210, 3230], radius=210, fill=255)
    stem = stem.rotate(9, resample=Image.BICUBIC, center=(1820, 1600))
    shell = ImageChops.lighter(body, stem)
    # The tip sits in the body's side, a third of it hidden behind the shell.
    tip = ellipse_layer(1330, 1470, 300, 340, angle=18)
    return shell, tip


def wave(radius, width, start=-46, end=46):
    layer = Image.new("L", (S, S), 0)
    cx, cy = 2120, 1540
    d = ImageDraw.Draw(layer)
    d.arc([cx - radius, cy - radius, cx + radius, cy + radius], start, end, fill=255, width=width)
    # Round the arc ends.
    for angle in (start, end):
        a = math.radians(angle)
        mx, my = cx + (radius - width / 2) * math.cos(a), cy + (radius - width / 2) * math.sin(a)
        d.ellipse([mx - width / 2, my - width / 2, mx + width / 2, my + width / 2], fill=255)
    return layer


def render(size: int) -> Image.Image:
    margin, radius = 160, 920
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = tile_mask(margin, radius)
    img.paste(diagonal_gradient(S, (66, 148, 255, 255), (22, 94, 250, 255)), (0, 0), mask)

    # Soft light from the top-left corner.
    glow = Image.new("L", (S, S), 0)
    ImageDraw.Draw(glow).ellipse([-900, -1400, 2600, 1900], fill=40)
    glow = ImageChops.multiply(glow.filter(ImageFilter.GaussianBlur(500)), mask)
    img.alpha_composite(Image.merge("RGBA", (Image.new("L", (S, S), 255),) * 3 + (glow,)))

    shell, tip = earbud()
    inner_wave, outer_wave = wave(1060, 210), wave(1480, 210, -40, 40)
    waves = ImageChops.lighter(inner_wave, outer_wave)
    # Drop shadow under the white shapes.
    shapes = ImageChops.lighter(ImageChops.lighter(shell, tip), waves)
    shadow = shapes.filter(ImageFilter.GaussianBlur(90)).point(lambda a: int(a * 0.18))
    shadow = ImageChops.multiply(ImageChops.offset(shadow, 0, 70), mask)
    img.alpha_composite(Image.merge("RGBA", (Image.new("L", (S, S), 8), Image.new("L", (S, S), 40), Image.new("L", (S, S), 120), shadow)))

    white = Image.new("L", (S, S), 255)
    ice = (Image.new("L", (S, S), 196), Image.new("L", (S, S), 222), Image.new("L", (S, S), 255))
    img.alpha_composite(Image.merge("RGBA", ice + (tip,)))
    img.alpha_composite(Image.merge("RGBA", (white, white, white, shell)))
    img.alpha_composite(Image.merge("RGBA", (white, white, white, inner_wave)))
    img.alpha_composite(Image.merge("RGBA", (white, white, white, outer_wave.point(lambda a: int(a * 0.55)))))

    # Small sizes: a little extra contrast keeps the shapes crisp.
    result = img.resize((size, size), Image.LANCZOS)
    if size <= 32:
        result = result.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=0))
    return result


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [render(sz) for sz in sizes]
images[-1].save(out / "EncoDeck.ico", sizes=[(sz, sz) for sz in sizes], append_images=images[:-1])
render(256).save(out / "EncoDeck-256.png")
render(48).save(out / "EncoDeck-48.png")
print("wrote", out)
