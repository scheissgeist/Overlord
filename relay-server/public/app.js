'use strict';

// ─── Config ──────────────────────────────────────────────────────────────────
const WS_URL = (() => {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${location.host}/ws`;
})();
const UI_BUILD = '20260709-buy-polish-v1';

// Twitch OAuth — set TWITCH_CLIENT_ID as a data attribute on <body> or
// injected by the server. Falls back to guest mode if absent.
const TWITCH_CLIENT_ID = document.body.dataset.twitchClientId || '';
const TWITCH_REDIRECT  = location.origin + '/';
const EMBEDDED_MODE    = document.body.dataset.embeddedMode === 'true';
const RELAY_SESSION_KEY = 'overlord_session';
const TWITCH_TOKEN_KEY = 'overlord_twitch_token';
const LOCAL_SESSION_KEY = 'overlord_local_identity';
const AUDIO_MODE_KEY = 'overlord_audio_mode';
const FOLLOW_PAWN_KEY = 'overlord_follow_pawn';
const MAP_BRIGHTNESS_KEY = 'overlord_map_brightness';
const RESOURCE_READOUT_COLLAPSED_KEY = 'overlord_resource_readout_collapsed';
const WORK_LOADOUTS_KEY = 'overlord_work_loadouts_v1';
const FORCE_JPEG_RENDERER = new URLSearchParams(location.search).get('renderer') === 'jpeg';
const LIVE_FRAME_MIN_ZOOM = 0.45;
const LIVE_FRAME_MAX_ZOOM = 5;
const MAP_BRIGHTNESS_MIN = 0.8;
const MAP_BRIGHTNESS_MAX = 2.0;
const MAP_BRIGHTNESS_DEFAULT = 1.2;
const MAP_BRIGHTNESS_STEP = 0.1;
const BINARY_MAGIC = 'OVL1';
const BINARY_HEADER_BYTES = 8;
const binaryTextDecoder = new TextDecoder();

console.info(`[Overlord] viewer UI ${UI_BUILD}`);

// Stamp the build marker into the login footer crumb on first paint.
document.addEventListener('DOMContentLoaded', () => {
  const crumb = document.getElementById('login-build-crumb');
  if (crumb) crumb.textContent = `build ${UI_BUILD}`;
});

// ─── State ───────────────────────────────────────────────────────────────────
let ws = null;
let identity = null; // { sessionToken, login, displayName }
let pawnState = null;
let hostCapabilities = null;
let relayCapabilities = null;
let viewerPermissions = null;
let toolkitState = null;
let colonyResources = null;
let resourceReadoutCollapsed = loadResourceReadoutCollapsed();
let relayOnline = false;
let hostOnline = false;
let lastHostStatusAt = 0;
let reconnectTimer = null;
let reconnectAttempts = 0;
let connectionSeq = 0;
let pendingMove = false;
let targetMode = null;
let lastContextMenuPoint = null;
let liveFrameMeta = null;
let liveFrameDrawRect = null;
let liveFrameImage = null;
let liveFrameObjectUrl = null;
let pendingLiveFrameObjectUrl = null;
let liveFrameLoadSeq = 0;
let liveFrameZoom = 1;
let liveFrameZoomInitialized = false;
let liveFrameZoomTimer = null;
let liveFrameRequestedCenter = null;
let liveFramePanDrag = null;
let followPawnMode = loadFollowPawnMode();
let mapBrightness = loadMapBrightness();
let suppressNextLiveMapClick = false;
let lastSentServerZoom = 1;
let lastSentAspect = 0;
let lastSentPixelHeight = 0;
let lastSentCenterX = null;
let lastSentCenterZ = null;
let aspectResizeTimer = null;
let lastMapClickAt = 0;
let viewerPhase = 'login';
let pendingClaimPawnId = null;
let lobbyRefreshTimer = null;
let lastColonistListAt = 0;
let activeTab = 'log';
let activeCommandMenu = 'quick';
let lastLogLine = 'Waiting for colony updates';
let audioMode = loadAudioMode();
let lastCommandAction = null;
let commandFeedbackTimer = null;
let lastToolkitRequestAt = 0;
let appearanceDraftPawnId = null;
let appearanceDraftHairDef = '';
let appearanceDraftGender = '';
let appearancePreviewData = '';
let appearancePreviewLabel = '';
let currentPortraitData = '';
let activeGearSlot = 'weapon';
let gearSortMode = 'distance';
let activeSocialTargetId = null;
let socialSortMode = 'alpha';
let selectedScheduleHour = 8;
let activeBuyShop = 'all';
let buySearchQuery = '';
let lastBuyFeedback = null;
let commandInteractionUntil = 0;
const buyQuantities = new Map();
const buyQuantityDrafts = new Map();
const buyStuffSelections = new Map();
const buyArgumentDrafts = new Map();
const storyPurchaseSelections = new Map();
const COMMAND_FEEDBACK_CLEAR_MS = 2200;
const MAP_RESYNC_THROTTLE_MS = 1500;
const clientDiagnostics = [];
const CLIENT_DIAGNOSTIC_LIMIT = 300;
let clientFrameStats = {
  startedAt: performance.now(),
  frames: 0,
  bytes: 0,
  maxDecodeMs: 0
};

// ─── DOM ─────────────────────────────────────────────────────────────────────
const $ = id => document.getElementById(id);

const screenLogin = $('screen-login');
const screenLobby = $('screen-lobby');
const screenMain  = $('screen-main');

const loginError    = $('login-error');
const loginSubtitle = $('login-subtitle');
const btnTwitch     = $('btn-twitch');
const localLogin    = $('local-login');
const usernameInput = $('username-input');
const btnLocalConnect = $('btn-local-connect');

const lobbyUser = $('lobby-user');
const lobbyStatus = $('lobby-status');
const lobbyTitle = $('lobby-title');
const lobbyPhase = $('lobby-phase');
const lobbyMsg = $('lobby-msg');
const lobbyClaimNote = $('lobby-claim-note');
const lobbyDeathCard = $('lobby-death-card');
const lobbyDeathName = $('lobby-death-name');
const lobbyDeathCopy = $('lobby-death-copy');
const lobbyTickets = $('lobby-tickets');
const lobbyCapabilities = $('lobby-capabilities');
const colonistList = $('colonist-list');

const pawnPortrait = $('pawn-portrait');
const pawnName = $('pawn-name');
const pawnSubtitle = $('pawn-subtitle');
const pawnHealthEl = $('pawn-health');
const pawnMoodEl = $('pawn-mood');
const mapCanvas = $('map-canvas');
const mapZoomLabel = $('map-zoom-label');
const btnFollowPawn = $('btn-follow-pawn');
const btnZoomOut = $('btn-zoom-out');
const btnZoomReset = $('btn-zoom-reset');
const btnZoomIn = $('btn-zoom-in');
const btnBrightDown = $('btn-bright-down');
const btnBrightReset = $('btn-bright-reset');
const btnBrightUp = $('btn-bright-up');
const mapWaiting = $('map-waiting');
let mapCtx         = null; // lazy init — tile map may own the canvas
let mapWaitingTimer = null;
let mapWaitingShownAt = 0;
const cmdBar = $('cmd-bar');
const statusText = $('status-text');
const pawnJob = $('pawn-job');
const overlayDisc = $('overlay-disconnected');
const logPanel = $('log-panel');
const bottomPanel = $('bottom-panel');
const drawerLabel = $('drawer-label');
const drawerPreview = $('drawer-preview');
const btnDrawerOpen = $('btn-drawer-open');
const btnDrawerClose = $('btn-drawer-close');
const btnSoundToggle = $('btn-sound-toggle');
const panelOpeners = document.querySelectorAll('[data-open-panel]');
const commandMenuOpeners = document.querySelectorAll('[data-command-menu-open]');
const commandWindow = $('command-window');
const btnCommandClose = $('btn-command-close');
const commandWindowTitle = $('command-window-title');
const commandWindowStatus = $('command-window-status');
const commandMenuNav = $('command-menu-nav');
const commandMenuContent = $('command-menu-content');

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, ch => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  }[ch]));
}

function escapeAttr(value) {
  return escapeHtml(value);
}

function cssEscape(value) {
  if (window.CSS?.escape) return CSS.escape(String(value ?? ''));
  return String(value ?? '').replace(/["\\\]]/g, '\\$&');
}

function clampPercent(value, fallback = 100) {
  const num = Number(value);
  if (!Number.isFinite(num)) return fallback;
  return Math.max(0, Math.min(100, Math.round(num)));
}

function conditionClass(value) {
  const pct = clampPercent(value);
  if (pct < 45) return 'low';
  if (pct < 70) return 'warn';
  return 'good';
}

function formatDefLabel(value) {
  return String(value ?? '')
    .replace(/_/g, ' ')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .trim()
    .replace(/\s+/g, ' ');
}

function getArray(value) {
  return Array.isArray(value) ? value : [];
}

function textHasAny(text, words) {
  const haystack = String(text || '').toLowerCase();
  return words.some(word => haystack.includes(word));
}

function uniqueStrings(values) {
  const seen = new Set();
  const out = [];
  getArray(values).forEach(value => {
    const text = typeof value === 'string'
      ? value
      : value?.label || value?.name || value?.defName || value?.def || '';
    const normalized = String(text || '').trim();
    if (!normalized || seen.has(normalized)) return;
    seen.add(normalized);
    out.push(normalized);
  });
  return out;
}

function getScheduleAssignments(state = pawnState) {
  const options = getArray(state?.scheduleAssignments)
    .map(item => ({
      defName: item?.defName || item?.name || item?.def || item,
      label: item?.label || item?.defName || item?.name || item
    }))
    .filter(item => item.defName);

  if (options.length) return options;
  return [
    { defName: 'Anything', label: 'Anything' },
    { defName: 'Work', label: 'Work' },
    { defName: 'Joy', label: 'Joy' },
    { defName: 'Sleep', label: 'Sleep' },
    { defName: 'Meditate', label: 'Meditate' }
  ];
}

function scheduleAssignmentClass(defName) {
  const key = String(defName || '').toLowerCase();
  if (key.includes('sleep')) return 'assignment-sleep';
  if (key.includes('work')) return 'assignment-work';
  if (key.includes('joy') || key.includes('recreation')) return 'assignment-joy';
  if (key.includes('meditat')) return 'assignment-meditate';
  return 'assignment-anything';
}

const HOSTILE_RESPONSE_OPTIONS = [
  { mode: 0, label: 'Flee' },
  { mode: 1, label: 'Fight' },
  { mode: 2, label: 'Ignore' }
];

const COMMAND_MENU_SECTIONS = [
  { id: 'quick', label: 'Quick' },
  { id: 'buy', label: 'Buy' },
  { id: 'story', label: 'Story' },
  { id: 'work', label: 'Work' },
  { id: 'schedule', label: 'Schedule' },
  { id: 'policies', label: 'Policies' },
  { id: 'help', label: 'Help' }
];

const COMMAND_ALIAS_GROUPS = [
  { label: 'Overlord tabs (use the UI — not Twitch chat)', commands: ['Health', 'Skills', 'Gear', 'Social', 'Work', 'Schedule'] },
  { label: 'Overlord Buy / Story (Toolkit store — uses your assigned colonist)', commands: ['Buy tab', 'Story traits/skills', 'healme / trait / etc.'] },
  { label: 'Twitch chat info only (prefer Overlord tabs)', commands: ['!mypawnhealth', '!mypawnskills', '!mypawngear'] },
  { label: 'Twitch chat store aliases (prefer Overlord Buy)', commands: ['!bal', '!buy', '!price', '!purchaselist'] },
  { label: 'Twitch chat repair aliases (prefer Overlord Buy when assigned)', commands: ['!imstuck', '!fixmypawn', '!reviveme', '!rescueme', '!healme'] }
];

// Must stay aligned with TwitchToolkitBridge.PawnTargetedSkus (+ category "pawn").
const PAWN_TARGETED_SKUS = new Set([
  'healme', 'fullheal', 'reviveme', 'rescueme', 'imstuck', 'fixmypawn',
  'trait', 'removetrait', 'settraits', 'levelskill', 'passionshuffle', 'genderswap'
]);

const BUY_SHOP_ORDER = ['medical', 'food', 'weapons', 'apparel', 'buildables', 'events', 'pawn', 'items', 'other'];
const BUY_SHOPS = {
  all: { label: 'All' },
  medical: { label: 'Medical' },
  food: { label: 'Food' },
  weapons: { label: 'Weapons' },
  apparel: { label: 'Apparel' },
  buildables: { label: 'Buildables' },
  events: { label: 'Events' },
  pawn: { label: 'Pawn' },
  items: { label: 'Items' },
  other: { label: 'Other' }
};

const STORY_PURCHASE_TYPES = [
  {
    key: 'heal-me',
    skus: ['healme', 'fullheal'],
    title: 'Heal me',
    detail: 'Toolkit heal on your Overlord-assigned colonist.',
    optionType: ''
  },
  {
    key: 'add-trait',
    skus: ['trait'],
    title: 'Add trait',
    detail: 'Choose a Toolkit trait purchase for your pawn.',
    optionType: 'traitOptions'
  },
  {
    key: 'remove-trait',
    skus: ['removetrait', 'remove_trait'],
    title: 'Remove trait',
    detail: 'Remove one trait your pawn currently has.',
    optionType: 'currentTraits'
  },
  {
    key: 'level-skill',
    skus: ['levelskill'],
    title: 'Improve skill',
    detail: 'Choose the skill argument for Toolkit.',
    optionType: 'skills'
  },
  {
    key: 'passion-shuffle',
    skus: ['passionshuffle', 'passion_shuffle'],
    title: 'Shuffle passions',
    detail: 'Use the Toolkit passion purchase.',
    optionType: ''
  },
  {
    key: 'gender-swap',
    skus: ['genderswap', 'gender_swap'],
    title: 'Gender swap',
    detail: 'Use the Toolkit gender purchase.',
    optionType: ''
  }
];

const RIMWORLD_WORK_ORDER = [
  'Firefighter',
  'Patient',
  'Doctor',
  'PatientBedRest',
  'Childcare',
  'BasicWorker',
  'Warden',
  'Handling',
  'Cooking',
  'Hunting',
  'Construction',
  'Growing',
  'Mining',
  'PlantCutting',
  'Smithing',
  'Tailoring',
  'Art',
  'Crafting',
  'Hauling',
  'Cleaning',
  'DarkStudy',
  'Research'
];
const RIMWORLD_WORK_ORDER_INDEX = new Map(RIMWORLD_WORK_ORDER.map((defName, index) => [defName, index]));

const GEAR_SLOT_DEFS = [
  { key: 'head', label: 'Head' },
  { key: 'outer', label: 'Outer' },
  { key: 'torso', label: 'Torso' },
  { key: 'hands', label: 'Hands' },
  { key: 'legs', label: 'Legs' },
  { key: 'weapon', label: 'Weapon' },
  { key: 'other', label: 'Other' }
];

// ─── Screens ──────────────────────────────────────────────────────────────────
function showScreen(name) {
  [screenLogin, screenLobby, screenMain].forEach(s => s.classList.remove('active'));
  $(`screen-${name}`).classList.add('active');
  if (name === 'login') {
    setViewerPhase('login');
  }
  if (name === 'main') {
    // Canvas may have been sized while the main screen was display:none (0×0).
    requestAnimationFrame(() => {
      refreshMapSurface();
      updateMapWaitingOverlay();
    });
  } else {
    hideMapWaitingOverlay();
  }
}

function hideMapWaitingOverlay() {
  clearTimeout(mapWaitingTimer);
  mapWaitingTimer = null;
  mapWaitingShownAt = 0;
  if (mapWaiting) {
    mapWaiting.classList.add('hidden');
    mapWaiting.textContent = 'Waiting for map…';
  }
}

function hasVisibleMapSurface() {
  if (isTileRendererActive()) return true;
  if (liveFrameImage && liveFrameMeta) return true;
  return false;
}

function updateMapWaitingOverlay(forceMessage) {
  if (!mapWaiting) return;
  if (!screenMain.classList.contains('active') || viewerPhase !== 'assigned') {
    hideMapWaitingOverlay();
    return;
  }
  if (hasVisibleMapSurface()) {
    hideMapWaitingOverlay();
    return;
  }

  const hostDown = !hostOnline && !hostCapabilities;
  const message = forceMessage
    || (hostDown
      ? 'Host is offline — waiting for the streamer to reconnect…'
      : 'Waiting for map frames… If this stays black, try hard-refresh or ask the host to reload Overlord.');

  mapWaiting.textContent = message;
  mapWaiting.classList.remove('hidden');
  if (!mapWaitingShownAt) mapWaitingShownAt = Date.now();

  clearTimeout(mapWaitingTimer);
  mapWaitingTimer = setTimeout(() => {
    if (!hasVisibleMapSurface() && screenMain.classList.contains('active')) {
      requestFreshViewerSnapshot();
      updateMapWaitingOverlay('Still waiting for map… Requested a fresh snapshot.');
    }
  }, 4000);
}

function refreshMapSurface() {
  if (isTileRendererActive()) {
    if (typeof tileMap.centerOnPawn === 'function' && pawnState) {
      const x = Number(pawnState.posX);
      const z = Number(pawnState.posZ);
      if (Number.isFinite(x) && Number.isFinite(z)) tileMap.centerOnPawn(x, z);
      else tileMap.centerOnPawn();
    }
    return;
  }
  if (liveFrameImage) drawLiveFrameImage(liveFrameImage);
}

function setViewerPhase(phase) {
  viewerPhase = phase;
  document.body.dataset.viewerPhase = phase;
}

function setDrawerExpanded(expanded) {
  if (!bottomPanel) return;
  bottomPanel.classList.toggle('expanded', expanded);
  bottomPanel.classList.toggle('collapsed', !expanded);
  btnDrawerOpen?.classList.toggle('hidden', expanded);
  btnDrawerClose?.classList.toggle('hidden', !expanded);
  renderDrawerPreview();
}

function getDrawerLabel(tab) {
  switch (tab) {
    case 'health': return 'Pawn';
    case 'events': return 'Events';
    case 'chat': return 'Chat';
    case 'skills': return 'Skills';
    case 'social': return 'Social';
    case 'gear': return 'Gear';
    case 'commands': return 'Commands';
    default: return 'Activity';
  }
}

function setActiveTab(tab, options = {}) {
  activeTab = tab;

  document.querySelectorAll('.tab').forEach(button => {
    button.classList.toggle('active', button.dataset.tab === tab);
  });

  document.querySelectorAll('.tab-pane').forEach(pane => {
    pane.classList.toggle('active', pane.id === `tab-${tab}`);
  });

  if (options.expand !== false) {
    setDrawerExpanded(true);
  }

  if (drawerLabel) {
    drawerLabel.textContent = getDrawerLabel(tab);
  }
}

function updateDrawerPreview(text, label) {
  if (text) {
    lastLogLine = text;
  }
  if (drawerLabel && label) {
    drawerLabel.textContent = label;
  }
  renderDrawerPreview();
}

function renderDrawerPreview() {
  if (!drawerPreview) return;
  if (pawnState && bottomPanel?.classList.contains('collapsed')) {
    const hp = pawnState.health?.summaryHp ?? 100;
    const mood = pawnState.needs?.Mood ?? pawnState.needs?.mood ?? 50;
    const hpCls = hp < 50 ? 'red' : hp < 75 ? 'yellow' : 'green';
    const moodCls = mood < 30 ? 'red' : mood < 50 ? 'yellow' : 'green';
    const job = pawnState.currentJob || 'Idle';
    drawerPreview.innerHTML =
      `<span class="dp-vitals"><span class="dp-${hpCls}">HP ${hp}%</span> · <span class="dp-${moodCls}">Mood ${mood}%</span></span>` +
      `<span class="dp-job">${escapeHtml(job)}</span>`;
  } else {
    drawerPreview.textContent = lastLogLine;
  }
}

function buildCapabilitySummary(msg) {
  if (!msg) return '';
  const missing = [];
  if (msg.contextMenu === false) missing.push('context actions');
  if (msg.portraits === false) missing.push('portraits');
  if (msg.work === false) missing.push('work');
  if (msg.schedule === false) missing.push('schedule');
  if (msg.outfit === false) missing.push('outfits');
  if (msg.drug === false) missing.push('drug policy');
  if (msg.food === false) missing.push('food policy');
  if (msg.area === false) missing.push('areas');
  if (!missing.length) return '';
  return `Host limits: ${missing.join(', ')}`;
}

function syncCapabilityNotice() {
  if (!lobbyCapabilities) return;
  lobbyCapabilities.textContent = buildCapabilitySummary(hostCapabilities);
}

function permissionKeyForAction(action) {
  switch (action) {
    case 'draft':
    case 'undraft':
    case 'set_hostile_response':
      return 'draft';
    case 'move':
    case 'context_menu':
    case 'context_action':
      return 'move';
    case 'attack':
      return 'attack';
    case 'set_work':
      return 'work';
    case 'set_schedule':
      return 'schedule';
    case 'set_outfit':
      return 'outfit';
    case 'set_drug_policy':
      return 'drugPolicy';
    case 'set_food_policy':
      return 'foodPolicy';
    case 'set_area':
      return 'area';
    case 'set_appearance':
      return 'appearance';
    case 'equip':
    case 'drop':
    case 'drop_inventory':
      return 'equip';
    case 'social_interact':
      return 'move';
    default:
      return null;
  }
}

function capabilityKeyForAction(action) {
  switch (action) {
    case 'context_menu':
    case 'context_action':
      return 'contextMenu';
    case 'set_work':
      return 'work';
    case 'set_schedule':
      return 'schedule';
    case 'set_outfit':
      return 'outfit';
    case 'set_drug_policy':
      return 'drug';
    case 'set_food_policy':
      return 'food';
    case 'set_area':
      return 'area';
    case 'trigger_event':
      return 'events';
    default:
      return null;
  }
}

function getActionBlockedReason(action) {
  const capKey = capabilityKeyForAction(action);
  if (capKey && hostCapabilities && hostCapabilities[capKey] === false) {
    return 'Not supported by this host';
  }

  // Only block if permissions explicitly say false — null/missing = allowed
  const permKey = permissionKeyForAction(action);
  if (action === 'set_appearance' && viewerPermissions?.freeAppearanceAvailable !== false) {
    return '';
  }
  if (permKey && viewerPermissions && viewerPermissions[permKey] === false) {
    return 'The streamer disabled this command';
  }

  if (action === 'trigger_event' && (!hostCapabilities || hostCapabilities.events !== true)) {
    return 'Viewer-triggered events are disabled';
  }

  return '';
}

function isActionAllowed(action) {
  return getActionBlockedReason(action) === '';
}

function summarizeClientMessage(msg, bytes = 0) {
  if (!msg) return { bytes };
  const out = {
    type: msg.type,
    action: msg.action,
    target: msg.target,
    ok: typeof msg.ok === 'boolean' ? msg.ok : undefined,
    bytes
  };
  if (typeof msg.data === 'string') out.dataBytes = msg.data.length;
  else if (typeof msg.dataBytes === 'number') out.dataBytes = msg.dataBytes;
  if (msg.binary) out.binary = true;
  return Object.fromEntries(Object.entries(out).filter(([, v]) => v !== undefined && v !== ''));
}

function parseBinaryEnvelope(data) {
  const buffer = data instanceof ArrayBuffer
    ? data
    : ArrayBuffer.isView(data)
      ? data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength)
      : null;
  if (!buffer) throw new Error('unsupported binary payload');

  const view = new DataView(buffer);
  if (view.byteLength < BINARY_HEADER_BYTES) throw new Error('binary payload too small');
  for (let i = 0; i < BINARY_MAGIC.length; i++) {
    if (view.getUint8(i) !== BINARY_MAGIC.charCodeAt(i)) throw new Error('bad binary magic');
  }

  const metadataLength = view.getUint32(4, true);
  const metadataEnd = BINARY_HEADER_BYTES + metadataLength;
  if (metadataLength < 1 || metadataEnd > view.byteLength) throw new Error('invalid binary metadata');

  const metadataBytes = new Uint8Array(buffer, BINARY_HEADER_BYTES, metadataLength);
  const msg = JSON.parse(binaryTextDecoder.decode(metadataBytes));
  const imageBytes = new Uint8Array(buffer, metadataEnd);
  msg.binary = true;
  msg.dataBytes = typeof msg.dataBytes === 'number' ? msg.dataBytes : imageBytes.byteLength;
  msg.binaryImageUrl = URL.createObjectURL(new Blob([imageBytes], { type: 'image/jpeg' }));
  return { msg, bytes: view.byteLength };
}

function releaseLiveFrameImage() {
  if (liveFrameImage && typeof liveFrameImage.close === 'function') {
    try { liveFrameImage.close(); } catch (_) {}
  }
  liveFrameImage = null;
}

function releaseLiveFrameObjectUrl(url = liveFrameObjectUrl) {
  if (!url) return;
  try { URL.revokeObjectURL(url); } catch (_) {}
  if (url === liveFrameObjectUrl) liveFrameObjectUrl = null;
  if (url === pendingLiveFrameObjectUrl) pendingLiveFrameObjectUrl = null;
}

function logClient(event, fields = {}) {
  const entry = {
    ts: new Date().toISOString(),
    event,
    ...fields
  };
  clientDiagnostics.push(entry);
  while (clientDiagnostics.length > CLIENT_DIAGNOSTIC_LIMIT) clientDiagnostics.shift();
  if (event !== 'frame_rx') {
    console.debug('[Overlord]', entry);
  }
}

window.OverlordDebug = {
  version: UI_BUILD,
  logs: clientDiagnostics,
  getState: () => ({
    viewerPhase,
    audioMode,
    followPawnMode,
    mapBrightness,
    hasPawn: !!pawnState,
    relayOnline,
    relayCapabilities,
    hostOnline,
    lastHostStatusAt,
    hasTileData,
    tileMap: getTileMapDebugState(),
    mapStream: {
      mapEpoch: mapStreamEpoch,
      seq: mapStreamSeq,
      chunkEpoch: chunkStreamEpoch,
      chunkSeq: chunkStreamSeq,
      lastResyncAt: mapStreamLastResyncAt
    },
    entityStream: {
      entityEpoch: entityStreamEpoch,
      seq: entityStreamSeq,
      lastResyncAt: entityStreamLastResyncAt
    },
    forceJpegRenderer: FORCE_JPEG_RENDERER,
    liveFrameMeta,
    liveFrameZoom,
    wsState: ws ? ws.readyState : null
  }),
  mapPointToCell: (clientX, clientY) => {
    const tileCell = tileMapPointToCell(clientX, clientY);
    return tileCell || liveFramePointToCell(clientX, clientY);
  }
};

function applyCommandAvailability() {
  const noPawn = !pawnState;
  document.querySelectorAll('.cmd-btn[data-action]').forEach(btn => {
    const action = btn.dataset.action;
    const reason = noPawn ? 'Waiting for pawn state…' : getActionBlockedReason(action);
    btn.disabled = !!reason;
    btn.title = reason;
  });

  renderEventButtons();
  renderCommandCenter();
}

function getCommandButton(action) {
  if (action === 'draft' || action === 'undraft') {
    return $('btn-draft-toggle');
  }
  return document.querySelector(`.cmd-btn[data-action="${action}"]`);
}

function clearCommandFeedback(action = null) {
  const buttons = action ? [getCommandButton(action)] : Array.from(document.querySelectorAll('.cmd-btn'));
  buttons.forEach(btn => {
    if (!btn) return;
    btn.classList.remove('cmd-armed', 'cmd-sent', 'cmd-accepted', 'cmd-failed');
  });
}

function setCommandFeedback(action, state, message) {
  if (!action) return;
  clearTimeout(commandFeedbackTimer);
  clearCommandFeedback(action);

  const btn = getCommandButton(action);
  if (btn) {
    btn.classList.add(`cmd-${state}`);
    if (message) btn.title = message;
  }
  if (message) {
    statusText.textContent = message;
    updateDrawerPreview(message, 'Activity');
  }

  if (state === 'accepted' || state === 'failed') {
    commandFeedbackTimer = setTimeout(() => {
      clearCommandFeedback(action);
      clearSubtitleFeedback();
      applyCommandAvailability();
    }, COMMAND_FEEDBACK_CLEAR_MS);
  }
}

function markCommandArmed(action, message) {
  setCommandFeedback(action, 'armed', message);
}

function markCommandSent(action, message) {
  lastCommandAction = action;
  setCommandFeedback(action, 'sent', message);
  if (pawnSubtitle && message) {
    const prev = pawnSubtitle.dataset.prevText ?? pawnSubtitle.textContent;
    pawnSubtitle.dataset.prevText = prev;
    pawnSubtitle.textContent = message;
  }
}

function clearSubtitleFeedback() {
  if (!pawnSubtitle || !pawnSubtitle.dataset.prevText) return;
  pawnSubtitle.textContent = pawnSubtitle.dataset.prevText;
  delete pawnSubtitle.dataset.prevText;
}

function markCommandResult(action, ok, message) {
  const resolvedAction = action || lastCommandAction;
  if (!resolvedAction) return;
  setCommandFeedback(resolvedAction, ok === false ? 'failed' : 'accepted', message);
  if (resolvedAction === lastCommandAction) lastCommandAction = null;
}

function loadFollowPawnMode() {
  try {
    const saved = localStorage.getItem(FOLLOW_PAWN_KEY);
    if (saved === '0' || saved === 'false') return false;
    if (saved === '1' || saved === 'true') return true;
  } catch (_) {}
  return true;
}

function loadResourceReadoutCollapsed() {
  try {
    const saved = localStorage.getItem(RESOURCE_READOUT_COLLAPSED_KEY);
    return saved === '1' || saved === 'true';
  } catch (_) {}
  return false;
}

function saveResourceReadoutCollapsed(collapsed) {
  resourceReadoutCollapsed = !!collapsed;
  try {
    localStorage.setItem(RESOURCE_READOUT_COLLAPSED_KEY, resourceReadoutCollapsed ? '1' : '0');
  } catch (_) {}
}

const RESOURCE_CATEGORY_LABELS = {
  food: 'Food',
  medicine: 'Medicine',
  materials: 'Materials',
  weapons: 'Weapons',
  apparel: 'Apparel',
  other: 'Other'
};

function handleResourceReadout(msg) {
  const rows = Array.isArray(msg?.resources) ? msg.resources : [];
  colonyResources = {
    hash: msg?.hash,
    resources: rows.filter(r => r && (Number(r.count) || 0) > 0)
  };
  renderResourceReadout();
}

function clearResourceReadout() {
  colonyResources = null;
  renderResourceReadout();
}

function renderResourceReadout() {
  const root = $('resource-readout');
  const body = $('resource-readout-body');
  const toggle = $('resource-readout-toggle');
  if (!root || !body) return;

  const enabled = hostCapabilities?.resourceReadout === true;
  const assigned = viewerPhase === 'assigned' && !!pawnState;
  const rows = colonyResources?.resources || [];
  const show = enabled && assigned && rows.length > 0;

  root.classList.toggle('hidden', !show);
  root.classList.toggle('collapsed', resourceReadoutCollapsed);
  if (toggle) {
    toggle.textContent = resourceReadoutCollapsed ? 'Stock >' : 'Stock';
    toggle.title = resourceReadoutCollapsed ? 'Expand stock list' : 'Collapse stock list';
  }
  if (!show) {
    body.innerHTML = '';
    return;
  }

  const byCat = new Map();
  for (const row of rows) {
    const cat = String(row.category || 'other');
    if (!byCat.has(cat)) byCat.set(cat, []);
    byCat.get(cat).push(row);
  }

  const order = ['food', 'medicine', 'materials', 'weapons', 'apparel', 'other'];
  const parts = [];
  for (const cat of order) {
    const list = byCat.get(cat);
    if (!list || !list.length) continue;
    parts.push(`<div class="resource-cat">${escapeHtml(RESOURCE_CATEGORY_LABELS[cat] || cat)}</div>`);
    for (const row of list) {
      const label = String(row.label || row.def || '');
      const count = Number(row.count) || 0;
      parts.push(
        `<div class="resource-row"><span class="resource-row-label">${escapeHtml(label)}</span>` +
        `<span class="resource-row-count">${escapeHtml(String(count))}</span></div>`
      );
    }
  }
  body.innerHTML = parts.join('');
}

function saveFollowPawnMode(enabled) {
  followPawnMode = !!enabled;
  try { localStorage.setItem(FOLLOW_PAWN_KEY, followPawnMode ? '1' : '0'); } catch (_) {}
  updateFollowPawnToggle();
  applyFollowPawnMode({ force: true });
}

function updateFollowPawnToggle() {
  if (!btnFollowPawn) return;
  btnFollowPawn.classList.toggle('active', followPawnMode);
  btnFollowPawn.setAttribute('aria-pressed', followPawnMode ? 'true' : 'false');
  btnFollowPawn.title = followPawnMode
    ? 'Auto-follow on — camera stays on your pawn. Pan to unlock.'
    : 'Auto-follow off — camera stays where you left it. Click to re-follow.';
  btnFollowPawn.textContent = followPawnMode ? 'Follow' : 'Free';
}

function applyFollowPawnMode(options = {}) {
  const force = options.force === true;
  if (tileMap) {
    if (typeof tileMap.setFollowPawn === 'function') {
      tileMap.setFollowPawn(followPawnMode);
    } else {
      tileMap.userMovedCamera = !followPawnMode;
      if (followPawnMode) tileMap.centerOnPawn?.();
    }
  }

  if (!followPawnMode) return;
  if (isTileRendererActive()) return;
  if (!liveFrameMeta || liveFrameMeta.cameraMode === 'host') return;
  liveFrameRequestedCenter = null;
  scheduleServerCameraZoom(force, { followPawn: true });
}

function loadMapBrightness() {
  try {
    const saved = Number(localStorage.getItem(MAP_BRIGHTNESS_KEY));
    if (Number.isFinite(saved)) {
      return Math.max(MAP_BRIGHTNESS_MIN, Math.min(MAP_BRIGHTNESS_MAX, saved));
    }
  } catch (_) {}
  return MAP_BRIGHTNESS_DEFAULT;
}

function saveMapBrightness(value) {
  const clamped = Math.max(
    MAP_BRIGHTNESS_MIN,
    Math.min(MAP_BRIGHTNESS_MAX, Math.round(Number(value) * 10) / 10)
  );
  mapBrightness = Number.isFinite(clamped) ? clamped : MAP_BRIGHTNESS_DEFAULT;
  try { localStorage.setItem(MAP_BRIGHTNESS_KEY, String(mapBrightness)); } catch (_) {}
  applyMapBrightness();
}

function applyMapBrightness() {
  const brightness = Number.isFinite(mapBrightness) ? mapBrightness : MAP_BRIGHTNESS_DEFAULT;
  const contrast = brightness <= 1
    ? 1
    : Math.min(1.18, 1 + (brightness - 1) * 0.25);
  document.documentElement.style.setProperty('--map-brightness', String(brightness));
  document.documentElement.style.setProperty('--map-contrast', String(Math.round(contrast * 100) / 100));
  if (btnBrightReset) {
    btnBrightReset.textContent = `${brightness.toFixed(1)}×`;
    btnBrightReset.title = brightness === MAP_BRIGHTNESS_DEFAULT
      ? 'Reset brightness'
      : `Brightness ${brightness.toFixed(1)}× — click to reset`;
  }
  if (btnBrightDown) btnBrightDown.disabled = brightness <= MAP_BRIGHTNESS_MIN;
  if (btnBrightUp) btnBrightUp.disabled = brightness >= MAP_BRIGHTNESS_MAX;
}

function loadAudioMode() {
  try {
    const saved = localStorage.getItem(AUDIO_MODE_KEY);
    if (saved === 'off' || saved === 'quiet' || saved === 'all') return saved;
  } catch (_) {}
  return 'quiet';
}

function saveAudioMode(mode) {
  audioMode = mode;
  try { localStorage.setItem(AUDIO_MODE_KEY, mode); } catch (_) {}
  updateSoundToggle();
}

function updateSoundToggle() {
  if (!btnSoundToggle) return;
  const label = audioMode === 'off' ? 'Sound off' : audioMode === 'all' ? 'Sound all' : 'Sound quiet';
  btnSoundToggle.textContent = label;
  btnSoundToggle.dataset.mode = audioMode;
  btnSoundToggle.title = audioMode === 'quiet'
    ? 'Only assignment, damage, and death sounds play'
    : audioMode === 'all'
      ? 'All notification sounds play'
      : 'All notification sounds are muted';
}

function showLobbyState(options = {}) {
  const phase = options.phase || 'lobby';
  setViewerPhase(phase);
  showScreen('lobby');

  const name = identity?.displayName || identity?.login || 'viewer';
  if (lobbyUser) lobbyUser.textContent = name;
  if (lobbyStatus && options.statusHtml != null) lobbyStatus.innerHTML = options.statusHtml;
  // Legacy lobbyTitle h2 is hidden; the new title is a single tmux-style line.
  const titleRow = document.getElementById('lobby-title');
  const titleTextEl = titleRow?.querySelector('.lobby-title-text');
  if (titleTextEl) {
    const t = String(options.title || '').toLowerCase();
    titleTextEl.textContent = t || 'waiting for assignment';
  }
  if (lobbyPhase) {
    lobbyPhase.dataset.phase = phase;
    lobbyPhase.title = phase === 'dead' ? 'Downed Out' : phase === 'assigned' ? 'Assigned' : 'Waiting';
  }
  if (lobbyMsg) lobbyMsg.textContent = options.message || '';

  const claimNote = options.claimNote || '';
  if (lobbyClaimNote) {
    lobbyClaimNote.textContent = claimNote;
    lobbyClaimNote.classList.toggle('hidden', !claimNote);
  }

  const deathName = options.deathName || '';
  const deathCopy = options.deathCopy || '';
  if (lobbyDeathCard) {
    lobbyDeathCard.classList.toggle('hidden', !(deathName || deathCopy));
  }
  if (lobbyDeathName) lobbyDeathName.textContent = deathName;
  if (lobbyDeathCopy) lobbyDeathCopy.textContent = deathCopy;

  syncCapabilityNotice();
}

function normalizeViewer(value) {
  return String(value || '').trim().toLowerCase();
}

function sameViewer(a, b) {
  const left = normalizeViewer(a);
  const right = normalizeViewer(b);
  return !!left && !!right && left === right;
}

function isCurrentViewer(value) {
  return sameViewer(value, identity?.login) || sameViewer(value, identity?.displayName);
}

function isSuggestedColonist(c) {
  return isCurrentViewer(c?.name);
}

function renderColonistWaiting(message = 'Waiting for colonists…') {
  if (!colonistList) return;
  colonistList.innerHTML = '';
  const row = document.createElement('div');
  row.className = 'lobby-empty';
  // The CSS pseudo-element provides the animated leader; we just append the message text.
  const text = document.createElement('span');
  text.textContent = message;
  row.appendChild(text);
  colonistList.appendChild(row);
}

function startLobbyRefresh() {
  if (lobbyRefreshTimer || !identity) return;
  const tick = () => {
    if (!identity || viewerPhase === 'assigned' || screenMain.classList.contains('active')) {
      stopLobbyRefresh();
      return;
    }
    send({ type: 'request_colonist_list' });
    send({ type: 'request_state' });
    lobbyRefreshTimer = setTimeout(tick, 2500);
  };
  tick();
}

function stopLobbyRefresh() {
  clearTimeout(lobbyRefreshTimer);
  lobbyRefreshTimer = null;
}

function resetPortrait() {
  currentPortraitData = '';
  appearancePreviewData = '';
  appearancePreviewLabel = '';
  pawnPortrait.removeAttribute('src');
  pawnPortrait.style.display = 'none';
  const placeholder = $('pawn-portrait-placeholder');
  if (placeholder) placeholder.style.display = '';
  const cmdPortrait = $('command-portrait-img');
  if (cmdPortrait) cmdPortrait.removeAttribute('src');
}

function resetMapSurface() {
  destroyTileMap();
  hasTileData = false;
  resetMapStream();
  liveFrameMeta = null;
  liveFrameDrawRect = null;
  releaseLiveFrameImage();
  liveFrameLoadSeq++;
  releaseLiveFrameObjectUrl();
  liveFrameZoom = 1;
  liveFrameZoomInitialized = false;
  liveFrameRequestedCenter = null;
  liveFramePanDrag = null;
  suppressNextLiveMapClick = false;
  lastSentServerZoom = 1;
  lastSentAspect = 0;
  lastSentPixelHeight = 0;
  lastSentCenterX = null;
  lastSentCenterZ = null;
  clearTimeout(liveFrameZoomTimer);
  liveFrameZoomTimer = null;
  updateLiveZoomLabel();
  mapCanvas.classList.remove('live-map', 'panning');
  hideMapWaitingOverlay();
}

function resetAssignedState() {
  pawnState = null;
  pendingMove = false;
  targetMode = null;
  pendingClaimPawnId = null;
  exitMoveMode();

  resetMapSurface();

  if (mapCtx) {
    mapCtx.clearRect(0, 0, mapCanvas.width, mapCanvas.height);
  }
  syncCommandDeck(null);

  pawnName.textContent = '';
  if (pawnSubtitle) pawnSubtitle.textContent = '';
  pawnHealthEl.textContent = '';
  pawnMoodEl.textContent = '';
  pawnJob.textContent = '';
  resetPortrait();
  clearCommandFeedback();
  lastCommandAction = null;
  applyCommandAvailability();
  clearResourceReadout();
  startLobbyRefresh();
}

// ─── Session persistence (localStorage survives tab close; sessionStorage did not) ─
function readStoredJson(key) {
  for (const store of [localStorage, sessionStorage]) {
    try {
      const raw = store.getItem(key);
      if (!raw) continue;
      return JSON.parse(raw);
    } catch (_) {
      try { store.removeItem(key); } catch (_) {}
    }
  }
  return null;
}

function saveRelaySession(session) {
  const payload = JSON.stringify(session);
  try { localStorage.setItem(RELAY_SESSION_KEY, payload); } catch (_) {}
  try { sessionStorage.setItem(RELAY_SESSION_KEY, payload); } catch (_) {}
}

function clearRelaySession() {
  try { localStorage.removeItem(RELAY_SESSION_KEY); } catch (_) {}
  try { sessionStorage.removeItem(RELAY_SESSION_KEY); } catch (_) {}
}

function saveTwitchAccessToken(token) {
  if (!token) return;
  try {
    localStorage.setItem(TWITCH_TOKEN_KEY, JSON.stringify({
      token,
      savedAt: Date.now(),
    }));
  } catch (_) {}
}

function readTwitchAccessToken() {
  try {
    const raw = localStorage.getItem(TWITCH_TOKEN_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    const token = typeof parsed === 'string' ? parsed : parsed?.token;
    const savedAt = typeof parsed === 'object' && parsed ? Number(parsed.savedAt || 0) : 0;
    // Twitch implicit tokens are typically ~4h; drop stale ones.
    if (!token || (savedAt && Date.now() - savedAt > 3.5 * 3600 * 1000)) {
      localStorage.removeItem(TWITCH_TOKEN_KEY);
      return null;
    }
    return token;
  } catch (_) {
    try { localStorage.removeItem(TWITCH_TOKEN_KEY); } catch (_) {}
    return null;
  }
}

function clearTwitchAccessToken() {
  try { localStorage.removeItem(TWITCH_TOKEN_KEY); } catch (_) {}
}

// ─── Login ────────────────────────────────────────────────────────────────────
(function initLoginUi() {
  if (!EMBEDDED_MODE) return;
  if (loginSubtitle) loginSubtitle.textContent = 'Connect directly to this RimWorld host';
  if (btnTwitch) btnTwitch.classList.add('hidden');
  if (localLogin) localLogin.classList.remove('hidden');
})();

(function initLogin() {
  if (EMBEDDED_MODE) {
    const saved = sessionStorage.getItem(LOCAL_SESSION_KEY);
    if (saved) {
      try {
        identity = JSON.parse(saved);
        connect();
        return;
      } catch {
        sessionStorage.removeItem(LOCAL_SESSION_KEY);
      }
    }
    showScreen('login');
    return;
  }

  // Check if returning from Twitch OAuth redirect (hash contains access_token)
  const hash = new URLSearchParams(location.hash.slice(1));
  if (hash.get('access_token')) {
    const token = hash.get('access_token');
    history.replaceState(null, '', location.pathname); // clean URL
    exchangeTwitchToken(token);
    return;
  }

  // Check saved session (localStorage preferred — survives tab close / refresh)
  const saved = readStoredJson(RELAY_SESSION_KEY);
  if (saved?.sessionToken) {
    identity = saved;
    saveRelaySession(identity);
    connect();
    return;
  }
})();

if (btnTwitch) {
  btnTwitch.addEventListener('click', () => {
    if (EMBEDDED_MODE) return;
    if (!TWITCH_CLIENT_ID) {
      showLoginError('Twitch auth is not configured on this server');
      return;
    }
    const scope = 'user:read:email';
    const url = `https://id.twitch.tv/oauth2/authorize?response_type=token&client_id=${TWITCH_CLIENT_ID}&redirect_uri=${encodeURIComponent(TWITCH_REDIRECT)}&scope=${scope}`;
    location.href = url;
  });
}

