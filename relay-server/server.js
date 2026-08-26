'use strict';

const express = require('express');
const http    = require('http');
const https   = require('https');
const { WebSocketServer, WebSocket } = require('ws');
const path    = require('path');
const crypto  = require('crypto');
const fs      = require('fs');

// ─── Config ──────────────────────────────────────────────────────────────────
const PORT             = parseInt(process.env.PORT || '8080', 10);
const HOST_SECRET      = process.env.HOST_SECRET      || '';
const MAX_VIEWERS      = parseInt(process.env.MAX_VIEWERS || '50', 10);
const TWITCH_CLIENT_ID = process.env.TWITCH_CLIENT_ID || '';
// Name-only viewer login, so a streamer can deploy a relay and have people join it
// over the internet WITHOUT first registering a Twitch developer application —
// which was the single hardest step of setup and the one most people never finish.
//
// Deliberately IGNORED when TWITCH_CLIENT_ID is set. With both enabled, a guest
// could type the login of a real Twitch viewer and inherit that person's colonist,
// their replay cache and their auto-reconnect pairing. Making the two mutually
// exclusive removes the impersonation path entirely instead of policing it.
const GUEST_LOGIN = !TWITCH_CLIENT_ID
  && /^(1|true|yes|on)$/i.test(process.env.ALLOW_GUEST_LOGIN || '');
// Open hosting: anyone can register a room on this relay and stream through it.
// OFF by default, so a relay deployed from the README button stays private to the
// person who deployed it and behaves exactly as it did before rooms existed.
const OPEN_HOSTING = /^(1|true|yes|on)$/i.test(process.env.OPEN_HOSTING || '');
// Optional gate on top of OPEN_HOSTING: only people who were handed this code can
// register. Lets a streamer open the relay to friends without opening it to the web.
const HOST_INVITE_CODE = process.env.HOST_INVITE_CODE || '';
// Rooms are permanent now, so this counts everyone who has EVER hosted here, not
// how many are live at once. It is deliberately generous: a dormant room is a few
// Maps, and it is viewers - not rooms - that cost bandwidth. MAX_TOTAL_VIEWERS is
// the limit that bounds the bill.
const MAX_ROOMS = Math.max(1, parseInt(process.env.MAX_ROOMS || '200', 10));
// A room that has EVER hosted is permanent. It used to be swept after 30 idle
// minutes, which quietly expired the link its streamer had already handed out -
// take a break longer than a coffee and everyone you invited hits a dead URL. A
// dormant room costs a few Maps and nothing on the wire; the thing that actually
// costs money is viewers, and MAX_TOTAL_VIEWERS bounds that directly.
// The ONE remaining time limit, and it can never touch a real streamer: a room that
// registered and NEVER hosted is dropped after this long. Without it every abandoned
// Join - a typo, a closed game, someone changing their mind - permanently consumed
// one of MAX_ROOMS and the relay answered the next real streamer with "This relay is
// full" (measured: a second run of the acceptance suite failed at registration with
// HTTP 503). Once a game connects even once, the room is kept forever.
const ROOM_UNCLAIMED_MS = Math.max(60 * 1000, parseInt(process.env.ROOM_UNCLAIMED_MS || String(10 * 60 * 1000), 10));
// MAX_VIEWERS is PER ROOM, so the real ceiling was MAX_ROOMS x MAX_VIEWERS - 400 at
// the defaults, eight times what the operator thinks they capped. Egress is the
// thing that costs money, so it needs its own relay-wide limit.
const MAX_TOTAL_VIEWERS = Math.max(
  MAX_VIEWERS,
  parseInt(process.env.MAX_TOTAL_VIEWERS || String(MAX_VIEWERS * 2), 10)
);
const ROOMS_FILE = process.env.ROOMS_FILE || path.join(LOG_DIR_FALLBACK(), 'rooms.json');
const LOG_TRAFFIC      = process.env.LOG_TRAFFIC !== '0';
function LOG_DIR_FALLBACK() {
  return process.env.LOG_DIR || path.join(__dirname, 'logs');
}
const LOG_DIR          = LOG_DIR_FALLBACK();
const INSTANCE_ID      = process.env.FLY_MACHINE_ID || `${process.pid}`;
const MAX_WS_BUFFERED_BYTES = parseInt(process.env.MAX_WS_BUFFERED_BYTES || String(1024 * 1024), 10);
const MAX_FRAME_BUFFERED_BYTES = parseInt(process.env.MAX_FRAME_BUFFERED_BYTES || String(768 * 1024), 10);
// Viewer sessions are in-memory. Use a long sliding TTL so live streams don't
// force Twitch re-login every few hours; deploys still wipe memory and need client re-auth.
const SESSION_TTL_MS = Math.max(
  60 * 60 * 1000,
  Math.min(parseInt(process.env.SESSION_TTL_MS || String(24 * 3600 * 1000), 10), 7 * 24 * 3600 * 1000)
);
const SESSION_SLIDE_MS = Math.max(5 * 60 * 1000, Math.min(SESSION_TTL_MS, parseInt(process.env.SESSION_SLIDE_MS || String(6 * 3600 * 1000), 10)));
const BINARY_MAGIC = Buffer.from('OVL1', 'ascii');
const BINARY_HEADER_BYTES = 8;
const BINARY_METADATA_LIMIT = 64 * 1024;
const RELAY_CAPABILITIES = Object.freeze({
  type: 'relay_capabilities',
  replayCache: true,
  cacheResync: true,
  replayAnnotations: true,
  mapTransportNegotiation: true,
  version: 2,
});

function normalizeMapTransport(value) {
  const transport = String(value || '').trim().toLowerCase();
  if (transport === 'jpeg' || transport === 'tile') return transport;
  return 'auto';
}

// ─── State ───────────────────────────────────────────────────────────────────
// Everything belonging to ONE running game lives on a Room. The relay used to hold
// exactly one of each of these at module scope, and that is what made it
// single-tenant: a second streamer's game evicted the first (close 4002) because
// there was only one `hostSocket` variable for a game to occupy.
//
// Relay-wide state stays at module scope on purpose. `admins` is the operator's
// console, `sessions` is viewer identity (a person is the same person in any room),
// and the ops log and frame counters measure this process, not any one game.

/** @type {Map<string, ReturnType<typeof makeRoom>>} roomId -> room */
const rooms = new Map();
const admins = new Map();

function makeRoom(roomId, opts) {
  opts = opts || {};
  const room = {
    roomId,
    // The credential a game presents to claim this room. For the owner room it is
    // HOST_SECRET, so the already-shipped mod DLL keeps working untouched. For a
    // registered room it is a key the relay generated and handed to the mod, which
    // is why a streamer joining someone else's relay never invents or copies one.
    hostKey: opts.hostKey || '',
    owner: !!opts.owner,
    label: opts.label || '',
    createdAt: opts.createdAt || Date.now(),
    lastActiveAt: Date.now(),
    hostConnectedAt: 0,
    hostSocket: null,
    /** @type {Map<string, WebSocket>} viewer login -> socket */
    viewers: new Map(),
    viewerInfo: new Map(),
    viewerReplayCache: new Map(),
    roomReplayCache: new Map(),
    viewerBatchQueues: new Map(),
    contextMenuRequests: new Map(),
  };
  rooms.set(roomId, room);
  return room;
}

function roomIsLive(room) {
  return !!room && room.hostSocket !== null && room.hostSocket.readyState === WebSocket.OPEN;
}

function listLiveRooms() {
  const out = [];
  for (const room of rooms.values()) {
    if (!roomIsLive(room)) continue;
    out.push({
      roomId: room.roomId,
      label: roomDisplayLabel(room),
      viewers: room.viewers.size,
      since: room.hostConnectedAt || room.createdAt,
    });
  }
  out.sort(function (a, b) { return (b.viewers - a.viewers) || (a.since - b.since); });
  return out;
}

/**
 * The name shown in the directory. The already-shipped mod broadcasts `game_info`
 * carrying `mapName` (the colony name) and the relay already caches that as a
 * room-level replay message - so a room gets a real label with no mod change at
 * all. Before the first game_info arrives there is nothing yet to show.
 */
function roomDisplayLabel(room) {
  if (room.label) return room.label;
  const cached = room.roomReplayCache.get('game_info');
  if (cached) {
    try {
      const parsed = JSON.parse(cached.text);
      if (parsed && parsed.mapName) return String(parsed.mapName).slice(0, 48);
    } catch (_) {}
  }
  return 'RimWorld colony';
}

function timingSafeEqualStr(a, b) {
  const ba = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  if (ba.length !== bb.length) return false;
  return crypto.timingSafeEqual(ba, bb);
}

function findRoomByHostKey(key) {
  if (!key) return null;
  for (const room of rooms.values()) {
    if (room.hostKey && timingSafeEqualStr(room.hostKey, key)) return room;
  }
  return null;
}

// The owner room exists whenever HOST_SECRET is configured, so a relay deployed
// before rooms existed behaves identically: the mod connects with
// ?secret=HOST_SECRET, lands here, and a viewer with no ?room= is auto-placed.
const OWNER_ROOM_ID = 'main';
if (HOST_SECRET) makeRoom(OWNER_ROOM_ID, { hostKey: HOST_SECRET, owner: true });

// Context menus are expensive host work and viewers only care about the latest
// right-click. Allow one request in flight per viewer and keep at most one newer
// target, paced by wall clock so a paused game cannot create retry storms.
const CONTEXT_MENU_MIN_INTERVAL_MS = 150;
const CONTEXT_MENU_RESPONSE_TIMEOUT_MS = 2000;

function clearContextMenuRequest(room, login) {
  const state = room.contextMenuRequests.get(login);
  if (state) {
    clearTimeout(state.sendTimer);
    clearTimeout(state.responseTimer);
  }
  room.contextMenuRequests.delete(login);
}

