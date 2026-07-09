# Viewer Data Renderer Protocol

**Project:** Overlord  
**Status:** Accepted v0 with open questions; Slice 2m incremental entity deltas implemented  
**Last updated:** 2026-05-02

## Purpose

This protocol moves Overlord from a JPEG-first viewer experience to a data-rendered browser client. The viewer should feel like they are playing RimWorld themselves, with their pawn selected.

RimWorld remains authoritative. The browser renders local camera movement and UI state, but every game action is a request back to the host.

## Source Baseline

Current source-backed pieces:

- `Source/MapView/TileMapSerializer.cs`
  - `SerializeFullMap` emits `map_full` with `width`, `height`, base64 terrain bytes, and packed roof bits.
  - `SerializeDelta` emits `map_delta` with compact pawn, richer building, and ground item lists.
- `relay-server/public/tilemap.js`
  - Already supports WASD, arrow keys, drag pan, wheel zoom, tile click, right-click, and recenter behavior.
- `relay-server/public/app.js`
  - Slice 1 now activates the legacy tilemap renderer when `map_full`/`map_delta` arrives.
  - `?renderer=jpeg` forces the JPEG path for debugging and fallback verification.
- `Source/Viewer/ViewerSession.cs` and `Source/PawnControl/PawnCommandRouter.cs`
  - Already support independent `camera_zoom` state for the JPEG path: zoom, optional center, follow-pawn, viewport aspect, and pixel height.
- `relay-server/server.js`
  - Has a first-pass in-process per-viewer replay cache for targeted legacy state: `map_full`, valid `map_delta`, `entity_keyframe`, `entity_delta`, pawn detail, permissions, Toolkit state, colonist list, host capabilities, and game info.
  - It still does not cache authoritative chunks, externalized room state, or per-viewer visibility-filtered chunk sets.
- `Source/MapView/MapRenderer.cs` and `Source/Networking/BinaryFrameProtocol.cs`
  - Existing binary `map_frame` JPEG path remains the fallback.

## Design Rules

1. **RimWorld is authority.** Browser state is rendering and intent only.
2. **Control traffic is sacred.** Commands, acks, assignment, moderation, and repair outrank visual data.
3. **Visual data is disposable.** Map chunks and rendered frames can be dropped or coalesced under backpressure.
4. **State needs repair.** Every state stream needs epoch, sequence, base revision, and resync behavior.
5. **Security is host-side.** Do not send a viewer state they are not allowed to know.
6. **JPEG stays during migration.** `map_frame` remains fallback until the data renderer is feature-complete enough for live play.

## Protocol Envelope

New state messages should use a common envelope:

```json
{
  "type": "entity_delta",
  "protocol": "vdr/0",
  "roomEpoch": "host-session-id",
  "mapId": 0,
  "mapEpoch": 12,
  "seq": 1042,
  "baseSeq": 1041,
  "tick": 384920,
  "target": "viewerlogin"
}
```

Field meanings:

| Field | Meaning |
| --- | --- |
| `protocol` | Protocol family/version. Use `vdr/0` for this draft. |
| `roomEpoch` | Changes whenever the host reconnects or a new save/map session invalidates relay cache. |
| `mapId` | Host-local map identity. Start with `Find.Maps` index or a stable generated map key. |
| `mapEpoch` | Increments when map dimensions or baseline map identity changes. |
| `seq` | Monotonic sequence for this viewer-visible state stream. |
| `baseSeq` | Last sequence the delta assumes the client has. |
| `tick` | RimWorld game tick when serialized. |
| `target` | Optional viewer login when state is visibility-filtered or viewer-specific. |

Do not rely on message arrival order alone. WebSocket order helps, but reconnects, host reloads, relay cache, and client repair need explicit sequence semantics.

## Message Types

### Capability

`host_capabilities` should eventually advertise:

```json
{
  "dataRenderer": true,
  "dataProtocol": "vdr/0",
  "mapFullV2": true,
  "mapChunks": true,
  "entityDeltas": true,
  "entityKeyframes": true,
  "jpegFallback": true,
  "tacticalMapEntityVisibility": "fog",
  "visibilityFilteredEntities": true,
  "visibilityFilteredMap": false
}
```

Old hosts without these flags stay on current `map_frame` or legacy `map_full`/`map_delta`.

### Map Manifest

`map_manifest` describes the active map and palette dictionaries.

```json
{
  "type": "map_manifest",
  "protocol": "vdr/0",
  "roomEpoch": "...",
  "mapId": 0,
  "mapEpoch": 12,
  "seq": 1,
  "width": 250,
  "height": 250,
  "chunkSize": 32,
  "terrainPalette": { "1": "Soil", "7": "RoughStone" },
  "roofPalette": { "1": "RoofRockThin", "2": "RoofRockThick" },
  "thingPalette": { "Wall": { "label": "wall", "kind": "building" } }
}
```

This replaces hardcoded client-only meaning where possible. The current `tilemap.js` color tables are acceptable for Slice 1 but should become def/palette driven.

### Full Map

`map_full_v2` is a complete baseline for a map epoch.

Payload should include:

- map dimensions
- chunk size
- terrain bytes or chunk references
- roof bytes
- explored/fog/visibility bytes if viewer-filtered mode is active
- optional movement/passability bytes
- optional room/zone/area overlays

