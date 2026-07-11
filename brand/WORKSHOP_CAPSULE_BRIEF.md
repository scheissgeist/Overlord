# Workshop capsule — art direction (replace the diagram-style Preview)

## Why replace the current one
The current Preview.png reads as a *flat explainer diagram* (UI windows + connector
lines + reticle over a game slice). It's clean and on-palette, but it explains the
mechanic instead of selling the fantasy — which is the shape that reads as
"tool-made." Steam capsules that feel human-made lead with the GAME as an
atmospheric SCENE, brand layered lightly on top.

## The concept (one line)
A lone RimWorld colonist, mid-colony, caught in a gold targeting reticle — the moment
a viewer takes control. The audience is watching THIS colonist.

## Composition (1280x720, Steam capsule)
- GROUND: a REAL RimWorld colony screenshot at night or golden-hour lamplight —
  built rooms, a few colonists, crops/workbenches visible, warm interior glow.
  Muted saturation, near-black edges vignetting inward. NOT a top-down clean grid;
  pick a frame with depth and mess (the colony feels lived-in).
- FOCUS: one colonist framed by the gold reticle (four corner brackets, #d2a95d),
  slightly off-center (rule of thirds, NOT dead-center). Thin gold command-lines
  route in from ONE edge toward the reticle — 2-3 lines max, not a bus of them.
- WORDMARK: OVERLORD in the heavy Egyptian slab (Roboto Slab 900 / Clarendon),
  gold #d2a95d, upper area, generous. The star-in-circle seal small above or beside it.
- TAGLINE: "Twitch viewers control your colonists." — verbatim, tracked slab-caps,
  muted. No second marketing subtitle.
- NEGATIVE SPACE: let the colony breathe. The diagram version is busy; this one is
  quiet with ONE focal moment.

## Palette lock (BRAND_SYSTEM.md)
Near-black ground, ONE gold accent (#d2a95d / bright #f0cf82), wood+parchment as
material only. NO purple, NO gradient hero, NO glow, NO second accent. Semantic
colors only if a real game HUD element carries them.

## What to AVOID (the slop tells this dodges)
- The flat vector-diagram look (windows + connector bus) — that's the current one.
- Purple/blue gradient, teal-orange grade, generic sans, gradient-fill text.
- Dead-center symmetry. AI hero glow. Fake UI chrome that isn't the real game.
- A busy collage — ONE focal colonist, not three floating panels.

## Two ways to produce it
1. COMPOSITE (real, no image model): capture a real atmospheric colony frame in-game
   (dev mode, good lighting, F11/Steam screenshot), hand it to me; I composite the
   reticle + command-lines + wordmark + seal per this spec. Real scene, real brand.
2. GENERATE (Grok/image model): use the paste-ready brief in BRAND_SYSTEM.md's
   grokBrief, PLUS this scene composition, to generate atmospheric key art; then I
   composite the exact wordmark/seal/tagline on top so type stays on-system (image
   models mangle text — never let the model render the wordmark).
