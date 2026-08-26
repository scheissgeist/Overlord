#!/usr/bin/env node
/**
 * Acceptance test for multi-tenant hosting on the Overlord relay.
 *
 * Written BEFORE the multi-tenancy change so the "before" state is measured, not
 * remembered. Against today's single-tenant relay this script is EXPECTED to fail
 * cases 2-6; the whole point of the change is to flip them. Run it against a clean
 * baseline first, keep the output, then run it again after.
 *
 *   node scripts/multi-tenant-acceptance.js            # spawns its own relay on :8097
 *   ACCEPT_URL=http://localhost:8080 node scripts/multi-tenant-acceptance.js
 *
 * It spawns the relay itself by default so the test cannot be fooled by a stale
 * process left over from an earlier run (a relay that is not the code under test
 * returns clean-looking, wrong answers - see the read-path rule in AGENTS.md).
 *
 * Every secret, viewer name and marker below is generated at runtime. There are no
 * literal identifiers in this file: reruns never collide with each other, and
 * nothing here refers to a real person or a real credential.
 */

'use strict';

const { spawn } = require('child_process');
const crypto = require('crypto');
const path = require('path');
const http = require('http');
const WebSocket = require('ws');

const SECRET_A = crypto.randomBytes(18).toString('hex');
const SECRET_B = crypto.randomBytes(18).toString('hex');
const NAME_A = 'v' + crypto.randomBytes(4).toString('hex');
const NAME_B = 'v' + crypto.randomBytes(4).toString('hex');
const MARK_A = 'COLONY_' + crypto.randomBytes(4).toString('hex').toUpperCase();
const MARK_B = 'COLONY_' + crypto.randomBytes(4).toString('hex').toUpperCase();

const PORT = parseInt(process.env.ACCEPT_PORT || '8097', 10);
const BASE = process.env.ACCEPT_URL || `http://localhost:${PORT}`;
const WS_BASE = BASE.replace(/^http/, 'ws') + '/ws';

const ROOMS_FILE = path.join(require('os').tmpdir(), 'overlord-acceptance-rooms-' + process.pid + '.json');

const results = [];
let relayProc = null;

function record(name, pass, detail) {
  results.push({ name, pass, detail });
  const tag = pass === true ? 'PASS' : pass === false ? 'FAIL' : 'SKIP';
  console.log(`${tag}  ${name}`);
  if (detail) console.log(`      ${detail}`);
}

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

function getJson(pathname) {
  return new Promise(resolve => {
    const req = http.get(BASE + pathname, res => {
      let b = '';
      res.on('data', d => (b += d));
      res.on('end', () => {
        let parsed = null;
        try { parsed = JSON.parse(b); } catch (_) {}
        resolve({ code: res.statusCode, json: parsed, raw: b });
      });
    });
    req.on('error', e => resolve({ code: 0, json: null, raw: e.message }));
    req.setTimeout(5000, () => { req.destroy(); resolve({ code: 0, json: null, raw: 'timeout' }); });
  });
}

function postJson(pathname, body) {
  return new Promise(resolve => {
    const data = JSON.stringify(body);
    const req = http.request(
      { host: 'localhost', port: PORT, path: pathname, method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) } },
      res => {
        let b = '';
        res.on('data', d => (b += d));
        res.on('end', () => {
          let parsed = null;
          try { parsed = JSON.parse(b); } catch (_) {}
          resolve({ code: res.statusCode, json: parsed, raw: b });
        });
      }
    );
    req.on('error', e => resolve({ code: 0, json: null, raw: e.message }));
    req.end(data);
  });
}

/**
 * A host connection that records what happened to it. `room` is optional on
 * purpose: the currently shipped mod DLL sends only role and secret, so the
 * no-room case is the backward-compatibility case and must keep working.
 */
