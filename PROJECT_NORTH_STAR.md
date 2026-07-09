# Overlord — Project North Star

**One-line:** A RimWorld 1.5/1.6 mod that lets Twitch viewers control a streamer's colonists from a browser, via a self-hosted relay.

## Ship criterion
Overlord is **shipped and live** (Steam Workshop item `3760983440`, GitHub `scheissgeist/Overlord` `v0.1.0`). The bar for any change is now: **it must not regress a live, public mod that streamers run during broadcasts.** Correctness and frame-time (no stutter on the streamer's game) outrank new features.

## Current phase
**Post-launch hardening — performance.** The mod is feature-complete enough to be public; the active work is removing main-thread lag on the RimWorld host (the streamer reported stutter). Everything the mod does on the game's main thread (map JPEG capture, per-viewer state sync, GUI overlays) is under review for cost.

## What the product is (the axis)
- **Mod (this repo):** runs inside RimWorld; captures pawn/map state, renders per-viewer map frames, applies viewer commands. Must be a good citizen of the game's single main thread.
- **Relay:** Node server (self-hosted); one host connection per instance. Not in this repo's hot path.
- **Viewers:** browser clients; get state + map frames, send commands.

## Forbidden moves
- Do not add main-thread work that runs per-frame / per-tick / per-GUI-event without change-detection gating. This mod's whole lag problem is un-gated per-cycle work.
- Do not send stale or wrong state to viewers in the name of speed — a perf fix that breaks the change-detection contract (viewer stops getting updates when something changed) is a regression, not a fix.
- Do not move Camera.Render / ReadPixels / Texture2D.Apply off the main thread — Unity forbids it. Only CPU-side work (JPEG encode of an already-read pixel buffer, hashing) may move to a background thread.
- Do not commit secrets or personal relay URLs to this public repo. Public tree is scrubbed; private ops live on archive/pre-public-history and gitignored docs.
- Do not rewrite public git history or force-push master without explicit user request.

## Approved next moves (perf hardening, 2026-07-09)
Verified by a multi-agent audit (10 confirmed main-thread hotspots). In severity order, all correctness-safe:
1. ComputeStateSignature must not call the heavy GetNearbyEquipment — use a cheap bounded fingerprint (id + def shortHash for nearby weapon/apparel via ThingRequestGroup, no CanReserveAndReach/IsForbidden/sort). [critical]
2. GetNearbyEquipment itself: replace listerThings.AllThings full scan with ThingsInGroup(Weapon)+ThingsInGroup(Apparel). [high]
3. Move EncodeToJPG off the main thread onto the existing send worker (snapshot pixels on main thread, compress on the send thread). [high]
4. Sample relations.OpinionOf (~colonists^2) on a slower cadence inside the signature. [medium]
5. GetSessionForPawn: O(1) pawnToViewer reverse-map fast path before the linear scan (helps the always-on ColonistBar overlay). [low, safe]
6. Cache RimWorldCompat.GetLabel reflection per concrete Type. [medium]
7. Tactical-map path (allowViewerTacticalMap, default OFF): compute one shared per-map chunk-hash snapshot per tick instead of per-viewer rehashing. [high, but only when toggle on]

## Reference
- Perf audit workflow + verdicts: session log docs/SESSION_LOG_2026_07_09.md.
- Settings defaults that shape cost: mapUpdateInterval=0.10 (~10 fps/viewer capture), allowViewerTacticalMap=false, allowViewerResourceReadout=false (Source/Core/OverlordSettings.cs).