function dispatchContextMenuRequest(room, login, msg, state) {
  const delay = Math.max(0, CONTEXT_MENU_MIN_INTERVAL_MS - (Date.now() - state.lastSentAt));
  if (delay > 0) {
    state.queued = msg;
    if (!state.sendTimer) {
      state.sendTimer = setTimeout(() => {
        state.sendTimer = null;
        const latest = state.queued;
        state.queued = null;
        if (latest && !state.inFlight) dispatchContextMenuRequest(room, login, latest, state);
      }, delay);
    }
    return;
  }

  state.inFlight = true;
  state.lastSentAt = Date.now();
  sendToHost(room, msg);
  clearTimeout(state.responseTimer);
  state.responseTimer = setTimeout(() => {
    state.responseTimer = null;
    state.inFlight = false;
    recordOps('context_menu_timeout', { room: room.roomId, username: login });
    flushQueuedContextMenuRequest(room, login, state);
  }, CONTEXT_MENU_RESPONSE_TIMEOUT_MS);
}

function queueContextMenuRequest(room, login, msg) {
  let state = room.contextMenuRequests.get(login);
  if (!state) {
    state = { inFlight: false, queued: null, lastSentAt: 0, sendTimer: null, responseTimer: null };
    room.contextMenuRequests.set(login, state);
  }
  if (state.inFlight || state.sendTimer) {
    state.queued = msg;
    recordOps('context_menu_coalesced', { room: room.roomId, username: login });
    return;
  }
  dispatchContextMenuRequest(room, login, msg, state);
}

function flushQueuedContextMenuRequest(room, login, state) {
  const latest = state.queued;
  state.queued = null;
  if (latest) dispatchContextMenuRequest(room, login, latest, state);
}

function completeContextMenuRequest(room, login) {
  const state = room.contextMenuRequests.get(login);
  if (!state || !state.inFlight) return;
  clearTimeout(state.responseTimer);
  state.responseTimer = null;
  state.inFlight = false;
  flushQueuedContextMenuRequest(room, login, state);
}

// ─── Per-viewer outbound batch queue ─────────────────────────────────────────
// Messages targeted to a specific viewer are queued for up to BATCH_FLUSH_MS
// before being flushed as a single `{"type":"batch","msgs":[...]}` envelope.
// This collapses the 3-6 individual sends per game tick into one TCP segment.
const BATCH_FLUSH_MS = 16;
function flushViewerBatch(room, login) {
  const q = room.viewerBatchQueues.get(login);
  if (!q || q.texts.length === 0) { room.viewerBatchQueues.delete(login); return; }
  room.viewerBatchQueues.delete(login);
  const ws = room.viewers.get(login);
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  if (q.texts.length === 1) {
    sendWs(ws, q.texts[0], { type: 'batched_single', target: login });
    return;
  }
  // Wrap in batch envelope — browser unpacks each msg individually
  const payload = '{"type":"batch","msgs":[' + q.texts.join(',') + ']}';
  sendWs(ws, payload, { type: 'batch', target: login });
}

function queueForViewer(room, login, text) {
  let q = room.viewerBatchQueues.get(login);
  if (!q) {
    q = { texts: [], timer: setTimeout(() => flushViewerBatch(room, login), BATCH_FLUSH_MS) };
    room.viewerBatchQueues.set(login, q);
  }
  q.texts.push(text);
}
const opsLog = [];
const OPS_LOG_LIMIT = 500;
let frameStats = newFrameStats();
let backpressureStats = newBackpressureStats();

/** @type {Map<string, {login: string, displayName: string, exp: number}>} sessionToken → identity */
const sessions = new Map();

const REPLAYABLE_TARGETED_TYPES = new Set([
  'host_capabilities',
  'permissions',
  'pawn_state',
  'toolkit_state',
  'colonist_list',
  'map_full',
  'map_chunk',
  'map_delta',
  'entity_keyframe',
  'entity_delta',
  'game_info',
  'resource_readout',
]);
const REPLAYABLE_ROOM_TYPES = new Set([
  'game_info',
]);
const REPLAY_ORDER = [
  'host_capabilities',
  'permissions',
  'pawn_state',
  'toolkit_state',
  'colonist_list',
  'map_full',
  'map_chunk',
  'map_delta',
  'entity_keyframe',
  'entity_delta',
  'game_info',
  'resource_readout',
];
const ROOM_REPLAY_ORDER = [
  'game_info',
];
const CLEAR_VIEWER_CACHE_TYPES = new Set([
  'viewer_kick',
  'banned',
  'pawn_died',
]);

// ─── Express ──────────────────────────────────────────────────────────────────
const app    = express();
const server = http.createServer(app);

// Serve index.html with TWITCH_CLIENT_ID injected into data attribute
const indexHtmlPath = path.join(__dirname, 'public', 'index.html');
const CLIENT_BUILD = readClientBuild();

function readClientBuild() {
  try {
    const appJs = fs.readFileSync(path.join(__dirname, 'public', 'app.js'), 'utf8');
    const match = appJs.match(/const\s+UI_BUILD\s*=\s*['"]([^'"]+)['"]/);
    return match ? match[1] : 'unknown';
  } catch (_) {
    return 'unknown';
  }
}

function newFrameStats() {
  return {
    startedAt: Date.now(),
    count: 0,
    bytes: 0,
    maxBytes: 0,
    targets: new Set(),
  };
}

function newBackpressureStats() {
  return {
    startedAt: Date.now(),
    frameDrops: 0,
    messageDrops: 0,
    maxBuffered: 0,
  };
}

function recordBackpressure(kind, buffered) {
  if (!LOG_TRAFFIC) return;
  if (kind === 'frame') backpressureStats.frameDrops++;
  else backpressureStats.messageDrops++;
  backpressureStats.maxBuffered = Math.max(backpressureStats.maxBuffered, buffered || 0);

  const elapsed = Date.now() - backpressureStats.startedAt;
  if (elapsed < 10000) return;

  recordOps('backpressure_stats', {
    frameDrops: backpressureStats.frameDrops,
    messageDrops: backpressureStats.messageDrops,
    seconds: Math.round(elapsed / 100) / 10,
    maxBuffered: backpressureStats.maxBuffered,
  });
  backpressureStats = newBackpressureStats();
}

function summarizeMessage(msg, bytes) {
  const out = {
    type: msg && msg.type,
    action: msg && msg.action,
    username: msg && msg.username,
    target: msg && msg.target,
    ok: msg && typeof msg.ok === 'boolean' ? msg.ok : undefined,
    bytes,
  };
  if (msg && typeof msg.data === 'string') out.dataBytes = msg.data.length;
  else if (msg && typeof msg.dataBytes === 'number') out.dataBytes = msg.dataBytes;
  return Object.fromEntries(Object.entries(out).filter(([, v]) => v !== undefined && v !== ''));
}

function sendWs(ws, payload, fields = {}) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return false;

  const type = fields.type || '';
  const isFrame = type === 'map_frame';
  const maxBuffered = isFrame ? MAX_FRAME_BUFFERED_BYTES : MAX_WS_BUFFERED_BYTES;
  if (ws.bufferedAmount > maxBuffered) {
    recordBackpressure(isFrame ? 'frame' : 'message', ws.bufferedAmount);
    return false;
  }

  try {
    ws.send(payload);
    return true;
  } catch (e) {
    recordOps('send_error', { type, target: fields.target || '', error: e.message });
    return false;
  }
}

function recordOps(event, fields = {}) {
  if (!LOG_TRAFFIC) return;
  const entry = {
    ts: new Date().toISOString(),
    event,
    ...fields,
  };
  opsLog.push(entry);
  while (opsLog.length > OPS_LOG_LIMIT) opsLog.shift();
  const line = JSON.stringify(entry);
  console.log(`[relay:${event}] ${line}`);
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    const day = entry.ts.slice(0, 10);
    fs.appendFile(path.join(LOG_DIR, `overlord-${day}.jsonl`), line + '\n', () => {});
  } catch {}
  if (event !== 'host_message' || fields.type !== 'map_frame') {
    broadcastToAdmins({ type: 'ops_log', entry });
  }
}

function recordFrame(msg, bytes) {
  if (!LOG_TRAFFIC) return;
  frameStats.count++;
  frameStats.bytes += bytes || 0;
  frameStats.maxBytes = Math.max(frameStats.maxBytes, bytes || 0);
  if (msg && msg.target) frameStats.targets.add(msg.target);

  const elapsed = Date.now() - frameStats.startedAt;
  if (elapsed < 10000) return;

  recordOps('frame_stats', {
    frames: frameStats.count,
    seconds: Math.round(elapsed / 100) / 10,
    avgBytes: frameStats.count ? Math.round(frameStats.bytes / frameStats.count) : 0,
    maxBytes: frameStats.maxBytes,
    viewers: frameStats.targets.size,
  });
  frameStats = newFrameStats();
}

function parseBinaryFrame(raw) {
  const buffer = Buffer.isBuffer(raw) ? raw : Buffer.from(raw);
  if (buffer.length < BINARY_HEADER_BYTES) {
    throw new Error('binary frame too small');
  }

  if (!buffer.subarray(0, BINARY_MAGIC.length).equals(BINARY_MAGIC)) {
    throw new Error('bad binary frame magic');
  }

  const metadataLength = buffer.readUInt32LE(4);
  if (metadataLength < 1 || metadataLength > BINARY_METADATA_LIMIT) {
    throw new Error('invalid binary metadata length');
  }

  const metadataEnd = BINARY_HEADER_BYTES + metadataLength;
  if (metadataEnd > buffer.length) {
    throw new Error('truncated binary metadata');
  }

  const msg = JSON.parse(buffer.subarray(BINARY_HEADER_BYTES, metadataEnd).toString('utf8'));
  msg.binary = true;
  if (typeof msg.dataBytes !== 'number') {
    msg.dataBytes = buffer.length - metadataEnd;
  }

  return { msg, buffer };
}

