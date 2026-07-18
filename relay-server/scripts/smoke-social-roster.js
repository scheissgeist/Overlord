'use strict';

// Slices: reciprocal relations + colony roster (+ social command shape).
// Asserts: (1) social list renders BOTH directions ("you" and "them" opinions);
// (2) opening the Colonists section sends request_roster and renders rows from
// roster_state (incl. gender/age/relations/viewer tag); (3) clicking a social
// interaction sends {action:'social_interact', targetId, interaction}.
// Run: node scripts/smoke-social-roster.js

const childProcess = require('child_process');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (25000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = `socros-${crypto.randomBytes(8).toString('hex')}`;
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
      weapon: null, apparel: [], inventory: [], nearbyEquipment: [],
      needs: [], skills: [], traits: [], thoughts: [], health: { summaryHp: 1, hediffs: [] }, capacities: [], appearance: {},
      relations: [{ id: 20001, pawn: 'Evan', relation: 'brother' }],
      opinions: [
        { id: 20001, pawn: 'Evan', opinion: 12, opinionOf: -5, distance: 8 },
        { id: 20002, pawn: 'Hanh', opinion: -3, opinionOf: 22, distance: 15 },
      ],
    }) }));

    // (1) social tab shows both directions
    await page.click('[data-tab="social"]');
    await page.waitForSelector('.social-person', { timeout: 8000 });
    const socialTxt = await page.evaluate(() => Array.from(document.querySelectorAll('.social-person')).map(b => b.innerText.replace(/\s+/g, ' ')).join(' | '));
    if (!/\+12/.test(socialTxt) || !/-5/.test(socialTxt)) failures.push(`social row missing reciprocal pair (+12 / -5): "${socialTxt}"`);
    if (!/\+22/.test(socialTxt)) failures.push(`second row missing their +22: "${socialTxt}"`);

    // (3) social interaction command shape (click person then Compliment)
    await page.click('.social-person');
    await wait(150);
    await page.click('[data-social-interaction="KindWords"]');
    const social = await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'social_interact'), 'social_interact command');
    if (Number(social.targetId) !== 20001 || social.interaction !== 'KindWords') failures.push(`social_interact wrong shape: ${JSON.stringify(social)}`);

    // (2) roster: open Colonists -> request_roster -> render reply
    await page.evaluate(() => window.OverlordDebug.openCommand('roster'));
    await waitFor(() => hostMessages.find(m => m.type === 'request_roster' && m.username === VIEWER_LOGIN), 'request_roster');
    hostWs.send(JSON.stringify({ type: 'roster_state', target: VIEWER_LOGIN, colonists: [
      { id: PAWN_ID, name: 'BroTeam', gender: 'Male', age: 34, relations: ['brother: Evan'], viewer: 'BroTeam' },
      { id: 20001, name: 'Evan', gender: 'Male', age: 29, relations: ['brother: BroTeam', 'lover: Hanh'], viewer: null },
      { id: 20002, name: 'Hanh', gender: 'Female', age: 31, relations: ['lover: Evan'], viewer: 'huculberryfinn' },
    ] }));
    await page.waitForSelector('.roster-row', { timeout: 8000 });
    const roster = await page.evaluate(() => ({
      rows: document.querySelectorAll('.roster-row').length,
      txt: Array.from(document.querySelectorAll('.roster-row')).map(r => r.innerText.replace(/\s+/g, ' ')).join(' | '),
    }));
    if (roster.rows !== 3) failures.push(`roster rows=${roster.rows}, expected 3`);
    if (!/♀ 31/.test(roster.txt) || !/lover: Evan/.test(roster.txt) || !/huculberryfinn/.test(roster.txt)) failures.push(`roster content wrong: "${roster.txt}"`);
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (hostWs) hostWs.close();
    relay.child.kill();
  }
  if (failures.length) { console.error('SOCIAL/ROSTER SMOKE FAILED:\n  ' + failures.join('\n  ')); process.exit(1); }
  console.log('SOCIAL/ROSTER SMOKE PASSED: reciprocal opinions render, roster request/render works, social command shape intact.');
}
main().catch(e => { console.error(e); process.exit(1); });
