#!/usr/bin/env node
/**
 * Proves durable identity without promoting the relay to game authority:
 *
 * - a registered host keeps the same room/key after a relay restart;
 * - a viewer keeps the same authenticated session token after restart;
 * - replay payloads remain in-memory and are NOT resurrected after restart.
 *
 * Every credential and identity is random. The relay gets a private temporary
 * data directory that is removed after the child processes stop.
 */

'use strict';

const childProcess = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const WebSocket = require('ws');

const PORT = parseInt(process.env.RESTART_SMOKE_PORT || '8098', 10);
const BASE = `http://127.0.0.1:${PORT}`;
const WS_BASE = `ws://127.0.0.1:${PORT}/ws`;
const ROOT = path.join(__dirname, '..');
const DATA_DIR = fs.mkdtempSync(path.join(os.tmpdir(), 'overlord-restart-smoke-'));
const ROOMS_FILE = path.join(DATA_DIR, 'rooms.json');
const SESSIONS_FILE = path.join(DATA_DIR, 'sessions.json');
const HOST_SECRET = crypto.randomBytes(24).toString('hex');
const VIEWER_NAME = `viewer_${crypto.randomBytes(4).toString('hex')}`;

let relay = null;
const results = [];

function record(name, pass, detail) {
  results.push({ name, pass });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}`);
  if (detail) console.log(`      ${detail}`);
}

function wait(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function request(method, pathname, body) {
  return new Promise(resolve => {
    const data = body === undefined ? null : JSON.stringify(body);
    const req = http.request({
      host: '127.0.0.1',
      port: PORT,
      path: pathname,
      method,
      headers: data ? {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(data),
      } : {},
    }, res => {
      let raw = '';
      res.on('data', chunk => { raw += chunk; });
      res.on('end', () => {
        let json = null;
        try { json = JSON.parse(raw); } catch (_) {}
        resolve({ code: res.statusCode, json, raw });
      });
    });
    req.on('error', error => resolve({ code: 0, json: null, raw: error.message }));
    req.setTimeout(5000, () => {
      req.destroy();
      resolve({ code: 0, json: null, raw: 'timeout' });
    });
    if (data) req.write(data);
    req.end();
  });
}

function openSocket(url) {
  return new Promise(resolve => {
    const ws = new WebSocket(url);
    const state = { ws, opened: false, closed: false, closeCode: null, messages: [] };
    ws.on('open', () => {
      state.opened = true;
      resolve(state);
    });
    ws.on('message', raw => {
      try { state.messages.push(JSON.parse(raw.toString())); } catch (_) {}
    });
    ws.on('close', code => {
      state.closed = true;
      state.closeCode = code;
      if (!state.opened) resolve(state);
    });
    ws.on('error', () => {
      if (!state.opened) resolve(state);
    });
    setTimeout(() => resolve(state), 5000);
  });
}

async function waitFor(predicate, label, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const value = await predicate();
    if (value) return value;
    await wait(40);
  }
  throw new Error(`Timed out waiting for ${label}`);
}

function startRelay() {
  return new Promise((resolve, reject) => {
    const child = childProcess.spawn(process.execPath, ['server.js'], {
      cwd: ROOT,
      env: {
        ...process.env,
        PORT: String(PORT),
        HOST_SECRET,
        TWITCH_CLIENT_ID: '',
        ALLOW_GUEST_LOGIN: '1',
        OPEN_HOSTING: '1',
        LOG_TRAFFIC: '0',
        LOG_DIR: DATA_DIR,
        ROOMS_FILE,
        SESSIONS_FILE,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    relay = child;
    let output = '';
    const onData = chunk => {
      output += chunk.toString();
      if (output.includes('listening on port')) resolve(child);
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);
    child.on('exit', code => {
      if (!output.includes('listening on port')) reject(new Error(`Relay exited ${code}:\n${output}`));
    });
    setTimeout(() => reject(new Error(`Relay did not start:\n${output}`)), 10000);
  });
}

function stopRelay() {
  return new Promise(resolve => {
    if (!relay || relay.exitCode !== null) return resolve();
    const child = relay;
    const done = () => resolve();
    child.once('exit', done);
    child.kill('SIGTERM');
    setTimeout(() => {
      if (child.exitCode === null) child.kill('SIGKILL');
      resolve();
    }, 5000);
  });
}

async function main() {
  let firstHost;
  let firstViewer;
  let secondHost;
  let secondViewer;
  try {
    await startRelay();

    const registration = await request('POST', '/api/host/register', { label: 'Restart smoke room' });
    const roomId = registration.json?.roomId;
    const hostKey = registration.json?.hostKey;
    record('room registration returns a durable identity',
      registration.code === 200 && !!roomId && !!hostKey,
      `HTTP ${registration.code}, room=${roomId || '-'}`);

    firstHost = await openSocket(`${WS_BASE}?role=host&key=${encodeURIComponent(hostKey || '')}`);
    const login = await request('POST', '/auth/guest', { name: VIEWER_NAME });
    const sessionToken = login.json?.sessionToken;
    firstViewer = await openSocket(`${WS_BASE}?role=viewer&session=${encodeURIComponent(sessionToken || '')}&room=${encodeURIComponent(roomId || '')}`);
    await waitFor(() => firstHost.messages.find(message => message.type === 'viewer_joined' && message.username === VIEWER_NAME), 'initial viewer_joined');
    record('host and viewer connect before restart',
      firstHost.opened && firstViewer.opened && login.code === 200,
      `host=${firstHost.opened}, viewer=${firstViewer.opened}, auth=${login.code}`);

    firstHost.ws.send(JSON.stringify({
      type: 'pawn_state',
      target: VIEWER_NAME,
      state: { name: 'must-not-survive-restart' },
    }));
    await waitFor(() => firstViewer.messages.find(message => message.type === 'pawn_state'), 'initial pawn_state');

    firstViewer.ws.close();
    firstHost.ws.close();
    await stopRelay();

    record('durable files were written',
      fs.existsSync(ROOMS_FILE) && fs.existsSync(SESSIONS_FILE),
      `rooms=${fs.existsSync(ROOMS_FILE)}, sessions=${fs.existsSync(SESSIONS_FILE)}`);

    await startRelay();
    secondHost = await openSocket(`${WS_BASE}?role=host&key=${encodeURIComponent(hostKey || '')}`);
    secondViewer = await openSocket(`${WS_BASE}?role=viewer&session=${encodeURIComponent(sessionToken || '')}&room=${encodeURIComponent(roomId || '')}`);
    await waitFor(() => secondHost.messages.find(message => message.type === 'viewer_joined' && message.username === VIEWER_NAME), 'restored viewer_joined');
    await wait(150);
    const health = await request('GET', '/health');
    const directory = await request('GET', '/api/rooms');
    const sameRoomLive = directory.json?.rooms?.some(room => room.roomId === roomId);

    record('same host key restores the same room after restart',
      secondHost.opened && !secondHost.closed && sameRoomLive,
      `host=${secondHost.opened}, closed=${secondHost.closed}, roomLive=${!!sameRoomLive}`);
    record('same viewer session token reconnects after restart',
      secondViewer.opened && health.json?.sessions === 1,
      `viewer=${secondViewer.opened}, sessions=${health.json?.sessions}`);

    await wait(300);
    const staleReplay = secondViewer.messages.some(message => message.type === 'pawn_state');
    record('restart does not replay stale game payloads', !staleReplay,
      staleReplay ? 'stale pawn_state was replayed' : 'identity survived; replay cache started empty');
  } finally {
    for (const state of [firstHost, firstViewer, secondHost, secondViewer]) {
      try { state?.ws?.close(); } catch (_) {}
    }
    await stopRelay();
    fs.rmSync(DATA_DIR, { recursive: true, force: true });
  }

  const failed = results.filter(result => !result.pass);
  console.log(`\n${results.length - failed.length} passed, ${failed.length} failed`);
  if (failed.length) process.exitCode = 1;
}

main().catch(error => {
  console.error(error.stack || error.message);
  process.exitCode = 1;
});
