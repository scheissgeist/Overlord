# Overlord Low-Latency Redesign

## Target

Overlord should feel like a direct remote control for one pawn:

- Commands stay tiny, prioritized, and never wait behind image traffic.
- The viewer camera is independent of the streamer camera.
- Visuals are live enough to play from, but old frames are disposable.
- The RimWorld main thread never blocks on network, logging, encoding, or slow viewers.
- The relay has exactly one authoritative room state for a stream.

## Current Production Baseline

Immediate stability changes now in place:

- Fly relay is pinned to one Machine in `sjc`.
- Fly autostop is disabled in `relay-server/fly.toml`.
- Relay health exposes the active instance id and pid.
- Relay sends now have WebSocket backpressure. Slow sockets drop stale frames instead of buffering them indefinitely.
- RimWorld host sends are already queued off the main thread.
- Assignment portraits are already queued and cached instead of rendering all at once.

This is the right short-term shape for the current in-memory relay. More Machines are unsafe until room state is externalized.

## Proper Architecture

### 1. RimWorld Host Agent

The mod should be a tick-budgeted publisher and command applier.

- Consume commands from a priority queue on the game thread.
- Apply commands on tick with cooldowns, permission checks, and area policy.
- Publish structured pawn, map, event, and assignment deltas.
- Render visuals only through a budgeted scheduler.
- Never synchronously wait on WebSocket, HTTP, file logging, portrait generation, image encoding, or viewer fanout.

### 2. Room Relay

The relay should be a single authoritative room process per streamer.

- `host` socket: receives commands and sends authoritative game state.
- `viewer` sockets: send intent, receive only their allowed view.
- `admin` sockets: observe, moderate, and repair state.
- Separate control and media priority queues.
- Coalesce state updates by viewer. Keep latest state, drop stale state.
- Drop stale image frames under backpressure.
- Keep latest assignment/session snapshot in memory and optionally persist it.
- On host reconnect, replay connected viewer list and request a host snapshot.

Until the room state is in Redis/NATS/Durable Objects, this should run as one always-on process.

### 3. Viewer App

The browser should render an independent tactical view, not depend on the streamer camera.

- Canvas/WebGL renderer owns pan, zoom, selection, labels, and overlays.
- Server sends a full map snapshot once, then small deltas.
- Viewer interpolates pawn movement locally between authoritative deltas.
- Commands are optimistic in UI, then confirmed or corrected by host acks.
- Hidden tabs and slow clients automatically lower visual update rate.

## Visual Pipeline

The low-lag version should not send a full JPEG per viewer as the primary map.

### Static Map

- Divide the RimWorld map into chunks, for example `32x32` or `64x64` cells.
- Publish chunk data or chunk images when terrain/buildings/items change.
- Cache chunks in the browser.
- Send dirty chunk ids, not the whole map.

### Dynamic Entities

Send compact deltas at a fixed rate:

- Pawns: id, x, z, faction, job, drafted, downed/dead, label when visible.
- Items on ground: id/type/category, x, z, stack count, forbidden/reserved flags.
- Buildings: id/type, x, z, size, faction, usable/hostile state.
- Events: damage, death, raid, fire, target warnings, job changes.

This lets viewers see ground items and colony state without camera dependence.

### Real Image Layer

For the "live real image" feel, use one optional visual layer:

- Low-rate rendered chunks near each controlled pawn, not constant per-viewer full-frame streams.
- Chunks are keyed by map area and zoom band so multiple viewers near the same area share output.
- Old image chunks can be dropped; latest chunk wins.
- Commands never wait behind image chunks.

## Transport

The current JSON/base64 path is simple but expensive.

Better:

- Use JSON only for low-volume control messages.
- Use binary WebSocket frames or HTTP blob URLs for images/chunks.
- Avoid base64 for map frames; it inflates payloads and adds browser decode cost.
- Add sequence numbers and timestamps to every command, ack, state delta, and media frame.
- Track round-trip command latency separately from render latency.

## Hosting Shape

### Short Term

Stay on the current Fly app as one always-on Machine while the relay remains in-memory.

### Production Permanent Option

Move the relay to a single Hetzner VPS in Hillsboro, Oregon if we want predictable behavior and simple operations:

- Docker Compose or systemd service.
- Caddy or nginx for TLS.
- Structured JSON logs on disk.
- One room process, no accidental horizontal split.
- Easy CPU, memory, and network visibility during streams.

### Long-Term Best Architecture

Cloudflare Durable Objects are the clean stateful-room model:

- One object per streamer room.
- WebSockets terminate into the room object.
- Room state and reconnect behavior live with the room.

For Overlord, Durable Objects are best for control/state coordination. Large rendered image delivery should still be modeled carefully, likely with CDN/object storage or a separate media path.

## Migration Order

1. Stabilize current relay.
   - One always-on Fly Machine.
   - WebSocket backpressure.
   - Health shows instance id.

2. Borrow Puppeteer's practical low-lag transport patterns.
   - Use binary frames for visual payloads instead of JSON/base64.
   - Make visual updates fully skipable when a viewer socket has buffered bytes.
   - Track per-viewer slow/stalling state.
   - Let the browser request the visible map rectangle instead of continuously pushing a fixed pawn camera.

3. Split traffic classes.
   - Commands, acks, assignments, and moderation are priority traffic.
   - Visual frames/chunks are disposable media traffic.

4. Replace per-viewer full-frame rendering.
   - Enable map chunk snapshots.
   - Stream entity/item deltas.
   - Keep real rendered chunks as an optional visual layer.

5. Add real observability.
   - Command RTT.
   - Host command queue depth.
   - RimWorld render/encode ms.
   - Relay buffered bytes and dropped frames.
   - Browser decode/draw ms.
   - Viewer reconnect count.

6. Move hosting if needed.
   - Hetzner for simplest permanent single-room server.
   - Durable Objects for a proper globally-routed room rewrite.

## Design Rule

Control traffic is sacred. Visual traffic is expendable.

If a viewer is lagging, drop visual data until they catch up. Do not delay commands, state acks, assignment messages, or reconnect repair.
