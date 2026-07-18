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
const LOG_TRAFFIC      = process.env.LOG_TRAFFIC !== '0';
const LOG_DIR          = process.env.LOG_DIR || path.join(__dirname, 'logs');
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
/** @type {WebSocket|null} */
let hostSocket = null;

/** @type {Map<string, WebSocket>} twitchLogin → socket */
const viewers = new Map();
const admins = new Map();
const viewerInfo = new Map();
const viewerReplayCache = new Map();

// Context menus are expensive host work and viewers only care about the latest
// right-click. Allow one request in flight per viewer and keep at most one newer
// target, paced by wall clock so a paused game cannot create retry storms.
const CONTEXT_MENU_MIN_INTERVAL_MS = 150;
const CONTEXT_MENU_RESPONSE_TIMEOUT_MS = 2000;
const contextMenuRequests = new Map();

function clearContextMenuRequest(login) {
  const state = contextMenuRequests.get(login);
  if (state) {
    clearTimeout(state.sendTimer);
    clearTimeout(state.responseTimer);
  }
  contextMenuRequests.delete(login);
}

function dispatchContextMenuRequest(login, msg, state) {
  const delay = Math.max(0, CONTEXT_MENU_MIN_INTERVAL_MS - (Date.now() - state.lastSentAt));
  if (delay > 0) {
    state.queued = msg;
    if (!state.sendTimer) {
      state.sendTimer = setTimeout(() => {
        state.sendTimer = null;
        const latest = state.queued;
        state.queued = null;
        if (latest && !state.inFlight) dispatchContextMenuRequest(login, latest, state);
      }, delay);
    }
    return;
  }

  state.inFlight = true;
  state.lastSentAt = Date.now();
  sendToHost(msg);
  clearTimeout(state.responseTimer);
  state.responseTimer = setTimeout(() => {
    state.responseTimer = null;
    state.inFlight = false;
    recordOps('context_menu_timeout', { username: login });
    flushQueuedContextMenuRequest(login, state);
  }, CONTEXT_MENU_RESPONSE_TIMEOUT_MS);
}

function queueContextMenuRequest(login, msg) {
  let state = contextMenuRequests.get(login);
  if (!state) {
    state = { inFlight: false, queued: null, lastSentAt: 0, sendTimer: null, responseTimer: null };
    contextMenuRequests.set(login, state);
  }
  if (state.inFlight || state.sendTimer) {
    state.queued = msg;
    recordOps('context_menu_coalesced', { username: login });
    return;
  }
  dispatchContextMenuRequest(login, msg, state);
}

function flushQueuedContextMenuRequest(login, state) {
  const latest = state.queued;
  state.queued = null;
  if (latest) dispatchContextMenuRequest(login, latest, state);
}

function completeContextMenuRequest(login) {
  const state = contextMenuRequests.get(login);
  if (!state || !state.inFlight) return;
  clearTimeout(state.responseTimer);
  state.responseTimer = null;
  state.inFlight = false;
  flushQueuedContextMenuRequest(login, state);
}

// ─── Per-viewer outbound batch queue ─────────────────────────────────────────
// Messages targeted to a specific viewer are queued for up to BATCH_FLUSH_MS
// before being flushed as a single `{"type":"batch","msgs":[...]}` envelope.
// This collapses the 3-6 individual sends per game tick into one TCP segment.
const BATCH_FLUSH_MS = 16;
/** @type {Map<string, {texts: string[], timer: ReturnType<typeof setTimeout>}>} */
const viewerBatchQueues = new Map();

function flushViewerBatch(login) {
  const q = viewerBatchQueues.get(login);
  if (!q || q.texts.length === 0) { viewerBatchQueues.delete(login); return; }
  viewerBatchQueues.delete(login);
  const ws = viewers.get(login);
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  if (q.texts.length === 1) {
    sendWs(ws, q.texts[0], { type: 'batched_single', target: login });
    return;
  }
  // Wrap in batch envelope — browser unpacks each msg individually
  const payload = '{"type":"batch","msgs":[' + q.texts.join(',') + ']}';
  sendWs(ws, payload, { type: 'batch', target: login });
}

