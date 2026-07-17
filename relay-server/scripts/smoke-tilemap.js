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
const MAP_WIDTH = 64;
const MAP_HEIGHT = 48;
const MAP_BUILDINGS = [
  [18, 18, 4, 8, 1, 30101, 'Wall', 'granite wall', 0, 0, -1, -1, 'wall', 0, '', 0, '', '', -1, '', -1, -1, -1, '', '', -1],
  [36, 15, 5, 3, 2, 30102, 'Door', 'steel door', 1, 0, -1, -1, 'door', 1, '', 0, '', '', -1, '', -1, -1, -1, '', '', -1],
  [22, 14, 3, 2, 4, 30103, 'FueledStove', 'fueled stove', 0, 2, 22, 13, 'workbench', 0, '', 3, 'Cook simple meal', 'BroTeam', PAWN_ID, 'DoBill', 30103, -1, -1, 'Cook simple meal 3x active', 'BroTeam Cook simple meal 42%', 42],
  [40, 18, 2, 1, 3, 30104, 'Bed', 'wooden bed', 1, 0, -1, -1, 'bed', 0, 'BroTeam', 0, '', '', -1, '', -1, -1, -1, '', '', -1],
];
const MAP_ITEMS = [
  [28, 24, 1, 1, 40101, 'Gun_BoltActionRifle', 'bolt-action rifle', 2, 'BroTeam', PAWN_ID, 'Equip', 40101, -1, -1],
  [31, 26, 3, 12, 40102, 'MealSimple', 'simple meal', 0],
  [34, 23, 5, 75, 40103, 'Steel', 'steel', 0],
];
const MAP_WORLD_ENTITIES = [
  { id: 50101, kind: 'fire', x: 38, z: 22, defName: 'Fire', label: 'fire' },
  { id: 50102, kind: 'plant', x: 29, z: 21, defName: 'Plant_Healroot', label: 'healroot', growth: 0.72 },
  { id: 50103, kind: 'construction', x: 24, z: 20, sizeX: 2, sizeZ: 1, defName: 'Frame_Wall', label: 'wall frame', rotation: 0, progress: 0.45, reserved: true, reservedById: PAWN_ID, reservedByLabel: VIEWER_DISPLAY, reservationJobDef: 'ConstructFinishFrame', reservationTargetId: 50103 },
];
const MAP_ENTITY_COUNT = 2 + MAP_BUILDINGS.length + MAP_ITEMS.length + MAP_WORLD_ENTITIES.length;

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

function sendHost(ws, msg) {
  ws.send(JSON.stringify(msg));
}

function makeTerrainBase64(width, height) {
  const terrain = Buffer.alloc(width * height);
  for (let z = 0; z < height; z++) {
    for (let x = 0; x < width; x++) {
      const edge = x === 0 || z === 0 || x === width - 1 || z === height - 1;
      const water = x > 42 && z > 30;
      const floor = x > 20 && x < 34 && z > 16 && z < 28;
      terrain[z * width + x] = edge ? 7 : water ? 21 : floor ? 30 : ((x + z) % 5 === 0 ? 2 : 1);
    }
  }
  return terrain.toString('base64');
}

function makeFogBase64(width, height) {
  const fog = Buffer.alloc(Math.ceil((width * height) / 8));
  for (let z = 0; z < height; z++) {
    for (let x = 0; x < width; x++) {
      if (x < 6 && z < 6) {
        const i = z * width + x;
        fog[i >> 3] |= 1 << (i & 7);
      }
    }
  }
  return fog.toString('base64');
}

function makeChunkBase64(width, height, value) {
  return Buffer.alloc(width * height, value).toString('base64');
}

function makeMapEntities(items = MAP_ITEMS) {
  return [
    { id: PAWN_ID, kind: 'pawn', x: 30, z: 24, faction: 1, defName: 'Human', label: VIEWER_DISPLAY },
    { id: 20202, kind: 'pawn', x: 35, z: 25, faction: 2, defName: 'Human', label: 'Raider' },
    ...MAP_BUILDINGS.map(b => ({
      id: b[5],
      kind: 'building',
      x: b[0],
      z: b[1],
      sizeX: b[2],
      sizeZ: b[3],
      buildingType: b[4],
      defName: b[6],
      label: b[7],
      rotation: b[8],
      flags: b[9],
      interactionX: b[10],
      interactionZ: b[11],
      role: b[12],
      hasInteractionCell: Number(b[10]) >= 0 && Number(b[11]) >= 0,
      open: b[13] === 1,
      owners: b[14] ? [b[14]] : [],
      billCount: b[15],
      billLabels: b[16] ? [b[16]] : [],
      reserved: Boolean(b[17]),
      reservedByLabel: b[17] || '',
      reservedById: b[18],
      reservationJobDef: b[19] || '',
      reservationTargetId: b[20],
      reservationTargetX: b[21],
      reservationTargetZ: b[22],
      billDetailSummary: b[23] || '',
      billDetails: b[23] ? [{
        label: b[16],
        recipeDef: 'CookMealSimple',
        suspended: false,
        shouldDoNow: true,
        repeatMode: 'RepeatCount',
        repeatInfo: '3x',
        repeatCount: 3,
        targetCount: 10,
        productCount: -1,
        ingredientSearchRadius: 999,
        ingredientFilterSummary: 'Usable ingredients',
        ingredientAllowedDefCount: 12,
        skillMin: 0,
        skillMax: 20,
        qualityMin: 'Awful',
        qualityMax: 'Legendary'
      }] : [],
      activeJobSummary: b[24] || '',
      activeJob: b[24] ? {
        active: true,
        workerId: PAWN_ID,
        workerLabel: VIEWER_DISPLAY,
        jobDef: 'DoBill',
        report: 'Cooking simple meal.',
        billLabel: b[16],
        recipeDef: 'CookMealSimple',
        workLeft: 58,
        workTotal: 100,
        progress: 0.42,
        progressPercent: 42,
        billStartTick: 12345,
        ticksSpentDoingRecipeWork: 90,
        toilIndex: 5,
        toil: 'DoRecipeWork',
        ticksLeftThisToil: 99999,
        activeSkill: 'Cooking',
        targetBId: 40102,
        targetBDef: 'MealSimple',
        targetBLabel: 'simple meal',
        targetBX: 31,
        targetBZ: 26
      } : {},
    })),
    ...items.map(item => ({
      id: item[4],
      kind: 'item',
      x: item[0],
      z: item[1],
      itemKind: item[2],
      stack: item[3],
      defName: item[5],
      label: item[6],
      flags: item[7],
      reserved: Boolean(item[8]),
      reservedByLabel: item[8] || '',
      reservedById: item[9],
      reservationJobDef: item[10] || '',
      reservationTargetId: item[11],
      reservationTargetX: item[12],
      reservationTargetZ: item[13],
    })),
    ...MAP_WORLD_ENTITIES,
  ];
}

