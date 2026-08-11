'use strict';

// Social roster scroll capture + verification.
//
// The social tab used Prev/Next pagination at 4 people per page. A viewer asked
// for scrolling instead (2026-08-04); the replacement shipped and a viewer then
// reported "he forgot to add a scroll to the social tab so you can only see 5
// colonists now" — i.e. the roster was CLIPPED rather than scrollable.
//
// Clipping is invisible to a smoke test that only asserts "the drawer does not
// overflow": a container with overflow:hidden satisfies that assert perfectly
// while hiding half the roster. So this harness measures the thing that actually
// matters to a viewer:
//   (1) every colonist is REACHABLE — scrollHeight covers all rows,
//   (2) the scroll container actually scrolls (scrollHeight > clientHeight when
//       the roster is taller than its box, and scrollTop can actually move),
//   (3) the sort row stays pinned and does not scroll away,
//   (4) the action panel is still on screen.
// Checked at desktop, mid, and mobile widths, since the board collapses from two
// columns to one and the height chain differs in each.
//
// Client-only, no RimWorld host. Run: node scripts/capture-social-roster.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const fs = require('fs');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (26000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `social-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;
const OUT_DIR = path.resolve(ROOT, '..', 'output', 'playwright');

// 14 colonists — a normal mid-game colony, and well past the old 4-per-page cap
// and the "only 5" the viewer reported.
const COLONY = [
  'Aldo', 'Brix', 'Cass', 'Dov', 'Emi', 'Fen', 'Gil',
  'Hana', 'Ivo', 'Jun', 'Kai', 'Lux', 'Mira', 'Nox',
];
const OPINIONS = COLONY.map((name, i) => ({
  id: 2000 + i,
  pawn: name,
  opinion: (i % 5) * 11 - 22,
  opinionOf: (i % 3) * 9 - 9,
  distance: 3 + i * 2,
}));
const RELATIONS = OPINIONS.slice(0, 4).map((o, i) => ({
  id: o.id,
  pawn: o.pawn,
  relation: ['friend', 'rival', 'lover', 'sibling'][i],
}));

// RimWorld's own social log (host: PawnStateSerializer.BuildSocialLog). Deliberately
// covers all three age buckets formatLogAge produces (<2500 ticks "just now", <60000
// "Nh", else "Nd") and one entry long enough to wrap, since the log sits in the narrow
// action column where an unwrapped string would blow the layout out.
const SOCIAL_LOG = [
  { text: 'Aldo chitchatted with you.', ticksAgo: 90 },
  { text: 'You insulted Brix. Brix was not impressed and the argument carried on for some time.', ticksAgo: 1800 },
  { text: 'Cass tried to romance you. You rejected them.', ticksAgo: 7400 },
  { text: 'Dov deep talked with you.', ticksAgo: 34000 },
  { text: 'Emi complimented your shooting.', ticksAgo: 132000 },
];

const PAWN_STATE = {
  id: PAWN_ID,
  name: VIEWER_DISPLAY,
  fullName: VIEWER_DISPLAY,
  drafted: false, dead: false, downed: false, posX: 100, posZ: 100,
  weapon: null,
  apparel: [],
  inventory: [], nearbyEquipment: [],
  needs: [], skills: [], traits: [], thoughts: [],
  health: { summaryHp: 1.0, painLevel: 0, hediffs: [] },
  capacities: [],
  relations: RELATIONS,
  opinions: OPINIONS,
  socialLog: SOCIAL_LOG,
  workPriorities: [], schedule: [],
  appearance: { hairDef: 'Bald', gender: 'Male' },
  currentJob: 'Standing',
};