function routeHostBinaryFrame(room, raw) {
  const { msg, buffer } = parseBinaryFrame(raw);
  if (!msg || msg.type !== 'map_frame') {
    recordOps('host_binary_rejected', summarizeMessage(msg, buffer.length));
    return;
  }

  recordFrame(msg, buffer.length);

  if (msg.target) {
    const dest = room.viewers.get(msg.target);
    sendWs(dest, buffer, { type: msg.type, target: msg.target });
    return;
  }

  for (const [, vws] of room.viewers) {
    sendWs(vws, buffer, { type: msg.type });
  }
}

function clearRoomReplayCache(room, reason) {
  if (room.viewerReplayCache.size > 0 || room.roomReplayCache.size > 0) {
    recordOps('replay_cache_clear', {
      room: room.roomId,
      reason,
      viewers: room.viewerReplayCache.size,
      roomLevel: room.roomReplayCache.size,
    });
  }
  room.viewerReplayCache.clear();
  room.roomReplayCache.clear();
}

function clearViewerReplayCache(room, login, reason) {
  const key = String(login || '');
  if (!key || !room) return false;
  const deleted = room.viewerReplayCache.delete(key);
  if (deleted) recordOps('replay_cache_viewer_clear', { room: room.roomId, username: key, reason });
  return deleted;
}

/** Every room a login has a cache in - used when a session expires relay-wide. */
function clearViewerReplayCacheEverywhere(login, reason) {
  for (const room of rooms.values()) clearViewerReplayCache(room, login, reason);
}

function clearViewerMapReplayCache(room, login, reason) {
  const key = String(login || '');
  if (!key) return false;
  const cache = room.viewerReplayCache.get(key);
  if (!cache) return false;
  const hadFull = cache.messages.delete('map_full');
  const hadDelta = cache.messages.delete('map_delta');
  const hadEntityKeyframe = cache.messages.delete('entity_keyframe');
  const hadEntityDelta = cache.messages.delete('entity_delta');
  const hadChunks = cache.mapChunks ? cache.mapChunks.size > 0 : false;
  if (cache.mapChunks) cache.mapChunks.clear();
  const hadMap = hadFull || hadDelta || hadEntityKeyframe || hadEntityDelta || hadChunks;
  if (hadMap) {
    cache.updatedAt = Date.now();
    recordOps('replay_cache_map_clear', { room: room.roomId, username: key, reason });
  }
  return hadMap;
}

function getViewerCache(room, login) {
  let cache = room.viewerReplayCache.get(login);
  if (!cache) {
    cache = {
      updatedAt: Date.now(),
      messages: new Map(),
      mapChunks: new Map(),
    };
    room.viewerReplayCache.set(login, cache);
  }
  return cache;
}

function cacheHostTextMessage(room, msg, text) {
  if (!msg || typeof text !== 'string') return;

  if (msg.type === 'map_transport' && msg.target) {
    clearViewerMapReplayCache(room, String(msg.target), 'map_transport_selected');
  }

  if (msg.type === 'host_capabilities' && msg.tacticalMap === false) {
    if (msg.target) clearViewerMapReplayCache(room, String(msg.target), 'tactical_map_disabled');
    else {
      for (const login of room.viewerReplayCache.keys()) {
        clearViewerMapReplayCache(room, login, 'tactical_map_disabled');
      }
    }
  }

  // Room-level broadcasts (no target) - keep latest for late joiners.
  if (!msg.target && REPLAYABLE_ROOM_TYPES.has(msg.type)) {
    room.roomReplayCache.set(msg.type, {
      text,
      type: msg.type,
      updatedAt: Date.now(),
    });
  }

  if (!msg.target) return;
  const target = String(msg.target);

  if (CLEAR_VIEWER_CACHE_TYPES.has(msg.type)) {
    clearViewerReplayCache(room, target, msg.type);
    return;
  }

  if (!REPLAYABLE_TARGETED_TYPES.has(msg.type)) return;

  const cache = getViewerCache(room, target);
  if (!cache.mapChunks) cache.mapChunks = new Map();
  if (msg.type === 'map_full') {
    cache.messages.delete('map_delta');
    cache.messages.delete('entity_keyframe');
    cache.messages.delete('entity_delta');
    cache.mapChunks.clear();
  }
  if (msg.type === 'map_chunk') {
    if (!isCacheableMapChunk(cache, msg, target)) return;
    const key = `${Number(msg.mapEpoch)}:${Number(msg.chunkX)}:${Number(msg.chunkZ)}`;
    cache.mapChunks.set(key, {
      text,
      type: msg.type,
      seq: Number.isFinite(Number(msg.chunkSeq)) ? Number(msg.chunkSeq) : null,
      mapEpoch: Number.isFinite(Number(msg.mapEpoch)) ? Number(msg.mapEpoch) : null,
      updatedAt: Date.now(),
    });
    cache.updatedAt = Date.now();
    return;
  }
  if (msg.type === 'map_delta' && !isCacheableMapDelta(cache, msg, target)) {
    return;
  }
  if ((msg.type === 'entity_keyframe' || msg.type === 'entity_delta') && !isCacheableEntityState(cache, msg, target)) {
    return;
  }
  cache.messages.set(msg.type, {
    text,
    type: msg.type,
    seq: Number.isFinite(Number(msg.seq)) ? Number(msg.seq) : null,
    mapEpoch: Number.isFinite(Number(msg.mapEpoch)) ? Number(msg.mapEpoch) : null,
    entitySeq: Number.isFinite(Number(msg.entitySeq)) ? Number(msg.entitySeq) : null,
    entityEpoch: Number.isFinite(Number(msg.entityEpoch)) ? Number(msg.entityEpoch) : null,
    updatedAt: Date.now(),
  });
  cache.updatedAt = Date.now();
}

function isCacheableMapDelta(cache, msg, target) {
  const full = cache.messages.get('map_full');
  if (!full) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'missing_map_full' });
    return false;
  }

  const msgEpoch = Number(msg.mapEpoch);
  if (Number.isFinite(msgEpoch) && Number.isFinite(full.mapEpoch) && msgEpoch !== full.mapEpoch) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'map_epoch_mismatch', mapEpoch: msgEpoch, cachedEpoch: full.mapEpoch });
    return false;
  }

  const msgBaseSeq = Number(msg.baseSeq);
  const cachedDelta = cache.messages.get('map_delta');
  const expectedBaseSeq = cachedDelta?.seq ?? full.seq;
  if (Number.isFinite(msgBaseSeq) && Number.isFinite(expectedBaseSeq) && msgBaseSeq !== expectedBaseSeq) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'base_seq_gap', baseSeq: msgBaseSeq, expectedBaseSeq });
    return false;
  }

  return true;
}

function isCacheableMapChunk(cache, msg, target) {
  const full = cache.messages.get('map_full');
  if (!full) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'missing_map_full' });
    return false;
  }
  const msgEpoch = Number(msg.mapEpoch);
  if (Number.isFinite(msgEpoch) && Number.isFinite(full.mapEpoch) && msgEpoch !== full.mapEpoch) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'map_epoch_mismatch', mapEpoch: msgEpoch, cachedEpoch: full.mapEpoch });
    return false;
  }
  if (!Number.isFinite(Number(msg.chunkX)) || !Number.isFinite(Number(msg.chunkZ))) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'invalid_chunk_key' });
    return false;
  }
  return true;
}

function isCacheableEntityState(cache, msg, target) {
  const full = cache.messages.get('map_full');
  if (!full) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'missing_map_full' });
    return false;
  }

  const msgEpoch = Number(msg.mapEpoch);
  if (Number.isFinite(msgEpoch) && Number.isFinite(full.mapEpoch) && msgEpoch !== full.mapEpoch) {
    recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'map_epoch_mismatch', mapEpoch: msgEpoch, cachedEpoch: full.mapEpoch });
    return false;
  }

  const msgEntityBaseSeq = Number(msg.entityBaseSeq);
  if (msg.type === 'entity_delta' && Number.isFinite(msgEntityBaseSeq)) {
    const cachedDelta = cache.messages.get('entity_delta');
    const cachedKeyframe = cache.messages.get('entity_keyframe');
    const expectedBaseSeq = cachedDelta?.entitySeq ?? cachedKeyframe?.entitySeq;
    if (Number.isFinite(expectedBaseSeq) && msgEntityBaseSeq !== expectedBaseSeq) {
      recordOps('replay_cache_skip', { username: target, type: msg.type, reason: 'entity_base_seq_gap', entityBaseSeq: msgEntityBaseSeq, expectedBaseSeq });
      return false;
    }
  }

  return true;
}

function wantedIncludes(msg, type) {
  const wanted = Array.isArray(msg?.wanted) ? msg.wanted.map(String) : [];
  if (!wanted.length) return true;
  if (wanted.includes(type)) return true;
  if (type === 'map_chunk' && wanted.includes('map_chunks')) return true;
  if (type === 'map_full' && (
    wanted.includes('map_delta') ||
    wanted.includes('map_chunks') ||
    wanted.includes('map_manifest') ||
    wanted.includes('entity_keyframe') ||
    wanted.includes('entity_delta')
  )) return true;
  if (type === 'entity_keyframe' && wanted.includes('entity_delta')) return true;
  return false;
}

function sendCachedReplay(ws, cached, type, login) {
  let payload = cached.text;
  try {
    const msg = JSON.parse(cached.text);
    msg.relayCached = true;
    msg.relayCachedAt = new Date().toISOString();
    payload = JSON.stringify(msg);
  } catch {}
  return sendWs(ws, payload, { type, target: login });
}

function replayRoomCachedState(room, ws, login, request = null) {
  if (!ws || ws.readyState !== WebSocket.OPEN || room.roomReplayCache.size === 0) return 0;
  let sent = 0;
  for (const type of ROOM_REPLAY_ORDER) {
    if (request && !wantedIncludes(request, type)) continue;
    const cached = room.roomReplayCache.get(type);
    if (!cached) continue;
    if (sendCachedReplay(ws, cached, type, login || 'room')) sent++;
  }
  return sent;
}