if (btnLocalConnect) {
  btnLocalConnect.addEventListener('click', connectLocal);
}

if (usernameInput) {
  usernameInput.addEventListener('keydown', e => {
    if (e.key === 'Enter') connectLocal();
  });
}

$('btn-reconnect').addEventListener('click', () => {
  overlayDisc.classList.add('hidden');
  connect();
});

btnDrawerOpen?.addEventListener('click', () => {
  setDrawerExpanded(true);
  setActiveTab(activeTab || 'log');
});

btnDrawerClose?.addEventListener('click', () => {
  setDrawerExpanded(false);
});

btnSoundToggle?.addEventListener('click', () => {
  const next = audioMode === 'quiet' ? 'off' : audioMode === 'off' ? 'all' : 'quiet';
  saveAudioMode(next);
});
updateSoundToggle();

panelOpeners.forEach(button => {
  button.addEventListener('click', () => {
    const tab = button.dataset.openPanel || 'log';
    setActiveTab(tab, { expand: true });
  });
});

setViewerPhase('login');
setDrawerExpanded(false);
setActiveTab('log', { expand: false });

function clearLoginError() {
  loginError.textContent = '';
  loginError.classList.add('hidden');
}

function showLoginError(msg) {
  loginError.textContent = msg;
  loginError.classList.remove('hidden');
}

function connectLocal() {
  if (!EMBEDDED_MODE) return;
  const username = usernameInput ? usernameInput.value.trim() : '';
  if (!username) {
    showLoginError('Enter a viewer name');
    return;
  }

  clearLoginError();
  identity = { login: username, displayName: username, local: true };
  sessionStorage.setItem(LOCAL_SESSION_KEY, JSON.stringify(identity));
  connect();
}

function showWaitingLobby(message) {
  showLobbyState({
    phase: viewerPhase === 'dead' ? 'dead' : 'lobby',
    title: viewerPhase === 'dead' ? 'Your colonist is gone' : 'Pick a colonist',
    message: message || (viewerPhase === 'dead' ? 'Hold on. Another colonist may open up soon.' : 'Tap Claim on someone below, or wait — the streamer can assign you.'),
    claimNote: viewerPhase === 'dead' ? '' : 'Waiting is fine. Claiming just asks for a specific colonist.',
    statusHtml: '<span class="on">Connected</span>'
  });
  renderColonistWaiting();
  startLobbyRefresh();
}

async function exchangeTwitchToken(accessToken, { silent = false } = {}) {
  if (!silent) {
    showLobbyState({
      phase: 'lobby',
      title: 'Signing you in…',
      message: 'Verifying your Twitch identity.',
      statusHtml: '<span class="on">Connecting</span>'
    });
    lobbyMsg.textContent = 'Verifying Twitch identity…';
  }
  try {
    const res  = await fetch('/auth/twitch', {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ token: accessToken }),
    });
    if (!res.ok) {
      clearTwitchAccessToken();
      if (!silent) {
        showScreen('login');
        showLoginError('Twitch auth failed');
      }
      return false;
    }
    const data = await res.json();
    identity = { sessionToken: data.sessionToken, login: data.login, displayName: data.displayName };
    saveRelaySession(identity);
    saveTwitchAccessToken(accessToken);
    connect();
    return true;
  } catch (e) {
    if (!silent) {
      showScreen('login');
      showLoginError('Auth error: ' + e.message);
    }
    return false;
  }
}

async function recoverExpiredSession() {
  const twitchToken = readTwitchAccessToken();
  if (!twitchToken) return false;
  showLobbyState({
    phase: 'lobby',
    title: 'Reconnecting…',
    message: 'Session expired on the relay — signing you back in.',
    statusHtml: '<span class="on">Reconnecting</span>'
  });
  return exchangeTwitchToken(twitchToken, { silent: true });
}

// ─── WebSocket ────────────────────────────────────────────────────────────────
function connect() {
  if (!identity) { showScreen('login'); return; }
  clearTimeout(reconnectTimer);
  const seq = ++connectionSeq;
  if (ws) {
    try {
      ws.onclose = null;
      ws.onerror = null;
      ws.close(1000, 'Reconnecting');
    } catch (_) {}
  }

  const params = new URLSearchParams({
    role: 'viewer',
    session: identity.sessionToken || '',
    build: UI_BUILD
  });
  const url = EMBEDDED_MODE ? WS_URL : `${WS_URL}?${params.toString()}`;
  const socket = new WebSocket(url);
  socket.binaryType = 'arraybuffer';
  ws = socket;
  relayCapabilities = null;

  socket.onopen = () => {
    if (seq !== connectionSeq) {
      try { socket.close(); } catch (_) {}
      return;
    }
    relayOnline = true;
    reconnectAttempts = 0;
    overlayDisc.classList.add('hidden');
    renderCommandCenter();
    if (EMBEDDED_MODE) {
      statusText.textContent = 'Authenticating...';
      send({
        type: 'auth',
        username: identity.login,
        displayName: identity.displayName || identity.login
      });
      return;
    }

    statusText.textContent = 'Connected';
    if (pawnState || hasTileData || liveFrameMeta) {
      resetMapSurface();
    }
    scheduleServerCameraZoom(true);
    requestFreshViewerSnapshot();
    if (pawnState) {
      setViewerPhase('assigned');
    } else {
      showWaitingLobby('You can request an open colonist, or stay connected and the streamer can assign you.');
    }
  };

  socket.onmessage = e => {
    if (seq !== connectionSeq) return;
    if (typeof e.data !== 'string') {
      const handleBinary = (data) => {
        if (seq !== connectionSeq) return;
        try {
          const { msg, bytes } = parseBinaryEnvelope(data);
          if (msg.type === 'map_frame') logClient('frame_rx_binary', summarizeClientMessage(msg, bytes));
          else logClient('recv_binary', summarizeClientMessage(msg, bytes));
          if (msg.type !== 'map_frame' && msg.binaryImageUrl) releaseLiveFrameObjectUrl(msg.binaryImageUrl);
          handleMessage(msg);
        } catch (err) {
          logClient('binary_parse_error', { error: err.message });
        }
      };

      if (e.data instanceof Blob) {
        e.data.arrayBuffer().then(handleBinary).catch(err => logClient('binary_parse_error', { error: err.message }));
      } else {
        handleBinary(e.data);
      }
      return;
    }

    try {
      const msg = JSON.parse(e.data);
      if (msg.type === 'batch') {
        // Relay-coalesced envelope — dispatch each inner message in order
        if (Array.isArray(msg.msgs)) {
          for (const inner of msg.msgs) {
            try { handleMessage(inner); } catch (_) {}
          }
        }
        return;
      }
      if (msg.type === 'map_frame') logClient('frame_rx', summarizeClientMessage(msg, e.data.length));
      else logClient('recv', summarizeClientMessage(msg, e.data.length));
      handleMessage(msg);
    } catch (_) {}
  };

  socket.onclose = (e) => {
    if (seq !== connectionSeq) return;
    relayOnline = false;
    renderCommandCenter();
    // Session missing/expired on relay (TTL, deploy wipe, or invalid token)
    if (e.code === 4003) {
      clearRelaySession();
      identity = null;
      recoverExpiredSession().then((ok) => {
        if (ok) return;
        showScreen('login');
        showLoginError('Session expired — please reconnect with Twitch');
      });
      return;
    }
    reconnectAttempts++;
    // Silent fast reconnect for first 5 attempts (covers deploys)
    if (reconnectAttempts <= 5) {
      statusText.textContent = 'Reconnecting…';
      reconnectTimer = setTimeout(connect, 1500);
    } else {
      statusText.textContent = 'Disconnected';
      overlayDisc.classList.remove('hidden');
      reconnectTimer = setTimeout(connect, Math.min(10000, 3000 + reconnectAttempts * 1000));
    }
  };

  socket.onerror = () => {
    if (seq === connectionSeq) socket.close();
  };
}

function send(obj) {
  const text = JSON.stringify(obj);
  logClient('send', summarizeClientMessage(obj, text.length));
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(text);
  else logClient('send_dropped', summarizeClientMessage(obj, text.length));
}

function requestMapResync(reason, msg = {}) {
  const now = Date.now();
  if (now - mapStreamLastResyncAt < MAP_RESYNC_THROTTLE_MS) return;
  mapStreamLastResyncAt = now;

  logClient('map_resync_request', {
    reason,
    mapEpoch: mapStreamEpoch,
    lastSeq: mapStreamSeq || 0,
    msgEpoch: msg?.mapEpoch,
    msgSeq: msg?.seq,
    msgBaseSeq: msg?.baseSeq
  });
  appendLog('Map sync repaired');

  send({
    type: 'state_resync_request',
    protocol: 'vdr/0',
    reason,
    mapEpoch: mapStreamEpoch,
    lastSeq: mapStreamSeq || 0,
    wanted: ['map_full', 'map_chunk', 'map_chunks', 'map_delta', 'entity_keyframe', 'entity_delta', 'pawn_state']
  });

  if (!relaySupportsCacheResync()) {
    send({ type: 'request_state' });
  } else {
    logClient('map_resync_compat_skipped', { reason, relayCache: true });
  }
}

function relaySupportsCacheResync() {
  return relayCapabilities?.replayCache === true && relayCapabilities?.cacheResync === true;
}

function requestFreshViewerSnapshot() {
  send({ type: 'request_colonist_list' });
  send({ type: 'request_state' });
  requestToolkitState();
}

function requestToolkitState(force = false) {
  const now = Date.now();
  if (!force && now - lastToolkitRequestAt < 2500) return;
  lastToolkitRequestAt = now;
  send({ type: 'command', action: 'toolkit_refresh' });
}

function beginHostResync(message = 'Catching up with the colony…') {
  resetAssignedState();
  showLobbyState({
    phase: 'lobby',
    title: 'Loading colony…',
    message,
    statusHtml: '<span class="on">Connected</span>'
  });
  requestFreshViewerSnapshot();
}

// ─── Message handling ─────────────────────────────────────────────────────────
function handleMessage(msg) {
  switch (msg.type) {
    case 'auth_ok':
      if (EMBEDDED_MODE) {
        statusText.textContent = 'Connected';
        relayOnline = true;
        renderCommandCenter();
        scheduleServerCameraZoom(true);
        requestFreshViewerSnapshot();
        if (pawnState) {
          setViewerPhase('assigned');
        } else {
          showWaitingLobby('You are connected. Request a colonist if you want one, or wait for the streamer to assign you.');
        }
      }
      break;
    case 'colonist_list':    handleColonistList(msg);    break;
    case 'pawn_state':       handlePawnState(msg);       break;
    case 'pawn_portrait':    handlePawnPortrait(msg);    break;
    case 'permissions':      handlePermissions(msg);      break;
    case 'map_frame':        handleMapFrame(msg);        break;
    case 'command_result':
    case 'action_result':    handleCommandResult(msg);   break;
    case 'pawn_died':        handlePawnDied(msg);        break;
    case 'action_log':       handleActionLog(msg);       break;
    case 'portal_available': handlePortalAvailable(msg); break;
    case 'ticket_update':    handleTicketUpdate(msg);    break;
    case 'game_info':        handleGameInfo(msg);        break;
    case 'resource_readout': handleResourceReadout(msg); break;
    case 'host_capabilities': handleHostCapabilities(msg); break;
    case 'relay_capabilities': handleRelayCapabilities(msg); break;
    case 'toolkit_state':    handleToolkitState(msg);    break;
    case 'host_connected':
      markHostOnline('host_connected');
      statusText.textContent = 'Connected';
      beginHostResync('The colony is live. Finding your colonist…');
      break;
    case 'context_menu':     handleContextMenu(msg);     break;
    case 'map_full':         handleMapFull(msg);         break;
    case 'map_delta':        handleMapDelta(msg);        break;
    case 'map_chunk':        handleMapChunk(msg);        break;
    case 'entity_keyframe':  handleEntityState(msg);     break;
    case 'entity_delta':     handleEntityState(msg);     break;
    case 'chat':             handleChatMessage(msg);     break;
    case 'vote_update':      handleVoteUpdate(msg);      break;
    case 'game_event':       handleGameEvent(msg);       break;
    case 'client_reload':    handleClientReload(msg);    break;
    case 'admin_message':
      if (msg.message) appendLog(`[Admin] ${msg.message}`);
      break;
    case 'viewer_kick':
    case 'banned':
      handleModeration(msg);
      break;
    case 'timeout':
      handleTimeout(msg);
      break;
    case 'host_disconnected':
      markHostOffline();
      statusText.textContent = 'Host offline';
      if (viewerPhase === 'assigned') {
        resetAssignedState();
      }
      showLobbyState({
        phase: 'lobby',
        title: 'Waiting for the streamer',
        message: 'The host needs to load a RimWorld save with Overlord active. Hang tight.',
        statusHtml: '<span class="off">Host offline</span>'
      });
      break;
  }
}

