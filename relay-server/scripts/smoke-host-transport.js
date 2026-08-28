#!/usr/bin/env node

'use strict';

const childProcess = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const WebSocket = require('ws');

const PORT = parseInt(process.env.HOST_SMOKE_PORT || '8099', 10);
const BASE = `http://127.0.0.1:${PORT}`;
const WS_BASE = `ws://127.0.0.1:${PORT}/ws`;
const ROOT = path.join(__dirname, '..');
const PROJECT = path.join(__dirname, 'host-transport-smoke', 'HostTransportSmoke.csproj');
const DATA_DIR = fs.mkdtempSync(path.join(os.tmpdir(), 'overlord-host-smoke-'));
const CLIENT_BUILD = fs.readFileSync(path.join(ROOT, 'public', 'app.js'), 'utf8')
  .match(/const\s+UI_BUILD\s*=\s*['"]([^'"]+)['"]/)?.[1] || '';
const HOST_SECRET = crypto.randomBytes(24).toString('hex');
const VIEWER = `clean_${crypto.randomBytes(4).toString('hex')}`;

let relay;
let host;
let viewer;
let hostState;
let relayOutput = '';

function request(method, pathname, body) {
  return new Promise(resolve => {
    const data = body === undefined ? null : JSON.stringify(body);
    const req = http.request({
      host: '127.0.0.1', port: PORT, path: pathname, method,
      headers: {
        Authorization: `Bearer ${HOST_SECRET}`,
        ...(data ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) } : {}),
      },
    }, res => {
      let raw = '';
      res.on('data', chunk => { raw += chunk; });
      res.on('end', () => {
        let json = null;
        try { json = JSON.parse(raw); } catch (_) {}
        resolve({ code: res.statusCode, json, raw });
      });
    });
    req.on('error', error => resolve({ code: 0, raw: error.message }));
    if (data) req.write(data);
    req.end();
  });
}

function waitFor(predicate, label, timeoutMs = 20000) {
  return new Promise((resolve, reject) => {
    const deadline = Date.now() + timeoutMs;
    const tick = () => {
      const value = predicate();
      if (value) return resolve(value);
      if (Date.now() >= deadline) return reject(new Error(`Timed out waiting for ${label}`));
      setTimeout(tick, 30);
    };
    tick();
  });
}

function startRelay() {
  return new Promise((resolve, reject) => {
    relay = childProcess.spawn(process.execPath, ['server.js'], {
      cwd: ROOT,
      env: {
        ...process.env,
        PORT: String(PORT), HOST_SECRET, TWITCH_CLIENT_ID: '',
        ALLOW_GUEST_LOGIN: '1', OPEN_HOSTING: '1', LOG_TRAFFIC: '0',
        LOG_DIR: DATA_DIR, ROOMS_FILE: path.join(DATA_DIR, 'rooms.json'), SESSIONS_FILE: '',
      },
      stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
    });
    const onData = chunk => {
      relayOutput += chunk.toString();
      if (relayOutput.includes('listening on port')) resolve();
    };
    relay.stdout.on('data', onData);
    relay.stderr.on('data', onData);
    relay.on('exit', code => reject(new Error(`Relay exited ${code}:\n${relayOutput}`)));
    setTimeout(() => reject(new Error(`Relay did not start:\n${relayOutput}`)), 10000);
  });
}

function startHost(hostKey) {
  host = childProcess.spawn('dotnet', ['run', '--project', PROJECT, '-c', 'Release', '--', BASE, hostKey, VIEWER], {
    cwd: ROOT, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  });
  const state = { output: '', error: '', exitCode: null };
  host.stdout.on('data', chunk => { state.output += chunk.toString(); });
  host.stderr.on('data', chunk => { state.error += chunk.toString(); });
  host.on('exit', code => { state.exitCode = code; });
  return state;
}