function replayCachedState(room, login, request) {
  const cache = room.viewerReplayCache.get(login);
  const ws = room.viewers.get(login);
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    recordOps('replay_cache_miss', { room: room.roomId, username: login, reason: 'viewer_unavailable' });
    return false;
  }

  let sent = 0;
  if (cache) {
    for (const type of REPLAY_ORDER) {
      if (!wantedIncludes(request, type)) continue;
      if (type === 'map_chunk') {
        const chunks = cache.mapChunks ? Array.from(cache.mapChunks.values()) : [];
        chunks.sort((a, b) => (a.seq || 0) - (b.seq || 0));
        for (const cachedChunk of chunks) {
          if (sendCachedReplay(ws, cachedChunk, type, login)) sent++;
        }
        continue;
      }
      const cached = cache.messages.get(type);
      if (!cached) continue;
      if (sendCachedReplay(ws, cached, type, login)) sent++;
    }
  }

  // Room-level game_info etc. - fill gaps for late joiners even without a viewer cache.
  sent += replayRoomCachedState(room, ws, login, request);

  recordOps(sent > 0 ? 'replay_cache_hit' : 'replay_cache_miss', {
    room: room.roomId,
    username: login,
    reason: request?.reason || '',
    wanted: Array.isArray(request?.wanted) ? request.wanted.join(',') : '',
    sent,
    cachedTypes: cache ? Array.from(cache.messages.keys()).join(',') : '',
    roomTypes: Array.from(room.roomReplayCache.keys()).join(','),
  });

  return sent > 0;
}

function serveViewerPage(res, roomId) {
  try {
    let html = fs.readFileSync(indexHtmlPath, 'utf8');
    html = html.replace(
      'data-twitch-client-id=""',
      `data-twitch-client-id="${TWITCH_CLIENT_ID}"`
    );
    html = html.replace(
      'data-allow-guest=""',
      `data-allow-guest="${GUEST_LOGIN ? 'true' : ''}"`
    );
    // A /g/<id> link arrives with the room already chosen, so someone handed a
    // link never sees a picker. Sanitised because it is written into an attribute.
    const safeRoom = String(roomId || '').replace(/[^a-zA-Z0-9_-]/g, '').slice(0, 32);
    html = html.replace('data-room=""', `data-room="${safeRoom}"`);
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Content-Type', 'text/html');
    res.send(html);
  } catch (e) {
    res.status(500).send('Server error');
  }
}

app.get('/', (_req, res) => serveViewerPage(res, ''));

app.get('/favicon.ico', (_req, res) => {
  res.status(204).end();
});

app.use(express.static(path.join(__dirname, 'public'), {
  setHeaders: (res, filePath) => {
    if (/\.(html|js|css)$/i.test(filePath)) {
      res.setHeader('Cache-Control', 'no-store');
    }
  },
}));

app.get('/health', (_req, res) => {
  res.json({
    ok:      true,
    instance: INSTANCE_ID,
    pid:     process.pid,
    uptime:  Math.round(process.uptime()),
    clientBuild: CLIENT_BUILD,
    viewers: totalViewers(),
    sessions: sessions.size,
    sessionTtlSeconds: Math.round(SESSION_TTL_MS / 1000),
    sessionSlideSeconds: Math.round(SESSION_SLIDE_MS / 1000),
    replayCacheViewers: totalReplayCacheViewers(),
    // `host` stays a boolean and keeps meaning "is a game connected", so every
    // existing checker - the mod's Test connection, release:verify, dashboards -
    // reads the same field it always did. It is now true if ANY room is live.
    host:    liveRoomCount() > 0,
    rooms:   liveRoomCount(),
    openHosting: OPEN_HOSTING,
    maxRooms: MAX_ROOMS,
    maxViewersPerRoom: MAX_VIEWERS,
    maxTotalViewers: MAX_TOTAL_VIEWERS,
    twitch:  !!TWITCH_CLIENT_ID,
    guest:   GUEST_LOGIN,
  });
});

function liveRoomCount() {
  let n = 0;
  for (const room of rooms.values()) if (roomIsLive(room)) n++;
  return n;
}

function totalViewers() {
  let n = 0;
  for (const room of rooms.values()) n += room.viewers.size;
  return n;
}

function totalReplayCacheViewers() {
  let n = 0;
  for (const room of rooms.values()) n += room.viewerReplayCache.size;
  return n;
}

app.get('/admin/logs', adminAuth, (req, res) => {
  const limit = Math.max(1, Math.min(parseInt(req.query.limit || '200', 10), OPS_LOG_LIMIT));
  res.json({ ok: true, logs: opsLog.slice(-limit) });
});

// ─── Twitch OAuth validation endpoint ────────────────────────────────────────
// Browser calls: POST /auth/twitch  { token: "<implicit grant access token>" }
// Returns:       { sessionToken, login, displayName }  or 401
app.use(express.json({ limit: '4kb' }));

app.post('/auth/twitch', async (req, res) => {
  const accessToken = req.body && req.body.token;
  if (!accessToken) return res.status(400).json({ error: 'Missing token' });

  if (!TWITCH_CLIENT_ID) {
    return res.status(503).json({ error: 'Twitch auth not configured on server' });
  }

  try {
    const identity = await validateTwitchToken(accessToken);
    if (!identity || !identity.login) {
      recordOps('auth_failed', { reason: 'invalid_twitch_token' });
      return res.status(401).json({ error: 'Invalid Twitch token' });
    }

    // Validate response has login but no display_name — fetch from Helix
    let displayName = identity.login;
    try {
      const user = await fetchTwitchUser(accessToken);
      if (user && user.display_name) displayName = user.display_name;
    } catch (e) {
      console.warn('[auth] Helix user fetch failed, using login:', e.message);
    }

    const session = createViewerSession(identity.login, displayName, SESSION_TTL_MS);

    recordOps('auth_ok', { username: identity.login, displayName, ttlSeconds: Math.round(SESSION_TTL_MS / 1000) });
    res.json(session);
  } catch (e) {
    console.error('[auth] Twitch validate error:', e.message);
    recordOps('auth_error', { error: e.message });
    res.status(500).json({ error: 'Auth server error' });
  }
});

// ─── Guest login ──────────────────────────────────────────────────────────────
// Browser calls: POST /auth/guest  { name: "someone" }
// Returns:       { sessionToken, login, displayName }  — the same shape /auth/twitch
// returns, so the viewer client treats the two identically from here on.
app.post('/auth/guest', (req, res) => {
  if (!GUEST_LOGIN) {
    return res.status(503).json({
      error: TWITCH_CLIENT_ID
        ? 'This relay uses Twitch login.'
        : 'Guest login is not enabled on this relay.',
    });
  }

  const raw = String((req.body && (req.body.name || req.body.username)) || '').trim();
  const login = raw.toLowerCase();
  if (!/^[a-z0-9_][a-z0-9_-]{0,23}$/.test(login)) {
    recordOps('auth_failed', { reason: 'invalid_guest_name' });
    return res.status(400).json({
      error: 'Use letters, numbers, _ or - (up to 24 characters, no spaces).',
    });
  }

  // Two people on the same name would silently evict each other on the viewer
  // socket ("Reconnected from another tab"), which reads as a broken relay.
  // Checked across every room: the same login cannot be two people anywhere here.
  const existing = findViewerSocketAnywhere(login);
  if (existing && existing.readyState === WebSocket.OPEN) {
    recordOps('auth_failed', { reason: 'guest_name_taken', username: login });
    return res.status(409).json({ error: 'Someone here is already using that name.' });
  }

  const session = createViewerSession(login, raw.slice(0, 24) || login, SESSION_TTL_MS);
  recordOps('auth_ok', { username: login, displayName: session.displayName, guest: true });
  res.json(session);
});

function validateTwitchToken(token) {
  return new Promise((resolve, reject) => {
    const options = {
      hostname: 'id.twitch.tv',
      path:     '/oauth2/validate',
      headers:  { Authorization: `OAuth ${token}` },
    };
    const req = https.get(options, (res) => {
      let data = '';
      res.on('data', d => data += d);
      res.on('end', () => {
        if (res.statusCode !== 200) return resolve(null);
        try { resolve(JSON.parse(data)); }
        catch { resolve(null); }
      });
    });
    req.on('error', reject);
    req.setTimeout(5000, () => { req.destroy(); reject(new Error('timeout')); });
  });
}

function fetchTwitchUser(token) {
  return new Promise((resolve, reject) => {
    const options = {
      hostname: 'api.twitch.tv',
      path:     '/helix/users',
      headers:  {
        Authorization:  `Bearer ${token}`,
        'Client-Id':    TWITCH_CLIENT_ID,
      },
    };
    const req = https.get(options, (res) => {
      let data = '';
      res.on('data', d => data += d);
      res.on('end', () => {
        if (res.statusCode !== 200) return resolve(null);
        try {
          const body = JSON.parse(data);
          resolve(body.data && body.data[0] ? body.data[0] : null);
        } catch { resolve(null); }
      });
    });
    req.on('error', reject);
    req.setTimeout(5000, () => { req.destroy(); reject(new Error('timeout')); });
  });
}

// ─── Session lookup helper ────────────────────────────────────────────────────
function touchSession(token, s, now = Date.now()) {
  if (!token || !s) return s;
  // Sliding expiry: active viewers keep their session alive without re-login.
  const nextExp = now + SESSION_SLIDE_MS;
  if (!s.exp || nextExp > s.exp) {
    s.exp = Math.min(nextExp, now + SESSION_TTL_MS);
  }
  return s;
}

function resolveSession(token) {
  if (!token) return null;
  const s = sessions.get(token);
  if (!s) return null;
  const now = Date.now();
  if (now > s.exp) {
    sessions.delete(token);
    if (!hasActiveSessionForLogin(s.login, now)) {
      clearViewerReplayCache(s.login, 'session_expired');
    }
    return null;
  }
  return touchSession(token, s, now);
}