// ─── Colonist list ────────────────────────────────────────────────────────────
function buildColonistHint(c) {
  const parts = [];
  if (c.health != null && Number(c.health) < 70) {
    parts.push(`${Math.round(c.health)}% health`);
  }
  if (c.topSkill && c.topSkillLevel != null) {
    parts.push(`${c.topSkill} ${c.topSkillLevel}`);
  } else if (c.title) {
    parts.push(c.title);
  }
  return parts.join(' · ');
}

function handleClientReload(msg) {
  const delayMs = Math.max(0, Math.min(Number(msg.delayMs ?? 800), 10000));
  const message = msg.message || 'Viewer update available. Reloading...';
  statusText.textContent = message;
  appendLog(message);
  updateDrawerPreview(message, 'Activity');
  setTimeout(() => location.reload(), delayMs);
}

function handleColonistList(msg) {
  const colonists = msg.colonists || [];
  lastColonistListAt = Date.now();

  if (msg.hostMap === false) {
    showLobbyState({
      phase: viewerPhase === 'dead' ? 'dead' : 'lobby',
      title: 'Colony is loading…',
      message: 'The map is on its way. You\'ll be assigned a colonist as soon as it arrives.',
      statusHtml: '<span class="on">Connected</span>',
      claimNote: viewerPhase === 'dead' ? '' : 'Stay here — colonist list will appear once the map loads.'
    });
    renderColonistWaiting('The colony map is loading…');
    startLobbyRefresh();
    return;
  }

  const mine = colonists.find(c => isCurrentViewer(c.assignedTo) || isCurrentViewer(c.assignedDisplayName));
  if (mine) {
    pendingClaimPawnId = null;
    stopLobbyRefresh();
    setViewerPhase('assigned');
    showScreen('main');
    if (!pawnState) {
      updateDrawerPreview(`Assigned to ${mine.name}. Syncing pawn state...`, 'Activity');
      send({ type: 'request_state' });
    }
    return;
  }

  if (viewerPhase === 'assigned') {
    resetAssignedState();
  }

  colonistList.innerHTML = '';

  const requestedColonist = colonists.find(c => c.id === pendingClaimPawnId);
  const waitingForClaim = requestedColonist && !requestedColonist.assignedTo;
  const claimNote = waitingForClaim
    ? `Waiting on approval for ${requestedColonist.name}. Stay here — the streamer can approve this or assign someone else.`
    : 'You can wait without claiming. Claim only if you want a specific colonist.';
  if (!waitingForClaim) {
    pendingClaimPawnId = null;
  }

  const openCount = colonists.filter(c => !c.assignedTo).length;
  showLobbyState({
    phase: viewerPhase === 'dead' ? 'dead' : 'lobby',
    title: viewerPhase === 'dead' ? 'Your colonist is gone' : 'Pick a colonist',
    message: colonists.length
      ? (openCount
        ? `${openCount} open · claim one, or wait to be assigned`
        : 'Everyone below is taken. Wait here — the streamer can still assign you.')
      : 'No open colonists right now. Hang tight — the streamer can still assign you one.',
    statusHtml: '<span class="on">Connected</span>',
    claimNote
  });

  if (!colonists.length) {
    renderColonistWaiting('No colonists available yet. The streamer may assign you directly.');
    startLobbyRefresh();
    return;
  }

  const sortedColonists = [...colonists].sort((a, b) => {
    const suggested = Number(isSuggestedColonist(b)) - Number(isSuggestedColonist(a));
    if (suggested) return suggested;
    const available = Number(!b.assignedTo) - Number(!a.assignedTo);
    if (available) return available;
    return String(a.name || '').localeCompare(String(b.name || ''));
  });

  startLobbyRefresh();

  const panel = document.createElement('div');
  panel.className = 'colonist-panel';
  const heading = document.createElement('div');
  heading.className = 'colonist-panel-head';
  heading.textContent = waitingForClaim ? 'Your request' : 'Open colonists';
  panel.appendChild(heading);

  sortedColonists.forEach((c, idx) => {
    const taken = !!c.assignedTo && !isCurrentViewer(c.assignedTo) && !isCurrentViewer(c.assignedDisplayName);
    const mine = isCurrentViewer(c.assignedTo) || isCurrentViewer(c.assignedDisplayName);
    const requested = pendingClaimPawnId === c.id;
    const row = document.createElement('div');
    row.className = [
      'colonist-row',
      isSuggestedColonist(c) ? 'suggested' : '',
      taken ? 'taken' : '',
      requested ? 'pending' : '',
      mine ? 'mine' : ''
    ].filter(Boolean).join(' ');
    row.dataset.pawnId = c.id;

    const main = document.createElement('div');
    main.className = 'colonist-main';

    const titleRow = document.createElement('div');
    titleRow.className = 'colonist-title-row';
    const nameEl = document.createElement('div');
    nameEl.className = 'colonist-name';
    nameEl.textContent = c.name;
    titleRow.appendChild(nameEl);
    if (isSuggestedColonist(c) && !c.assignedTo) {
      const badge = document.createElement('span');
      badge.className = 'colonist-badge';
      badge.textContent = 'name match';
      titleRow.appendChild(badge);
    }
    main.appendChild(titleRow);

    const st = document.createElement('div');
    st.className = 'colonist-meta';
    if (mine) {
      st.classList.add('available');
      st.textContent = 'assigned to you';
    } else if (c.assignedTo) {
      st.classList.add('taken');
      st.textContent = `taken by ${c.assignedDisplayName || c.assignedTo}`;
    } else if (requested) {
      st.classList.add('requested');
      st.textContent = 'request sent — waiting on streamer';
    } else {
      st.classList.add('available');
      st.textContent = 'open';
    }
    main.appendChild(st);

    const detailBits = [buildColonistHint(c), c.currentJob || c.job].filter(Boolean);
    if (detailBits.length) {
      const detail = document.createElement('div');
      detail.className = 'colonist-detail';
      detail.textContent = detailBits.join(' · ');
      main.appendChild(detail);
    }
    row.appendChild(main);

    if (!c.assignedTo) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'claim-btn';
      btn.textContent = requested ? 'Requested' : 'Claim';
      btn.disabled = requested;
      btn.setAttribute('aria-label', requested ? `Claim requested for ${c.name}` : `Claim ${c.name}`);
      btn.addEventListener('click', () => {
        pendingClaimPawnId = c.id;
        updateDrawerPreview(`Claim requested for ${c.name}`, 'Activity');
        send({ type: 'command', action: 'claim_colonist', pawnId: parseInt(c.id, 10) });
        handleColonistList({ colonists });
      });
      row.appendChild(btn);
    } else if (mine) {
      const mark = document.createElement('span');
      mark.className = 'colonist-yours';
      mark.textContent = 'Yours';
      row.appendChild(mark);
    }

    panel.appendChild(row);
  });

  colonistList.appendChild(panel);
}

// ─── Pawn state ───────────────────────────────────────────────────────────────
function handlePawnState(msg) {
  let s = msg.state;
  if (!s) return;
  // State may arrive as a JSON string (RawJson wrapper) — parse it
  if (typeof s === 'string') { try { s = JSON.parse(s); } catch { return; } }
  const previousState = pawnState;
  const wasNull = previousState === null;
  markHostOnline('pawn_state');
  pawnState = s;
  if (appearanceDraftPawnId !== s.id) {
    appearanceDraftPawnId = s.id;
    appearanceDraftHairDef = s.appearance?.hairDef || '';
    appearanceDraftGender = s.appearance?.gender || '';
    appearancePreviewData = '';
    appearancePreviewLabel = '';
  }
  pendingClaimPawnId = null;
  stopLobbyRefresh();
  setViewerPhase('assigned');
  showScreen('main');
  updateMapWaitingOverlay();
  renderResourceReadout();
  if (wasNull) {
    playSound('assign');
    setActiveTab('health', { expand: true });
    applyFollowPawnMode({ force: true });
  }

  pawnName.textContent = s.name || '';
  const jobLabel = s.currentJob || 'Idle';
  const goodAt = summarizeGoodAt(s.skills, 2);
  if (pawnSubtitle) {
    delete pawnSubtitle.dataset.prevText;
    pawnSubtitle.textContent = goodAt.length
      ? `${jobLabel} · Good at ${goodAt.map(x => x.label).join(', ')}`
      : jobLabel;
  }
  pawnJob.textContent = s.currentJob || '';

  // RimWorld-style state pill: drafted/downed/dead/sleeping/...
  const statePill = $('pawn-state-pill');
  if (statePill) {
    let pillText = '';
    let pillClass = '';
    if (s.dead) { pillText = 'Dead'; pillClass = 'danger'; }
    else if (s.downed) { pillText = 'Downed'; pillClass = 'danger'; }
    else if (s.drafted) { pillText = 'Drafted'; pillClass = ''; }
    if (pillText) {
      statePill.textContent = pillText;
      statePill.className = `state-pill ${pillClass}`.trim();
    } else {
      statePill.className = 'state-pill hidden';
      statePill.textContent = '';
    }
  }

  const hp = s.health ? (s.health.summaryHp ?? 100) : 100;
  const prevHp = previousState?._lastHp ?? 100;
  if (hp < prevHp - 5) playSound('damage');
  s._lastHp = hp;
  const mood = s.needs ? (s.needs.Mood ?? s.needs.mood ?? 50) : 50;
  const hpColor = hp < 50 ? 'var(--red)' : hp < 75 ? 'var(--yellow)' : 'var(--green)';
  const moodColor = mood < 30 ? 'var(--red)' : mood < 50 ? 'var(--yellow)' : 'var(--green)';
  pawnHealthEl.innerHTML = `<span style="color:${hpColor}">${hp}%</span> <span style="color:var(--text-muted);font-weight:400">HP</span>`;
  pawnMoodEl.innerHTML = `<span style="color:${moodColor}">${mood}%</span> <span style="color:var(--text-muted);font-weight:400">Mood</span>`;

  if (s.portrait) {
    const dataUrl = `data:image/png;base64,${s.portrait}`;
    pawnPortrait.src = dataUrl;
    pawnPortrait.style.display = '';
    const ph = $('pawn-portrait-placeholder');
    if (ph) ph.style.display = 'none';
    const cmdPortrait = $('command-portrait-img');
    if (cmdPortrait) cmdPortrait.src = dataUrl;
  }

  syncCommandDeck(s);
  renderNeeds(s.needs);
  renderBioSummary(s);
  renderSkills(s.skills);
  renderCapacities(s.capacities);
  renderHealth(s.health);
  renderTraits(s.traits);
  renderThoughts(s.thoughts);
  renderSocial(s);
  if (!isSelectMenuOpen($('gear-list'))) renderGear(s);
  if (!isSelectMenuOpen($('inventory-list'))) renderInventory(s.inventory);
  applyCommandAvailability();
  updateDrawerPreview(s.currentJob || 'Ready for commands', 'Activity');
  renderDrawerPreview();
  if (liveFrameImage && liveFrameMeta?.cameraMode === 'host') {
    drawLiveFrameImage(liveFrameImage);
  }
}

function syncCommandDeck(state) {
  const drafted = !!state?.drafted;
  const draftToggle = $('btn-draft-toggle');
  const moveBtn = document.querySelector('.cmd-btn[data-action="move"]');

  if (draftToggle) {
    draftToggle.dataset.action = drafted ? 'undraft' : 'draft';
    draftToggle.textContent = drafted ? 'Undraft' : 'Draft';
    draftToggle.classList.toggle('drafted', drafted);
  }
  if (moveBtn) moveBtn.textContent = drafted ? 'Move' : 'Walk';
}

function handlePawnPortrait(msg) {
  if (!msg.data) return;
  currentPortraitData = msg.data;
  const dataUrl = `data:image/png;base64,${msg.data}`;
  pawnPortrait.src = dataUrl;
  pawnPortrait.style.display = '';
  const ph = $('pawn-portrait-placeholder');
  if (ph) ph.style.display = 'none';
  const cmdPortrait = $('command-portrait-img');
  if (cmdPortrait) cmdPortrait.src = dataUrl;
  if (!appearancePreviewData) renderCommandCenterFromState();
}

function renderNeeds(needs) {
  if (!needs) return;
  const defs = [
    { id: 'need-food', key: 'Food',  label: 'Food' },
    { id: 'need-rest', key: 'Rest',  label: 'Rest' },
    { id: 'need-joy',  key: 'Joy',   label: 'Joy'  },
    { id: 'need-mood', key: 'Mood',  label: 'Mood' },
  ];
  defs.forEach(({ id, key, label }) => {
    const el = $(id);
    if (!el) return;
    let pct = needs[key] ?? needs[key.toLowerCase()] ?? 0;
    if (pct > 0 && pct <= 1) pct = Math.round(pct * 100);
    pct = Math.round(pct);
    const cls = pct < 25 ? 'crit' : pct < 50 ? 'warn' : '';
    el.innerHTML = `<span class="need-label">${label}</span>
      <div class="need-bar-bg">
        <div class="need-bar-fill ${cls}" style="width:${pct}%"></div>
        <span class="need-val">${pct}%</span>
      </div>`;
    el.title = `${label}: ${pct}%`;
  });
}

function summarizeGoodAt(skills, limit = 4) {
  const list = Array.isArray(skills) ? skills : [];
  return list
    .filter(skill => skill && !skill.disabled)
    .map(skill => ({
      label: skill.label || skill.def || skill.name || '?',
      level: Math.max(0, Number(skill.level ?? 0)),
      passion: Math.max(0, Number(skill.passion || 0))
    }))
    .filter(skill => skill.level >= 8 || skill.passion > 0)
    .sort((a, b) => {
      if (b.passion !== a.passion) return b.passion - a.passion;
      return b.level - a.level;
    })
    .slice(0, limit);
}

function renderBioSummary(state) {
  const el = $('bio-summary');
  if (!el) return;
  if (!state) {
    el.innerHTML = '';
    return;
  }

  const story = state.story || {};
  const goodAt = summarizeGoodAt(state.skills, 4);
  const lines = [];

  if (story.title) {
    lines.push(`<div class="bio-line"><span class="bio-label">Title</span>${escapeHtml(story.title)}</div>`);
  }
  if (story.childhood || story.adulthood) {
    const childhood = story.childhood ? escapeHtml(story.childhood) : '—';
    const adulthood = story.adulthood ? escapeHtml(story.adulthood) : '—';
    lines.push(`<div class="bio-line"><span class="bio-label">Story</span>${childhood} → ${adulthood}</div>`);
  }

  if (goodAt.length) {
    const chips = goodAt.map(skill => {
      const passionMark = skill.passion >= 2 ? ' ★★' : skill.passion === 1 ? ' ★' : '';
      const cls = skill.passion > 0 ? 'bio-skill-chip passion' : 'bio-skill-chip';
      return `<span class="${cls}">${escapeHtml(skill.label)} <strong>${skill.level}${passionMark}</strong></span>`;
    }).join('');
    lines.push(`<div class="bio-line"><span class="bio-label">Good at</span></div><div class="bio-good-at">${chips}</div>`);
  } else {
    lines.push(`<div class="bio-line"><span class="bio-label">Good at</span>No standout skills yet — open Skills for the full board.</div>`);
  }

  el.innerHTML = lines.join('');
}

function renderSkills(skills) {
  const el = $('skills-list');
  const summary = $('skills-summary');
  if (!el) return;
  if (!Array.isArray(skills)) {
    el.innerHTML = '';
    if (summary) summary.innerHTML = '';
    return;
  }

  const goodAt = summarizeGoodAt(skills, 5);
  if (summary) {
    summary.innerHTML = goodAt.length
      ? `Best / passions: ${goodAt.map(s => `${escapeHtml(s.label)} ${s.level}${s.passion >= 2 ? '★★' : s.passion === 1 ? '★' : ''}`).join(' · ')}`
      : 'No standout skills yet.';
  }
  el.innerHTML = `<div class="skill-board">${skills.map(renderSkillCard).join('')}</div>`;
}

function skillTierClass(level) {
  if (level >= 15) return 'tier-master';
  if (level >= 11) return 'tier-good';
  if (level >= 5) return 'tier-mid';
  if (level >= 1) return 'tier-low';
  return 'tier-zero';
}

function renderSkillCard(skill) {
  const level = Math.max(0, Math.min(20, Number(skill.level ?? 0)));
  const disabled = skill.disabled ? ' disabled' : '';
  const passion = Number(skill.passion || 0);
  const passionGlyph = passion === 2
    ? '<span class="passion-flame major" aria-label="major passion">🔥🔥</span>'
    : passion === 1
      ? '<span class="passion-flame minor" aria-label="minor passion">🔥</span>'
      : '';
  const width = Math.max(2, Math.round((level / 20) * 100));
  const tier = skillTierClass(level);
  return `<div class="skill-card${disabled}">
    <div class="skill-head">
      <span class="skill-name">${escapeHtml(skill.label || skill.def || skill.name || '?')}</span>
      <span class="skill-val ${tier}">${level}${passionGlyph}</span>
    </div>
    <div class="skill-meter"><span class="${tier}" style="width:${width}%"></span></div>
  </div>`;
}

function renderTraits(traits) {
  const el = $('traits-list');
  if (!el) return;
  if (!Array.isArray(traits) || traits.length === 0) {
    el.innerHTML = '';
    return;
  }
  el.innerHTML = traits
    .map(t => `<span class="trait-chip">${escapeHtml(t.label || t.def || String(t))}</span>`)
    .join('');
}

function renderThoughts(thoughts) {
  const el = $('thoughts-list');
  if (!el) return;
  if (!Array.isArray(thoughts) || thoughts.length === 0) {
    el.innerHTML = '';
    return;
  }
  // Group by label so "Deep talk" x6 collapses into one row with stacks count.
  const groups = new Map();
  thoughts.forEach(t => {
    const label = t.label || t.def || '?';
    const mood = Number(t.mood ?? t.moodOffset ?? 0);
    const existing = groups.get(label);
    if (existing) {
      existing.count += 1;
      existing.totalMood += mood;
    } else {
      groups.set(label, { label, count: 1, totalMood: mood, unitMood: mood });
    }
  });
  const rows = Array.from(groups.values())
    .sort((a, b) => Math.abs(b.totalMood) - Math.abs(a.totalMood))
    .slice(0, 14);
  const items = rows.map(g => {
    const cls = g.totalMood > 0 ? 'positive' : g.totalMood < 0 ? 'negative' : '';
    const sign = g.totalMood > 0 ? '+' : '';
    const count = g.count > 1 ? ` <span class="thought-count">x${g.count}</span>` : '';
    const value = g.totalMood === 0 ? '' : `${sign}${g.totalMood}`;
    return `<div class="thought-row"><span class="thought-label">${escapeHtml(g.label)}${count}</span><span class="thought-mood ${cls}">${value}</span></div>`;
  }).join('');
  el.innerHTML = `<div class="health-section-title">Mind</div>${items}`;
}

function renderGear(s) {
  const el = $('gear-list');
  if (!el) return;
  const items = buildGearItems(s);
  const nearby = buildNearbyGearItems(s);

  const slotItems = new Map();
  items.forEach(item => {
    if (!slotItems.has(item.slotKey)) slotItems.set(item.slotKey, item);
  });
  const slotHasContent = GEAR_SLOT_DEFS.some(def => slotItems.has(def.key) || nearby.some(item => item.slotKey === def.key));
  if (!slotHasContent) activeGearSlot = 'weapon';
  if (!GEAR_SLOT_DEFS.some(def => def.key === activeGearSlot)) activeGearSlot = 'weapon';
  const activeDef = GEAR_SLOT_DEFS.find(def => def.key === activeGearSlot) || GEAR_SLOT_DEFS[0];
  const activeEquipped = items.filter(item => item.slotKey === activeGearSlot);
  const activeNearbyRaw = sortGearItems(nearby.filter(item => item.slotKey === activeGearSlot));
  const activeNearby = dedupeNearbyGear(activeNearbyRaw);
  const totalReachable = activeNearbyRaw.length;

  const sortLabels = {
    distance: 'Nearest',
    quality: 'Highest quality',
    value: 'Highest value',
    condition: 'Best condition',
    alpha: 'Name A–Z',
  };

  el.innerHTML = `<div class="gear-layout">
    <div class="gear-worn-all">
      <div class="gear-nearby-title">Wearing now (${items.length})</div>
      ${items.length
        ? items.map(item => `<div class="gear-worn-row">${escapeHtml(item.label || item.defName || '')}${Number.isFinite(item.hp) ? ` <span class="gear-sheet-count">${item.hp}%</span>` : ''}</div>`).join('')
        : '<div class="quiet-empty slim">Nothing equipped</div>'}
    </div>
    <div class="gear-rig" aria-label="Equipment slots">
      ${GEAR_SLOT_DEFS.map(def => renderGearSlot(def, slotItems.get(def.key))).join('')}
    </div>
    <div class="gear-sheet">
      <div class="gear-sheet-head">
        <span class="gear-sheet-title">${escapeHtml(activeDef.label)}</span>
        <span class="gear-sheet-count">${totalReachable} nearby${activeNearby.length !== totalReachable ? ` · ${activeNearby.length} kinds` : ''}</span>
      </div>
      ${activeEquipped.length
        ? activeEquipped.map(renderGearRow).join('')
        : '<div class="quiet-empty slim">Nothing equipped in this slot</div>'}
      <div class="gear-nearby-title">
        <span>Available nearby</span>
        <label class="gear-sort-dropdown">
          <span class="visually-hidden">Sort by</span>
          <select data-gear-sort-select>
            ${Object.entries(sortLabels).map(([key, lbl]) =>
              `<option value="${escapeAttr(key)}"${gearSortMode === key ? ' selected' : ''}>${escapeHtml(lbl)}</option>`
            ).join('')}
          </select>
        </label>
      </div>
      ${activeNearby.length
        ? activeNearby.map(renderNearbyGearRow).join('')
        : '<div class="quiet-empty slim">No reachable items for this slot</div>'}
    </div>
  </div>`;
  bindGearButtons(el);
}

function buildGearItems(s) {
  const equipBlocked = getActionBlockedReason('drop');
  const items = [];
  if (s.weapon) {
    const wLabel = typeof s.weapon === 'string' ? s.weapon : (s.weapon.label || s.weapon.defName || '?');
    const hp = typeof s.weapon === 'object' ? clampPercent(s.weapon.hp) : null;
    items.push({
      type: 'weapon',
      slotKey: 'weapon',
      slotLabel: 'Weapon',
      label: wLabel,
      meta: hp == null ? 'equipped' : `${hp}% condition`,
      hp,
      action: 'drop',
      slot: 'weapon',
      button: 'Drop',
      blocked: equipBlocked
    });
  }
  if (s.apparel && Array.isArray(s.apparel)) {
    s.apparel.forEach((a, index) => {
      const label = typeof a === 'string' ? a : (a.label || a.def || '?');
      const slot = typeof a === 'string' ? label : (a.defName || a.def || label);
      const hp = typeof a === 'object' ? clampPercent(a.hp) : null;
      const slotKey = a.slotKey || inferGearSlot(label, index);
      items.push({
        type: 'worn',
        slotKey,
        slotLabel: gearSlotLabel(slotKey),
        label,
        meta: hp == null ? 'apparel' : `${hp}% condition`,
        hp,
        action: 'drop',
        slot,
        button: 'Take off',
        blocked: equipBlocked
      });
    });
  }
  return items;
}

function buildNearbyGearItems(s) {
  const equipBlocked = getActionBlockedReason('equip');
  return getArray(s?.nearbyEquipment).map(item => {
    const label = item?.label || item?.defName || 'item';
    const hp = clampPercent(item?.hp ?? 100);
      const slotKey = item?.slotKey || inferGearSlot(`${item?.defName || ''} ${label}`);
      const distance = Number(item?.distance);
      const marketValue = Number(item?.marketValue ?? item?.price ?? item?.value);
      const qualityRank = Number(item?.qualityRank ?? -1);
      const quality = item?.quality || '';
      const metaParts = [
        Number.isFinite(distance) ? `${distance} cells` : 'nearby',
        quality ? String(quality) : '',
        Number.isFinite(marketValue) && marketValue > 0 ? `${Math.round(marketValue)} silver` : ''
      ].filter(Boolean);
      return {
      id: item?.id,
      type: item?.type || (slotKey === 'weapon' ? 'weapon' : 'apparel'),
      slotKey,
      slotLabel: gearSlotLabel(slotKey),
      label,
      meta: metaParts.join(' - '),
      distance,
      marketValue: Number.isFinite(marketValue) ? marketValue : 0,
      qualityRank: Number.isFinite(qualityRank) ? qualityRank : -1,
      hp,
      blocked: equipBlocked
    };
  }).filter(item => item.id != null && GEAR_SLOT_DEFS.some(def => def.key === item.slotKey));
}

function sortGearItems(items) {
  return [...items].sort((a, b) => {
    if (gearSortMode === 'alpha') return String(a.label).localeCompare(String(b.label));
    if (gearSortMode === 'condition') {
      const ah = Number.isFinite(Number(a.hp)) ? Number(a.hp) : 0;
      const bh = Number.isFinite(Number(b.hp)) ? Number(b.hp) : 0;
      if (ah !== bh) return bh - ah;
    }
    if (gearSortMode === 'quality') {
      const aq = Number.isFinite(Number(a.qualityRank)) ? Number(a.qualityRank) : -1;
      const bq = Number.isFinite(Number(b.qualityRank)) ? Number(b.qualityRank) : -1;
      if (aq !== bq) return bq - aq;
    }
    if (gearSortMode === 'value') {
      const av = Number.isFinite(Number(a.marketValue)) ? Number(a.marketValue) : 0;
      const bv = Number.isFinite(Number(b.marketValue)) ? Number(b.marketValue) : 0;
      if (av !== bv) return bv - av;
    }
    const da = Number.isFinite(Number(a.distance)) ? Number(a.distance) : 999;
    const db = Number.isFinite(Number(b.distance)) ? Number(b.distance) : 999;
    if (da !== db) return da - db;
    return String(a.label).localeCompare(String(b.label));
  });
}

function inferGearSlot(label, index = 0) {
  const lower = String(label || '').toLowerCase();
  if (/(hat|helmet|cap|hood|tuque|crown|head|cowboy)/.test(lower)) return 'head';
  if (/(parka|coat|duster|jacket|robe|armor|armour|vest|flak|cape|shield belt)/.test(lower)) return 'outer';
  if (/(shirt|t-shirt|tee|button-down|torso|corset|skin layer)/.test(lower)) return 'torso';
  if (/(glove|hand|gauntlet)/.test(lower)) return 'hands';
  if (/(pant|trouser|skirt|leg|shorts)/.test(lower)) return 'legs';
  return index === 0 ? 'other' : `other-${index}`;
}

function gearSlotLabel(slotKey) {
  if (slotKey?.startsWith('other-')) return 'Other';
  return GEAR_SLOT_DEFS.find(def => def.key === slotKey)?.label || 'Other';
}

function renderGearSlot(def, item) {
  const isActive = activeGearSlot === def.key;
  if (!item) {
    return `<button class="gear-slot empty${isActive ? ' active' : ''}" data-gear-slot-select="${escapeAttr(def.key)}" title="${escapeHtml(def.label)} — empty">
      <span class="gear-slot-label">${escapeHtml(def.label)}</span>
    </button>`;
  }
  const hp = item.hp == null ? 100 : clampPercent(item.hp);
  const blocked = item.blocked || '';
  return `<button class="gear-slot filled ${conditionClass(hp)}${isActive ? ' active' : ''}" data-gear-slot-select="${escapeAttr(def.key)}" title="${escapeAttr(blocked || `${def.label}: ${item.label}`)}">
    <span class="gear-slot-label">${escapeHtml(def.label)}</span>
    <span class="gear-slot-name">${escapeHtml(item.label)}</span>
  </button>`;
}

function renderGearRow(item) {
  const hp = item.hp == null ? 100 : clampPercent(item.hp);
  const blocked = item.blocked || '';
  return `<div class="gear-row equipped">
    <div class="gear-row-main">
      <span class="gear-name">${escapeHtml(item.label)}</span>
      <span class="gear-meta-text">${escapeHtml(item.meta)}</span>
    </div>
    ${item.hp == null ? '' : `<span class="condition ${conditionClass(hp)}"><span style="width:${hp}%"></span></span>`}
    <button class="item-action ghost" data-gear-action="${escapeAttr(item.action)}" data-slot="${escapeAttr(item.slot)}" ${blocked ? 'disabled' : ''} title="${escapeAttr(blocked)}">${escapeHtml(item.button)}</button>
  </div>`;
}