For Slice 1, this can wrap the current `map_full` data shape with protocol fields.

### Map Chunk

`map_chunk` sends one changed chunk.

```json
{
  "type": "map_chunk",
  "protocol": "vdr/0",
  "mapId": 0,
  "mapEpoch": 12,
  "seq": 204,
  "chunkX": 3,
  "chunkZ": 7,
  "revision": 18,
  "checksum": "crc32-or-fast-hash",
  "layers": {
    "terrain": "base64-bytes",
    "roof": "base64-bits",
    "fog": "base64-bits"
  }
}
```

Chunks are visual/state data and can be coalesced. Relay should keep only the newest chunk per `mapId/mapEpoch/chunkX/chunkZ`.

### Entity Keyframe

`entity_keyframe` is the complete current entity table visible to a viewer or globally safe to broadcast.

Entities should be keyed by stable RimWorld `thingIDNumber` where possible.

Minimum entity fields:

```json
{
  "id": 12345,
  "kind": "pawn",
  "defName": "Human",
  "label": "BroTeam",
  "x": 120,
  "z": 80,
  "rot": 2,
  "faction": "player",
  "hostile": false,
  "visible": true,
  "flags": ["drafted", "selectedPawn"]
}
```

Entity kinds:

- `pawn`
- `animal`
- `mech`
- `item`
- `building`
- `door`
- `plant`
- `fire`
- `filth`
- `construction`
- `projectile` later, only if useful

### Entity Delta

`entity_delta` updates the entity table.

```json
{
  "type": "entity_delta",
  "protocol": "vdr/0",
  "seq": 205,
  "baseSeq": 204,
  "updates": [
    { "id": 12345, "x": 121, "z": 80, "job": "hauling steel" }
  ],
  "removes": [
    { "id": 991, "reason": "despawned" }
  ]
}
```

Slice 2 can use full-list keyframes. Slice 3 should introduce true incremental add/update/remove semantics.

### Pawn Detail State

Existing `pawn_state` should remain for deep selected-pawn data: needs, skills, gear, social, story, schedule, work priorities, permissions, and current command status.

Do not fold all pawn detail into entity deltas. The map renderer needs fast, shallow entity updates; the side panels need detailed pawn state.

### Viewport Interest

Once the relay caches map/entity state, the browser can send:

```json
{
  "type": "viewport_interest",
  "protocol": "vdr/0",
  "centerX": 120,
  "centerZ": 80,
  "zoom": 8,
  "pixelWidth": 1920,
  "pixelHeight": 1080,
  "followPawn": false
}
```

Initial implementation can keep this browser-local. Later, relay/host can prioritize visible chunks/entities and reduce offscreen visual update rate.

### State Resync

Client sends `state_resync_request` when:

- `roomEpoch` changed
- `mapEpoch` changed
- received `baseSeq` does not match local last sequence
- chunk checksum mismatch
- entity table references unknown required IDs
- viewer reconnects after missing too much delta history

```json
{
  "type": "state_resync_request",
  "protocol": "vdr/0",
  "reason": "seq_gap",
  "lastSeq": 1041,
  "wanted": ["map_manifest", "map_chunks", "entity_keyframe"]
}
```

Relay may answer from cache. If cache is missing/stale, relay asks host for a fresh baseline.

## Relay Cache Shape

Current `relay-server/server.js` tracks sockets, sessions, ops logs, frame/backpressure stats, and routes host messages. The data renderer needs a room cache:

```js
roomState = {
  roomEpoch,
  hostConnected,
  mapEpoch,
  mapManifest,
  latestSeq,
  chunks: Map("mapId:mapEpoch:chunkX:chunkZ" -> {
    revision,
    checksum,
    payload,
    updatedAt
  }),
  entities: Map("entityId" -> {
    revision,
    seq,
    data,
    updatedAt
  }),
  recentDeltas: RingBuffer(500-2000),
  viewerState: Map("login" -> {
    lastAckSeq,
    viewport,
    rendererMode,
    slow,
    assignedPawnId
  })
}
```

Short term: keep this in the single Fly room process. Long term: externalize room state only after the protocol works.

## Visibility and Security

Current `map_full` sends full map terrain/roof data when tactical map is enabled, and current `map_delta` iterates all spawned pawns and selected buildings. That is acceptable only as a streamer-enabled tactical-map mode, not as the final security model.

Security boundaries:

- Host decides what a viewer may know.
- Relay may cache already-filtered per-viewer messages.
- Browser filtering is UI only, never security.
- Fogged/unknown entities should be omitted or sent as coarse unknown markers only if explicitly allowed.
- Viewer-controlled pawn state can always include that pawn's current status.
- Admin sockets may receive broader state than viewer sockets.

Open decision:

- Whether normal viewers can see the full colony map regardless of pawn vision. This is a streamer product decision, not a browser implementation detail.

## Command Correlation

Current commands rely mostly on `action_result` by action. Data renderer commands should add `commandId`:

```json
{
  "type": "command",
  "action": "move",
  "commandId": "viewer-123",
  "x": 120,
  "z": 80,
  "clientSeq": 55
}
```

Host responds:

```json
{
  "type": "action_result",
  "commandId": "viewer-123",
  "action": "move",
  "ok": true,
  "tick": 384944,
  "message": "Moving to (120, 80)"
}
```

This lets the browser show optimistic UI without confusing old acks with new clicks.

## JPEG Fallback

Keep `map_frame` for:

- old host builds
- streamer chooses live image mode
- data renderer lacks a needed visual layer
- modded content is not represented well by symbols yet
- debugging render/data drift
- portrait/action preview surfaces that benefit from real rendered imagery

Fallback should become optional, not deleted.

## Migration Order

### Slice 1: Data Camera Activation

Use existing `map_full`/`map_delta` and `TileMapRenderer`.

Implemented in this pass:

- If map data arrives, show tilemap as primary unless `?renderer=jpeg` is present.
- WASD/drag/wheel stay local to `tilemap.js`.
- Left/right click continue to send current host-validated command/context messages.
- `window.OverlordDebug.mapPointToCell` resolves through the active renderer.
- `npm run smoke:tilemap` covers `map_full`/`map_delta`, local WASD/drag, move clicks, and context cell mapping.

Still open after Slice 1:

- Add a visible renderer mode toggle instead of only the debug query parameter.
- Add sequence/epoch repair before relying on data renderer as the only map source.

### Slice 1b: Legacy Baseline Repair

Implemented in this pass:

- `StateProtocol` now has constants for legacy `map_full` and `map_delta`.
- `TileMapSerializer` uses those constants.
- `ViewerManager.SendTacticalMapSnapshot()` sends a full map followed by an immediate current delta.
- Assignment and `request_state` use that shared helper when `allowViewerTacticalMap` is enabled.
- Browser reconnect/resync clears the old map surface while waiting for a new baseline.
- `smoke:tilemap` simulates `host_connected`, asserts fresh `request_colonist_list`/`request_state`, verifies tilemap reset, then replays `map_full`/`map_delta`.

Still open after Slice 1b:

- Add explicit sequence/epoch repair; this slice only gives legacy baseline resend.
- Add relay cache/replay so host does not have to resend every repair baseline.
- Add a visible renderer mode toggle instead of only the debug query parameter.

### Slice 1c: Legacy Sequence and Gap Repair

Implemented in this pass:

- `StateProtocol` now includes `state_resync_request`.
- The relay allow-list forwards `state_resync_request` from authenticated viewers to the host.
- The host treats `state_resync_request` like `request_state` until relay cache/replay exists.
- Legacy `map_full` and `map_delta` carry the v0 envelope fields:
  - `protocol: "vdr/0"`
  - `mapEpoch`
  - `seq`
  - `baseSeq`
  - `snapshot`
  - `tick`
  - `mapId`
- Each viewer session keeps transient tactical map stream counters.
- Browser state tracks the active map epoch and last sequence.
- Sequenced deltas are applied only when `mapEpoch` and `baseSeq` match the browser baseline.
- On gap, the browser sends `state_resync_request` plus compatibility `request_state`.
- `smoke:tilemap` injects an artificial sequence gap and verifies repair through a new full baseline.

Still open after Slice 1c:

- Broader relay cache/replay. Today only the latest targeted legacy messages are cached.
- A visible renderer mode toggle instead of only the debug query parameter.
- Separate chunk/entity streams with their own revisions and checksums.

### Slice 1d: Legacy Relay Cache Replay

Implemented in this pass:

- Relay stores latest targeted replayable host messages per viewer:
  - `host_capabilities`
  - `permissions`
  - `pawn_state`
  - `toolkit_state`
  - `colonist_list`
  - `map_full`
  - `map_delta`
  - `game_info`
- Relay clears the room replay cache on host connect/reconnect.
- Relay clears a viewer replay cache on kick, ban, or pawn death messages.
- Relay answers `state_resync_request` from cache when it has replayable messages for that viewer.
- Relay only caches a `map_delta` if:
  - a cached `map_full` exists
  - its `mapEpoch` matches the cached baseline when both are sequenced
  - its `baseSeq` matches the cached stream's current sequence when both are sequenced
- If cache replay succeeds, relay does not forward `state_resync_request` to the host.
- The browser still sends compatibility `request_state` after a gap for now, so old host repair remains available.
- `/health`, `/admin/status`, and `admin_sync` expose `replayCacheViewers`.
- `/admin/cache` and `/admin/replay-cache` expose metadata-only cache diagnostics: viewer login, slot names, age, bytes, map epoch, and sequence.
- On viewer reconnect, relay replays cached state immediately if that viewer still has valid targeted cache entries.
- Admin-minted replacement viewer sessions clear any replay cache for that login before issuing the new session.
- Expired sessions clear that viewer's replay cache when no other valid session for the same login remains.
- When `host_capabilities.tacticalMap === false`, relay clears cached `map_full` and `map_delta` for the target viewer, or for all viewers if the capability message is broadcast.
- Cached replay messages are annotated with `relayCached: true` and `relayCachedAt` at replay time.
- Relay sends `relay_capabilities` to viewers on connect with `replayCache`, `cacheResync`, `replayAnnotations`, and `version`.
- Browser resets relay capabilities on each new socket. If `relay_capabilities.cacheResync === true`, a map gap sends only `state_resync_request`; otherwise it also sends legacy `request_state`.
- `smoke:tilemap` verifies an artificial bad delta is rejected by the browser, not cached by relay, repaired from cached `map_full`/`map_delta`, and not forwarded as `state_resync_request` to the fake host.
- `smoke:tilemap` also verifies that a cache-capable relay suppresses compatibility `request_state` after the map gap.
- `smoke:relay-cache` verifies cache diagnostics, cached `state_resync_request` repair, replay annotation, host non-forwarding, automatic reconnect replay, admin-session cache eviction, and tactical-map-disabled cache eviction.