function hasActiveSessionForLogin(login, now = Date.now()) {
  for (const session of sessions.values()) {
    if (session.login === login && now <= session.exp) return true;
  }
  return false;
}

function createViewerSession(login, displayName, ttlMs) {
  const sessionToken = crypto.randomBytes(24).toString('hex');
  const lifetime = Math.max(60 * 1000, Math.min(ttlMs || SESSION_TTL_MS, SESSION_TTL_MS));
  const exp = Date.now() + lifetime;
  const identity = {
    login,
    displayName: displayName || login,
    exp,
  };
  sessions.set(sessionToken, identity);
  return {
    sessionToken,
    login: identity.login,
    displayName: identity.displayName,
    expiresAt: new Date(exp).toISOString(),
    ttlSeconds: Math.round(lifetime / 1000),
  };
}

// ─── WebSocket server ─────────────────────────────────────────────────────────
// maxPayload is NOT set by default — ws 8.x allows 100 MiB per frame, and
// express.json({ limit: '4kb' }) covers only HTTP, not this socket. Every accepted
// viewer message is JSON.parsed, re-stringified to measure it, stringified again by
// recordOps, written to disk, broadcast to admins, and stringified a third time on
// the way to the host — so one oversized frame is amplified several times before it
// reaches the game. 1 MiB is far above any legitimate message (host->viewer map
// frames travel on this SAME socket path with ?role=host, so the cap must clear the
// largest legitimate map frame — observed max 149,728 bytes at a 700k-pixel budget,
// and a solo viewer's 2.1M-pixel budget is ~3x that. 4 MiB leaves an order of
// magnitude of headroom over the real traffic while cutting the abuse ceiling by 25x.
const wss = new WebSocketServer({ server, path: '/ws', maxPayload: 4 * 1024 * 1024 });

wss.on('connection', (ws, req) => {
  const url     = new URL(req.url, 'http://localhost');
  const role    = url.searchParams.get('role');
  const secret  = url.searchParams.get('secret');
  const hostKeyParam = url.searchParams.get('key');
  const roomParam = String(url.searchParams.get('room') || '').trim();
  const sToken  = url.searchParams.get('session');
  const clientBuild = String(url.searchParams.get('build') || '').slice(0, 80);
  const mapTransport = normalizeMapTransport(url.searchParams.get('mapTransport'));

  // ── Host ──────────────────────────────────────────────────────────────────
  if (role === 'host') {
    // The credential IS the room. A game presents either the key the relay issued
    // it (?key=) or, for the relay owner's own game, HOST_SECRET (?secret=) - which
    // is what the already-shipped mod DLL sends, so it keeps working with no update.
    // Rooms are never addressed by name on this path, so there is nothing to squat:
    // you cannot claim a room without holding its key.
    const presented = hostKeyParam || secret;
    const room = findRoomByHostKey(presented);
    if (!room) {
      ws.close(4001, 'Unauthorized');
      console.log(presented
        ? '[relay] Host rejected: unknown key'
        : '[relay] Host rejected: no key or secret presented');
      recordOps('host_rejected', {
        reason: presented ? 'unknown_key' : 'no_credential',
        openHosting: OPEN_HOSTING,
      });
      return;
    }

    // If the game named a room as well as presenting a key, the two must agree.
    // The key alone already decides which room this is, so a mismatch means the
    // caller believes it is somewhere it is not - refuse rather than silently put
    // it somewhere else and let it stream into a stranger's room.
    if (roomParam && roomParam !== room.roomId) {
      ws.close(4001, 'Unauthorized');
      recordOps('host_rejected', { reason: 'room_key_mismatch', requested: roomParam, actual: room.roomId });
      return;
    }

    // Only ever evicts the SAME game reconnecting (a RimWorld restart, a dropped
    // socket). A different streamer holds a different key and therefore a different
    // room, so one game can no longer throw another off the relay.
    if (room.hostSocket && room.hostSocket.readyState === WebSocket.OPEN) {
      room.hostSocket.close(4002, 'Replaced by new host');
    }

    room.hostSocket = ws;
    room.hostConnectedAt = Date.now();
    room.lastActiveAt = Date.now();
    clearRoomReplayCache(room, 'host_connected');
    console.log(`[relay] Host connected: room ${room.roomId}`);
    recordOps('host_connected', { room: room.roomId });
    broadcastToAdmins({ type: 'host_status', connected: true, room: room.roomId });
    const hostConnectedNotice = JSON.stringify({ type: 'host_connected', instance: INSTANCE_ID });
    for (const [, vws] of room.viewers) {
      sendWs(vws, hostConnectedNotice, { type: 'host_connected' });
    }

    // Send existing viewers to the newly connected host so quickloads/reconnects rebuild host state.
    for (const viewer of getViewerList(room)) {
      sendWs(ws, JSON.stringify({
        type: 'viewer_joined',
        username: viewer.login,
        displayName: viewer.displayName || viewer.login,
        mapTransport: viewer.mapTransport,
      }), { type: 'viewer_joined', target: 'host' });
    }

    ws.on('message', (raw, isBinary) => {
      try {
        room.lastActiveAt = Date.now();
        if (isBinary) {
          routeHostBinaryFrame(room, raw);
          return;
        }

        const text = raw.toString('utf8');
        const msg  = JSON.parse(text);
        const summary = summarizeMessage(msg, Buffer.byteLength(text));
        if (msg.type === 'map_frame') recordFrame(msg, summary.bytes);
        else recordOps('host_message', summary);
        cacheHostTextMessage(room, msg, text);

        if (msg.target) {
          const login = msg.target;
          if (msg.type === 'context_menu' || (msg.type === 'action_result' && msg.action === 'context_menu')) {
            completeContextMenuRequest(room, login);
          }
          // Kick/ban must arrive immediately and close the socket - bypass batch queue
          if (msg.type === 'viewer_kick' || msg.type === 'banned') {
            const dest = room.viewers.get(login);
            sendWs(dest, text, { type: msg.type, target: login });
            if (dest && dest.readyState === WebSocket.OPEN) {
              setTimeout(() => {
                try { dest.close(4007, msg.type === 'banned' ? 'Banned by streamer' : 'Kicked by streamer'); } catch (e) {}
              }, 50);
            }
            recordOps('host_moderation', { room: room.roomId, type: msg.type, target: login });
            return;
          }
          // All other targeted messages are batched into a 16ms flush window
          queueForViewer(room, login, text);
          return;
        }

        if (msg.adminOnly) {
          broadcastToAdmins(msg);
          return;
        }

        for (const [, vws] of room.viewers) {
          sendWs(vws, text, { type: msg.type });
        }
        if (msg.type === 'map_frame') return;
        broadcastToAdmins(msg);
      } catch (e) {
        console.error('[relay] Bad host message:', e.message);
      }
    });

    ws.on('close', () => {
      if (room.hostSocket === ws) {
        room.hostSocket = null;
        room.lastActiveAt = Date.now();
        for (const login of Array.from(room.contextMenuRequests.keys())) clearContextMenuRequest(room, login);
        console.log(`[relay] Host disconnected: room ${room.roomId}`);
        recordOps('host_disconnected', { room: room.roomId });
        const notice = JSON.stringify({ type: 'host_disconnected' });
        for (const [, vws] of room.viewers) {
          sendWs(vws, notice, { type: 'host_disconnected' });
        }
        broadcastToAdmins({ type: 'host_status', connected: false, room: room.roomId });
      }
    });

    ws.on('error', (err) => console.error('[relay] Host error:', err.message));
    return;
  }

  // ── Viewer ────────────────────────────────────────────────────────────────
  // -- Viewer ------------------------------------------------------------
  if (role === 'viewer') {
    const identity = resolveSession(sToken);
    if (!identity) {
      recordOps('viewer_rejected', { reason: 'invalid_session' });
      ws.close(4003, 'Invalid or missing session token');
      return;
    }

    const room = resolveViewerRoom(roomParam);
    if (!room) {
      // No room named and none to fall back on, or a room id that no longer exists.
      // Hand back the directory so the page can show what IS live rather than an
      // opaque failure, then close - the client reconnects with a ?room= choice.
      const live = listLiveRooms();
      recordOps('viewer_room_choice', { requested: roomParam || '(none)', live: live.length });
      sendWs(ws, JSON.stringify({ type: 'room_choice', rooms: live, requested: roomParam || '' }),
        { type: 'room_choice' });
      setTimeout(() => { try { ws.close(4009, 'Choose a game'); } catch (_) {} }, 120);
      return;
    }

    const { login, displayName } = identity;
    const prev = room.viewers.get(login);
    if (!prev && totalViewers() >= MAX_TOTAL_VIEWERS) {
      recordOps('viewer_rejected', { room: room.roomId, username: login, reason: 'relay_full', viewers: totalViewers() });
      ws.close(4004, 'Relay full');
      return;
    }
    if (!prev && room.viewers.size >= MAX_VIEWERS) {
      recordOps('viewer_rejected', { room: room.roomId, username: login, reason: 'server_full', viewers: room.viewers.size });
      ws.close(4004, 'Server full');
      return;
    }

    if (prev && prev.readyState === WebSocket.OPEN) {
      prev.close(4005, 'Reconnected from another tab');
    }
    // The same person switching rooms must not leave a socket behind in the old one.
    for (const other of rooms.values()) {
      if (other === room) continue;
      const stale = other.viewers.get(login);
      if (stale) {
        try { stale.close(4005, 'Joined another game'); } catch (_) {}
        other.viewers.delete(login);
        other.viewerInfo.delete(login);
        clearContextMenuRequest(other, login);
        sendToHost(other, { type: 'viewer_left', username: login });
      }
    }
    clearContextMenuRequest(room, login);

    room.viewers.set(login, ws);
    room.lastActiveAt = Date.now();
    room.viewerInfo.set(login, { login, displayName: displayName || login, connectedAt: Date.now(), clientBuild, mapTransport });
    console.log(`[relay] Viewer joined: ${displayName} (${login}) -> room ${room.roomId}`);
    recordOps('viewer_joined', { room: room.roomId, username: login, displayName, clientBuild });
    sendToHost(room, { type: 'viewer_joined', username: login, displayName, mapTransport });
    broadcastToAdmins({ type: 'viewer_update', action: 'joined', login, displayName, room: room.roomId });
    suggestClientReload(ws, login, clientBuild);
    sendWs(ws, JSON.stringify(Object.assign({}, RELAY_CAPABILITIES, {
      room: room.roomId,
      roomLabel: roomDisplayLabel(room),
    })), { type: 'relay_capabilities', target: login });
    if (room.hostSocket && room.hostSocket.readyState === WebSocket.OPEN) {
      sendWs(ws, JSON.stringify({ type: 'host_connected', instance: INSTANCE_ID }), { type: 'host_connected', target: login });
      // Immediately replay room-level state (game_info) so the pill isn't stuck on "Host waiting".
      replayRoomCachedState(room, ws, login);
    } else {
      // This else was missing, and its absence is the whole "I can't see his game"
      // experience: host_connected was the ONLY signal about the game's existence and
      // host_disconnected only fires for a host that had already connected. A viewer
      // arriving at a relay with no game got neither, so the page showed a green
      // "Connected" pill and "Waiting for colonists…" indefinitely. That pill was
      // always about the viewer's own socket to the relay, never about the game.
      sendWs(ws, JSON.stringify({
        type: 'host_absent',
        instance: INSTANCE_ID,
        rooms: listLiveRooms(),
      }), { type: 'host_absent', target: login });
    }
    if (room.viewerReplayCache.has(login)) {
      replayCachedState(room, login, { type: 'state_resync_request', reason: 'viewer_reconnected' });
    }

    // Per-viewer send budget: ~20 msg/s sustained, burst 40. Every accepted message
    // costs the HOST real main-thread work — a pawn-state serialize, an armory scan,
    // a colonist-list rebuild — and the relay itself stringifies each one three times
    // and writes it to disk. The host has its own per-type floors, but those run
    // AFTER the message has already been parsed, logged and forwarded, so the cheap
    // ceiling belongs here, in front of the game. Silent drop: telling a flooding
    // client about each rejection would double the traffic.
    let sendTokens = 40;
    let lastRefill = Date.now();
    let throttleReported = 0;

    ws.on('message', (raw) => {
      try {
        const nowMs = Date.now();
        sendTokens = Math.min(40, sendTokens + ((nowMs - lastRefill) / 1000) * 20);
        lastRefill = nowMs;
        if (sendTokens < 1) {
          // Report at most once per 10s per viewer so the log itself cannot be a flood.
          if (nowMs - throttleReported > 10000) {
            throttleReported = nowMs;
            recordOps('viewer_throttled', { username: login });
            console.warn(`[relay] Throttling ${login}: over 20 msg/s`);
          }
          return;
        }
        sendTokens -= 1;

        // Keep sliding TTL alive while the viewer is actively sending.
        resolveSession(sToken);
        const text = raw.toString('utf8');
        const msg  = JSON.parse(text);
        const allowedViewerTypes = new Set(['command', 'request_state', 'state_resync_request', 'request_armory', 'request_icons', 'request_roster', 'map_transport', 'chat', 'request_colonist_list']);
        if (!allowedViewerTypes.has(msg.type)) {
          console.warn(`[relay] Rejected viewer message type from ${login}: ${msg.type}`);
          recordOps('viewer_message_rejected', { username: login, type: msg.type });
          return;
        }
        // SECURITY — key ORDER is load-bearing here, not just key value.
        // The host reads these three fields with a LAST-occurrence scan over the raw
        // JSON text (JsonHelper.ExtractLastString), which does not track brace depth,
        // so a key nested inside a viewer-supplied sub-object is a valid match. In JS,
        // assigning to an EXISTING key overwrites in place and does NOT move it to the
        // end — so a viewer who sent {"username":"a","source":"a","adminCommand":false,
        // ...,"x":{"source":"admin","adminCommand":true}} kept our pinned values at the
        // front while their nested copies sat last and won the LastIndexOf. That gave
        // any viewer admin: ban/timeout other viewers, spawn unlimited colonists into
        // the save, grant tickets, and act under another viewer's name.
        // delete-then-set forces ours to be the FINAL three keys serialized.
        delete msg.username;
        delete msg.source;
        delete msg.adminCommand;
        msg.username = login;
        msg.source = 'viewer';
        msg.adminCommand = false;
        recordOps('viewer_message', summarizeMessage(msg, Buffer.byteLength(JSON.stringify(msg))));
        if (msg.type === 'state_resync_request' && replayCachedState(login, msg)) {
          return;
        }
        if (msg.type === 'map_transport') {
          msg.transport = normalizeMapTransport(msg.transport);
          const info = room.viewerInfo.get(login);
          if (info) info.mapTransport = msg.transport;
          if (msg.transport === 'jpeg') clearViewerMapReplayCache(login, 'viewer_selected_jpeg');
        }
        if (msg.type === 'command' && msg.action === 'context_menu') {
          queueContextMenuRequest(room, login, msg);
          return;
        }
        sendToHost(room, msg);
      } catch (e) {
        console.error(`[relay] Bad viewer message from ${login}:`, e.message);
      }
    });

    ws.on('close', () => {
      if (room.viewers.get(login) === ws) {
        room.viewers.delete(login);
        room.viewerInfo.delete(login);
        room.lastActiveAt = Date.now();
        clearContextMenuRequest(room, login);
        console.log(`[relay] Viewer left: ${login} (room ${room.roomId})`);
        recordOps('viewer_left', { room: room.roomId, username: login });
        sendToHost(room, { type: 'viewer_left', username: login });
        broadcastToAdmins({ type: 'viewer_update', action: 'left', login, room: room.roomId });
      }
    });

    ws.on('error', (err) => console.error(`[relay] Viewer error (${login}):`, err.message));
    return;
  }

  // ── Admin (receives all host broadcasts, read-only) ──────────────────────
  if (role === 'admin') {
    if (!HOST_SECRET || secret !== HOST_SECRET) {
      ws.close(4001, 'Unauthorized');
      return;
    }

    console.log('[relay] Admin connected');
    recordOps('admin_connected');

    const adminKey = '__admin__' + Date.now() + '_' + Math.random().toString(16).slice(2);
    ws._isAdmin = true;
    admins.set(adminKey, ws);

    // The admin console is the RELAY OPERATOR's view, so it defaults to the owner
    // room (what it always showed) and can be pointed at any room with ?room=.
    const adminRoom = resolveAdminRoom(roomParam);
    sendWs(ws, JSON.stringify({
      type: 'admin_sync',
      room: adminRoom ? adminRoom.roomId : '',
      host: roomIsLive(adminRoom),
      viewers: getViewerList(adminRoom),
      rooms: listLiveRooms(),
      sessions: sessions.size,
      uptime: process.uptime(),
      instance: INSTANCE_ID,
      clientBuild: CLIENT_BUILD,
      replayCacheViewers: adminRoom ? adminRoom.viewerReplayCache.size : 0,
    }), { type: 'admin_sync', target: 'admin' });
    if (adminRoom) sendToHost(adminRoom, { type: 'request_colonist_list', source: 'admin', adminCommand: true });

    ws.on('close', () => {
      admins.delete(adminKey);
      console.log('[relay] Admin disconnected');
      recordOps('admin_disconnected');
    });

    ws.on('error', () => {});
    return;
  }

  ws.close(4000, 'Unknown role');
});

