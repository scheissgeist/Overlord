"""Composite the Overlord Workshop hero from REAL assets — no generated game content.

Why: About/Preview.png reads as AI-made, and every tell is in the ILLUSTRATED game
content (a soft-shaded hoodie figure that is not a RimWorld pawn, three panels of
fake UI with scribble placeholder text, generic painted floor tiles). The wordmark
itself is good. So this rebuilds the hero from parts that are actually real:

  - wordmark block  : cropped from About/Preview.png (existing gold Egyptian slab)
  - game content    : a REAL in-game screenshot, never illustrated
  - ground/frame    : brand/concepts/overlord-hero-c-reticle-plate.png (nano-banana,
                      background plate only — deliberately contains no game content)

Output: brand/hero/overlord-hero-<name>.png at 1280x720 (Workshop Preview size).

Run:  python tools/build_workshop_hero.py <screenshot.png>
"""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
PREVIEW = ROOT / "About" / "Preview.png"
# Ground plate only. NOT the reticle plate: its command-lines were drawn to route
# from side panels into a colonist, and with no panels in this layout they point at
# nothing and read as gold clutter over the map. Verified by rendering v2.
PLATE = ROOT / "brand" / "concepts" / "overlord-hero-b-parchment-desk.png"
OUT_DIR = ROOT / "brand" / "hero"

W, H = 1280, 720
GOLD = (210, 169, 93)
GROUND = (9, 11, 10)

# Wordmark block in Preview.png, measured by cropping and looking at it.
WORDMARK_BOX = (420, 20, 1250, 330)


def load_wordmark():
    """Gold wordmark on near-black, as a mask-composited layer."""
    src = Image.open(PREVIEW).convert("RGB").crop(WORDMARK_BOX)
    # The block is gold-on-near-black; use luminance as the alpha so the dark
    # ground drops out and only the gold marks carry over.
    lum = src.convert("L")
    layer = Image.new("RGBA", src.size, GOLD + (0,))
    layer.putalpha(lum.point(lambda v: 0 if v < 28 else min(255, int(v * 1.35))))
    return layer


def build(shot_path: Path, name: str):
    shot = Image.open(shot_path).convert("RGB")

    canvas = Image.new("RGB", (W, H), GROUND)

    # ── Ground: flat brand near-black with a single warm falloff from the right.
    # The generated plates were tried here and BOTH failed for the same reason: any
    # plate with its own internal structure (a parchment edge, a frame border) shows
    # a visible vertical band where the screenshot's feather crosses it. A ground
    # has no business having features. One decisive light, per the brand doc.
    ground = Image.new("RGB", (W, H), GROUND)
    gd = ImageDraw.Draw(ground)
    for x in range(W):
        t = max(0.0, (x / W - 0.30) / 0.70)
        gd.line([(x, 0), (x, H)], fill=(
            int(GROUND[0] + (36 - GROUND[0]) * t * t),
            int(GROUND[1] + (28 - GROUND[1]) * t * t),
            int(GROUND[2] + (20 - GROUND[2]) * t * t),
        ))
    canvas.paste(ground, (0, 0))

    # ── Game content: real screenshot, right side, feathered into the ground.
    # Crop first: the raw capture carries a clipped Health panel in the lower-left
    # and dead map at the top. Trim both before scaling so neither survives.
    sw, sh = shot.size
    shot = shot.crop((int(sw * 0.16), int(sh * 0.04), sw, int(sh * 0.90)))

    # Cover the right portion fully — scale by whichever axis needs more, then
    # center-crop, so there is never a hard edge inside the frame.
    target_w = int(W * 0.66)
    scale = max(target_w / shot.width, H / shot.height)
    shot_r = shot.resize((int(shot.width * scale), int(shot.height * scale)), Image.LANCZOS)
    # Bias the crop so the big central dirt mound is pushed off-center rather than
    # sitting dead middle and eating the composition.
    left = int((shot_r.width - target_w) * 0.18)
    top = int((shot_r.height - H) * 0.30)
    shot_r = shot_r.crop((left, top, left + target_w, top + H))

    sx, sy = W - target_w, 0

    # Wide feather on the left edge so the map dissolves into the plate instead of
    # ending on a visible vertical seam.
    mask = Image.new("L", shot_r.size, 255)
    d = ImageDraw.Draw(mask)
    feather = int(target_w * 0.42)
    for i in range(feather):
        t = i / feather
        d.line([(i, 0), (i, shot_r.height)], fill=int(255 * (t * t)))
    mask = mask.filter(ImageFilter.GaussianBlur(24))
    canvas.paste(shot_r, (sx, sy), mask)

    # ── Vignette so the corners fall to brand ground.
    vig = Image.new("L", (W, H), 0)
    dv = ImageDraw.Draw(vig)
    dv.ellipse((-W // 3, -H // 3, W + W // 3, H + H // 3), fill=255)
    vig = vig.filter(ImageFilter.GaussianBlur(190))
    canvas = Image.composite(canvas, Image.new("RGB", (W, H), GROUND), vig)

    # ── Wordmark, left third, vertically centered.
    wm = load_wordmark()
    wm_w = int(W * 0.42)
    wm = wm.resize((wm_w, int(wm.height * wm_w / wm.width)), Image.LANCZOS)
    # Lower-left anchor: asymmetric (de-slop rule 19) and clear of the plate's
    # reticle brackets, which sit in the upper-left third.
    canvas.paste(wm, (int(W * 0.04), int(H * 0.60)), wm)

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    out = OUT_DIR / f"overlord-hero-{name}.png"
    canvas.save(out)
    print(f"saved {out} ({out.stat().st_size} bytes)")
    return out


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit("usage: build_workshop_hero.py <screenshot> [name]")
    build(Path(sys.argv[1]), sys.argv[2] if len(sys.argv) > 2 else "v1")