function queueForViewer(login, text) {
  let q = viewerBatchQueues.get(login);
  if (!q) {
    q = { texts: [], timer: setTimeout(() => flushViewerBatch(login), BATCH_FLUSH_MS) };
    viewerBatchQueues.set(login, q);
  }
  q.texts.push(text);
}
const opsLog = [];
const OPS_LOG_LIMIT = 500;
let frameStats = newFrameStats();
let backpressureStats = newBackpressureStats();

/** @type {Map<string, {login: string, displayName: string, exp: number}>} sessionToken → identity */
const sessions = new Map();
/** Latest room-level broadcast messages (no target) for late-joining viewers. */
let roomReplayCache = new Map();

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

function routeHostBinaryFrame(raw) {
  const { msg, buffer } = parseBinaryFrame(raw);
  if (!msg || msg.type !== 'map_frame') {
    recordOps('host_binary_rejected', summarizeMessage(msg, buffer.length));
    return;
  }

  recordFrame(msg, buffer.length);

  if (msg.target) {
    const dest = viewers.get(msg.target);
    sendWs(dest, buffer, { type: msg.type, target: msg.target });
    return;
  }

  for (const [, vws] of viewers) {
    sendWs(vws, buffer, { type: msg.type });
  }
}

function clearRoomReplayCache(reason) {
  if (viewerReplayCache.size > 0 || roomReplayCache.size > 0) {
    recordOps('replay_cache_clear', {
      reason,
      viewers: viewerReplayCache.size,
      room: roomReplayCache.size,
    });
  }
  viewerReplayCache.clear();
  roomReplayCache.clear();
}

function clearViewerReplayCache(login, reason) {
  const key = String(login || '');
  if (!key) return false;
  const deleted = viewerReplayCache.delete(key);
  if (deleted) recordOps('replay_cache_viewer_clear', { username: key, reason });
  return deleted;
}

function clearViewerMapReplayCache(login, reason) {
  const key = String(login || '');
  if (!key) return false;
  const cache = viewerReplayCache.get(key);
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
    recordOps('replay_cache_map_clear', { username: key, reason });
  }
  return hadMap;
}

function getViewerCache(login) {
  let cache = viewerReplayCache.get(login);
  if (!cache) {
    cache = {
      updatedAt: Date.now(),
      messages: new Map(),
      mapChunks: new Map(),
    };
    viewerReplayCache.set(login, cache);
  }
  return cache;
}