// ─── Helpers ──────────────────────────────────────────────────────────────────
function sendToHost(room, msg) {
  if (!room || !room.hostSocket || room.hostSocket.readyState !== WebSocket.OPEN) {
    const summary = typeof msg === 'string'
      ? { type: 'raw', bytes: Buffer.byteLength(msg) }
      : summarizeMessage(msg, Buffer.byteLength(JSON.stringify(msg || {})));
    summary.room = room ? room.roomId : '(none)';
    recordOps('host_message_dropped', summary);
    return;
  }
  try {
    const type = typeof msg === 'string' ? '' : msg && msg.type;
    sendWs(room.hostSocket, typeof msg === 'string' ? msg : JSON.stringify(msg), { type, target: 'host' });
  } catch (e) {
    console.error('[relay] Failed to send to host:', e.message);
  }
}

function findViewerSocketAnywhere(login) {
  for (const room of rooms.values()) {
    const ws = room.viewers.get(login);
    if (ws) return ws;
  }
  return null;
}

function suggestClientReload(ws, login, clientBuild, delayMs = 800) {
  if (!clientBuild || CLIENT_BUILD === 'unknown' || clientBuild === CLIENT_BUILD) return false;
  const payload = JSON.stringify({
    type: 'client_reload',
    build: CLIENT_BUILD,
    delayMs,
    message: 'Viewer update available. Reloading...'
  });
  const sent = sendWs(ws, payload, { type: 'client_reload', target: login });
  if (sent) recordOps('client_reload_suggested', { username: login, from: clientBuild, to: CLIENT_BUILD });
  return sent;
}

function getViewerList(room) {
  const list = [];
  if (!room) return list;
  for (const [login, ws] of room.viewers) {
    const info = room.viewerInfo.get(login) || { login, displayName: login, connectedAt: 0 };
    list.push({
      login,
      displayName: info.displayName || login,
      connected: ws.readyState === WebSocket.OPEN,
      connectedAt: info.connectedAt || 0,
      clientBuild: info.clientBuild || '',
      mapTransport: info.mapTransport || 'auto',
    });
  }
  list.sort((a, b) => a.login.localeCompare(b.login));
  return list;
}

