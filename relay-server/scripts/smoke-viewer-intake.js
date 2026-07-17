'use strict';

const childProcess = require('child_process');
const fs = require('fs');
const http = require('http');
const path = require('path');
const crypto = require('crypto');
const WebSocket = require('ws');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.SMOKE_PORT || (19000 + Math.floor(Math.random() * 1000)));
const HOST_SECRET = process.env.SMOKE_HOST_SECRET || `smoke-${crypto.randomBytes(8).toString('hex')}`;
const BASE_URL = `http://127.0.0.1:${PORT}`;
const WS_URL = `ws://127.0.0.1:${PORT}/ws`;
const VIEWER_LOGIN = 'broteam';
const VIEWER_DISPLAY = 'BroTeam';
const PAWN_ID = 10101;
const SCREENSHOT_DIR = process.env.OVERLORD_SMOKE_SCREENSHOTS
  ? path.resolve(ROOT, '..', 'output', 'playwright')
  : '';
const SUMMARY_OUTPUT = process.env.OVERLORD_SMOKE_SUMMARY === '1';

function requireMaybeGlobal(name) {
  try {
    return require(name);
  } catch (localError) {
    try {
      const npmRoot = childProcess.execSync('npm root -g', { encoding: 'utf8' }).trim();
      return require(path.join(npmRoot, name));
    } catch (_) {
      throw localError;
    }
  }
}

const { chromium } = requireMaybeGlobal('playwright');

function wait(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitFor(fn, label, timeoutMs = 10000) {
  const start = Date.now();
  let lastError = null;
  while (Date.now() - start < timeoutMs) {
    try {
      const value = await fn();
      if (value) return value;
    } catch (error) {
      lastError = error;
    }
    await wait(100);
  }
  const suffix = lastError ? `: ${lastError.message}` : '';
  throw new Error(`Timed out waiting for ${label}${suffix}`);
}

function requestJson(method, urlPath, body = null) {
  const payload = body ? JSON.stringify(body) : null;
  return new Promise((resolve, reject) => {
    const req = http.request(`${BASE_URL}${urlPath}`, {
      method,
      headers: {
        Authorization: `Bearer ${HOST_SECRET}`,
        ...(payload ? {
          'Content-Type': 'application/json',
          'Content-Length': Buffer.byteLength(payload),
        } : {}),
      },
    }, res => {
      let data = '';
      res.on('data', chunk => { data += chunk; });
      res.on('end', () => {
        let json = null;
        try { json = data ? JSON.parse(data) : null; } catch (_) {}
        if (res.statusCode < 200 || res.statusCode >= 300) {
          reject(new Error(`${method} ${urlPath} failed ${res.statusCode}: ${data}`));
          return;
        }
        resolve(json);
      });
    });
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

function waitForWsOpen(ws, label) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`${label} websocket did not open`)), 10000);
    ws.once('open', () => {
      clearTimeout(timer);
      resolve();
    });
    ws.once('error', reject);
  });
}