function renderNearbyGearRow(item) {
  const hp = item.hp == null ? 100 : clampPercent(item.hp);
  const blocked = item.blocked || '';
  // Collapsed group rows show stack count; single rows show distance/quality.
  const stackBadge = item.stackCount > 1
    ? `<span class="gear-stack-badge">x${item.stackCount}</span>`
    : '';
  return `<div class="gear-row">
    <div class="gear-row-main">
      <span class="gear-name">${escapeHtml(item.label)}${stackBadge}</span>
      <span class="gear-meta-text">${escapeHtml(item.meta)}</span>
    </div>
    <span class="condition ${conditionClass(hp)}"><span style="width:${hp}%"></span></span>
    <button class="item-action" data-equip-thing-id="${escapeAttr(item.id)}" ${blocked ? 'disabled' : ''} title="${escapeAttr(blocked)}">Equip</button>
  </div>`;
}

function dedupeNearbyGear(items) {
  // Group items by display label (so "Wood x75" stacks count), keep nearest as the
  // representative and sum stack counts. This is a pure visual collapse — clicking
  // Equip still targets a single thingId.
  const groups = new Map();
  items.forEach(item => {
    const key = `${item.label}::${item.slotKey}`;
    const existing = groups.get(key);
    if (!existing) {
      groups.set(key, { ...item, stackCount: 1 });
      return;
    }
    existing.stackCount += 1;
    // Keep the nearest representative for the Equip button.
    const ad = Number.isFinite(Number(existing.distance)) ? Number(existing.distance) : 999;
    const bd = Number.isFinite(Number(item.distance)) ? Number(item.distance) : 999;
    if (bd < ad) {
      const stack = existing.stackCount;
      groups.set(key, { ...item, stackCount: stack });
    }
  });
  return Array.from(groups.values());
}

function bindGearButtons(root) {
  root.querySelectorAll('[data-gear-sort-select]').forEach(sel => {
    sel.addEventListener('focus', () => markCommandInteraction(4000));
    sel.addEventListener('mousedown', () => markCommandInteraction(4000));
    sel.addEventListener('change', () => {
      gearSortMode = sel.value || 'distance';
      renderGear(pawnState);
    });
  });
  root.querySelectorAll('[data-gear-sort]').forEach(btn => {
    btn.addEventListener('click', () => {
      gearSortMode = btn.dataset.gearSort || 'distance';
      renderGear(pawnState);
    });
  });
  root.querySelectorAll('[data-gear-slot-select]').forEach(btn => {
    btn.addEventListener('click', () => {
      activeGearSlot = btn.dataset.gearSlotSelect || 'weapon';
      renderGear(pawnState);
    });
  });
  root.querySelectorAll('[data-gear-action]').forEach(btn => {
    btn.addEventListener('click', () => {
      const action = btn.dataset.gearAction;
      const slot = btn.dataset.slot || 'weapon';
      sendGearAction(action, slot);
    });
  });
  root.querySelectorAll('[data-equip-thing-id]').forEach(btn => {
    btn.addEventListener('click', () => {
      sendEquipAction(btn.dataset.equipThingId);
    });
  });
}

function sendGearAction(action, slot) {
  const blocked = getActionBlockedReason(action);
  if (blocked) {
    appendLog(blocked);
    return;
  }
  markCommandSent(action, slot === 'weapon' ? 'Drop weapon sent' : 'Take off sent');
  send({ type: 'command', action, slot });
}

function sendEquipAction(thingId) {
  const blocked = getActionBlockedReason('equip');
  if (blocked) {
    appendLog(blocked);
    return;
  }
  const id = Number(thingId);
  if (!Number.isFinite(id)) return;
  markCommandSent('equip', 'Equip sent');
  send({ type: 'command', action: 'equip', thingId: id });
}

// ─── Tile Map ─────────────────────────────────────────────────────────────────
let tileMap = null;
let hasTileData = false;
let mapStreamEpoch = null;
let mapStreamSeq = 0;
let mapStreamLastResyncAt = 0;
let chunkStreamEpoch = null;
let chunkStreamSeq = 0;
let entityStreamEpoch = null;
let entityStreamSeq = 0;
let entityStreamLastResyncAt = 0;

function isTileRendererActive() {
  return !!(hasTileData && tileMap && tileMap.active);
}

function tileMapPointToCell(clientX, clientY) {
  if (!isTileRendererActive()) return null;
  if (typeof tileMap.screenToCell === 'function') return tileMap.screenToCell(clientX, clientY);
  if (typeof tileMap._screenToTile === 'function') return tileMap._screenToTile(clientX, clientY);
  return null;
}

function getTileMapDebugState() {
  if (!tileMap) return null;
  if (typeof tileMap.getDebugState === 'function') return tileMap.getDebugState();
  return {
    active: !!tileMap.active,
    hasFullMap: !!tileMap.terrainCanvas,
    width: tileMap.width || 0,
    height: tileMap.height || 0,
    camX: tileMap.camX,
    camY: tileMap.camY,
    zoom: tileMap.zoom,
    targetZoom: tileMap.targetZoom,
    pawnCount: Array.isArray(tileMap.pawns) ? tileMap.pawns.length : 0,
    buildingCount: Array.isArray(tileMap.buildings) ? tileMap.buildings.length : 0,
    itemCount: Array.isArray(tileMap.items) ? tileMap.items.length : 0,
    itemTotal: Number.isFinite(Number(tileMap.itemCount)) ? Number(tileMap.itemCount) : 0,
    itemsTruncated: tileMap.itemsTruncated === true,
    entityCount: tileMap.entities instanceof Map ? tileMap.entities.size : 0,
    entityTotal: Number.isFinite(Number(tileMap.entityCount)) ? Number(tileMap.entityCount) : 0,
    entityKeyframe: tileMap.entityKeyframe === true,
    entitiesTruncated: tileMap.entitiesTruncated === true,
    viewerPawnId: tileMap.viewerPawnId
  };
}

function resetMapStream() {
  mapStreamEpoch = null;
  mapStreamSeq = 0;
  mapStreamLastResyncAt = 0;
  chunkStreamEpoch = null;
  chunkStreamSeq = 0;
  resetEntityStream();
}

function resetEntityStream() {
  entityStreamEpoch = null;
  entityStreamSeq = 0;
  entityStreamLastResyncAt = 0;
}

function getMapEnvelopeNumber(msg, field) {
  const value = Number(msg?.[field]);
  return Number.isFinite(value) ? value : null;
}

function hasMapEnvelope(msg) {
  return getMapEnvelopeNumber(msg, 'mapEpoch') !== null ||
    getMapEnvelopeNumber(msg, 'seq') !== null ||
    getMapEnvelopeNumber(msg, 'baseSeq') !== null;
}

function getEntityEnvelopeNumber(msg, field) {
  const value = Number(msg?.[field]);
  return Number.isFinite(value) ? value : null;
}

function hasEntityEnvelope(msg) {
  return getEntityEnvelopeNumber(msg, 'entityEpoch') !== null ||
    getEntityEnvelopeNumber(msg, 'entitySeq') !== null ||
    getEntityEnvelopeNumber(msg, 'entityBaseSeq') !== null;
}

function requestEntityResync(reason, msg) {
  entityStreamLastResyncAt = Date.now();
  logClient('entity_resync_request', {
    reason,
    entityEpoch: entityStreamEpoch,
    lastSeq: entityStreamSeq || 0,
    msgEpoch: msg?.entityEpoch,
    msgSeq: msg?.entitySeq,
    msgBaseSeq: msg?.entityBaseSeq
  });
  requestMapResync(reason, msg);
}

function acceptEntityStateEnvelope(msg) {
  if (!hasEntityEnvelope(msg)) return true;

  const epoch = getEntityEnvelopeNumber(msg, 'entityEpoch');
  const seq = getEntityEnvelopeNumber(msg, 'entitySeq');
  const baseSeq = getEntityEnvelopeNumber(msg, 'entityBaseSeq');
  const snapshot = msg?.entitySnapshot === true || msg?.entityKeyframe === true || msg?.type === 'entity_keyframe';

  if (epoch === null || seq === null || seq < 1 || (!snapshot && (baseSeq === null || seq <= baseSeq))) {
    requestEntityResync('invalid_entity_envelope', msg);
    return false;
  }

  if (snapshot) {
    entityStreamEpoch = epoch;
    entityStreamSeq = seq;
    entityStreamLastResyncAt = 0;
    return true;
  }

  if (!hasTileData || !tileMap?.terrainCanvas) {
    requestEntityResync('entity_delta_without_baseline', msg);
    return false;
  }

  if (entityStreamEpoch !== epoch) {
    requestEntityResync('entity_epoch_mismatch', msg);
    return false;
  }

  if (baseSeq !== entityStreamSeq) {
    requestEntityResync('entity_seq_gap', msg);
    return false;
  }

  entityStreamSeq = seq;
  entityStreamLastResyncAt = 0;
  return true;
}

function acceptMapFullEnvelope(msg) {
  const epoch = getMapEnvelopeNumber(msg, 'mapEpoch');
  const seq = getMapEnvelopeNumber(msg, 'seq');
  if (epoch === null && seq === null) {
    mapStreamEpoch = null;
    mapStreamSeq = 0;
    mapStreamLastResyncAt = 0;
    return true;
  }

  if (epoch === null || seq === null || seq < 1) {
    requestMapResync('invalid_map_full_envelope', msg);
    return false;
  }

  mapStreamEpoch = epoch;
  mapStreamSeq = seq;
  mapStreamLastResyncAt = 0;
  chunkStreamEpoch = epoch;
  chunkStreamSeq = 0;
  return true;
}

function acceptMapChunkEnvelope(msg) {
  const epoch = getMapEnvelopeNumber(msg, 'mapEpoch');
  const seq = getMapEnvelopeNumber(msg, 'chunkSeq');
  const baseSeq = getMapEnvelopeNumber(msg, 'chunkBaseSeq');
  if (epoch === null && seq === null && baseSeq === null) {
    return !!(hasTileData && tileMap?.terrainCanvas);
  }

  if (epoch === null || seq === null || baseSeq === null || seq <= baseSeq) {
    requestMapResync('invalid_map_chunk_envelope', msg);
    return false;
  }

  if (!hasTileData || !tileMap?.terrainCanvas) {
    requestMapResync('chunk_without_baseline', msg);
    return false;
  }

  if (mapStreamEpoch !== epoch) {
    requestMapResync('chunk_epoch_mismatch', msg);
    return false;
  }

  if (chunkStreamEpoch !== epoch) {
    chunkStreamEpoch = epoch;
    chunkStreamSeq = 0;
  }

  if (baseSeq !== chunkStreamSeq) {
    requestMapResync('chunk_seq_gap', msg);
    return false;
  }

  chunkStreamSeq = seq;
  mapStreamLastResyncAt = 0;
  return true;
}

function acceptMapDeltaEnvelope(msg) {
  if (!hasMapEnvelope(msg)) return true;

  const epoch = getMapEnvelopeNumber(msg, 'mapEpoch');
  const seq = getMapEnvelopeNumber(msg, 'seq');
  const baseSeq = getMapEnvelopeNumber(msg, 'baseSeq');
  if (epoch === null || seq === null || baseSeq === null || seq <= baseSeq) {
    requestMapResync('invalid_map_delta_envelope', msg);
    return false;
  }

  if (!hasTileData || !tileMap?.terrainCanvas) {
    requestMapResync('delta_without_baseline', msg);
    return false;
  }

  if (mapStreamEpoch !== epoch) {
    requestMapResync('map_epoch_mismatch', msg);
    return false;
  }

  if (baseSeq !== mapStreamSeq) {
    requestMapResync('seq_gap', msg);
    return false;
  }

  mapStreamSeq = seq;
  mapStreamLastResyncAt = 0;
  return true;
}

function destroyTileMap() {
  if (!tileMap) return;
  if (typeof tileMap.destroy === 'function') tileMap.destroy();
  else tileMap.stop();
  tileMap = null;
}

function initTileMap() {
  if (tileMap) return;
  tileMap = new TileMapRenderer(mapCanvas);
  if (typeof tileMap.setTargetMode === 'function') tileMap.setTargetMode(targetMode);
  if (typeof tileMap.setFollowPawn === 'function') tileMap.setFollowPawn(followPawnMode);
  tileMap.onFollowChanged = (enabled) => {
    followPawnMode = !!enabled;
    try { localStorage.setItem(FOLLOW_PAWN_KEY, followPawnMode ? '1' : '0'); } catch (_) {}
    updateFollowPawnToggle();
  };

  // Left click → move command
  tileMap.onTileClick = (x, z) => {
    if (targetMode === 'attack') {
      if (!isActionAllowed('attack')) {
        appendLog(getActionBlockedReason('attack'));
        exitTargetMode();
        return;
      }
      markCommandSent('attack', `Attack sent near (${x}, ${z})`);
      if (typeof tileMap.markTarget === 'function') tileMap.markTarget(x, z, 'attack');
      const hoverTarget = typeof tileMap.getHoverTarget === 'function' ? tileMap.getHoverTarget() : null;
      const payload = { type: 'command', action: 'attack', x, z };
      if (hoverTarget?.id != null) payload.targetId = hoverTarget.id;
      send(payload);
      appendLog(`Attacking target near (${x}, ${z})`);
      exitTargetMode();
      return;
    }

    if (!isActionAllowed('move')) {
      appendLog(getActionBlockedReason('move'));
      return;
    }
    markCommandSent('move', `Move sent to (${x}, ${z})`);
    if (typeof tileMap.markTarget === 'function') tileMap.markTarget(x, z, 'move');
    send({ type: 'command', action: 'move', x, z });
    appendLog(`Moving to (${x}, ${z})`);
    if (targetMode === 'move') exitTargetMode();
  };

  // Right click → context menu
  tileMap.onTileRightClick = (x, z, clientX = null, clientY = null) => {
    if (hostCapabilities && hostCapabilities.contextMenu === false) {
      appendLog('Context actions unavailable on this RimWorld build');
      return;
    }
    if (!isActionAllowed('context_menu')) {
      appendLog(getActionBlockedReason('context_menu'));
      return;
    }
    lastContextMenuPoint = Number.isFinite(clientX) && Number.isFinite(clientY)
      ? { x: clientX, y: clientY }
      : null;
    markCommandSent('context_menu', `Checking actions near (${x}, ${z})`);
    if (typeof tileMap.markTarget === 'function') tileMap.markTarget(x, z, 'context_menu');
    const hoverTarget = typeof tileMap.getHoverTarget === 'function' ? tileMap.getHoverTarget() : null;
    const payload = { type: 'command', action: 'context_menu', x, z };
    if (hoverTarget?.id != null) {
      payload.targetId = hoverTarget.id;
      payload.targetKind = hoverTarget.kind || '';
      payload.targetLabel = hoverTarget.label || '';
    }
    send(payload);
  };
}

// ─── Map frame (JPEG fallback — used when tile data not available) ────────────
function updateLiveZoomLabel() {
  if (!mapZoomLabel) return;
  if (!liveFrameMeta || hasTileData) {
    mapZoomLabel.classList.add('hidden');
    return;
  }
  const zoom = Number.isFinite(Number(liveFrameZoom)) ? Number(liveFrameZoom) : 1;
  mapZoomLabel.textContent = `${zoom.toFixed(zoom < 1 ? 2 : 1)}x`;
  mapZoomLabel.classList.remove('hidden');
}

function liveFrameUsesServerZoom() {
  return !!(hostCapabilities?.serverCameraZoom && liveFrameMeta?.cameraMode === 'pawn');
}

function getLiveFrameDrawZoom() {
  if (!liveFrameUsesServerZoom()) return liveFrameZoom;
  const serverZoom = Number(liveFrameMeta?.zoom || 1);
  if (!Number.isFinite(serverZoom) || serverZoom <= 0) return liveFrameZoom;
  return Math.max(0.35, liveFrameZoom / serverZoom);
}

function scheduleServerCameraZoom(force = false, options = {}) {
  if (isTileRendererActive()) return;
  if (hostCapabilities && hostCapabilities.serverCameraZoom === false) return;
  if (options.followPawn) {
    liveFrameRequestedCenter = null;
  } else if (options.center && Number.isFinite(options.center.x) && Number.isFinite(options.center.z)) {
    liveFrameRequestedCenter = {
      x: Math.max(0, Math.min(Number(liveFrameMeta?.mapWidth || Infinity) - 1, options.center.x)),
      z: Math.max(0, Math.min(Number(liveFrameMeta?.mapHeight || Infinity) - 1, options.center.z))
    };
  }
  clearTimeout(liveFrameZoomTimer);
  liveFrameZoomTimer = setTimeout(() => {
    const zoom = Math.round(Math.max(LIVE_FRAME_MIN_ZOOM, Math.min(LIVE_FRAME_MAX_ZOOM, liveFrameZoom)) * 100) / 100;
    const aspect = computeViewportAspect();
    const pixelHeight = computeViewportPixelHeight();
    const aspectChanged = Math.abs(aspect - lastSentAspect) > 0.02;
    const heightChanged = Math.abs(pixelHeight - lastSentPixelHeight) > 48;
    const center = liveFrameRequestedCenter;
    const centerChanged = !!center && (
      lastSentCenterX == null ||
      Math.abs(center.x - lastSentCenterX) > 0.4 ||
      Math.abs(center.z - lastSentCenterZ) > 0.4
    );
    const followChanged = options.followPawn && (lastSentCenterX != null || lastSentCenterZ != null);
    if (!force && Math.abs(zoom - lastSentServerZoom) < 0.01 && !aspectChanged && !heightChanged && !centerChanged && !followChanged) return;
    lastSentServerZoom = zoom;
    lastSentAspect = aspect;
    lastSentPixelHeight = pixelHeight;
    const payload = { type: 'command', action: 'camera_zoom', zoom, aspect, pixelHeight };
    if (options.followPawn) {
      payload.followPawn = true;
      lastSentCenterX = null;
      lastSentCenterZ = null;
    } else if (center) {
      payload.centerX = Math.round(center.x * 10) / 10;
      payload.centerZ = Math.round(center.z * 10) / 10;
      lastSentCenterX = center.x;
      lastSentCenterZ = center.z;
    }
    send(payload);
  }, 120);
}

function computeViewportAspect() {
  if (!mapCanvas) return 16 / 9;
  const rect = mapCanvas.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) return 16 / 9;
  return Math.max(0.5, Math.min(3, rect.width / rect.height));
}

function computeViewportPixelHeight() {
  if (!mapCanvas) return 720;
  const rect = mapCanvas.getBoundingClientRect();
  const cssHeight = rect.height > 0 ? rect.height : window.innerHeight || 720;
  const dpr = Math.max(1, Math.min(window.devicePixelRatio || 1, 2));
  return Math.max(360, Math.min(1440, Math.round(cssHeight * dpr)));
}

// Resend aspect when the viewer resizes their window — debounced.
window.addEventListener('resize', () => {
  clearTimeout(aspectResizeTimer);
  aspectResizeTimer = setTimeout(() => {
    scheduleServerCameraZoom();
  }, 250);
});

function setLiveFrameZoom(nextZoom) {
  const clamped = Math.max(LIVE_FRAME_MIN_ZOOM, Math.min(LIVE_FRAME_MAX_ZOOM, nextZoom));
  if (Math.abs(clamped - liveFrameZoom) < 0.01) return;
  liveFrameZoom = clamped;
  liveFrameZoomInitialized = true;
  updateLiveZoomLabel();
  if (liveFrameImage) drawLiveFrameImage(liveFrameImage);
  scheduleServerCameraZoom();
  logClient('live_zoom', { zoom: Math.round(liveFrameZoom * 10) / 10 });
}

function resetLiveFrameView() {
  liveFrameZoom = 1;
  liveFrameZoomInitialized = true;
  liveFrameRequestedCenter = null;
  followPawnMode = true;
  try { localStorage.setItem(FOLLOW_PAWN_KEY, '1'); } catch (_) {}
  updateFollowPawnToggle();
  updateLiveZoomLabel();
  if (liveFrameImage) drawLiveFrameImage(liveFrameImage);
  scheduleServerCameraZoom(true, { followPawn: true });
  if (tileMap && typeof tileMap.setFollowPawn === 'function') tileMap.setFollowPawn(true);
}

function getLiveFramePanCenter() {
  if (liveFrameRequestedCenter) return liveFrameRequestedCenter;
  if (!liveFrameMeta) return null;
  return {
    x: Number(liveFrameMeta.centerX || 0),
    z: Number(liveFrameMeta.centerZ || 0)
  };
}

function clampLiveFrameCenter(center) {
  if (!center) return null;
  const maxX = Number(liveFrameMeta?.mapWidth || 0) > 0 ? Number(liveFrameMeta.mapWidth) - 1 : Infinity;
  const maxZ = Number(liveFrameMeta?.mapHeight || 0) > 0 ? Number(liveFrameMeta.mapHeight) - 1 : Infinity;
  return {
    x: Math.max(0, Math.min(maxX, center.x)),
    z: Math.max(0, Math.min(maxZ, center.z))
  };
}

function drawLiveFramePawnMarker() {
  if (!mapCtx || !liveFrameMeta || liveFrameMeta.cameraMode !== 'host' || !pawnState) return;
  const x = Number(pawnState.posX);
  const z = Number(pawnState.posZ);
  if (!Number.isFinite(x) || !Number.isFinite(z)) return;
  if (liveFrameMeta.radiusX <= 0 || liveFrameMeta.radiusZ <= 0 || !liveFrameDrawRect) return;

  const nx = 0.5 + ((x - liveFrameMeta.centerX) / (liveFrameMeta.radiusX * 2));
  const ny = 0.5 - ((z - liveFrameMeta.centerZ) / (liveFrameMeta.radiusZ * 2));
  if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return;

  const px = liveFrameDrawRect.x + nx * liveFrameDrawRect.width;
  const py = liveFrameDrawRect.y + ny * liveFrameDrawRect.height;
  const r = Math.max(7, Math.min(14, mapCanvas.width * 0.012));

  mapCtx.save();
  mapCtx.lineWidth = Math.max(2, r * 0.22);
  mapCtx.strokeStyle = '#36f36f';
  mapCtx.fillStyle = 'rgba(54, 243, 111, 0.16)';
  mapCtx.shadowColor = 'rgba(54, 243, 111, 0.55)';
  mapCtx.shadowBlur = r * 0.75;
  mapCtx.beginPath();
  mapCtx.arc(px, py, r, 0, Math.PI * 2);
  mapCtx.fill();
  mapCtx.stroke();
  mapCtx.restore();
}

function drawLiveFrameImage(img) {
  if (!img || !mapCtx) return;
  const cssW = mapCanvas.offsetWidth || 300;
  const cssH = mapCanvas.offsetHeight || 160;
  const sourceScale = img.width > 0 ? img.width / Math.max(1, cssW) : 1;
  const dpr = Math.max(1, Math.min(window.devicePixelRatio || 1, 2, sourceScale));
  const pixelW = Math.round(cssW * dpr);
  const pixelH = Math.round(cssH * dpr);
  if (mapCanvas.width !== pixelW || mapCanvas.height !== pixelH) {
    mapCanvas.width = pixelW;
    mapCanvas.height = pixelH;
  }
  const canvasAspect = mapCanvas.width / Math.max(1, mapCanvas.height);
  const imageAspect = img.width / Math.max(1, img.height);
  let drawW = mapCanvas.width;
  let drawH = mapCanvas.height;
  let drawX = 0;
  let drawY = 0;

  if (canvasAspect > imageAspect) {
    drawW = mapCanvas.width;
    drawH = drawW / imageAspect;
    drawY = (mapCanvas.height - drawH) / 2;
  } else {
    drawH = mapCanvas.height;
    drawW = drawH * imageAspect;
    drawX = (mapCanvas.width - drawW) / 2;
  }

  const drawZoom = getLiveFrameDrawZoom();
  drawW *= drawZoom;
  drawH *= drawZoom;
  drawX = (mapCanvas.width - drawW) / 2;
  drawY = (mapCanvas.height - drawH) / 2;

  liveFrameDrawRect = { x: drawX, y: drawY, width: drawW, height: drawH };
  mapCtx.clearRect(0, 0, mapCanvas.width, mapCanvas.height);
  mapCtx.imageSmoothingEnabled = true;
  mapCtx.imageSmoothingQuality = 'high';
  mapCtx.drawImage(img, drawX, drawY, drawW, drawH);
  drawLiveFramePawnMarker();
}

function handleMapFrame(msg) {
  const imageSrc = msg.binaryImageUrl || (msg.data ? `data:image/jpeg;base64,${msg.data}` : '');
  if (!imageSrc) return;
  if (!mapCtx) mapCtx = mapCanvas.getContext('2d');
  const frameStartedAt = performance.now();
  const objectUrl = msg.binaryImageUrl || null;
  const frameSeq = ++liveFrameLoadSeq;
  if (objectUrl) {
    if (pendingLiveFrameObjectUrl && pendingLiveFrameObjectUrl !== liveFrameObjectUrl && pendingLiveFrameObjectUrl !== objectUrl) {
      releaseLiveFrameObjectUrl(pendingLiveFrameObjectUrl);
    }
    pendingLiveFrameObjectUrl = objectUrl;
  }

  liveFrameMeta = {
    centerX: msg.centerX ?? 0,
    centerZ: msg.centerZ ?? 0,
    radiusX: msg.radiusX ?? 0,
    radiusZ: msg.radiusZ ?? 0,
    mapWidth: msg.mapWidth ?? 0,
    mapHeight: msg.mapHeight ?? 0,
    cameraMode: msg.cameraMode || 'pawn',
    zoom: msg.zoom ?? 1
  };
  if (!liveFrameZoomInitialized && liveFrameMeta.cameraMode === 'pawn') {
    const serverZoom = Number(liveFrameMeta.zoom || 1);
    if (Number.isFinite(serverZoom) && serverZoom > 0) {
      liveFrameZoom = Math.max(LIVE_FRAME_MIN_ZOOM, Math.min(LIVE_FRAME_MAX_ZOOM, serverZoom));
      lastSentServerZoom = Math.max(LIVE_FRAME_MIN_ZOOM, Math.min(LIVE_FRAME_MAX_ZOOM, liveFrameZoom));
    }
  }
  scheduleServerCameraZoom();

  if (isTileRendererActive()) {
    releaseLiveFrameObjectUrl(objectUrl);
    updateLiveZoomLabel();
    return;
  }

  hasTileData = false;
  mapCanvas.classList.add('live-map');

  const applyDecodedFrame = (img) => {
    if (frameSeq !== liveFrameLoadSeq) {
      releaseLiveFrameObjectUrl(objectUrl);
      return;
    }
    if (isTileRendererActive()) {
      releaseLiveFrameObjectUrl(objectUrl);
      return;
    }
    if (pendingLiveFrameObjectUrl === objectUrl) {
      pendingLiveFrameObjectUrl = null;
    }
    const decodeMs = performance.now() - frameStartedAt;
    if (objectUrl && liveFrameObjectUrl !== objectUrl) {
      releaseLiveFrameObjectUrl();
      liveFrameObjectUrl = objectUrl;
    } else if (!objectUrl) {
      releaseLiveFrameObjectUrl();
    }
    if (liveFrameImage && liveFrameImage !== img && typeof liveFrameImage.close === 'function') {
      try { liveFrameImage.close(); } catch (_) {}
    }
    liveFrameImage = img;
    drawLiveFrameImage(img);
    updateLiveZoomLabel();
    updateMapWaitingOverlay();
    noteFrameDraw(msg.dataBytes || (msg.data ? msg.data.length : 0), decodeMs);
  };

  const loadWithImageElement = () => {
    const img = new Image();
    img.onload = () => applyDecodedFrame(img);
    img.onerror = () => {
      releaseLiveFrameObjectUrl(objectUrl);
    };
    img.src = imageSrc;
  };

  if (typeof createImageBitmap === 'function') {
    const bitmapSource = objectUrl
      ? fetch(objectUrl).then(r => {
        if (!r.ok) throw new Error(`frame fetch ${r.status}`);
        return r.blob();
      })
      : Promise.resolve(imageSrc);

    Promise.resolve(bitmapSource)
      .then(src => createImageBitmap(src))
      .then(bitmap => {
        applyDecodedFrame(bitmap);
      })
      .catch(() => {
        loadWithImageElement();
      });
    return;
  }

  loadWithImageElement();
}

