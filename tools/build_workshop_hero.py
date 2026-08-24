"""Compose the Overlord Workshop hero from REAL assets. No generated game content.

DESIGN NOTES (v5) — v4 was rejected as reading like AI slop. Named defects and fixes:

  1. WRONG COPY. The tagline read "Twitch viewers control your colonists" while the
     mod's headline feature is that it needs NEITHER Twitch NOR streaming. Fixed:
     the tagline is rebuilt, not cropped, and states what the mod does.
  2. DEAD SPACE. The wordmark floated in a large empty void — a gap, not negative
     space. Fixed: a left COLUMN with real content stacked in it (mark, wordmark,
     rule, tagline), aligned to a baseline grid.
  3. MUSH. A soft gradient dissolve between panel and screenshot is the
     "atmospheric blend" tell and contradicts --radius:0px / square RimWorld edges.
     Fixed: a HARD vertical edge with a 3px gold rule. Engraved, not blurred.
  4. NO HIERARCHY. The dirt mound dominated while the actual subject — colonists
     wearing viewer names — sat small. Fixed: crop hard onto the pawn cluster and
     drop the mound entirely.
  5. VESTIGIAL DECO. The star seal and deco rules were carried at a size where they
     read as noise. Fixed: the seal is scaled to a deliberate size, and the
     line-diamond-line rules are drawn at the column width instead of shrunk.

Palette/geometry locked by docs/BRAND_SYSTEM.md: ground #090b0a, one gold #d2a95d,
bright gold #f0cf82, 0px radius, 70/25/5 distribution, no gradients as decoration.

Run: python tools/build_workshop_hero.py <screenshot.png> [name]
"""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
PREVIEW = ROOT / "About" / "Preview.png"
OUT_DIR = ROOT / "brand" / "hero"

W, H = 1280, 720
GROUND = (9, 11, 10)
GOLD = (210, 169, 93)
GOLD_HI = (240, 207, 130)

# Wordmark letterforms only (no tagline, no rules) — measured from Preview.png.
WORDMARK_BOX = (430, 75, 1245, 265)
# The star-in-circle seal.
# Measured by thresholding Preview.png, not eyeballed — v5 clipped the ring top.
SEAL_BOX = (816, 29, 879, 90)

COLUMN_W = int(W * 0.335)      # left command column
PAD = 52                        # column inner padding
# Optical top: the stack is anchored to a grid value, not an arbitrary number, and
# sits high enough that the block reads as top-aligned rather than floating.
TOP = 112


def _extract(box):
    """Pull gold-on-near-black art out of Preview.png as an RGBA layer."""
    src = Image.open(PREVIEW).convert("RGB").crop(box)
    lum = src.convert("L")
    layer = Image.new("RGBA", src.size, (0, 0, 0, 0))
    layer.paste(Image.new("RGBA", src.size, GOLD + (255,)),
                (0, 0), lum.point(lambda v: 0 if v < 30 else min(255, int(v * 1.4))))
    return layer


FONT_DIR = ROOT / "brand" / "fonts"


def _font(size, role="display", weight=None):
    """Typefaces named by docs/BRAND_SYSTEM.md, vendored in brand/fonts/.

    The brand doc asks for a condensed Egyptian SLAB for display and IBM Plex Mono
    for the ledger/console body voice, and explicitly rules out Fraunces (wrong
    terminals) and Plex Sans (the slop tell). Earlier passes substituted whatever
    Windows had — Segoe, then Impact, then Cinzel — which is tell #16 from
    E:/Towers/Artist/ui/01-visual-craft-and-de-slop.md: a DEFAULT, not a choice.
    These are the real faces, downloaded and validated (getname() checked).

    Both display faces are VARIABLE (weight axis 100-900), so the tower's
    "extreme weight jumps (200 vs 800, not 400 vs 600)" is actually achievable.
    """
    files = {
        "slab":       "robotoslab.ttf",   # Egyptian slab — the wordmark register
        "condensed":  "oswald.ttf",       # condensed caps — lead lines
        "mono":       "plexmono.ttf",     # IBM Plex Mono — ledger/console voice
    }
    path = FONT_DIR / files[role]
    f = ImageFont.truetype(str(path), size)
    if weight is not None:
        try:
            f.set_variation_by_axes([weight])
        except Exception:
            pass
    return f


