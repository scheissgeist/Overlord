# Puppeteer 1.5 Reference Notes

Sources reviewed:

- `https://github.com/veloxcity/Puppeteer-1.5-Update` at `cb6001b878cb92b157f0d3bcb3e4cf5fddadfd07`
- `https://github.com/pardeike/Puppeteer-Central` at `011c25d4a737816d893effd0cfccf2023a632562`

## What Helps Overlord

### Binary WebSocket Protocol

Puppeteer serializes messages as BSON on both the mod and browser/server side instead of JSON with base64 payloads.

Useful pattern:

- Structured command/state messages and image bytes travel in one binary frame.
- The browser receives `arraybuffer`, deserializes BSON, and turns image bytes into a `Blob` URL.
- This avoids the base64 size inflation and client decode overhead in Overlord's current `map_frame` path.

Overlord should keep JSON for low-volume control if desired, but map frames/chunks should move to binary.

### Backpressure Is Core Design

Puppeteer-Central marks visual/state updates as skipable. For skipable messages it only sends when `socket.bufferedAmount == 0`, and it reports a viewer as `stalling` when their socket starts buffering.

This validates Overlord's current relay backpressure work. We should go further:

- Drop visual frames whenever a viewer already has buffered data.
- Track a viewer `slow` state and tell the host to reduce that viewer's map frequency.
- Never let command messages wait behind visual traffic.

### Viewer-Driven Map Rectangles

Puppeteer does not simply stream the streamer camera. The browser owns the requested map frame:

- Initial grid request has no frame, so the mod sends a small pawn-centered rectangle.
- Browser pan/zoom changes `newFrame`.
- Browser periodically sends `state: grid` with the requested rectangle.
- The mod renders that rectangle and sends it back.

This is a better interaction model than pushing a continuous pawn camera frame to every viewer. It supports independent camera, close zoom, and reduced rendering when the viewer is idle.

### Host Work Is Queued By Type

Puppeteer has separate operation queues for portrait, map render, state mutation, job, selection, social, gear, inventory, and log work. It processes only small pieces during known game UI/update hooks.

Overlord already has a generic send queue and portrait queue, but we should convert map rendering and expensive state builders to explicit typed queues with per-tick budgets.

### Real RimWorld Selection/Gizmo Bridge

Puppeteer can ask the game for real RimWorld actions at a selected cell:

- `menu` uses `FloatMenuMakerMap.ChoicesAtFor`.
- `select` uses selectable objects and actual gizmos.
- It renders the real gizmo icons into an atlas and sends disabled reasons.

This is a major "feels like RimWorld" feature. Overlord has context menu support, but Puppeteer's design shows the better end state: click an object, see the actual available RimWorld actions, and run one safely.

### Streamer-Side Feedback

Puppeteer shows connection status, outgoing queue pressure, average send time, background-operation count, assignment state, and action override cooldown directly inside RimWorld.

Overlord should add similar always-visible host diagnostics, but with clearer labels:

- Relay connected/offline
- Host command queue
- Outgoing send queue
- Map render queue
- Dropped visual frames
- Slow viewers
- Last command latency

## What Not To Copy

### Not Durable Across Multiple Servers

Puppeteer-Central keeps live clients, games, sockets, assignments, and viewer joins in process memory. This has the same multi-process split risk Overlord just fixed on Fly.

Do not scale Overlord horizontally until room state is externalized.

### Still JPEG-First

Puppeteer improves over a streamer-camera stream by using requested rectangles, but it still uses rendered JPEG grids as the gameplay map.

Overlord's better long-term path remains:

- Static terrain/building/item chunks cached in the browser.
- Compact pawn/item/threat/job deltas.
- Optional rendered chunks for visual polish.

### Old UI Structure

Puppeteer's browser UI is functionally rich but visually old and tab-heavy. It is useful as an information model, not as the final visual design.

## Recommended Borrow Order

1. Convert map frames to binary frames or BSON/MessagePack payloads.
2. Make visual messages fully skipable when a viewer socket has buffered data.
3. Add per-viewer slow/stalling state and surface it in the host console.
4. Move viewer map control to requested rectangles: browser chooses frame, host renders latest requested frame.
5. Replace continuous per-viewer camera streaming with a coalesced render queue.
6. Add real RimWorld selection/gizmo atlas support.
7. Add action-label restriction zones similar to Puppeteer's off-limits system.
8. Build the structured chunk/delta renderer after the above stabilizes the current live-image path.

## Conclusion

Puppeteer confirms the direction:

- Binary transport for heavy payloads.
- Skipable visual sends.
- Viewer-owned camera rectangle.
- Queued host work.
- Real RimWorld action discovery.

It does not invalidate the Overlord redesign. It gives us a practical intermediate step before the full structured map/chunk renderer.
