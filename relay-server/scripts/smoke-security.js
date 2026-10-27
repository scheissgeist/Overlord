#!/usr/bin/env node
/**
 * Regression checks for the 2026-10-27 security audit
 * (docs/SECURITY_AUDIT_2026_10_27.md). Starts a private relay with random
 * credentials, plays the attacker, and asserts the relay holds.
 *
 * The expired-session check waits ~62s for a 60s session to lapse; set
 * SECURITY_SMOKE_FAST=1 to skip it.
 */

'use strict';

const childProcess = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const WebSocket = require('ws');

function requireMaybeGlobal(name) {
  try {
    return require(name);
  } catch (localError) {
    try {
      const npmRoot = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim();
      return require(path.join(npmRoot, name));
    } catch {
      throw localError;
    }
  }
}

const PORT = parseInt(process.env.SECURITY_SMOKE_PORT || '8097', 10);
const BASE = `http://127.0.0.1:${PORT}`;
const WS_BASE = `ws://127.0.0.1:${PORT}/ws`;
const ROOT = path.join(__dirname, '..');
const DATA_DIR = fs.mkdtempSync(path.join(os.tmpdir(), 'overlord-security-smoke-'));
const HOST_SECRET = crypto.randomBytes(24).toString('hex');
const FAST = process.env.SECURITY_SMOKE_FAST === '1';

let relay = null;
let relayExited = false;
let relayOutput = '';
const results = [];

function record(name, pass, detail) {
  results.push({ name, pass });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}`);
  if (detail && !pass) console.log(`      ${detail}`);
}

function wait(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function request(method, pathname, body, headers = {}) {
  const opts = { method, headers: { ...headers } };
  if (body !== undefined) {
    opts.headers['Content-Type'] = 'application/json';
    opts.body = typeof body === 'string' ? body : JSON.stringify(body);
  }
  try {
    const res = await fetch(BASE + pathname, opts);
    const raw = await res.text();
    let json = null;
    try { json = JSON.parse(raw); } catch (_) {}
    return { code: res.status, json, raw, headers: res.headers };
  } catch (error) {
    return { code: 0, json: null, raw: error.message, headers: new Headers() };
  }
}

function admin(method, pathname, body) {
  return request(method, pathname, body, { Authorization: `Bearer ${HOST_SECRET}` });
}

/** Opens a socket and collects every message; resolves once open or closed. */
function openSocket(query) {
  return new Promise(resolve => {
    const ws = new WebSocket(`${WS_BASE}?${query}`);
    const state = { ws, messages: [], closed: false, closeCode: 0 };
    ws.on('message', (data, isBinary) => {
      if (isBinary) return;
      try { state.messages.push(JSON.parse(data.toString())); } catch (_) {}
    });
    ws.on('open', () => resolve(state));
    ws.on('close', code => { state.closed = true; state.closeCode = code; resolve(state); });
    ws.on('error', () => {});
  });
}

async function mintSession(login, ttlMs) {
  const res = await admin('POST', '/admin/viewer-session', { login, ttlMs });
  return res.json && res.json.sessionToken;
}

async function startRelay(extraEnv = {}) {
  relayExited = false;
  relayOutput = '';
  relay = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: ROOT,
    env: {
      ...process.env,
      PORT: String(PORT),
      HOST_SECRET,
      TWITCH_CLIENT_ID: '',
      ALLOW_GUEST_LOGIN: '1',
      OPEN_HOSTING: '1',
      LOG_DIR: path.join(DATA_DIR, 'logs'),
      ROOMS_FILE: path.join(DATA_DIR, 'rooms.json'),
      STREAM_MARKERS_FILE: path.join(DATA_DIR, 'markers.jsonl'),
      ...extraEnv,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  relay.stdout.on('data', d => { relayOutput += d; });
  relay.stderr.on('data', d => { relayOutput += d; });
  relay.on('exit', () => { relayExited = true; });
  for (let i = 0; i < 50; i++) {
    const res = await request('GET', '/health');
    if (res.code === 200) return;
    await wait(100);
  }
  throw new Error('relay did not start:\n' + relayOutput);
}

async function stopRelay() {
  if (relay && !relayExited) {
    relay.kill();
    await new Promise(resolve => relay.once('exit', resolve));
  }
}

// ── Checks ────────────────────────────────────────────────────────────────────

async function checkNestedIdentityKeys() {
  const host = await openSocket(`role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
  const token = await mintSession('attacker', 3600 * 1000);
  const viewer = await openSocket(`role=viewer&session=${token}`);
  await wait(200);
  host.messages.length = 0;

  viewer.ws.send(JSON.stringify({
    0: { type: 'viewer_joined', username: 'victim', displayName: 'spoofed' },
    type: 'chat',
    message: 'nested',
  }));
  viewer.ws.send(JSON.stringify({ type: 'chat', message: 'plain' }));
  await wait(300);

  const chats = host.messages.filter(m => m.type === 'chat').map(m => m.message);
  record('viewer message hiding a nested type/username is not forwarded to the host',
    !chats.includes('nested'), `host received: ${JSON.stringify(chats)}`);
  record('ordinary viewer chat still reaches the host',
    chats.includes('plain'), `host received: ${JSON.stringify(chats)}`);
  viewer.ws.close();
  host.ws.close();
  await wait(100);
}

// A hostile host (any streamer, when OPEN_HOSTING is on) sends script payloads in
// every field the operator consoles render. Neither page may run them: both hold
// HOST_SECRET (admin.html in sessionStorage, obs.html in its URL).
const XSS = '<img src=x onerror="window.__pwned=(window.__pwned||0)+1">';
const QUOTE_BREAK = `x');window.__pwned=(window.__pwned||0)+1;//"'`;