function makeEntityStateMessage(type = 'entity_keyframe', items = MAP_ITEMS, overrides = {}) {
  const entityKeyframe = type === 'entity_keyframe';
  return {
    type,
    target: VIEWER_LOGIN,
    viewerPawnId: PAWN_ID,
    entities: makeMapEntities(items),
    removedEntities: [],
    entityCount: 2 + MAP_BUILDINGS.length + items.length + MAP_WORLD_ENTITIES.length,
    entitiesTruncated: false,
    entityKeyframe,
    entityProtocol: 'vdr/entity/0',
    entityEpoch: 1,
    entitySeq: entityKeyframe ? 1 : 2,
    entityBaseSeq: entityKeyframe ? 0 : 1,
    entitySnapshot: entityKeyframe,
    itemCount: items.length,
    itemsTruncated: false,
    worldEntityCount: MAP_WORLD_ENTITIES.length,
    worldEntitiesTruncated: false,
    ...overrides,
  };
}

function makePawnState() {
  return {
    id: PAWN_ID,
    name: VIEWER_DISPLAY,
    currentJob: 'Smoke synced',
    drafted: false,
    health: { summaryHp: 100, hediffs: [] },
    needs: { Mood: 74, Food: 75, Rest: 80, Joy: 53 },
    skills: [],
    capacities: [],
    work: {},
    workPriorities: [],
    schedule: Array.from({ length: 24 }, () => 'Anything'),
    scheduleAssignments: [],
    apparel: [],
    nearbyEquipment: [],
    inventory: [],
    relations: [],
    opinions: [],
    traits: [],
    thoughts: [],
  };
}

function sendAssignedColonistList(hostWs) {
  sendHost(hostWs, {
    type: 'colonist_list',
    target: VIEWER_LOGIN,
    hostMap: true,
    colonists: [
      {
        id: PAWN_ID,
        name: VIEWER_DISPLAY,
        assignedTo: VIEWER_LOGIN,
        assignedDisplayName: VIEWER_DISPLAY,
      },
    ],
  });
}

function sendAssignedSnapshot(hostWs) {
  sendAssignedColonistList(hostWs);
  sendHost(hostWs, {
    type: 'pawn_state',
    target: VIEWER_LOGIN,
    state: makePawnState(),
  });
  sendHost(hostWs, {
    type: 'permissions',
    target: VIEWER_LOGIN,
    draft: true,
    move: true,
    attack: true,
    equip: true,
  });
}

function sendMapSnapshot(hostWs, mapEpoch = 1) {
  sendHost(hostWs, {
    type: 'map_full',
    target: VIEWER_LOGIN,
    protocol: 'vdr/0',
    mapEpoch,
    seq: 1,
    baseSeq: 0,
    snapshot: true,
    width: MAP_WIDTH,
    height: MAP_HEIGHT,
    terrain: makeTerrainBase64(MAP_WIDTH, MAP_HEIGHT),
    fog: makeFogBase64(MAP_WIDTH, MAP_HEIGHT),
    visibilityFilteredMap: true,
  });
  sendHost(hostWs, {
    type: 'map_chunk',
    target: VIEWER_LOGIN,
    protocol: 'vdr/0',
    mapEpoch,
    chunkSeq: 1,
    chunkBaseSeq: 0,
    chunkX: 2,
    chunkZ: 2,
    chunkSize: 4,
    x: 8,
    z: 8,
    width: 4,
    height: 4,
    terrain: makeChunkBase64(4, 4, 6),
    fog: Buffer.alloc(2).toString('base64'),
    visibilityFilteredMap: true,
  });
  sendHost(hostWs, {
    type: 'map_delta',
    target: VIEWER_LOGIN,
    protocol: 'vdr/0',
    mapEpoch,
    seq: 2,
    baseSeq: 1,
    viewerPawnId: PAWN_ID,
    pawns: [
      [30, 24, 1, PAWN_ID, VIEWER_DISPLAY],
      [35, 25, 2, 20202, 'Raider'],
    ],
    buildings: MAP_BUILDINGS,
    items: MAP_ITEMS,
    itemCount: MAP_ITEMS.length,
    itemsTruncated: false,
    entities: makeMapEntities(),
    entityCount: MAP_ENTITY_COUNT,
    entitiesTruncated: false,
    entityKeyframe: true,
    removedEntities: [],
  });
  sendHost(hostWs, makeEntityStateMessage('entity_keyframe', MAP_ITEMS, {
    protocol: 'vdr/0',
    mapEpoch,
    seq: 2,
    baseSeq: 1,
    entityEpoch: mapEpoch,
    entitySeq: 1,
    entityBaseSeq: 0,
    entitySnapshot: true,
  }));
}

