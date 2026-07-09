# Overlord Execution Plan

## Goal

Ship a Puppeteer-style RimWorld mod that survives RimWorld version drift by isolating unstable game APIs behind a narrow compatibility boundary while keeping the browser and relay protocol stable.

## Architecture Rules

### 1. Protocol is the product surface

The browser and relay layer should depend on stable message types and payload shapes, not RimWorld implementation details.

Current stable messages:
- `colonist_list`
- `pawn_state`
- `map_frame`
- `map_full`
- `map_delta`
- `action_result`
- `pawn_died`
- `ticket_update`
- `vote_update`
- `game_info`
- `chat`

### 2. RimWorld API access goes through `RimWorldCompat`

All version-sensitive game API calls should be routed through [RimWorldCompat.cs](</e:/Overlord/Source/Compat/RimWorldCompat.cs>).

Current responsibilities:
- policy getters/setters
- area restriction access
- schedule/work dispatch boundary
- portrait API lookup
- colonist bar access
- context menu option lookup
- capability reporting

Rule:
- New feature code should not call unstable RimWorld APIs directly if the call can reasonably drift between versions.
- If direct usage is necessary, move it into `RimWorldCompat` first.

### 3. Tiered degradation

If a future RimWorld update breaks a feature, the mod should degrade by capability tier instead of failing wholesale.

Tier 1:
- connect
- assign
- request state
- draft
- move
- attack

Tier 2:
- work priorities
- schedule
- policies
- area restriction
- context actions
- inventory actions

Tier 3:
- portraits
- map rendering
- colonist bar overlay
- OBS/admin extras
- votes
- events
- respawn polish

## Current Status

Implemented in the current pass:
- created a compatibility facade in [RimWorldCompat.cs](</e:/Overlord/Source/Compat/RimWorldCompat.cs>)
- routed policy/state/portrait/overlay/context-menu code through the compat layer
- fixed relay UI protocol drift so the browser accepts `action_result`
- fixed the browser `request_state` keyboard shortcut
- fixed lobby ticket rendering to target the actual DOM node
- added admin-only relay broadcasts for pawn deaths so OBS/admin views can react without breaking viewers
- added explicit `host_capabilities` messaging so viewer/admin clients can adapt to the current RimWorld build
- replaced the embedded server's hard-coded fallback UI with static serving of the canonical web app
- updated the install script to copy `relay-server/public` into the mod as `WebUI`
- logged capability status during mod initialization
- verified the streamer-side main tab is now a three-pane runtime operations console
- added explicit viewer connection state tracking to sessions and disconnect handling
- rebuilt the viewer browser flow around `waiting`, `assigned`, and `dead/unassigned` phases
- converted the viewer detail surfaces into a collapsible drawer so the map stays primary
- fixed a browser client syntax bug caused by duplicate `let reconnectAttempts`

Reference:
- full implementation record is in [IMPLEMENTATION_STATUS.md](</e:/Overlord/docs/IMPLEMENTATION_STATUS.md>)

## Workstreams

### Workstream A: Host Compatibility

Files:
- [RimWorldCompat.cs](</e:/Overlord/Source/Compat/RimWorldCompat.cs>)
- [PawnPolicyController.cs](</e:/Overlord/Source/PawnControl/PawnPolicyController.cs>)
- [PawnStateSerializer.cs](</e:/Overlord/Source/PawnControl/PawnStateSerializer.cs>)
- [PawnCommandRouter.cs](</e:/Overlord/Source/PawnControl/PawnCommandRouter.cs>)
- [ColonistBarOverlay.cs](</e:/Overlord/Source/UI/ColonistBarOverlay.cs>)
- [PortraitRenderer.cs](</e:/Overlord/Source/MapView/PortraitRenderer.cs>)

Next tasks:
- move any remaining unstable direct RimWorld calls into compat
- expand capability handling beyond context-menu gating and admin logging
- add compatibility smoke checks at startup for critical APIs

### Workstream B: Protocol Hardening

Files:
- [StateProtocol.cs](</e:/Overlord/Source/Networking/StateProtocol.cs>)
- [OverlordGameComponent.cs](</e:/Overlord/Source/Core/OverlordGameComponent.cs>)
- [ViewerManager.cs](</e:/Overlord/Source/Viewer/ViewerManager.cs>)
- [server.js](</e:/Overlord/relay-server/server.js>)
- [app.js](</e:/Overlord/relay-server/public/app.js>)

Next tasks:
- add protocol versioning in addition to capability messaging
- define viewer-only/admin-only/broadcast routing semantics cleanly
- remove old message aliases once both sides are aligned

### Workstream C: Single Frontend

Problem:
- embedded mode needed to stop relying on a separate hand-coded fallback UI

Goal:
- one canonical web app for both relay and embedded hosting

Current status:
- `EmbeddedWebServer` now serves the canonical frontend from `WebUI/`, with a repo fallback to `relay-server/public/`
- `build.bat` now copies the canonical frontend into the installed mod folder

Next tasks:
- verify the canonical embedded frontend live inside RimWorld
- decide whether embedded mode should expose viewer-only pages only or also local admin/OBS surfaces
- remove any remaining assumptions in the browser app that are relay-only

### Workstream D: Verification

Required checks after each major change:
- `dotnet build Overlord.csproj -c Release`
- manual protocol grep after renames/removals
- RimWorld smoke check: load save, connect viewer, assign pawn, request state, move, draft, kill pawn, verify admin/OBS signal

Future verification work:
- build a save-driven regression checklist
- capture protocol snapshots for reconnect, assignment, death, respawn, and vote flows

## Immediate Backlog

1. Run a live smoke test in RimWorld and the browser for login, claim, assignment, state sync, move, draft, death, respawn, and reconnect.
2. Remove the remaining unreachable legacy branches in `relay-server/public/app.js`.
3. Expand capability-driven UI disabling and error messaging beyond the current context-menu and warning paths.
4. Decide whether portrait delivery belongs inside `pawn_state` or should stay as a separate cached message path.
5. Add a save-driven regression checklist for assignment, reconnect, death, respawn, and vote flows.