Still open after Slice 1d:

- Keep the compatibility `request_state` fallback only for relays that do not advertise cache resync.
- Add relay replay for richer entity keyframes/chunks once those streams exist.
- Add TTL or explicit cache lifecycle for long-idle viewer caches beyond session expiry if needed.
- Add visibility/security filtering before expanding cache scope beyond targeted viewer-safe messages.

### Slice 2a: Legacy Structured Entity Seed

Implemented in this pass:

- Legacy `map_delta.buildings` remains backward compatible:
  - existing prefix stays `[x, z, sizeX, sizeZ, type]`
  - new suffix adds `[id, defName, label, rotation]`
- Legacy `map_delta.items` now carries ground item stacks:
  - `[x, z, kind, stackCount, id, defName, label, flags]`
  - `flags & 1` means forbidden for the viewer pawn
- The browser tile renderer draws ground items as compact map diamonds:
  - weapons, apparel, food, medicine/drugs, materials, and generic items use stable category colors
  - stack count and labels appear only at higher zoom levels
- `RimWorldCompat` now owns map item/building classification and safe thing label/rotation helpers.
- `window.OverlordDebug.getState().tileMap` exposes item counts, truncation state, and small item/building samples for smoke diagnostics.
- `smoke:tilemap` verifies:
  - item metadata reaches the browser
  - richer building metadata survives the legacy array shape
  - the same data survives host reconnect repair and relay-cache sequence-gap repair

Still open after Slice 2a:

- Move from legacy `map_delta` entity payloads to standalone `entity_keyframe`/`entity_delta` streams.
- Add fires, plants, construction frames/blueprints, reservations, and richer door/workbench affordances.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2b: Keyed Entity Table and Removal Seed

Implemented in this pass:

- Legacy `map_delta` now carries an additive keyed entity payload:
  - `entities`: current entity records keyed by RimWorld `thingIDNumber`
  - `entityCount`: host-side current entity count for the viewer stream
  - `entitiesTruncated`: true if the current payload omits entities because of the item cap
  - `entityKeyframe`: true when the browser should clear its entity table before applying `entities`
  - `removedEntities`: IDs that were present in the previous viewer-visible entity set and are no longer present
- `ViewerSession` tracks the last viewer-visible tactical-map entity ID set alongside map epoch/sequence.
- `SendTacticalMapSnapshot()` resets entity tracking so the immediate post-baseline `map_delta` is a keyframe.
- The browser keeps `TileMapRenderer.entities` as a `Map(id -> entity)` and derives pawn/building/item render lists from it.
- `map_full` and tile renderer teardown clear the browser entity table.
- Legacy `pawns`, `buildings`, and `items` arrays remain in `map_delta` for compatibility.
- `smoke:tilemap` now verifies:
  - keyed entity table counts
  - initial entity keyframe handling
  - a removal-only `map_delta` deleting a ground item from the browser table
  - relay-cache gap repair still restores the keyed entity table

Still open after Slice 2b:

- Resolved in Slice 2c below: split this into explicit `entity_keyframe` and `entity_delta` message types once relay replay can cache entity state independently from legacy `map_delta`.
- Add fires, plants, construction frames/blueprints, reservations, and richer door/workbench affordances.
- Resolved in Slice 2d below: add movement interpolation for pawn entities.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2c: Standalone Entity Stream Seed

Implemented in this pass:

- `StateProtocol` now names `entity_keyframe` and `entity_delta`.
- After each legacy tactical-map `map_delta`, the host emits a standalone entity state message derived from the same keyed entity payload.
- Standalone entity messages keep the same v0 envelope fields as their source delta:
  - `protocol`
  - `mapEpoch`
  - `seq`
  - `baseSeq`
  - `snapshot`
  - `tick`
  - `mapId`
- `entity_keyframe` is emitted when the source delta is an entity keyframe; otherwise `entity_delta` is emitted.
- Legacy `map_delta` still carries `entities` and `removedEntities`, so old clients and the current map repair path remain compatible.
- The browser consumes standalone entity messages through the tilemap entity-table updater.
- Standalone entity messages do not advance the browser map stream sequence separately yet, because they currently share the source `map_delta` sequence.
- Relay caches targeted `entity_keyframe` and `entity_delta` messages only when a compatible `map_full` baseline exists.
- Relay clears cached entity messages whenever the matching tactical-map cache is cleared or replaced.
- Relay replays cached standalone entity messages during cache-backed resync and same-viewer reconnect.
- `smoke:tilemap` verifies standalone entity keyframe replay after a gap and standalone entity removal handling.
- `smoke:relay-cache` verifies cache diagnostics, resync replay, reconnect replay, and tactical-map-disabled eviction include entity streams.