function noteFrameDraw(dataBytes, decodeMs) {
  clientFrameStats.frames++;
  clientFrameStats.bytes += dataBytes || 0;
  clientFrameStats.maxDecodeMs = Math.max(clientFrameStats.maxDecodeMs, decodeMs || 0);
  const elapsed = performance.now() - clientFrameStats.startedAt;
  if (elapsed < 10000) return;

  logClient('frame_stats', {
    frames: clientFrameStats.frames,
    seconds: Math.round(elapsed / 100) / 10,
    avgDataBytes: clientFrameStats.frames ? Math.round(clientFrameStats.bytes / clientFrameStats.frames) : 0,
    maxDecodeMs: Math.round(clientFrameStats.maxDecodeMs)
  });
  clientFrameStats = {
    startedAt: performance.now(),
    frames: 0,
    bytes: 0,
    maxDecodeMs: 0
  };
}

function liveFramePointToCell(clientX, clientY) {
  if (!liveFrameMeta || !liveFrameDrawRect || liveFrameMeta.radiusX <= 0 || liveFrameMeta.radiusZ <= 0) {
    return null;
  }

  const rect = mapCanvas.getBoundingClientRect();
  const sx = ((clientX - rect.left) / rect.width) * mapCanvas.width;
  const sy = ((clientY - rect.top) / rect.height) * mapCanvas.height;
  const nx = (sx - liveFrameDrawRect.x) / liveFrameDrawRect.width;
  const ny = (sy - liveFrameDrawRect.y) / liveFrameDrawRect.height;

  if (nx < 0 || nx > 1 || ny < 0 || ny > 1) {
    return null;
  }

  let x = Math.round(liveFrameMeta.centerX + (nx - 0.5) * liveFrameMeta.radiusX * 2);
  let z = Math.round(liveFrameMeta.centerZ - (ny - 0.5) * liveFrameMeta.radiusZ * 2);

  if (liveFrameMeta.mapWidth > 0) x = Math.max(0, Math.min(liveFrameMeta.mapWidth - 1, x));
  if (liveFrameMeta.mapHeight > 0) z = Math.max(0, Math.min(liveFrameMeta.mapHeight - 1, z));

  return { x, z };
}

function handleLiveMapClick(e) {
  if (!liveFrameMeta) return;
  if (!screenMain.classList.contains('active')) return;
  if (e.button != null && e.button !== 0) return;
  if (suppressNextLiveMapClick) {
    suppressNextLiveMapClick = false;
    return;
  }

  const now = performance.now();
  if (now - lastMapClickAt < 180) return;
  lastMapClickAt = now;

  const point = e.changedTouches ? e.changedTouches[0] : (e.touches ? e.touches[0] : e);
  if (!point) return;

  const cell = liveFramePointToCell(point.clientX, point.clientY);
  if (!cell) return;

  if (targetMode === 'attack') {
    if (!isActionAllowed('attack')) {
      appendLog(getActionBlockedReason('attack'));
      exitTargetMode();
      return;
    }
    markCommandSent('attack', `Attack sent near (${cell.x}, ${cell.z})`);
    send({ type: 'command', action: 'attack', x: cell.x, z: cell.z });
    appendLog(`Attacking target near (${cell.x}, ${cell.z})`);
    exitTargetMode();
    return;
  }

  if (!isActionAllowed('move')) {
    appendLog(getActionBlockedReason('move'));
    return;
  }
  markCommandSent('move', `Move sent to (${cell.x}, ${cell.z})`);
  send({ type: 'command', action: 'move', x: cell.x, z: cell.z });
  appendLog(`Moving to (${cell.x}, ${cell.z})`);
  if (targetMode === 'move') exitTargetMode();
}

// ─── Command result ───────────────────────────────────────────────────────────
function handleCommandResult(msg) {
  if (msg.silent) return;
  if (msg.action === 'preview_appearance' && msg.appearancePreview) {
    appearancePreviewData = msg.appearancePreview;
    appearancePreviewLabel = msg.previewLabel || 'Preview';
    renderCommandCenter();
    return;
  }
  const text = msg.message || (msg.ok ? 'OK' : 'Failed');
  statusText.textContent = text;
  appendLog(`${msg.ok === false ? 'Failed: ' : ''}${text}`);
  markCommandResult(msg.action, msg.ok !== false, text);
  if (msg.action === 'toolkit_purchase' || /purchase|toolkit|maxed|cooldown|coins/i.test(text)) {
    lastBuyFeedback = { ok: msg.ok !== false, message: text };
    if (activeCommandMenu === 'buy') renderCommandCenter();
  }
  setTimeout(() => { statusText.textContent = 'Connected'; }, 3000);
}

function handleHostCapabilities(msg) {
  hostCapabilities = msg;
  markHostOnline('host_capabilities');
  const version = msg.rimworldVersion || 'unknown';
  appendLog(`Host capabilities loaded for RimWorld ${version}`);
  syncCapabilityNotice();
  scheduleServerCameraZoom(true);
  applyCommandAvailability();
  if (hostCapabilities?.resourceReadout !== true) clearResourceReadout();
  else renderResourceReadout();
  const summary = buildCapabilitySummary(msg);
  if (summary) {
    appendLog(summary);
  }
}

function handleRelayCapabilities(msg) {
  relayCapabilities = msg || {};
  logClient('relay_capabilities', {
    replayCache: relayCapabilities.replayCache === true,
    cacheResync: relayCapabilities.cacheResync === true,
    version: relayCapabilities.version
  });
}

function handleToolkitState(msg) {
  toolkitState = msg || null;
  renderCommandCenterFromState();
}

function handlePermissions(msg) {
  viewerPermissions = msg;
  applyCommandAvailability();
}

// ─── Pawn died ────────────────────────────────────────────────────────────────
function handlePawnDied(msg) {
  const name = msg.pawnName || 'Your colonist';
  playSound('death');
  appendLog(`${name} died.`);
  updateDrawerPreview(`${name} died`, 'Activity');

  const deathFlash = document.createElement('div');
  deathFlash.style.cssText = 'position:fixed;inset:0;background:rgba(110,20,20,0.42);z-index:99;display:flex;align-items:center;justify-content:center;flex-direction:column;gap:10px;transition:opacity 1.6s';
  deathFlash.innerHTML = `<div style="font-size:20px;font-weight:700;color:#fff;letter-spacing:0.06em;text-transform:uppercase">Colonist lost</div><div style="font-size:18px;font-weight:700;color:#fff">${escapeHtml(name)} has died</div>`;
  document.body.appendChild(deathFlash);

  setTimeout(() => { deathFlash.style.opacity = '0'; }, 1500);
  setTimeout(() => {
    deathFlash.remove();
    resetAssignedState();

    const remaining = msg.ticketsRemaining;
    const deathCopy = remaining != null
      ? `${name} is gone. You can wait for a new assignment or spend one of your ${remaining} remaining ticket${remaining !== 1 ? 's' : ''} when a respawn portal opens.`
      : `${name} is gone. Wait for another assignment or for a respawn portal to appear.`;

    showLobbyState({
      phase: 'dead',
      title: 'Your colonist is gone',
      message: 'You are back in the queue until the streamer assigns you again.',
      statusHtml: '<span class="on">Connected</span> - waiting for reassignment',
      deathName: `${name} has died`,
      deathCopy
    });
  }, 2600);
}

// ─── Action log ───────────────────────────────────────────────────────────────
function handleActionLog(msg) {
  if (msg.entries && Array.isArray(msg.entries)) {
    msg.entries.forEach(e => appendLog(e));
  } else if (msg.entry) {
    appendLog(msg.entry);
  }
}

function handleModeration(msg) {
  const isBan = msg.type === 'banned';
  const reason = msg.message || (isBan ? 'You have been banned' : 'You have been kicked');
  appendLog(`[${isBan ? 'BANNED' : 'KICKED'}] ${reason}`);
  showLobbyState({
    phase: 'lobby',
    title: isBan ? 'Banned' : 'Kicked',
    message: reason,
    statusHtml: `<span class="off">${isBan ? 'Banned' : 'Kicked'}</span>`
  });
  if (isBan) {
    clearRelaySession();
    clearTwitchAccessToken();
    identity = null;
  }
}

function handleTimeout(msg) {
  const seconds = Number(msg.seconds) || 0;
  const reason = msg.message || `Timed out for ${seconds}s`;
  appendLog(`[TIMEOUT] ${reason}`);
}

function appendLog(text) {
  if (!logPanel) return;
  const line = document.createElement('div');
  line.className = 'log-line';
  line.textContent = text;
  logPanel.appendChild(line);
  updateDrawerPreview(text, 'Activity');
  while (logPanel.children.length > 40) logPanel.removeChild(logPanel.firstChild);
  logPanel.scrollTop = logPanel.scrollHeight;
}

// ─── Tile map handlers ────────────────────────────────────────────────────────
function handleMapFull(msg) {
  if (FORCE_JPEG_RENDERER) {
    hasTileData = false;
    return;
  }
  if (!acceptMapFullEnvelope(msg)) return;
  initTileMap();
  tileMap.setFullMap(msg);
  hasTileData = true;
  mapCanvas.classList.remove('live-map', 'panning');
  if (pawnState) {
    const x = Number(pawnState.posX);
    const z = Number(pawnState.posZ);
    if (Number.isFinite(x) && Number.isFinite(z)) tileMap.centerOnPawn(x, z);
    else tileMap.centerOnPawn();
  } else {
    tileMap.centerOnPawn();
  }
  updateLiveZoomLabel();
  updateMapWaitingOverlay();
  appendLog(`Map loaded: ${msg.width}x${msg.height}`);
}

function handleMapDelta(msg) {
  if (FORCE_JPEG_RENDERER) {
    return;
  }
  if (!acceptMapDeltaEnvelope(msg)) return;
  if (!tileMap) initTileMap();
  tileMap.setDelta(msg);
  if (hasTileData && typeof tileMap.centerOnPawn === 'function' && !tileMap.hasCenteredOnPawn) {
    if (pawnState) {
      const x = Number(pawnState.posX);
      const z = Number(pawnState.posZ);
      if (Number.isFinite(x) && Number.isFinite(z)) tileMap.centerOnPawn(x, z);
      else tileMap.centerOnPawn();
    } else {
      tileMap.centerOnPawn();
    }
  }
  updateMapWaitingOverlay();
  if (!hasTileData) {
    // No full map yet but we have deltas — request will come on next assign
  }
}

// ─── Game info ────────────────────────────────────────────────────────────────
function handleMapChunk(msg) {
  if (FORCE_JPEG_RENDERER) {
    return;
  }
  if (!acceptMapChunkEnvelope(msg)) return;
  if (!tileMap) initTileMap();
  if (typeof tileMap.applyChunk === 'function') tileMap.applyChunk(msg);
}

function handleEntityState(msg) {
  if (FORCE_JPEG_RENDERER) {
    return;
  }
  if (!acceptEntityStateEnvelope(msg)) return;
  if (!tileMap) initTileMap();
  tileMap.setDelta(msg);
}

function markHostOnline(source = 'host') {
  const wasOnline = hostOnline;
  hostOnline = true;
  lastHostStatusAt = Date.now();
  const speedEl = $('host-speed');
  // Leave "Host waiting" as soon as we know the host is up, even before game_info.
  let pillUpdated = false;
  if (speedEl && (speedEl.classList.contains('wait') || /waiting/i.test(speedEl.textContent || ''))) {
    speedEl.textContent = 'Host live';
    speedEl.className = 'host-speed';
    speedEl.title = 'Host is connected — waiting for colony clock/speed';
    pillUpdated = true;
  }
  if (!wasOnline || pillUpdated) renderCommandCenter();
  if (source && (!wasOnline || pillUpdated)) logClient('host_online', { source });
}

function markHostOffline() {
  hostOnline = false;
  hostCapabilities = null;
  const speedEl = $('host-speed');
  if (speedEl) {
    speedEl.textContent = 'Host offline';
    speedEl.className = 'host-speed wait';
    speedEl.title = 'The RimWorld host is not connected to the relay';
  }
  renderCommandCenter();
}

function updateHostSpeed(msg) {
  const speedEl = $('host-speed');
  if (!speedEl) return;
  hostOnline = true;
  lastHostStatusAt = Date.now();
  const speedValue = Number(msg?.speed ?? 0);
  const speedLabels = ['Paused', '1x', '2x', '3x', '4x'];
  const label = msg?.paused ? 'Paused' : (speedLabels[speedValue] || `${speedValue}x`);
  const speedClass = msg?.paused ? 'paused' : `speed-${Math.max(1, Math.min(4, speedValue || 1))}`;
  speedEl.textContent = msg?.paused ? 'Host paused' : `Host ${label}`;
  speedEl.className = `host-speed ${speedClass}`;
  speedEl.title = msg?.paused ? 'The RimWorld host is paused' : `The RimWorld host is running at ${label}`;
}

function handleGameInfo(msg) {
  updateHostSpeed(msg);
  const el = $('game-info');
  if (!el) return;
  const speedLabels = ['Paused', '1×', '2×', '3×', '4×'];
  const spd = msg.paused ? 'Paused' : (speedLabels[msg.speed] || `${msg.speed}×`);
  const time = `${String(msg.hour ?? '?').padStart(2, '0')}:00`;
  el.innerHTML =
    `<span>${msg.season || ''} Day ${msg.day || '?'}, Yr ${msg.year || '?'}</span>` +
    `<span>${time} · ${msg.temperature ?? '?'}°C · ${spd}</span>` +
    (msg.mapName ? `<span>${msg.mapName}</span>` : '');
}

// ─── Context menu ─────────────────────────────────────────────────────────────
function ctxPriorityClass(opt) {
  if (!opt || opt.disabled) return '';
  const p = Number(opt.priority);
  if (!Number.isFinite(p)) return '';
  // RimWorld MenuOptionPriority: AttackEnemy=6, RescueOrCapture=8, SummonThreat=9 are notable.
  if (p >= 6) return 'ctx-priority-high';
  if (p === 1) return 'ctx-priority-go';
  return '';
}

function handleContextMenu(msg) {
  const el = $('context-menu');
  if (!el || !msg.options) return;

  el.innerHTML = '';
  const targetLabel = String(msg.targetLabel || '').trim();
  const targetId = Number(msg.targetId);
  const cellX = Number.isFinite(Number(msg.x)) ? Number(msg.x) : null;
  const cellZ = Number.isFinite(Number(msg.z)) ? Number(msg.z) : null;
  const targetText = targetLabel || (cellX !== null && cellZ !== null ? `Cell ${cellX}, ${cellZ}` : 'Actions');
  const subText = cellX !== null && cellZ !== null
    ? `${targetId >= 0 ? `#${targetId} ` : ''}${cellX}, ${cellZ}`
    : (targetId >= 0 ? `#${targetId}` : '');
  const head = document.createElement('div');
  head.className = 'ctx-head';
  head.innerHTML = `<div class="ctx-target">${escapeHtml(targetText)}</div>${subText ? `<div class="ctx-cell">${escapeHtml(subText)}</div>` : ''}`;
  el.appendChild(head);

  msg.options.forEach(opt => {
    const div = document.createElement('div');
    const priorityClass = ctxPriorityClass(opt);
    div.className = 'ctx-option' + (opt.disabled ? ' disabled' : '') + (priorityClass ? ' ' + priorityClass : '');

    const labelText = String(opt.label || '?');
    const reason = opt.disabled ? String(opt.disabledReason || '').trim() : '';
    const tooltipText = String(opt.tooltip || '').trim();
    const iconLabel = String(opt.iconLabel || opt.iconDefName || '').trim();

    let bodyHtml = `<span class="ctx-option-label">${escapeHtml(labelText)}</span>`;
    if (iconLabel)
      bodyHtml += `<span class="ctx-option-icon" title="${escapeHtml(iconLabel)}">${escapeHtml(iconLabel)}</span>`;
    if (reason)
      bodyHtml += `<span class="ctx-option-reason">${escapeHtml(reason)}</span>`;
    div.innerHTML = bodyHtml;

    const tipParts = [];
    if (tooltipText && tooltipText !== reason) tipParts.push(tooltipText);
    if (opt.priorityName && opt.priorityName !== 'Default') tipParts.push(`Priority: ${opt.priorityName}`);
    if (tipParts.length) div.title = tipParts.join('\n');

    if (!opt.disabled) {
      div.addEventListener('click', () => {
        send({ type: 'command', action: 'context_action', optionId: opt.id });
        el.classList.add('hidden');
      });
    }
    el.appendChild(div);
  });

  el.classList.remove('hidden');
  const fallbackPoint = { x: window.innerWidth / 2, y: window.innerHeight * 0.4 };
  const point = Number.isFinite(lastContextMenuPoint?.x) && Number.isFinite(lastContextMenuPoint?.y)
    ? lastContextMenuPoint
    : fallbackPoint;
  const width = el.offsetWidth || 240;
  const height = el.offsetHeight || 260;
  const x = Math.max(8, Math.min(window.innerWidth - width - 8, point.x));
  const y = Math.max(8, Math.min(window.innerHeight - height - 8, point.y));
  el.style.left = `${x}px`;
  el.style.top = `${y}px`;
  el.style.transform = 'none';

  // Close on click outside
  setTimeout(() => {
    const closeCtx = (e) => {
      if (!el.contains(e.target)) { el.classList.add('hidden'); }
      document.removeEventListener('click', closeCtx);
    };
    document.addEventListener('click', closeCtx);
  }, 100);
}

// ─── Chat ─────────────────────────────────────────────────────────────────────
function handleChatMessage(msg) {
  if (msg.username && msg.message) {
    appendLog(`[${msg.username}] ${msg.message}`);
    appendChatLog(`[${msg.username}] ${msg.message}`);
    playSound('chat');
  }
}

// ─── Tab switching ────────────────────────────────────────────────────────────
document.querySelectorAll('.tab-row').forEach(row => {
  row.addEventListener('click', e => {
    const tab = e.target.closest('.tab');
    if (!tab) return;
    const target = tab.dataset.tab;
    if (!target) return;
    setActiveTab(target, { expand: true });
  });
});

// ─── Chat ─────────────────────────────────────────────────────────────────────
const chatInput = $('chat-input');
const btnChat   = $('btn-chat');
if (btnChat) btnChat.addEventListener('click', sendChat);
if (chatInput) chatInput.addEventListener('keydown', e => { if (e.key === 'Enter') sendChat(); });

function sendChat() {
  if (!chatInput) return;
  const msg = chatInput.value.trim();
  if (!msg) return;
  send({ type: 'chat', message: msg });
  appendChatLog(`[You] ${msg}`);
  chatInput.value = '';
}

function appendChatLog(text) {
  const el = $('chat-log');
  if (!el) return;
  const line = document.createElement('div');
  line.className = 'log-line';
  line.textContent = text;
  el.appendChild(line);
  while (el.children.length > 50) el.removeChild(el.firstChild);
  el.scrollTop = el.scrollHeight;
}

// ─── Capacities renderer ─────────────────────────────────────────────────────
function renderCapacities(caps) {
  const el = $('capacities-list');
  if (!el) return;
  if (!Array.isArray(caps) || caps.length === 0) {
    el.innerHTML = '';
    return;
  }
  // Show only deviations from 100%. Summarize the rest as a single muted line.
  const deviations = caps
    .map(c => ({ ...c, level: clampPercent(c.level), label: c.label || c.def || '?' }))
    .filter(c => c.level < 100)
    .sort((a, b) => a.level - b.level);
  const normalCount = caps.length - deviations.length;
  const rows = deviations.map(c => {
    const cls = c.level < 50 ? 'low' : c.level < 85 ? 'warn' : 'ok';
    return `<div class="cap-row">
      <div class="cap-head"><span class="cap-name">${escapeHtml(c.label)}</span><span class="cap-val ${cls}">${c.level}%</span></div>
      <div class="cap-meter"><span class="${cls}" style="width:${c.level}%"></span></div>
    </div>`;
  }).join('');
  const normalLine = normalCount > 0
    ? `<div class="cap-normal">${normalCount} ${normalCount === 1 ? 'system' : 'systems'} normal</div>`
    : '';
  el.innerHTML = `<div class="health-section-title">Capacities</div>
    <div class="capacity-board">${rows}${normalLine}</div>`;
}

// ─── Health renderer ──────────────────────────────────────────────────────────
function renderHealth(health) {
  const el = $('health-list');
  if (!el) return;
  if (!health) { el.innerHTML = ''; return; }

  const summaryHp = Number(health.summaryHp ?? 100);
  const pain = Number(health.painLevel ?? 0);
  const hediffs = Array.isArray(health.hediffs) ? health.hediffs : [];

  // Summary row only when meaningful.
  const headerBits = [];
  if (summaryHp < 100) {
    const cls = summaryHp < 50 ? 'low' : summaryHp < 85 ? 'warn' : 'ok';
    headerBits.push(`<span class="health-summary-stat ${cls}">${summaryHp}% overall</span>`);
  }
  if (pain > 0) {
    const cls = pain >= 60 ? 'low' : pain >= 30 ? 'warn' : 'ok';
    headerBits.push(`<span class="health-summary-stat ${cls}">${pain}% pain</span>`);
  }
  if (summaryHp >= 100 && pain <= 0 && hediffs.length === 0) {
    el.innerHTML = `<div class="health-section-title">Body</div>
      <div class="health-empty">No injuries or conditions.</div>`;
    return;
  }

  // Sort hediffs by severity descending; if no severity, push to bottom.
  const sorted = hediffs.slice().sort((a, b) => Number(b.severity ?? 0) - Number(a.severity ?? 0));
  const rows = sorted.map(h => {
    const sev = Number(h.severity ?? 0);
    const sevCls = sev >= 60 ? 'low' : sev >= 30 ? 'warn' : '';
    const part = h.part ? `<span class="hediff-part">${escapeHtml(h.part)}</span>` : '';
    return `<div class="hediff-row">
      <span class="hediff-label">${escapeHtml(h.label || '?')}${part}</span>
      <span class="hediff-sev ${sevCls}">${sev > 0 ? `${sev}%` : ''}</span>
    </div>`;
  }).join('');

  el.innerHTML = `<div class="health-section-title">
      <span>Body</span>
      ${headerBits.length ? `<span class="health-summary">${headerBits.join(' · ')}</span>` : ''}
    </div>${rows}`;
}

// ─── Social renderer ──────────────────────────────────────────────────────────
function renderSocial(s) {
  const el = $('social-list');
  if (!el) return;
  const people = buildSocialPeople(s);
  if (!people.length) {
    el.innerHTML = '<span style="color:var(--text-muted)">No relationships</span>';
    return;
  }
  if (!people.some(person => String(person.id) === String(activeSocialTargetId))) {
    activeSocialTargetId = people[0].id;
  }
  const active = people.find(person => String(person.id) === String(activeSocialTargetId)) || people[0];
  const blocked = getActionBlockedReason('social_interact');
  el.innerHTML = `<div class="social-board">
    <div class="social-list">
      <div class="social-sort">
        <button class="${socialSortMode === 'alpha' ? 'active' : ''}" data-social-sort="alpha">A-Z</button>
        <button class="${socialSortMode === 'distance' ? 'active' : ''}" data-social-sort="distance">Near</button>
        <button class="${socialSortMode === 'opinion' ? 'active' : ''}" data-social-sort="opinion">Opinion</button>
      </div>
      ${people.map(person => {
        const opinion = Number(person.opinion ?? 0);
        const distance = Number(person.distance);
        const cls = opinion > 0 ? 'positive' : opinion < 0 ? 'negative' : '';
        const sign = opinion > 0 ? '+' : '';
        return `<button class="social-person${String(person.id) === String(active.id) ? ' active' : ''}" data-social-target="${escapeAttr(person.id)}">
          <span>${escapeHtml(person.pawn)}</span>
          <strong class="${cls}">${sign}${escapeHtml(String(opinion))}</strong>
          <small>${escapeHtml([
            person.relation || 'colonist',
            Number.isFinite(distance) && distance > 0 ? `${Math.round(distance)} cells` : ''
          ].filter(Boolean).join(' - '))}</small>
        </button>`;
      }).join('')}
    </div>
    <div class="social-actions">
      <div class="social-actions-head">
        <span>${escapeHtml(active.pawn)}</span>
        <small>${escapeHtml(active.relation || 'colonist')}</small>
      </div>
      <button data-social-interaction="KindWords" ${blocked ? 'disabled' : ''}>Compliment</button>
      <button data-social-interaction="DeepTalk" ${blocked ? 'disabled' : ''}>Deep talk</button>
      <button data-social-interaction="RomanceAttempt" ${blocked ? 'disabled' : ''}>Flirt</button>
      <button data-social-interaction="Insult" ${blocked ? 'disabled' : ''}>Insult</button>
    </div>
  </div>`;
  bindSocialButtons(el);
}

function buildSocialPeople(s) {
  const byId = new Map();
  getArray(s?.opinions).forEach(item => {
    if (item?.id == null) return;
    byId.set(String(item.id), {
      id: item.id,
      pawn: item.pawn || 'colonist',
      opinion: Number(item.opinion ?? 0),
      distance: Number(item.distance ?? 0),
      relation: ''
    });
  });
  getArray(s?.relations).forEach(item => {
    if (item?.id == null) return;
    const key = String(item.id);
    const existing = byId.get(key) || {
      id: item.id,
      pawn: item.pawn || 'colonist',
      opinion: 0,
      distance: 0,
      relation: ''
    };
    existing.relation = item.relation || existing.relation;
    byId.set(key, existing);
  });
  return Array.from(byId.values()).sort((a, b) => {
    if (socialSortMode === 'alpha') {
      return String(a.pawn).localeCompare(String(b.pawn));
    }
    if (socialSortMode === 'opinion') {
      const oa = Number.isFinite(Number(a.opinion)) ? Number(a.opinion) : 0;
      const ob = Number.isFinite(Number(b.opinion)) ? Number(b.opinion) : 0;
      if (oa !== ob) return ob - oa;
      return String(a.pawn).localeCompare(String(b.pawn));
    }
    const da = Number.isFinite(a.distance) ? a.distance : 999;
    const db = Number.isFinite(b.distance) ? b.distance : 999;
    if (da !== db) return da - db;
    return String(a.pawn).localeCompare(String(b.pawn));
  });
}

function bindSocialButtons(root) {
  root.querySelectorAll('[data-social-sort]').forEach(btn => {
    btn.addEventListener('click', () => {
      socialSortMode = btn.dataset.socialSort || 'alpha';
      renderSocial(pawnState);
    });
  });
  root.querySelectorAll('[data-social-target]').forEach(btn => {
    btn.addEventListener('click', () => {
      activeSocialTargetId = btn.dataset.socialTarget || null;
      renderSocial(pawnState);
    });
  });
  root.querySelectorAll('[data-social-interaction]').forEach(btn => {
    btn.addEventListener('click', () => {
      sendSocialInteraction(activeSocialTargetId, btn.dataset.socialInteraction);
    });
  });
}

function sendSocialInteraction(targetId, interaction) {
  const blocked = getActionBlockedReason('social_interact');
  if (blocked) {
    appendLog(blocked);
    return;
  }
  const id = Number(targetId);
  if (!Number.isFinite(id) || !interaction) return;
  markCommandSent('social_interact', interaction);
  send({ type: 'command', action: 'social_interact', targetId: id, interaction });
}

// ─── Inventory renderer ───────────────────────────────────────────────────────
function renderInventory(inv) {
  const el = $('inventory-list');
  if (!el) return;
  if (!inv || !Array.isArray(inv) || inv.length === 0) {
    el.innerHTML = '<div class="quiet-empty">No carried inventory</div>';
    return;
  }
  el.innerHTML = `<div class="inventory-sheet">
    <div class="intel-title">Carried</div>
    ${inv.map(item => {
    const countStr = item.count > 1 ? ` x${item.count}` : '';
    const dropBlocked = getActionBlockedReason('drop_inventory');
    const hp = clampPercent(item.hp);
    return `<div class="inventory-row">
      <div class="gear-row-main">
        <span class="gear-kind">Carried</span>
        <span class="gear-name">${escapeHtml(item.label)}${escapeHtml(countStr)}</span>
        <span class="gear-meta-text">${item.ingestible ? 'Usable' : `${hp}% condition`}</span>
      </div>
      ${item.ingestible ? '<span class="condition-label">Use item</span>' : `<span class="condition ${conditionClass(hp)}"><span style="width:${hp}%"></span></span>`}
      <div class="inv-actions">
        ${item.ingestible ? `<button class="item-action" data-inv-action="consume" data-thing-id="${escapeAttr(item.id)}">Use</button>` : ''}
        <button class="item-action" data-inv-action="drop_inventory" data-thing-id="${escapeAttr(item.id)}" ${dropBlocked ? 'disabled' : ''} title="${escapeAttr(dropBlocked)}">Drop</button>
      </div>
    </div>`;
  }).join('')}</div>`;
  bindInventoryButtons(el);
}

