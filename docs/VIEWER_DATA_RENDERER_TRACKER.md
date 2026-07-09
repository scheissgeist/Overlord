# Viewer Data Renderer Tracker

**Project:** Overlord  
**Owner:** Codex integration lane  
**Status:** Slice 20 changed map chunks implemented; visual identity pending  
**Last updated:** 2026-05-02

## North Star

Viewers should feel like they are playing RimWorld themselves, with their pawn selected.

The browser should not depend on where the streamer camera is looking. RimWorld remains authoritative, the relay caches and routes state, and the browser renders a live tactical RimWorld-like view from real game data.

## Target Architecture

| Layer | Responsibility |
| --- | --- |
| RimWorld host | Authoritative simulation, command validation, state snapshot and delta publisher |
| Relay | Room state cache, sequence tracker, reconnect repair, delta fanout, slow-client protection |
| Browser | Local camera, renderer, selection, input, interpolation, command intent UI |

Control traffic is sacred. Visual traffic is expendable. Commands, acks, assignments, moderation, and repair messages must never wait behind map visuals.

## Current Source-Backed Seed

| Area | Existing support | Files |
| --- | --- | --- |
| Full map baseline | `map_full` sends terrain and roof arrays as base64 byte grids | `Source/MapView/TileMapSerializer.cs` |
| Entity state seed | `map_delta` still sends compatible pawn/building/item arrays, and standalone `entity_keyframe`/`entity_delta` carries the keyed scene table | `Source/MapView/TileMapSerializer.cs`, `Source/Viewer/ViewerManager.cs` |
| Browser tile renderer | WASD, drag pan, scroll zoom, click/right-click cell mapping already exist | `relay-server/public/tilemap.js` |
| Independent camera state | Viewer session stores camera zoom and optional center | `Source/Viewer/ViewerSession.cs`, `Source/PawnControl/PawnCommandRouter.cs` |
| Context actions | Browser can request context menu for a cell and host validates options | `Source/PawnControl/PawnCommandRouter.cs`, `relay-server/public/app.js` |
| JPEG fallback | Binary `map_frame` path still exists and is useful as fallback during migration | `Source/MapView/MapRenderer.cs`, `Source/Networking/BinaryFrameProtocol.cs` |

## Non-Goals

- Do not remove JPEG streaming in the first implementation slice.
- Do not trust browser-side game state for command authority.
- Do not invent RimWorld actions in the browser.
- Do not expose unseen/fogged/forbidden information unless the host explicitly allows it.
- Do not build a full art asset pipeline before the data renderer is playable.

## Phase Plan

### Phase 0: Protocol Design Lock

**Goal:** Define the live-data protocol before code changes.

Tasks:
- Specify `map_full_v2`, `map_delta_v2`, `map_chunk`, `entity_delta`, `entity_remove`, `state_keyframe`, and `state_resync_request`.
- Add monotonically increasing sequence numbers to map/entity state messages.
- Define client repair behavior when sequence gaps are detected.
- Define relay cache shape for latest baseline, latest entity table, dirty chunks, and recent delta log.
- Define capability gates so old hosts continue using JPEG mode.

Done:
- `docs/VIEWER_DATA_RENDERER_PROTOCOL.md` exists.
- Opus review result is integrated or explicitly rejected with reasoning.
- No code implementation starts until message names and ownership are written down.

### Phase 1: Independent Data Camera

**Goal:** Make the existing tilemap the primary camera surface when `map_full` exists.

Tasks:
- [ ] Add a viewer UI toggle: `Data map` / `Live image fallback`.
- [x] Promote tilemap canvas to the main playfield when map data is available.
- [x] Keep WASD, drag pan, wheel zoom, recenter, and follow-pawn controls in the primary UI.
- [x] Route left-click and right-click through the same server-validated command/context paths.
- [x] Keep current live JPEG as fallback; `?renderer=jpeg` forces it for debugging.
- [x] Add focused smoke coverage for tilemap activation, local pan, move, and context-menu cells.

Done:
- Browser camera movement is instant and local.
- Pawn commands still validate in RimWorld.
- `npm run smoke:tilemap` covers activation, paint, WASD/drag without `camera_zoom`, left-click move, and right-click context position.