Still open after Slice 2c:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Add fires, plants, construction frames/blueprints, reservations, and richer door/workbench affordances.
- Resolved in Slice 2d below: add movement interpolation for pawn entities.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2d: Pawn Movement Interpolation

Implemented in this pass:

- `TileMapRenderer` now keeps a separate visual position cache for pawn-like entities.
- Host-authored `entity.x` and `entity.z` remain the authoritative cell positions.
- When a pawn, animal, or mech receives a new target cell, the browser eases its drawn position toward the new cell over a short local duration.
- Keyframes, map teardown, and explicit removals clear visual interpolation state.
- Large jumps snap instead of animating, so teleports, map changes, or stale repair state do not glide across the map.
- Repeated entity packets with the same target do not restart interpolation.
- `OverlordDebug.getState().tileMap` exposes `pawnVisualSample` and `interpolatingPawnCount` for smoke diagnostics.
- `smoke:tilemap` verifies a moved pawn reports a new authoritative target while its displayed position is still between old and new cells.

Still open after Slice 2d:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Resolved in Slice 2e below: add fires, plants, and construction frames/blueprints.
- Add reservations and richer door/workbench affordances.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2e: Fire, Plant, and Construction Symbols

Implemented in this pass:

- `RimWorldCompat` now classifies map world entities behind the compatibility facade:
  - `Fire` -> `fire`
  - `Plant` -> `plant`
  - `Blueprint` / `Frame` -> `construction`
- `TileMapSerializer` adds those world entities into the keyed scene table with stable IDs, positions, def names, labels, rotation, and small type-specific fields.
- Plant entries include safe growth values.
- Construction entries include size data so frames and blueprints can be drawn at the right footprint.
- `map_delta` and standalone entity messages carry `worldEntityCount` and `worldEntitiesTruncated`.
- Browser tilemap derives `worldEntities` from the keyed entity table.
- Browser renderer draws:
  - fire as a small pulsing orange marker
  - plants as green growth-scaled markers
  - construction/blueprints as outlined build footprints
- `OverlordDebug.getState().tileMap` exposes `worldEntityCount` and `worldEntitySample`.
- `smoke:tilemap` verifies fire, plant, and construction entities reach the browser, render as world symbols, and survive relay-cache gap repair.

Still open after Slice 2e:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Resolved in Slice 2f below: add reservation flags, building roles, interaction cells, and construction progress.
- Add richer door open state, bed ownership, workbench bills, and reservation targets.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2f: Building Semantics and Reservation Flags

Implemented in this pass:

- `RimWorldCompat` exposes map building roles behind the compatibility facade:
  - `wall`
  - `door`
  - `bed`
  - `workbench`
  - fallback `building`
- `RimWorldCompat` exposes shared map thing flags:
  - bit `1`: forbidden for the viewer pawn
  - bit `2`: reserved by RimWorld's reservation manager
- `RimWorldCompat` safely extracts interaction cells for buildings with interaction cells.
- `RimWorldCompat` safely extracts construction progress from frames/blueprints through reflected work fields.
- Building arrays keep their legacy prefix and append:
  - `flags`
  - `interactionX`
  - `interactionZ`
  - `role`
- Building entity dictionaries now include `role`, `flags`, `hasInteractionCell`, and optional `interactionX` / `interactionZ`.
- Item and world entity dictionaries also carry shared `flags`.
- Construction entities now carry `progress`.
- Browser tilemap renders:
  - reserved/forbidden outlines
  - door center lines
  - bed pillow blocks
  - workbench surface marks
  - interaction-cell markers
  - construction progress bars
- `smoke:tilemap` verifies building semantic flags, interaction cells, and construction progress survive initial render and relay-cache gap repair.

Still open after Slice 2f:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Resolved in Slice 2g below: add richer door open state, bed ownership, and workbench bill counts.
- Add reservation target metadata and richer workbench/job state.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2g: Door, Bed, and Workbench Details

Implemented in this pass:

- `RimWorldCompat` safely extracts:
  - door open state
  - bed owner labels
  - workbench bill counts
- Building arrays keep their legacy prefix and append:
  - `open`
  - `ownerLabel`
  - `billCount`
- Building entity dictionaries now include `open`, `owners`, and `billCount`.
- Browser tilemap renders:
  - open doors with a diagonal door mark
  - owned beds with compact owner text at higher zoom
  - workbenches with bill pips at higher zoom
- `smoke:tilemap` verifies door, bed, and workbench details survive initial render and relay-cache gap repair.

Still open after Slice 2g:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Add reservation target metadata and richer workbench/job state.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2h: Workbench Bill Labels

Implemented in this pass:

- `RimWorldCompat` safely extracts up to four workbench bill labels from `BillStack`.
- Building arrays keep their legacy prefix and append:
  - `billLabel`
- Building entity dictionaries now include `billLabels`.
- Browser tilemap renders the compact bill label near workbenches only at higher zoom.
- `smoke:tilemap` verifies workbench bill labels survive initial render and relay-cache gap repair.

Still open after Slice 2h:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Add fail-soft reservation metadata: reserver pawn identity, reserver job def, and target thing/cell where available.
- Keep deeper bill internals such as repeat mode, suspended state, target counts, and ingredient filters as a separate workbench slice.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2i: Reservation Metadata