window.sendInvAction = function(action, thingId) {
  const blocked = getActionBlockedReason(action);
  if (blocked) {
    appendLog(blocked);
    return;
  }
  markCommandSent(action, action === 'consume' ? 'Use item sent' : 'Drop item sent');
  send({ type: 'command', action, thingId: parseInt(thingId) });
};

function bindInventoryButtons(root) {
  root.querySelectorAll('[data-inv-action]').forEach(btn => {
    btn.addEventListener('click', () => {
      window.sendInvAction(btn.dataset.invAction, btn.dataset.thingId);
    });
  });
}

// ─── Portal available ────────────────────────────────────────────────────────
let respawnPortalId = null;

function handlePortalAvailable(msg) {
  respawnPortalId = msg.portalId;
  renderCommandCenter();
  // Show respawn button if in lobby
  if (screenLobby.classList.contains('active')) {
    showRespawnButton();
  }
}

function handleTicketUpdate(msg) {
  const count = msg.tickets ?? 0;
  const btn = $('btn-respawn');
  if (btn) {
    btn.disabled = count <= 0;
    btn.textContent = `Respawn (${count} ticket${count !== 1 ? 's' : ''})`;
  }
  if (lobbyTickets) lobbyTickets.textContent = `${count} ticket${count !== 1 ? 's' : ''}`;
  renderCommandCenter();
}

function showRespawnButton() {
  if ($('btn-respawn')) return; // already shown
  const btn = document.createElement('button');
  btn.id = 'btn-respawn';
  btn.className = 'btn-respawn';
  btn.textContent = 'Respawn';
  btn.addEventListener('click', () => {
    if (respawnPortalId != null) {
      send({ type: 'command', action: 'respawn', portalId: respawnPortalId });
      btn.disabled = true;
      btn.textContent = 'Respawning…';
    }
  });
  document.querySelector('.lobby-footer')?.appendChild(btn);
}

// --- Viewer command center -------------------------------------------------
function isSelectMenuOpen(root = document) {
  const active = document.activeElement;
  if (!active || active.tagName !== 'SELECT') return false;
  if (root && root !== document && !root.contains(active)) return false;
  return true;
}

function renderCommandCenter() {
  const focus = captureCommandFocus();
  renderCommandWindowChrome();
  renderCommandMenuNav();
  renderCommandMenuContent();
  restoreCommandFocus(focus);
}

function renderCommandCenterFromState() {
  if (!commandWindow || commandWindow.classList.contains('hidden')) return;
  // Never rebuild the open Orders page while a <select> is focused — pawn_state
  // ticks every ~167ms and would close the dropdown mid-choice.
  if (isSelectMenuOpen(commandWindow) || (Date.now() < commandInteractionUntil && commandMenuContent?.childElementCount)) {
    renderCommandWindowChrome();
    renderCommandMenuNav();
    return;
  }
  renderCommandCenter();
}

function markCommandInteraction(ms = 1400) {
  commandInteractionUntil = Math.max(commandInteractionUntil, Date.now() + ms);
}

function captureCommandFocus() {
  const active = document.activeElement;
  if (!active || !commandWindow?.contains(active)) return null;
  if (active.matches?.('[data-buy-qty-input]')) {
    return {
      kind: 'buyQty',
      sku: active.dataset.buyQtyInput || '',
      value: active.value,
      start: active.selectionStart,
      end: active.selectionEnd
    };
  }
  if (active.matches?.('[data-buy-stuff-select]')) {
    return { kind: 'buyStuff', sku: active.dataset.buyStuffSelect || '', value: active.value };
  }
  if (active.matches?.('[data-buy-search]')) {
    return {
      kind: 'buySearch',
      value: active.value,
      start: active.selectionStart,
      end: active.selectionEnd
    };
  }
  if (active.matches?.('[data-buy-arg-input]')) {
    return {
      kind: 'buyArg',
      sku: active.dataset.buyArgInput || '',
      value: active.value,
      start: active.selectionStart,
      end: active.selectionEnd
    };
  }
  if (active.matches?.('[data-story-purchase-select]')) {
    return { kind: 'storyPurchase', key: active.dataset.storyPurchaseSelect || '', value: active.value };
  }
  if (active.matches?.('[data-policy-action]')) {
    return {
      kind: 'policy',
      action: active.dataset.policyAction || '',
      field: active.dataset.policyField || '',
      value: active.value
    };
  }
  if (active.tagName === 'SELECT') {
    return { kind: 'select', name: active.getAttribute('name') || '', value: active.value };
  }
  return null;
}

function restoreCommandFocus(focus) {
  if (!focus || commandWindow?.classList.contains('hidden')) return;
  if (focus.kind === 'buyQty' && focus.sku) {
    const input = commandWindow.querySelector(`[data-buy-qty-input="${cssEscape(focus.sku)}"]`);
    if (!input) return;
    input.focus({ preventScroll: true });
    input.value = focus.value;
    try {
      const start = Number.isFinite(focus.start) ? focus.start : input.value.length;
      const end = Number.isFinite(focus.end) ? focus.end : start;
      input.setSelectionRange(start, end);
    } catch {}
    return;
  }
  if (focus.kind === 'buyStuff' && focus.sku) {
    const sel = commandWindow.querySelector(`[data-buy-stuff-select="${cssEscape(focus.sku)}"]`);
    if (!sel) return;
    if (focus.value != null) sel.value = focus.value;
    sel.focus({ preventScroll: true });
    return;
  }
  if (focus.kind === 'buySearch') {
    const input = commandWindow.querySelector('[data-buy-search]');
    if (!input) return;
    input.focus({ preventScroll: true });
    if (focus.value != null) input.value = focus.value;
    try {
      const start = Number.isFinite(focus.start) ? focus.start : input.value.length;
      const end = Number.isFinite(focus.end) ? focus.end : start;
      input.setSelectionRange(start, end);
    } catch {}
    return;
  }
  if (focus.kind === 'buyArg' && focus.sku) {
    const input = commandWindow.querySelector(`[data-buy-arg-input="${cssEscape(focus.sku)}"]`);
    if (!input) return;
    input.focus({ preventScroll: true });
    if (focus.value != null) input.value = focus.value;
    try {
      const start = Number.isFinite(focus.start) ? focus.start : input.value.length;
      const end = Number.isFinite(focus.end) ? focus.end : start;
      input.setSelectionRange(start, end);
    } catch {}
    return;
  }
  if (focus.kind === 'storyPurchase' && focus.key) {
    const sel = commandWindow.querySelector(`[data-story-purchase-select="${cssEscape(focus.key)}"]`);
    if (!sel) return;
    if (focus.value != null) sel.value = focus.value;
    sel.focus({ preventScroll: true });
    return;
  }
  if (focus.kind === 'policy' && focus.action) {
    const sel = commandWindow.querySelector(
      `[data-policy-action="${cssEscape(focus.action)}"][data-policy-field="${cssEscape(focus.field || '')}"]`
    );
    if (!sel) return;
    if (focus.value != null) sel.value = focus.value;
    sel.focus({ preventScroll: true });
  }
}

function statusClass(ok, waiting = false) {
  if (ok) return 'ok';
  return waiting ? 'wait' : 'off';
}

function openCommandWindow(section = activeCommandMenu || 'quick') {
  activeCommandMenu = section;
  commandWindow?.classList.remove('hidden');
  document.body.classList.add('command-window-open');
  renderCommandCenter();
}

function closeCommandWindow() {
  commandWindow?.classList.add('hidden');
  document.body.classList.remove('command-window-open');
}

function renderCommandWindowChrome() {
  if (!commandWindow) return;
  if (commandWindowTitle) {
    commandWindowTitle.textContent = pawnState?.name
      ? `${pawnState.name} orders`
      : 'Orders';
  }
  renderConnectionPanel();
}

function renderConnectionPanel() {
  if (!commandWindowStatus) return;
  const relayReady = !!(ws && ws.readyState === WebSocket.OPEN && relayOnline);
  const hostReady = !!(hostOnline || hostCapabilities || pawnState);
  const pawnReady = !!pawnState;

  let summary, cls;
  if (!relayReady) {
    summary = 'Reconnecting to relay server…';
    cls = 'wait';
  } else if (!hostReady) {
    summary = 'Waiting for streamer to load a colony';
    cls = 'wait';
  } else if (!pawnReady) {
    summary = 'Connected — waiting for pawn assignment';
    cls = 'wait';
  } else {
    const name = pawnState.name || 'your pawn';
    summary = `Assigned to ${name} — all commands available`;
    cls = 'ok';
  }

  commandWindowStatus.innerHTML = `<span class="status-summary ${escapeAttr(cls)}">${escapeHtml(summary)}</span>`;
}

function renderStatusPill(label, ok, value) {
  return `<span class="status-pill ${escapeAttr(statusClass(ok, true))}"><span></span>${escapeHtml(label)} ${escapeHtml(value)}</span>`;
}

function renderCommandMenuNav() {
  if (!commandMenuNav) return;
  commandMenuNav.innerHTML = COMMAND_MENU_SECTIONS.map(section =>
    `<button class="command-nav-btn${activeCommandMenu === section.id ? ' active' : ''}" data-command-section="${escapeAttr(section.id)}">${escapeHtml(section.label)}</button>`
  ).join('');
}

function renderCommandMenuContent() {
  if (!commandMenuContent) return;
  if (!pawnState && activeCommandMenu !== 'help' && activeCommandMenu !== 'buy') {
    commandMenuContent.innerHTML = `<div class="command-empty">
      <div class="command-page-title">Waiting for assignment</div>
      <p>Orders unlock as soon as your pawn is linked. You can stay here or go back to the claim screen.</p>
      <button class="order-card primary" data-command-action="resync"><span>Check assignment</span><small>Requests fresh state from the game host</small></button>
    </div>`;
    return;
  }

  switch (activeCommandMenu) {
    case 'buy':
      commandMenuContent.innerHTML = `<div class="command-page">${renderBuyControls()}</div>`;
      requestToolkitState();
      break;
    case 'story':
      commandMenuContent.innerHTML = `<div class="command-page">${renderStoryControls(pawnState)}</div>`;
      break;
    case 'work':
      commandMenuContent.innerHTML = `<div class="command-page">${renderWorkControls(pawnState)}</div>`;
      break;
    case 'schedule':
      commandMenuContent.innerHTML = `<div class="command-page">${renderScheduleControls(pawnState)}</div>`;
      break;
    case 'policies':
      commandMenuContent.innerHTML = `<div class="command-page">${renderPolicyPage(pawnState)}</div>`;
      break;
    case 'help':
      commandMenuContent.innerHTML = renderAliasHelp();
      break;
    default:
      commandMenuContent.innerHTML = renderQuickCommandPage();
      break;
  }
}

function renderQuickCommandPage() {
  const respawnDisabled = respawnPortalId == null ? 'disabled' : '';
  const drafted = !!pawnState?.drafted;
  const job = pawnState?.currentJob || 'Idle';
  const hp = pawnState?.health?.summaryHp ?? 100;
  const mood = pawnState?.needs?.Mood ?? pawnState?.needs?.mood ?? 50;
  const hpCls = hp < 50 ? 'red' : hp < 75 ? 'yellow' : 'green';
  const moodCls = mood < 30 ? 'red' : mood < 50 ? 'yellow' : 'green';

  return `<div class="command-page order-sheet-page">
    <div class="command-page-title">Quick orders</div>
    <div class="quick-status-strip">
      <span>${escapeHtml(job)}</span>
      <span class="qs-divider">·</span>
      <span class="qs-${hpCls}">HP ${hp}%</span>
      <span class="qs-divider">·</span>
      <span class="qs-${moodCls}">Mood ${mood}%</span>
      <span class="qs-divider">·</span>
      <span>${drafted ? 'Drafted' : 'Undrafted'}</span>
    </div>
    <div class="quick-action-row">
      <button class="quick-act-btn" data-command-action="quick-draft">${drafted ? 'Undraft' : 'Draft'}</button>
      <button class="quick-act-btn" data-command-action="quick-resync">Resync</button>
      <button class="quick-act-btn" data-command-action="respawn" ${respawnDisabled}>Respawn</button>
    </div>
    <div class="order-sheet">
      <div class="order-sheet-group">
        <div class="order-sheet-label">View pawn</div>
        ${renderOrderRow('Body', 'Health, injuries, thoughts', 'health')}
        ${renderOrderRow('Skills', 'Levels and passions', 'skills')}
        ${renderOrderRow('Gear', 'Equipment and inventory', 'gear')}
        ${renderOrderRow('Social', 'Relations and opinions', 'social')}
      </div>
      <div class="order-sheet-group">
        <div class="order-sheet-label">Control pawn</div>
        ${renderOrderSectionRow('Story', 'Backstory, traits, passions', 'story')}
        ${renderOrderSectionRow('Work', 'Job priority numbers', 'work')}
        ${renderOrderSectionRow('Schedule', 'Daily hour-by-hour plan', 'schedule')}
        ${renderOrderSectionRow('Restrictions', 'Outfit, food, drugs, area', 'policies')}
      </div>
      <div class="order-sheet-group">
        <div class="order-sheet-label">Stream</div>
        ${renderOrderSectionRow('Buy', 'Toolkit store — coins, items, events', 'buy', 'in Overlord')}
        ${renderOrderRow('Events', 'Viewer events and votes', 'events')}
        ${renderOrderRow('Chat', 'Send an in-game message', 'chat')}
      </div>
    </div>
  </div>`;
}

function renderOrderRow(title, detail, tab, alias = '', primary = false) {
  return `<button class="order-row${primary ? ' primary' : ''}" data-command-open="${escapeAttr(tab)}">
    <span class="order-row-title">${escapeHtml(title)}</span>
    <span class="order-row-detail">${escapeHtml(detail)}</span>
    ${alias ? `<span class="order-row-alias">${escapeHtml(alias)}</span>` : ''}
  </button>`;
}

function renderOrderSectionRow(title, detail, section, alias = '', primary = false) {
  return `<button class="order-row${primary ? ' primary' : ''}" data-command-section="${escapeAttr(section)}">
    <span class="order-row-title">${escapeHtml(title)}</span>
    <span class="order-row-detail">${escapeHtml(detail)}</span>
    ${alias ? `<span class="order-row-alias">${escapeHtml(alias)}</span>` : ''}
  </button>`;
}

function renderOrderActionRow(title, detail, action, alias = '', disabled = '') {
  return `<button class="order-row" data-command-action="${escapeAttr(action)}" ${disabled}>
    <span class="order-row-title">${escapeHtml(title)}</span>
    <span class="order-row-detail">${escapeHtml(detail)}</span>
    ${alias ? `<span class="order-row-alias">${escapeHtml(alias)}</span>` : ''}
  </button>`;
}

function renderStoryControls(state) {
  if (!state) return '<div class="command-empty">No pawn story is loaded yet.</div>';
  const story = state.story || {};
  const traits = getArray(state.traits);
  const skills = getArray(state.skills);
  const passions = skills.filter(skill => Number(skill.passion || 0) > 0);

  return `<div class="story-page">
    <div class="command-page-title">Pawn story</div>
    <div class="story-sheet">
      <div class="story-row"><span>Title</span><strong>${escapeHtml(story.title || 'Unset')}</strong></div>
      <div class="story-row"><span>Childhood</span><strong>${escapeHtml(story.childhood || 'Unset')}</strong></div>
      <div class="story-row"><span>Adulthood</span><strong>${escapeHtml(story.adulthood || 'Unset')}</strong></div>
    </div>
    ${renderStoryCustomizationControls(state)}
    ${renderFreeAppearanceControls(state)}
    <div class="story-block">
      <div class="story-block-title">Traits</div>
      <div class="story-tags">${traits.length ? traits.map(t => `<span>${escapeHtml(t.label || t.defName || t.def || t)}</span>`).join('') : '<span>None</span>'}</div>
    </div>
    <div class="story-block">
      <div class="story-block-title">Passions</div>
      <div class="story-tags">${passions.length ? passions.map(skill => `<span>${escapeHtml(skill.label || skill.name || skill.def || '?')}</span>`).join('') : '<span>None</span>'}</div>
    </div>
  </div>`;
}

function renderStoryCustomizationControls(state) {
  const rows = getStoryPurchaseRows(state);
  return `<div class="story-customizer">
    ${renderStoryWallet()}
    ${rows.length
      ? `<div class="story-purchase-list">${rows.map(row => renderStoryPurchaseRow(row)).join('')}</div>`
      : `<div class="story-note">${toolkitState ? 'No pawn customization purchases are exposed by the current Toolkit store.' : 'Waiting for Twitch Toolkit prices before paid story changes are shown.'}</div>`}
  </div>`;
}

function renderStoryWallet() {
  if (!toolkitState) {
    return `<div class="story-wallet">
      <span>Coins</span>
      <strong>Waiting</strong>
      <button class="toolkit-refresh" data-command-action="toolkit-refresh">Refresh</button>
    </div>`;
  }
  const coins = toolkitState.unlimitedCoins ? 'Unlimited' : formatNumber(toolkitState.coins ?? 0);
  const connected = !!toolkitState.chatConnected && !!toolkitState.available;
  return `<div class="story-wallet">
    <span>Coins</span>
    <strong>${escapeHtml(coins)}</strong>
    <span>Toolkit</span>
    <strong class="${connected ? 'ok' : 'warn'}">${escapeHtml(connected ? 'Connected' : 'Offline')}</strong>
    <button class="toolkit-refresh" data-command-action="toolkit-refresh">Refresh</button>
  </div>`;
}

function getStoryPurchaseRows(state) {
  const entries = getArray(toolkitState?.entries);
  if (!entries.length) return [];
  const used = new Set();
  const rows = [];
  STORY_PURCHASE_TYPES.forEach(type => {
    const entry = findStoryPurchaseEntry(type, used);
    if (!entry) return;
    if (Number(entry.variables || 0) > 1) return;
    if (entry.needsInput && hostCapabilities?.storyPurchaseArguments !== true) return;
    const options = getStoryOptionsForType(type.optionType, state);
    if (type.optionType && !options.length) return;
    used.add(String(entry.sku || '').toLowerCase());
    rows.push({ type, entry, options });
  });

  entries.forEach(entry => {
    const sku = String(entry?.sku || '').toLowerCase();
    if (!sku || used.has(sku) || sku === 'pawn') return;
    if (String(entry?.category || '').toLowerCase() !== 'pawn') return;
    if (entry.needsInput) return;
    used.add(sku);
    rows.push({
      type: {
        key: `toolkit-${sku}`,
        title: entry.label || sku,
        detail: entry.description || entry.command || 'Toolkit pawn purchase',
        optionType: ''
      },
      entry,
      options: []
    });
  });

  return rows.slice(0, 8);
}

function findStoryPurchaseEntry(type, used) {
  const entries = getArray(toolkitState?.entries);
  const skuSet = new Set(getArray(type.skus).map(sku => String(sku).toLowerCase()));
  return entries.find(entry => {
    const sku = String(entry?.sku || '').toLowerCase();
    if (!sku || used.has(sku)) return false;
    if (skuSet.has(sku)) return true;
    const label = String(entry?.label || '').toLowerCase();
    return getArray(type.skus).some(alias => label.includes(String(alias).replace(/_/g, ' ')));
  });
}

function getStoryOptionsForType(optionType, state) {
  // Toolkit's trait commands resolve by trait LABEL, not RimWorld defName — and
  // degreed traits share one defName ("Iron-willed"/"Steadfast" are both Nerves),
  // so defName values made those options identical and unresolvable. Send the label.
  if (optionType === 'traitOptions') {
    return getArray(state?.traitOptions).map(trait => ({
      value: trait?.label || trait?.defName || trait,
      label: trait?.label || trait?.defName || trait
    })).filter(option => option.value);
  }
  if (optionType === 'currentTraits') {
    return getArray(state?.traits).map(trait => ({
      value: trait?.label || trait?.defName || trait,
      label: trait?.label || trait?.defName || trait
    })).filter(option => option.value);
  }
  if (optionType === 'skills') {
    return getArray(state?.skills).filter(skill => !skill?.disabled).map(skill => ({
      value: skill?.name || skill?.def || skill?.label,
      label: skill?.label || skill?.name || skill?.def
    })).filter(option => option.value);
  }
  return [];
}

function getStorySelection(key, options) {
  if (!options.length) return '';
  const saved = storyPurchaseSelections.get(key);
  if (saved && options.some(option => String(option.value) === saved)) return saved;
  const next = String(options[0].value);
  storyPurchaseSelections.set(key, next);
  return next;
}

function renderStoryPurchaseRow(row) {
  const entry = row.entry || {};
  const type = row.type || {};
  const key = type.key || String(entry.sku || '');
  const buyState = getBuyItemState(entry);
  const selected = getStorySelection(key, row.options || []);
  const canBuy = !!toolkitState?.available && !!toolkitState?.chatConnected;
  const needsSelection = !!entry.needsInput || !!type.optionType;
  const missingSelection = needsSelection && !selected;
  const disabled = !canBuy || !buyState.hasPrice || !buyState.affordable || missingSelection || buyState.needsAssignedPawn;
  const reason = buyState.needsAssignedPawn
    ? 'Needs assigned colonist'
    : (missingSelection
    ? 'Choose'
    : (!buyState.hasPrice ? 'No price'
      : (!buyState.affordable ? 'Not enough coins' : (!canBuy ? 'Offline' : 'Buy'))));
  const options = getArray(row.options);
  const selector = options.length ? `<select data-story-purchase-select="${escapeAttr(key)}">
    ${options.map(option => `<option value="${escapeAttr(option.value)}" ${String(option.value) === selected ? 'selected' : ''}>${escapeHtml(option.label)}</option>`).join('')}
  </select>` : '';
  return `<div class="story-purchase-row">
    <div class="story-purchase-copy">
      <strong>${escapeHtml(type.title || entry.label || entry.sku)}</strong>
      <span>${escapeHtml(type.detail || entry.description || entry.syntax || entry.command || '')}</span>
    </div>
    <div class="story-purchase-control">
      ${selector}
      <span class="story-price">${escapeHtml(formatPrice(buyState.totalCost))}</span>
      <button data-story-purchase-sku="${escapeAttr(entry.sku || '')}" data-story-purchase-kind="${escapeAttr(entry.kind || '')}" data-story-purchase-key="${escapeAttr(key)}" data-story-purchase-argument="${escapeAttr(selected)}" ${disabled ? 'disabled' : ''}>${escapeHtml(reason)}</button>
    </div>
  </div>`;
}

function renderFreeAppearanceControls(state) {
  const appearance = state?.appearance || {};
  const hairOptions = getArray(appearance.hairOptions);
  const genderOptions = getArray(appearance.genderOptions);
  const freeAvailable = viewerPermissions?.freeAppearanceAvailable !== false;
  const unlocked = freeAvailable || viewerPermissions?.appearance === true;
  const blocked = unlocked ? '' : 'Free appearance change already used';
  const currentHair = appearance.hairDef || '';
  const currentGender = appearance.gender || '';
  const selectedHair = appearanceDraftHairDef || currentHair || String(hairOptions[0]?.defName || hairOptions[0] || '');
  const selectedGender = appearanceDraftGender || currentGender || String(genderOptions[0] || 'Male');
  const selectedHairOption = hairOptions.find(hair => String(hair?.defName || hair || '') === selectedHair);
  const selectedHairLabel = selectedHairOption?.label || selectedHair || 'Unset';
  const previewImage = appearancePreviewData || currentPortraitData;
  const previewTitle = appearancePreviewData ? (appearancePreviewLabel || 'Preview') : 'Current';
  const previewBody = `${selectedHairLabel} / ${selectedGender || 'Unset'}`;

  return `<div class="appearance-once">
    <div class="story-block-title">Free appearance change</div>
    <div class="appearance-preview">
      <div class="appearance-preview-portrait">
        ${previewImage
          ? `<img src="data:image/png;base64,${escapeAttr(previewImage)}" alt="">`
          : '<span>No portrait</span>'}
      </div>
      <div class="appearance-preview-copy">
        <span>${escapeHtml(previewTitle)}</span>
        <strong>${escapeHtml(previewBody)}</strong>
      </div>
      <button data-command-action="preview-appearance" ${blocked ? 'disabled' : ''}>Preview</button>
    </div>
    <div class="appearance-grid">
      <div class="appearance-stepper">
        <span>Hair</span>
        <button data-appearance-hair-step="-1" ${blocked || hairOptions.length < 2 ? 'disabled' : ''}>Prev</button>
        <strong>${escapeHtml(selectedHairLabel)}</strong>
        <button data-appearance-hair-step="1" ${blocked || hairOptions.length < 2 ? 'disabled' : ''}>Next</button>
      </div>
      <div class="appearance-gender">
        <span>Gender</span>
        <div class="segmented-control">
          ${genderOptions.map(gender => {
            const value = String(gender || '');
            return `<button class="segment-btn${value === selectedGender ? ' active' : ''}" data-appearance-gender="${escapeAttr(value)}" ${blocked ? 'disabled' : ''}>${escapeHtml(value)}</button>`;
          }).join('')}
        </div>
      </div>
      <button data-command-action="set-appearance" ${blocked ? 'disabled' : ''} title="${escapeAttr(blocked)}">${escapeHtml(freeAvailable ? 'Use free change' : 'Change')}</button>
    </div>
    <div class="appearance-note">${freeAvailable ? 'One no-cost hairstyle and gender update is available.' : (viewerPermissions?.appearance ? 'Appearance changes are unlocked by the host.' : 'The free appearance change has already been used.')}</div>
  </div>`;
}

function renderBuyControls() {
  if (!toolkitState) {
    const hostKnown = !!(hostCapabilities || hostOnline);
    const waitMsg = hostKnown
      ? 'The streamer\'s game host is connected but Twitch Toolkit hasn\'t reported yet. Click Refresh or wait a moment.'
      : 'Waiting for the game host to connect. The Buy page loads once Twitch Toolkit reports in.';
    return `<div class="toolkit-page">
      <div class="command-page-title">Buy</div>
      <div class="toolkit-empty">${escapeHtml(waitMsg)}</div>
      <button class="order-row" data-command-action="toolkit-refresh">
        <span class="order-row-title">Refresh</span>
        <span class="order-row-detail">Ask the game host for Toolkit balance and store state</span>
      </button>
    </div>`;
  }

  const entries = getArray(toolkitState.entries);
  const connected = !!toolkitState.chatConnected;
  const available = !!toolkitState.available;
  const coins = Number(toolkitState.coins ?? 0);
  const karma = Number(toolkitState.karma ?? 0);
  const unlimited = !!toolkitState.unlimitedCoins;
  const status = available
    ? (connected ? 'Connected' : 'Toolkit offline')
    : 'Not loaded';
  const canBuy = available && connected;
  const query = String(buySearchQuery || '').trim().toLowerCase();
  const decorated = entries
    .map(item => ({ item, state: getBuyItemState(item), shop: getBuyShop(item) }))
    .filter(row => row.item)
    .filter(row => {
      if (!query) return true;
      const hay = [
        row.item?.sku,
        row.item?.label,
        row.item?.description,
        row.item?.command,
        row.item?.kind,
        row.shop,
        row.item?.syntax
      ].filter(Boolean).join(' ').toLowerCase();
      return hay.includes(query);
    });
  const affordable = decorated.filter(row => row.state.listedAffordable && !row.state.needsInput);
  const shops = groupBuyRowsByShop(decorated);
  const visibleShopKeys = BUY_SHOP_ORDER.filter(key => shops.get(key)?.length);
  if (activeBuyShop !== 'all' && !shops.get(activeBuyShop)?.length) activeBuyShop = 'all';
  const selectedShopKeys = activeBuyShop === 'all' ? visibleShopKeys : [activeBuyShop];
  const feedback = lastBuyFeedback
    ? `<div class="buy-feedback ${lastBuyFeedback.ok ? 'ok' : 'fail'}">${escapeHtml(lastBuyFeedback.message || '')}</div>`
    : '';

  return `<div class="toolkit-page buy-page">
    <div class="buy-wallet-bar">
      <div class="buy-wallet-stats">
        <span><strong>${unlimited ? '∞' : escapeHtml(formatNumber(coins))}</strong> coins</span>
        <span class="buy-wallet-sep">·</span>
        <span><strong>${escapeHtml(formatKarma(karma))}</strong> karma</span>
        <span class="buy-wallet-sep">·</span>
        <span class="buy-wallet-status ${connected ? 'ok' : 'warn'}">${escapeHtml(status)}</span>
      </div>
      <button class="toolkit-refresh" data-command-action="toolkit-refresh">Refresh</button>
    </div>
    ${canBuy ? renderToolkitRate() : ''}
    ${feedback}
    ${!available ? `<div class="buy-banner">Twitch Toolkit is not loaded on the host.</div>` : ''}
    ${available && !connected ? `<div class="buy-banner warn">Toolkit chat is offline on the host — purchases stay locked until it reconnects in RimWorld.</div>` : ''}
    <div class="buy-toolbar">
      <input class="buy-search" type="search" data-buy-search value="${escapeAttr(buySearchQuery)}" placeholder="Search…" aria-label="Search store">
      <span class="buy-toolbar-count">${escapeHtml(String(decorated.length))}</span>
    </div>
    <div class="buy-shop-tabs">
      ${renderBuyShopTab('all', decorated.length, affordable.length)}
      ${visibleShopKeys.map(key => renderBuyShopTab(key, shops.get(key).length, shops.get(key).filter(row => row.state.listedAffordable && !row.state.needsInput).length)).join('')}
    </div>
    <div class="buy-shops">
      ${selectedShopKeys.length
        ? selectedShopKeys.map(key => renderBuyShopGroup(key, shops.get(key) || [], canBuy)).join('')
        : `<div class="toolkit-empty slim">${query ? 'No matches.' : 'Store is empty.'}</div>`}
    </div>
  </div>`;
}

