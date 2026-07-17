'use strict';

// Slice-1 verification: Buy & Equip. Boots the relay + a claimed viewer, feeds a
// toolkit_state whose item entries carry the real host's isWeapon/isApparel flags,
// then asserts:
//   (1) weapons/apparel show a second "Buy & Equip" button; resources/buildables do NOT.
//   (2) clicking Buy & Equip sends a toolkit_purchase command with equipToPawn:true
//       (plus the chosen material as argument), while plain Buy does not.
// Client-only measurement infra (no RimWorld host), north-star safe.
// Run: OVERLORD_SMOKE_SCREENSHOTS=1 node scripts/smoke-buy-equip.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (22000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `buyequip-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;
const OUT_DIR = path.resolve(ROOT, '..', 'output', 'playwright');

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
  catch (e) { try { const r = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim(); return require(path.join(r, name)); } catch (_) { throw e; } }
}
const { chromium } = requireMaybeGlobal('playwright');

// Item entries WITH the real host's isWeapon/isApparel flags.
const ENTRIES = [
  { kind: 'item', category: 'weapons', sku: 'chargerifle', label: 'charge rifle', defName: 'Gun_ChargeRifle', cost: 1200, price: 1200, unitCost: 1200, affordable: true, needsInput: false, isWeapon: true, isApparel: false, command: '!buy chargerifle' },
  { kind: 'item', category: 'apparel', sku: 'flakvest', label: 'flak vest', defName: 'Apparel_FlakVest', cost: 1600, price: 1600, unitCost: 1600, affordable: true, needsInput: false, isWeapon: false, isApparel: true, madeFromStuff: true, stuffOptions: [{ defName: 'Steel', label: 'steel', category: 'Metallic' }, { defName: 'Plasteel', label: 'plasteel', category: 'Metallic' }], command: '!buy flakvest' },
  { kind: 'item', category: 'materials', sku: 'steel', label: 'steel', defName: 'Steel', cost: 30, price: 30, unitCost: 30, affordable: true, needsInput: false, isWeapon: false, isApparel: false, command: '!buy steel' },
  { kind: 'item', category: 'buildables', sku: 'wall', label: 'wall', cost: 45, price: 45, unitCost: 45, affordable: true, needsInput: false, isWeapon: false, isApparel: false, isBuildable: true, command: '!buy wall' },
];

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const relay = startRelay();
  let browser = null, hostWs = null;
  const failures = [];
  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'relay health', 15000);
    hostWs = new WebSocket(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
    const hostMessages = [];
    hostWs.on('message', raw => { try { hostMessages.push(JSON.parse(raw.toString('utf8'))); } catch (_) {} });
    await waitForWsOpen(hostWs, 'host');

    const session = await requestJson('POST', '/admin/viewer-session', { login: VIEWER_LOGIN, displayName: VIEWER_DISPLAY, ttlMs: 600000 });
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 900, height: 780 } });
    page.on('pageerror', e => console.error(`[pageerror] ${e.message}`));
    await page.addInitScript(id => sessionStorage.setItem('overlord_session', JSON.stringify(id)),
      { sessionToken: session.sessionToken, login: session.login, displayName: session.displayName });
    await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });
    await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === VIEWER_LOGIN), 'viewer_joined');

    hostWs.send(JSON.stringify({ type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4871', work: true, schedule: true, contextMenu: true, toolkitBridge: true }));
    hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY }] }));
    await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
    await page.click('.colonist-row .claim-btn');
    await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'claim_colonist' && m.username === VIEWER_LOGIN), 'claim');
    hostWs.send(JSON.stringify({ type: 'command_result', target: VIEWER_LOGIN, action: 'claim_colonist', ok: true, message: 'assigned' }));
    hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY, assignedTo: VIEWER_LOGIN.toUpperCase(), assignedDisplayName: VIEWER_DISPLAY }] }));
    hostWs.send(JSON.stringify({ type: 'pawn_state', target: VIEWER_LOGIN, state: JSON.stringify({ id: PAWN_ID, name: VIEWER_DISPLAY, drafted: false, dead: false, downed: false, weapon: null, apparel: [], inventory: [], nearbyEquipment: [], needs: [], skills: [], traits: [], thoughts: [], health: { summaryHp: 1, hediffs: [] }, capacities: [], appearance: {} }) }));
    hostWs.send(JSON.stringify({
      type: 'toolkit_state', target: VIEWER_LOGIN, available: true, toolkitLoaded: true, toolkitUtilsLoaded: true,
      chatConnected: true, status: 'connected', username: VIEWER_LOGIN, coins: 5000, karma: 100,
      unlimitedCoins: false, earningCoins: true, coinAmount: 30, entries: ENTRIES,
    }));

    // Open the Buy menu via the test hook.
    await wait(300);
    await page.evaluate(() => window.OverlordDebug.openCommand('buy'));
    await page.waitForSelector('.buy-item', { timeout: 8000 });

    // (1) Button presence per item.
    const presence = await page.evaluate(() => {
      const rows = Array.from(document.querySelectorAll('.buy-item'));
      const out = {};
      rows.forEach(row => {
        const buy = row.querySelector('[data-buy-sku]:not([data-buy-equip])');
        const equip = row.querySelector('[data-buy-equip]');
        const sku = (buy || equip)?.dataset.buySku;
        if (sku) out[sku] = { hasBuy: !!buy, hasEquip: !!equip };
      });
      return out;
    });

    // Expected: chargerifle + flakvest have equip; steel + wall do NOT.
    const expectEquip = { chargerifle: true, flakvest: true, steel: false, wall: false };
    for (const [sku, exp] of Object.entries(expectEquip)) {
      const got = presence[sku];
      if (!got) { failures.push(`missing buy row for ${sku}`); continue; }
      if (!got.hasBuy) failures.push(`${sku}: no plain Buy button`);
      if (got.hasEquip !== exp) failures.push(`${sku}: Buy&Equip present=${got.hasEquip}, expected ${exp}`);
    }

    if (process.env.OVERLORD_SMOKE_SCREENSHOTS !== '0') {
      const el = await page.$('.buy-shops') || await page.$('.command-window') || page;
      await el.screenshot({ path: path.join(OUT_DIR, 'buy-equip-buttons.png') });
    }

    // (2) Click Buy & Equip on the flak vest (pick a material first) → expect equipToPawn.
    await page.evaluate(() => {
      const sel = document.querySelector('[data-buy-stuff-select="flakvest"]');
      if (sel) { sel.value = 'Plasteel'; sel.dispatchEvent(new Event('change', { bubbles: true })); }
    });
    await wait(150);
    const beforeCount = hostMessages.filter(m => m.type === 'command' && m.action === 'toolkit_purchase').length;
    await page.click('[data-buy-equip][data-buy-sku="flakvest"]');
    await waitFor(() => hostMessages.filter(m => m.type === 'command' && m.action === 'toolkit_purchase').length > beforeCount, 'Buy&Equip toolkit_purchase');
    const equipMsg = hostMessages.filter(m => m.type === 'command' && m.action === 'toolkit_purchase').pop();
    if (equipMsg.purchase !== 'flakvest') failures.push(`equip purchase sku=${equipMsg.purchase}, expected flakvest`);
    if (equipMsg.equipToPawn !== true) failures.push(`equip msg missing equipToPawn:true → ${JSON.stringify(equipMsg)}`);
    if (equipMsg.argument !== 'Plasteel') failures.push(`equip msg argument=${equipMsg.argument}, expected Plasteel`);

    // (3) Plain Buy on the same item → NO equipToPawn.
    const beforeCount2 = hostMessages.filter(m => m.type === 'command' && m.action === 'toolkit_purchase').length;
    await page.click('[data-buy-sku="flakvest"]:not([data-buy-equip])');
    await waitFor(() => hostMessages.filter(m => m.type === 'command' && m.action === 'toolkit_purchase').length > beforeCount2, 'plain Buy toolkit_purchase');
    const buyMsg = hostMessages.filter(m => m.type === 'command' && m.action === 'toolkit_purchase').pop();
    if (buyMsg.equipToPawn) failures.push(`plain Buy wrongly set equipToPawn → ${JSON.stringify(buyMsg)}`);
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (hostWs) hostWs.close();
    relay.child.kill();
  }
  if (failures.length) {
    console.error('BUY&EQUIP SMOKE FAILED:\n  ' + failures.join('\n  '));
    process.exit(1);
  }
  console.log('BUY&EQUIP SMOKE PASSED: buttons gated by item type; equipToPawn flag sent only for Buy&Equip.');
}

main().catch(e => { console.error(e); process.exit(1); });