Implemented in this pass:

- Verified RimWorld 1.6 reservation source with `ilspycmd` against `Verse.AI.ReservationManager`.
- `RimWorldCompat` safely extracts reservation metadata from `ReservationsReadOnly`:
  - reserver pawn id
  - reserver pawn label
  - reserver job def
  - reservation target thing id
  - reservation target cell coordinates when the target is a cell
- `RimWorldCompat.IsReserved()` also checks workbench interaction-cell reservations, matching RimWorld's own workbench reservation conflict path.
- Building arrays keep their legacy prefix and append reservation fields after `billLabel`.
- Item arrays append reservation fields after `flags`.
- Building, item, and world entity dictionaries include named reservation fields only when reserved.
- Browser tilemap preserves reservation metadata through the keyed entity table and renders compact reserved-by text at higher zoom.
- `smoke:tilemap` verifies reservation metadata survives initial render and relay-cache gap repair for building, item, and world entity cases.

Still open after Slice 2i:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Add deeper workbench/job state: bill suspended state, repeat mode, target counts, ingredient filters, and active work progress where safe.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2j: Workbench Bill Details

Implemented in this pass:

- Verified RimWorld 1.6 bill source with `ilspycmd` against `RimWorld.Bill`, `RimWorld.Bill_Production`, `RimWorld.BillStack`, and `Verse.ThingFilter`.
- `RimWorldCompat` safely extracts up to four workbench bill detail dictionaries:
  - label and recipe def
  - suspended, paused, active/waiting state
  - repeat mode/info and repeat count
  - target count and current product count when available
  - ingredient search radius, ingredient filter summary, and allowed def count
  - skill range, pawn restriction, worker restriction, HP range, quality range, include-equipped/tainted flags, and allowed-stuff limit
- Building arrays keep their legacy prefix and append `billDetailSummary` after reservation fields at index `23`.
- Building entity dictionaries now include `billDetails` and `billDetailSummary`.
- Browser tilemap preserves bill detail summaries through keyed entity conversion and renders compact high-zoom workbench detail text.
- `smoke:tilemap` verifies bill detail summaries survive initial render and relay-cache gap repair.

Still open after Slice 2j:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Add active work progress/current worker metadata if RimWorld exposes it safely.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2k: Active Workbench Job Metadata

Implemented in this pass:

- Verified RimWorld 1.6 active bill-work source with `ilspycmd` against `Verse.AI.JobDriver_DoBill`, `Verse.AI.JobDriver`, `Verse.AI.Job`, `Verse.AI.Pawn_JobTracker`, and `Verse.AI.Toils_Recipe`.
- `RimWorldCompat` safely extracts current workbench job metadata when a pawn has a `DoBill` job targeting the bench:
  - worker id and label
  - job def and RimWorld job report
  - bill label and recipe def
  - work left, total work, progress percent, bill start tick, recipe-work ticks, current toil, toil index, ticks left, and active skill
  - current target B id/def/label/cell where available
- Building arrays keep their legacy prefix and append:
  - index `24`: `activeJobSummary`
  - index `25`: active job progress percent, or `-1`
- Building entity dictionaries now include `activeJob` and `activeJobSummary`.
- Browser tilemap preserves active workbench job metadata through keyed entity conversion and renders a compact progress bar plus current worker summary at higher zoom.
- `smoke:tilemap` verifies active job summaries and progress survive initial render and relay-cache gap repair.

Still open after Slice 2k:

- Move from a derived entity stream to native entity revisions with an independent sequence model.
- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2l: Standalone Entity Envelope

Implemented in this pass:

- `ViewerSession` now tracks a transient entity epoch/sequence alongside the legacy tactical map epoch/sequence.
- Host-authored `entity_keyframe` and `entity_delta` messages now include:
  - `entityProtocol`
  - `entityEpoch`
  - `entitySeq`
  - `entityBaseSeq`
  - `entitySnapshot`
- Browser state now tracks the standalone entity stream separately from the map stream.
- Browser accepts old entity messages with no entity envelope for compatibility.
- Browser rejects invalid or gapped entity envelopes and requests the same state resync path used by map gaps.
- Relay cache metadata now stores `entityEpoch` and `entitySeq`.
- Relay rejects an `entity_delta` with an `entityBaseSeq` gap when it already has a compatible entity keyframe/delta.
- `smoke:tilemap` verifies entity stream keyframe/delta acceptance and entity-stream reset across host reconnect repair.

Still open after Slice 2l:

- Reduce `entity_delta` to true incremental updates once the browser no longer needs legacy full-list fallback.
- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.

### Slice 2m: Incremental Standalone Entity Deltas

Implemented in this pass:

- `ViewerSession` now tracks a transient per-viewer hash table for the last host-sent entity state.
- `TileMapSerializer.SerializeDelta()` keeps full legacy `entities`, `pawns`, `buildings`, and `items` in `map_delta` for compatibility.
- The serializer now also computes `entityUpdates`, containing only entities whose stable serialized hash changed or appeared since the previous viewer update.
- `BuildEntityStateMessage()` uses:
  - full `entities` for `entity_keyframe`
  - `entityUpdates` plus `removedEntities` for `entity_delta`
