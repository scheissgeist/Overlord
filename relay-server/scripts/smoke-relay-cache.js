'use strict';

const childProcess = require('child_process');
const crypto = require('crypto');
const http = require('http');
const path = require('path');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (19000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = process.env.SMOKE_HOST_SECRET || `smoke-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'cacheviewer';
const VIEWER_DISPLAY = 'Cache Viewer';
const PAWN_ID = 4242;

function wait(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitFor(fn, label, timeoutMs = 8000) {
  const start = Date.now();
  let lastError = null;
  while (Date.now() - start < timeoutMs) {
    try {
      const value = await fn();
      if (value) return value;
    } catch (error) {
      lastError = error;
    }
    await wait(50);
  }
  const suffix = lastError ? `: ${lastError.message}` : '';
  throw new Error(`Timed out waiting for ${label}${suffix}`);
}

function requestJson(method, urlPath, body = null, { allowStatus = [] } = {}) {
  const payload = body ? JSON.stringify(body) : null;
  return new Promise((resolve, reject) => {
    const req = http.request(`${BASE_URL}${urlPath}`, {
      method,
      headers: {
        Authorization: `Bearer ${HOST_SECRET}`,
        ...(payload ? {
          'Content-Type': 'application/json',
          'Content-Length': Buffer.byteLength(payload),
        } : {}),
      },
    }, res => {
      let data = '';
      res.on('data', chunk => { data += chunk; });
      res.on('end', () => {
        let json = null;
        try { json = data ? JSON.parse(data) : null; } catch (_) {}
        if ((res.statusCode < 200 || res.statusCode >= 300) && !allowStatus.includes(res.statusCode)) {
          reject(new Error(`${method} ${urlPath} failed ${res.statusCode}: ${data}`));
          return;
        }
        resolve({ statusCode: res.statusCode, body: json, raw: data });
      });
    });
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

function startRelay() {
  const child = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: ROOT,
    env: {
      ...process.env,
      PORT: String(PORT),
      HOST_SECRET,
      TWITCH_CLIENT_ID: '',
      LOG_TRAFFIC: '0',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  const output = [];
  child.stdout.on('data', chunk => output.push(chunk.toString()));
  child.stderr.on('data', chunk => output.push(chunk.toString()));
  return { child, output };
}

function waitForWsOpen(ws, label) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`${label} websocket did not open`)), 8000);
    ws.once('open', () => {
      clearTimeout(timer);
      resolve();
    });
    ws.once('error', reject);
  });
}

function collectMessages(ws) {
  const messages = [];
  ws.on('message', raw => {
    try {
      const msg = JSON.parse(raw.toString('utf8'));
      if (msg.type === 'batch' && Array.isArray(msg.msgs)) messages.push(...msg.msgs);
      else messages.push(msg);
    } catch (_) {}
  });
  return messages;
}

function send(ws, msg) {
  ws.send(JSON.stringify(msg));
}

function findMessageAfter(messages, cursor, type, predicate = () => true) {
  return messages.slice(cursor).find(msg => msg.type === type && predicate(msg));
}

function waitForMessageAfter(messages, cursor, type, label, predicate = () => true) {
  return waitFor(() => findMessageAfter(messages, cursor, type, predicate), label);
}

async function readReplayCacheDiagnostics() {
  const attempts = [];
  for (const urlPath of ['/admin/cache', '/admin/replay-cache']) {
    const response = await requestJson('GET', urlPath, null, { allowStatus: [404] });
    attempts.push({ path: urlPath, statusCode: response.statusCode, body: response.body, raw: response.raw });
    if (response.statusCode === 200) return { path: urlPath, body: response.body };
  }

  throw new Error(
    `Replay cache diagnostics endpoint is missing. Expected GET /admin/cache or GET /admin/replay-cache with admin auth; got ${attempts.map(a => `${a.path}=${a.statusCode}`).join(', ')}.`
  );
}

function cacheDiagnosticsIncludeTypes(body, login, expectedTypes) {
  const text = JSON.stringify(body || {});
  if (!text.includes(login)) return false;
  return expectedTypes.every(type => text.includes(type));
}

function cacheDiagnosticsIncludeAnyType(body, login, expectedTypes) {
  const text = JSON.stringify(body || {});
  if (!text.includes(login)) return false;
  return expectedTypes.some(type => text.includes(type));
}

async function main() {
  const relay = startRelay();
  let hostWs = null;
  let viewerWs = null;

  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'relay health', 15000);

    hostWs = new WebSocket(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
    const hostMessages = collectMessages(hostWs);
    await waitForWsOpen(hostWs, 'host');

    const sessionResponse = await requestJson('POST', '/admin/viewer-session', {
      login: VIEWER_LOGIN,
      displayName: VIEWER_DISPLAY,
      ttlMs: 10 * 60 * 1000,
    });
    const session = sessionResponse.body;
    if (!session?.sessionToken) {
      throw new Error(`Viewer session response missing sessionToken: ${JSON.stringify(sessionResponse)}`);
    }

    viewerWs = new WebSocket(`${WS_URL}?role=viewer&session=${encodeURIComponent(session.sessionToken)}&build=smoke-relay-cache`);
    const viewerMessages = collectMessages(viewerWs);
    await waitForWsOpen(viewerWs, 'viewer');
    await waitForMessageAfter(hostMessages, 0, 'viewer_joined', 'viewer_joined forwarded to host', msg => msg.username === VIEWER_LOGIN);

    const targetedMessages = [
      {
        type: 'permissions',
        target: VIEWER_LOGIN,
        draft: true,
        move: true,
        attack: false,
        equip: true,
      },
      {
        type: 'pawn_state',
        target: VIEWER_LOGIN,
        state: {
          id: PAWN_ID,
          name: VIEWER_DISPLAY,
          currentJob: 'Cache smoke',
          health: { summaryHp: 100, hediffs: [] },
        },
      },
      {
        type: 'map_full',
        target: VIEWER_LOGIN,
        protocol: 'vdr/0',
        mapEpoch: 7,
        seq: 10,
        baseSeq: 0,
        snapshot: true,
        width: 4,
        height: 3,
        terrain: Buffer.from([1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4]).toString('base64'),
      },
      {
        type: 'map_delta',
        target: VIEWER_LOGIN,
        protocol: 'vdr/0',
        mapEpoch: 7,
        seq: 11,
        baseSeq: 10,
        viewerPawnId: PAWN_ID,
        pawns: [[1, 1, 0, PAWN_ID, VIEWER_DISPLAY]],
        buildings: [[2, 1, 4, 2, 1]],
      },
      {
        type: 'entity_keyframe',
        target: VIEWER_LOGIN,
        protocol: 'vdr/0',
        mapEpoch: 7,
        seq: 11,
        baseSeq: 10,
        entityKeyframe: true,
        entityCount: 1,
        entitiesTruncated: false,
        entities: [{ id: PAWN_ID, kind: 'pawn', x: 1, z: 1, label: VIEWER_DISPLAY }],
        removedEntities: [],
      },
    ];

    const initialViewerCursor = viewerMessages.length;
    for (const msg of targetedMessages) send(hostWs, msg);

    await waitForMessageAfter(viewerMessages, initialViewerCursor, 'permissions', 'viewer permissions delivery', msg => msg.move === true);
    await waitForMessageAfter(viewerMessages, initialViewerCursor, 'pawn_state', 'viewer pawn_state delivery', msg => msg.state?.id === PAWN_ID);
    await waitForMessageAfter(viewerMessages, initialViewerCursor, 'map_full', 'viewer map_full delivery', msg => msg.mapEpoch === 7 && msg.seq === 10);
    await waitForMessageAfter(viewerMessages, initialViewerCursor, 'map_delta', 'viewer map_delta delivery', msg => msg.mapEpoch === 7 && msg.seq === 11);
    await waitForMessageAfter(viewerMessages, initialViewerCursor, 'entity_keyframe', 'viewer entity_keyframe delivery', msg => msg.mapEpoch === 7 && msg.entityCount === 1);

    const diagnostics = await readReplayCacheDiagnostics();
    const expectedCachedTypes = ['permissions', 'pawn_state', 'map_full', 'map_delta', 'entity_keyframe'];
    if (!cacheDiagnosticsIncludeTypes(diagnostics.body, VIEWER_LOGIN, expectedCachedTypes)) {
      throw new Error(
        `Replay cache diagnostics at ${diagnostics.path} did not include viewer ${VIEWER_LOGIN} and cached types ${expectedCachedTypes.join(', ')}: ${JSON.stringify(diagnostics.body)}`
      );
    }

    const disableCursor = viewerMessages.length;
    send(hostWs, {
      type: 'host_capabilities',
      target: VIEWER_LOGIN,
      tacticalMap: false,
    });
    await waitForMessageAfter(viewerMessages, disableCursor, 'host_capabilities', 'viewer host_capabilities tacticalMap false delivery', msg => msg.tacticalMap === false);
    const disabledDiagnostics = await readReplayCacheDiagnostics();
    if (cacheDiagnosticsIncludeAnyType(disabledDiagnostics.body, VIEWER_LOGIN, ['map_full', 'map_delta', 'entity_keyframe', 'entity_delta'])) {
      throw new Error(`Replay cache still included map state after tacticalMap=false: ${JSON.stringify(disabledDiagnostics.body)}`);
    }

    const recacheViewerCursor = viewerMessages.length;
    send(hostWs, targetedMessages[2]);
    send(hostWs, targetedMessages[3]);
    send(hostWs, targetedMessages[4]);
    await waitForMessageAfter(viewerMessages, recacheViewerCursor, 'map_full', 'viewer map_full recache delivery', msg => msg.mapEpoch === 7 && msg.seq === 10);
    await waitForMessageAfter(viewerMessages, recacheViewerCursor, 'map_delta', 'viewer map_delta recache delivery', msg => msg.mapEpoch === 7 && msg.seq === 11);
    await waitForMessageAfter(viewerMessages, recacheViewerCursor, 'entity_keyframe', 'viewer entity_keyframe recache delivery', msg => msg.mapEpoch === 7 && msg.entityCount === 1);
    const recachedDiagnostics = await readReplayCacheDiagnostics();
    if (!cacheDiagnosticsIncludeTypes(recachedDiagnostics.body, VIEWER_LOGIN, ['map_full', 'map_delta', 'entity_keyframe'])) {
      throw new Error(`Replay cache did not restore map/entity state after fresh state: ${JSON.stringify(recachedDiagnostics.body)}`);
    }

    const hostCursor = hostMessages.length;
    const replayCursor = viewerMessages.length;
    send(viewerWs, {
      type: 'state_resync_request',
      reason: 'smoke_replay_cache',
      wanted: ['map_full', 'map_delta', 'entity_keyframe'],
    });

    await waitForMessageAfter(viewerMessages, replayCursor, 'map_full', 'cached map_full replay', msg => msg.mapEpoch === 7 && msg.seq === 10 && msg.relayCached === true);
    await waitForMessageAfter(viewerMessages, replayCursor, 'map_delta', 'cached map_delta replay', msg => msg.mapEpoch === 7 && msg.seq === 11 && msg.relayCached === true);
    await waitForMessageAfter(viewerMessages, replayCursor, 'entity_keyframe', 'cached entity_keyframe replay', msg => msg.mapEpoch === 7 && msg.entityCount === 1 && msg.relayCached === true);
    await wait(300);
    const forwarded = findMessageAfter(hostMessages, hostCursor, 'state_resync_request');
    if (forwarded) {
      throw new Error(`state_resync_request was forwarded to host instead of being satisfied from cache: ${JSON.stringify(forwarded)}`);
    }

    const reconnectHostCursor = hostMessages.length;
    viewerWs.close();
    await waitForMessageAfter(hostMessages, reconnectHostCursor, 'viewer_left', 'viewer_left forwarded to host on reconnect setup', msg => msg.username === VIEWER_LOGIN);

    const reconnectWs = new WebSocket(`${WS_URL}?role=viewer&session=${encodeURIComponent(session.sessionToken)}&build=smoke-relay-cache`);
    const reconnectMessages = collectMessages(reconnectWs);
    viewerWs = reconnectWs;
    await waitForWsOpen(reconnectWs, 'viewer reconnect');
    await waitForMessageAfter(hostMessages, reconnectHostCursor, 'viewer_joined', 'viewer_joined forwarded to host after reconnect', msg => msg.username === VIEWER_LOGIN);
    await waitForMessageAfter(reconnectMessages, 0, 'map_full', 'cached map_full replay on reconnect', msg => msg.mapEpoch === 7 && msg.seq === 10 && msg.relayCached === true);
    await waitForMessageAfter(reconnectMessages, 0, 'map_delta', 'cached map_delta replay on reconnect', msg => msg.mapEpoch === 7 && msg.seq === 11 && msg.relayCached === true);
    await waitForMessageAfter(reconnectMessages, 0, 'entity_keyframe', 'cached entity_keyframe replay on reconnect', msg => msg.mapEpoch === 7 && msg.entityCount === 1 && msg.relayCached === true);

    const replacementSession = await requestJson('POST', '/admin/viewer-session', {
      login: VIEWER_LOGIN,
      displayName: VIEWER_DISPLAY,
      ttlMs: 10 * 60 * 1000,
    });
    if (!replacementSession.body?.sessionToken) {
      throw new Error(`Replacement viewer session response missing sessionToken: ${JSON.stringify(replacementSession)}`);
    }
    const afterReplacementDiagnostics = await readReplayCacheDiagnostics();
    if (cacheDiagnosticsIncludeTypes(afterReplacementDiagnostics.body, VIEWER_LOGIN, expectedCachedTypes)) {
      throw new Error(
        `Replay cache still included old state for ${VIEWER_LOGIN} after admin-minted replacement session: ${JSON.stringify(afterReplacementDiagnostics.body)}`
      );
    }

    console.log(JSON.stringify({
      ok: true,
      baseUrl: BASE_URL,
      viewer: VIEWER_LOGIN,
      diagnosticsPath: diagnostics.path,
      cachedTypes: expectedCachedTypes,
      assertions: {
        targetedMessagesDelivered: true,
        diagnosticsEndpointPresent: true,
        mapReplayDelivered: true,
        stateResyncNotForwardedToHost: true,
        reconnectReplayDelivered: true,
        adminSessionClearedCache: true,
        tacticalMapDisabledClearedMapCache: true,
        relayCachedReplayAnnotated: true,
      },
    }, null, 2));
  } catch (error) {
    const relayOutput = relay.output.join('').trim();
    console.error(JSON.stringify({
      ok: false,
      error: error.message,
      relayOutput: relayOutput.slice(-4000),
      stack: error.stack,
    }, null, 2));
    process.exitCode = 1;
  } finally {
    if (viewerWs) viewerWs.close();
    if (hostWs) hostWs.close();
    relay.child.kill();
    await wait(200);
  }
}

main();
