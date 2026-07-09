'use strict';

const childProcess = require('child_process');
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

  try {
    await waitFor(() => requestJson('GET', '/health').catch(() => null), 'relay health', 15000);

    hostWs = new WebSocket(`${WS_URL}?role=host&secret=${encodeURIComponent(HOST_SECRET)}`);
    const hostMessages = [];
    hostWs.on('message', raw => {
      try { hostMessages.push(JSON.parse(raw.toString('utf8'))); } catch (_) {}
    });
    await waitForWsOpen(hostWs, 'host');

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
    await waitFor(() => hostMessages.find(m => m.type === 'viewer_joined' && m.username === VIEWER_LOGIN), 'viewer_joined');
    await waitFor(() => hostMessages.find(m => m.type === 'request_colonist_list' && m.username === VIEWER_LOGIN), 'initial colonist list request');

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
        health: { summaryHp: 100 },
        needs: { Mood: 74, Food: 75, Rest: 80, Joy: 53 },
        skills: [
          { name: 'Cooking', label: 'cooking', level: 3, passion: 0, disabled: false },
          { name: 'Mining', label: 'mining', level: 3, passion: 0, disabled: false },
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
        ],
        inventory: [
          { id: 7001, label: 'Simple meal', count: 1, hp: 100, ingestible: true },
          { id: 7002, label: 'Herbal medicine', count: 3, hp: 100, ingestible: false },
        ],
        relations: [
          { id: 20202, pawn: 'Wig', relation: 'friend' },
        ],
        opinions: [
          { id: 20202, pawn: 'Wig', opinion: 12, distance: 6 },
          { id: 30303, pawn: 'Soap', opinion: -3, distance: 9 },
        ],
        traits: [
          { defName: 'Cannibal', label: 'Cannibal', degree: 0 },
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
        thoughts: [],
      },
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
    const expectedContextCell = await page.evaluate(point => {
      return window.OverlordDebug.mapPointToCell(point.x, point.y);
    }, contextPoint);
    if (!expectedContextCell) {
      throw new Error('Live map context point did not resolve to a cell');
    }
    await page.evaluate(point => {
      const canvas = document.getElementById('map-canvas');
      canvas.dispatchEvent(new MouseEvent('contextmenu', {
        button: 2,
        clientX: point.x,
        clientY: point.y,
        bubbles: true,
        cancelable: true,
      }));
    }, contextPoint);
    const contextCommand = await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'context_menu' &&
      m.username === VIEWER_LOGIN
    ), 'context_menu command from live map right-click');
    if (Number(contextCommand.x) !== Number(expectedContextCell.x) || Number(contextCommand.z) !== Number(expectedContextCell.z)) {
      throw new Error(`Context menu cell mismatch: ${JSON.stringify({ expectedContextCell, contextCommand })}`);
    }
    hostWs.send(JSON.stringify({
      type: 'context_menu',
      target: VIEWER_LOGIN,
      ok: true,
      options: [
        { id: 0, label: 'Prioritize hauling steel', disabled: false },
        { id: 1, label: 'Cannot reach reserved thing', disabled: true },
      ],
    }));
    await page.waitForSelector('#context-menu:not(.hidden)', { timeout: 10000 });
    const contextBox = await page.locator('#context-menu').boundingBox();
    if (Math.abs(contextBox.x - contextPoint.x) > 80 || Math.abs(contextBox.y - contextPoint.y) > 80) {
      throw new Error(`Context menu was not anchored near click: ${JSON.stringify({ contextBox, contextPoint })}`);
    }

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
      return {
        noHorizontalOverflow: tab.scrollWidth <= tab.clientWidth + 1,
        slotLabels,
        actions,
        nearby,
        inventoryRows: document.querySelectorAll('.inventory-row').length,
      };
    });
    if (!gearLayout.noHorizontalOverflow || !gearLayout.slotLabels.includes('Head') || !gearLayout.actions.includes('Take off') || !gearLayout.nearby.includes('Equip')) {
      throw new Error(`Gear layout regression: ${JSON.stringify(gearLayout)}`);
    }
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
      m.argument === 'Jogger'
    ), 'trait toolkit_purchase command from Story menu');
    await page.selectOption('[data-story-purchase-select="remove-trait"]', 'Cannibal');
    await page.waitForSelector('[data-story-purchase-select="remove-trait"]', { timeout: 10000 });
    await page.click('[data-story-purchase-sku="removetrait"]');
    await waitFor(() => hostMessages.find(m =>
      m.type === 'command' &&
      m.action === 'toolkit_purchase' &&
      m.username === VIEWER_LOGIN &&
      m.purchase === 'removetrait' &&
      m.argument === 'Cannibal'
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
    await page.waitForSelector('.toolkit-wallet', { timeout: 10000 });
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
    if (!buyText.includes('Price 2,167 coins') || !buyText.includes('Unit 900 coins') || !buyText.includes('Total 900 coins') || !buyText.includes('flak vest') || !buyText.includes('wall')) {
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
    await page.waitForFunction(() => document.querySelector('[data-buy-sku="medicine"]')?.closest('.buy-item')?.innerText.includes('Total 2,700 coins'), null, { timeout: 10000 });
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

    const result = await page.evaluate(() => ({
      phase: document.body.dataset.viewerPhase,
      mainActive: document.getElementById('screen-main')?.classList.contains('active'),
      lobbyActive: document.getElementById('screen-lobby')?.classList.contains('active'),
      pawnName: document.getElementById('pawn-name')?.textContent.trim(),
      status: document.getElementById('status-text')?.textContent.trim(),
      commandWindowOpen: !document.getElementById('command-window')?.classList.contains('hidden'),
      activeCommandSection: document.querySelector('.command-nav-btn.active')?.textContent.trim(),
      commandStatus: document.querySelector('#command-window-status .status-summary')?.textContent.trim(),
    }));

    console.log(JSON.stringify({
      ok: true,
      baseUrl: BASE_URL,
      viewer: VIEWER_LOGIN,
      pawnId: PAWN_ID,
      result,
      hostMessages: hostMessages.map(m => ({ type: m.type, action: m.action, username: m.username, pawnId: m.pawnId })),
    }, null, 2));
  } finally {
    if (browser) await browser.close().catch(() => {});
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