### Phase 1b: Legacy Baseline Repair

**Goal:** Make the current legacy `map_full`/`map_delta` path recover after reconnect or manual state refresh.

Tasks:
- [x] Add protocol constants for legacy `map_full` and `map_delta`.
- [x] Send full map plus immediate delta from one shared host helper.
- [x] Use that helper on assignment and `request_state`.
- [x] Reset the browser map surface when reconnect/resync starts.
- [x] Extend smoke coverage for host reconnect, fresh state request, tilemap clearing, and tilemap reactivation.

Done:
- A viewer who reconnects or receives `host_connected` requests state and can receive a fresh map baseline.
- The browser does not keep drawing a stale tilemap while waiting for the new baseline.
- `npm run smoke:tilemap` covers reset and replay.

### Phase 1c: Legacy Sequence and Gap Repair

**Goal:** Make the current legacy `map_full`/`map_delta` path detect missed or out-of-order map state before relay cache exists.

Tasks:
- [x] Add `state_resync_request` to the host/relay protocol.
- [x] Tag legacy tactical map messages with `protocol`, `mapEpoch`, `seq`, `baseSeq`, `snapshot`, `tick`, and `mapId`.
- [x] Reset per-viewer tactical map stream state on assignment changes, unassign, and pawn death.
- [x] Have the browser reject sequenced deltas that do not match its current baseline.
- [x] Have the browser request `state_resync_request` and compatibility `request_state` when it detects a sequence gap.
- [x] Extend smoke coverage with an artificial sequence gap and repaired baseline.

Done:
- A skipped sequenced delta no longer mutates the browser map state silently.
- The current host can repair the browser through the existing `request_state` full-map snapshot path.
- `npm run smoke:tilemap` covers sequence-gap detection and recovery.

### Phase 1d: Legacy Relay Cache Replay

**Goal:** Let the relay repair a missed legacy tactical-map delta without forcing the host to resend the full baseline every time.

Tasks:
- [x] Add a per-viewer in-process replay cache for targeted host messages.
- [x] Cache latest targeted `host_capabilities`, `permissions`, `pawn_state`, `toolkit_state`, `colonist_list`, `map_full`, `map_delta`, and `game_info`.
- [x] Clear room replay cache when a host connects/reconnects.
- [x] Clear a viewer cache on kick/ban/death messages.
- [x] Answer `state_resync_request` from relay cache when possible instead of forwarding it to the host.
- [x] Validate cached `map_delta` sequence against the cached baseline/current delta before storing, so a corrupt gap delta cannot poison replay.
- [x] Expose replay cache size on `/health`, `/admin/status`, and `admin_sync`.
- [x] Expose replay cache diagnostics on `/admin/cache` and `/admin/replay-cache` without dumping full payloads.
- [x] Replay cached state immediately when the same viewer reconnects while their per-viewer cache is still valid.
- [x] Clear a viewer replay cache when an admin mints a replacement viewer session for that login.
- [x] Clear a viewer replay cache when its last session expires.
- [x] Clear cached `map_full` and `map_delta` when `host_capabilities.tacticalMap === false`.
- [x] Annotate replayed cached messages with `relayCached: true` and `relayCachedAt`.
- [x] Send `relay_capabilities` on viewer connect and skip compatibility `request_state` after map gaps when `cacheResync` is supported.
- [x] Extend smoke coverage so a bad delta triggers browser repair, relay replays cached `map_full`/`map_delta`, and the fake host does not receive `state_resync_request`.
- [x] Add `npm run smoke:relay-cache` for focused cache diagnostics, resync replay, replay annotation, reconnect replay, admin-session cache eviction, and tactical-map-disabled cache eviction coverage.

Done:
- Relay can replay the latest valid targeted map baseline/delta for a viewer.
- A gap-causing delta is not cached unless its `baseSeq` matches the cached stream.
- `npm run smoke:tilemap` covers relay-cache repair and bad-delta cache rejection.
- `npm run smoke:tilemap` covers `relay_capabilities.cacheResync` skipping the old compatibility `request_state` after a cache-repaired map gap.
- `npm run smoke:relay-cache` covers cache diagnostics, manual resync replay, replay annotation, automatic reconnect replay, admin-session cache eviction, and tactical-map-disabled cache eviction.

