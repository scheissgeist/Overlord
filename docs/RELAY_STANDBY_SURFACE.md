# Relay standby surface

**Status:** Shipped to production 2026-08-26  
**Production build:** `20260826-relay-standby-v2`  
**Production URL:** <https://overlord-relay.fly.dev/>  
**Scope:** Viewer web UI shown when the relay is online but no RimWorld host is connected

## Outcome

The old host-absent lobby was a sparse text block at the top-left of a largely empty wood panel. It accurately reported that no game was connected, but it looked unfinished and did not explain the working parts of the system.

The replacement is a purpose-built live status surface. Its full composition is centered like a conventional website, with a strong Overlord headline and a compact connection route that distinguishes the viewer, relay, and RimWorld host states. The page still joins automatically when RimWorld comes online.

This is intentionally not a generic empty state. The relay and viewer are active, so the useful content is the connection topology and the one missing stage.

## Final experience

The surface communicates four facts in order:

1. The relay is standing by.
2. No RimWorld game is currently connected.
3. The viewer should keep the page open because entry is automatic.
4. Viewer and relay are online; only RimWorld is awaiting its host.

The visible copy is:

- `Relay standby`
- `No game connected`
- `Keep this page open. It will enter the colony automatically when RimWorld comes online.`
- `Auto-join armed`
- Connection nodes: `Viewer / Connected`, `Relay / Online`, `RimWorld / Awaiting host`

## State-routing contract

`showLobbyState()` writes an explicit state to `#screen-lobby[data-lobby-state]`. The specialized surface is selected only by `data-lobby-state="relay-empty"`.

| Runtime condition | UI state | Presentation |
|---|---|---|
| No live rooms exist | `relay-empty` | Dedicated centered relay standby surface |
| The selected room disappeared but other rooms are live | `room-missing` | Existing lobby messaging and room choices |
| Claim, death, reconnect, assignment, or live-game lobby state | Existing phase/state | Existing lobby presentation |

The distinction matters. A viewer whose specific room disappeared should be offered the other live rooms, not shown a false global outage. Future state additions must preserve that separation.

## Layout and responsive behavior

The ops header remains at the top. Everything below it is one centered composition with a maximum width of 720px.

### Above 480px

- The hero and connection route are centered horizontally.
- The grid uses `place-content: center`, centering the composition vertically in the remaining viewport.
- The route is horizontal: Viewer → Relay → RimWorld.
- The headline scales from 48px to 86px without introducing extra hierarchy tiers.
- At widths up to 700px, padding tightens, the route icon shrinks, and numeric node labels are removed.

### At 480px and below

- The page can scroll vertically instead of clipping short phone viewports.
- The composition begins near the top with controlled padding.
- The route becomes a centered vertical chain.
- The scanning link changes from horizontal to vertical motion.
- The minimum content height is 720px so the route is not crushed into the hero.

The implementation was visually checked at 390×844, 539×481, and 1280×720. All three had no unintended document overflow.

## Visual system

The surface stays inside the existing Overlord brand system:

- Dark repeated wood is the page material.
- Near-black is used for the connection route, not as another decorative card layer.
- Gold is the single decorative accent.
- Green and yellow are semantic connection colors only.
- The headline uses the heavy slab display face; supporting copy uses the existing UI face.
- Edges remain square. No gradients, glow, glass effects, large rounded cards, or decorative icon clutter were introduced.
- The star-in-circle favicon is reused once in the connection route header.

The lobby wordmark was also moved to the display face so the ops header and hero belong to the same product.

## Motion and accessibility

The only new motion is a small scan traveling from the online relay toward the waiting RimWorld host. It describes a real system behavior: the viewer is waiting for the final connection stage.

- Desktop and compact layouts use `standby-scan` horizontally.
- Phone layouts use `standby-scan-vertical`.
- `prefers-reduced-motion: reduce` disables the animation and leaves a static marker.
- The section is a polite live status region: `role="status"` and `aria-live="polite"`.
- Decorative marks, links, and the route icon are hidden from assistive technology where appropriate.
- The route has the accessible label `Connection route`.

## Implementation map

| File | Responsibility |
|---|---|
| `relay-server/public/app.js` | Defines build `20260826-relay-standby-v2`; writes the explicit lobby state; routes true global host absence to `relay-empty` and a missing selected room to `room-missing` |
| `relay-server/public/index.html` | Owns the semantic standby hero and three-node connection route |
| `relay-server/public/style.css` | Hides the legacy lobby content only for `relay-empty`; owns centered layout, route geometry, responsive changes, motion, and reduced-motion behavior |
| `docs/BRAND_SYSTEM.md` | Source of truth for the material, typography, palette, geometry, and voice |
| `docs/RELEASE_GATE.md` | Source of truth for local, installation, deployment, and production verification |

