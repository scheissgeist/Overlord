'use strict';

// Colour-wheel dye capture + verification.
//
// Viewers previously picked apparel colour from 19 fixed swatches; the wheel
// lets them pick any hue while saturation/value stay clamped to the game's
// muted register (host: PawnCommandRouter.ClampToGameGamut).
//
// This drives the REAL interaction — open the dye panel, drag the wheel past
// its rim, apply — and checks things a unit test on the clamp alone would miss:
//   (1) the panel renders the wheel when the host advertises dyeCustomColors,
//   (2) dragging sends dye_apparel with a colorHex (not a swatch colorId),
//   (3) the hex leaving the client is already inside the gamut, so the preview
//       the viewer saw matches what the host will apply,
//   (4) the fixed swatches still send colorId — the wheel is additive.
// Also screenshots the panel so the wheel can be LOOKED at, not just measured.
//
// The dyeable shirt fixture (7003 / Apparel_CollarShirt) matches the existing
// capture-gear-squish.js fixture so both harnesses exercise the same item.
//
// Client-only, no RimWorld host. Run: node scripts/capture-dye-wheel.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (25000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `dye-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;
const OUT_DIR = path.resolve(ROOT, '..', 'output', 'playwright');

// The dyeable shirt this harness drives (same fixture as capture-gear-squish).
const shirt = 7003;

// Mirrors PawnCommandRouter.BuildDyeGamutMessage / ClampToGameGamut.
const GAMUT = { maxSaturation: 0.72, minValue: 0.14, maxValue: 0.90 };

// A trimmed subset of the real fixed palette (PawnCommandRouter.DyePalette).
const DYE_PALETTE = [
  { id: 'crimson', label: 'Crimson', hex: '#8C1F1F' },
  { id: 'teal', label: 'Teal', hex: '#297070' },
  { id: 'navy', label: 'Navy', hex: '#29386B' },
  { id: 'sand', label: 'Sand', hex: '#CCB885' },
  { id: 'charcoal', label: 'Charcoal', hex: '#29292B' },
];

const PAWN_STATE = {
  id: PAWN_ID,
  name: VIEWER_DISPLAY,
  fullName: VIEWER_DISPLAY,
  drafted: false, dead: false, downed: false, posX: 100, posZ: 100,
  weapon: null,
  apparel: [
    { id: shirt, label: 'Devilstrand button-down shirt', defName: 'Apparel_CollarShirt', slotKey: 'torso', hp: 94, dyeable: true, color: '#7a5b3a' },
    { id: 7004, label: 'Cataphract helmet', defName: 'Apparel_ArmorCataphractHelmet', slotKey: 'head', hp: 79, dyeable: false },
  ],
  inventory: [], nearbyEquipment: [],
  needs: [], skills: [], traits: [], thoughts: [],
  health: { summaryHp: 1.0, painLevel: 0, hediffs: [] },
  capacities: [], relations: [], opinions: [], workPriorities: [], schedule: [],
  appearance: { hairDef: 'Bald', gender: 'Male' },
  currentJob: 'Standing',
};