function openViewer(sessionToken, roomId) {
  return new Promise(resolve => {
    viewer = new WebSocket(`${WS_BASE}?role=viewer&session=${encodeURIComponent(sessionToken)}&room=${encodeURIComponent(roomId)}&build=${encodeURIComponent(CLIENT_BUILD)}`);
    const state = { messages: [] };
    viewer.on('message', raw => {
      try {
        const message = JSON.parse(raw.toString());
        if (message.type === 'batch' && Array.isArray(message.msgs)) state.messages.push(...message.msgs);
        else state.messages.push(message);
      } catch (_) {}
    });
    viewer.on('open', () => resolve(state));
    viewer.on('error', error => { state.error = error; resolve(state); });
  });
}

async function stop(child) {
  if (!child || child.exitCode !== null) return;
  await new Promise(resolve => {
    child.once('exit', resolve);
    child.kill('SIGTERM');
    setTimeout(resolve, 3000);
  });
}

async function main() {
  try {
    await startRelay();
    const registration = await request('POST', '/api/host/register', { label: 'Clean profile smoke' });
    if (registration.code !== 200 || !registration.json?.hostKey) throw new Error(`Registration failed: ${registration.raw}`);

    hostState = startHost(registration.json.hostKey);
    await waitFor(() => hostState.output.includes('HOST_CONNECTED'), 'production C# RelayClient connection');

    const login = await request('POST', '/auth/guest', { name: VIEWER });
    if (login.code !== 200 || !login.json?.sessionToken) throw new Error(`Guest login failed: ${login.raw}`);
    const viewerState = await openViewer(login.json.sessionToken, registration.json.roomId);
    if (viewerState.error) throw viewerState.error;

    let capabilities;
    try {
      capabilities = await waitFor(
        () => viewerState.messages.find(message => message.type === 'host_capabilities'),
        'host capabilities from production C# transport'
      );
    } catch (error) {
      const logs = await request('GET', '/admin/logs?limit=30');
      throw new Error(`${error.message}; viewer=${JSON.stringify(viewerState.messages)}; ops=${JSON.stringify(logs.json?.logs || [])}`);
    }
    const pawnState = await waitFor(
      () => viewerState.messages.find(message => message.type === 'pawn_state'),
      'pawn state from production C# serializer'
    );
    if (capabilities.rimworldVersion !== 'smoke-real-host-transport') throw new Error('Wrong host capabilities payload');
    if (pawnState.state?.id !== 4242 || pawnState.state?.name !== 'Clean Profile') throw new Error('Wrong pawn state payload');
    if (pawnState.preferredWeapon !== 'Gun_SmokeRifle') throw new Error('Production pawn_state omitted preferredWeapon');

    viewer.send(JSON.stringify({ type: 'command', action: 'draft', username: 'forged', source: 'admin', adminCommand: true }));
    await waitFor(() => hostState.output.includes('HOST_COMMAND_RECEIVED'), 'viewer command at production C# host');
    await waitFor(() => hostState.exitCode !== null, 'C# host smoke exit');
    if (hostState.exitCode !== 0) throw new Error(`C# host failed (${hostState.exitCode}):\n${hostState.output}\n${hostState.error}`);

    console.log(JSON.stringify({
      ok: true,
      transport: 'production RelayClient + JsonHelper + StateProtocol',
      room: registration.json.roomId,
      viewer: VIEWER,
      assertions: {
        cleanRoomRegistered: true,
        csharpHostConnected: true,
        viewerReceivedHostCapabilities: true,
        viewerReceivedPawnState: true,
        requiredPawnStateFieldsPresent: true,
        hostReceivedPinnedViewerCommand: true,
      },
    }, null, 2));
  } finally {
    try { viewer?.close(); } catch (_) {}
    await stop(host);
    await stop(relay);
    fs.rmSync(DATA_DIR, { recursive: true, force: true });
  }
}

main().catch(error => {
  console.error(error.stack || error.message);
  if (hostState) {
    console.error('C# host stdout:\n' + hostState.output);
    console.error('C# host stderr:\n' + hostState.error);
    console.error('C# host exit: ' + hostState.exitCode);
  }
  console.error('Relay output:\n' + relayOutput);
  process.exitCode = 1;
});