function findCommandAfter(messages, cursor, action, predicate = () => true) {
  return messages.slice(cursor).find(m =>
    m.type === 'command' &&
    m.action === action &&
    m.username === VIEWER_LOGIN &&
    predicate(m)
  );
}

async function waitForCommandAfter(messages, cursor, action, label, predicate = () => true) {
  return waitFor(() => findCommandAfter(messages, cursor, action, predicate), label);
}

function assertNoCommandAfter(messages, cursor, action, label) {
  const match = findCommandAfter(messages, cursor, action);
  if (match) {
    throw new Error(`${label}: unexpected ${action} command ${JSON.stringify(match)}`);
  }
}

function findMessageAfter(messages, cursor, type, predicate = () => true) {
  return messages.slice(cursor).find(m =>
    m.type === type &&
    m.username === VIEWER_LOGIN &&
    predicate(m)
  );
}

async function waitForMessageAfter(messages, cursor, type, label, predicate = () => true) {
  return waitFor(() => findMessageAfter(messages, cursor, type, predicate), label);
}

function assertNoMessageAfter(messages, cursor, type, label) {
  const match = findMessageAfter(messages, cursor, type);
  if (match) {
    throw new Error(`${label}: unexpected ${type} message ${JSON.stringify(match)}`);
  }
}

async function readTileDebug(page) {
  return page.evaluate(() => {
    const debug = window.OverlordDebug || null;
    const canvas = document.getElementById('map-canvas');
    const rect = canvas ? canvas.getBoundingClientRect() : null;
    let state = null;
    let rendererState = null;

    try {
      state = debug && typeof debug.getState === 'function' ? debug.getState() : null;
    } catch (error) {
      state = { error: error.message };
    }

    try {
      const tileRenderer = window.TileMapRenderer;
      rendererState = tileRenderer && typeof tileRenderer.getDebugState === 'function'
        ? tileRenderer.getDebugState()
        : null;
    } catch (error) {
      rendererState = { error: error.message };
    }

    return {
      hasDebug: !!debug,
      hasMapper: !!debug && typeof debug.mapPointToCell === 'function',
      bodyPhase: document.body?.dataset?.viewerPhase || '',
      state,
      rendererState,
      canvas: canvas ? {
        className: String(canvas.className || ''),
        width: canvas.width,
        height: canvas.height,
        cssWidth: rect ? rect.width : 0,
        cssHeight: rect ? rect.height : 0,
      } : null,
    };
  });
}

function debugShowsTileMapActive(debug) {
  const state = debug?.state || {};
  const rendererState = debug?.rendererState || {};
  const stateTile = state.tileMap || {};
  const rendererActive = rendererState.active === true || rendererState.hasTileData === true;
  const stateActive = state.hasTileData === true || state.tileMapActive === true || stateTile.active === true;
  return debug?.bodyPhase === 'assigned' &&
    debug?.canvas?.cssWidth > 0 &&
    debug?.canvas?.cssHeight > 0 &&
    (stateActive || rendererActive);
}

async function waitForTileMapActive(page) {
  try {
    return await waitFor(async () => {
      const debug = await readTileDebug(page);
      return debugShowsTileMapActive(debug) ? debug : null;
    }, 'tilemap activation from map_full/map_delta', 10000);
  } catch (error) {
    const debug = await readTileDebug(page).catch(readError => ({ readError: readError.message }));
    throw new Error(`${error.message}; last debug state ${JSON.stringify(debug)}`);
  }
}

async function waitForTileMapReset(page) {
  try {
    return await waitFor(async () => {
      const debug = await readTileDebug(page);
      const state = debug?.state || {};
      const tileMap = state.tileMap || null;
      const tileMapCleared = !state.hasTileData && (!tileMap || tileMap.active === false);
      const pawnCleared = state.hasPawn === false;
      return debug?.bodyPhase === 'lobby' && tileMapCleared && pawnCleared ? debug : null;
    }, 'tilemap reset after host reconnect signal', 10000);
  } catch (error) {
    const debug = await readTileDebug(page).catch(readError => ({ readError: readError.message }));
    throw new Error(`${error.message}; last debug state ${JSON.stringify(debug)}`);
  }
}

async function waitForCanvasPaint(page) {
  return waitFor(() => page.evaluate(() => {
    const canvas = document.getElementById('map-canvas');
    if (!canvas || !canvas.width || !canvas.height) return null;
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;

    const points = [
      [0.50, 0.50],
      [0.54, 0.50],
      [0.50, 0.56],
      [0.46, 0.44],
    ];
    const samples = points.map(([px, py]) => {
      const x = Math.max(0, Math.min(canvas.width - 1, Math.floor(canvas.width * px)));
      const y = Math.max(0, Math.min(canvas.height - 1, Math.floor(canvas.height * py)));
      return Array.from(ctx.getImageData(x, y, 1, 1).data);
    });
    const painted = samples.some(([r, g, b, a]) => a > 0 && (r > 20 || g > 20 || b > 20));
    return painted ? { width: canvas.width, height: canvas.height, samples } : null;
  }), 'tilemap canvas paint', 5000);
}

async function mapPointToCell(page, point, label) {
  const result = await page.evaluate(({ x, y }) => {
    const debug = window.OverlordDebug || null;
    const state = debug && typeof debug.getState === 'function' ? debug.getState() : null;
    if (!debug) return { error: 'window.OverlordDebug missing', state };
    if (typeof debug.mapPointToCell !== 'function') {
      return { error: 'window.OverlordDebug.mapPointToCell missing', state };
    }
    try {
      return { cell: debug.mapPointToCell(x, y), state };
    } catch (error) {
      return { error: error.message, state };
    }
  }, point);

  const raw = result.cell || {};
  const x = Number(raw.x);
  const z = Number(raw.z ?? raw.y);
  if (result.error || !result.cell || !Number.isFinite(x) || !Number.isFinite(z)) {
    throw new Error(`${label} did not resolve to a tile cell: ${JSON.stringify(result)}`);
  }
  return { x, z };
}