function cacheHostTextMessage(msg, text) {
  if (!msg || typeof text !== 'string') return;

  if (msg.type === 'map_transport' && msg.target) {
    clearViewerMapReplayCache(String(msg.target), 'map_transport_selected');
  }

  if (msg.type === 'host_capabilities' && msg.tacticalMap === false) {
    if (msg.target) clearViewerMapReplayCache(String(msg.target), 'tactical_map_disabled');
    else {
      for (const login of viewerReplayCache.keys()) {
        clearViewerMapReplayCache(login, 'tactical_map_disabled');
      }
    }
  }

  // Room-level broadcasts (no target) — keep latest for late joiners.
  if (!msg.target && REPLAYABLE_ROOM_TYPES.has(msg.type)) {
    roomReplayCache.set(msg.type, {
      text,
      type: msg.type,
      updatedAt: Date.now(),
    });
  }

  if (!msg.target) return;
  const target = String(msg.target);

  if (CLEAR_VIEWER_CACHE_TYPES.has(msg.type)) {
    clearViewerReplayCache(target, msg.type);
    return;
  }

  if (!REPLAYABLE_TARGETED_TYPES.has(msg.type)) return;

  const cache = getViewerCache(target);
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

function replayRoomCachedState(ws, login, request = null) {
  if (!ws || ws.readyState !== WebSocket.OPEN || roomReplayCache.size === 0) return 0;
  let sent = 0;
  for (const type of ROOM_REPLAY_ORDER) {
    if (request && !wantedIncludes(request, type)) continue;
    const cached = roomReplayCache.get(type);
    if (!cached) continue;
    if (sendCachedReplay(ws, cached, type, login || 'room')) sent++;
  }
  return sent;
}

function replayCachedState(login, request) {
  const cache = viewerReplayCache.get(login);
  const ws = viewers.get(login);
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    recordOps('replay_cache_miss', { username: login, reason: 'viewer_unavailable' });
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

  // Room-level game_info etc. — fill gaps for late joiners even without a viewer cache.
  sent += replayRoomCachedState(ws, login, request);

  recordOps(sent > 0 ? 'replay_cache_hit' : 'replay_cache_miss', {
    username: login,
    reason: request?.reason || '',
    wanted: Array.isArray(request?.wanted) ? request.wanted.join(',') : '',
    sent,
    cachedTypes: cache ? Array.from(cache.messages.keys()).join(',') : '',
    roomTypes: Array.from(roomReplayCache.keys()).join(','),
  });

  return sent > 0;
}

app.get('/', (_req, res) => {
  try {
    let html = fs.readFileSync(indexHtmlPath, 'utf8');
    html = html.replace(
      'data-twitch-client-id=""',
      `data-twitch-client-id="${TWITCH_CLIENT_ID}"`
    );
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('Content-Type', 'text/html');
    res.send(html);
  } catch (e) {
    res.status(500).send('Server error');
  }
});

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
    viewers: viewers.size,
    sessions: sessions.size,
    sessionTtlSeconds: Math.round(SESSION_TTL_MS / 1000),
    sessionSlideSeconds: Math.round(SESSION_SLIDE_MS / 1000),
    replayCacheViewers: viewerReplayCache.size,
    host:    hostSocket !== null && hostSocket.readyState === WebSocket.OPEN,
    twitch:  !!TWITCH_CLIENT_ID,
  });
});

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
const wss = new WebSocketServer({ server, path: '/ws' });