### Phase 2: Structured Entity Delta

**Goal:** Replace the current coarse delta with a useful game scene graph.

Tasks:
- [x] Preserve legacy building array compatibility while appending building IDs, def names, labels, and rotation.
- [x] Add ground item arrays with position, category, stack count, stable thing ID, def name, label, and forbidden flag.
- [x] Render ground item symbols in the browser tilemap.
- [x] Add smoke coverage for item metadata and richer building metadata.
- [x] Add keyed `entities` state using RimWorld `thingIDNumber`.
- [x] Add `removedEntities` despawn/delete semantics.
- [x] Have the browser maintain a keyed entity table and derive render lists from it.
- [x] Emit standalone `entity_keyframe` and `entity_delta` messages while preserving legacy `map_delta` fallback.
- [x] Cache and replay standalone entity messages in the relay.
- [x] Have the browser consume standalone entity messages.
- [x] Reduce standalone `entity_delta` to changed/new entities plus removals while preserving full legacy `map_delta`.
- [x] Add fire, plant, and construction/blueprint entities to the keyed scene table.
- [x] Expand map deltas for building roles, interaction cells, reserved flags, and construction progress.
- [x] Expand map deltas for door open state, bed ownership, and workbench bill counts.
- [x] Expand map deltas for reservation target identity.
- [x] Expand map deltas for deeper workbench/job state.
- [x] Add movement interpolation client-side for pawns.

Done:
- Viewer can pan away from pawn and see pawn/building/item positions from data, not the streamer camera.
- Ground item map symbols no longer depend on host camera visibility.
- Richer building and item metadata survives host reconnect and relay-cache gap repair through legacy `map_delta`.
- Entity despawns/removals are explicit and do not depend on full-list replacement.
- Entity state can now travel as its own replayable targeted stream, so future chunks can shrink legacy `map_delta` without breaking old clients.
- Standalone entity messages now carry their own optional `entityEpoch/entitySeq/entityBaseSeq` envelope; the browser tracks it separately from the legacy map stream and the relay validates entity-base gaps when the envelope is present.
- Standalone entity deltas now send only changed/new entity payloads and explicit removals; legacy `map_delta` keeps full lists for compatibility.
- Pawn positions render smoothly between host-authored entity updates without changing authoritative cell positions.
- Fire, plants, and construction frames/blueprints now render as stable map symbols instead of disappearing from the data view.
- Buildings now carry role, flags, and interaction-cell metadata; construction entries carry progress for clearer build-state reading.
- Doors, beds, and workbenches now carry small role-specific details that the browser can draw without host camera frames.
- Reserved buildings, items, and world entities can now carry optional reserver identity, reserver job def, and reservation target thing/cell metadata.
- Workbenches now carry optional source-backed bill details: active/suspended/paused state, repeat info, target counts, product counts, ingredient filter summaries, skill gates, quality ranges, and worker restrictions.
- Workbenches now carry optional current worker/progress metadata when a pawn is actively running a `DoBill` job at that bench.

### Phase 3: Chunked Map State

**Goal:** Send changed map chunks, not whole maps or continuous images.

Tasks:
- Divide map into fixed chunks, likely `32x32` cells.
- Track dirty terrain, roof, fog, building, item, and plant chunks.
- Send `map_chunk` updates with chunk coordinates, version, and payload.
- Relay caches latest chunk versions.
- Browser only redraws dirty chunks.

Done:
- Late-joining viewer gets cached baseline plus current chunks.
- Existing viewer receives only dirty chunks after baseline.
- Large maps do not force full resend during normal play.

### Phase 4: Visual Identity Layer

**Goal:** Make the data renderer readable and RimWorld-like without waiting for live camera frames.

Tasks:
- Add def-driven icons/colors for terrain, buildings, items, pawns, weapons, apparel, doors, fire, plants, and threats.
- Export a small icon/atlas manifest from RimWorld where feasible.
- Fall back to stable geometric symbols when an icon is unavailable.
- Keep labels sparse and zoom-dependent.