function describeReplayCacheEntry(room, login, cache, now = Date.now()) {
  const messages = [];
  for (const [type, cached] of cache.messages) {
    messages.push({
      type,
      ageMs: cached.updatedAt ? now - cached.updatedAt : null,
      mapEpoch: cached.mapEpoch,
      seq: cached.seq,
      entityEpoch: cached.entityEpoch,
      entitySeq: cached.entitySeq,
      bytes: typeof cached.text === 'string' ? Buffer.byteLength(cached.text) : 0,
    });
  }
  if (cache.mapChunks) {
    for (const cached of cache.mapChunks.values()) {
      messages.push({
        type: 'map_chunk',
        ageMs: cached.updatedAt ? now - cached.updatedAt : null,
        mapEpoch: cached.mapEpoch,
        seq: cached.seq,
        bytes: typeof cached.text === 'string' ? Buffer.byteLength(cached.text) : 0,
      });
    }
  }
  messages.sort((a, b) => REPLAY_ORDER.indexOf(a.type) - REPLAY_ORDER.indexOf(b.type));
  return {
    login,
    ageMs: cache.updatedAt ? now - cache.updatedAt : null,
    connected: room.viewers.has(login),
    messageCount: messages.length,
    mapChunkCount: cache.mapChunks ? cache.mapChunks.size : 0,
    messages,
  };
}

// -- Room resolution -------------------------------------------------------

/**
 * Which game a viewer socket belongs to.
 *  - an explicit ?room= wins, and an id that does not exist is NOT silently
 *    redirected somewhere else - the viewer gets the directory instead, because
 *    quietly putting someone in a different streamer's game is worse than an error.
 *  - with no ?room=, a single live game is chosen automatically. That is what
 *    keeps a one-streamer relay behaving exactly as it did before rooms existed.
 *  - with no ?room= and several live games, there is nothing to guess: return null
 *    and let the caller hand back the directory.
 */
function resolveViewerRoom(roomParam) {
  if (roomParam) {
    const room = rooms.get(roomParam);
    return room || null;
  }
  const live = [];
  for (const room of rooms.values()) if (roomIsLive(room)) live.push(room);
  if (live.length === 1) return live[0];
  if (live.length === 0) {
    // Nothing is hosting. Fall back to the owner room so the viewer still lands
    // somewhere and receives host_absent, which is a readable state; a relay with
    // no owner room and no live games has genuinely nothing to show.
    return rooms.get(OWNER_ROOM_ID) || null;
  }
  return null;
}

/** The relay operator's console: owner room by default, any room via ?room=. */
function resolveAdminRoom(roomParam) {
  if (roomParam && rooms.has(roomParam)) return rooms.get(roomParam);
  const owner = rooms.get(OWNER_ROOM_ID);
  if (owner) return owner;
  for (const room of rooms.values()) if (roomIsLive(room)) return room;
  return rooms.values().next().value || null;
}

function reapIdleRooms() {
  const now = Date.now();
  for (const [roomId, room] of Array.from(rooms.entries())) {
    if (room.owner) continue;                       // the operator's own room is permanent
    if (roomIsLive(room)) continue;
    if (room.viewers.size > 0) continue;
    // NEVER reap a room that has hosted. Its streamer's link stays valid for good,
    // whether they are away for ten minutes or a month.
    if (room.hostConnectedAt !== 0) continue;
    if (now - room.lastActiveAt < ROOM_UNCLAIMED_MS) continue;
    for (const login of Array.from(room.contextMenuRequests.keys())) clearContextMenuRequest(room, login);
    rooms.delete(roomId);
    recordOps('room_reaped', { room: roomId, idleMs: now - room.lastActiveAt, reason: 'never_hosted' });
    persistRooms();
  }
}
setInterval(reapIdleRooms, 60 * 1000);

// -- Room registry persistence --------------------------------------------
// Best effort only. A relay restart on a machine with no volume loses this, and
// that is survivable: the mod re-registers automatically when its key is refused,
// so a streamer sees a reconnect rather than a dead end.
function persistRooms() {
  try {
    const out = [];
    for (const room of rooms.values()) {
      if (room.owner) continue;
      // A room that never had a game in it is not worth surviving a restart; keeping
      // them is what let abandoned Join attempts accumulate across reboots.
      if (room.hostConnectedAt === 0) continue;
      out.push({
        roomId: room.roomId,
        hostKey: room.hostKey,
        label: room.label,
        createdAt: room.createdAt,
        lastActiveAt: room.lastActiveAt,
      });
    }
    fs.mkdirSync(path.dirname(ROOMS_FILE), { recursive: true });
    fs.writeFileSync(ROOMS_FILE, JSON.stringify(out), 'utf8');
  } catch (_) {}
}

function restoreRooms() {
  try {
    if (!fs.existsSync(ROOMS_FILE)) return;
    const saved = JSON.parse(fs.readFileSync(ROOMS_FILE, 'utf8'));
    if (!Array.isArray(saved)) return;
    let restored = 0;
    for (const entry of saved) {
      if (!entry || !entry.roomId || !entry.hostKey) continue;
      if (rooms.has(entry.roomId)) continue;
      if (rooms.size >= MAX_ROOMS) break;
      const room = makeRoom(entry.roomId, {
        hostKey: entry.hostKey,
        label: entry.label || '',
        createdAt: entry.createdAt || Date.now(),
      });
      // Carry the real idle clock across the restart. Resetting it to now (which is
      // what makeRoom does) meant a relay that restarts often never reaps anything.
      if (entry.lastActiveAt) room.lastActiveAt = entry.lastActiveAt;
      // It hosted before the restart, so it is a returning streamer, not an
      // abandoned registration - the long idle grace applies.
      room.hostConnectedAt = entry.lastActiveAt || entry.createdAt || Date.now();
      restored++;
    }
    if (restored) console.log(`[relay] Restored ${restored} room(s) from ${ROOMS_FILE}`);
  } catch (e) {
    console.warn('[relay] Could not restore rooms:', e.message);
  }
}
restoreRooms();

// -- Public room directory -------------------------------------------------
app.get('/api/rooms', (_req, res) => {
  res.json({
    ok: true,
    instance: INSTANCE_ID,
    openHosting: OPEN_HOSTING,
    inviteRequired: OPEN_HOSTING && !!HOST_INVITE_CODE,
    maxRooms: MAX_ROOMS,
    rooms: listLiveRooms(),
  });
});

// -- Self-serve hosting ----------------------------------------------------
// The whole point: a streamer who is not the relay operator should never see an
// env var, a secret, or a deploy button. The mod calls this once, is handed a
// room and a key, stores them itself, and connects. Nothing is typed by hand.
const registerHits = new Map(); // ip -> {count, windowStart}
// Anti-abuse only: enough headroom that a person retrying a typo, or a household
// on one IP, never meets it.
const REGISTER_WINDOW_MS = 10 * 60 * 1000;
const REGISTER_MAX_PER_WINDOW = Math.max(1, parseInt(process.env.REGISTER_MAX_PER_WINDOW || '30', 10));

function registerRateLimited(ip) {
  const now = Date.now();
  const hit = registerHits.get(ip);
  if (!hit || now - hit.windowStart > REGISTER_WINDOW_MS) {
    registerHits.set(ip, { count: 1, windowStart: now });
    return false;
  }
  hit.count++;
  return hit.count > REGISTER_MAX_PER_WINDOW;
}

function newRoomId() {
  // Short, unambiguous, URL-safe. No vowels, so it cannot spell anything.
  const alphabet = '23456789bcdfghjkmnpqrstvwxz';
  let id = '';
  const bytes = crypto.randomBytes(8);
  for (const b of bytes) id += alphabet[b % alphabet.length];
  return rooms.has(id) ? newRoomId() : id;
}

app.post('/api/host/register', (req, res) => {
  if (!OPEN_HOSTING) {
    return res.status(403).json({
      error: 'This relay does not accept other hosts.',
      detail: 'The person who runs it can enable OPEN_HOSTING=1.',
    });
  }

  const ip = String(req.headers['fly-client-ip'] || req.ip || req.socket.remoteAddress || '');
  if (registerRateLimited(ip)) {
    recordOps('host_register_rate_limited', { ip });
    return res.status(429).json({ error: 'Too many attempts. Wait a few minutes.' });
  }

  if (HOST_INVITE_CODE) {
    const given = String((req.body && req.body.invite) || '').trim();
    if (!given || !timingSafeEqualStr(HOST_INVITE_CODE, given)) {
      recordOps('host_register_bad_invite', { ip });
      return res.status(403).json({ error: 'That invite code is not right.' });
    }
  }

  // Sweep first. Otherwise the relay reports "full" while most of the slots are
  // held by rooms nobody ever hosted in, which is how it filled up in testing.
  reapIdleRooms();
  if (rooms.size >= MAX_ROOMS) {
    recordOps('host_register_full', { rooms: rooms.size });
    return res.status(503).json({
      error: 'This relay is full.',
      detail: `It is set to allow ${MAX_ROOMS} games at once.`,
    });
  }

  const label = String((req.body && req.body.label) || '').trim().slice(0, 48);
  const roomId = newRoomId();
  const hostKey = crypto.randomBytes(24).toString('base64url');
  makeRoom(roomId, { hostKey, label });
  persistRooms();
  recordOps('host_registered', { room: roomId, label, ip });

  res.json({
    ok: true,
    roomId,
    hostKey,
    label,
    // Handed back ready to paste into chat - the streamer never assembles a URL.
    viewerPath: `/g/${roomId}`,
  });
});

// A game whose key was refused (relay restarted without a volume, room reaped)
// re-registers with this so the streamer sees a reconnect instead of a dead end.
app.post('/api/host/reclaim', (req, res) => {
  const key = String((req.body && req.body.hostKey) || '');
  const existing = findRoomByHostKey(key);
  if (existing) {
    return res.json({ ok: true, roomId: existing.roomId, hostKey: existing.hostKey, viewerPath: `/g/${existing.roomId}` });
  }
  res.status(404).json({ error: 'That key is not known to this relay.' });
});

