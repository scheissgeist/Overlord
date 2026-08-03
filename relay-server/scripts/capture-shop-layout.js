'use strict';

// Shop visual capture. Viewer report (2026-08-03): the Buy page "looks dogshit"
// — screenshot showed 3 items in a near-empty full-width panel, each row's
// price/qty/Buy/Buy&Equip stacked in a tall right-hand column, tiny icons.
//
// This reproduces THAT screen (same coins/karma/search text) so the layout can
// be looked at directly instead of reasoned about, and prints the geometry the
// eye can't measure (row height, dead horizontal space, icon box vs natural
// size, whether the action buttons stack).
//
// Entry field names mirror the host contract in
// Source/Toolkit/TwitchToolkitBridge.cs:493-515. defNames used here are ones
// that appear in this repo's own data (verified by grep), so nothing is invented.
//
// Client-only, no RimWorld host — no C# / main-thread involvement, so this sits
// outside the perf-hardening hot path in PROJECT_NORTH_STAR.md.
// Run: node scripts/capture-shop-layout.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (24000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `shop-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;
const OUT_DIR = path.resolve(ROOT, '..', 'output', 'playwright');

// Wallet numbers + search string taken from the reporter's screenshot.
const REPORT_COINS = 18789;
const REPORT_KARMA = 200;
const REPORT_SEARCH = 'shot';

// A catalog wide enough that the UNFILTERED shop (the common case) is
// representative, and that a search can narrow it to a few rows like the report.
const ENTRIES = [
  { kind: 'item', category: 'weapons', sku: 'chainshotgun', label: 'chain shotgun', defName: 'Gun_ChainShotgun', cost: 675, price: 675, unitCost: 675, affordable: true, needsInput: false, isWeapon: true, isApparel: false, command: '!buy chainshotgun' },
  { kind: 'item', category: 'weapons', sku: 'pumpshotgun', label: 'pump shotgun', defName: 'Gun_PumpShotgun', cost: 675, price: 675, unitCost: 675, affordable: true, needsInput: false, isWeapon: true, isApparel: false, command: '!buy pumpshotgun' },
  { kind: 'item', category: 'weapons', sku: 'boltactionrifle', label: 'bolt-action rifle', defName: 'Gun_BoltActionRifle', cost: 450, price: 450, unitCost: 450, affordable: true, needsInput: false, isWeapon: true, isApparel: false, command: '!buy boltactionrifle' },
  { kind: 'item', category: 'weapons', sku: 'chargerifle', label: 'charge rifle', defName: 'Gun_ChargeRifle', cost: 1200, price: 1200, unitCost: 1200, affordable: true, needsInput: false, isWeapon: true, isApparel: false, command: '!buy chargerifle' },
  { kind: 'item', category: 'apparel', sku: 'flakvest', label: 'flak vest', defName: 'Apparel_FlakVest', cost: 1600, price: 1600, unitCost: 1600, affordable: true, needsInput: false, isWeapon: false, isApparel: true, madeFromStuff: true, stuffOptions: [{ defName: 'Steel', label: 'steel', category: 'Metallic' }, { defName: 'Plasteel', label: 'plasteel', category: 'Metallic' }], command: '!buy flakvest' },
  { kind: 'item', category: 'apparel', sku: 'apparelduster', label: 'duster', defName: 'Apparel_Duster', cost: 320, price: 320, unitCost: 320, affordable: true, needsInput: false, isWeapon: false, isApparel: true, madeFromStuff: true, stuffOptions: [{ defName: 'Cloth', label: 'cloth', category: 'Fabric' }], command: '!buy apparelduster' },
  { kind: 'item', category: 'materials', sku: 'steel', label: 'steel', defName: 'Steel', cost: 30, price: 30, unitCost: 30, affordable: true, needsInput: false, isWeapon: false, isApparel: false, command: '!buy steel' },
  { kind: 'item', category: 'materials', sku: 'wood', label: 'wood', defName: 'WoodLog', cost: 12, price: 12, unitCost: 12, affordable: true, needsInput: false, isWeapon: false, isApparel: false, command: '!buy wood' },
  { kind: 'item', category: 'food', sku: 'mealsimple', label: 'simple meal', defName: 'MealSimple', cost: 60, price: 60, unitCost: 60, affordable: true, needsInput: false, isWeapon: false, isApparel: false, command: '!buy mealsimple' },
  { kind: 'item', category: 'medical', sku: 'medicine', label: 'medicine', defName: 'Medicine', cost: 180, price: 180, unitCost: 180, affordable: true, needsInput: false, isWeapon: false, isApparel: false, command: '!buy medicine' },
  { kind: 'item', category: 'buildables', sku: 'wall', label: 'wall', defName: 'Wall', cost: 45, price: 45, unitCost: 45, affordable: true, needsInput: false, isWeapon: false, isApparel: false, isBuildable: true, command: '!buy wall' },
  { kind: 'item', category: 'buildables', sku: 'table', label: 'table', defName: 'Table2x2c', cost: 198, price: 198, unitCost: 198, affordable: true, needsInput: false, isWeapon: false, isApparel: false, isBuildable: true, madeFromStuff: true, stuffOptions: [{ defName: 'WoodLog', label: 'wood', category: 'Woody' }, { defName: 'Steel', label: 'steel', category: 'Metallic' }], command: '!buy table' },
];

