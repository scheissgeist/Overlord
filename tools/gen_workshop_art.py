"""Nano-banana concept sheet for the Overlord Steam Workshop hero art.

Pipeline copied from E:/ZELDAFPS/scripts/concept/gen_*.py and E:/Wood/tools/gen_*.py —
Replicate `google/nano-banana`, key at E:/Towers/Artist/tools/api_keys/replicate.txt.

Output: brand/concepts/overlord-hero-<variant>.png

WHY: the current About/Preview.png reads as AI-generated, and the tells are all in
the ILLUSTRATED GAME CONTENT, not the type. Inspected at full resolution:
  - the colonist is a generic soft-shaded hoodie figure, not a RimWorld pawn
  - the three side panels carry fake UI — scribble placeholder text, mushy icons,
    and a "person silhouette" avatar (the single most common AI-UI tell)
  - the floor is generic painted top-down tiles, approximately-RimWorld but wrong
The wordmark itself is GOOD (heavy Egyptian slab, correct bracketed terminals).

So these variants deliberately do NOT try to render fake RimWorld gameplay. They
generate BACKGROUND / FRAME / MOOD plates only — the real game content is meant to
be composited in from an actual screenshot, and the existing gold wordmark from
Preview.png reused on top. Generating a fake colony is what broke the current art;
repeating it at higher quality would just be a better-looking lie.

Palette and type direction are LOCKED by docs/BRAND_SYSTEM.md and are not to be
improvised here: ground #090b0a, one gold #d2a95d, bright gold #f0cf82, wood
#3e2918/#8a5e30, parchment #e8d4a8. No purple, no gradients, no glow, no glass.
"""
import os, sys
from pathlib import Path
import requests, replicate

key_file = Path("E:/Towers/Artist/tools/api_keys/replicate.txt")
if not os.environ.get("REPLICATE_API_TOKEN") and key_file.exists():
    os.environ["REPLICATE_API_TOKEN"] = key_file.read_text().strip()

OUT_DIR = Path(__file__).resolve().parents[1] / "brand" / "concepts"

# Locked brand constraints — lifted verbatim in spirit from docs/BRAND_SYSTEM.md.
STYLE = (
    "STRICT COLOR: near-black background #090b0a; ONE gold accent #d2a95d with "
    "brighter gold highlights #f0cf82; aged wood #3e2918 and #8a5e30 and aged "
    "parchment #e8d4a8 as the ONLY textures, subtle and matte, never glossy. "
    "NO other colors, NO purple, NO blue, NO gradients, NO glow, NO lens flare, "
    "NO glassmorphism, NO drop shadows used as decoration. "
    "Square tight geometry, 0-2px corner radius, flat with a subtle engraved bevel. "
    "Engraved ledger / letterpress / old-world administrative document feel. "
    "16:9 landscape. NO text, NO lettering, NO words, NO UI mockups, NO logos — "
    "type is composited separately and any rendered text would be wrong."
)

VARIANTS = {
    "a-ledger-frame": (
        "An empty engraved ornamental FRAME for a game banner: a near-black field "
        "with a thin gold rule border, ornamental line-diamond-line deco rules "
        "running along the top and bottom edges, and generous empty space in the "
        "middle. The center must be EMPTY and dark — a screenshot will be placed "
        "there. Think an old ledger page or an engraved stock certificate border."
    ),
    "b-parchment-desk": (
        "An overseer's desk surface viewed from directly above: dark aged wood, a "
        "sheet of aged parchment lying on it with faint ruled lines and no writing, "
        "a brass rule, and low warm candlelight from the left. Deep shadow around "
        "the edges falling to near-black. The parchment area must be BLANK and "
        "unmarked. Matte, photographic but muted, no gloss."
    ),
    "c-reticle-plate": (
        "A dark near-black plate with a single gold TARGETING RETICLE made of four "
        "square corner brackets floating in the center-left, and three thin gold "
        "command lines routing in from the left edge toward it, each ending in a "
        "small gold diamond node. The area inside the reticle brackets must be "
        "EMPTY near-black. Precise, engraved, technical drawing feel."
    ),
    "d-vignette-ground": (
        "A pure atmospheric background plate: near-black ground with a single warm "
        "gold key light falling from the upper left, a subtle paper-grain texture, "
        "and heavy vignette to near-black at all four corners. No objects, no "
        "figures, no text — an empty stage for compositing. One decisive light "
        "source only, not layered gradients."
    ),
}

def gen(name, extra):
    out = OUT_DIR / f"overlord-hero-{name}.png"
    print(f"[{name}] generating...")
    r = replicate.run(
        "google/nano-banana",
        input={"prompt": f"{extra} {STYLE}", "output_format": "png"},
    )
    if isinstance(r, list):
        r = r[0]
    data = r.read() if hasattr(r, "read") else (requests.get(r).content if isinstance(r, str) else bytes(r))
    out.write_bytes(data)
    print(f"[{name}] saved {out} ({len(data)} bytes)")

if __name__ == "__main__":
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for k in (sys.argv[1:] or list(VARIANTS)):
        gen(k, VARIANTS[k])