function openHost(secret, room) {
  return new Promise(resolve => {
    let url = `${WS_BASE}?role=host&secret=${encodeURIComponent(secret)}`;
    if (room) url += `&room=${encodeURIComponent(room)}`;
    const ws = new WebSocket(url);
    const state = { ws, url, opened: false, closeCode: null, closeReason: null, messages: [] };
    ws.on('open', () => { state.opened = true; resolve(state); });
    ws.on('message', raw => { try { state.messages.push(JSON.parse(raw.toString())); } catch (_) {} });
    ws.on('close', (code, reason) => {
      state.closeCode = code;
      state.closeReason = reason ? reason.toString() : '';
      if (!state.opened) resolve(state);
    });
    ws.on('error', () => { if (!state.opened) resolve(state); });
    setTimeout(() => resolve(state), 4000);
  });
}

/** The path a streamer who is NOT the relay owner takes: a key the relay issued. */
function openHostWithKey(hostKey, room) {
  return new Promise(resolve => {
    let url = `${WS_BASE}?role=host&key=${encodeURIComponent(hostKey)}`;
    if (room) url += `&room=${encodeURIComponent(room)}`;
    const ws = new WebSocket(url);
    const state = { ws, url, opened: false, closeCode: null, closeReason: null, messages: [] };
    ws.on('open', () => { state.opened = true; resolve(state); });
    ws.on('close', (code, reason) => {
      state.closeCode = code;
      state.closeReason = reason ? reason.toString() : '';
      if (!state.opened) resolve(state);
    });
    ws.on('error', () => { if (!state.opened) resolve(state); });
    setTimeout(() => resolve(state), 4000);
  });
}

function openViewer(sessionToken, room) {
  return new Promise(resolve => {
    let url = `${WS_BASE}?role=viewer&session=${encodeURIComponent(sessionToken)}&build=acceptance`;
    if (room) url += `&room=${encodeURIComponent(room)}`;
    const ws = new WebSocket(url);
    const state = { ws, url, opened: false, closeCode: null, messages: [] };
    ws.on('open', () => { state.opened = true; resolve(state); });
    ws.on('message', raw => {
      try { state.messages.push(JSON.parse(raw.toString())); } catch (_) { state.messages.push({ type: '(binary)' }); }
    });
    ws.on('close', code => { state.closeCode = code; if (!state.opened) resolve(state); });
    ws.on('error', () => { if (!state.opened) resolve(state); });
    setTimeout(() => resolve(state), 4000);
  });
}