def _deco_rule(draw, x0, x1, y):
    """line — diamond — line, drawn at real size instead of shrunk to noise."""
    mid = (x0 + x1) // 2
    d = 5
    draw.line([(x0, y), (mid - d - 6, y)], fill=GOLD, width=2)
    draw.line([(mid + d + 6, y), (x1, y)], fill=GOLD, width=2)
    draw.polygon([(mid, y - d), (mid + d, y), (mid, y + d), (mid - d, y)], fill=GOLD)


def build(shot_path: Path, name: str):
    canvas = Image.new("RGB", (W, H), GROUND)

    # ── RIGHT: the real game, cropped onto the colonists, mound excluded ──────
    shot = Image.open(shot_path).convert("RGB")
    sw, sh = shot.size
    # Pawn cluster measured by cropping and looking: x 250-745, y 60-340 of 745x541.
    # v5 cropped so tight that Wehomo and Badevin were sliced by the column edge
    # and their name labels truncated. Pull back, and stop above the dirt mound.
    # v6 overcorrected: only 2 of the 5 named pawns survived and the right half
    # was barrels. The pitch is MANY viewers on MANY colonists, so keep the whole
    # cluster (Wehomo, Starthandshake, Badevin, Optimustimel, Bryo_Zoa) in frame
    # and accept a smaller pawn scale. Bottom stops above the dirt mound.
    # Left edge pulled in so Optimustimel is not sliced by the column rule;
    # bottom raised so the dirt mound stays out of frame entirely.
    crop = shot.crop((int(sw * 0.25), int(sh * 0.09), int(sw * 0.93), int(sh * 0.53)))

    img_w = W - COLUMN_W
    scale = max(img_w / crop.width, H / crop.height)
    shot_r = crop.resize((int(crop.width * scale), int(crop.height * scale)), Image.LANCZOS)
    left = (shot_r.width - img_w) // 2
    top = (shot_r.height - H) // 2
    canvas.paste(shot_r.crop((left, top, left + img_w, top + H)), (COLUMN_W, 0))

    # ── LEFT: flat command column. Solid, not a fade. ────────────────────────
    canvas.paste(Image.new("RGB", (COLUMN_W, H), GROUND), (0, 0))
    d = ImageDraw.Draw(canvas)
    # Hard gold edge — engraved, square, no blur.
    d.rectangle([COLUMN_W, 0, COLUMN_W + 2, H], fill=GOLD)

    inner_x0, inner_x1 = PAD, COLUMN_W - PAD

    # Seal, deliberately sized.
    seal = _extract(SEAL_BOX)
    seal_w = 78
    seal = seal.resize((seal_w, int(seal.height * seal_w / seal.width)), Image.LANCZOS)
    canvas.paste(seal, (inner_x0, TOP), seal)

    # Wordmark, filling the column width — the primary type moment.
    wm = _extract(WORDMARK_BOX)
    wm_w = inner_x1 - inner_x0
    wm = wm.resize((wm_w, int(wm.height * wm_w / wm.width)), Image.LANCZOS)
    wm_y = TOP + seal.height + 42
    canvas.paste(wm, (inner_x0, wm_y), wm)

    y = wm_y + wm.height + 34
    _deco_rule(d, inner_x0, inner_x1, y)

    # HIERARCHY — tower file 01: extreme jumps in SIZE, WEIGHT, FACE and
    # BRIGHTNESS together, never a smooth 1.25x scale. Weight 800 vs 300 is the
    # "200 vs 800, not 400 vs 600" rule, now actually reachable via the variable axis.
    y += 36
    f_lead = _font(34, "condensed", weight=700)
    for line in ("YOUR VIEWERS", "OR YOUR FRIENDS"):
        d.text((inner_x0, y), line, font=f_lead, fill=GOLD_HI)
        y += 40

    y += 10
    f_body = _font(17, "mono")
    for line in ("control your colonists,", "from a browser."):
        d.text((inner_x0, y), line, font=f_body, fill=GOLD)
        y += 25

    # Dimmest tier — the 5% in 70/25/5. Carries the fact the old art never did.
    y += 42
    f_small = _font(13, "mono")
    for line in ("TWITCH STREAM  /  LOCAL CO-OP", "NO TWITCH ACCOUNT REQUIRED"):
        d.text((inner_x0, y), line, font=f_small, fill=(132, 116, 86))
        y += 20

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    out = OUT_DIR / f"overlord-hero-{name}.png"
    canvas.save(out, optimize=True)
    print(f"saved {out} ({out.stat().st_size} bytes)")
    return out


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit("usage: build_workshop_hero.py <screenshot> [name]")
    build(Path(sys.argv[1]), sys.argv[2] if len(sys.argv) > 2 else "v5")
