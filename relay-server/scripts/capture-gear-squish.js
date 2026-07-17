'use strict';

// Focused capture harness: render the Gear panel with LONG real RimWorld item
// names at true phone widths and desktop, screenshot each, and report per-row
// horizontal overflow / name truncation. Purpose: SEE the "squished text" bug
// the streamer's viewers hit before touching CSS, then re-run to verify the fix.
//
// Client-only measurement infra — boots the relay + a headless viewer, pushes
// synthetic pawn state. No RimWorld host, no main-thread cost (north-star safe).
// Reuses the relay-boot + viewer-join + claim pattern from smoke-viewer-intake.js.
// Run: OVERLORD_SMOKE_SCREENSHOTS=1 node scripts/capture-gear-squish.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (21000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `gearcap-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;
const OUT_DIR = path.resolve(ROOT, '..', 'output', 'playwright');

const WIDTHS = [
  { name: 'phone-360', width: 360, height: 780 },
  { name: 'phone-390', width: 390, height: 844 },
  { name: 'desktop-1280', width: 1280, height: 720 },
];

// Long, real RimWorld labels — the squish only shows with realistic content,
// including quality prefixes and stuff prefixes the game actually produces.
const PAWN_STATE = {
  id: PAWN_ID,
  name: VIEWER_DISPLAY,
  fullName: `${VIEWER_DISPLAY} 'Longshot' McTavish`,
  drafted: false, dead: false, downed: false, posX: 100, posZ: 100,
  weapon: { label: 'Masterwork plasteel longsword', defName: 'MeleeWeapon_LongSword', slotKey: 'weapon', hp: 91 },
  apparel: [
    { id: 7001, label: 'Masterwork cataphract armor', defName: 'Apparel_ArmorCataphract', slotKey: 'outer', hp: 88, dyeable: false },
    { id: 7002, label: 'Excellent prestige cataphract helmet', defName: 'Apparel_ArmorCataphractHelmetPrestige', slotKey: 'head', hp: 79, dyeable: false },
    { id: 7003, label: 'Devilstrand button-down shirt', defName: 'Apparel_CollarShirt', slotKey: 'torso', hp: 94, dyeable: true, color: '#7a5b3a' },
    { id: 7004, label: 'Thrumbofur cowboy hat', defName: 'Apparel_CowboyHat', slotKey: 'head', hp: 62, dyeable: true, color: '#4a3b2a' },
  ],
  inventory: [
    { id: 7101, label: 'Packaged survival meal', defName: 'MealSurvivalPack', count: 12, hp: 100, ingestible: true },
    { id: 7102, label: 'Glitterworld medicine', defName: 'MedicineUltratech', count: 8, hp: 100, ingestible: false },
    { id: 7103, label: 'Component', defName: 'ComponentIndustrial', count: 40, hp: 100, ingestible: false },
  ],
  nearbyEquipment: [
    { id: 8001, label: 'Excellent charge rifle', defName: 'Gun_ChargeRifle', type: 'weapon', slotKey: 'weapon', hp: 92, distance: 4 },
    { id: 8002, label: 'Legendary marine armor (plasteel)', defName: 'Apparel_PowerArmor', type: 'apparel', slotKey: 'outer', hp: 100, distance: 7 },
    { id: 8003, label: 'Awful reinforced flak trousers', defName: 'Apparel_FlakPants', type: 'apparel', slotKey: 'legs', hp: 41, distance: 9 },
  ],
  needs: [], skills: [], traits: [], thoughts: [],
  health: { summaryHp: 1.0, painLevel: 0, hediffs: [] },
  capacities: [], relations: [], opinions: [], workPriorities: [], schedule: [],
  appearance: { hairDef: 'Bald', gender: 'Male' },
  currentJob: 'Standing',
};

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
      headers: {
        Authorization: `Bearer ${HOST_SECRET}`,
        ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}),
      },
    }, res => {
      let data = ''; res.on('data', c => { data += c; });
      res.on('end', () => {
        let json = null; try { json = data ? JSON.parse(data) : null; } catch (_) {}
        if (res.statusCode < 200 || res.statusCode >= 300) return reject(new Error(`${method} ${urlPath} ${res.statusCode}: ${data}`));
        resolve(json);
      });
    });
    req.on('error', reject); if (payload) req.write(payload); req.end();
  });
}
function waitForWsOpen(ws, label) {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`${label} ws did not open`)), 10000);
    ws.once('open', () => { clearTimeout(t); resolve(); });
    ws.once('error', reject);
  });
}
function startRelay() {
  const child = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: ROOT,
    env: { ...process.env, PORT: String(PORT), HOST_SECRET, TWITCH_CLIENT_ID: '', LOG_TRAFFIC: '0' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const output = [];
  child.stdout.on('data', c => output.push(c.toString()));
  child.stderr.on('data', c => output.push(c.toString()));
  return { child, output };
}

function requireMaybeGlobal(name) {
  try { return require(name); }
  catch (e) {
    try { const r = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim(); return require(path.join(r, name)); }
    catch (_) { throw e; }
  }
}
const { chromium } = requireMaybeGlobal('playwright');

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const relay = startRelay();
  let browser = null, hostWs = null;
  const findings = [];
  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'relay health', 15000);
    hostWs = new WebSocket(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
    const hostMessages = [];
    hostWs.on('message', raw => { try { hostMessages.push(JSON.parse(raw.toString('utf8'))); } catch (_) {} });
    await waitForWsOpen(hostWs, 'host');

    const session = await requestJson('POST', '/admin/viewer-session', { login: VIEWER_LOGIN, displayName: VIEWER_DISPLAY, ttlMs: 600000 });
    browser = await chromium.launch({ headless: true });

    for (const vp of WIDTHS) {
      const page = await browser.newPage({ viewport: { width: vp.width, height: vp.height } });
      page.on('pageerror', e => console.error(`[pageerror ${vp.name}] ${e.message}`));
      await page.addInitScript(id => sessionStorage.setItem('overlord_session', JSON.stringify(id)),
        { sessionToken: session.sessionToken, login: session.login, displayName: session.displayName });
      await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });
      await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === VIEWER_LOGIN), `viewer_joined ${vp.name}`);

      hostWs.send(JSON.stringify({ type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4871', work: true, schedule: true, contextMenu: true, toolkitBridge: true }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY }] }));
      await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
      await page.click('.colonist-row .claim-btn');
      await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'claim_colonist' && m.username === VIEWER_LOGIN), `claim ${vp.name}`);
      hostWs.send(JSON.stringify({ type: 'command_result', target: VIEWER_LOGIN, action: 'claim_colonist', ok: true, message: 'assigned' }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY, assignedTo: VIEWER_LOGIN.toUpperCase(), assignedDisplayName: VIEWER_DISPLAY }] }));
      hostWs.send(JSON.stringify({ type: 'pawn_state', target: VIEWER_LOGIN, state: JSON.stringify(PAWN_STATE) }));

      await page.click('[data-tab="gear"]');
      await page.waitForSelector('.gear-layout', { timeout: 10000 });
      await wait(400);

      // Measure overflow + truncation
      const report = await page.evaluate(() => {
        const docOverflow = document.documentElement.scrollWidth - document.documentElement.clientWidth;
        const truncated = [];
        document.querySelectorAll('.gear-name, .gear-slot-name, .gear-worn-row').forEach(el => {
          if (el.scrollWidth > el.clientWidth + 1) truncated.push({ t: el.textContent.trim().slice(0, 48), sw: el.scrollWidth, cw: el.clientWidth });
        });
        const gl = document.getElementById('gear-list');
        return {
          docOverflowPx: docOverflow,
          gearListOverflow: gl ? gl.scrollWidth - gl.clientWidth : null,
          truncatedCount: truncated.length,
          truncatedSample: truncated.slice(0, 8),
        };
      });
      findings.push({ vp: vp.name, ...report });

      if (process.env.OVERLORD_SMOKE_SCREENSHOTS !== '0') {
        const target = await page.$('#bottom-panel') || page;
        await target.screenshot({ path: path.join(OUT_DIR, `gear-squish-${vp.name}.png`) });
      }
      await page.close();
    }
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (hostWs) hostWs.close();
    relay.child.kill();
  }
  console.log(JSON.stringify(findings, null, 2));
  const anyBad = findings.some(f => f.docOverflowPx > 0 || (f.gearListOverflow || 0) > 0 || f.truncatedCount > 0);
  console.log(anyBad ? '\nSQUISH DETECTED (overflow or truncated names present)' : '\nNo squish: no overflow, no truncated names.');
}

main().catch(e => { console.error(e); process.exit(1); });
