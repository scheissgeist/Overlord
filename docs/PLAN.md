# Overlord: Puppeteer Replacement for RimWorld 1.5/1.6

## Context

Puppeteer (by pardeike/BleuSquid) — the RimWorld mod that lets Twitch viewers control individual colonists via web browser — is abandoned at version 1.4. RimWorld 1.5/1.6 removed or restructured `PawnRenderer`, `ColonistBarColonistDrawer`, `FloatMenuOption`, and `GizmoGridDrawer`, breaking ~8 of Puppeteer's 55 Harmony patches. The hosted relay at puppeteer.rimworld.live is dead.

**Overlord** is a new standalone mod at `E:\Overlord` that replaces Puppeteer with a better, future-proof architecture.

## What Puppeteer Does (Full Feature Set)

- Viewer-to-pawn assignment (streamer assigns colonists to viewers)
- Browser control panel showing: skills, needs, health, mood, schedule, work priorities, apparel, equipment, social relations
- Direct pawn commands: draft, move, attack, change work/schedule/outfit/drug/food/area, equip/drop, customize appearance
- Live map view in browser (JPEG stream, clickable, mobile support)
- Respawn system with resurrection portal + ticket system
- Off-limits areas (streamer restricts where viewers can send pawns)
- Colonist bar overlay (connection status icons)
- Twitch chat integration
- New colonist creation ([+] button)
- Remote action log
- Settings panel with per-colonist control toggles

## How Overlord Is Better

1. **Self-hosted relay** on Railway (no dead external service dependency)
2. **Small Harmony patch surface** instead of 55 — uses GameComponent + public APIs where possible, with narrow lifecycle/render hooks where needed
3. **No external DLL dependencies** — manual JSON, raw WebSocket
4. **Mobile-first browser UI**
5. **Granular permission system** (per-colonist, per-action-type flags)
6. **Delta state sync** (only send changed fields, not full snapshots)
7. **Binary WebSocket frames** for map images (no base64 overhead)
8. **OBS overlay support** (transparent page for stream overlays)
9. **Multi-platform auth ready** (Twitch first, YouTube/Discord later)
10. **Public event API** for other mods to hook into

## Architecture

### Core Insight: GameComponent Replaces Most Patches

RimWorld's `GameComponent` gives us `GameComponentTick()`, `GameComponentUpdate()`, `GameComponentOnGUI()`, and `ExposeData()` for free. This eliminates the need to patch `Game.UpdatePlay` and handles save/load natively.

### Only 4 Harmony Patches Needed

| Patch | Target | Purpose | Risk |
|-------|--------|---------|------|
| 1 | `Game.InitNewGame` (postfix) | Initialize Overlord | Very low |
| 2 | `Game.LoadGame` (postfix) | Initialize on load | Very low |
| 3 | `Game.DeinitAndRemoveMap` (prefix) | Cleanup | Very low |
| 4 | `Pawn.Kill` (postfix) | Death tracking for respawn | Low |

The colonist bar overlay uses `GameComponentOnGUI()` + `Find.ColonistBar` public API — no patch needed.

### Pawn Control via Public APIs (No Patches)

All pawn control uses direct public API calls — verified via reflection against the 1.6 DLL:

- Draft: `pawn.drafter.Drafted = true/false`
- Jobs: `pawn.jobs.TryTakeOrderedJob(job)`
- Work: `pawn.workSettings.SetPriority(workType, priority)`
- Schedule: `pawn.timetable.SetAssignment(hour, assignment)`
- Outfit: `pawn.outfits.CurrentApparelPolicy = policy`
- Drugs: `pawn.drugs.CurrentPolicy = policy`
- Food: `pawn.foodRestriction.CurrentFoodPolicy = policy`
- Area: `pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area`
- Apparel: `pawn.apparel.Wear()` / `pawn.apparel.TryDrop()`
- Portraits: `PortraitsCache.Get(pawn, size, rotation)`

### State Sync Protocol

**Game -> Browser**: `pawn_state` (delta), `pawn_portrait`, `map_frame`, `action_result`, `permissions`, `colonist_list`
**Browser -> Game**: `command` (action + params), `map_click`, `request_state`

Delta updates: `PawnStateSerializer` hashes last-sent state per viewer, only sends changed fields. Map frames skip when nothing moved.

### Map Rendering

Camera-clone approach (proven in FTB). Improvements:
- Configurable resolution (256-800px) and FPS (2-10)
- Background compression thread
- Shared rendering for nearby pawns
- Skip frames when pawn area unchanged
- Binary WebSocket frames (no base64 overhead)

## Project Structure