function groupBuyRowsByShop(rows) {
  const groups = new Map();
  for (const row of rows) {
    const key = BUY_SHOPS[row.shop] ? row.shop : 'other';
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(row);
  }
  for (const list of groups.values()) {
    list.sort((a, b) => {
      const aa = a.state.listedAffordable && !a.state.needsInput ? 0 : 1;
      const bb = b.state.listedAffordable && !b.state.needsInput ? 0 : 1;
      if (aa !== bb) return aa - bb;
      const ac = Number(a.state.unitCost ?? Number.MAX_SAFE_INTEGER);
      const bc = Number(b.state.unitCost ?? Number.MAX_SAFE_INTEGER);
      if (ac !== bc) return ac - bc;
      return String(a.item?.label || a.state.sku).localeCompare(String(b.item?.label || b.state.sku));
    });
  }
  return groups;
}

function renderBuyShopTab(key, total, available) {
  const info = BUY_SHOPS[key] || BUY_SHOPS.other;
  const active = activeBuyShop === key;
  const count = key === 'all' ? total : `${available}/${total}`;
  return `<button class="buy-shop-tab${active ? ' active' : ''}" data-buy-shop="${escapeAttr(key)}">
    <span>${escapeHtml(info.label)}</span>
    <small>${escapeHtml(String(count))}</small>
  </button>`;
}

function renderBuyShopGroup(key, rows, canBuy) {
  const info = BUY_SHOPS[key] || BUY_SHOPS.other;
  const available = rows.filter(row => row.state.listedAffordable && !row.state.needsInput).length;
  return `<div class="buy-group">
    <div class="buy-group-head">
      <span>${escapeHtml(info.label)}</span>
      <small>${available}/${rows.length}</small>
    </div>
    <div class="buy-grid">
      ${rows.length ? rows.map(row => renderBuyItem(row.item, canBuy, row.state)).join('') : '<div class="toolkit-empty slim">Nothing here.</div>'}
    </div>
  </div>`;
}

function renderToolkitRate() {
  if (!toolkitState?.earningCoins) return '';
  const amount = Number(toolkitState.coinAmount ?? 0);
  const interval = Number(toolkitState.coinInterval ?? 0);
  if (!amount || !interval) return '';
  return `<div class="toolkit-rate">Earning ${escapeHtml(formatNumber(amount))} coins every ${escapeHtml(formatNumber(interval))} minute${interval === 1 ? '' : 's'} before karma and viewer bonuses.</div>`;
}

function getBuyShop(item) {
  const explicit = String(item?.shop || item?.category || item?.group || '').toLowerCase();
  if (BUY_SHOPS[explicit] && explicit !== 'all') return explicit;

  const kind = String(item?.kind || '').toLowerCase();
  const text = [
    item?.sku,
    item?.label,
    item?.description,
    item?.command,
    kind,
    explicit
  ].filter(Boolean).join(' ');

  if (textHasAny(text, ['medicine', 'medical', 'heal', 'revive', 'rescue', 'injury', 'disease', 'infection', 'drug'])) return 'medical';
  if (textHasAny(text, ['meal', 'food', 'meat', 'vegetable', 'fruit', 'milk', 'egg', 'pemmican', 'nutrition'])) return 'food';
  if (textHasAny(text, ['weapon', 'gun', 'rifle', 'pistol', 'revolver', 'bow', 'sword', 'knife', 'mace', 'spear', 'grenade', 'launcher'])) return 'weapons';
  if (textHasAny(text, ['apparel', 'armor', 'armour', 'helmet', 'hat', 'shirt', 'pants', 'jacket', 'parka', 'duster', 'vest', 'clothes', 'clothing'])) return 'apparel';
  if (textHasAny(text, ['building', 'buildable', 'wall', 'door', 'floor', 'bed', 'table', 'chair', 'lamp', 'battery', 'generator', 'turret', 'bench', 'workbench', 'sculpture', 'plant pot', 'shelf'])) return 'buildables';
  if (textHasAny(text, ['trait', 'passion', 'skill', 'story', 'pawn', 'colonist', 'hair', 'gender', 'appearance'])) return 'pawn';
  if (kind === 'item') return 'items';
  if (kind === 'event' || textHasAny(text, ['raid', 'event', 'incident', 'weather', 'manhunter', 'trader', 'drop', 'quest', 'threat'])) return 'events';
  return 'other';
}

function getBuyUnitCost(item) {
  const raw = item?.unitCost ?? item?.cost ?? item?.price;
  const value = Number(raw);
  return Number.isFinite(value) ? Math.max(0, Math.round(value)) : null;
}

function getBuyQuantity(item) {
  const sku = String(item?.sku || '');
  const saved = Number(buyQuantities.get(sku));
  if (Number.isFinite(saved) && saved > 0) return Math.max(1, Math.min(100, Math.round(saved)));
  return 1;
}

function getBuyQuantityInputValue(item) {
  const sku = String(item?.sku || '');
  if (buyQuantityDrafts.has(sku)) return buyQuantityDrafts.get(sku);
  return String(getBuyQuantity(item));
}

function setBuyQuantity(sku, quantity, deferRender = false) {
  if (!sku) return;
  const next = Math.max(1, Math.min(100, Math.round(Number(quantity) || 1)));
  buyQuantities.set(sku, next);
  buyQuantityDrafts.set(sku, String(next));
  if (deferRender) {
    setTimeout(renderCommandCenter, 0);
  } else {
    renderCommandCenter();
  }
}

function updateBuyQuantityDraft(sku, value) {
  if (!sku) return;
  const digits = String(value ?? '').replace(/\D/g, '').slice(0, 3);
  buyQuantityDrafts.set(sku, digits);
  const parsed = Number(digits);
  if (Number.isFinite(parsed) && parsed > 0) {
    buyQuantities.set(sku, Math.max(1, Math.min(100, Math.round(parsed))));
  }
}

function getBuyItemState(item) {
  const sku = String(item?.sku || '');
  const kind = String(item?.kind || '');
  const isItem = kind === 'item';
  const unitCost = getBuyUnitCost(item);
  const quantity = isItem ? getBuyQuantity(item) : 1;
  const totalCost = unitCost == null ? null : unitCost * quantity;
  const coins = Number(toolkitState?.coins ?? 0);
  const unlimited = !!toolkitState?.unlimitedCoins;
  const minimumPurchase = Math.max(0, Number(toolkitState?.minimumPurchase ?? 0) || 0);
  const needsInput = !!item?.needsInput;
  const argument = String(buyArgumentDrafts.get(sku) || '').trim();
  const missingArgument = needsInput && !argument;
  const hasPrice = unitCost != null;
  const meetsMinimum = !isItem || !hasPrice || !minimumPurchase || totalCost >= minimumPurchase;
  const affordable = unlimited || (hasPrice && coins >= totalCost);
  const listedAffordable = !!item?.affordable || (hasPrice && (unlimited || coins >= unitCost));
  const researchBlocked = isItem && item?.mustResearchFirst === true && item?.researched === false;
  const needsAssignedPawn = isPawnTargetedToolkitSku(sku, item) && !pawnState;
  return {
    sku,
    kind,
    isItem,
    unitCost,
    quantity,
    totalCost,
    needsInput,
    argument,
    missingArgument,
    hasPrice,
    meetsMinimum,
    affordable,
    listedAffordable,
    researchBlocked,
    needsAssignedPawn,
    syntax: String(item?.syntax || '')
  };
}

function renderBuyItem(item, canBuy, state = null) {
  const buyState = state || getBuyItemState(item);
  const sku = buyState.sku;
  const unitCost = buyState.unitCost;
  const totalCost = buyState.totalCost;
  const disabled = !canBuy || !buyState.hasPrice || !buyState.affordable || !buyState.meetsMinimum || buyState.missingArgument || buyState.researchBlocked || buyState.needsAssignedPawn;
  const blockReason = buyState.needsAssignedPawn
    ? 'Needs assigned colonist'
    : (buyState.missingArgument
    ? 'Needs argument'
    : (!buyState.hasPrice ? 'No price'
      : (!buyState.meetsMinimum ? `Min ${formatNumber(toolkitState?.minimumPurchase ?? 0)}`
        : (buyState.researchBlocked ? 'Research locked'
          : (!buyState.affordable ? 'Not enough coins' : (!canBuy ? 'Offline' : ''))))));
  const buttonLabel = disabled ? (blockReason || 'Locked') : 'Buy';
  const priceLine = buyState.isItem && buyState.quantity > 1
    ? `${formatNumber(unitCost)} × ${buyState.quantity} = ${formatNumber(totalCost)}`
    : formatNumber(totalCost ?? unitCost);
  const stuffOptions = getArray(item?.stuffOptions);
  const selectedStuff = buyStuffSelections.has(sku) ? buyStuffSelections.get(sku) : '';
  const stuffSelector = buyState.isItem && stuffOptions.length ? `<select class="buy-stuff-select" data-buy-stuff-select="${escapeAttr(sku)}" aria-label="Material for ${escapeAttr(item?.label || sku)}">
      <option value="" ${selectedStuff ? '' : 'selected'}>Random material</option>
      ${stuffOptions.map(stuff => {
        const value = String(stuff?.defName || '');
        const label = `${stuff?.label || value}${stuff?.category ? ` - ${stuff.category}` : ''}`;
        return `<option value="${escapeAttr(value)}" ${value === selectedStuff ? 'selected' : ''}>${escapeHtml(label)}</option>`;
      }).join('')}
    </select>` : '';
  const qtyControls = buyState.isItem ? `<div class="buy-quantity" aria-label="Quantity">
      <button data-buy-qty-step="-1" data-buy-qty-sku="${escapeAttr(sku)}" ${buyState.quantity <= 1 ? 'disabled' : ''}>-</button>
      <input data-buy-qty-input="${escapeAttr(sku)}" type="text" inputmode="numeric" pattern="[0-9]*" value="${escapeAttr(getBuyQuantityInputValue(item))}" aria-label="Quantity for ${escapeAttr(item?.label || sku)}">
      <button data-buy-qty-step="1" data-buy-qty-sku="${escapeAttr(sku)}" ${buyState.quantity >= 100 ? 'disabled' : ''}>+</button>
    </div>` : '';
  const argInput = buyState.needsInput ? `<input class="buy-arg-input" data-buy-arg-input="${escapeAttr(sku)}" type="text" value="${escapeAttr(buyArgumentDrafts.get(sku) || '')}" placeholder="${escapeAttr(buyState.syntax || 'Argument')}" aria-label="Argument for ${escapeAttr(item?.label || sku)}">` : '';
  return `<div class="buy-item${disabled ? ' disabled' : ''}">
    <div class="buy-main">
      <strong>${escapeHtml(item?.label || sku)}</strong>
      ${item?.mustResearchFirst && item?.researched === false ? `<span class="buy-warning">${escapeHtml(item?.researchProject ? `Needs ${item.researchProject}` : 'Needs research')}</span>` : ''}
      ${buyState.needsInput && buyState.syntax ? `<span class="buy-syntax">${escapeHtml(buyState.syntax)}</span>` : ''}
      ${stuffSelector}
      ${argInput}
    </div>
    <div class="buy-meta">
      <span class="buy-price">${escapeHtml(priceLine)}<small> coins</small></span>
      ${qtyControls}
      <button data-buy-sku="${escapeAttr(sku)}" data-buy-kind="${escapeAttr(item?.kind || '')}" ${disabled ? 'disabled' : ''} title="${escapeAttr(blockReason || 'Buy')}">${escapeHtml(buttonLabel)}</button>
    </div>
  </div>`;
}

function formatPrice(value) {
  if (value == null || !Number.isFinite(Number(value))) return 'unknown';
  return `${formatNumber(value)} coins`;
}

function formatNumber(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return '0';
  return Math.round(num).toLocaleString();
}

function formatKarma(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return '0%';
  return `${Math.round(num)}%`;
}

function getWorkPriorityEntries(state) {
  const ordered = getArray(state?.workPriorities)
    .map(item => ({
      defName: item?.defName || item?.name || item?.def || '',
      label: item?.label || item?.defName || item?.name || item?.def || '',
      priority: Number(item?.priority ?? -1),
      disabled: !!item?.disabled || Number(item?.priority ?? -1) < 0,
      relevantSkills: getArray(item?.relevantSkills)
    }))
    .filter(item => item.defName);
  if (ordered.length) return ordered;
  return Object.entries(state?.work || {}).map(([defName, value]) => ({
    defName,
    label: formatDefLabel(defName),
    priority: Number(value),
    disabled: Number(value) < 0,
    relevantSkills: []
  })).sort((a, b) => {
    const ao = RIMWORLD_WORK_ORDER_INDEX.has(a.defName) ? RIMWORLD_WORK_ORDER_INDEX.get(a.defName) : 999;
    const bo = RIMWORLD_WORK_ORDER_INDEX.has(b.defName) ? RIMWORLD_WORK_ORDER_INDEX.get(b.defName) : 999;
    if (ao !== bo) return ao - bo;
    return a.label.localeCompare(b.label);
  });
}

function renderWorkControls(state) {
  const entries = getWorkPriorityEntries(state);
  if (!entries.length) return '<div class="command-empty">No work priorities are available for this pawn.</div>';
  const blocked = getActionBlockedReason('set_work');
  return `<div class="native-command-block">
    ${renderWorkLoadoutControls()}
    <div class="work-priority-board">
      <div class="work-row work-priority-head">
        <span class="work-name">Work type</span>
        <span class="work-skill-head">Pawn skill</span>
        <span class="priority-head-cell">1</span>
        <span class="priority-head-cell">2</span>
        <span class="priority-head-cell">3</span>
        <span class="priority-head-cell">4</span>
        <span class="priority-head-cell">Off</span>
      </div>
      ${entries.map(entry => renderWorkRow(entry, blocked)).join('')}
    </div>
  </div>`;
}

function renderWorkRow(entry, blocked) {
  const defName = entry.defName;
  const value = Number(entry.priority);
  const disabled = entry.disabled || value < 0;
  const reason = disabled ? 'Pawn cannot do this work' : blocked;
  return `<div class="work-row">
    <span class="work-name${disabled ? ' disabled' : ''}">${escapeHtml(entry.label || formatDefLabel(defName))}</span>
    ${renderWorkSkillSummary(entry.relevantSkills)}
    ${[1, 2, 3, 4, 0].map(priority => {
      const active = value === priority;
      const label = priority === 0 ? 'Off' : String(priority);
      return `<button class="priority-btn${active ? ' active' : ''}" data-work-def="${escapeAttr(defName)}" data-priority="${priority}" ${reason ? 'disabled' : ''} title="${escapeAttr(reason)}">${label}</button>`;
    }).join('')}
  </div>`;
}

function renderWorkSkillSummary(skills) {
  const relevant = getArray(skills).filter(skill => !skill?.disabled);
  if (!relevant.length) return '<span class="work-skill-summary muted">-</span>';
  return `<span class="work-skill-summary">${relevant.map(skill => {
    const level = Math.max(0, Math.min(20, Number(skill?.level ?? 0)));
    const passion = Number(skill?.passion || 0);
    const passionText = passion >= 2 ? '++' : passion === 1 ? '+' : '';
    const title = `${skill?.label || skill?.name || '?'} ${level}${passionText}`;
    return `<span class="work-skill-pill ${skillTierClass(level)}" title="${escapeAttr(title)}"><span>${escapeHtml(skill?.label || skill?.name || '?')}</span><strong>${level}${passionText}</strong></span>`;
  }).join('')}</span>`;
}

function renderScheduleControls(state) {
  const schedule = getArray(state?.schedule);
  if (schedule.length < 24) return '';
  const blocked = getActionBlockedReason('set_schedule');
  const assignments = getScheduleAssignments(state);
  selectedScheduleHour = Math.max(0, Math.min(23, Number(selectedScheduleHour) || 0));
  const selectedAssignment = schedule[selectedScheduleHour] || 'Anything';
  const selectedLabel = assignmentLabel(assignments, selectedAssignment);
  return `<div class="native-command-block">
    <div class="command-page-title">Daily schedule</div>
    ${renderWorkLoadoutControls()}
    <div class="schedule-strip">
      ${schedule.slice(0, 24).map((assignment, hour) => {
        const label = assignmentLabel(assignments, assignment);
        return `<button class="schedule-hour ${scheduleAssignmentClass(assignment)}${hour === selectedScheduleHour ? ' active' : ''}" data-schedule-select-hour="${hour}" title="${escapeAttr(`${hour}:00 ${label}`)}" aria-label="${escapeAttr(`${hour}:00 ${label}`)}">
          <span class="schedule-hour-label">${hour}</span>
          <span class="schedule-swatch" aria-hidden="true"></span>
        </button>`;
      }).join('')}
    </div>
    <div class="schedule-picker">
      <div class="schedule-selected"><span>${selectedScheduleHour}:00</span><strong>${escapeHtml(selectedLabel)}</strong></div>
      <div class="schedule-assignment-buttons">
        ${assignments.map(a => `<button class="segment-btn${a.defName === selectedAssignment ? ' active' : ''}" data-schedule-hour="${selectedScheduleHour}" data-next-assignment="${escapeAttr(a.defName)}" ${blocked ? 'disabled' : ''}>${escapeHtml(a.label || a.defName)}</button>`).join('')}
      </div>
    </div>
    <div class="schedule-blocks">
      <button class="mini-command" data-schedule-block="sleep-night" ${blocked ? 'disabled' : ''}>Sleep night</button>
      <button class="mini-command" data-schedule-block="work-day" ${blocked ? 'disabled' : ''}>Work day</button>
      <button class="mini-command" data-schedule-block="joy-evening" ${blocked ? 'disabled' : ''}>Joy evening</button>
      <button class="mini-command" data-schedule-block="anything-all" ${blocked ? 'disabled' : ''}>Anything all</button>
    </div>
  </div>`;
}

function renderWorkLoadoutControls() {
  const saved = getSavedWorkLoadout();
  const blocked = getActionBlockedReason('set_work') || getActionBlockedReason('set_schedule');
  const canSave = !!pawnState && getWorkPriorityEntries(pawnState).length > 0 && getArray(pawnState.schedule).length >= 24;
  const savedLabel = saved?.savedAt ? new Date(saved.savedAt).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : 'None saved';
  return `<div class="work-loadout-bar">
    <span>Loadout</span>
    <strong>${escapeHtml(savedLabel)}</strong>
    <button class="mini-command" data-work-loadout="save" ${canSave ? '' : 'disabled'}>Save current</button>
    <button class="mini-command" data-work-loadout="apply" ${saved && !blocked ? '' : 'disabled'} title="${escapeAttr(blocked || '')}">Apply saved</button>
  </div>`;
}

function assignmentLabel(assignments, defName) {
  const found = assignments.find(item => item.defName === defName);
  return found?.label || defName || 'Anything';
}

function renderPolicyPage(state) {
  return `${renderHostileControls(state?.hostileResponse)}${renderPolicyControls(state || {})}`;
}

function renderPolicyControls(state) {
  const controls = [
    { action: 'set_outfit', label: 'Outfit', field: 'policy', current: state.outfitPolicy, options: state.outfitPolicyOptions },
    { action: 'set_drug_policy', label: 'Drug policy', field: 'policy', current: state.drugPolicy, options: state.drugPolicyOptions },
    { action: 'set_food_policy', label: 'Food policy', field: 'policy', current: state.foodPolicy, options: state.foodPolicyOptions },
    { action: 'set_area', label: 'Area', field: 'area', current: state.areaRestriction || 'Unrestricted', options: state.areaOptions }
  ];

  const html = controls.map(control => renderPolicySelect(control)).filter(Boolean).join('');
  if (!html) return '';
  return `<div class="native-command-block">
    <div class="command-page-title">Restrictions</div>
    <div class="command-page-note">These use the same game-side policy controls the host has enabled for your pawn.</div>
    <div class="policy-grid">${html}</div>
  </div>`;
}

function renderPolicySelect(control) {
  const options = uniqueStrings([control.current, ...getArray(control.options)]);
  if (!options.length) return '';
  const blocked = getActionBlockedReason(control.action);
  return `<label class="policy-control">
    <span>${escapeHtml(control.label)}</span>
    <select data-policy-action="${escapeAttr(control.action)}" data-policy-field="${escapeAttr(control.field)}" ${blocked ? 'disabled' : ''} title="${escapeAttr(blocked)}">
      ${options.map(opt => `<option value="${escapeAttr(opt)}" ${opt === control.current ? 'selected' : ''}>${escapeHtml(opt)}</option>`).join('')}
    </select>
  </label>`;
}

function renderHostileControls(mode) {
  const blocked = getActionBlockedReason('set_hostile_response');
  const current = Number.isFinite(Number(mode)) ? Number(mode) : -1;
  return `<div class="native-command-block compact">
    <div class="native-command-title">When threatened</div>
    <div class="segmented-control">
      ${HOSTILE_RESPONSE_OPTIONS.map(opt => `<button class="segment-btn${current === opt.mode ? ' active' : ''}" data-hostile-mode="${opt.mode}" ${blocked ? 'disabled' : ''} title="${escapeAttr(blocked)}">${escapeHtml(opt.label)}</button>`).join('')}
    </div>
  </div>`;
}

function renderAliasHelp() {
  return `<div class="command-page">
    <div class="command-page-title">Help</div>
    <div class="command-page-note">Pawn control and Toolkit pawn purchases (heal, traits, skills) in Overlord target your assigned colonist. Twitch chat !mypawn* info commands still use Toolkit's own binding and can differ — prefer Overlord tabs.</div>
    <div class="alias-grid">
      ${COMMAND_ALIAS_GROUPS.map(group => `<div class="alias-row">
        <strong>${escapeHtml(group.label)}</strong>
        <span>${group.commands.map(cmd => `<code>${escapeHtml(cmd)}</code>`).join('')}</span>
      </div>`).join('')}
    </div>
  </div>`;
}

function isPawnTargetedToolkitSku(sku, item = null) {
  const key = String(sku || '').trim().toLowerCase();
  if (!key) return false;
  if (PAWN_TARGETED_SKUS.has(key)) return true;
  const category = String(item?.category || '').toLowerCase();
  return category === 'pawn';
}

function sendWorkPriority(defName, priority) {
  const blocked = getActionBlockedReason('set_work');
  if (blocked) {
    appendLog(blocked);
    return;
  }
  markCommandSent('set_work', `Set ${formatDefLabel(defName)} to ${priority === '0' ? 'off' : priority}`);
  send({ type: 'command', action: 'set_work', workDef: defName, priority: Number(priority) });
}

function sendScheduleAssignment(hour, assignment) {
  const blocked = getActionBlockedReason('set_schedule');
  if (blocked) {
    appendLog(blocked);
    return;
  }
  markCommandSent('set_schedule', `Set ${hour}:00 to ${formatDefLabel(assignment)}`);
  send({ type: 'command', action: 'set_schedule', hour: Number(hour), assignment });
}

function findScheduleAssignment(defName) {
  const assignments = getScheduleAssignments(pawnState);
  const wanted = String(defName || '').toLowerCase();
  return assignments.find(item =>
    String(item.defName || '').toLowerCase() === wanted ||
    String(item.label || '').toLowerCase() === wanted
  )?.defName || defName;
}

function workLoadoutUserKey() {
  return String(identity?.login || identity?.displayName || identity?.username || 'viewer').trim().toLowerCase() || 'viewer';
}