- The browser already applies partial entity updates into its keyed table, so unchanged entities remain rendered without being resent.
- `smoke:tilemap` now verifies a one-entity movement delta updates the pawn and preserves the rest of the scene.

Still open after Slice 2m:

- Add visibility filtering before making entity data broader than streamer-enabled tactical-map mode.
- Start dirty map chunks after visibility policy is enforced.

### Slice 2n: Dynamic Entity Fog Filtering

Implemented in this pass:

- `RimWorldCompat` now exposes `IsTacticalMapThingVisible()` and `IsTacticalMapCellVisible()` as the single compat boundary for tactical-map visibility checks.
- The visibility helper uses RimWorld `FogGrid.IsFogged(IntVec3)` after validating bounds, and checks multi-cell things through `OccupiedRect()`.
- `TileMapSerializer.SerializeDelta()` now skips fogged pawns, colonist buildings, hostile buildings, items, plants, fire, and construction entities before adding them to legacy arrays or standalone keyed entities.
- Viewer capability payloads now advertise `tacticalMapEntityVisibility: "fog"` and `visibilityFilteredEntities: true`; `visibilityFilteredMap` remains false because the full terrain/roof baseline is not masked yet.

Still open after Slice 2n:

- Add fog/exploration bytes to `map_full`/future chunks and have the browser mask terrain/roof display.
- Start dirty map chunks after terrain/fog visibility is represented explicitly.

### Slice 2o: Fog-Masked Full Map Baseline

Implemented in this pass:

- `TileMapSerializer.SerializeFullMap()` now builds terrain, roof, and fog layers together.
- Fogged cells are emitted as zero terrain with no roof bit, and their fog bit is set in a packed base64 `fog` layer.
- `host_capabilities.visibilityFilteredMap` now advertises `true`.
- The browser tile renderer decodes the `fog` bitset, renders fogged cells as hidden, and exposes `hasFog`/`fogCount` in debug state.
- `smoke:tilemap` now sends fog data and asserts the browser received a fogged baseline.

Still open after Slice 2o:

- Start dirty `map_chunk` updates for terrain, roof, and fog changes so newly revealed areas do not need a full baseline resend.

### Slice 2p: Changed Map Chunks

Implemented in this pass:

- Added `map_chunk` to `StateProtocol`.
- `TileMapSerializer` now computes fog-masked chunk layers and stable per-chunk hashes.
- Each viewer session stores last-sent chunk hashes and an independent chunk sequence.
- The host seeds chunk hashes from `map_full` and sends up to four changed chunks per tactical-map delta tick.
- The browser accepts sequenced and legacy unsequenced chunks, updates terrain/fog arrays in place, and redraws the offscreen terrain canvas.
- The relay caches multiple `map_chunk` entries per viewer and replays them after `map_full` during cache-backed resync.
- `smoke:tilemap` verifies live chunk application and relay-cached chunk replay.

Still open after Slice 2p:

- Add def-driven visual identity/icons and richer target affordances on top of the data renderer.

### Slice 2q: First Visual Identity Glyph Layer

Implemented in this pass:

- Browser tile rendering now exposes `visualIdentityVersion: 1` in debug state.
- Known building roles render sparse zoom-dependent glyphs:
  - doors as `D`
  - beds as `B`
  - workbenches/tables/benches as `W`
  - hostile structures as `!`
- Ground items render sparse zoom-dependent glyphs from kind/def/label hints:
  - weapons as `W`
  - apparel as `A`
  - food/meals as `F`
  - medicine/drugs as `+`
  - materials/components as `M`
- Unknown things still render as stable existing symbols instead of disappearing.
- `smoke:tilemap` asserts the visual identity marker is active.

Still open after Slice 2q:

- Replace heuristic glyph selection with a host/exported visual manifest where feasible.
- Add richer hover/selection/target affordances so clicking the data view feels closer to native RimWorld play.

### Slice 2r: Browser Target Affordances

Implemented in this pass:

- `TileMapRenderer` now tracks `hoverCell`, `hoverEntity`, `targetMode`, and `recentTarget`.
- The renderer draws a cell outline under the pointer and a compact label with the target cell or hovered entity.
- Hovered pawns, items, buildings, and world entities get an entity outline derived from the current data-rendered state.
- Move/attack target mode draws a line from the viewer pawn to the hovered target cell.
- Successful move, attack, and context-menu requests leave a short-lived sent-order marker on the map.
- The debug surface exposes hover and recent-target state, and `smoke:tilemap` verifies hover tracking plus sent target markers.

Still open after Slice 2r:

- Have the host return entity-aware FloatMenu options so right-clicking an entity can show RimWorld-derived actions and disabled reasons.
- Add stronger object identity from a host/exported visual manifest instead of browser heuristics.

### Slice 2s: Entity-Aware Click Requests

Implemented in this pass:

- The browser exposes the current hovered entity through `TileMapRenderer.getHoverTarget()`.
- Attack requests include `targetId` when the viewer is attacking a hovered entity.
- Context-menu requests include `targetId`, `targetKind`, and `targetLabel` when the viewer right-clicks a hovered entity.
- The host context-menu router accepts `targetId`/`targetEntityId`, resolves the live spawned `Thing`, and prefers that thing's real map position before falling back to coordinate probing.
- Host context-menu responses include resolved `targetId` and `targetLabel` when a live target was used.
- `smoke:tilemap` right-clicks a rendered item and verifies the outgoing context-menu command carries the expected item id.