The same canonical frontend is deployed to Fly and copied into each local mod as `WebUI`; there is no separate embedded version of this screen to maintain.

## Design research consulted

The redesign was grounded in the existing Towers rather than an invented visual trend. The consulted sources were:

- `E:\Towers\Artist\ui\INDEX.md`
- `E:\Towers\Artist\ui\01-visual-craft-and-de-slop.md`
- `E:\Towers\Artist\ui\02-product-and-app-architecture.md`
- `E:\Towers\Artist\ui\04-motion-and-interaction.md`
- `E:\Towers\Artist\knowledge\ui_ux\design_principles_2026.md`
- `E:\Towers\Artist\knowledge\ui_ux\product_personality.md`
- `E:\Towers\Artist\knowledge\ui_ux\gaming_streaming_aesthetic.md`
- `E:\Towers\Marketing\knowledge_base\32_onboarding_first_five_minutes.md`
- `E:\Towers\Marketing\knowledge_base\36_game_marketing_playbook.md`

Those sources drove three decisions:

1. Treat connection status as the content instead of decorating an empty message.
2. Use scale, typography, material, and tight geometry for personality rather than card soup or icon density.
3. Use motion only to explain system state and provide a reduced-motion equivalent.

## Iteration history

1. A restrained centered empty-state pass improved alignment but did not materially upgrade the screen.
2. A broader Towers-informed pass introduced the large hero and explicit connection topology in a split composition.
3. The final user-directed pass centered the entire experience in one normal web composition and placed the connection route horizontally beneath the hero.

The first split version briefly deployed as `20260826-relay-standby-v1` while the centering instruction arrived during the release transaction. The transaction was allowed to finish safely, then the corrected layout was assigned the distinct `v2` marker and deployed again. The distinct markers prevented the live verifier from accepting stale assets.

## Release and verification record

The final release ran from `relay-server` with:

```powershell
$env:RELAY_URL = 'https://overlord-relay.fly.dev/'
npm run release:fly
```

The complete final gate passed:

- C# release build: 0 warnings, 0 errors.
- JavaScript syntax and whitespace gates passed.
- Fresh-viewer runtime smoke passed.
- Desktop, compact, and narrow viewer coverage passed.
- Both local RimWorld mod installations matched the built DLL and content hashes.
- Fly rolling deployment completed healthy.
- Production `/health.clientBuild`, served `app.js`, and local build all matched `20260826-relay-standby-v2`.
- Production returned `Cache-Control: no-store` for the viewer assets.
- Exact production asset verification passed.
- Direct production checks found the `.relay-standby` markup and final centered CSS.

Production hashes reported by the verifier:

| Asset | Short hash |
|---|---|
| `app.js` | `091ccd536bc0` |
| `style.css` | `b924469348ee` |
| `index.html` | `17899676608a` |

The initial `v1` deploy itself completed, but its post-deploy verifier was invoked without `RELAY_URL` in that shell and exited before checking production. This was a verifier configuration failure, not a Fly failure. The final `v2` release supplied the URL and passed the entire path end to end.

## Installation record

The established release path synchronized the final DLL and WebUI content to:

- `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Overlord`
- `C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\3760983440`

The reported installed DLL hash was `1D35744C37F1BE74`.

## Evidence

- Final target capture: `output/playwright/relay-empty-state-539x481.png`
- Session record: `docs/SESSION_LOG_2026_08_26.md`
- Live relay: <https://overlord-relay.fly.dev/>

The Playwright capture was taken from a local relay reproduction with no host, not from production player data. Browser inspection reported no console errors or warnings.

## Maintenance guardrails

- Do not display this surface when other rooms are available; keep `room-missing` distinct from `relay-empty`.
- Do not replace automatic entry with a manual refresh or join button unless connection behavior changes.
- Keep the route truthful. If relay or viewer connectivity semantics change, update the node state rather than leaving a decorative diagram.
- Do not hide the ops header; it retains viewer and relay operational context.
- Keep all relay-empty selectors scoped to `data-lobby-state="relay-empty"` so claim, death, reconnect, and live lobby states cannot regress.
- Preserve reduced-motion behavior when changing route animation.
- Preserve the maximum 720px centered composition unless a new content requirement exceeds it.
- Bump `UI_BUILD` for every deployed viewer-asset change, then use the complete release gate. Do not hot-patch static production files.
- Verify both local mod copies and production assets before declaring a viewer release complete.