function readWorkLoadouts() {
  try {
    const parsed = JSON.parse(localStorage.getItem(WORK_LOADOUTS_KEY) || '{}');
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function getSavedWorkLoadout() {
  return readWorkLoadouts()[workLoadoutUserKey()] || null;
}

function saveCurrentWorkLoadout() {
  if (!pawnState) return;
  const work = getWorkPriorityEntries(pawnState)
    .filter(entry => !entry.disabled && Number(entry.priority) >= 0)
    .map(entry => ({ defName: entry.defName, priority: Number(entry.priority) }));
  const schedule = getArray(pawnState.schedule).slice(0, 24).map(assignment => String(assignment || 'Anything'));
  if (!work.length && schedule.length < 24) {
    appendLog('No work loadout data is available yet');
    return;
  }

  const loadouts = readWorkLoadouts();
  loadouts[workLoadoutUserKey()] = { savedAt: Date.now(), work, schedule };
  localStorage.setItem(WORK_LOADOUTS_KEY, JSON.stringify(loadouts));
  appendLog('Work loadout saved');
  renderCommandCenter();
}

function applySavedWorkLoadout() {
  const blocked = getActionBlockedReason('set_work') || getActionBlockedReason('set_schedule');
  if (blocked) {
    appendLog(blocked);
    return;
  }
  const saved = getSavedWorkLoadout();
  if (!saved) {
    appendLog('No saved work loadout');
    return;
  }

  const work = getArray(saved.work).filter(entry => entry?.defName && Number.isFinite(Number(entry.priority)));
  const schedule = getArray(saved.schedule).slice(0, 24);
  markCommandSent('set_work', 'Applying saved work loadout');
  work.forEach(entry => {
    send({ type: 'command', action: 'set_work', workDef: entry.defName, priority: Number(entry.priority) });
  });
  schedule.forEach((assignment, hour) => {
    send({ type: 'command', action: 'set_schedule', hour, assignment: String(assignment || 'Anything') });
  });
}

function sendScheduleBlock(block) {
  const blocked = getActionBlockedReason('set_schedule');
  if (blocked) {
    appendLog(blocked);
    return;
  }

  const blocks = {
    'sleep-night': { label: 'Sleep night', assignment: findScheduleAssignment('Sleep'), hours: [22, 23, 0, 1, 2, 3, 4, 5] },
    'work-day': { label: 'Work day', assignment: findScheduleAssignment('Work'), hours: [8, 9, 10, 11, 12, 13, 14, 15, 16, 17] },
    'joy-evening': { label: 'Joy evening', assignment: findScheduleAssignment('Joy'), hours: [18, 19, 20, 21] },
    'anything-all': { label: 'Anything all', assignment: findScheduleAssignment('Anything'), hours: Array.from({ length: 24 }, (_, hour) => hour) }
  };
  const selected = blocks[block];
  if (!selected?.assignment) return;

  markCommandSent('set_schedule', selected.label);
  selected.hours.forEach(hour => {
    send({ type: 'command', action: 'set_schedule', hour, assignment: selected.assignment });
  });
}

function stepAppearanceHair(direction) {
  const options = getArray(pawnState?.appearance?.hairOptions);
  if (!options.length) return;
  const values = options.map(hair => String(hair?.defName || hair || '')).filter(Boolean);
  if (!values.length) return;
  const current = appearanceDraftHairDef || pawnState?.appearance?.hairDef || values[0];
  const index = Math.max(0, values.indexOf(current));
  const nextIndex = (index + Number(direction || 0) + values.length) % values.length;
  appearanceDraftHairDef = values[nextIndex];
  appearancePreviewData = '';
  appearancePreviewLabel = '';
  renderCommandCenter();
}

function setAppearanceGender(gender) {
  if (!gender) return;
  appearanceDraftGender = gender;
  appearancePreviewData = '';
  appearancePreviewLabel = '';
  renderCommandCenter();
}

function sendPolicyCommand(action, field, value) {
  const blocked = getActionBlockedReason(action);
  if (blocked) {
    appendLog(blocked);
    return;
  }
  markCommandSent(action, `Set ${formatDefLabel(field)} to ${value}`);
  send({ type: 'command', action, [field]: value });
}

function sendHostileResponse(mode) {
  const blocked = getActionBlockedReason('set_hostile_response');
  if (blocked) {
    appendLog(blocked);
    return;
  }
  const label = HOSTILE_RESPONSE_OPTIONS.find(opt => opt.mode === Number(mode))?.label || mode;
  markCommandSent('set_hostile_response', `Hostile response: ${label}`);
  send({ type: 'command', action: 'set_hostile_response', mode: Number(mode) });
}

function resyncViewerCommands() {
  appendLog('Requesting fresh pawn state');
  updateDrawerPreview('Requesting fresh pawn state', 'Activity');
  requestFreshViewerSnapshot();
}

function requestRespawnFromCommands() {
  if (respawnPortalId == null) {
    appendLog('Respawn is not available');
    return;
  }
  markCommandSent('respawn', 'Respawn requested');
  send({ type: 'command', action: 'respawn', portalId: respawnPortalId });
}

function sendToolkitPurchase(sku, kind = '', quantity = null, argument = '') {
  if (!sku) return;
  if (!toolkitState?.available) {
    appendLog('Twitch Toolkit is not loaded');
    lastBuyFeedback = { ok: false, message: 'Twitch Toolkit is not loaded' };
    renderCommandCenter();
    return;
  }
  if (!toolkitState?.chatConnected) {
    appendLog('Twitch Toolkit is not connected');
    lastBuyFeedback = { ok: false, message: 'Twitch Toolkit chat is not connected on the host' };
    renderCommandCenter();
    return;
  }
  const item = getArray(toolkitState.entries).find(entry => String(entry?.sku || '') === sku);
  if (isPawnTargetedToolkitSku(sku, item || { kind }) && !pawnState) {
    lastBuyFeedback = {
      ok: false,
      message: 'Assign a colonist before buying pawn Toolkit items (heal, traits, skills)'
    };
    renderCommandCenter();
    return;
  }
  const buyState = getBuyItemState(item || { sku, kind });
  const qty = buyState.isItem ? Math.max(1, Math.min(100, Number(quantity ?? buyState.quantity) || 1)) : 1;
  const selectedStuff = buyState.isItem ? (buyStuffSelections.get(sku) || '') : '';
  const detail = String(argument || buyState.argument || selectedStuff || '').trim();
  if (buyState.needsInput && !detail) {
    lastBuyFeedback = {
      ok: false,
      message: `${item?.label || sku} needs an argument${buyState.syntax ? ` (${buyState.syntax})` : ''}`
    };
    renderCommandCenter();
    return;
  }
  markCommandSent('toolkit_purchase', detail ? `Buying ${sku}: ${detail}` : (qty > 1 ? `Buying ${qty} ${sku}` : `Buying ${sku}`));
  lastBuyFeedback = {
    ok: true,
    message: detail ? `Buying ${item?.label || sku}: ${detail}…` : `Buying ${item?.label || sku}…`
  };
  const message = { type: 'command', action: 'toolkit_purchase', purchase: sku, purchaseKind: kind, quantity: qty };
  if (detail) message.argument = detail;
  send(message);
  if (activeCommandMenu === 'buy' || activeCommandMenu === 'story') renderCommandCenter();
}

function sendFreeAppearanceChange() {
  const blocked = getActionBlockedReason('set_appearance');
  if (blocked) {
    appendLog(blocked);
    return;
  }

  const hairDef = appearanceDraftHairDef || pawnState?.appearance?.hairDef || '';
  const gender = appearanceDraftGender || pawnState?.appearance?.gender || '';
  if (!hairDef && !gender) return;
  markCommandSent('set_appearance', 'Appearance change sent');
  send({ type: 'command', action: 'set_appearance', hairDef, gender });
}

function requestAppearancePreview() {
  const blocked = getActionBlockedReason('set_appearance');
  if (blocked) {
    appendLog(blocked);
    return;
  }

  const hairDef = appearanceDraftHairDef || pawnState?.appearance?.hairDef || '';
  const gender = appearanceDraftGender || pawnState?.appearance?.gender || '';
  if (!hairDef && !gender) return;
  markCommandSent('preview_appearance', 'Preview requested');
  send({ type: 'command', action: 'preview_appearance', hairDef, gender });
}

function handleCommandCenterClick(event) {
  const section = event.target.closest('[data-command-section]');
  if (section) {
    activeCommandMenu = section.dataset.commandSection || 'quick';
    renderCommandCenter();
    return;
  }

  const open = event.target.closest('[data-command-open]');
  if (open) {
    setActiveTab(open.dataset.commandOpen, { expand: true });
    closeCommandWindow();
    return;
  }

  const action = event.target.closest('[data-command-action]');
  if (action) {
    if (action.dataset.commandAction === 'resync' || action.dataset.commandAction === 'quick-resync') resyncViewerCommands();
    if (action.dataset.commandAction === 'respawn') requestRespawnFromCommands();
    if (action.dataset.commandAction === 'toolkit-refresh') requestToolkitState(true);
    if (action.dataset.commandAction === 'preview-appearance') requestAppearancePreview();
    if (action.dataset.commandAction === 'set-appearance') sendFreeAppearanceChange();
    if (action.dataset.commandAction === 'quick-draft') {
      const act = pawnState?.drafted ? 'undraft' : 'draft';
      markCommandSent(act, act === 'draft' ? 'Draft sent' : 'Undraft sent');
      send({ type: 'command', action: act });
      renderCommandCenter();
    }
    return;
  }

  const hairStep = event.target.closest('[data-appearance-hair-step]');
  if (hairStep) {
    stepAppearanceHair(Number(hairStep.dataset.appearanceHairStep || 0));
    return;
  }

  const gender = event.target.closest('[data-appearance-gender]');
  if (gender) {
    setAppearanceGender(gender.dataset.appearanceGender || '');
    return;
  }

  const buyShop = event.target.closest('[data-buy-shop]');
  if (buyShop) {
    activeBuyShop = buyShop.dataset.buyShop || 'all';
    renderCommandCenter();
    return;
  }

  const buy = event.target.closest('[data-buy-sku]');
  if (buy) {
    sendToolkitPurchase(buy.dataset.buySku, buy.dataset.buyKind);
    return;
  }

  const storyPurchase = event.target.closest('[data-story-purchase-sku]');
  if (storyPurchase) {
    const key = storyPurchase.dataset.storyPurchaseKey || '';
    const argument = storyPurchaseSelections.get(key) || storyPurchase.dataset.storyPurchaseArgument || '';
    sendToolkitPurchase(storyPurchase.dataset.storyPurchaseSku, storyPurchase.dataset.storyPurchaseKind, null, argument);
    return;
  }

  const buyQty = event.target.closest('[data-buy-qty-sku][data-buy-qty-step]');
  if (buyQty) {
    const sku = buyQty.dataset.buyQtySku || '';
    const item = getArray(toolkitState?.entries).find(entry => String(entry?.sku || '') === sku);
    setBuyQuantity(sku, getBuyQuantity(item || { sku, kind: 'item' }) + Number(buyQty.dataset.buyQtyStep || 0));
    return;
  }

  const loadout = event.target.closest('[data-work-loadout]');
  if (loadout) {
    if (loadout.dataset.workLoadout === 'save') saveCurrentWorkLoadout();
    if (loadout.dataset.workLoadout === 'apply') applySavedWorkLoadout();
    return;
  }

  const priority = event.target.closest('[data-work-def][data-priority]');
  if (priority) {
    sendWorkPriority(priority.dataset.workDef, priority.dataset.priority);
    return;
  }

  const schedule = event.target.closest('[data-schedule-hour][data-next-assignment]');
  if (schedule) {
    sendScheduleAssignment(schedule.dataset.scheduleHour, schedule.dataset.nextAssignment);
    return;
  }

  const scheduleSelect = event.target.closest('[data-schedule-select-hour]');
  if (scheduleSelect) {
    selectedScheduleHour = Math.max(0, Math.min(23, Number(scheduleSelect.dataset.scheduleSelectHour || 0)));
    renderCommandCenter();
    return;
  }

  const scheduleBlock = event.target.closest('[data-schedule-block]');
  if (scheduleBlock) {
    sendScheduleBlock(scheduleBlock.dataset.scheduleBlock);
    return;
  }

  const hostile = event.target.closest('[data-hostile-mode]');
  if (hostile) {
    sendHostileResponse(hostile.dataset.hostileMode);
  }
}

function handleCommandCenterChange(event) {
  markCommandInteraction(2500);
  const input = event.target.closest('[data-buy-qty-input]');
  if (input) {
    setBuyQuantity(input.dataset.buyQtyInput || '', input.value, true);
    return;
  }

  const stuffSelect = event.target.closest('[data-buy-stuff-select]');
  if (stuffSelect) {
    buyStuffSelections.set(stuffSelect.dataset.buyStuffSelect || '', stuffSelect.value);
    return;
  }

  const storySelect = event.target.closest('[data-story-purchase-select]');
  if (storySelect) {
    storyPurchaseSelections.set(storySelect.dataset.storyPurchaseSelect || '', storySelect.value);
    const row = storySelect.closest('.story-purchase-row');
    const button = row?.querySelector('[data-story-purchase-sku]');
    if (button) button.dataset.storyPurchaseArgument = storySelect.value;
    return;
  }

  const select = event.target.closest('[data-policy-action][data-policy-field]');
  if (!select) return;
  sendPolicyCommand(select.dataset.policyAction, select.dataset.policyField, select.value);
}

function handleCommandCenterInput(event) {
  markCommandInteraction(2500);
  const search = event.target.closest('[data-buy-search]');
  if (search) {
    buySearchQuery = String(search.value || '');
    renderCommandCenter();
    return;
  }

  const argInput = event.target.closest('[data-buy-arg-input]');
  if (argInput) {
    const sku = argInput.dataset.buyArgInput || '';
    if (sku) buyArgumentDrafts.set(sku, String(argInput.value || ''));
    renderCommandCenter();
    return;
  }

  const input = event.target.closest('[data-buy-qty-input]');
  if (!input) return;
  const sku = input.dataset.buyQtyInput || '';
  const before = input.value;
  updateBuyQuantityDraft(sku, before);
  const after = buyQuantityDrafts.get(sku) || '';
  if (after !== before) {
    input.value = after;
  }
}

function handleCommandCenterKeydown(event) {
  markCommandInteraction(2500);
  const argInput = event.target.closest('[data-buy-arg-input]');
  if (argInput && event.key === 'Enter') {
    event.preventDefault();
    const sku = argInput.dataset.buyArgInput || '';
    if (sku) buyArgumentDrafts.set(sku, String(argInput.value || ''));
    const item = getArray(toolkitState?.entries).find(entry => String(entry?.sku || '') === sku);
    sendToolkitPurchase(sku, item?.kind || '', null, argInput.value);
    return;
  }
  const input = event.target.closest('[data-buy-qty-input]');
  if (!input || event.key !== 'Enter') return;
  event.preventDefault();
  setBuyQuantity(input.dataset.buyQtyInput || '', input.value, true);
}

commandMenuOpeners.forEach(button => {
  button.addEventListener('click', () => openCommandWindow(button.dataset.commandMenuOpen || 'quick'));
});
btnCommandClose?.addEventListener('click', closeCommandWindow);
commandWindow?.addEventListener('click', event => {
  markCommandInteraction();
  if (event.target === commandWindow) {
    closeCommandWindow();
    return;
  }
  handleCommandCenterClick(event);
});
commandWindow?.addEventListener('change', handleCommandCenterChange);
commandWindow?.addEventListener('input', handleCommandCenterInput);
commandWindow?.addEventListener('keydown', handleCommandCenterKeydown);
commandWindow?.addEventListener('pointerdown', () => markCommandInteraction());
commandWindow?.addEventListener('focusin', event => {
  const isSelect = event.target?.tagName === 'SELECT';
  markCommandInteraction(isSelect ? 8000 : 2500);
  const input = event.target.closest('[data-buy-qty-input]');
  if (input) setTimeout(() => input.select(), 0);
});
commandWindow?.addEventListener('mousedown', event => {
  if (event.target?.tagName === 'SELECT' || event.target?.closest?.('select')) {
    markCommandInteraction(8000);
  }
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !commandWindow?.classList.contains('hidden')) {
    closeCommandWindow();
  }
});

// ─── Commands ─────────────────────────────────────────────────────────────────
cmdBar.addEventListener('click', e => {
  const btn = e.target.closest('.cmd-btn');
  if (!btn) return;
  const action = btn.dataset.action;
  if (!action) return;
  dispatchCommand(action, btn);
});

function dispatchCommand(action, btn) {
  const blocked = getActionBlockedReason(action);
  if (blocked) {
    appendLog(blocked);
    return;
  }

  // With map data active, arm movement explicitly so the player gets cursor
  // feedback and the button clears after the next map click.
  if (action === 'move' && (hasTileData || liveFrameMeta)) {
    markCommandArmed('move', 'Choose a destination on the map');
    enterTargetMode('move');
    appendLog('Click the map to move your pawn');
    return;
  }

  if (action === 'attack') {
    markCommandArmed('attack', 'Choose a target on the map');
    enterTargetMode('attack');
    return;
  }

  if (action === 'move') { enterMoveMode(); return; }
  markCommandSent(action, `${action === 'draft' ? 'Draft' : action === 'undraft' ? 'Undraft' : action} sent`);
  send({ type: 'command', action });
}

function enterTargetMode(action) {
  targetMode = action;
  if (tileMap && typeof tileMap.setTargetMode === 'function') tileMap.setTargetMode(action);
  document.querySelectorAll('.cmd-btn.active').forEach(b => b.classList.remove('active'));
  document.querySelector(`.cmd-btn[data-action="${action}"]`)?.classList.add('active');
  mapCanvas.style.cursor = 'crosshair';

  const hint = $('map-hint');
  if (hint) {
    hint.textContent = action === 'attack' ? 'Click a hostile target' : 'Click map to move';
    hint.classList.remove('hidden');
  }

  if (!hasTileData && !liveFrameMeta) {
    const finish = (e) => {
      e.preventDefault();
      const point = e.changedTouches ? e.changedTouches[0] : (e.touches ? e.touches[0] : e);
      if (!point) return;
      const rect = mapCanvas.getBoundingClientRect();
      const cx = (point.clientX - rect.left) / rect.width;
      const cy = (point.clientY - rect.top) / rect.height;
      send({ type: 'command', action, targetX: cx, targetY: cy });
      exitTargetMode();
    };

    mapCanvas.addEventListener('mouseup', finish, { once: true });
    mapCanvas.addEventListener('touchend', finish, { once: true });
  }
}

function exitTargetMode() {
  const action = targetMode;
  targetMode = null;
  if (tileMap && typeof tileMap.setTargetMode === 'function') tileMap.setTargetMode(null);
  document.querySelectorAll('.cmd-btn.active').forEach(b => b.classList.remove('active'));
  mapCanvas.style.cursor = '';
  const btn = getCommandButton(action);
  if (btn?.classList.contains('cmd-armed')) {
    clearCommandFeedback(action);
    applyCommandAvailability();
  }
  const hint = $('map-hint');
  if (hint) {
    hint.textContent = 'Click map to move';
    hint.classList.add('hidden');
  }
}

// ─── Move mode ────────────────────────────────────────────────────────────────
function enterMoveMode() {
  if (pendingMove) return;
  pendingMove = true;
  markCommandArmed('move', 'Choose a destination on the map');
  document.querySelector('.cmd-btn[data-action="move"]')?.classList.add('active');
  mapCanvas.style.cursor = 'crosshair';
  const hint = $('map-hint');
  if (hint) hint.classList.remove('hidden');

  const finish = (e) => {
    e.preventDefault();
    const rect = mapCanvas.getBoundingClientRect();
    const cx = ((e.touches ? e.touches[0].clientX : e.clientX) - rect.left) / rect.width;
    const cy = ((e.touches ? e.touches[0].clientY : e.clientY) - rect.top)  / rect.height;
    markCommandSent('move', 'Move sent');
    send({ type: 'command', action: 'move', targetX: cx, targetY: cy });
    exitMoveMode();
  };

  mapCanvas.addEventListener('mouseup',  finish, { once: true });
  mapCanvas.addEventListener('touchend', finish, { once: true });
}

function exitMoveMode() {
  pendingMove = false;
  const moveBtn = getCommandButton('move');
  if (moveBtn?.classList.contains('cmd-armed')) {
    clearCommandFeedback('move');
    applyCommandAvailability();
  }
  exitTargetMode();
}

// ─── Event triggers ───────────────────────────────────────────────────────────
const EVENTS = [
  { id: 'wanderer',      label: 'Wanderer',       cost: 1 },
  { id: 'self_tame',     label: 'Self-tame',      cost: 1 },
  { id: 'drop_pod',      label: 'Refugee Pod',    cost: 1 },
  { id: 'solar_flare',   label: 'Solar Flare',    cost: 1 },
  { id: 'short_circuit', label: 'Short Circuit',  cost: 1 },
  { id: 'raid_small',    label: 'Raid',           cost: 2 },
  { id: 'manhunter',     label: 'Manhunters',     cost: 2 },
  { id: 'psychic_drone', label: 'Psychic Drone',  cost: 3 },
];

function renderEventButtons() {
  const el = $('event-buttons');
  if (!el) return;
  const blocked = getActionBlockedReason('trigger_event');
  if (blocked) {
    el.innerHTML = `<div class="events-blocked">${escapeHtml(blocked)}</div>`;
    return;
  }
  el.innerHTML = EVENTS.map(ev =>
    `<button class="evt-btn" onclick="triggerEvent('${ev.id}')">${ev.label} <span style="color:var(--text-muted)">(${ev.cost}t)</span></button>`
  ).join('');
}

renderEventButtons();

window.triggerEvent = function(id) {
  const blocked = getActionBlockedReason('trigger_event');
  if (blocked) {
    appendLog(blocked);
    return;
  }
  send({ type: 'command', action: 'trigger_event', eventId: id });
};

// ─── Vote UI ──────────────────────────────────────────────────────────────────
function handleVoteUpdate(msg) {
  const el = $('vote-area');
  if (!el) return;
  if (!msg.active) { el.innerHTML = ''; return; }

  playSound('vote');
  updateDrawerPreview(msg.question || 'Vote active', 'Events');
  const total = (msg.options || []).reduce((s, o) => s + (o.votes || 0), 0) || 1;
  el.innerHTML = `<div class="vote-box">
    <div class="vote-q">${escapeHtml(msg.question || 'Vote')}</div>
    ${(msg.options || []).map((o, i) => `<div class="vote-opt" onclick="castVote(${i})">
      <span>${escapeHtml(o.label ?? '')}</span>
      <div class="vote-bar-bg"><div class="vote-bar" style="width:${(o.votes||0)/total*100}%"></div></div>
      <span>${o.votes || 0}</span>
    </div>`).join('')}
  </div>`;
}

window.castVote = function(idx) {
  send({ type: 'command', action: 'vote', option: idx });
};

function handleGameEvent(msg) {
  if (msg.message) {
    appendLog(`⚡ ${msg.message}`);
    playSound('vote');
  }
}

// ─── Keyboard shortcuts ───────────────────────────────────────────────────────
window.addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;
  if (!screenMain.classList.contains('active')) return;
  const key = e.key.toLowerCase();
  switch (key) {
    case 'r': send({ type: 'request_state' }); break;
    case 'escape':
      exitMoveMode();
      exitTargetMode();
      $('context-menu')?.classList.add('hidden');
      break;
    case ' ':
      e.preventDefault();
      if (tileMap) tileMap.centerOnPawn();
      break;
  }
});

// ─── Sound effects ────────────────────────────────────────────────────────────
const AudioCtx = window.AudioContext || window.webkitAudioContext;
let audioCtx = null;
const lastSoundAt = new Map();
const SOUND_RULES = {
  assign: { quiet: true, cooldown: 10000 },
  damage: { quiet: true, cooldown: 6000 },
  death:  { quiet: true, cooldown: 30000 },
  chat:   { quiet: false, cooldown: 15000 },
  vote:   { quiet: false, cooldown: 12000 }
};

function playSound(type) {
  if (audioMode === 'off') return;
  const rule = SOUND_RULES[type] || { quiet: false, cooldown: 10000 };
  if (audioMode === 'quiet' && !rule.quiet) return;
  const nowMs = performance.now();
  const last = lastSoundAt.get(type) || 0;
  if (nowMs - last < rule.cooldown) return;
  lastSoundAt.set(type, nowMs);
  if (!AudioCtx) return;
  if (!audioCtx) { try { audioCtx = new AudioCtx(); } catch { return; } }
  if (audioCtx.state === 'suspended') {
    try { audioCtx.resume(); } catch (_) {}
  }
  const osc = audioCtx.createOscillator();
  const gain = audioCtx.createGain();
  osc.connect(gain);
  gain.connect(audioCtx.destination);
  gain.gain.value = 0.15;
  const now = audioCtx.currentTime;

  switch (type) {
    case 'assign':
      osc.frequency.setValueAtTime(523, now);
      osc.frequency.setValueAtTime(659, now + 0.1);
      osc.frequency.setValueAtTime(784, now + 0.2);
      gain.gain.exponentialRampToValueAtTime(0.001, now + 0.4);
      osc.start(now); osc.stop(now + 0.4);
      break;
    case 'damage':
      osc.type = 'sawtooth';
      osc.frequency.setValueAtTime(200, now);
      osc.frequency.exponentialRampToValueAtTime(80, now + 0.3);
      gain.gain.exponentialRampToValueAtTime(0.001, now + 0.3);
      osc.start(now); osc.stop(now + 0.3);
      break;
    case 'death':
      osc.type = 'sawtooth';
      osc.frequency.setValueAtTime(400, now);
      osc.frequency.exponentialRampToValueAtTime(50, now + 0.8);
      gain.gain.value = 0.2;
      gain.gain.exponentialRampToValueAtTime(0.001, now + 0.8);
      osc.start(now); osc.stop(now + 0.8);
      break;
    case 'chat':
      osc.frequency.setValueAtTime(880, now);
      gain.gain.value = 0.08;
      gain.gain.exponentialRampToValueAtTime(0.001, now + 0.1);
      osc.start(now); osc.stop(now + 0.1);
      break;
    case 'vote':
      osc.frequency.setValueAtTime(440, now);
      osc.frequency.setValueAtTime(554, now + 0.15);
      gain.gain.exponentialRampToValueAtTime(0.001, now + 0.3);
      osc.start(now); osc.stop(now + 0.3);
      break;
  }
}

// Right-click on map → context menu (only when tile map is NOT active)
function beginLiveFramePan(e) {
  if (!liveFrameMeta || hasTileData || (tileMap && tileMap.active)) return;
  if (targetMode) return;
  if (e.button != null && e.button !== 0) return;
  const center = getLiveFramePanCenter();
  if (!center || !liveFrameDrawRect) return;

  liveFramePanDrag = {
    pointerId: e.pointerId,
    startX: e.clientX,
    startY: e.clientY,
    center,
    moved: false
  };
  mapCanvas.classList.add('panning');
  mapCanvas.setPointerCapture?.(e.pointerId);
}

function updateLiveFramePan(e) {
  if (!liveFramePanDrag || liveFramePanDrag.pointerId !== e.pointerId) return;
  if (!liveFrameMeta || !liveFrameDrawRect) return;

  const dx = e.clientX - liveFramePanDrag.startX;
  const dy = e.clientY - liveFramePanDrag.startY;
  if (!liveFramePanDrag.moved && Math.hypot(dx, dy) < 8) return;
  liveFramePanDrag.moved = true;
  e.preventDefault();

  const rect = mapCanvas.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0 || liveFrameDrawRect.width <= 0 || liveFrameDrawRect.height <= 0) return;
  const canvasDx = dx / rect.width * mapCanvas.width;
  const canvasDy = dy / rect.height * mapCanvas.height;
  const cellDx = canvasDx / liveFrameDrawRect.width * liveFrameMeta.radiusX * 2;
  const cellDz = canvasDy / liveFrameDrawRect.height * liveFrameMeta.radiusZ * 2;
  const nextCenter = clampLiveFrameCenter({
    x: liveFramePanDrag.center.x - cellDx,
    z: liveFramePanDrag.center.z + cellDz
  });
  if (!nextCenter) return;
  if (followPawnMode) {
    followPawnMode = false;
    try { localStorage.setItem(FOLLOW_PAWN_KEY, '0'); } catch (_) {}
    updateFollowPawnToggle();
  }
  scheduleServerCameraZoom(true, { center: nextCenter });
}

function endLiveFramePan(e) {
  if (!liveFramePanDrag || liveFramePanDrag.pointerId !== e.pointerId) return;
  if (liveFramePanDrag.moved) {
    suppressNextLiveMapClick = true;
    setTimeout(() => { suppressNextLiveMapClick = false; }, 250);
  }
  mapCanvas.releasePointerCapture?.(e.pointerId);
  mapCanvas.classList.remove('panning');
  liveFramePanDrag = null;
}

mapCanvas.addEventListener('pointerdown', beginLiveFramePan);
mapCanvas.addEventListener('pointermove', updateLiveFramePan);
mapCanvas.addEventListener('pointerup', endLiveFramePan);
mapCanvas.addEventListener('pointercancel', endLiveFramePan);

mapCanvas.addEventListener('wheel', (e) => {
  if (!liveFrameMeta || hasTileData || (tileMap && tileMap.active)) return;
  e.preventDefault();
  const factor = e.deltaY < 0 ? 1.18 : 1 / 1.18;
  setLiveFrameZoom(liveFrameZoom * factor);
}, { passive: false });

mapCanvas.addEventListener('dblclick', () => {
  if (isTileRendererActive()) {
    saveFollowPawnMode(true);
    return;
  }
  if (!liveFrameMeta) return;
  resetLiveFrameView();
});

window.addEventListener('keydown', (e) => {
  if (e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.isContentEditable)) return;
  if (e.key !== ' ' && e.key !== 'c' && e.key !== 'C') return;
  if (isTileRendererActive()) return; // tilemap handles its own recenter
  if (!liveFrameMeta || viewerPhase !== 'assigned') return;
  e.preventDefault();
  saveFollowPawnMode(true);
});

btnFollowPawn?.addEventListener('click', () => {
  saveFollowPawnMode(!followPawnMode);
});

btnZoomOut?.addEventListener('click', () => {
  if (isTileRendererActive()) {
    if (tileMap) {
      tileMap.targetZoom = Math.max(tileMap.minZoom, tileMap.targetZoom - 1);
    }
    return;
  }
  if (!liveFrameMeta) return;
  setLiveFrameZoom(liveFrameZoom / 1.22);
});

btnZoomReset?.addEventListener('click', () => {
  if (isTileRendererActive()) {
    saveFollowPawnMode(true);
    return;
  }
  if (!liveFrameMeta) return;
  resetLiveFrameView();
});

btnZoomIn?.addEventListener('click', () => {
  if (isTileRendererActive()) {
    if (tileMap) {
      tileMap.targetZoom = Math.min(tileMap.maxZoom, tileMap.targetZoom + 1);
    }
    return;
  }
  if (!liveFrameMeta) return;
  setLiveFrameZoom(liveFrameZoom * 1.22);
});

btnBrightDown?.addEventListener('click', () => {
  saveMapBrightness(mapBrightness - MAP_BRIGHTNESS_STEP);
});

btnBrightReset?.addEventListener('click', () => {
  saveMapBrightness(MAP_BRIGHTNESS_DEFAULT);
});

btnBrightUp?.addEventListener('click', () => {
  saveMapBrightness(mapBrightness + MAP_BRIGHTNESS_STEP);
});

$('resource-readout-toggle')?.addEventListener('click', () => {
  saveResourceReadoutCollapsed(!resourceReadoutCollapsed);
  renderResourceReadout();
});

updateFollowPawnToggle();
applyMapBrightness();
renderResourceReadout();

mapCanvas.addEventListener('click', handleLiveMapClick);
mapCanvas.addEventListener('touchend', handleLiveMapClick);

mapCanvas.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  if (hasTileData) return; // tile map renderer handles its own right-click
  if (hostCapabilities && hostCapabilities.contextMenu === false) {
    appendLog('Context actions unavailable on this RimWorld build');
    return;
  }
  if (!isActionAllowed('context_menu')) {
    appendLog(getActionBlockedReason('context_menu'));
    return;
  }
  if (liveFrameMeta) {
    const cell = liveFramePointToCell(e.clientX, e.clientY);
    if (!cell) return;
    lastContextMenuPoint = { x: e.clientX, y: e.clientY };
    markCommandSent('context_menu', `Checking actions near (${cell.x}, ${cell.z})`);
    send({ type: 'command', action: 'context_menu', x: cell.x, z: cell.z });
    return;
  }
  const rect = mapCanvas.getBoundingClientRect();
  const cx = (e.clientX - rect.left) / rect.width;
  const cy = (e.clientY - rect.top) / rect.height;
  lastContextMenuPoint = { x: e.clientX, y: e.clientY };
  markCommandSent('context_menu', 'Checking actions');
  send({ type: 'command', action: 'context_menu', targetX: cx, targetY: cy });
});