async function cellToMapPoint(page, cell, label) {
  const result = await page.evaluate(({ x, z }) => {
    const canvas = document.getElementById('map-canvas');
    const debug = window.OverlordDebug || null;
    const state = debug && typeof debug.getState === 'function' ? debug.getState() : null;
    const tileMap = state?.tileMap || null;
    const rect = canvas ? canvas.getBoundingClientRect() : null;
    if (!canvas || !rect || !tileMap) {
      return { error: 'tile map debug unavailable', state };
    }
    const zoom = Number(tileMap.zoom) || 1;
    const camX = Number(tileMap.camX) || 0;
    const camY = Number(tileMap.camY) || 0;
    const sx = (Number(x) + 0.5 - camX) * zoom + canvas.width / 2;
    const sy = (Number(z) + 0.5 - camY) * zoom + canvas.height / 2;
    return {
      point: {
        x: rect.left + (sx / canvas.width) * rect.width,
        y: rect.top + (sy / canvas.height) * rect.height,
      },
      state,
    };
  }, cell);

  const point = result.point || {};
  if (result.error || !Number.isFinite(Number(point.x)) || !Number.isFinite(Number(point.y))) {
    throw new Error(`${label} did not resolve to a screen point: ${JSON.stringify(result)}`);
  }
  return { x: Number(point.x), y: Number(point.y) };
}

