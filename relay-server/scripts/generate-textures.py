"""Generate seamless wood + parchment textures for the Overlord viewer HUD.

Source-faithful to the GPT-image-2 reference Sean approved 2026-05-02.

Outputs (all 8-bit PNG, tileable):
  public/textures/wood-dark.png        — dark hand-painted wood planks (256x256)
  public/textures/wood-mid.png         — mid-tone carved wood for inner frames (256x256)
  public/textures/parchment.png        — cream parchment surface (512x512)
  public/textures/wood-frame-edge.png  — wood frame edge tile, used with border-image (96x96)

Run from repo root:
  python relay-server/scripts/generate-textures.py
"""
from __future__ import annotations

import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

OUT_DIR = Path(__file__).resolve().parent.parent / "public" / "textures"
OUT_DIR.mkdir(parents=True, exist_ok=True)

# Palette pulled from the reference image Sean approved.
PARCHMENT_BASE = (232, 212, 168)
PARCHMENT_HIGHLIGHT = (240, 223, 181)
PARCHMENT_SHADOW = (200, 174, 130)
PARCHMENT_FIBER = (164, 134, 88)

WOOD_DARK_BASE = (62, 41, 24)
WOOD_DARK_HIGHLIGHT = (94, 64, 36)
WOOD_DARK_SHADOW = (32, 22, 14)
WOOD_DARK_GRAIN = (140, 96, 50)

WOOD_MID_BASE = (138, 94, 48)
WOOD_MID_HIGHLIGHT = (172, 122, 64)
WOOD_MID_SHADOW = (94, 60, 28)
WOOD_MID_GRAIN = (52, 32, 16)


def lerp(a: tuple, b: tuple, t: float) -> tuple:
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def value_noise_field(width: int, height: int, octaves: int, persistence: float, seed: int) -> list:
    """Wrap-tileable value noise via cosine sums on a torus."""
    rng = random.Random(seed)
    field = [[0.0] * width for _ in range(height)]
    amp_total = 0.0
    for octave in range(octaves):
        freq = 2 ** octave
        amp = persistence ** octave
        amp_total += amp
        # Generate a low-res grid that tiles, then upsample with cosine interp.
        gx = max(2, freq * 2)
        gy = max(2, freq * 2)
        grid = [[rng.random() for _ in range(gx)] for _ in range(gy)]
        for y in range(height):
            for x in range(width):
                fx = (x / width) * gx
                fy = (y / height) * gy
                ix0 = int(fx) % gx
                iy0 = int(fy) % gy
                ix1 = (ix0 + 1) % gx
                iy1 = (iy0 + 1) % gy
                tx = fx - int(fx)
                ty = fy - int(fy)
                # cosine smoothstep
                tx = (1 - math.cos(tx * math.pi)) * 0.5
                ty = (1 - math.cos(ty * math.pi)) * 0.5
                a = grid[iy0][ix0] * (1 - tx) + grid[iy0][ix1] * tx
                b = grid[iy1][ix0] * (1 - tx) + grid[iy1][ix1] * tx
                field[y][x] += (a * (1 - ty) + b * ty) * amp
    return [[v / amp_total for v in row] for row in field]


def make_wood_planks(size: int, base, highlight, shadow, grain, plank_height: int, seed: int) -> Image.Image:
    """Hand-painted wood: horizontal planks with grain stripes and value noise."""
    img = Image.new("RGB", (size, size), base)
    px = img.load()
    rng = random.Random(seed)

    # Base value noise across the whole tile.
    noise = value_noise_field(size, size, octaves=4, persistence=0.55, seed=seed)

    # Grain stripes: long horizontal bands at slightly different brightness.
    grain_field = [[0.0] * size for _ in range(size)]
    n_stripes = size // 3
    for _ in range(n_stripes):
        y_center = rng.randint(0, size - 1)
        thickness = rng.randint(1, 3)
        contrast = rng.uniform(-0.18, 0.22)
        for dy in range(-thickness, thickness + 1):
            y = (y_center + dy) % size
            falloff = 1.0 - abs(dy) / (thickness + 1)
            for x in range(size):
                jitter = (noise[y][x] - 0.5) * 0.3
                grain_field[y][x] += contrast * falloff * (0.7 + jitter)

    for y in range(size):
        plank_idx = y // plank_height
        plank_phase = (y % plank_height) / plank_height  # 0 at top of plank, ~1 at bottom
        # Plank seam darkening near top and bottom of each plank.
        seam = 0.0
        if plank_phase < 0.06:
            seam = -0.45 * (1.0 - plank_phase / 0.06)
        elif plank_phase > 0.94:
            seam = -0.45 * ((plank_phase - 0.94) / 0.06)
        plank_tone = ((plank_idx * 37) % 7 - 3) * 0.04  # plank-to-plank variation
        for x in range(size):
            n = noise[y][x]
            g = grain_field[y][x]
            t = max(0.0, min(1.0, n + g + seam + plank_tone + 0.5 - 0.5))
            if t < 0.5:
                color = lerp(shadow, base, t * 2)
            else:
                color = lerp(base, highlight, (t - 0.5) * 2)
            # Sparse darker grain flecks
            if rng.random() < 0.003:
                color = lerp(color, grain, 0.5)
            px[x, y] = color
    return img