const VIEWPORTS = [
  { name: 'w1280', width: 1280, height: 800 },
  { name: 'w880', width: 880, height: 720 },
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
      res.on('end', () => {
        let json = null; try { json = data ? JSON.parse(data) : null; } catch (_) {}
        (res.statusCode >= 200 && res.statusCode < 300) ? resolve(json) : reject(new Error(`${method} ${urlPath} ${res.statusCode}: ${data}`));
      });
    });
    req.on('error', reject); if (payload) req.write(payload); req.end();
  });
}
function waitForWsOpen(ws, label) {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`${label} ws timeout`)), 10000);
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
  child.stdout.on('data', () => {}); child.stderr.on('data', () => {});
  return { child };
}
function requireMaybeGlobal(name) {
  try { return require(name); }
  catch (e) { try { const r = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim(); return require(path.join(r, name)); } catch (_) { throw e; } }
}
const { chromium } = requireMaybeGlobal('playwright');

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

    for (const vp of VIEWPORTS) {
      const page = await browser.newPage({ viewport: { width: vp.width, height: vp.height } });
      page.on('pageerror', e => console.error(`[pageerror ${vp.name}] ${e.message}`));
      await page.addInitScript(id => sessionStorage.setItem('overlord_session', JSON.stringify(id)),
        { sessionToken: session.sessionToken, login: session.login, displayName: session.displayName });
      await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });
      await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === VIEWER_LOGIN), `joined ${vp.name}`);

      hostWs.send(JSON.stringify({
        type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4871',
        work: true, schedule: true, contextMenu: true, social: true,
      }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY }] }));
      await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
      await page.click('.colonist-row .claim-btn');
      await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'claim_colonist'), `claim ${vp.name}`);
      hostWs.send(JSON.stringify({ type: 'command_result', target: VIEWER_LOGIN, action: 'claim_colonist', ok: true, message: 'assigned' }));
      hostWs.send(JSON.stringify({ type: 'colonist_list', target: VIEWER_LOGIN, hostMap: true, colonists: [{ id: PAWN_ID, name: VIEWER_DISPLAY, assignedTo: VIEWER_LOGIN.toUpperCase(), assignedDisplayName: VIEWER_DISPLAY }] }));
      hostWs.send(JSON.stringify({ type: 'pawn_state', target: VIEWER_LOGIN, state: JSON.stringify(PAWN_STATE) }));

      await page.click('[data-tab="social"]');
      await page.waitForSelector('.social-board', { timeout: 10000 });
      await wait(350);

      const m = await page.evaluate(expected => {
        const scroll = document.querySelector('.social-scroll');
        const board = document.querySelector('.social-board');
        const sort = document.querySelector('.social-sort');
        const actions = document.querySelector('.social-actions');
        const pane = document.getElementById('tab-social');
        const rows = Array.from(document.querySelectorAll('.social-person'));
        if (!scroll || !board) return { fatal: 'no .social-scroll / .social-board' };

        const scrollBox = scroll.getBoundingClientRect();
        // Visible right now, without scrolling anything.
        const visibleNow = rows.filter(r => {
          const b = r.getBoundingClientRect();
          return b.bottom > scrollBox.top + 1 && b.top < scrollBox.bottom - 1;
        }).length;

        // REACHABILITY, tested the way a viewer experiences it: scroll each row
        // into view by whatever mechanism exists (the inner box on desktop, the
        // #tab-content pane on mobile) and confirm it actually lands on screen.
        // Asserting on a specific container's scrollTop would bake in one layout
        // and miss a clip introduced by the other.
        const drawer = document.getElementById('tab-content');
        let reachable = 0;
        for (const r of rows) {
          r.scrollIntoView({ block: 'nearest' });
          const b = r.getBoundingClientRect();
          const onScreen = b.top >= 0 && b.bottom <= innerHeight + 1 && b.height > 0;
          const insideDrawer = !drawer || (() => {
            const d = drawer.getBoundingClientRect();
            return b.bottom > d.top - 1 && b.top < d.bottom + 1;
          })();
          if (onScreen && insideDrawer) reachable++;
        }
        rows[0]?.scrollIntoView({ block: 'nearest' });

        // Can the inner box scroll? Drive it, don't infer it.
        const before = scroll.scrollTop;
        scroll.scrollTop = scroll.scrollHeight;
        const after = scroll.scrollTop;
        scroll.scrollTop = before;

        const cs = getComputedStyle(scroll);
        const paneCs = pane ? getComputedStyle(pane) : null;
        return {
          rows: rows.length,
          expected,
          reachable,
          visibleNow,
          scrollClientH: Math.round(scroll.clientHeight),
          scrollScrollH: Math.round(scroll.scrollHeight),
          didScroll: after > before,
          maxScrollTop: Math.round(after),
          overflowY: cs.overflowY,
          scrollMaxH: cs.maxHeight,
          boardH: Math.round(board.getBoundingClientRect().height),
          sortH: sort ? Math.round(sort.getBoundingClientRect().height) : 0,
          sortVisible: sort ? sort.getBoundingClientRect().height > 0 : false,
          actionsH: actions ? Math.round(actions.getBoundingClientRect().height) : 0,
          actionsOnScreen: actions ? actions.getBoundingClientRect().top < innerHeight : false,
          paneOverflow: paneCs ? paneCs.overflow : null,
        };
      }, COLONY.length);

      if (m.fatal) {
        failures.push(`${vp.name}: ${m.fatal}`);
        await page.close();
        continue;
      }

      // (1) every colonist rendered
      if (m.rows !== COLONY.length)
        failures.push(`${vp.name}: rendered ${m.rows} rows, expected ${COLONY.length}`);
      // (2) every colonist reachable — this is the "only see 5" defect
      if (m.reachable !== COLONY.length)
        failures.push(`${vp.name}: only ${m.reachable}/${COLONY.length} colonists reachable (clipped)`);
      // (3) if the INNER box is the scroller, overflow must actually scroll.
      //     When the pane scrolls instead (mobile), overflow-y is visible and the
      //     inner box legitimately has nothing to scroll — reachability above is
      //     what covers that case.
      if (m.overflowY !== 'visible' && m.scrollScrollH > m.scrollClientH + 1 && !m.didScroll)
        failures.push(`${vp.name}: content overflows (${m.scrollScrollH} > ${m.scrollClientH}) but the container does not scroll`);
      // (4) the sort row must stay
      if (!m.sortVisible)
        failures.push(`${vp.name}: sort row not visible`);
      // (5) the action panel must stay on screen
      if (!m.actionsOnScreen)
        failures.push(`${vp.name}: action panel pushed off screen`);

      report.push({ viewport: vp.name, ...m });

      if (process.env.OVERLORD_SMOKE_SCREENSHOTS !== '0') {
        // Reset every scroller first — the reachability probe above scrolled the
        // pane, and a screenshot taken there shows a mid-scroll state rather than
        // what a viewer sees when the tab opens.
        await page.evaluate(() => {
          const d = document.getElementById('tab-content');
          if (d) d.scrollTop = 0;
          const s = document.querySelector('.social-scroll');
          if (s) s.scrollTop = 0;
        });
        await wait(150);
        const target = (await page.$('#bottom-panel')) || page;
        await target.screenshot({ path: path.join(OUT_DIR, `social-roster-${vp.name}.png`) });
      }
      await page.close();
    }
  } catch (err) {
    failures.push(`harness error: ${err.message}`);
  } finally {
    try { if (hostWs) hostWs.close(); } catch (_) {}
    try { if (browser) await browser.close(); } catch (_) {}
    try { relay.child.kill(); } catch (_) {}
  }

  console.log(JSON.stringify({ ok: failures.length === 0, failures, report }, null, 2));
  process.exit(failures.length === 0 ? 0 : 1);
}

main();