function sameCell(command, cell) {
  return Number(command.x) === Number(cell.x) && Number(command.z) === Number(cell.z);
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

    sendHost(hostWs, {
      type: 'host_capabilities',
      target: VIEWER_LOGIN,
      rimworldVersion: '1.6.4633',
      contextMenu: true,
      serverCameraZoom: true,
      toolkitBridge: true,
    });

    sendHost(hostWs, {
      type: 'colonist_list',
      target: VIEWER_LOGIN,
      hostMap: true,
      colonists: [
        { id: PAWN_ID, name: VIEWER_DISPLAY },
      ],
    });

    await page.waitForSelector('.colonist-row .claim-btn:not([disabled])', { timeout: 10000 });
    await page.click('.colonist-row .claim-btn');

    await waitForCommandAfter(
      hostMessages,
      0,
      'claim_colonist',
      'claim_colonist command',
      m => Number(m.pawnId) === PAWN_ID
    );

    sendHost(hostWs, {
      type: 'command_result',
      target: VIEWER_LOGIN,
      action: 'claim_colonist',
      ok: true,
      message: `Assigned to ${VIEWER_DISPLAY}`,
    });
    sendAssignedSnapshot(hostWs);

    await page.waitForFunction(() => (
      document.body.dataset.viewerPhase === 'assigned' &&
      document.getElementById('screen-main')?.classList.contains('active') &&
      document.getElementById('pawn-name')?.textContent.trim() === 'BroTeam'
    ), null, { timeout: 10000 });

    sendHost(hostWs, {
      type: 'map_delta',
      target: VIEWER_LOGIN,
      viewerPawnId: PAWN_ID,
      pawns: [
        [30, 24, 1, PAWN_ID, VIEWER_DISPLAY],
        [35, 25, 2, 20202, 'Raider'],
      ],
      buildings: MAP_BUILDINGS,
      items: MAP_ITEMS,
      itemCount: MAP_ITEMS.length,
      itemsTruncated: false,
      entities: makeMapEntities(),
      entityCount: MAP_ENTITY_COUNT,
      entitiesTruncated: false,
      entityKeyframe: true,
      removedEntities: [],
    });
    sendHost(hostWs, makeEntityStateMessage('entity_keyframe'));
    sendHost(hostWs, {
      type: 'map_full',
      target: VIEWER_LOGIN,
      width: MAP_WIDTH,
      height: MAP_HEIGHT,
      terrain: makeTerrainBase64(MAP_WIDTH, MAP_HEIGHT),
      fog: makeFogBase64(MAP_WIDTH, MAP_HEIGHT),
      visibilityFilteredMap: true,
    });
    sendHost(hostWs, {
      type: 'map_chunk',
      target: VIEWER_LOGIN,
      chunkX: 2,
      chunkZ: 2,
      chunkSize: 4,
      x: 8,
      z: 8,
      width: 4,
      height: 4,
      terrain: makeChunkBase64(4, 4, 6),
      fog: Buffer.alloc(2).toString('base64'),
      visibilityFilteredMap: true,
    });
    sendHost(hostWs, {
      type: 'map_delta',
      target: VIEWER_LOGIN,
      viewerPawnId: PAWN_ID,
      pawns: [
        [30, 24, 1, PAWN_ID, VIEWER_DISPLAY],
        [35, 25, 2, 20202, 'Raider'],
      ],
      buildings: MAP_BUILDINGS,
      items: MAP_ITEMS,
      itemCount: MAP_ITEMS.length,
      itemsTruncated: false,
      entities: makeMapEntities(),
      entityCount: MAP_ENTITY_COUNT,
      entitiesTruncated: false,
      entityKeyframe: true,
      removedEntities: [],
    });
    sendHost(hostWs, makeEntityStateMessage('entity_keyframe'));

    const tileDebug = await waitForTileMapActive(page);
    const rendererState = tileDebug.rendererState || tileDebug.state?.tileMap || {};
    if (rendererState.itemCount !== MAP_ITEMS.length || rendererState.itemTotal !== MAP_ITEMS.length) {
      throw new Error(`Tilemap did not expose structured item data: ${JSON.stringify(rendererState)}`);
    }
    if (rendererState.hasFog !== true || rendererState.fogCount <= 0) {
      throw new Error(`Tilemap did not expose fogged baseline cells: ${JSON.stringify(rendererState)}`);
    }
    if (!Number.isFinite(Number(rendererState.chunkApplyCount)) || Number(rendererState.chunkApplyCount) < 1) {
      throw new Error(`Tilemap did not apply map_chunk data: ${JSON.stringify(rendererState)}`);
    }
    if (Number(rendererState.visualIdentityVersion) < 1) {
      throw new Error(`Tilemap did not expose visual identity glyph layer: ${JSON.stringify(rendererState)}`);
    }
    const firstBuilding = Array.isArray(rendererState.buildingSample) ? rendererState.buildingSample[0] : null;
    if (!Array.isArray(firstBuilding) || firstBuilding[5] !== MAP_BUILDINGS[0][5] || firstBuilding[6] !== MAP_BUILDINGS[0][6]) {
      throw new Error(`Tilemap did not preserve richer building metadata: ${JSON.stringify(rendererState)}`);
    }
    const semanticBuilding = Array.isArray(rendererState.buildingSample)
      ? rendererState.buildingSample.find(entry => Array.isArray(entry) && entry[12] === 'workbench')
      : null;
    if (!semanticBuilding || semanticBuilding[9] !== 2 || semanticBuilding[10] !== 22 || semanticBuilding[11] !== 13 || semanticBuilding[15] !== 3 || semanticBuilding[16] !== 'Cook simple meal' || semanticBuilding[17] !== 'BroTeam' || semanticBuilding[18] !== PAWN_ID || semanticBuilding[19] !== 'DoBill' || semanticBuilding[20] !== 30103 || semanticBuilding[23] !== 'Cook simple meal 3x active' || semanticBuilding[24] !== 'BroTeam Cook simple meal 42%' || semanticBuilding[25] !== 42) {
      throw new Error(`Tilemap did not preserve building semantic flags/interactions: ${JSON.stringify(rendererState)}`);
    }
    const doorBuilding = Array.isArray(rendererState.buildingSample)
      ? rendererState.buildingSample.find(entry => Array.isArray(entry) && entry[12] === 'door')
      : null;
    const bedBuilding = Array.isArray(rendererState.buildingSample)
      ? rendererState.buildingSample.find(entry => Array.isArray(entry) && entry[12] === 'bed')
      : null;
    if (!doorBuilding || doorBuilding[13] !== 1 || !bedBuilding || bedBuilding[14] !== 'BroTeam') {
      throw new Error(`Tilemap did not preserve door/bed semantic details: ${JSON.stringify(rendererState)}`);
    }
    const firstItem = Array.isArray(rendererState.itemSample) ? rendererState.itemSample[0] : null;
    if (!Array.isArray(firstItem) || firstItem[4] !== MAP_ITEMS[0][4] || firstItem[6] !== MAP_ITEMS[0][6]) {
      throw new Error(`Tilemap did not preserve item metadata: ${JSON.stringify(rendererState)}`);
    }
    if (firstItem[7] !== 2 || firstItem[8] !== 'BroTeam' || firstItem[9] !== PAWN_ID || firstItem[10] !== 'Equip' || firstItem[11] !== MAP_ITEMS[0][4]) {
      throw new Error(`Tilemap did not preserve item reservation metadata: ${JSON.stringify(rendererState)}`);
    }
    if (rendererState.entityCount !== MAP_ENTITY_COUNT || rendererState.entityTotal !== MAP_ENTITY_COUNT) {
      throw new Error(`Tilemap did not expose keyed entity table: ${JSON.stringify(rendererState)}`);
    }
    if (rendererState.entityKeyframe !== true) {
      throw new Error(`Initial entity payload was not treated as a keyframe: ${JSON.stringify(rendererState)}`);
    }
    const initialEntityStream = tileDebug.state?.entityStream || {};
    if (initialEntityStream.entityEpoch !== 1 || initialEntityStream.seq !== 1) {
      throw new Error(`Initial entity stream envelope was not accepted: ${JSON.stringify(tileDebug)}`);
    }
    const worldKinds = new Set((Array.isArray(rendererState.worldEntitySample) ? rendererState.worldEntitySample : [])
      .map(entity => entity?.kind));
    if (rendererState.worldEntityCount !== MAP_WORLD_ENTITIES.length ||
        !worldKinds.has('fire') ||
        !worldKinds.has('plant') ||
        !worldKinds.has('construction')) {
      throw new Error(`Tilemap did not expose world entity symbols: ${JSON.stringify(rendererState)}`);
    }
    const constructionSample = (Array.isArray(rendererState.worldEntitySample) ? rendererState.worldEntitySample : [])
      .find(entity => entity?.kind === 'construction');
    if (!constructionSample || Math.abs(Number(constructionSample.progress) - 0.45) > 0.001) {
      throw new Error(`Tilemap did not expose construction progress: ${JSON.stringify(rendererState)}`);
    }

    const movedPawnX = 34;
    sendHost(hostWs, makeEntityStateMessage('entity_delta', MAP_ITEMS, {
      entityEpoch: 1,
      entitySeq: 2,
      entityBaseSeq: 1,
      entitySnapshot: false,
      entities: [
        { id: PAWN_ID, kind: 'pawn', x: movedPawnX, z: 24, faction: 1, defName: 'Human', label: VIEWER_DISPLAY },
      ],
      entityCount: MAP_ENTITY_COUNT,
      entityUpdateCount: 1,
      entityKeyframe: false,
      removedEntities: [],
    }));
    const interpolationDebug = await waitFor(async () => {
      const debug = await readTileDebug(page);
      const state = debug?.state?.tileMap || {};
      const sample = Array.isArray(state.pawnVisualSample)
        ? state.pawnVisualSample.find(pawn => Number(pawn.id) === PAWN_ID)
        : null;
      if (!sample) return null;
      const moving = state.interpolatingPawnCount > 0 &&
        sample.interpolating === true &&
        sample.targetX === movedPawnX &&
        sample.x < movedPawnX - 0.1;
      return moving ? debug : null;
    }, 'pawn movement interpolation updates display position without replacing authority');

    const removedItemId = MAP_ITEMS[1][4];
    sendHost(hostWs, makeEntityStateMessage('entity_delta', MAP_ITEMS.filter(item => item[4] !== removedItemId), {
      entityEpoch: 1,
      entitySeq: 3,
      entityBaseSeq: 2,
      entitySnapshot: false,
      entities: [],
      removedEntities: [removedItemId],
    }));
    const removalDebug = await waitFor(async () => {
      const debug = await readTileDebug(page);
      const state = debug?.state?.tileMap || {};
      const samples = Array.isArray(state.itemSample) ? state.itemSample : [];
      const removed = Array.isArray(state.removedEntities) ? state.removedEntities : [];
      const stillHasRemoved = samples.some(item => Array.isArray(item) && Number(item[4]) === removedItemId);
      return state.itemCount === MAP_ITEMS.length - 1 &&
        state.entityCount === MAP_ENTITY_COUNT - 1 &&
        debug?.state?.entityStream?.entityEpoch === 1 &&
        debug?.state?.entityStream?.seq === 3 &&
        removed.includes(removedItemId) &&
        !stillHasRemoved
        ? debug
        : null;
    }, 'keyed entity removal updates tilemap table');
    const paint = await waitForCanvasPaint(page);
    if (!tileDebug.hasMapper) {
      throw new Error(`Tilemap debug mapper is not exposed: ${JSON.stringify(tileDebug)}`);
    }

    const resyncCursor = hostMessages.length;
    sendHost(hostWs, {
      type: 'host_connected',
      target: VIEWER_LOGIN,
    });
    const resyncColonistRequest = await waitForMessageAfter(
      hostMessages,
      resyncCursor,
      'request_colonist_list',
      'request_colonist_list after host reconnect signal'
    );
    const resyncStateRequest = await waitForMessageAfter(
      hostMessages,
      resyncCursor,
      'request_state',
      'request_state after host reconnect signal'
    );
    const resetDebug = await waitForTileMapReset(page);

    sendAssignedSnapshot(hostWs);
    sendMapSnapshot(hostWs, 2);
    const resyncTileDebug = await waitForTileMapActive(page);
    const resyncPaint = await waitForCanvasPaint(page);
    if (!resyncTileDebug.hasMapper) {
      throw new Error(`Tilemap debug mapper is not exposed after resync: ${JSON.stringify(resyncTileDebug)}`);
    }

    const gapCursor = hostMessages.length;
    const clientLogCursor = await page.evaluate(() => window.OverlordDebug?.logs?.length || 0);
    const streamBeforeGap = await page.evaluate(() => window.OverlordDebug?.getState?.().mapStream || null);
    const gapEpoch = Number(streamBeforeGap?.mapEpoch || 2);
    const gapBaseSeq = Number(streamBeforeGap?.seq || 2) + 1;
    sendHost(hostWs, {
      type: 'map_delta',
      target: VIEWER_LOGIN,
      protocol: 'vdr/0',
      mapEpoch: gapEpoch,
      seq: gapBaseSeq + 1,
      baseSeq: gapBaseSeq,
      viewerPawnId: PAWN_ID,
      pawns: [
        [31, 24, 1, PAWN_ID, VIEWER_DISPLAY],
      ],
      buildings: [],
      items: [],
      itemCount: 0,
      itemsTruncated: false,
      entities: [
        { id: PAWN_ID, kind: 'pawn', x: 31, z: 24, faction: 1, defName: 'Human', label: VIEWER_DISPLAY },
      ],
      removedEntities: [],
      entityKeyframe: false,
      entityCount: 1,
      entitiesTruncated: false,
    });
    const clientResyncLog = await waitFor(() => page.evaluate(cursor => {
      const logs = window.OverlordDebug?.logs || [];
      return logs.slice(cursor).find(entry => entry.event === 'map_resync_request' && entry.reason === 'seq_gap') || null;
    }, clientLogCursor), 'client map_resync_request log after sequence gap');
    const relayReplayedFull = await waitFor(() => page.evaluate(cursor => {
      const logs = window.OverlordDebug?.logs || [];
      return logs.slice(cursor).find(entry => entry.event === 'recv' && entry.type === 'map_full') || null;
    }, clientLogCursor), 'relay-cached map_full replay after sequence gap');
    const relayReplayedChunk = await waitFor(() => page.evaluate(({ cursor, fullTs }) => {
      const logs = window.OverlordDebug?.logs || [];
      const recent = logs.slice(cursor);
      const fullIndex = recent.findIndex(entry => entry.event === 'recv' && entry.type === 'map_full' && entry.ts === fullTs);
      if (fullIndex < 0) return null;
      return recent.slice(fullIndex + 1).find(entry => entry.event === 'recv' && entry.type === 'map_chunk') || null;
    }, { cursor: clientLogCursor, fullTs: relayReplayedFull.ts }), 'relay-cached map_chunk replay after sequence gap');
    const relayReplayedDelta = await waitFor(() => page.evaluate(({ cursor, fullTs }) => {
      const logs = window.OverlordDebug?.logs || [];
      const recent = logs.slice(cursor);
      const fullIndex = recent.findIndex(entry => entry.event === 'recv' && entry.type === 'map_full' && entry.ts === fullTs);
      if (fullIndex < 0) return null;
      return recent.slice(fullIndex + 1).find(entry => entry.event === 'recv' && entry.type === 'map_delta') || null;
    }, { cursor: clientLogCursor, fullTs: relayReplayedFull.ts }), 'relay-cached map_delta replay after sequence gap');
    const relayReplayedEntityKeyframe = await waitFor(() => page.evaluate(cursor => {
      const logs = window.OverlordDebug?.logs || [];
      return logs.slice(cursor).find(entry => entry.event === 'recv' && entry.type === 'entity_keyframe') || null;
    }, clientLogCursor), 'relay-cached entity_keyframe replay after sequence gap');
    await wait(300);
    assertNoMessageAfter(hostMessages, gapCursor, 'state_resync_request', 'relay cache handled state_resync_request');
    assertNoMessageAfter(hostMessages, gapCursor, 'request_state', 'relay capabilities skipped compat request_state after sequence gap');
    let gapRepairDebug = null;
    try {
      gapRepairDebug = await waitFor(async () => {
        const debug = await readTileDebug(page);
        return debugShowsTileMapActive(debug) &&
          Number(debug?.state?.mapStream?.mapEpoch) === gapEpoch &&
          Number(debug?.state?.mapStream?.seq) === 2
          ? debug
          : null;
      }, 'tilemap sequence gap repair');
    } catch (error) {
      const debug = await readTileDebug(page).catch(readError => ({ readError: readError.message }));
      const logs = await page.evaluate(cursor => (window.OverlordDebug?.logs || []).slice(cursor), clientLogCursor)
        .catch(readError => [{ readError: readError.message }]);
      throw new Error(`${error.message}; last debug ${JSON.stringify(debug)}; recent logs ${JSON.stringify(logs)}`);
    }

    const mapBox = await page.locator('#map-canvas').boundingBox();
    if (!mapBox || mapBox.width <= 0 || mapBox.height <= 0) {
      throw new Error(`Map canvas has no usable bounding box: ${JSON.stringify(mapBox)}`);
    }

    const panCursor = hostMessages.length;
    await page.keyboard.down('w');
    await wait(40);
    await page.keyboard.up('w');
    await page.keyboard.down('a');
    await wait(40);
    await page.keyboard.up('a');
    await page.keyboard.down('s');
    await wait(40);
    await page.keyboard.up('s');
    await page.keyboard.down('d');
    await wait(40);
    await page.keyboard.up('d');
    await page.mouse.move(mapBox.x + mapBox.width * 0.50, mapBox.y + mapBox.height * 0.50);
    await page.mouse.down();
    await page.mouse.move(mapBox.x + mapBox.width * 0.515, mapBox.y + mapBox.height * 0.515, { steps: 6 });
    await page.mouse.up();
    await wait(500);
    assertNoCommandAfter(hostMessages, panCursor, 'camera_zoom', 'WASD/drag tilemap input');

    const leftPoint = {
      x: mapBox.x + mapBox.width * 0.55,
      y: mapBox.y + mapBox.height * 0.52,
    };
    const expectedMoveCell = await mapPointToCell(page, leftPoint, 'left click point');
    await page.evaluate(point => {
      document.getElementById('map-canvas')?.dispatchEvent(new MouseEvent('mousemove', {
        clientX: point.x, clientY: point.y, bubbles: true,
      }));
    }, leftPoint);
    const hoverAffordanceDebug = await waitFor(async () => {
      const debug = await readTileDebug(page);
      const tileState = debug?.state?.tileMap || debug?.rendererState || {};
      const hover = tileState.hoverCell || {};
      return Number(hover.x) === Number(expectedMoveCell.x) && Number(hover.z) === Number(expectedMoveCell.z)
        ? debug
        : null;
    }, 'tilemap hover affordance follows pointer cell');
    const moveCursor = hostMessages.length;
    await page.evaluate(point => {
      document.getElementById('map-canvas')?.dispatchEvent(new MouseEvent('mousedown', {
        button: 0, clientX: point.x, clientY: point.y, bubbles: true, cancelable: true,
      }));
      window.dispatchEvent(new MouseEvent('mouseup', {
        button: 0, clientX: point.x, clientY: point.y, bubbles: true, cancelable: true,
      }));
    }, leftPoint);
    const moveCommand = await waitForCommandAfter(
      hostMessages,
      moveCursor,
      'move',
      'move command from tilemap left-click',
      m => sameCell(m, expectedMoveCell)
    );
    const moveAffordanceDebug = await waitFor(async () => {
      const debug = await readTileDebug(page);
      const tileState = debug?.state?.tileMap || debug?.rendererState || {};
      const target = tileState.recentTarget || {};
      return target.action === 'move' &&
        Number(target.x) === Number(expectedMoveCell.x) &&
        Number(target.z) === Number(expectedMoveCell.z)
        ? debug
        : null;
    }, 'tilemap sent move marker');

    const expectedContextTarget = MAP_ITEMS[0];
    const rightPoint = await cellToMapPoint(
      page,
      { x: expectedContextTarget[0], z: expectedContextTarget[1] },
      'right click item point'
    );
    const expectedContextCell = await mapPointToCell(page, rightPoint, 'right click item point');
    const contextCursor = hostMessages.length;
    await page.evaluate(point => {
      const canvas = document.getElementById('map-canvas');
      canvas.dispatchEvent(new MouseEvent('contextmenu', {
        button: 2,
        clientX: point.x,
        clientY: point.y,
        bubbles: true,
        cancelable: true,
      }));
    }, rightPoint);
    const contextCommand = await waitForCommandAfter(
      hostMessages,
      contextCursor,
      'context_menu',
      'context_menu command from tilemap right-click',
      m => sameCell(m, expectedContextCell) && Number(m.targetId) === Number(expectedContextTarget[4])
    );
    sendHost(hostWs, {
      type: 'context_menu',
      target: VIEWER_LOGIN,
      ok: true,
      x: expectedContextCell.x,
      z: expectedContextCell.z,
      targetId: expectedContextTarget[4],
      targetLabel: expectedContextTarget[6],
      options: [
        {
          id: 0,
          label: 'Equip bolt-action rifle',
          disabled: false,
          priority: 4,
          priorityName: 'Default',
          orderInPriority: 0,
          tooltip: 'Picks up the rifle and wields it.',
          iconDefName: 'Gun_BoltActionRifle',
          iconLabel: 'bolt-action rifle',
        },
        {
          id: 1,
          label: 'Cannot haul: reserved by another colonist',
          disabled: true,
          priority: 0,
          priorityName: 'DisabledOption',
          orderInPriority: 0,
          disabledReason: 'reserved by another colonist',
          tooltip: 'Reserved by another colonist',
        },
      ],
    });
    const contextMenuHeader = await waitFor(() => page.evaluate(() => {
      const menu = document.getElementById('context-menu');
      const target = menu?.querySelector('.ctx-target')?.textContent?.trim() || '';
      const cell = menu?.querySelector('.ctx-cell')?.textContent?.trim() || '';
      const options = Array.from(menu?.querySelectorAll('.ctx-option') || []).map(el => ({
        text: el.textContent.trim(),
        disabled: el.classList.contains('disabled'),
        reason: el.querySelector('.ctx-option-reason')?.textContent?.trim() || '',
        icon: el.querySelector('.ctx-option-icon')?.textContent?.trim() || '',
        title: el.getAttribute('title') || '',
      }));
      return menu && !menu.classList.contains('hidden') && target === 'bolt-action rifle'
        ? { target, cell, options }
        : null;
    }), 'context menu target header');
    if (!Array.isArray(contextMenuHeader.options) || contextMenuHeader.options.length !== 2)
      throw new Error('expected two context options, got ' + JSON.stringify(contextMenuHeader.options));
    const enabledOpt = contextMenuHeader.options[0];
    const disabledOpt = contextMenuHeader.options[1];
    if (enabledOpt.disabled)
      throw new Error('first context option should be enabled (priority sort): ' + JSON.stringify(enabledOpt));
    if (!disabledOpt.disabled)
      throw new Error('second context option should be disabled: ' + JSON.stringify(disabledOpt));
    if (!disabledOpt.reason || !/reserved/i.test(disabledOpt.reason))
      throw new Error('disabled option missing reason: ' + JSON.stringify(disabledOpt));
    if (!enabledOpt.icon || !/bolt-action rifle/i.test(enabledOpt.icon))
      throw new Error('enabled option missing icon hint: ' + JSON.stringify(enabledOpt));
    if (!enabledOpt.title || !/picks up the rifle/i.test(enabledOpt.title))
      throw new Error('enabled option missing tooltip title: ' + JSON.stringify(enabledOpt));
    const contextAffordanceDebug = await waitFor(async () => {
      const debug = await readTileDebug(page);
      const tileState = debug?.state?.tileMap || debug?.rendererState || {};
      const target = tileState.recentTarget || {};
      return target.action === 'context_menu' &&
        Number(target.x) === Number(expectedContextCell.x) &&
        Number(target.z) === Number(expectedContextCell.z)
        ? debug
        : null;
    }, 'tilemap sent context target marker');

    console.log(JSON.stringify({
      ok: true,
      baseUrl: BASE_URL,
      viewer: VIEWER_LOGIN,
      pawnId: PAWN_ID,
      map: { width: MAP_WIDTH, height: MAP_HEIGHT },
      tileDebug,
      paint,
      assertions: {
        tileMapActivated: true,
        structuredItemsRendered: true,
        foggedBaselineRendered: true,
        mapChunkApplied: true,
        relayCachedMapChunk: true,
        visualIdentityGlyphs: true,
        richerBuildingMetadata: true,
        buildingSemanticFlags: true,
        doorBedWorkbenchDetails: true,
        workbenchBillLabels: true,
        workbenchBillDetails: true,
        workbenchActiveJob: true,
        reservationMetadata: true,
        keyedEntityTable: true,
        worldEntitiesRendered: true,
        constructionProgressRendered: true,
        pawnMovementInterpolation: true,
        incrementalEntityDelta: true,
        explicitEntityRemoval: true,
        hostReconnectRequestedFreshState: true,
        tileMapClearedOnHostReconnect: true,
        tileMapReactivatedAfterHostReconnect: true,
        entityStreamEnvelope: true,
        sequenceGapRequestedResync: true,
        noCameraZoomFromWasdOrDrag: true,
        hoverTargetAffordance: true,
        sentTargetAffordance: true,
        contextTargetIdentity: true,
        contextMenuTargetHeader: true,
        contextMenuOptionMetadata: true,
        moveCell: expectedMoveCell,
        contextCell: expectedContextCell,
      },
      resync: {
        removalDebug,
        interpolationDebug,
        resetDebug,
        tileDebug: resyncTileDebug,
        paint: resyncPaint,
        requests: {
          colonistList: resyncColonistRequest,
          state: resyncStateRequest,
        compatibilityState: null,
        },
        relayCache: {
          clientResyncLog,
        replayedFull: relayReplayedFull,
        replayedChunk: relayReplayedChunk,
        replayedDelta: relayReplayedDelta,
          replayedEntityKeyframe: relayReplayedEntityKeyframe,
          hostStateResyncForwarded: false,
        },
        gapRepairDebug,
      },
      commands: {
        move: moveCommand,
        contextMenu: contextCommand,
      },
      contextMenuHeader,
      affordances: {
        hover: hoverAffordanceDebug?.state?.tileMap || hoverAffordanceDebug?.rendererState,
        move: moveAffordanceDebug?.state?.tileMap || moveAffordanceDebug?.rendererState,
        context: contextAffordanceDebug?.state?.tileMap || contextAffordanceDebug?.rendererState,
      },
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