Done:
- Viewer can identify important objects at a glance.
- Missing graphics degrade to clean symbols, not blank state.
- Renderer remains fast on mobile and desktop.
- First browser-side glyph pass renders readable door, bed, workbench, hostile, item, weapon, apparel, food, medicine, and material markers from host-provided entity metadata.

### Phase 5: Native RimWorld Action Feel

**Goal:** The browser feels like a RimWorld client, not a remote control dashboard.

Tasks:
- Keep selected pawn visible as the active unit.
- Right-click cell/entity opens host-generated FloatMenu-style actions.
- Add selection affordances for target entity/cell.
- Add action previews for move, attack, equip, consume, rescue, tend, interact, and prioritize work.
- Keep all final decisions host-authoritative.

Done:
- Viewers can play by looking at the map and right-clicking, not digging through tabs.
- Invalid actions show RimWorld-derived disabled reasons.
- Browser hover now shows the exact target cell, highlights hovered entities, and leaves a short-lived sent-order marker for move/attack/context requests.
- Right-click and attack requests can now carry the hovered data-renderer entity id, and the host prefers that thing's real map position before coordinate probing.
- Context-menu responses now render a compact resolved target header with the target label and map cell/id.
- Context-menu options now carry `priority`, `priorityName`, `orderInPriority`, `tooltip`, `disabledReason`, and `iconDefName`/`iconLabel` from the host. The browser sorts host-side by priority then `orderInPriority`, renders inline disabled reasons in italic muted text, exposes tooltip and priority via the `title` attribute, and tags high-priority (`AttackEnemy`+) options and Go-here options with a left border accent.

## Tracker

| ID | Work item | Owner | Status | Deliverable |
| --- | --- | --- | --- | --- |
| VDR-001 | Architecture/protocol audit | Opus | Complete | `docs/TASK_ZHUANG_OPUS_RESULT.md` |
| VDR-002 | Slice 1-3 implementation plan | Sonnet | Complete | `docs/TASK_ZHUANG_SONNET_RESULT.md` |
| VDR-003 | Integration tracker and gating | Codex | Active | This file, `docs/TASK_ZHUANG_CODEX.md` |
| VDR-004 | Protocol spec | Codex | Accepted with open questions | `docs/VIEWER_DATA_RENDERER_PROTOCOL.md` |
| VDR-005 | Data camera UI slice | Codex + workers | Slice 4 implemented | `relay-server/public/app.js`, `relay-server/public/tilemap.js`, `relay-server/scripts/smoke-tilemap.js`, `Source/Viewer/ViewerManager.cs`, `Source/Core/OverlordGameComponent.cs`, `relay-server/server.js` |
| VDR-005b | Relay cache/replay hardening plan | Opus2 | Dispatched | `docs/TASK_OPUS2.md`, `docs/TASK_OPUS2_RESULT.md` |
| VDR-006 | Expanded entity schema | Codex | Partial: fog-filtered dynamic entities, incremental standalone keyed pawns/buildings/items/world symbols + semantic flags/details, independent entity envelope, reservation metadata, workbench bill details and active worker progress, removals, and pawn interpolation shipped | `RimWorldCompat.cs`, `TileMapSerializer.cs`, `ViewerSession.cs`, `ViewerManager.cs`, `server.js`, `app.js`, `tilemap.js`, `smoke-tilemap.js`, `smoke-relay-cache.js` |
| VDR-007 | Chunk cache and resync | Codex | Slice 20 implemented | host chunk serializer, relay chunk replay cache, browser chunk application, smoke coverage |
| VDR-008 | Visual identity layer | Codex | Slice 21 implemented | browser glyph layer, visual identity debug marker, smoke coverage |
| VDR-009 | Host action metadata enrichment | Codex | Slice 25 implemented | `RimWorldCompat.BuildContextOptionMetadata`, priority/disabled reason/tooltip/icon-def in context_menu, browser inline-reason render, smoke coverage |

## Verification Matrix