const WIDTHS = [
  { name: 'w1280', width: 1280, height: 800 },
  { name: 'w880', width: 880, height: 680 },
  { name: 'w390', width: 390, height: 844 },
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
function requireMaybeGlobal(name) {
  try { return require(name); }
  catch (e) { try { const r = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim(); return require(path.join(r, name)); } catch (_) { throw e; } }
}
const { chromium } = requireMaybeGlobal('playwright');

// 22x22 mid-grey PNG stands in for a real RimWorld sprite at true icon size, so
// the icon measurement reflects real geometry rather than a stretched 1x1.
const ICON_PNG = 'iVBORw0KGgoAAAANSUhEUgAAABYAAAAWCAYAAADEtGw7AAAAJUlEQVR42u3NMQEAAAgDoC251a3gLwSgc7dJAAAAAAAAAAAAAPwZWtAAAT7Ai7oAAAAASUVORK5CYII=';

async function measure(page) {
  return page.evaluate(() => {
    const out = {};
    const rect = el => { const r = el.getBoundingClientRect(); return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) }; };
    const win = document.querySelector('.command-window') || document.body;
    out.viewport = { w: window.innerWidth, h: window.innerHeight };
    out.window = rect(win);

    const shops = document.querySelector('.buy-shops');
    out.shops = shops ? rect(shops) : null;

    const items = Array.from(document.querySelectorAll('.buy-item'));
    out.itemCount = items.length;
    out.items = items.map(el => {
      const r = rect(el);
      const main = el.querySelector('.buy-main');
      const meta = el.querySelector('.buy-meta');
      const label = el.querySelector('.buy-main strong');
      const icon = el.querySelector('.buy-icon');
      const actions = Array.from(el.querySelectorAll('.buy-actions button')).map(b => ({ text: b.textContent.trim(), ...rect(b) }));
      const mainR = main ? rect(main) : null;
      // Dead space = width of .buy-main not covered by its widest child.
      const widestChild = main ? Math.max(0, ...Array.from(main.children).map(c => c.getBoundingClientRect().width)) : 0;
      return {
        label: label ? label.textContent.trim() : '(none)',
        row: r,
        main: mainR,
        meta: meta ? rect(meta) : null,
        deadSpaceInMain: mainR ? Math.round(mainR.w - widestChild) : null,
        icon: icon ? { ...rect(icon), naturalW: icon.naturalWidth, naturalH: icon.naturalHeight } : null,
        actions,
        actionsStacked: actions.length > 1 ? Math.abs(actions[0].y - actions[1].y) > 4 : false,
      };
    });

    out.tabs = Array.from(document.querySelectorAll('.buy-shop-tab')).map(b => ({ text: b.textContent.replace(/\s+/g, ' ').trim(), ...rect(b) }));

    const buyPage = document.querySelector('.buy-page');
    if (buyPage && items.length) {
      const firstItem = rect(items[0]);
      out.chromeAboveFirstItem = Math.round(firstItem.y - rect(buyPage).y);
      out.itemRowHeight = firstItem.h;
    }
    return out;
  });
}

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
      hostWs.send(JSON.stringify({ type: 'pawn_state', target: VIEWER_LOGIN, state: JSON.stringify({ id: PAWN_ID, name: VIEWER_DISPLAY, drafted: false, dead: false, downed: false, weapon: { label: 'chain shotgun' }, apparel: [], inventory: [], nearbyEquipment: [], needs: [], skills: [], traits: [], thoughts: [], health: { summaryHp: 1, hediffs: [] }, capacities: [], appearance: {} }) }));
      hostWs.send(JSON.stringify({
        type: 'toolkit_state', target: VIEWER_LOGIN, available: true, toolkitLoaded: true, toolkitUtilsLoaded: true,
        chatConnected: true, status: 'connected', username: VIEWER_LOGIN, coins: REPORT_COINS, karma: REPORT_KARMA,
        unlimitedCoins: false, earningCoins: true, coinAmount: 30, coinInterval: 2, minimumPurchase: 0, entries: ENTRIES,
      }));

      await wait(300);
      await page.evaluate(() => window.OverlordDebug.openCommand('buy'));
      await page.waitForSelector('.buy-item', { timeout: 8000 });

      // Feed icons so thumbnails render at real size instead of empty boxes.
      // request_icons is debounced client-side, so wait for it rather than
      // grabbing whatever has arrived (that raced and yielded blank placeholders).
      const iconReq = await waitFor(
        () => hostMessages.filter(m => m.type === 'request_icons' && typeof m.defs === 'string').pop(),
        `request_icons ${vp.name}`, 8000
      ).catch(() => null);
      if (iconReq) {
        const icons = {};
        for (const def of iconReq.defs.split(',')) if (def) icons[def] = ICON_PNG;
        hostWs.send(JSON.stringify({ type: 'item_icons', target: VIEWER_LOGIN, icons }));
      } else {
        console.error(`[${vp.name}] no request_icons seen — icons will render as placeholders`);
      }
      await wait(500);

      // (A) Full shop, no filter — the common case.
      const full = await measure(page);
      await page.screenshot({ path: path.join(OUT_DIR, `shop-${vp.name}-full.png`), fullPage: false });

      // (B) Narrowed by search, like the reported screenshot.
      await page.evaluate(q => {
        const input = document.querySelector('[data-buy-search]');
        if (input) { input.value = q; input.dispatchEvent(new Event('input', { bubbles: true })); }
      }, REPORT_SEARCH);
      await wait(400);
      const filtered = await measure(page);
      await page.screenshot({ path: path.join(OUT_DIR, `shop-${vp.name}-search.png`), fullPage: false });

      report.push({ vp: vp.name, full, filtered });
      await page.close();
    }
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (hostWs) hostWs.close();
    relay.child.kill();
  }
  console.log(JSON.stringify(report, null, 2));
  console.log(`\nScreenshots -> ${OUT_DIR}`);
}
main().catch(e => { console.error(e); process.exit(1); });