wss.on('connection', (ws, req) => {
  const url     = new URL(req.url, 'http://localhost');
  const role    = url.searchParams.get('role');
  const secret  = url.searchParams.get('secret');
  const sToken  = url.searchParams.get('session');
  const clientBuild = String(url.searchParams.get('build') || '').slice(0, 80);
  const mapTransport = normalizeMapTransport(url.searchParams.get('mapTransport'));

  // ── Host ──────────────────────────────────────────────────────────────────
  if (role === 'host') {
    if (HOST_SECRET && secret !== HOST_SECRET) {
      ws.close(4001, 'Unauthorized');
      console.log('[relay] Host rejected: bad secret');
      recordOps('host_rejected', { reason: 'bad_secret' });
      return;
    }

    if (hostSocket && hostSocket.readyState === WebSocket.OPEN) {
      hostSocket.close(4002, 'Replaced by new host');
    }

    hostSocket = ws;
    clearRoomReplayCache('host_connected');
    console.log('[relay] Host connected');
    recordOps('host_connected');
    broadcastToAdmins({ type: 'host_status', connected: true });
    const hostConnectedNotice = JSON.stringify({ type: 'host_connected', instance: INSTANCE_ID });
    for (const [, vws] of viewers) {
      sendWs(vws, hostConnectedNotice, { type: 'host_connected' });
    }

    // Send existing viewers to the newly connected host so quickloads/reconnects rebuild host state.
    for (const viewer of getViewerList()) {
      sendWs(ws, JSON.stringify({
        type: 'viewer_joined',
        username: viewer.login,
        displayName: viewer.displayName || viewer.login,
        mapTransport: viewer.mapTransport,
      }), { type: 'viewer_joined', target: 'host' });
    }

    ws.on('message', (raw, isBinary) => {
      try {
        if (isBinary) {
          routeHostBinaryFrame(raw);
          return;
        }

        const text = raw.toString('utf8');
        const msg  = JSON.parse(text);
        const summary = summarizeMessage(msg, Buffer.byteLength(text));
        if (msg.type === 'map_frame') recordFrame(msg, summary.bytes);
        else recordOps('host_message', summary);
        cacheHostTextMessage(msg, text);

        if (msg.target) {
          const login = msg.target;
          if (msg.type === 'context_menu' || (msg.type === 'action_result' && msg.action === 'context_menu')) {
            completeContextMenuRequest(login);
          }
          // Kick/ban must arrive immediately and close the socket — bypass batch queue
          if (msg.type === 'viewer_kick' || msg.type === 'banned') {
            const dest = viewers.get(login);
            sendWs(dest, text, { type: msg.type, target: login });
            if (dest && dest.readyState === WebSocket.OPEN) {
              setTimeout(() => {
                try { dest.close(4007, msg.type === 'banned' ? 'Banned by streamer' : 'Kicked by streamer'); } catch (e) {}
              }, 50);
            }
            recordOps('host_moderation', { type: msg.type, target: login });
            return;
          }
          // All other targeted messages are batched into a 16ms flush window
          queueForViewer(login, text);
          return;
        }

        if (msg.adminOnly) {
          broadcastToAdmins(msg);
          return;
        }

        for (const [, vws] of viewers) {
          sendWs(vws, text, { type: msg.type });
        }
        if (msg.type === 'map_frame') return;
        broadcastToAdmins(msg);
      } catch (e) {
        console.error('[relay] Bad host message:', e.message);
      }
    });

    ws.on('close', () => {
      if (hostSocket === ws) {
        hostSocket = null;
        for (const login of contextMenuRequests.keys()) clearContextMenuRequest(login);
        console.log('[relay] Host disconnected');
        recordOps('host_disconnected');
        const notice = JSON.stringify({ type: 'host_disconnected' });
        for (const [, vws] of viewers) {
          sendWs(vws, notice, { type: 'host_disconnected' });
        }
        broadcastToAdmins({ type: 'host_status', connected: false });
      }
    });

    ws.on('error', (err) => console.error('[relay] Host error:', err.message));
    return;
  }

  // ── Viewer ────────────────────────────────────────────────────────────────
  if (role === 'viewer') {
    const identity = resolveSession(sToken);
    if (!identity) {
      recordOps('viewer_rejected', { reason: 'invalid_session' });
      ws.close(4003, 'Invalid or missing session token');
      return;
    }

    const { login, displayName } = identity;
    const prev = viewers.get(login);
    if (!prev && viewers.size >= MAX_VIEWERS) {
      recordOps('viewer_rejected', { username: login, reason: 'server_full', viewers: viewers.size });
      ws.close(4004, 'Server full');
      return;
    }

    if (prev && prev.readyState === WebSocket.OPEN) {
      prev.close(4005, 'Reconnected from another tab');
    }
    clearContextMenuRequest(login);

    viewers.set(login, ws);
    viewerInfo.set(login, { login, displayName: displayName || login, connectedAt: Date.now(), clientBuild, mapTransport });
    console.log(`[relay] Viewer joined: ${displayName} (${login})`);
    recordOps('viewer_joined', { username: login, displayName, clientBuild });
    sendToHost({ type: 'viewer_joined', username: login, displayName, mapTransport });
    broadcastToAdmins({ type: 'viewer_update', action: 'joined', login, displayName });
    suggestClientReload(ws, login, clientBuild);
    sendWs(ws, JSON.stringify(RELAY_CAPABILITIES), { type: 'relay_capabilities', target: login });
    if (hostSocket && hostSocket.readyState === WebSocket.OPEN) {
      sendWs(ws, JSON.stringify({ type: 'host_connected', instance: INSTANCE_ID }), { type: 'host_connected', target: login });
      // Immediately replay room-level state (game_info) so the pill isn't stuck on "Host waiting".
      replayRoomCachedState(ws, login);
    }
    if (viewerReplayCache.has(login)) {
      replayCachedState(login, { type: 'state_resync_request', reason: 'viewer_reconnected' });
    }

    ws.on('message', (raw) => {
      try {
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
        msg.username = login;
        msg.source = 'viewer';
        msg.adminCommand = false;
        recordOps('viewer_message', summarizeMessage(msg, Buffer.byteLength(JSON.stringify(msg))));
        if (msg.type === 'state_resync_request' && replayCachedState(login, msg)) {
          return;
        }
        if (msg.type === 'map_transport') {
          msg.transport = normalizeMapTransport(msg.transport);
          const info = viewerInfo.get(login);
          if (info) info.mapTransport = msg.transport;
          if (msg.transport === 'jpeg') clearViewerMapReplayCache(login, 'viewer_selected_jpeg');
        }
        if (msg.type === 'command' && msg.action === 'context_menu') {
          queueContextMenuRequest(login, msg);
          return;
        }
        sendToHost(msg);
      } catch (e) {
        console.error(`[relay] Bad viewer message from ${login}:`, e.message);
      }
    });

    ws.on('close', () => {
      if (viewers.get(login) === ws) {
        viewers.delete(login);
        viewerInfo.delete(login);
        clearContextMenuRequest(login);
        console.log(`[relay] Viewer left: ${login}`);
        recordOps('viewer_left', { username: login });
        sendToHost({ type: 'viewer_left', username: login });
        broadcastToAdmins({ type: 'viewer_update', action: 'left', login });
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

    // Send current state immediately
    sendWs(ws, JSON.stringify({
      type: 'admin_sync',
      host: hostSocket !== null && hostSocket.readyState === WebSocket.OPEN,
      viewers: getViewerList(),
      sessions: sessions.size,
      uptime: process.uptime(),
      instance: INSTANCE_ID,
      clientBuild: CLIENT_BUILD,
      replayCacheViewers: viewerReplayCache.size,
    }), { type: 'admin_sync', target: 'admin' });
    sendToHost({ type: 'request_colonist_list', source: 'admin', adminCommand: true });

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
function sendToHost(msg) {
  if (!hostSocket || hostSocket.readyState !== WebSocket.OPEN) {
    const summary = typeof msg === 'string'
      ? { type: 'raw', bytes: Buffer.byteLength(msg) }
      : summarizeMessage(msg, Buffer.byteLength(JSON.stringify(msg || {})));
    recordOps('host_message_dropped', summary);
    return;
  }
  try {
    const type = typeof msg === 'string' ? '' : msg && msg.type;
    sendWs(hostSocket, typeof msg === 'string' ? msg : JSON.stringify(msg), { type, target: 'host' });
  } catch (e) {
    console.error('[relay] Failed to send to host:', e.message);
  }
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

function getViewerList() {
  const list = [];
  for (const [login, ws] of viewers) {
    const info = viewerInfo.get(login) || { login, displayName: login, connectedAt: 0 };
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

function describeReplayCacheEntry(login, cache, now = Date.now()) {
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
    connected: viewers.has(login),
    messageCount: messages.length,
    mapChunkCount: cache.mapChunks ? cache.mapChunks.size : 0,
    messages,
  };
}

// ─── Admin API (protected by HOST_SECRET) ─────────────────────────────────────
function adminAuth(req, res, next) {
  const auth = req.headers.authorization;
  if (!HOST_SECRET || auth === `Bearer ${HOST_SECRET}`) return next();
  res.status(401).json({ error: 'Unauthorized' });
}

app.get('/admin/status', adminAuth, (_req, res) => {
  res.json({
    instance: INSTANCE_ID,
    pid:      process.pid,
    host:     hostSocket !== null && hostSocket.readyState === WebSocket.OPEN,
    clientBuild: CLIENT_BUILD,
    viewers:  getViewerList(),
    replayCacheViewers: viewerReplayCache.size,
    sessions: sessions.size,
    uptime:   Math.round(process.uptime()),
  });
});

function sendReplayCacheSummary(res) {
  const now = Date.now();
  res.json({
    ok: true,
    instance: INSTANCE_ID,
    host: hostSocket !== null && hostSocket.readyState === WebSocket.OPEN,
    replayCacheViewers: viewerReplayCache.size,
    viewers: Array.from(viewerReplayCache.entries())
      .map(([login, cache]) => describeReplayCacheEntry(login, cache, now))
      .sort((a, b) => a.login.localeCompare(b.login)),
  });
}

app.get('/admin/cache', adminAuth, (_req, res) => {
  sendReplayCacheSummary(res);
});

app.get('/admin/replay-cache', adminAuth, (_req, res) => {
  sendReplayCacheSummary(res);
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
  clearViewerReplayCache(login, 'admin_viewer_session');
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
  const ws = viewers.get(username);
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.close(4006, 'Kicked by admin');
    viewers.delete(username);
    viewerInfo.delete(username);
    recordOps('admin_kick', { username });
    sendToHost({ type: 'viewer_left', username });
    return res.json({ ok: true, message: `Kicked ${username}` });
  }
  res.json({ ok: false, message: 'Viewer not found or not connected' });
});

app.post('/admin/message', adminAuth, (req, res) => {
  const { username, message } = req.body || {};
  if (!message) return res.status(400).json({ error: 'Missing message' });

  const payload = JSON.stringify({ type: 'admin_message', message });

  if (username) {
    const ws = viewers.get(username);
    if (ws && ws.readyState === WebSocket.OPEN) {
      recordOps('admin_message', { target: username, bytes: Buffer.byteLength(message) });
      sendWs(ws, payload, { type: 'admin_message', target: username });
      return res.json({ ok: true });
    }
    return res.json({ ok: false, message: 'Viewer not found' });
  }

  // Broadcast to all
  for (const [, ws] of viewers) {
    sendWs(ws, payload, { type: 'admin_message' });
  }
  recordOps('admin_message', { target: 'all', bytes: Buffer.byteLength(message), viewers: viewers.size });
  res.json({ ok: true, message: `Sent to ${viewers.size} viewers` });
});

app.post('/admin/reload', adminAuth, (req, res) => {
  const delayMs = Math.max(0, Math.min(parseInt(req.body?.delayMs ?? '800', 10), 10000));
  const message = req.body?.message || 'Viewer update available. Reloading...';
  const payload = JSON.stringify({ type: 'client_reload', build: CLIENT_BUILD, delayMs, message });
  let sent = 0;
  for (const [login, ws] of viewers) {
    if (sendWs(ws, payload, { type: 'client_reload', target: login })) sent++;
  }
  recordOps('admin_reload', { viewers: viewers.size, sent, build: CLIENT_BUILD, delayMs });
  res.json({ ok: true, build: CLIENT_BUILD, viewers: viewers.size, sent });
});

app.post('/admin/host-command', adminAuth, (req, res) => {
  const { command } = req.body || {};
  if (!command) return res.status(400).json({ error: 'Missing command' });
  if (!hostSocket || hostSocket.readyState !== WebSocket.OPEN) {
    return res.json({ ok: false, message: 'Host not connected' });
  }
  recordOps('admin_command', summarizeMessage(command, Buffer.byteLength(JSON.stringify(command))));
  sendToHost({ ...command, source: 'admin', adminCommand: true });
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
        clearViewerReplayCache(s.login, 'session_expired');
      }
    }
  }
}, 30 * 60 * 1000);

// ─── Start ────────────────────────────────────────────────────────────────────
server.listen(PORT, () => {
  console.log(`[relay] Overlord relay server listening on port ${PORT}`);
  if (!TWITCH_CLIENT_ID) console.warn('[relay] TWITCH_CLIENT_ID not set — running in guest mode');
});
