'use strict';

const childProcess = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const http = require('http');
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

const { chromium } = requireMaybeGlobal('playwright');

const ROOT = path.resolve(__dirname, '..');
const REPO = path.resolve(ROOT, '..');
const PORT = 19000 + Math.floor(Math.random() * 1000);
const HOST_SECRET = `admin-smoke-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'admin_smoke_viewer';
const PAWN_ID = 7171;

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

async function waitFor(fn, label, timeoutMs = 10000) {
  const started = Date.now();
  let lastError = null;
  while (Date.now() - started < timeoutMs) {
    try {
      const value = await fn();
      if (value) return value;
    } catch (error) {
      lastError = error;
    }
    await wait(50);
  }
  throw new Error(`Timed out waiting for ${label}${lastError ? `: ${lastError.message}` : ''}`);
}

function requestJson(method, urlPath, body = null) {
  const payload = body ? JSON.stringify(body) : null;
  return new Promise((resolve, reject) => {
    const req = http.request(`${BASE_URL}${urlPath}`, {
      method,
      headers: {
        Authorization: `Bearer ${HOST_SECRET}`,
        ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}),
      },
    }, res => {
      let data = '';
      res.on('data', chunk => { data += chunk; });
      res.on('end', () => {
        if (res.statusCode < 200 || res.statusCode >= 300) return reject(new Error(`${method} ${urlPath} failed ${res.statusCode}: ${data}`));
        try { resolve(JSON.parse(data)); } catch { resolve({}); }
      });
    });
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

function openWs(url, label) {
  const ws = new WebSocket(url);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`${label} websocket did not open`)), 8000);
    ws.once('open', () => { clearTimeout(timer); resolve(ws); });
    ws.once('error', reject);
  });
}

function collect(ws) {
  const messages = [];
  ws.on('message', raw => {
    try { messages.push(JSON.parse(raw.toString('utf8'))); } catch {}
  });
  return messages;
}

async function chipValue(page, id) {
  return page.locator(`#${id} .chip-value`).textContent();
}