def make_parchment(size: int) -> Image.Image:
    """Cream parchment with subtle fiber noise and a faint vignette per tile."""
    img = Image.new("RGB", (size, size), PARCHMENT_BASE)
    px = img.load()
    rng = random.Random(42)

    noise = value_noise_field(size, size, octaves=5, persistence=0.5, seed=7)
    fiber = value_noise_field(size, size, octaves=2, persistence=0.6, seed=13)

    for y in range(size):
        for x in range(size):
            n = noise[y][x]
            f = fiber[y][x]
            t = max(0.0, min(1.0, (n - 0.5) * 0.5 + 0.5))
            if t < 0.5:
                color = lerp(PARCHMENT_SHADOW, PARCHMENT_BASE, t * 2)
            else:
                color = lerp(PARCHMENT_BASE, PARCHMENT_HIGHLIGHT, (t - 0.5) * 2)

            # Faint paper fibers.
            if f > 0.78:
                color = lerp(color, PARCHMENT_FIBER, (f - 0.78) * 0.45)
            # Sparse darker specks
            if rng.random() < 0.0015:
                color = lerp(color, PARCHMENT_FIBER, 0.4)
            px[x, y] = color
    # Soften slightly so noise doesn't shimmer at small sizes.
    img = img.filter(ImageFilter.GaussianBlur(radius=0.4))
    return img


def make_wood_frame_edge(size: int = 96) -> Image.Image:
    """A square tile of dark wood, suitable for border-image: 24px slice.

    The center is transparent; the outer 24px is wood, with a slightly
    darker beveled inner edge so panels look carved, not pasted.
    """
    border = 24
    img = make_wood_planks(size, WOOD_DARK_BASE, WOOD_DARK_HIGHLIGHT, WOOD_DARK_SHADOW, WOOD_DARK_GRAIN, plank_height=size, seed=99).convert("RGBA")
    px = img.load()

    # Carve out the center
    for y in range(size):
        for x in range(size):
            if border <= x < size - border and border <= y < size - border:
                px[x, y] = (0, 0, 0, 0)
            else:
                # Beveled inner edge: darker pixel one step in from the carve line
                d = min(
                    x if x < size - x else size - x,
                    y if y < size - y else size - y,
                )
                # d=0 outermost, d=border-1 innermost
                if d >= border - 2:
                    r, g, b, a = px[x, y]
                    px[x, y] = (max(0, r - 32), max(0, g - 24), max(0, b - 16), a)
                elif d <= 1:
                    # outermost pixel slight highlight
                    r, g, b, a = px[x, y]
                    px[x, y] = (min(255, r + 18), min(255, g + 14), min(255, b + 8), a)
    return img


def main() -> None:
    print(f"Output directory: {OUT_DIR}")

    print("Generating wood-dark.png ...")
    wood_dark = make_wood_planks(
        size=256,
        base=WOOD_DARK_BASE,
        highlight=WOOD_DARK_HIGHLIGHT,
        shadow=WOOD_DARK_SHADOW,
        grain=WOOD_DARK_GRAIN,
        plank_height=64,
        seed=2611,
    )
    wood_dark.save(OUT_DIR / "wood-dark.png", optimize=True)

    print("Generating wood-mid.png ...")
    wood_mid = make_wood_planks(
        size=256,
        base=WOOD_MID_BASE,
        highlight=WOOD_MID_HIGHLIGHT,
        shadow=WOOD_MID_SHADOW,
        grain=WOOD_MID_GRAIN,
        plank_height=48,
        seed=771,
    )
    wood_mid.save(OUT_DIR / "wood-mid.png", optimize=True)

    print("Generating parchment.png ...")
    parchment = make_parchment(size=512)
    parchment.save(OUT_DIR / "parchment.png", optimize=True)

    print("Generating wood-frame-edge.png (border-image source) ...")
    edge = make_wood_frame_edge(size=96)
    edge.save(OUT_DIR / "wood-frame-edge.png", optimize=True)

    print("Done.")


if __name__ == "__main__":
    main()