function hostileHostMessages(viewerLogin) {
  return [
    { type: 'game_info', season: XSS, day: XSS, hour: XSS, temperature: XSS, mapName: XSS },
    { type: 'colonist_list', colonists: [{ id: `1)${XSS}`, name: XSS, assignedTo: viewerLogin }] },
    { type: 'claim_request', username: QUOTE_BREAK, displayName: XSS, pawnId: `1);window.__pwned=1;//`, pawnName: XSS },
    { type: 'vote_update', active: true, question: XSS, options: [{ label: XSS, votes: XSS }] },
    { type: 'pawn_died', pawnName: XSS, username: XSS },
  ];
}

async function checkConsoleXss() {
  let chromium;
  try { ({ chromium } = requireMaybeGlobal('playwright')); } catch (_) {
    record('operator consoles escape host data (playwright not installed)', false, 'install playwright to run this check');
    return;
  }
  const host = await openSocket(`role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
  const token = await mintSession('xss_viewer', 3600 * 1000);
  const viewer = await openSocket(`role=viewer&session=${token}`);
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    await context.addInitScript(secret => {
      if (location.pathname === '/admin') sessionStorage.setItem('overlord_admin_secret', secret);
    }, HOST_SECRET);
    const adminPage = await context.newPage();
    await adminPage.goto(`${BASE}/admin`);
    await adminPage.waitForSelector('#dashboard', { state: 'visible', timeout: 10000 });
    const obsPage = await context.newPage();
    await obsPage.goto(`${BASE}/obs?secret=${encodeURIComponent(HOST_SECRET)}`);
    await wait(800);

    for (const msg of hostileHostMessages('xss_viewer')) host.ws.send(JSON.stringify(msg));
    await wait(800);
    // The claim buttons used inline onclick handlers built from host strings.
    const approve = adminPage.locator('#claim-list button').first();
    if (await approve.count()) await approve.click().catch(() => {});
    await adminPage.locator('#colonist-list .colonist-card').first().click().catch(() => {});
    await wait(300);

    const adminPwned = await adminPage.evaluate(() => window.__pwned || 0);
    const obsPwned = await obsPage.evaluate(() => window.__pwned || 0);
    const claimText = await adminPage.locator('#claim-list').innerText().catch(() => '');
    record('admin console does not execute host-sent markup', adminPwned === 0, `__pwned=${adminPwned}`);
    record('admin console still renders the hostile claim as text', claimText.includes('<img'), `claim list: ${claimText.slice(0, 120)}`);
    record('OBS overlay does not execute host-sent markup', obsPwned === 0, `__pwned=${obsPwned}`);
  } finally {
    await browser.close();
    viewer.ws.close();
    host.ws.close();
    await wait(100);
  }
}

async function checkViewerXss() {
  let chromium;
  try { ({ chromium } = requireMaybeGlobal('playwright')); } catch (_) {
    record('viewer page escapes host data (playwright not installed)', false, 'install playwright to run this check');
    return;
  }
  const host = await openSocket(`role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
  const login = 'xss_target';
  const res = await admin('POST', '/admin/viewer-session', { login, ttlMs: 3600 * 1000 });
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    await context.addInitScript(session => {
      localStorage.setItem('overlord_session', JSON.stringify(session));
    }, res.json);
    const page = await context.newPage();
    await page.goto(`${BASE}/`);
    // Wait until the relay has told the host about this viewer, then attack it.
    for (let i = 0; i < 50 && !host.messages.some(m => m.type === 'viewer_joined' && m.username === login); i++) {
      await wait(100);
    }
    host.ws.send(JSON.stringify({ type: 'game_info', season: XSS, day: XSS, year: XSS, hour: XSS, temperature: XSS, speed: XSS, mapName: XSS }));
    host.ws.send(JSON.stringify({ type: 'vote_update', active: true, question: 'q', options: [{ label: 'a', votes: XSS }] }));
    await wait(1000);
    const pwned = await page.evaluate(() => window.__pwned || 0);
    const info = await page.locator('#game-info').innerText().catch(() => '');
    record('viewer page does not execute host-sent game_info / vote markup', pwned === 0, `__pwned=${pwned}`);
    record('viewer page shows the game_info text', info.includes('<img'), `#game-info: ${info.slice(0, 120)}`);
  } finally {
    await browser.close();
    host.ws.close();
    await wait(100);
  }
}

async function checkExpiredSession() {
  if (FAST) { console.log('SKIP  expired session (SECURITY_SMOKE_FAST=1)'); return; }
  const token = await mintSession('sleeper', 60 * 1000);
  console.log('      waiting 62s for a 60s session to expire...');
  await wait(62 * 1000);
  const viewer = await openSocket(`role=viewer&session=${token}`);
  await wait(500);
  record('expired session token is refused without crashing the relay',
    !relayExited && viewer.closed && viewer.closeCode === 4003,
    `relayExited=${relayExited} closeCode=${viewer.closeCode}`);
}

async function main() {
  await startRelay();
  try {
    await checkNestedIdentityKeys();
    await checkConsoleXss();
    await checkViewerXss();
    await checkExpiredSession();
  } finally {
    await stopRelay();
    fs.rmSync(DATA_DIR, { recursive: true, force: true });
  }
  const failed = results.filter(r => !r.pass).length;
  console.log(`\n${results.length - failed}/${results.length} security checks passed`);
  process.exit(failed ? 1 : 0);
}

main().catch(error => {
  console.error(error);
  stopRelay().finally(() => process.exit(1));
});