function startRelay() {
  return new Promise((resolve, reject) => {
    try { require('fs').unlinkSync(ROOMS_FILE); } catch (_) {}
    const serverPath = path.join(__dirname, '..', 'server.js');
    relayProc = spawn(process.execPath, [serverPath], {
      env: {
        ...process.env,
        PORT: String(PORT),
        // Streamer A's secret is the one the relay knows in the single-tenant world.
        HOST_SECRET: SECRET_A,
        TWITCH_CLIENT_ID: '',
        ALLOW_GUEST_LOGIN: '1',
        LOG_TRAFFIC: '0',
        OPEN_HOSTING: '1',
        // Its own registry, deleted below. Sharing the real one made run N+1 fail
        // at registration with "This relay is full" - rooms from earlier runs were
        // still holding slots. A test that contaminates the next run is not a test.
        ROOMS_FILE: ROOMS_FILE,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let out = '';
    relayProc.stdout.on('data', d => {
      out += d.toString();
      if (out.includes('listening on port')) resolve();
    });
    relayProc.stderr.on('data', d => { out += d.toString(); });
    relayProc.on('exit', c => { if (!out.includes('listening')) reject(new Error('relay exited ' + c + '\n' + out)); });
    setTimeout(() => reject(new Error('relay did not start in 10s:\n' + out)), 10000);
  });
}

async function main() {
  if (!process.env.ACCEPT_URL) {
    await startRelay();
    console.log(`relay under test: ${BASE} (spawned)\n`);
  } else {
    console.log(`relay under test: ${BASE} (external)\n`);
  }

  // 1. Backward compatibility: a host with NO room param still connects.
  // The shipped mod DLL sends exactly this. If this fails, every existing
  // Workshop subscriber is locked out.
  const legacyHost = await openHost(SECRET_A);
  record('1. legacy host (no room param) connects',
    legacyHost.opened === true && legacyHost.closeCode === null,
    `opened=${legacyHost.opened} closeCode=${legacyHost.closeCode} reason=${legacyHost.closeReason || '-'}`);

  // 2. A SECOND streamer hosts here. This is the whole ask, and it is done the way
  // a real person would: the mod asks the relay for a room and is handed a key. No
  // secret is invented, copied, or typed. Before this change the relay closed 4001
  // (unknown secret) or, with the owner's secret, evicted the incumbent with 4002.
  const reg = await postJson('/api/host/register', { label: 'Second Streamer' });
  record('2a. a second streamer can register a room',
    reg.code === 200 && !!(reg.json && reg.json.hostKey && reg.json.roomId),
    `HTTP ${reg.code} ${(reg.raw || '').slice(0, 160)}`);

  const hostB = reg.json && reg.json.hostKey
    ? await openHostWithKey(reg.json.hostKey)
    : { opened: false, closeCode: null, closeReason: 'no key issued', ws: { close() {} } };
  await sleep(600);
  record('2. second host is accepted alongside the first',
    hostB.opened === true && hostB.closeCode === null,
    `opened=${hostB.opened} closeCode=${hostB.closeCode} reason=${hostB.closeReason || '-'}`);

  record('3. first host SURVIVES the second connecting',
    legacyHost.closeCode === null,
    legacyHost.closeCode === null
      ? 'still open'
      : `evicted with ${legacyHost.closeCode} "${legacyHost.closeReason}"`);

  // 4. Directory lists both live games.
  const rooms = await getJson('/api/rooms');
  const roomCount = rooms.json && Array.isArray(rooms.json.rooms) ? rooms.json.rooms.length : 0;
  record('4. /api/rooms lists both live games',
    rooms.code === 200 && roomCount >= 2,
    `HTTP ${rooms.code}, rooms=${roomCount}, body=${(rooms.raw || '').slice(0, 200)}`);

  // 5. Frames are isolated: A's viewer never receives B's traffic.
  let isolation = 'skipped - no room ids available';
  let isolationPass = null;
  if (roomCount >= 2) {
    const roomA = rooms.json.rooms[0];
    const roomB = rooms.json.rooms[1];
    const sessA = await postJson('/auth/guest', { name: NAME_A });
    const sessB = await postJson('/auth/guest', { name: NAME_B });
    if (sessA.code === 200 && sessB.code === 200) {
      const vA = await openViewer(sessA.json.sessionToken, roomA.roomId);
      const vB = await openViewer(sessB.json.sessionToken, roomB.roomId);
      await sleep(400);

      if (legacyHost.opened) {
        legacyHost.ws.send(JSON.stringify({ type: 'game_info', mapName: MARK_A, hour: 1 }));
      }
      if (hostB.opened) {
        hostB.ws.send(JSON.stringify({ type: 'game_info', mapName: MARK_B, hour: 2 }));
      }
      await sleep(900);

      const aSaw = JSON.stringify(vA.messages);
      const bSaw = JSON.stringify(vB.messages);
      const leakToA = aSaw.includes(MARK_B);
      const leakToB = bSaw.includes(MARK_A);
      isolationPass = !leakToA && !leakToB;
      isolation = `viewer in room A saw room B payload: ${leakToA}; viewer in room B saw room A payload: ${leakToB}`;
      try { vA.ws.close(); vB.ws.close(); } catch (_) {}
    } else {
      isolation = `guest auth failed: A=${sessA.code} B=${sessB.code}`;
    }
  }
  record('5. cross-room traffic is isolated', isolationPass, isolation);

  // 6. Room ownership: another streamer's secret must not take over a room.
  let ownership = 'skipped';
  let ownershipPass = null;
  if (roomCount >= 2) {
    const roomB = rooms.json.rooms[1];
    const impostor = await openHost(SECRET_A, roomB.roomId);
    await sleep(500);
    ownershipPass = (impostor.opened === false || impostor.closeCode !== null) && hostB.closeCode === null;
    ownership = `impostor: opened=${impostor.opened} closeCode=${impostor.closeCode} reason=${impostor.closeReason || '-'}; hostB still open=${hostB.closeCode === null}`;
    try { impostor.ws.close(); } catch (_) {}
  }
  record('6. a room cannot be stolen with a different rooms secret', ownershipPass, ownership);

  // 8. Same credential twice is the SAME game reconnecting - a RimWorld restart or
  // a dropped socket - so replacing the old socket is correct and must keep working.
  // This used to be the second half of the single-tenant problem, because handing a
  // friend your secret was the only way to let him host. It is not any more: he
  // registers and gets his own key (2a), so nobody has a reason to share one.
  // Runs last because it deliberately ends the owner room's host socket.
  const twinHost = await openHost(SECRET_A);
  await sleep(700);
  record('8. same credential reconnecting replaces its own socket',
    twinHost.opened === true && twinHost.closeCode === null && legacyHost.closeCode === 4002,
    `reconnect: opened=${twinHost.opened} closeCode=${twinHost.closeCode}; ` +
    `previous socket closed with ${legacyHost.closeCode} "${legacyHost.closeReason || '-'}" (4002 expected); ` +
    `host B unaffected: ${hostB.closeCode === null}`);
  try { twinHost.ws.close(); } catch (_) {}
  await sleep(300);

  // 9. Abandoned Join attempts must not permanently consume a room slot.
  // Measured failure this cost: a second run of THIS suite against a shared
  // registry died at registration with HTTP 503 "This relay is full", because
  // every room ever registered - hosted or not - was written to disk and restored
  // forever. On a real relay that means a handful of typos locks out every future
  // streamer. Rooms that never had a game in them are now never persisted.
  const before = await postJson('/api/host/register', { label: 'never hosted' });
  await postJson('/api/host/register', { label: 'also never hosted' });
  await sleep(400);
  let persisted = [];
  let persistErr = '';
  try {
    persisted = JSON.parse(require('fs').readFileSync(ROOMS_FILE, 'utf8'));
  } catch (e) { persistErr = e.message; }
  const hostedIds = persisted.map(r => r.roomId);
  record('9. rooms that never hosted are not persisted',
    before.code === 200 && Array.isArray(persisted) && !hostedIds.includes(before.json.roomId),
    `registry holds ${persisted.length} room(s): ${hostedIds.join(', ') || '(none)'}; ` +
    `the unhosted room ${before.json ? before.json.roomId : '?'} is ${hostedIds.includes(before.json && before.json.roomId) ? 'PRESENT (bug)' : 'absent'}` +
    (persistErr ? ` [read error: ${persistErr}]` : ''));

  // 7. Health still answers.
  const health = await getJson('/health');
  record('7. /health responds',
    health.code === 200 && health.json && health.json.ok === true,
    (health.raw || '').slice(0, 240));

  try { legacyHost.ws.close(); hostB.ws.close(); } catch (_) {}

  const pass = results.filter(r => r.pass === true).length;
  const fail = results.filter(r => r.pass === false).length;
  const skip = results.filter(r => r.pass === null).length;
  console.log(`\n${pass} passed, ${fail} failed, ${skip} skipped`);

  if (relayProc) relayProc.kill();
  try { require('fs').unlinkSync(ROOMS_FILE); } catch (_) {}
  process.exit(fail > 0 ? 1 : 0);
}

main().catch(e => {
  console.error('harness error:', e.message);
  if (relayProc) relayProc.kill();
  process.exit(2);
});