const WIDTHS = [
  { name: 'w1280', width: 1280, height: 800 },
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

function hexToHsv(hex) {
  const s = hex.replace('#', '');
  const r = parseInt(s.slice(0, 2), 16) / 255;
  const g = parseInt(s.slice(2, 4), 16) / 255;
  const b = parseInt(s.slice(4, 6), 16) / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  const d = max - min;
  let h = 0;
  if (d !== 0) {
    if (max === r) h = ((g - b) / d) % 6;
    else if (max === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
  }
  h /= 6; if (h < 0) h += 1;
  return { h, s: max === 0 ? 0 : d / max, v: max };
}

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const relay = startRelay();
  let browser = null, hostWs = null;
  const failures = [];
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

      hostWs.send(JSON.stringify({
        type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4871',
        work: true, schedule: true, contextMenu: true, toolkitBridge: true,
        dyePalette: DYE_PALETTE, dyeCustomColors: true, dyeGamut: GAMUT,
      }));
      // A real host always sends permissions (ViewerManager.SendPermissions), and the
      // dye gate now reads permissions.appearance directly — matching the host's own
      // check in ExecuteDyeApparel — rather than the one-free-change escape hatch,
      // which reported "allowed" on a default host and rendered Dye buttons that every
      // click then bounced. This fixture omitted permissions entirely, so it was
      // asserting against a state no real viewer is ever in.
      hostWs.send(JSON.stringify({
        type: 'permissions', target: VIEWER_LOGIN,
        draft: true, move: true, attack: true, work: true, schedule: true,
        outfit: true, drugPolicy: true, foodPolicy: true, area: true, equip: true,
        appearance: true, freeAppearanceAvailable: true,
      }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY }] }));
      await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
      await page.click('.colonist-row .claim-btn');
      await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'claim_colonist'), `claim ${vp.name}`);
      hostWs.send(JSON.stringify({ type: 'command_result', target: VIEWER_LOGIN, action: 'claim_colonist', ok: true, message: 'assigned' }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY, assignedTo: VIEWER_LOGIN.toUpperCase(), assignedDisplayName: VIEWER_DISPLAY }] }));
      hostWs.send(JSON.stringify({ type: 'pawn_state', target: VIEWER_LOGIN, state: JSON.stringify(PAWN_STATE) }));

      await page.click('[data-tab="gear"]');
      await page.waitForSelector('.gear-layout', { timeout: 10000 });
      await wait(300);

      const toggle = await page.$(`[data-dye-toggle="${shirt}"]`);
      if (!toggle) { failures.push(`${vp.name}: no Dye button on the dyeable item`); await page.close(); continue; }
      await toggle.click();
      await wait(250);

      // (1) The wheel exists when the host advertises dyeCustomColors.
      const wheel = await page.$(`[data-dye-wheel="${shirt}"]`);
      if (!wheel) { failures.push(`${vp.name}: dye wheel not rendered despite dyeCustomColors:true`); await page.close(); continue; }

      // (2) Drag past the rim — max saturation, a hue with no fixed swatch.
      //     This is the "neon" attempt the gamut clamp has to defeat.
      const box = await wheel.boundingBox();
      const cx = box.x + box.width / 2;
      const cy = box.y + box.height / 2;
      await page.mouse.move(cx, cy);
      await page.mouse.down();
      await page.mouse.move(cx + box.width, cy, { steps: 8 });
      await page.mouse.up();
      await wait(200);

      // Push brightness to max too, attacking the value clamp as well.
      await page.evaluate(id => {
        const sl = document.querySelector(`[data-dye-value="${id}"]`);
        if (sl) { sl.value = sl.max; sl.dispatchEvent(new Event('input', { bubbles: true })); }
      }, shirt);
      await wait(200);

      const preview = await page.evaluate(id => {
        const el = document.querySelector(`[data-dye-preview="${id}"]`);
        return el ? getComputedStyle(el).backgroundColor : null;
      }, shirt);

      if (process.env.OVERLORD_SMOKE_SCREENSHOTS !== '0') {
        const target = await page.$('#bottom-panel') || page;
        await target.screenshot({ path: path.join(OUT_DIR, `dye-wheel-${vp.name}.png`) });
      }

      // Geometry: the palette is an absolutely-positioned popover, so confirm
      // it stays inside the panel it is anchored to instead of painting over
      // the layout / running off the right edge.
      const geo = await page.evaluate(id => {
        const pal = document.querySelector(`[data-dye-palette="${id}"]`);
        const box = el => { if (!el) return null; const b = el.getBoundingClientRect(); return { x: Math.round(b.x), w: Math.round(b.width), right: Math.round(b.right) }; };
        return {
          palette: box(pal),
          offsetParent: pal && pal.offsetParent ? (pal.offsetParent.id || pal.offsetParent.className) : null,
          offsetParentBox: box(pal && pal.offsetParent),
          panel: box(document.getElementById('bottom-panel')),
          viewportW: window.innerWidth,
        };
      }, shirt);
      if (geo.palette && geo.panel && geo.palette.right > geo.panel.right + 1) {
        failures.push(`${vp.name}: dye popover right edge ${geo.palette.right} exceeds panel right ${geo.panel.right}`);
      }

      // (3) Apply → dye_apparel carrying an in-gamut colorHex.
      const before = hostMessages.filter(m => m.type === 'command' && m.action === 'dye_apparel').length;
      await page.click(`[data-dye-apply-custom="${shirt}"]`);
      await waitFor(() => hostMessages.filter(m => m.type === 'command' && m.action === 'dye_apparel').length > before, `dye command ${vp.name}`);
      const msg = hostMessages.filter(m => m.type === 'command' && m.action === 'dye_apparel').pop();

      if (!msg.colorHex) failures.push(`${vp.name}: dye command has no colorHex → ${JSON.stringify(msg)}`);
      if (msg.colorId) failures.push(`${vp.name}: wheel apply wrongly sent a swatch colorId → ${JSON.stringify(msg)}`);
      if (Number(msg.itemId) !== shirt) failures.push(`${vp.name}: dye targeted item ${msg.itemId}, expected ${shirt}`);

      let hsv = null;
      if (msg.colorHex) {
        hsv = hexToHsv(msg.colorHex);
        if (hsv.s > GAMUT.maxSaturation + 0.02) failures.push(`${vp.name}: saturation ${hsv.s.toFixed(3)} exceeds gamut max ${GAMUT.maxSaturation}`);
        if (hsv.v > GAMUT.maxValue + 0.02) failures.push(`${vp.name}: value ${hsv.v.toFixed(3)} exceeds gamut max ${GAMUT.maxValue}`);
        if (hsv.v < GAMUT.minValue - 0.02) failures.push(`${vp.name}: value ${hsv.v.toFixed(3)} below gamut min ${GAMUT.minValue}`);
      }

      // (4) Fixed swatches still work — the wheel is additive, not a swap.
      const before2 = hostMessages.filter(m => m.type === 'command' && m.action === 'dye_apparel').length;
      await page.click(`[data-dye-apply="${shirt}"][data-dye-color="teal"]`);
      await waitFor(() => hostMessages.filter(m => m.type === 'command' && m.action === 'dye_apparel').length > before2, `swatch dye ${vp.name}`);
      const swatchMsg = hostMessages.filter(m => m.type === 'command' && m.action === 'dye_apparel').pop();
      if (swatchMsg.colorId !== 'teal') failures.push(`${vp.name}: swatch sent colorId=${swatchMsg.colorId}, expected teal`);
      if (swatchMsg.colorHex) failures.push(`${vp.name}: swatch wrongly sent a colorHex → ${JSON.stringify(swatchMsg)}`);

      report.push({ vp: vp.name, previewCss: preview, sentHex: msg.colorHex || null, hsv, geo });
      await page.close();
    }
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (hostWs) hostWs.close();
    relay.child.kill();
  }

  console.log(JSON.stringify(report, null, 2));
  if (failures.length) {
    console.error('\nDYE WHEEL FAILURES:');
    failures.forEach(f => console.error('  - ' + f));
    process.exit(1);
  }
  console.log('\nDYE WHEEL PASSED: wheel renders, drag sends in-gamut colorHex, swatches still send colorId.');
  console.log(`Screenshots -> ${OUT_DIR}`);
}
main().catch(e => { console.error(e); process.exit(1); });
