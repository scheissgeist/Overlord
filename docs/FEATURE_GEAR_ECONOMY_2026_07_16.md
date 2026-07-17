# Feature scoping — Viewer inventory / buy-routing / gear rework (2026-07-16)

Streamer (viewer) requests, verbatim intent:
1. Viewers can see the **whole colony's inventory**.
2. When a viewer **buys** something, it goes into **their pawn's** inventory — *unless* it's a resource (steel), which should go to the colony. Differentiate by item type.
3. Viewers can see their clothing/armor/weapons and **equip** them.
4. Prefer **equipping what's already lying around** the colony *before* buying it.
5. New idea (mid-session): a separate **"Buy & Equip"** button alongside plain Buy.
6. LIVE BUG: gear display is **squishing text** for viewers.

## Sean's locked design decisions (AskUserQuestion, 2026-07-16)
- **Routing:** buyer picks each time — but the cleaner realization is **two buttons**: plain **Buy** (→ colony drop pod, current behavior) and **Buy & Equip** (→ buyer's pawn). "Buy & Equip" only shows for equippable items (self-differentiates; steel has no equip button → colony).
- **Equip fallback:** equip if the pawn can right now, **else route to that pawn's backpack** (inventory), never drop at feet, never to colony pile.
- **Loose gear reach:** surface **all colony-available gear** (stockpiles + loose) so viewers grab existing gear before buying.
- **Build order:** **buy-routing first**, then unified buy/equip surface, then colony-inventory view + item icons. Full-build-and-verify each slice, no half-building all three.

## What ALREADY EXISTS (4-agent code map, 2026-07-16)
- **Per-pawn Gear tab** (`#tab-gear`): equipped weapon + worn apparel + carried inventory, all serialized (`PawnStateSerializer.cs:411-446, 574-592`) and rendered (`app.js renderGear/renderInventory`). Drop/take-off, dye, use/drop all work.
- **Equip-what's-lying-around:** DONE. Gear tab has a **Nearby / Armory** source switch. Armory = colony-storage weapons/apparel (`ArmoryCatalog.cs`, `request_armory`→`armory_state`, paginated, permission-gated, 12s claim). Nearby = loose gear ≤24 tiles. Equip issues correct `JobDefOf.Wear` (apparel) / `JobDefOf.Equip` (weapon) — `PawnCommandRouter.ExecuteEquip:726-772`.
- **Item categorization** (weapon/apparel/resource/food/medicine): computed (`TwitchToolkitBridge.CategoryForThing:1531-1565`, flags `isWeapon/isApparel/madeFromStuff`) — but used ONLY for store sorting, never for delivery routing.
- **Base64 image plumbing:** exists for pawn portraits (`PortraitRenderer` RT→PNG→base64, throttled queue/cache in `OverlordGameComponent`), map frames, appearance preview. NOT wired to `ThingDef.uiIcon`; no item icons anywhere.
- **Server-side diff gate** (north-star-critical): `pawn_state` gated by `ComputeStateSignature` (`ViewerManager.cs:749-765`); `resource_readout` gated by hash + 6-cycle throttle (`1092-1119`). Client per-panel gate `panelChanged(key, slice)` (`app.js:2038`).

## The THREE real gaps
1. **Buy never routes to pawn.** `ExecuteItemPurchaseWithStuff` always `TradeUtility.SpawnDropPod` at colony (`TwitchToolkitBridge.cs:667-675`); `targetPawn` passed but ignored. Plain-item path defers to Toolkit `ResolvePurchase` (`:150-176`) → also colony. Insertion point for "to pawn vs colony": the branch at `:667-690` (targetPawn already in scope) + classification at `:114-153`.
2. **No colony-wide inventory view.** Only per-pawn gear + aggregate resource *counts* (`ResourceReadoutSerializer`, max 80 defs). No browsable colony item list. Building block: generalize `ArmoryCatalog.EnumerateStoredEquipment` beyond Weapon/Apparel groups. Extension pattern: mirror resource-readout (protocol const + serializer w/ out-hash + `ViewerManager` throttled dispatcher) OR armory request/response for pagination. MUST use the hash gate (north star forbidden move #1).
3. **No item icons.** Wire `ThingDef.uiIcon` → base64 like portraits, cached by def+stuff, throttled/off-main-thread (north star: no per-frame main-thread work).

## LIVE BUG (fixing FIRST — it's hurting viewers now): gear text squish
- **Reproduced** via `relay-server/scripts/capture-gear-squish.js` (new; client-only measurement infra, no main-thread cost). Long real item names truncate at ALL widths incl. desktop 1280.
- **Root cause:** `.gear-layout` grid `210px minmax(0,1fr)` (`style.css:2079-2084`) — fixed-width left column + fixed-width "Carried" right column both ellipsis-truncate names (`.gear-name/.gear-slot-name` nowrap+ellipsis `2169-2180`, `.gear-row` 3-col grid `2329-2338`) while the CENTER column wastes horizontal space. Screenshots: `output/playwright/gear-squish-{phone-360,phone-390,desktop-1280}.png`.
- **Fix direction:** let equipped/carried names use available width + wrap instead of truncating at a fixed narrow column; collapse the fixed side columns responsively. Verify by re-running the capture (truncatedCount → 0, no overflow).

## North-star constraints that bind this feature (read PROJECT_NORTH_STAR.md)
- Phase = post-launch PERF hardening. Forbidden move #1 = un-gated per-frame/per-tick main-thread work.
- Colony-inventory serializer MUST go through change-detection hash gate.
- Item-icon generation MUST be cached + throttled/off-main-thread (follow the portrait queue pattern).
- Buy-routing is event-driven (per-command), not per-tick → inherently safe.
- CSS squish fix is pure client-side → zero host cost.