function startRelay() {
  const child = childProcess.spawn(process.execPath, ['server.js'], {
    cwd: ROOT,
    env: {
      ...process.env,
      PORT: String(PORT),
      HOST_SECRET,
      TWITCH_CLIENT_ID: '',
      LOG_TRAFFIC: '0',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  const output = [];
  child.stdout.on('data', chunk => output.push(chunk.toString()));
  child.stderr.on('data', chunk => output.push(chunk.toString()));
  child.once('exit', code => {
    if (code && code !== 0) {
      output.push(`[relay exited ${code}]`);
    }
  });

  return { child, output };
}

async function main() {
  const relay = startRelay();
  let browser = null;
  let hostWs = null;
  let burstViewerWs = null;

  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'relay health', 15000);

    hostWs = new WebSocket(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
    const hostMessages = [];
    hostWs.on('message', raw => {
      try { hostMessages.push(JSON.parse(raw.toString('utf8'))); } catch (_) {}
    });
    await waitForWsOpen(hostWs, 'host');

    const burstLogin = 'contextburst';
    const burstSession = await requestJson('POST', '/admin/viewer-session', {
      login: burstLogin,
      displayName: 'Context Burst',
      ttlMs: 60 * 1000,
    });
    burstViewerWs = new WebSocket(`${WS_URL}?role=viewer&session=${encodeURIComponent(burstSession.sessionToken)}&build=context-burst-smoke`);
    await waitForWsOpen(burstViewerWs, 'context burst viewer');
    await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === burstLogin), 'context burst viewer_joined');
    for (let x = 1; x <= 25; x++) {
      burstViewerWs.send(JSON.stringify({ type: 'command', action: 'context_menu', x, z: 10 }));
    }
    await waitFor(() => hostMessages.find(m => m.type === 'command' && m.action === 'context_menu' && m.username === burstLogin), 'first coalesced context request');
    await wait(250);
    let burstRequests = hostMessages.filter(m => m.type === 'command' && m.action === 'context_menu' && m.username === burstLogin);
    if (burstRequests.length !== 1 || Number(burstRequests[0].x) !== 1) {
      throw new Error(`Relay did not hold a context burst to one in-flight request: ${JSON.stringify(burstRequests)}`);
    }
    hostWs.send(JSON.stringify({
      type: 'action_result', target: burstLogin, action: 'context_menu', ok: false, message: 'No actions here',
    }));
    await waitFor(() => hostMessages.filter(m => m.type === 'command' && m.action === 'context_menu' && m.username === burstLogin).length === 2, 'latest coalesced context request');
    await wait(250);
    burstRequests = hostMessages.filter(m => m.type === 'command' && m.action === 'context_menu' && m.username === burstLogin);
    if (burstRequests.length !== 2 || Number(burstRequests[1].x) !== 25) {
      throw new Error(`Relay did not retain only the latest context target: ${JSON.stringify(burstRequests)}`);
    }
    hostWs.send(JSON.stringify({
      type: 'context_menu', target: burstLogin, ok: true, x: 25, z: 10, options: [],
    }));
    burstViewerWs.close();
    burstViewerWs = null;

    const viewerSession = await requestJson('POST', '/admin/viewer-session', {
      login: VIEWER_LOGIN,
      displayName: VIEWER_DISPLAY,
      ttlMs: 10 * 60 * 1000,
    });

    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
    page.on('console', msg => {
      if (msg.type() === 'error') console.error(`[browser] ${msg.text()}`);
    });
    page.on('pageerror', error => {
      console.error(`[pageerror] ${error.message}`);
    });

    await page.addInitScript(identity => {
      sessionStorage.setItem('overlord_session', JSON.stringify(identity));
    }, {
      sessionToken: viewerSession.sessionToken,
      login: viewerSession.login,
      displayName: viewerSession.displayName,
    });

    await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });
    const joinedMessage = await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === VIEWER_LOGIN), 'viewer_joined');
    if (joinedMessage.mapTransport !== 'tile') {
      throw new Error(`Viewer join did not advertise its preferred map transport: ${JSON.stringify(joinedMessage)}`);
    }
    await waitFor(() => hostMessages.find(m => m.type === 'map_transport' && m.username === VIEWER_LOGIN && m.transport === 'tile'), 'map transport negotiation request');
    hostWs.send(JSON.stringify({
      type: 'map_transport', target: VIEWER_LOGIN, requested: 'tile', selected: 'jpeg',
      tileAvailable: false, jpegAvailable: true,
    }));
    await page.waitForFunction(() => window.OverlordDebug.getState().mapTransport.selected === 'jpeg', null, { timeout: 10000 });
    await waitFor(() => hostMessages.find(m => m.type === 'request_colonist_list' && m.username === VIEWER_LOGIN), 'initial colonist list request');

    // A waiting viewer may poll the small colonist list, but must not repeatedly
    // request the full pawn snapshot or the ~450 KB Toolkit catalog.
    await wait(2700);
    const initialStateRequests = hostMessages.filter(m => m.type === 'request_state' && m.username === VIEWER_LOGIN);
    const initialToolkitRequests = hostMessages.filter(m => m.type === 'command' && m.action === 'toolkit_refresh' && m.username === VIEWER_LOGIN);
    if (initialStateRequests.length > 1 || initialToolkitRequests.length !== 0) {
      throw new Error(`Viewer intake request storm: state=${initialStateRequests.length}, toolkit=${initialToolkitRequests.length}`);
    }

    hostWs.send(JSON.stringify({
      type: 'host_capabilities',
      target: VIEWER_LOGIN,
      rimworldVersion: '1.6.4633',
      work: true,
      schedule: true,
      contextMenu: true,
      serverCameraZoom: true,
      toolkitBridge: true,
      storyPurchaseArguments: true,
    }));

    hostWs.send(JSON.stringify({
      type: 'colonist_list',
      target: VIEWER_LOGIN,
      hostMap: true,
      colonists: [
        { id: PAWN_ID, name: VIEWER_DISPLAY },
      ],
    }));

    await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
    await page.click('.colonist-row .claim-btn');

    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'claim_colonist' &&
      m.username === VIEWER_LOGIN &&
      Number(m.pawnId) === PAWN_ID
    ), 'claim_colonist command');

    hostWs.send(JSON.stringify({
      type: 'command_result',
      target: VIEWER_LOGIN,
      action: 'claim_colonist',
      ok: true,
      message: `Already assigned to ${VIEWER_DISPLAY}`,
    }));
    hostWs.send(JSON.stringify({
      type: 'colonist_list',
      target: VIEWER_LOGIN,
      hostMap: true,
      colonists: [
        {
          id: PAWN_ID,
          name: VIEWER_DISPLAY,
          assignedTo: VIEWER_LOGIN.toUpperCase(),
          assignedDisplayName: VIEWER_DISPLAY,
        },
      ],
    }));
    hostWs.send(JSON.stringify({
      type: 'pawn_state',
      target: VIEWER_LOGIN,
      state: {
        id: PAWN_ID,
        name: VIEWER_DISPLAY,
        currentJob: 'Smoke synced',
        drafted: false,
        health: {
          summaryHp: 82,
          painLevel: 14,
          hediffs: [
            { label: 'Bruise', part: 'left arm', severity: 18 },
            { label: 'Alcohol tolerance (small)', severity: 1 },
          ],
        },
        needs: { Mood: 74, Food: 75, Rest: 80, Joy: 53 },
        skills: [
          { name: 'Cooking', label: 'cooking', level: 11, passion: 2, disabled: false },
          { name: 'Mining', label: 'mining', level: 3, passion: 0, disabled: false },
          { name: 'Plants', label: 'plants', level: 9, passion: 1, disabled: false },
          { name: 'Medical', label: 'medical', level: 8, passion: 0, disabled: false },
        ],
        capacities: [
          { label: 'moving', def: 'Moving', level: 93 },
          { label: 'consciousness', def: 'Consciousness', level: 100 },
          { label: 'manipulation', def: 'Manipulation', level: 100 },
          { label: 'blood filtration', def: 'BloodFiltration', level: 100 },
          { label: 'blood pumping', def: 'BloodPumping', level: 100 },
          { label: 'digestion', def: 'Digestion', level: 100 },
        ],
        work: { Firefighter: 1, Patient: 2, Cooking: 3, Mining: 3, Artistic: -1 },
        workPriorities: [
          { defName: 'Firefighter', label: 'Firefighter', priority: 1, disabled: false },
          { defName: 'Patient', label: 'Patient', priority: 2, disabled: false },
          { defName: 'Doctor', label: 'Doctor', priority: 0, disabled: false },
          { defName: 'Cooking', label: 'Cooking', priority: 3, disabled: false },
          { defName: 'Mining', label: 'Mining', priority: 3, disabled: false },
          { defName: 'Artistic', label: 'Artistic', priority: -1, disabled: true },
        ],
        schedule: Array.from({ length: 24 }, () => 'Anything'),
        scheduleAssignments: [
          { defName: 'Anything', label: 'Anything' },
          { defName: 'Work', label: 'Work' },
          { defName: 'Joy', label: 'Joy' },
          { defName: 'Sleep', label: 'Sleep' },
        ],
        outfitPolicy: 'Anything',
        outfitPolicyOptions: ['Anything', 'Worker'],
        drugPolicy: 'Social Drugs',
        drugPolicyOptions: ['Social Drugs', 'No Drugs'],
        foodPolicy: 'Lavish',
        foodPolicyOptions: ['Lavish', 'Simple'],
        areaRestriction: 'Unrestricted',
        areaOptions: ['Unrestricted', 'Home'],
        hostileResponse: 1,
        weapon: { label: 'Steel knife', defName: 'MeleeWeapon_Knife', hp: 88 },
        apparel: [
          { label: 'Lightleather pants', defName: 'Apparel_Pants', slotKey: 'legs', hp: 77 },
          { label: 'Plainleather T-shirt', defName: 'Apparel_BasicShirt', slotKey: 'torso', hp: 95 },
          { label: 'Cloth parka', defName: 'Apparel_Parka', slotKey: 'outer', hp: 72 },
          { label: 'Cloth cowboy hat', defName: 'Apparel_CowboyHat', slotKey: 'head', hp: 91 },
        ],
        nearbyEquipment: [
          { id: 8001, label: 'Steel revolver', defName: 'Gun_Revolver', type: 'weapon', slotKey: 'weapon', hp: 82, distance: 4 },
          { id: 8002, label: 'Flak vest', defName: 'Apparel_FlakVest', type: 'apparel', slotKey: 'outer', hp: 68, distance: 7, marketValue: 1600, quality: 'normal', qualityRank: 2 },
          { id: 8003, label: 'Cloth tuque', defName: 'Apparel_Tuque', type: 'apparel', slotKey: 'head', hp: 94, distance: 3, marketValue: 90, quality: 'awful', qualityRank: 0 },
          { id: 8004, label: 'Synthread duster', defName: 'Apparel_Duster', type: 'apparel', slotKey: 'outer', hp: 90, distance: 12, marketValue: 1200, quality: 'masterwork', qualityRank: 5 },
          { id: 8005, label: 'Marine helmet', defName: 'Apparel_MarineHelmet', type: 'apparel', slotKey: 'head', hp: 79, distance: 8, marketValue: 1800, quality: 'excellent', qualityRank: 4 },
          { id: 8006, label: 'Broadwrap', defName: 'Apparel_Broadwrap', type: 'apparel', slotKey: 'head', hp: 98, distance: 11, marketValue: 120, quality: 'good', qualityRank: 3 },
          { id: 8007, label: 'War mask', defName: 'Apparel_WarMask', type: 'apparel', slotKey: 'head', hp: 66, distance: 14, marketValue: 240, quality: 'normal', qualityRank: 2 },
        ],
        inventory: [
          { id: 7001, label: 'Simple meal', count: 1, hp: 100, ingestible: true },
          { id: 7002, label: 'Herbal medicine', count: 3, hp: 100, ingestible: false },
          { id: 7003, label: 'Kibble', count: 14, hp: 100, ingestible: true },
          { id: 7004, label: 'Component', count: 2, hp: 100, ingestible: false },
          { id: 7005, label: 'Smokeleaf joint', count: 1, hp: 97, ingestible: true },
        ],
        relations: [
          { id: 20202, pawn: 'Wig', relation: 'friend' },
        ],
        opinions: [
          { id: 20202, pawn: 'Wig', opinion: 12, distance: 6 },
          { id: 30303, pawn: 'Soap', opinion: -3, distance: 9 },
          { id: 40404, pawn: 'Willow', opinion: 4, distance: 18 },
          { id: 50505, pawn: 'Xavier', opinion: -8, distance: 22 },
          { id: 60606, pawn: 'York', opinion: 0, distance: 25 },
          { id: 70707, pawn: 'Zed', opinion: 18, distance: 31 },
        ],
        traits: [
          { defName: 'Cannibal', label: 'Cannibal', degree: 0 },
          { defName: 'Undergrounder', label: 'Undergrounder', degree: 0 },
          { defName: 'Pessimist', label: 'Pessimist', degree: 0 },
          { defName: 'SuperImmune', label: 'Super-immune', degree: 0 },
        ],
        traitOptions: [
          { defName: 'Cannibal', label: 'Cannibal', degree: 0 },
          { defName: 'Jogger', label: 'Jogger', degree: 0 },
        ],
        story: {
          title: 'Cave tender',
          childhood: 'Cave child',
          adulthood: 'Colony tender',
          childhoodDef: 'SmokeChild',
          adulthoodDef: 'SmokeAdult',
        },
        appearance: {
          hair: 'Short',
          hairDef: 'Short',
          gender: 'Male',
          bodyType: 'Male',
          hairOptions: [
            { defName: 'Short', label: 'Short' },
            { defName: 'Mohawk', label: 'Mohawk' },
          ],
          genderOptions: ['Male', 'Female'],
        },
        thoughts: [
          { label: 'Extremely impressive rec room', mood: 6 },
          { label: 'Fine meal', mood: 5 },
          { label: 'Disturbed sleep', mood: -3 },
          { label: 'Ate without table', mood: -3 },
          { label: 'Soaking wet', mood: -3 },
          { label: 'Deep talk', mood: 4 },
          { label: 'Slighted', mood: -2 },
          { label: 'Chitchat', mood: 1 },
        ],
      },
    }));
    hostWs.send(JSON.stringify({
      type: 'pawn_portrait',
      target: VIEWER_LOGIN,
      data: 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=',
    }));
    hostWs.send(JSON.stringify({
      type: 'permissions',
      target: VIEWER_LOGIN,
      draft: true,
      move: true,
      attack: true,
      work: true,
      schedule: true,
      outfit: true,
      drugPolicy: true,
      foodPolicy: true,
      area: true,
      equip: true,
      appearance: false,
      freeAppearanceAvailable: true,
    }));
    const toolkitEntries = [
      { kind: 'service', category: 'pawn', sku: 'repairgear', label: 'Repair equipped gear', description: 'Fully repair equipped weapon and worn apparel.', cost: 2000, price: 2000, unitCost: 2000, affordable: true, needsInput: false, command: '!buy repairgear' },
      { kind: 'event', sku: 'healme', label: 'healme', cost: 2167, price: 2167, unitCost: 2167, affordable: true, needsInput: false, command: '!buy healme' },
      { kind: 'event', sku: 'reviveme', label: 'reviveme', cost: 2833, price: 2833, unitCost: 2833, affordable: true, needsInput: false, command: '!buy reviveme' },
      { kind: 'event', sku: 'passionshuffle', label: 'passion shuffle', cost: 1500, price: 1500, unitCost: 1500, affordable: true, needsInput: false, command: '!buy passionshuffle' },
      { kind: 'event', category: 'pawn', sku: 'trait', label: 'add trait', cost: 2500, price: 2500, unitCost: 2500, affordable: true, needsInput: true, variables: 1, command: '!buy trait <trait>' },
      { kind: 'event', category: 'pawn', sku: 'removetrait', label: 'remove trait', cost: 2800, price: 2800, unitCost: 2800, affordable: true, needsInput: true, variables: 1, command: '!buy removetrait <trait>' },
      { kind: 'item', category: 'medical', sku: 'medicine', label: 'medicine', cost: 900, price: 900, unitCost: 900, affordable: true, needsInput: false, command: '!buy medicine' },
      { kind: 'item', category: 'food', sku: 'simplemeal', label: 'simple meal', cost: 80, price: 80, unitCost: 80, affordable: true, needsInput: false, command: '!buy simplemeal' },
      { kind: 'item', category: 'weapons', sku: 'revolver', label: 'revolver', defName: 'Gun_Revolver', cost: 1200, price: 1200, unitCost: 1200, affordable: true, needsInput: false, command: '!buy revolver' },
      { kind: 'item', category: 'apparel', sku: 'flakvest', label: 'flak vest', defName: 'Apparel_FlakVest', cost: 1600, price: 1600, unitCost: 1600, affordable: true, needsInput: false, madeFromStuff: true, stuffOptions: [{ defName: 'Steel', label: 'steel', category: 'Metallic' }, { defName: 'Plasteel', label: 'plasteel', category: 'Metallic' }], command: '!buy flakvest' },
      { kind: 'item', category: 'buildables', sku: 'wall', label: 'wall', cost: 45, price: 45, unitCost: 45, affordable: true, needsInput: false, command: '!buy wall' },
    ];
    hostWs.send(JSON.stringify({
      type: 'toolkit_state',
      target: VIEWER_LOGIN,
      available: true,
      toolkitLoaded: true,
      toolkitUtilsLoaded: true,
      chatConnected: true,
      status: 'connected',
      username: VIEWER_LOGIN,
      coins: 3000,
      karma: 112,
      unlimitedCoins: false,
      earningCoins: true,
      coinAmount: 30,
      coinInterval: 2,
      minimumPurchase: 60,
      itemCount: 2,
      entries: toolkitEntries,
    }));
    hostWs.send(JSON.stringify({
      type: 'game_info',
      target: VIEWER_LOGIN,
      hour: 13,
      day: 17,
      season: 'Fall',
      year: 5501,
      temperature: 19,
      speed: 0,
      paused: true,
      mapName: "Robber's Stone",
    }));

    await page.waitForFunction(() => (
      document.body.dataset.viewerPhase === 'assigned' &&
      document.getElementById('screen-main')?.classList.contains('active') &&
      document.getElementById('pawn-name')?.textContent.trim() === 'BroTeam'
    ), null, { timeout: 10000 });
    await page.waitForFunction(() => document.getElementById('host-speed')?.textContent.trim() === 'Host paused', null, { timeout: 10000 });

    const tinyJpeg = await page.evaluate(() => {
      const canvas = document.createElement('canvas');
      canvas.width = 64;
      canvas.height = 36;
      const ctx = canvas.getContext('2d');
      ctx.fillStyle = '#1c1f1b';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = '#6a8f63';
      ctx.fillRect(24, 12, 16, 12);
      return canvas.toDataURL('image/jpeg', 0.8).split(',')[1];
    });
    hostWs.send(JSON.stringify({
      type: 'map_frame',
      target: VIEWER_LOGIN,
      centerX: 100,
      centerZ: 100,
      radiusX: 20,
      radiusZ: 10,
      sourceWidth: 640,
      sourceHeight: 360,
      quality: 76,
      cameraMode: 'pawn',
      zoom: 1,
      mapWidth: 200,
      mapHeight: 200,
      data: tinyJpeg,
    }));
    await page.waitForSelector('#map-canvas.live-map', { timeout: 10000 });
    const mapBox = await page.locator('#map-canvas').boundingBox();
    const verticalCells = await page.evaluate(() => {
      const canvas = document.getElementById('map-canvas');
      const rect = canvas.getBoundingClientRect();
      return {
        top: window.OverlordDebug.mapPointToCell(rect.left + rect.width / 2, rect.top + rect.height * 0.25),
        bottom: window.OverlordDebug.mapPointToCell(rect.left + rect.width / 2, rect.top + rect.height * 0.75),
      };
    });
    if (!verticalCells.top || !verticalCells.bottom || verticalCells.top.z <= verticalCells.bottom.z) {
      throw new Error(`Live map Z orientation regression: ${JSON.stringify(verticalCells)}`);
    }

    await page.mouse.move(mapBox.x + mapBox.width / 2, mapBox.y + mapBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(mapBox.x + mapBox.width / 2 + 120, mapBox.y + mapBox.height / 2, { steps: 6 });
    await page.mouse.up();
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'camera_zoom' &&
      m.username === VIEWER_LOGIN &&
      Number.isFinite(Number(m.centerX)) &&
      Number(m.centerX) < 100
    ), 'camera pan command from live map drag');
    const contextPoint = {
      x: mapBox.x + mapBox.width * 0.66,
      y: mapBox.y + mapBox.height * 0.58,
    };
    const latestContextPoint = {
      x: mapBox.x + mapBox.width * 0.78,
      y: mapBox.y + mapBox.height * 0.42,
    };
    const expectedContextCells = await page.evaluate(points => {
      return points.map(point => window.OverlordDebug.mapPointToCell(point.x, point.y));
    }, [contextPoint, latestContextPoint]);
    if (expectedContextCells.some(cell => !cell)) {
      throw new Error('Live map context points did not resolve to cells');
    }
    await page.evaluate(points => {
      const canvas = document.getElementById('map-canvas');
      points.forEach(point => canvas.dispatchEvent(new MouseEvent('contextmenu', {
        button: 2, clientX: point.x, clientY: point.y, bubbles: true, cancelable: true,
      })));
    }, [contextPoint, latestContextPoint]);
    await waitFor(() => hostMessages.filter(m =>
      m.type === 'command' && m.action === 'context_menu' && m.username === VIEWER_LOGIN
    ).length === 1, 'one in-flight context_menu command from live map right-click burst');
    await wait(250);
    let contextCommands = hostMessages.filter(m =>
      m.type === 'command' && m.action === 'context_menu' && m.username === VIEWER_LOGIN
    );
    if (contextCommands.length !== 1 || Number(contextCommands[0].x) !== Number(expectedContextCells[0].x) || Number(contextCommands[0].z) !== Number(expectedContextCells[0].z)) {
      throw new Error(`Browser did not hold the latest context request while one was in flight: ${JSON.stringify({ expectedContextCells, contextCommands })}`);
    }
    hostWs.send(JSON.stringify({
      type: 'context_menu',
      target: VIEWER_LOGIN,
      ok: true,
      x: expectedContextCells[0].x,
      z: expectedContextCells[0].z,
      options: [
        { id: 0, label: 'First response', disabled: false },
        { id: 1, label: 'Cannot reach reserved thing', disabled: true },
      ],
    }));
    await waitFor(() => hostMessages.filter(m =>
      m.type === 'command' && m.action === 'context_menu' && m.username === VIEWER_LOGIN
    ).length === 2, 'latest browser context request after first response');
    contextCommands = hostMessages.filter(m =>
      m.type === 'command' && m.action === 'context_menu' && m.username === VIEWER_LOGIN
    );
    if (Number(contextCommands[1].x) !== Number(expectedContextCells[1].x) || Number(contextCommands[1].z) !== Number(expectedContextCells[1].z)) {
      throw new Error(`Browser did not retain the latest context target: ${JSON.stringify({ expectedContextCells, contextCommands })}`);
    }
    hostWs.send(JSON.stringify({
      type: 'context_menu', target: VIEWER_LOGIN, ok: true,
      x: expectedContextCells[1].x, z: expectedContextCells[1].z,
      options: [{ id: 0, label: 'Latest response', disabled: false }],
    }));
    await page.waitForFunction(() => document.getElementById('context-menu')?.textContent.includes('Latest response'), null, { timeout: 10000 });
    const contextBox = await page.locator('#context-menu').boundingBox();
    if (Math.abs(contextBox.x - latestContextPoint.x) > 80 || Math.abs(contextBox.y - latestContextPoint.y) > 80) {
      throw new Error(`Context menu was not anchored near the latest click: ${JSON.stringify({ contextBox, latestContextPoint })}`);
    }
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => document.getElementById('context-menu')?.classList.contains('hidden'), null, { timeout: 10000 });

    await page.click('[data-tab="skills"]');
    await page.waitForSelector('.skill-board .skill-card', { timeout: 10000 });
    const skillLayout = await page.evaluate(() => {
      const tab = document.getElementById('tab-skills');
      return {
        noHorizontalOverflow: tab.scrollWidth <= tab.clientWidth + 1,
        skillCount: document.querySelectorAll('.skill-card').length,
      };
    });
    if (!skillLayout.noHorizontalOverflow || skillLayout.skillCount === 0) {
      throw new Error(`Skills layout regression: ${JSON.stringify(skillLayout)}`);
    }

    // Capacities now live on the Health tab as deviations-only.
    await page.click('[data-tab="health"]');
    await page.waitForSelector('#capacities-list .capacity-board', { timeout: 10000 });
    const healthLayout = await page.evaluate(() => {
      const tab = document.getElementById('tab-health');
      const capRows = document.querySelectorAll('.capacity-board .cap-row');
      const capHeads = Array.from(document.querySelectorAll('.cap-head')).map(row => {
        const [name, value] = row.children;
        const a = name.getBoundingClientRect();
        const b = value.getBoundingClientRect();
        return { ok: a.right <= b.left, name: name.textContent, value: value.textContent };
      });
      return {
        noHorizontalOverflow: tab.scrollWidth <= tab.clientWidth + 1,
        capRowCount: capRows.length,
        capHeads,
        normalLine: document.querySelector('.cap-normal')?.textContent || '',
      };
    });
    if (!healthLayout.noHorizontalOverflow || healthLayout.capHeads.some(row => !row.ok)) {
      throw new Error(`Health layout regression: ${JSON.stringify(healthLayout)}`);
    }

    await page.click('[data-tab="gear"]');
    await page.waitForSelector('.gear-layout .gear-slot.filled', { timeout: 10000 });
    await page.waitForSelector('.inventory-sheet .inventory-row', { timeout: 10000 });
    await page.click('[data-gear-slot-select="head"]');
    await page.waitForSelector('[data-equip-thing-id="8003"]', { timeout: 10000 });
    const gearLayout = await page.evaluate(() => {
      const tab = document.getElementById('tab-gear');
      const slotLabels = Array.from(document.querySelectorAll('.gear-slot-label')).map(row => row.textContent.trim());
      const actions = Array.from(document.querySelectorAll('#gear-list [data-gear-action]')).map(row => row.textContent.trim());
      const nearby = Array.from(document.querySelectorAll('#gear-list [data-equip-thing-id]')).map(row => row.textContent.trim());
      const equippedNames = Array.from(document.querySelectorAll('.gear-equipped-sheet .gear-name')).map(name => ({
        text: name.textContent.trim(),
        clientWidth: name.clientWidth,
        scrollWidth: name.scrollWidth,
      }));
      return {
        noHorizontalOverflow: tab.scrollWidth <= tab.clientWidth + 1,
        tabWidth: { client: tab.clientWidth, scroll: tab.scrollWidth },
        gearListWidth: (() => { const el = document.getElementById('gear-list'); return { client: el.clientWidth, scroll: el.scrollWidth }; })(),
        inventoryWidth: (() => { const el = document.getElementById('inventory-list'); return { client: el.clientWidth, scroll: el.scrollWidth }; })(),
        slotLabels,
        actions,
        nearby,
        equippedNames,
        inventoryRows: document.querySelectorAll('.inventory-row').length,
      };
    });
    if (!gearLayout.noHorizontalOverflow || !gearLayout.slotLabels.includes('Head') || !gearLayout.actions.includes('Take off') ||
        !gearLayout.nearby.includes('Equip') || gearLayout.equippedNames.some(name => name.scrollWidth > name.clientWidth + 1)) {
      throw new Error(`Gear layout regression: ${JSON.stringify(gearLayout)}`);
    }
    if (SCREENSHOT_DIR) {
      await page.locator('#bottom-panel').screenshot({ path: path.join(SCREENSHOT_DIR, 'overlord-gear-desktop.png') });
    }
    const eagerArmoryRequests = hostMessages.filter(m => m.type === 'request_armory' && m.username === VIEWER_LOGIN);
    if (eagerArmoryRequests.length !== 0) {
      throw new Error(`Armory must be on-demand, found ${eagerArmoryRequests.length} eager requests`);
    }
    await page.click('[data-gear-source="armory"]');
    const armoryRequest = await waitFor(() => hostMessages.find(m =>
      m.type === 'request_armory' &&
      m.username === VIEWER_LOGIN &&
      m.slot === 'head' &&
      m.sort === 'distance' &&
      Number(m.page) === 0 &&
      Number(m.pageSize) === 3
    ), 'on-demand head armory request');
    hostWs.send(JSON.stringify({
      type: 'armory_state',
      target: VIEWER_LOGIN,
      ok: true,
      requestId: armoryRequest.requestId,
      search: '',
      slot: 'head',
      sort: 'distance',
      page: 0,
      pageSize: 3,
      pageCount: 1,
      total: 2,
      items: [
        { id: 9001, label: 'Marine helmet', defName: 'Apparel_MarineHelmet', type: 'apparel', slotKey: 'head', hp: 86, distance: 42, marketValue: 1800, quality: 'excellent', qualityRank: 4, available: true },
        { id: 9002, label: 'Prestige cataphract helmet', defName: 'Apparel_ArmorCataphractHelmetPrestige', type: 'apparel', slotKey: 'head', hp: 97, distance: 55, marketValue: 3200, quality: 'masterwork', qualityRank: 5, available: false, blockedReason: 'Reserved or unreachable' },
      ],
    }));
    await page.waitForSelector('[data-equip-thing-id="9001"]', { timeout: 10000 });
    const unavailableArmoryItem = page.locator('[data-equip-thing-id="9002"]');
    if (!(await unavailableArmoryItem.isDisabled())) {
      throw new Error('Armory did not preserve the host unavailable reason');
    }
    await page.fill('[data-armory-search]', 'marine');
    const searchRequest = await waitFor(() => hostMessages.find(m =>
      m.type === 'request_armory' &&
      m.username === VIEWER_LOGIN &&
      m.search === 'marine' &&
      Number(m.requestId) > Number(armoryRequest.requestId)
    ), 'server-side armory search request');
    hostWs.send(JSON.stringify({
      type: 'armory_state',
      target: VIEWER_LOGIN,
      ok: true,
      requestId: searchRequest.requestId,
      search: 'marine',
      slot: 'head',
      sort: 'distance',
      page: 0,
      pageSize: 3,
      pageCount: 1,
      total: 1,
      items: [
        { id: 9001, label: 'Marine helmet', defName: 'Apparel_MarineHelmet', type: 'apparel', slotKey: 'head', hp: 86, distance: 42, marketValue: 1800, quality: 'excellent', qualityRank: 4, available: true },
      ],
    }));
    await page.waitForFunction(() => document.querySelector('[data-armory-search]')?.value === 'marine', null, { timeout: 10000 });
    const armorySearchFocus = await page.evaluate(() => document.activeElement?.matches?.('[data-armory-search]') === true);
    if (!armorySearchFocus) {
      throw new Error('Armory search lost focus when host results arrived');
    }
    if (SCREENSHOT_DIR) {
      await page.locator('#bottom-panel').screenshot({ path: path.join(SCREENSHOT_DIR, 'overlord-armory-desktop.png') });
    }
    await page.click('[data-equip-thing-id="9001"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'equip' &&
      m.username === VIEWER_LOGIN &&
      Number(m.thingId) === 9001
    ), 'equip command from colony armory');
    await page.click('[data-gear-source="nearby"]');
    const repairButtonText = await page.locator('[data-gear-repair]').innerText();
    if (!repairButtonText.includes('2,000 coins')) {
      throw new Error(`Gear repair price regression: ${repairButtonText}`);
    }
    await page.click('[data-gear-repair]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'toolkit_purchase' &&
      m.username === VIEWER_LOGIN &&
      m.purchase === 'repairgear'
    ), 'repairgear toolkit_purchase command from Gear tab');
    await page.click('[data-gear-slot-select="outer"]');
    let firstOuterGear = await page.locator('#gear-list [data-equip-thing-id]').first().evaluate(el => el.closest('.gear-row')?.innerText || '');
    if (!firstOuterGear.includes('Flak vest')) {
      throw new Error(`Gear distance sort regression: ${firstOuterGear}`);
    }
    await page.selectOption('[data-gear-sort-select]', 'quality');
    firstOuterGear = await page.locator('#gear-list [data-equip-thing-id]').first().evaluate(el => el.closest('.gear-row')?.innerText || '');
    if (!firstOuterGear.includes('Synthread duster')) {
      throw new Error(`Gear quality sort regression: ${firstOuterGear}`);
    }
    await page.selectOption('[data-gear-sort-select]', 'value');
    firstOuterGear = await page.locator('#gear-list [data-equip-thing-id]').first().evaluate(el => el.closest('.gear-row')?.innerText || '');
    if (!firstOuterGear.includes('Flak vest')) {
      throw new Error(`Gear value sort regression: ${firstOuterGear}`);
    }
    await page.click('[data-gear-slot-select="head"]');
    await page.selectOption('[data-gear-sort-select]', 'distance');
    await page.click('[data-equip-thing-id="8003"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'equip' &&
      m.username === VIEWER_LOGIN &&
      Number(m.thingId) === 8003
    ), 'equip command from gear slot');

    await page.click('[data-tab="social"]');
    await page.waitForSelector('.social-board [data-social-target="20202"]', { timeout: 10000 });
    const firstSocialAlpha = await page.locator('.social-person').first().innerText();
    if (!firstSocialAlpha.includes('Soap')) {
      throw new Error(`Social alpha sort regression: ${firstSocialAlpha}`);
    }
    await page.click('[data-social-sort="distance"]');
    const firstSocialNear = await page.locator('.social-person').first().innerText();
    if (!firstSocialNear.includes('Wig')) {
      throw new Error(`Social proximity sort regression: ${firstSocialNear}`);
    }
    await page.click('[data-social-target="20202"]');
    await page.click('[data-social-interaction="KindWords"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'social_interact' &&
      m.username === VIEWER_LOGIN &&
      Number(m.targetId) === 20202 &&
      m.interaction === 'KindWords'
    ), 'social_interact command from Social tab');

    await page.click('#btn-drawer-close');
    await page.click('#btn-command-menu');
    await page.waitForSelector('#command-window:not(.hidden)', { timeout: 10000 });
    await page.waitForSelector('.order-sheet .order-row', { timeout: 10000 });
    await page.waitForSelector('#command-window-status .status-summary.ok', { timeout: 10000 });
    await page.click('[data-command-section="story"]');
    await page.waitForSelector('.story-page', { timeout: 10000 });
    const storyText = await page.locator('.story-page').innerText();
    const storyTextLower = storyText.toLowerCase();
    if (!storyText.includes('Cave child') || !storyText.includes('Free appearance change') || !storyText.includes('Coins') || !storyText.includes('3,000') || !storyTextLower.includes('add trait') || !storyTextLower.includes('shuffle passions') || storyText.includes('not wired into the viewer tool yet')) {
      throw new Error(`Story page regression: ${storyText}`);
    }
    await page.selectOption('[data-story-purchase-select="add-trait"]', 'Jogger');
    await page.waitForSelector('[data-story-purchase-select="add-trait"]', { timeout: 10000 });
    await page.click('[data-story-purchase-sku="trait"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'toolkit_purchase' &&
      m.username === VIEWER_LOGIN &&
      m.purchase === 'trait' &&
      m.argument === 'Jogger:0'
    ), 'trait toolkit_purchase command from Story menu');
    await page.selectOption('[data-story-purchase-select="remove-trait"]', 'Cannibal');
    await page.waitForSelector('[data-story-purchase-select="remove-trait"]', { timeout: 10000 });
    await page.click('[data-story-purchase-sku="removetrait"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'toolkit_purchase' &&
      m.username === VIEWER_LOGIN &&
      m.purchase === 'removetrait' &&
      m.argument === 'Cannibal:0'
    ), 'remove trait toolkit_purchase command from Story menu');
    await page.click('[data-appearance-hair-step="1"]');
    await page.click('[data-appearance-gender="Female"]');
    await page.click('[data-command-action="preview-appearance"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'preview_appearance' &&
      m.username === VIEWER_LOGIN &&
      m.hairDef === 'Mohawk' &&
      m.gender === 'Female'
    ), 'preview_appearance command from Story menu');
    hostWs.send(JSON.stringify({
      type: 'action_result',
      target: VIEWER_LOGIN,
      action: 'preview_appearance',
      ok: true,
      message: 'Preview ready',
      appearancePreview: 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
      previewLabel: 'Preview',
    }));
    await page.waitForSelector('.appearance-preview img', { timeout: 10000 });
    await page.click('[data-command-action="set-appearance"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'set_appearance' &&
      m.username === VIEWER_LOGIN &&
      m.hairDef === 'Mohawk' &&
      m.gender === 'Female'
    ), 'set_appearance command from Story menu');
    await page.click('[data-command-section="buy"]');
    await page.waitForSelector('.buy-wallet-bar', { timeout: 10000 });
    await page.waitForSelector('[data-buy-sku="healme"]:not([disabled])', { timeout: 10000 });
    const buyTabs = await page.locator('.buy-shop-tab span').allTextContents();
    for (const expected of ['All', 'Medical', 'Food', 'Weapons', 'Apparel', 'Buildables', 'Pawn']) {
      if (!buyTabs.includes(expected)) {
        throw new Error(`Buy shop tab missing: ${expected} in ${buyTabs.join(', ')}`);
      }
    }
    await page.click('[data-buy-shop="food"]');
    await page.waitForSelector('[data-buy-sku="simplemeal"]', { timeout: 10000 });
    const foodShopText = await page.locator('.buy-shops').innerText();
    if (!foodShopText.includes('simple meal') || foodShopText.includes('healme')) {
      throw new Error(`Buy food shop regression: ${foodShopText}`);
    }
    await page.click('[data-buy-shop="all"]');
    const buyText = await page.locator('.toolkit-page').innerText();
    if (!buyText.includes('2,167 coins') || !buyText.includes('900 coins') || !buyText.includes('flak vest') || !buyText.includes('wall')) {
      throw new Error(`Buy price display regression: ${buyText}`);
    }
    const qtyType = await page.getAttribute('[data-buy-qty-input="medicine"]', 'type');
    if (qtyType !== 'text') {
      throw new Error(`Buy quantity input should be plain text, got ${qtyType}`);
    }
    await page.selectOption('[data-buy-stuff-select="flakvest"]', 'Plasteel');
    await page.click('[data-buy-sku="flakvest"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'toolkit_purchase' &&
      m.username === VIEWER_LOGIN &&
      m.purchase === 'flakvest' &&
      m.argument === 'Plasteel'
    ), 'material toolkit_purchase command from Buy menu');
    await page.click('[data-buy-qty-input="medicine"]');
    await page.fill('[data-buy-qty-input="medicine"]', '2');
    hostWs.send(JSON.stringify({
      type: 'toolkit_state',
      target: VIEWER_LOGIN,
      available: true,
      toolkitLoaded: true,
      toolkitUtilsLoaded: true,
      chatConnected: true,
      status: 'connected',
      username: VIEWER_LOGIN,
      coins: 3000,
      karma: 112,
      unlimitedCoins: false,
      earningCoins: true,
      coinAmount: 30,
      coinInterval: 2,
      minimumPurchase: 60,
      itemCount: 2,
      entries: toolkitEntries,
    }));
    await page.waitForFunction(() => document.activeElement?.matches('[data-buy-qty-input="medicine"]'), null, { timeout: 10000 });
    await page.fill('[data-buy-qty-input="medicine"]', '3');
    await page.locator('[data-buy-qty-input="medicine"]').press('Enter');
    await page.waitForFunction(() => document.querySelector('[data-buy-sku="medicine"]')?.closest('.buy-item')?.innerText.includes('2,700 coins'), null, { timeout: 10000 });
    await page.click('[data-buy-sku="healme"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'toolkit_purchase' &&
      m.username === VIEWER_LOGIN &&
      m.purchase === 'healme'
    ), 'toolkit_purchase command from Buy menu');
    await page.click('[data-buy-sku="medicine"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'toolkit_purchase' &&
      m.username === VIEWER_LOGIN &&
      m.purchase === 'medicine' &&
      Number(m.quantity) === 3
    ), 'quantity toolkit_purchase command from Buy menu');
    await page.click('[data-command-section="work"]');
    await page.waitForSelector('.work-row', { timeout: 10000 });
    const workHeaders = (await page.locator('.work-priority-head .priority-head-cell').allTextContents()).join('|');
    if (workHeaders !== '1|2|3|4|Off') {
      throw new Error(`Work priority columns regression: ${workHeaders}`);
    }
    const firstWork = await page.locator('.work-row .work-name').first().innerText();
    if (firstWork !== 'Work type') {
      throw new Error(`Work order regression: ${firstWork}`);
    }
    const firstActualWork = await page.locator('.work-row:not(.work-priority-head) .work-name').first().innerText();
    if (firstActualWork !== 'Firefighter') {
      throw new Error(`Work order regression: ${firstActualWork}`);
    }
    await page.click('[data-work-loadout="save"]');
    await page.click('[data-work-loadout="apply"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'set_work' &&
      m.username === VIEWER_LOGIN &&
      m.workDef === 'Cooking' &&
      Number(m.priority) === 3
    ), 'saved work loadout apply command');
    await page.waitForSelector('[data-work-def="Cooking"][data-priority="1"]', { timeout: 10000 });
    await page.click('[data-work-def="Cooking"][data-priority="1"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'set_work' &&
      m.username === VIEWER_LOGIN &&
      m.workDef === 'Cooking' &&
      Number(m.priority) === 1
    ), 'set_work command from viewer Orders menu');
    await page.click('[data-command-section="schedule"]');
    await page.waitForSelector('.schedule-strip .schedule-hour', { timeout: 10000 });
    const scheduleProbe = await page.locator('[data-schedule-select-hour="8"]').evaluate(el => ({
      className: el.className,
      text: el.innerText,
      swatchCount: el.querySelectorAll('.schedule-swatch').length,
    }));
    if (!scheduleProbe.className.includes('assignment-anything') || scheduleProbe.swatchCount !== 1 || scheduleProbe.text.includes('A')) {
      throw new Error(`Schedule color swatch regression: ${JSON.stringify(scheduleProbe)}`);
    }
    await page.click('[data-schedule-select-hour="8"]');
    await page.click('[data-schedule-hour="8"][data-next-assignment="Work"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'set_schedule' &&
      m.username === VIEWER_LOGIN &&
      Number(m.hour) === 8 &&
      m.assignment === 'Work'
    ), 'set_schedule command from schedule picker');
    await page.click('[data-schedule-block="sleep-night"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'set_schedule' &&
      m.username === VIEWER_LOGIN &&
      Number(m.hour) === 22 &&
      m.assignment === 'Sleep'
    ), 'set_schedule command from schedule block');

    // Visual/overflow gate at the compact browser-source dimensions reported by viewers.
    await page.click('#btn-command-close');
    await page.setViewportSize({ width: 735, height: 452 });
    await page.click('#btn-drawer-open');
    await page.waitForFunction(() => {
      const panel = document.getElementById('bottom-panel');
      return panel?.classList.contains('expanded') && panel.getBoundingClientRect().height > 180;
    }, null, { timeout: 10000 });
    const compactChrome = await page.evaluate(() => {
      const rect = id => {
        const box = document.getElementById(id)?.getBoundingClientRect();
        return box ? { left: box.left, top: box.top, right: box.right, bottom: box.bottom, width: box.width, height: box.height } : null;
      };
      const panel = rect('bottom-panel');
      const inspect = rect('inspect-pane');
      const commands = rect('cmd-bar');
      const overlaps = (a, b) => !!a && !!b && a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
      const topAt = box => box ? document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2) : null;
      const inspectTop = topAt(inspect);
      const commandTop = topAt(commands);
      const commandStyle = getComputedStyle(document.getElementById('cmd-bar'));
      const portrait = document.getElementById('pawn-portrait');
      return {
        panel, inspect, commands,
        inspectCovered: overlaps(panel, inspect),
        commandsCovered: overlaps(panel, commands),
        commandsVisible: commandStyle.opacity !== '0' && commandStyle.pointerEvents !== 'none',
        inspectOnTop: !!inspectTop?.closest('#inspect-pane'),
        commandsOnTop: !!commandTop?.closest('#cmd-bar'),
        inspectTop: inspectTop ? `${inspectTop.id || ''}.${inspectTop.className || ''}` : '',
        commandTop: commandTop ? `${commandTop.id || ''}.${commandTop.className || ''}` : '',
        portraitVisible: !!portrait?.getAttribute('src') && getComputedStyle(portrait).display !== 'none',
      };
    });
    if (compactChrome.inspectCovered || compactChrome.commandsCovered || !compactChrome.commandsVisible ||
        !compactChrome.commandsOnTop || !compactChrome.portraitVisible) {
      throw new Error(`Compact chrome overlap regression: ${JSON.stringify(compactChrome)}`);
    }
    if (SCREENSHOT_DIR) fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    if (SCREENSHOT_DIR) {
      await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'overlord-compact-chrome.png') });
      await page.locator('#inspect-pane').screenshot({ path: path.join(SCREENSHOT_DIR, 'overlord-compact-profile.png') });
      await page.locator('#cmd-bar').screenshot({ path: path.join(SCREENSHOT_DIR, 'overlord-compact-commands.png') });
    }
    for (const tabName of ['health', 'gear', 'social']) {
      await page.click(`[data-tab="${tabName}"]`);
      await page.waitForFunction(name => document.getElementById(`tab-${name}`)?.classList.contains('active'), tabName);
      const drawerOverflow = await page.evaluate(() => {
        const el = document.getElementById('tab-content');
        const pane = el.querySelector('.tab-pane.active');
        const children = Array.from(pane?.children || []).map(child => ({ id: child.id, height: child.getBoundingClientRect().height }));
        return { clientHeight: el.clientHeight, scrollHeight: el.scrollHeight, clientWidth: el.clientWidth, scrollWidth: el.scrollWidth, paneHeight: pane?.getBoundingClientRect().height, children };
      });
      if (drawerOverflow.scrollHeight > drawerOverflow.clientHeight + 1 || drawerOverflow.scrollWidth > drawerOverflow.clientWidth + 1) {
        throw new Error(`${tabName} compact drawer overflow: ${JSON.stringify(drawerOverflow)}`);
      }
      if (tabName === 'health') {
        const healthProfile = await page.evaluate(() => {
          const traits = document.getElementById('traits-list');
          const traitBox = traits?.getBoundingClientRect();
          const chips = Array.from(traits?.querySelectorAll('.trait-chip') || []);
          const portrait = document.getElementById('health-pawn-portrait');
          return {
            traitCount: chips.length,
            traitsContained: !!traitBox && chips.every(chip => {
              const box = chip.getBoundingClientRect();
              return box.left >= traitBox.left - 1 && box.right <= traitBox.right + 1 && box.top >= traitBox.top - 1 && box.bottom <= traitBox.bottom + 1;
            }),
            portraitVisible: !!portrait?.getAttribute('src') && getComputedStyle(portrait).display !== 'none',
          };
        });
        if (healthProfile.traitCount !== 4 || !healthProfile.traitsContained || !healthProfile.portraitVisible) {
          throw new Error(`Health profile regression: ${JSON.stringify(healthProfile)}`);
        }
      }
      if (SCREENSHOT_DIR) {
        await page.locator('#bottom-panel').screenshot({ path: path.join(SCREENSHOT_DIR, `overlord-${tabName}-compact.png`) });
      }
    }

    const eventsTab = page.locator('[data-tab="events"]');
    if (await eventsTab.isVisible()) throw new Error('Events tab should be hidden when host event capability is disabled');
    hostWs.send(JSON.stringify({
      type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4633',
      work: true, schedule: true, contextMenu: true, serverCameraZoom: true,
      toolkitBridge: true, storyPurchaseArguments: true, events: true,
    }));
    hostWs.send(JSON.stringify({ type: 'ticket_update', target: VIEWER_LOGIN, tickets: 2 }));
    await eventsTab.waitFor({ state: 'visible', timeout: 10000 });
    await eventsTab.click();
    await page.waitForSelector('.events-grid .evt-btn', { timeout: 10000 });
    if (await page.locator('.events-grid .evt-btn').count() !== 8) throw new Error('Events sheet did not expose all eight host incidents');
    await page.click('[data-trigger-event="wanderer"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' && m.action === 'trigger_event' && m.username === VIEWER_LOGIN && m.eventId === 'wanderer'
    ), 'trigger_event command from Events tab');
    if (SCREENSHOT_DIR) {
      await page.locator('#bottom-panel').screenshot({ path: path.join(SCREENSHOT_DIR, 'overlord-events-compact.png') });
    }
    hostWs.send(JSON.stringify({
      type: 'host_capabilities', target: VIEWER_LOGIN, rimworldVersion: '1.6.4633',
      work: true, schedule: true, contextMenu: true, serverCameraZoom: true,
      toolkitBridge: true, storyPurchaseArguments: true, events: false,
    }));
    await eventsTab.waitFor({ state: 'hidden', timeout: 10000 });

    // Very narrow browsers use the stacked Gear layout. Vertical detail scrolling
    // is allowed here, but the page itself, names, and core actions must remain
    // within the viewport and clickable.
    await page.setViewportSize({ width: 480, height: 700 });
    await page.click('[data-tab="gear"]');
    await page.waitForSelector('#tab-gear.active .gear-layout', { timeout: 10000 });
    const narrowGear = await page.evaluate(() => {
      const panel = document.getElementById('bottom-panel')?.getBoundingClientRect();
      const tab = document.getElementById('tab-gear');
      const names = Array.from(tab?.querySelectorAll('.gear-name') || []).map(name => ({
        text: name.textContent.trim(),
        clientWidth: name.clientWidth,
        scrollWidth: name.scrollWidth,
      }));
      const sourceButtons = Array.from(tab?.querySelectorAll('[data-gear-source]') || []);
      const action = tab?.querySelector('[data-equip-thing-id]');
      const actionBox = action?.getBoundingClientRect();
      const commands = document.getElementById('cmd-bar');
      const commandBox = commands?.getBoundingClientRect();
      const commandStyle = commands ? getComputedStyle(commands) : null;
      const commandTop = commandBox
        ? document.elementFromPoint(commandBox.left + commandBox.width / 2, commandBox.top + commandBox.height / 2)
        : null;
      return {
        viewportWidth: innerWidth,
        documentWidth: document.documentElement.scrollWidth,
        panel: panel ? { left: panel.left, top: panel.top, right: panel.right, bottom: panel.bottom, width: panel.width, height: panel.height } : null,
        tabHorizontalOverflow: !!tab && tab.scrollWidth > tab.clientWidth + 1,
        names,
        sourceButtons: sourceButtons.map(button => button.textContent.trim()),
        actionVisible: !!actionBox && actionBox.width > 0 && actionBox.height > 0 &&
          actionBox.left >= -1 && actionBox.right <= innerWidth + 1,
        commandsVisible: !!commandStyle && commandStyle.opacity !== '0' && commandStyle.pointerEvents !== 'none',
        commandBox: commandBox ? { left: commandBox.left, top: commandBox.top, right: commandBox.right, bottom: commandBox.bottom, width: commandBox.width, height: commandBox.height } : null,
        commandsCovered: !!panel && !!commandBox &&
          panel.left < commandBox.right && panel.right > commandBox.left &&
          panel.top < commandBox.bottom && panel.bottom > commandBox.top,
        commandsOnTop: !!commandTop?.closest('#cmd-bar'),
      };
    });
    if (narrowGear.documentWidth > narrowGear.viewportWidth + 1 ||
        narrowGear.tabHorizontalOverflow ||
        !narrowGear.sourceButtons.includes('Nearby') ||
        !narrowGear.sourceButtons.includes('Armory') ||
        !narrowGear.actionVisible ||
        !narrowGear.commandsVisible ||
        narrowGear.commandsCovered ||
        !narrowGear.commandsOnTop ||
        narrowGear.names.some(name => name.scrollWidth > name.clientWidth + 1)) {
      throw new Error(`Narrow Gear regression: ${JSON.stringify(narrowGear)}`);
    }
    if (SCREENSHOT_DIR) {
      await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'overlord-gear-narrow.png') });
    }

    // A full browser reload is the deterministic reconnect transition. The relay
    // must replay the assigned pawn exactly once and remain stable long enough to
    // prove build negotiation did not start another reload loop.
    const viewerJoinsBeforeReconnect = hostMessages.filter(m =>
      m.type === 'viewer_joined' && m.username === VIEWER_LOGIN
    ).length;
    await page.reload({ waitUntil: 'domcontentloaded' });
    await waitFor(() => hostMessages.filter(m =>
      m.type === 'viewer_joined' && m.username === VIEWER_LOGIN
    ).length === viewerJoinsBeforeReconnect + 1, 'single viewer reconnect');
    await page.waitForFunction(expectedName =>
      document.body.dataset.viewerPhase === 'assigned' &&
      document.getElementById('pawn-name')?.textContent.trim() === expectedName,
      VIEWER_DISPLAY,
      { timeout: 10000 });
    await wait(2200);
    const reconnectJoins = hostMessages.filter(m =>
      m.type === 'viewer_joined' && m.username === VIEWER_LOGIN
    ).length - viewerJoinsBeforeReconnect;
    if (reconnectJoins !== 1) {
      throw new Error(`Reconnect reload loop: expected one viewer_joined, got ${reconnectJoins}`);
    }
    await page.click('#btn-command-menu');
    await page.waitForSelector('#command-window-status .status-summary.ok', { timeout: 10000 });
    const reconnectCommandStatus = await page.locator('#command-window-status .status-summary').innerText();
    if (!reconnectCommandStatus.includes(`Assigned to ${VIEWER_DISPLAY}`)) {
      throw new Error(`Reconnect command state regression: ${reconnectCommandStatus}`);
    }
    await page.click('#btn-command-close');

    const result = await page.evaluate(reconnectCount => ({
      phase: document.body.dataset.viewerPhase,
      mainActive: document.getElementById('screen-main')?.classList.contains('active'),
      lobbyActive: document.getElementById('screen-lobby')?.classList.contains('active'),
      pawnName: document.getElementById('pawn-name')?.textContent.trim(),
      status: document.getElementById('status-text')?.textContent.trim(),
      commandWindowOpen: !document.getElementById('command-window')?.classList.contains('hidden'),
      activeCommandSection: document.querySelector('.command-nav-btn.active')?.textContent.trim(),
      commandStatus: document.querySelector('#command-window-status .status-summary')?.textContent.trim(),
      reconnectJoins: reconnectCount,
      viewport: { width: innerWidth, height: innerHeight },
    }), reconnectJoins);

    const summarizedHostMessages = SUMMARY_OUTPUT
      ? {
          total: hostMessages.length,
          types: hostMessages.reduce((counts, message) => {
            const key = message.type || 'unknown';
            counts[key] = (counts[key] || 0) + 1;
            return counts;
          }, {}),
          actions: hostMessages.reduce((counts, message) => {
            if (!message.action) return counts;
            counts[message.action] = (counts[message.action] || 0) + 1;
            return counts;
          }, {}),
        }
      : hostMessages.map(m => ({ type: m.type, action: m.action, username: m.username, pawnId: m.pawnId }));
    console.log(JSON.stringify({
      ok: true,
      baseUrl: BASE_URL,
      viewer: VIEWER_LOGIN,
      pawnId: PAWN_ID,
      result,
      hostMessages: summarizedHostMessages,
    }, null, 2));
  } finally {
    if (browser) await browser.close().catch(() => {});
    if (burstViewerWs) burstViewerWs.close();
    if (hostWs) hostWs.close();
    relay.child.kill();
    await wait(200);
  }
}

main().catch(error => {
  console.error(JSON.stringify({
    ok: false,
    error: error.message,
    stack: error.stack,
  }, null, 2));
  process.exit(1);
});
