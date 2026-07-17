'use strict';

// Full-viewport capture: claim a pawn, deliver a real portrait, and screenshot
// the WHOLE viewer page at desktop + phone widths so we can SEE where the
// colonist profile / portrait actually sits (report: "floating off to the top
// right"). Also reports the bounding box of the inspect pane + each portrait img.
// Client-only, no RimWorld host. Run: node scripts/capture-viewer-layout.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (23000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `layout-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;
const OUT_DIR = path.resolve(ROOT, '..', 'output', 'playwright');

// A real 2x2 PNG (not 1x1) so display:block images have measurable size.
const PORTRAIT_PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEklEQVR42mNk+M9Qz0BkYBxVSAwAxk0F/QIWr7wAAAAASUVORK5CYII=';

const WIDTHS = [
  { name: 'desktop-1280', width: 1280, height: 720 },
  { name: 'phone-390', width: 390, height: 844 },
];

function wait(ms) { return new Promise(r => setTimeout(r, ms)); }
async function waitFor(fn, label, timeoutMs = 10000) {
  const start = Date.now(); let lastError = null;
  while (Date.now() - start < timeoutMs) {
    try { const v = await fn(); if (v) return v; } catch (e) { lastError = e; }
    await wait(100);
  }
  throw new Error(`Timed out waiting for ${label}${lastError ? ': ' + lastError.message : ''}`);
}
function requestJson(method, urlPath, body = null) {
  const payload = body ? JSON.stringify(body) : null;
  return new Promise((resolve, reject) => {
    const req = http.request(`${BASE_URL}${urlPath}`, {
      method,
      headers: { Authorization: `Bearer ${HOST_SECRET}`, ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}) },
    }, res => {
      let data = ''; res.on('data', c => { data += c; });
      res.on('end', () => { let json = null; try { json = data ? JSON.parse(data) : null; } catch (_) {} (res.statusCode >= 200 && res.statusCode < 300) ? resolve(json) : reject(new Error(`${method} ${urlPath} ${res.statusCode}: ${data}`)); });
    });
    req.on('error', reject); if (payload) req.write(payload); req.end();
  });
}
function waitForWsOpen(ws, label) {
  return new Promise((resolve, reject) => { const t = setTimeout(() => reject(new Error(`${label} ws timeout`)), 10000); ws.once('open', () => { clearTimeout(t); resolve(); }); ws.once('error', reject); });
}
function startRelay() {
  const child = childProcess.spawn(process.execPath, ['server.js'], { cwd: ROOT, env: { ...process.env, PORT: String(PORT), HOST_SECRET, TWITCH_CLIENT_ID: '', LOG_TRAFFIC: '0' }, stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout.on('data', () => {}); child.stderr.on('data', () => {});
  return { child };
}
function requireMaybeGlobal(name) { try { return require(name); } catch (e) { try { const r = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim(); return require(path.join(r, name)); } catch (_) { throw e; } } }
const { chromium } = requireMaybeGlobal('playwright');

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const relay = startRelay();
  let browser = null, hostWs = null;
  const report = [];
  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'health', 15000);
    hostWs = new WebSocket(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
    const hostMessages = [];
    hostWs.on('message', raw => { try { hostMessages.push(JSON.parse(raw.toString('utf8'))); } catch (_) {} });
    await waitForWsOpen(hostWs, 'host');
    const session = await requestJson('POST', '/admin/viewer-session', { login: VIEWER_LOGIN, displayName: VIEWER_DISPLAY, ttlMs: 600000 });
    browser = await chromium.launch({ headless: true });

    for (const vp of WIDTHS) {
      const page = await browser.newPage({ viewport: { width: vp.width, height: vp.height } });
      page.on('pageerror', e => console.error(`[pageerror ${vp.name}] ${e.message}`));
      await page.addInitScript(id => sessionStorage.setItem('overlord_session', JSON.stringify(id)), { sessionToken: session.sessionToken, login: session.login, displayName: session.displayName });
      await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });
      await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === VIEWER_LOGIN), `joined ${vp.name}`);
      hostWs.send(JSON.stringify({ type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4871', work: true, schedule: true, contextMenu: true, toolkitBridge: true }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY }] }));
      await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
      await page.click('.colonist-row .claim-btn');
      await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'claim_colonist'), `claim ${vp.name}`);
      hostWs.send(JSON.stringify({ type: 'command_result', target: VIEWER_LOGIN, action: 'claim_colonist', ok: true, message: 'assigned' }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY, assignedTo: VIEWER_LOGIN.toUpperCase(), assignedDisplayName: VIEWER_DISPLAY }] }));
      hostWs.send(JSON.stringify({ type: 'pawn_state', target: VIEWER_LOGIN, state: JSON.stringify({ id: PAWN_ID, name: VIEWER_DISPLAY, fullName: VIEWER_DISPLAY, drafted: false, dead: false, downed: false, posX: 10, posZ: 10, currentJob: 'Standing', weapon: null, apparel: [], inventory: [], nearbyEquipment: [], needs: [{ key: 'food', label: 'Food', value: 0.7 }], skills: [], traits: [], thoughts: [], health: { summaryHp: 1, hediffs: [] }, capacities: [], appearance: {} }) }));
      // Deliver the portrait — this is the element the report says floats.
      hostWs.send(JSON.stringify({ type: 'pawn_portrait', target: VIEWER_LOGIN, data: PORTRAIT_PNG }));
      await wait(500);

      // Also test the EXPANDED bottom-drawer state (mobile has a distinct
      // #inspect-pane override with contain:paint when the drawer is open).
      await page.evaluate(() => {
        const open = document.getElementById('btn-drawer-open');
        if (open) open.click();
      });
      await wait(600);

      const boxes = await page.evaluate(() => {
        const vw = window.innerWidth, vh = window.innerHeight;
        const grab = (sel) => {
          const el = document.querySelector(sel);
          if (!el) return null;
          const r = el.getBoundingClientRect();
          const cs = getComputedStyle(el);
          return { sel, x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height), pos: cs.position, display: cs.display, vw, vh,
            offRight: Math.round(r.right) > vw + 1, nearTop: r.y < vh * 0.33, nearRight: r.x > vw * 0.5 };
        };
        return {
          inspectPane: grab('#inspect-pane'),
          pawnPortrait: grab('#pawn-portrait'),
          portraitBox: grab('.inspect-portrait'),
          cmdPortrait: grab('#command-portrait-img'),
          healthPortrait: grab('#health-pawn-portrait'),
        };
      });
      report.push({ vp: vp.name, boxes });
      await page.screenshot({ path: path.join(OUT_DIR, `viewer-layout-${vp.name}.png`), fullPage: false });
      await page.close();
    }
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (hostWs) hostWs.close();
    relay.child.kill();
  }
  console.log(JSON.stringify(report, null, 2));
}
main().catch(e => { console.error(e); process.exit(1); });