| Feature | Required test |
| --- | --- |
| Data camera pan | WASD changes browser camera without sending pawn move |
| Follow pawn | Recenter/follow snaps camera back to controlled pawn |
| Right-click context | Browser cell matches host cell in command payload |
| Sequence repair | Client detects skipped delta and requests resync |
| Relay cache | Late viewer gets current baseline without forcing full host resend |
| Fog/security | Browser does not receive disallowed hidden entity details |
| Fallback | JPEG mode still works if data renderer capability is absent |
| Performance | Commands remain responsive while map updates are dropped/coalesced |

## Risks

- RimWorld state visibility is nuanced. Fog, allowed areas, hostile visibility, and reservations must be serialized deliberately.
- Browser renderer can drift if deltas miss deletion/despawn events.
- Relay cache must be single-room authoritative until external state storage exists.
- Asset export can grow scope quickly. Use def-driven symbols first.
- The legacy `map_full`/`map_delta` path is still sparse and not visibility-safe enough to become the only renderer.

## Model Lanes

| Model | Lane | Write scope |
| --- | --- | --- |
| Opus | Read-only architecture and protocol audit | `docs/TASK_ZHUANG_OPUS_RESULT.md` only |
| Sonnet | Read-only phased implementation plan for first slices | `docs/TASK_ZHUANG_SONNET_RESULT.md` only |
| Codex | Tracker, integration, protocol synthesis, implementation gates | Tracker docs, session log, source integration |

## Dispatch Notes

- Every model must follow `AGENTS.md`.
- The worktree is dirty. Do not revert unrelated files.
- Opus and Sonnet result docs were reviewed before Slice 1 source edits.
- Opus2 has a read-only relay cache/replay design lane in `docs/TASK_OPUS2.md`.
- Prefer parallel async work from here onward: run independent reads/checks together and split implementation/audit lanes across agents whenever write scopes are disjoint.

## Current Next Slice Notes

- Slice 2i ships fail-soft reservation metadata behind `RimWorldCompat`: reserver pawn id/label, reserver current job def, and target thing/cell where RimWorld exposes it safely.
- Slice 2j ships source-backed workbench bill details behind `RimWorldCompat`: repeat info, suspended/paused/active state, target/product counts, ingredient summaries, skill gates, quality ranges, and worker restrictions.
- Slice 2k ships source-backed active workbench job metadata behind `RimWorldCompat`: worker identity, job report, bill/recipe, work left/total, progress percent, toil, active skill, and current target B metadata.
- Slice 2l starts native entity stream separation: `entity_keyframe`/`entity_delta` now carry optional `entityEpoch`, `entitySeq`, `entityBaseSeq`, and `entitySnapshot` fields independent of legacy map sequence.
- Slice 2m reduces standalone `entity_delta` payloads to changed/new entity records plus removals, while preserving full legacy `map_delta` lists.
- Slice 2n filters dynamic pawns, buildings, items, plants, fire, and construction entries against RimWorld fog before serializing viewer-bound tactical-map deltas.
- Slice 2o masks full-map baseline terrain/roof bytes for fogged cells, sends a packed `fog` bitset, and has the browser render hidden cells without seeing raw terrain.
- Slice 2p sends changed fog-masked terrain/roof/fog chunks after baseline, applies them in the browser, and replays cached chunks from the relay during resync.
- Slice 2q adds the first browser visual-identity glyph layer for doors, beds, workbenches, hostiles, items, weapons, apparel, food, medicine, and materials.
- Slice 2r adds browser-side hover and sent-order affordances over the data-rendered map while preserving host-authoritative command validation.
- Slice 2s sends hovered entity identity with attack/context-menu requests and has the host prefer the identified thing's map position before falling back to cell probing.
- Slice 2t improves the browser context menu with a resolved target header and smoke coverage for target-label rendering.
- Slice 2u ships richer host-generated context-menu metadata behind `RimWorldCompat.BuildContextOptionMetadata`: priority/orderInPriority sort, `tooltip`, `disabledReason`, `iconDefName`/`iconLabel`. The browser renders disabled reasons inline, surfaces tooltip/priority via the `title` attribute, and tags high-priority/Go-here options with a left border accent.
- Next renderer hardening work should move into source-backed visual manifests (def-driven icon/atlas export) now that the action menu carries enough metadata to feel RimWorld-native.