Still open after Slice 2s:

- Add target-specific action menus/disabled reasons in the browser once the host can return richer action metadata.
- Add source-backed visual manifest data so the browser does less heuristic object classification.

### Slice 2t: Target-Labeled Context Menu

Implemented in this pass:

- Browser context menus now render a compact header above the action list.
- The header uses `targetLabel` when available, otherwise falls back to the resolved cell.
- The header also shows the target id and resolved map cell when available.
- Context-menu styling was tightened to a 2px radius, bounded width, single-line ellipsis, and a simple divider.
- `smoke:tilemap` sends a fake host context-menu response for a right-clicked item and verifies the target header renders.

Still open after Slice 2t:

- Add richer host-generated action metadata so disabled options can show source-backed reasons instead of only labels.
- Replace heuristic glyphs with a host/exported visual manifest where feasible.

### Slice 2u: Action Metadata Enrichment

Implemented in this pass:

- `RimWorldCompat.BuildContextOptionMetadata(option, id)` returns a viewer-safe dictionary per `FloatMenuOption`: `id`, `label`, `disabled`, `priority` (numeric `MenuOptionPriority`), `priorityName`, `orderInPriority`, optional `tooltip`, optional `disabledReason`, and optional `iconDefName`/`iconLabel` derived from `shownItem`/`iconThing`.
- `disabledReason` extraction prefers a colon-delimited or parenthesized suffix in the option label (RimWorld's vanilla convention for "Cannot do thing: reason"), with the resolved tooltip text as a fallback.
- All field reads use cached reflection so the helper continues to work if RimWorld field shapes drift between 1.5 and 1.6.
- `PawnCommandRouter.ExecuteContextMenu` now sorts options host-side by priority descending, then `orderInPriority` descending, before serializing the top 20.
- The browser renders the inline disabled reason in muted italic text below the label, surfaces tooltip/priority via the `title` attribute, shows a small icon-def hint chip when an icon def is available, and tags high-priority (`AttackEnemy`+) and Go-here options with a left border accent.
- `smoke:tilemap` now fakes both an enabled rifle-equip option (with tooltip + icon hint) and a disabled hauling option (with reserved reason). The smoke asserts disabled state, inline reason text, icon hint chip, and tooltip-derived `title` attribute.

Still open after Slice 2u:

- Replace heuristic glyphs with a host/exported visual manifest where feasible.
- Consider exporting RimWorld FloatMenu icon textures alongside def names so the browser can show real RimWorld icons next to high-frequency actions.

### Slice 2: Protocol Envelope and Relay Cache

- Extend relay cache from legacy targeted messages to latest manifest, chunks, entity keyframe, and recent deltas.
- Let relay answer richer `state_resync_request` variants from cache when it has a valid baseline.
- Keep legacy messages working.

### Slice 3: Rich Entity Keyframes

- Add item/building/pawn/fire/plant/construction entity table.
- Add deletion semantics.
- Add client entity table renderer.
- Add zoom-dependent labels and selection state.

### Slice 4: Chunked Map

- Add dirty chunk tracking.
- Add `map_chunk`.
- Browser redraws chunks instead of full map.

### Slice 5: Visual Identity and Native Actions

- Def-driven icon/atlas manifest.
- Host-generated action menu for selected cell/entity.
- Optional rendered image chunks for high-fidelity local overlays.

## Smoke Coverage Required

Add or extend `relay-server/scripts/smoke-viewer-intake.js` for:

- fake host sends `host_capabilities` with data renderer enabled
- fake host sends `map_full`
- fake host sends `map_delta` with pawns, buildings, and items / future `entity_keyframe`
- browser activates data renderer as primary
- WASD changes tilemap camera without sending pawn move
- wheel zoom changes tilemap zoom
- recenter/follow snaps camera to viewer pawn
- left-click sends expected move/attack cell depending on mode
- right-click sends expected `context_menu` cell
- `map_frame` still renders when data renderer is unavailable or live fallback selected
- client detects artificial sequence gap and sends `state_resync_request` once that protocol exists

## Open Questions for Opus/Sonnet Review

- Exact map/entity sequence model: one global stream or separate chunk/entity streams?
- How aggressively should viewer visibility be filtered by pawn vision versus streamer-approved tactical map?
- Does relay cache per-viewer filtered state, global safe state, or both?
- What is the minimum entity schema for Slice 3 before asset/icon work?
- Should `map_full_v2` stay JSON/base64 initially or move chunks to binary immediately?
- Should the first implementation add `StateProtocol` constants for legacy `map_full`/`map_delta`, or only for v2 messages?

## Implementation Gate

The initial gate is satisfied:

- `docs/TASK_ZHUANG_OPUS_RESULT.md` exists and has been reviewed.
- `docs/TASK_ZHUANG_SONNET_RESULT.md` exists and has been reviewed.
- This protocol is accepted with open questions.

Do not promote the data renderer to sole production renderer until epoch/sequence repair, visibility filtering, and richer entity coverage are implemented.