// -- Pretty viewer link ----------------------------------------------------
app.get('/g/:roomId', (req, res) => {
  const roomId = String(req.params.roomId || '').trim();
  // Only room-shaped ids. Without this, ANY path under /g/ returned the viewer HTML
  // - including /g/app.js, which is what a relative <script src="app.js"> on a
  // /g/<id> page asks for. The browser then parsed HTML as JavaScript
  // ("Unexpected token '<'") and the entire client failed to start. Assets are
  // absolute now; this makes the wrong request a plain 404 instead of a booby trap.
  if (!/^[a-z0-9_-]{1,32}$/i.test(roomId)) {
    return res.status(404).send('Not found');
  }
  serveViewerPage(res, roomId);
});

// ─── Admin API (protected by HOST_SECRET) ─────────────────────────────────────
function adminAuth(req, res, next) {
  // FAIL CLOSED. This previously read `if (!HOST_SECRET || auth === ...)`, so an
  // unset HOST_SECRET (the default is '') made the first disjunct true and left
  // EVERY admin route unauthenticated: /admin/logs, /admin/status, /admin/cache,
  // /admin/viewer-session (mints viewer sessions), /admin/kick, /admin/message,
  // /admin/reload and /admin/host-command. The admin WebSocket role already fails
  // closed, so the two disagreed — that makes this a defect, not a design choice.
  // A relay with no secret must refuse admin access, not hand it to anyone.
  const auth = req.headers.authorization;
  if (HOST_SECRET && auth === `Bearer ${HOST_SECRET}`) return next();
  res.status(401).json({ error: 'Unauthorized' });
}

app.get('/admin/status', adminAuth, (req, res) => {
  const room = resolveAdminRoom(String(req.query.room || '').trim());
  res.json({
    instance: INSTANCE_ID,
    pid:      process.pid,
    room:     room ? room.roomId : '',
    host:     roomIsLive(room),
    clientBuild: CLIENT_BUILD,
    viewers:  getViewerList(room),
    rooms:    listLiveRooms(),
    replayCacheViewers: room ? room.viewerReplayCache.size : 0,
    sessions: sessions.size,
    uptime:   Math.round(process.uptime()),
  });
});

function sendReplayCacheSummary(room, res) {
  const now = Date.now();
  res.json({
    ok: true,
    instance: INSTANCE_ID,
    room: room ? room.roomId : '',
    host: roomIsLive(room),
    replayCacheViewers: room ? room.viewerReplayCache.size : 0,
    viewers: room ? Array.from(room.viewerReplayCache.entries())
      .map(([login, cache]) => describeReplayCacheEntry(room, login, cache, now))
      .sort((a, b) => a.login.localeCompare(b.login)) : [],
  });
}

app.get('/admin/cache', adminAuth, (req, res) => {
  sendReplayCacheSummary(resolveAdminRoom(String(req.query.room || '').trim()), res);
});

app.get('/admin/replay-cache', adminAuth, (req, res) => {
  sendReplayCacheSummary(resolveAdminRoom(String(req.query.room || '').trim()), res);
});

app.post('/admin/viewer-session', adminAuth, (req, res) => {
  const rawLogin = String(req.body?.login || req.body?.username || '').trim();
  const login = rawLogin.toLowerCase();
  if (!/^[a-z0-9_][a-z0-9_-]{0,31}$/.test(login)) {
    return res.status(400).json({ error: 'Invalid login' });
  }

  const rawDisplay = String(req.body?.displayName || rawLogin || login).trim();
  const displayName = rawDisplay.slice(0, 32) || login;
  const ttlMs = Math.max(60 * 1000, Math.min(Number(req.body?.ttlMs || SESSION_TTL_MS), SESSION_TTL_MS));
  clearViewerReplayCacheEverywhere(login, 'admin_viewer_session');
  const session = createViewerSession(login, displayName, ttlMs);
  recordOps('admin_viewer_session', {
    username: login,
    displayName,
    ttlSeconds: Math.round(ttlMs / 1000),
  });
  res.json(session);
});

app.post('/admin/kick', adminAuth, (req, res) => {
  const { username } = req.body || {};
  if (!username) return res.status(400).json({ error: 'Missing username' });
  const room = resolveAdminRoom(String((req.body && req.body.room) || req.query.room || '').trim());
  const ws = room && room.viewers.get(username);
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.close(4006, 'Kicked by admin');
    room.viewers.delete(username);
    room.viewerInfo.delete(username);
    recordOps('admin_kick', { room: room.roomId, username });
    sendToHost(room, { type: 'viewer_left', username });
    return res.json({ ok: true, message: `Kicked ${username}` });
  }
  res.json({ ok: false, message: 'Viewer not found or not connected' });
});

app.post('/admin/message', adminAuth, (req, res) => {
  const { username, message } = req.body || {};
  if (!message) return res.status(400).json({ error: 'Missing message' });

  const payload = JSON.stringify({ type: 'admin_message', message });

  if (username) {
    const ws = findViewerSocketAnywhere(username);
    if (ws && ws.readyState === WebSocket.OPEN) {
      recordOps('admin_message', { target: username, bytes: Buffer.byteLength(message) });
      sendWs(ws, payload, { type: 'admin_message', target: username });
      return res.json({ ok: true });
    }
    return res.json({ ok: false, message: 'Viewer not found' });
  }

  // Broadcast to every viewer on the relay - an operator notice is relay-wide.
  let count = 0;
  for (const room of rooms.values()) {
    for (const [, ws] of room.viewers) {
      sendWs(ws, payload, { type: 'admin_message' });
      count++;
    }
  }
  recordOps('admin_message', { target: 'all', bytes: Buffer.byteLength(message), viewers: count });
  res.json({ ok: true, message: `Sent to ${count} viewers` });
});

app.post('/admin/reload', adminAuth, (req, res) => {
  const delayMs = Math.max(0, Math.min(parseInt(req.body?.delayMs ?? '800', 10), 10000));
  const message = req.body?.message || 'Viewer update available. Reloading...';
  const payload = JSON.stringify({ type: 'client_reload', build: CLIENT_BUILD, delayMs, message });
  let sent = 0;
  let total = 0;
  for (const room of rooms.values()) {
    for (const [login, ws] of room.viewers) {
      total++;
      if (sendWs(ws, payload, { type: 'client_reload', target: login })) sent++;
    }
  }
  recordOps('admin_reload', { viewers: total, sent, build: CLIENT_BUILD, delayMs });
  res.json({ ok: true, build: CLIENT_BUILD, viewers: total, sent });
});

app.post('/admin/host-command', adminAuth, (req, res) => {
  const { command } = req.body || {};
  if (!command) return res.status(400).json({ error: 'Missing command' });
  const room = resolveAdminRoom(String((req.body && req.body.room) || req.query.room || '').trim());
  if (!roomIsLive(room)) {
    return res.json({ ok: false, message: 'Host not connected' });
  }
  recordOps('admin_command', summarizeMessage(command, Buffer.byteLength(JSON.stringify(command))));
  sendToHost(room, { ...command, source: 'admin', adminCommand: true });
  res.json({ ok: true });
});

// Serve admin page
app.get('/admin', (req, res) => {
  res.sendFile(path.join(__dirname, 'public', 'admin.html'));
});

// OBS overlay (transparent, add as Browser Source)
app.get('/obs', (req, res) => {
  res.sendFile(path.join(__dirname, 'public', 'obs.html'));
});

function broadcastToAdmins(msg) {
  const text = JSON.stringify(msg);
  for (const [, ws] of admins) {
    sendWs(ws, text, { type: msg && msg.type, target: 'admin' });
  }
}

// Prune expired sessions every 30 minutes
setInterval(() => {
  const now = Date.now();
  for (const [k, s] of sessions) {
    if (now > s.exp) {
      sessions.delete(k);
      if (!hasActiveSessionForLogin(s.login, now)) {
        clearViewerReplayCacheEverywhere(s.login, 'session_expired');
      }
    }
  }
}, 30 * 60 * 1000);

// ─── Start ────────────────────────────────────────────────────────────────────
server.listen(PORT, () => {
  console.log(`[relay] Overlord relay server listening on port ${PORT}`);
  if (OPEN_HOSTING) {
    console.log(`[relay] Open hosting is ON - up to ${MAX_ROOMS} games at once${HOST_INVITE_CODE ? ', invite code required' : ', no invite code'}.`);
    console.log(`[relay] Ceilings: ${MAX_ROOMS} games, ${MAX_VIEWERS} viewers per game, ${MAX_TOTAL_VIEWERS} viewers total on this relay.`);
  } else {
    console.log('[relay] Open hosting is OFF - only the game holding HOST_SECRET can host. Set OPEN_HOSTING=1 to let others host here.');
  }
  if (!TWITCH_CLIENT_ID) {
    // NOT a "guest mode" — there is no anonymous login on the relay path. With no
    // client id, /auth/twitch returns 503 and the viewer UI shows "Twitch auth is
    // not configured", so NOBODY can log in. The old wording claimed a working
    // mode that does not exist and sent people looking for it.
    // Playing without Twitch is the MOD's local mode (blank Relay URL in Mod
    // Settings), which does not involve this server at all.
    if (GUEST_LOGIN) {
      console.warn('[relay] TWITCH_CLIENT_ID not set. ALLOW_GUEST_LOGIN is on, so viewers join by typing a name — anyone with this URL can join as anyone. Fine for a first run or a private group; set TWITCH_CLIENT_ID before a public stream (that turns guest login off).');
    } else {
      console.warn('[relay] TWITCH_CLIENT_ID not set — viewers CANNOT log in; /auth/twitch will return 503.');
      console.warn('[relay] Either set TWITCH_CLIENT_ID, or set ALLOW_GUEST_LOGIN=1 to let people join by typing a name.');
      console.warn('[relay] To play with no server at all, leave Relay Server URL blank in Mod Settings (local mode) instead of running this relay.');
    }
  }
});
