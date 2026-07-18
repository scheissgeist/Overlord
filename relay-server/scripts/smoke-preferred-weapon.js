'use strict';

// Slice: preferred-weapon standing order. Boots a claimed viewer, feeds pawn
// state with a nearby WEAPON (has defName), opens the Gear tab, and asserts:
//   (1) the weapon row shows a "prefer" star (☆);
//   (2) clicking it sends {action:'set_preferred_weapon', defName:...} and the
//       star flips to ★ optimistically;
//   (3) clicking again clears it (defName:'').
// Client-only, no RimWorld host. Run: node scripts/smoke-preferred-weapon.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (24000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `prefwpn-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;

function wait(ms) { return new Promise(r => setTimeout(r, ms)); }
async function waitFor(fn, label, timeoutMs = 10000) {
  const start = Date.now(); let e0 = null;
  while (Date.now() - start < timeoutMs) { try { const v = await fn(); if (v) return v; } catch (e) { e0 = e; } await wait(100); }
  throw new Error(`Timed out waiting for ${label}${e0 ? ': ' + e0.message : ''}`);
}
function requestJson(method, urlPath, body = null) {
  const payload = body ? JSON.stringify(body) : null;
  return new Promise((resolve, reject) => {
    const req = http.request(`${BASE_URL}${urlPath}`, { method, headers: { Authorization: `Bearer ${HOST_SECRET}`, ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}) } }, res => {
      let d = ''; res.on('data', c => { d += c; }); res.on('end', () => { let j = null; try { j = d ? JSON.parse(d) : null; } catch (_) {} (res.statusCode >= 200 && res.statusCode < 300) ? resolve(j) : reject(new Error(`${method} ${urlPath} ${res.statusCode}: ${d}`)); });
    });
    req.on('error', reject); if (payload) req.write(payload); req.end();
  });
}
function waitForWsOpen(ws, label) { return new Promise((resolve, reject) => { const t = setTimeout(() => reject(new Error(`${label} ws timeout`)), 10000); ws.once('open', () => { clearTimeout(t); resolve(); }); ws.once('error', reject); }); }
function startRelay() { const c = childProcess.spawn(process.execPath, ['server.js'], { cwd: ROOT, env: { ...process.env, PORT: String(PORT), HOST_SECRET, TWITCH_CLIENT_ID: '', LOG_TRAFFIC: '0' }, stdio: ['ignore', 'pipe', 'pipe'] }); c.stdout.on('data', () => {}); c.stderr.on('data', () => {}); return { child: c }; }
function requireMaybeGlobal(name) { try { return require(name); } catch (e) { try { const r = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim(); return require(path.join(r, name)); } catch (_) { throw e; } } }
const { chromium } = requireMaybeGlobal('playwright');

async function main() {
  const relay = startRelay();
  let browser = null, hostWs = null;
  const failures = [];
  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'health', 15000);
    hostWs = new WebSocket(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
    const hostMessages = [];
    hostWs.on('message', raw => { try { hostMessages.push(JSON.parse(raw.toString('utf8'))); } catch (_) {} });
    await waitForWsOpen(hostWs, 'host');
    const session = await requestJson('POST', '/admin/viewer-session', { login: VIEWER_LOGIN, displayName: VIEWER_DISPLAY, ttlMs: 600000 });
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 900, height: 780 } });
    page.on('pageerror', e => console.error(`[pageerror] ${e.message}`));
    await page.addInitScript(id => sessionStorage.setItem('overlord_session', JSON.stringify(id)), { sessionToken: session.sessionToken, login: session.login, displayName: session.displayName });
    await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });
    await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === VIEWER_LOGIN), 'joined');
    hostWs.send(JSON.stringify({ type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4871', work: true, schedule: true, contextMenu: true, toolkitBridge: true }));
    hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY }] }));
    await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
    await page.click('.colonist-row .claim-btn');
    await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'claim_colonist'), 'claim');
    hostWs.send(JSON.stringify({ type: 'command_result', target: VIEWER_LOGIN, action: 'claim_colonist', ok: true, message: 'assigned' }));
    hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY, assignedTo: VIEWER_LOGIN.toUpperCase(), assignedDisplayName: VIEWER_DISPLAY }] }));
    hostWs.send(JSON.stringify({ type: 'pawn_state', target: VIEWER_LOGIN, state: JSON.stringify({
      id: PAWN_ID, name: VIEWER_DISPLAY, drafted: false, dead: false, downed: false, currentJob: 'Standing',
      weapon: { label: 'Steel knife', defName: 'MeleeWeapon_Knife', slotKey: 'weapon', hp: 88 }, apparel: [], inventory: [],
      nearbyEquipment: [ { id: 8001, label: 'Charge rifle', defName: 'Gun_ChargeRifle', type: 'weapon', slotKey: 'weapon', hp: 92, distance: 4 } ],
      needs: [], skills: [], traits: [], thoughts: [], health: { summaryHp: 1, hediffs: [] }, capacities: [], appearance: {},
    }) }));

    await page.click('[data-tab="gear"]');
    await page.waitForSelector('.gear-layout', { timeout: 10000 });
    // Weapon slot is the default; nearby source shows the charge rifle.
    await page.click('[data-gear-slot-select="weapon"]').catch(() => {});
    await page.click('[data-gear-source="nearby"]').catch(() => {});
    await page.waitForSelector('[data-prefer-weapon="Gun_ChargeRifle"]', { timeout: 8000 });

    // (1) star present and inactive (☆)
    let starTxt = await page.locator('[data-prefer-weapon="Gun_ChargeRifle"]').innerText();
    if (starTxt.trim() !== '☆') failures.push(`initial star should be ☆, got "${starTxt}"`);

    // (2) click → command sent with defName, star flips to ★
    await page.click('[data-prefer-weapon="Gun_ChargeRifle"]');
    const setMsg = await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'set_preferred_weapon' && m.defName === 'Gun_ChargeRifle'), 'set_preferred_weapon command');
    if (!setMsg) failures.push('no set_preferred_weapon command with defName');
    await wait(150);
    starTxt = await page.locator('[data-prefer-weapon="Gun_ChargeRifle"]').innerText();
    if (starTxt.trim() !== '★') failures.push(`after set, star should be ★, got "${starTxt}"`);

    // (3) click again → clears (defName '')
    const before = hostMessages.filter(m => m.type === 'command' && m.action === 'set_preferred_weapon').length;
    await page.click('[data-prefer-weapon="Gun_ChargeRifle"]');
    await waitFor(() => hostMessages.filter(m => m.type === 'command' && m.action === 'set_preferred_weapon').length > before, 'clear command');
    const clearMsg = hostMessages.filter(m => m.type === 'command' && m.action === 'set_preferred_weapon').pop();
    if (clearMsg.defName !== '') failures.push(`clear should send defName:'' , got "${clearMsg.defName}"`);
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (hostWs) hostWs.close();
    relay.child.kill();
  }
  if (failures.length) { console.error('PREFERRED-WEAPON SMOKE FAILED:\n  ' + failures.join('\n  ')); process.exit(1); }
  console.log('PREFERRED-WEAPON SMOKE PASSED: star toggles, set/clear commands sent with correct defName.');
}
main().catch(e => { console.error(e); process.exit(1); });