async function main() {
  const relay = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: ROOT,
    env: { ...process.env, PORT: String(PORT), HOST_SECRET, TWITCH_CLIENT_ID: '', LOG_TRAFFIC: '0', OPEN_HOSTING: '1' },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  let browser = null;
  let hostWs = null;
  let secondHostWs = null;
  let viewerWs = null;

  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'relay health', 15000);
    hostWs = await openWs(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`, 'host');
    const hostMessages = collect(hostWs);

    const session = await requestJson('POST', '/admin/viewer-session', {
      login: VIEWER_LOGIN,
      displayName: 'Admin Smoke Viewer',
      ttlMs: 10 * 60 * 1000,
    });
    viewerWs = await openWs(`${WS_URL}?role=viewer&session=${encodeURIComponent(session.sessionToken)}&build=admin-smoke`, 'viewer');

    hostWs.send(JSON.stringify({
      type: 'claim_request', username: VIEWER_LOGIN, displayName: 'Admin Smoke Viewer',
      pawnId: PAWN_ID, pawnName: 'Mira', adminOnly: true,
    }));
    hostWs.send(JSON.stringify({
      type: 'action_result', target: VIEWER_LOGIN, username: VIEWER_LOGIN,
      commandId: 'admin-smoke-failure', action: 'move', ok: false, message: 'Path blocked',
    }));
    hostWs.send(JSON.stringify({
      type: 'vote_update', active: true, question: 'Build a hospital?',
      options: [{ label: 'Yes', votes: 2 }, { label: 'No', votes: 1 }],
    }));
    hostWs.send(JSON.stringify({
      type: 'stream_marker', label: 'Raid survived', gameTick: 123456,
      day: 18, hour: 22, year: 5501, season: 'Jugust', mapName: 'Smoke Colony', adminOnly: true,
    }));

    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
    const adminResponse = await page.goto(`${BASE_URL}/admin`, { waitUntil: 'domcontentloaded' });
    if (!/(?:^|,)\s*no-store(?:,|$)/i.test(adminResponse?.headers()['cache-control'] || '')) {
      throw new Error(`Admin console cache policy is not no-store: ${adminResponse?.headers()['cache-control'] || 'missing'}`);
    }
    await page.fill('#secret-input', HOST_SECRET);
    await page.click('#btn-auth');
    await page.waitForSelector('#dashboard:not([style*="display:none"])');

    await waitFor(async () => (
      (await chipValue(page, 'chip-admin')) === 'Connected' &&
      (await chipValue(page, 'chip-host')) === 'Connected' &&
      (await chipValue(page, 'chip-claims')) === '1 pending' &&
      (await chipValue(page, 'chip-failures')) === '1 failed' &&
      (await chipValue(page, 'chip-vote')) === 'Active'
    ), 'replayed exception strip');

    const claimText = await page.locator('#claim-list').textContent();
    if (!claimText.includes('Admin Smoke Viewer') || !claimText.includes('Mira')) {
      throw new Error(`Pending claim did not replay into admin UI: ${claimText}`);
    }
    await waitFor(async () => (await page.locator('#marker-list').textContent()).includes('Raid survived'), 'replayed VOD marker');

    await page.fill('#marker-input', 'Trade ship arrived');
    await page.click('#btn-marker');
    await waitFor(() => hostMessages.find(msg => msg.type === 'command' && msg.action === 'mark_stream' && msg.label === 'Trade ship arrived'), 'host VOD marker command');

    await page.locator('#claim-list button', { hasText: 'Reject' }).click();
    await waitFor(() => hostMessages.find(msg => msg.type === 'claim_response' && msg.username === VIEWER_LOGIN), 'host claim_response');
    if ((await chipValue(page, 'chip-claims')) !== '1 pending') {
      throw new Error('Claim disappeared before the host confirmed resolution');
    }

    hostWs.send(JSON.stringify({
      type: 'claim_resolved', username: VIEWER_LOGIN, pawnId: PAWN_ID,
      resolution: 'rejected', assignedTo: '', adminOnly: true,
    }));
    await waitFor(async () => (await chipValue(page, 'chip-claims')) === 'None', 'host-confirmed claim resolution');

    const secondRoom = await requestJson('POST', '/api/host/register', { label: 'Other Smoke Room' });
    secondHostWs = await openWs(`${WS_URL}?role=host&room=${encodeURIComponent(secondRoom.roomId)}&secret=${encodeURIComponent(secondRoom.hostKey)}`, 'second host');
    secondHostWs.send(JSON.stringify({
      type: 'claim_request', username: 'other_room_viewer', displayName: 'Other Room Viewer',
      pawnId: 8181, pawnName: 'Not In This Room', adminOnly: true,
    }));
    await wait(300);
    if ((await chipValue(page, 'chip-claims')) !== 'None' || (await page.locator('#claim-list').textContent()).includes('Other Room Viewer')) {
      throw new Error('Owner-room admin received another room\'s pending claim');
    }

    hostWs.close();
    await waitFor(async () => (await chipValue(page, 'chip-host')) === 'Disconnected', 'host disconnect exception');

    await page.setViewportSize({ width: 480, height: 760 });
    const layout = await page.evaluate(() => ({
      viewport: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      chips: Array.from(document.querySelectorAll('.exception-chip')).filter(el => el.getBoundingClientRect().width > 0).length,
    }));
    if (layout.scrollWidth > layout.viewport || layout.chips !== 5) {
      throw new Error(`Narrow admin layout hid critical state: ${JSON.stringify(layout)}`);
    }

    const outputDir = path.join(REPO, 'output', 'playwright');
    fs.mkdirSync(outputDir, { recursive: true });
    const screenshot = path.join(outputDir, 'overlord-admin-exceptions.png');
    await page.screenshot({ path: screenshot, fullPage: true });

    console.log(JSON.stringify({
      ok: true,
      assertions: {
        pendingClaimReplayedAfterAdminOpen: true,
        recentFailureReplayed: true,
        activeVoteReplayed: true,
        streamMarkerReplayed: true,
        streamMarkerCommandReachedHost: true,
        claimActionNotOptimistic: true,
        hostResolutionClearedClaim: true,
        crossRoomAdminEventsIsolated: true,
        hostDisconnectVisible: true,
        narrowCriticalControlsVisible: true,
        adminConsoleNoStore: true,
      },
      layout,
      screenshot,
    }, null, 2));
  } finally {
    try { viewerWs?.close(); } catch {}
    try { secondHostWs?.close(); } catch {}
    try { hostWs?.close(); } catch {}
    if (browser) await browser.close().catch(() => {});
    relay.kill();
    await wait(100);
  }
}

main().catch(error => {
  console.error(error.stack || error.message);
  process.exit(1);
});