```
E:\Overlord\
  About\About.xml
  Assemblies\                          (build output)
  Defs\
    ThingDefs\Overlord_Buildings.xml   (resurrection portal)
    HediffDefs\Overlord_Hediffs.xml    (respawn cooldown)
  Source\
    Core\
      OverlordMod.cs                   (Mod entry, Harmony init)
      OverlordSettings.cs              (settings: port, auth, permissions)
      OverlordGameComponent.cs         (GameComponent lifecycle)
      JsonHelper.cs                    (manual JSON parser/serializer)
      LogUtil.cs                       (tagged logging)
    Networking\
      RelayClient.cs                   (WebSocket to Railway relay)
      EmbeddedWebServer.cs             (local HTTP+WS fallback)
      ThreadSafeQueue.cs               (generic thread-safe queue)
      StateProtocol.cs                 (message type constants)
    Viewer\
      ViewerManager.cs                 (viewer registry, assignment)
      ViewerSession.cs                 (per-viewer state + pawn ref)
      ViewerPermissions.cs             (per-colonist permission flags)
      ViewerActionLog.cs               (action history, saveable)
    PawnControl\
      PawnCommandRouter.cs             (command dispatch + permission check)
      PawnStateSerializer.cs           (pawn -> JSON with delta)
      PawnPolicyController.cs          (schedule/outfit/drug/food/area)
      PawnAppearanceController.cs      (hair, body type)
      PawnSocialSerializer.cs          (relationships)
    MapView\
      MapRenderer.cs                   (camera clone -> JPEG pipeline)
      PortraitRenderer.cs              (PortraitsCache wrapper)
      MapOverlayPainter.cs             (pawn/enemy/area indicators)
    UI\
      ColonistBarOverlay.cs            (draw badges on colonist bar)
      OverlordSettingsWindow.cs        (settings UI)
      StreamerOverlay.cs               (OBS overlay rendering)
    Patches\
      HarmonyPatches.cs                (all 4 patches)
    Respawn\
      ResurrectionPortal.cs            (portal building ThingComp)
      RespawnManager.cs                (tickets, cooldowns, death cause)
    Events\
      OverlordEventBus.cs             (pub/sub for mod interop)
      OverlordAPI.cs                   (static public API)
  Overlord.csproj
  build.bat
  relay-server\
    server.js                          (Railway Node.js relay)
    package.json
    railway.toml / nixpacks.toml
    public\
      index.html                       (mobile-first SPA)
      app.js                           (control panel logic)
      style.css
      overlay.html                     (OBS transparent overlay)
```

## Phased Delivery

### Phase 1: Core Infrastructure
Create project, build pipeline, GameComponent, relay client, core Harmony patch surface, JsonHelper.
**Milestone**: Mod loads in RimWorld 1.6, connects to relay, logs "Overlord initialized".
**Files**: About.xml, Overlord.csproj, build.bat, OverlordMod.cs, OverlordSettings.cs, OverlordGameComponent.cs, JsonHelper.cs, LogUtil.cs, RelayClient.cs, ThreadSafeQueue.cs, StateProtocol.cs, HarmonyPatches.cs

### Phase 2: Viewer Assignment + Pawn Control
Viewer manager, session tracking, command router, state serializer, policy controller.
**Milestone**: Streamer assigns colonist. Viewer can draft, move, attack, change work/schedule/policy via relay.
**Files**: ViewerManager.cs, ViewerSession.cs, ViewerPermissions.cs, PawnCommandRouter.cs, PawnStateSerializer.cs, PawnPolicyController.cs

### Phase 3: Browser UI + Map Rendering
Map renderer, portrait renderer, embedded web server, relay server, browser SPA.
**Milestone**: Viewer sees live map + all pawn data in browser, can issue all commands. Mobile-responsive.
**Files**: MapRenderer.cs, PortraitRenderer.cs, MapOverlayPainter.cs, EmbeddedWebServer.cs, relay-server/*, ColonistBarOverlay.cs

### Phase 4: Advanced Features
Respawn portal, off-limits areas, action logging, appearance customization, settings UI, social data.
**Milestone**: Full Puppeteer parity plus permission system and action logging.
**Files**: ResurrectionPortal.cs, RespawnManager.cs, Defs/*, ViewerActionLog.cs, PawnAppearanceController.cs, PawnSocialSerializer.cs, OverlordSettingsWindow.cs

### Phase 5: Extras
OBS overlay, multi-platform auth, event bus, public API.
**Milestone**: Stream-ready with overlay, YouTube/Discord auth, mod interop API.
**Files**: StreamerOverlay.cs, Auth/*, OverlordEventBus.cs, OverlordAPI.cs, overlay.html

## Patterns to Reuse from FTB

- **RelayClient** pattern: `e:/Fuck The Base/Source/Server/RelayClient.cs` — thread-safe queue, background WebSocket, auto-reconnect
- **MapRenderer** camera clone: `e:/Fuck The Base/Source/Server/MapRenderer.cs` — Texture2D -> JPEG -> base64 pipeline
- **EmbeddedWebServer** HttpListener: `e:/Fuck The Base/Source/Server/EmbeddedWebServer.cs` — HTTP+WS dual server
- **relay-server**: `e:/Fuck The Base/relay-server/server.js` — Express+ws with Twitch OAuth
- **Harmony patch patterns**: `e:/Fuck The Base/Source/Components/HarmonyPatches.cs` — prefix/postfix patterns
- **csproj reference paths**: `e:/Fuck The Base/FuckTheBase.csproj` — RimWorld DLL paths

## 1.5/1.6 Compatibility Notes

- `CurrentApparelPolicy` (1.6) was `CurrentOutfit` (1.4) — use reflection to try both
- `CurrentFoodPolicy` (1.6) was `CurrentFoodRestriction` (1.4) — same approach
- `PortraitsCache.Get()` replaces all `PawnRenderer.RenderPawnInternal` usage
- `FloatMenuOptionProvider` is the new pattern but we don't need it (no float menu patches)
- `ColonistBarColonistDrawer` is gone but we don't patch it (draw overlay via GameComponentOnGUI)

## Verification

1. Build with `msbuild Overlord.csproj /p:Configuration=Release`
2. Copy `Assemblies/Overlord.dll` to RimWorld Mods folder
3. Enable mod in RimWorld, start new game
4. Check RimWorld log for "Overlord initialized" with no red errors
5. Deploy relay-server to Railway, verify `/health` endpoint
6. Open browser to relay URL, authenticate via Twitch
7. Assign colonist in-game, verify browser shows pawn data
8. Issue commands from browser, verify pawn responds in-game
9. Kill viewer pawn near portal, verify respawn with ticket deduction
10. Test on mobile browser for responsive layout
