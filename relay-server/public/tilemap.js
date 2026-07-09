'use strict';

// ─── Tile Map Renderer ─────────────────────────────────────────────────────
// Renders RimWorld map data as a colored tile grid with WASD pan + scroll zoom.
// Receives: map_full (terrain grid), map_delta (entity positions)

const TILE_COLORS = {
  0:  '#1a1a1a', // unknown
  1:  '#5c4a32', // soil/dirt
  2:  '#6b6355', // gravel
  3:  '#c2a95e', // sand
  4:  '#3d4a2e', // marsh
  5:  '#4a3c28', // mud
  6:  '#b8d4e3', // ice
  7:  '#555555', // rough stone
  8:  '#7a6b55', // sandstone
  9:  '#8a8a8a', // marble
  10: '#777777', // smooth stone
  20: '#1a3a5c', // deep water
  21: '#2a5a7a', // shallow water
  22: '#0a2a4a', // ocean
  30: '#6b5535', // wood floor
  31: '#7a7a7a', // metal
  32: '#aaaaaa', // sterile
  33: '#666666', // paved
  34: '#3a2222', // carpet
  35: '#5a5a5a', // flagstone
};

const PAWN_COLORS = {
  0: '#cccccc', // neutral
  1: '#4a9eff', // player
  2: '#e04040', // hostile
  3: '#7ab648', // animal
};

const BUILDING_COLORS = {
  0: '#888888', // generic
  1: '#555555', // wall
  2: '#7a6530', // door
  3: '#5577aa', // bed
  4: '#aa8844', // workbench
  5: '#cc3333', // hostile
};

const ITEM_COLORS = {
  0: '#d6b45d', // generic
  1: '#e3e7ee', // weapon
  2: '#9eb7d5', // apparel
  3: '#7bc272', // food
  4: '#d67b7b', // medicine/drug
  5: '#c2a26d', // material
};

const WORLD_ENTITY_COLORS = {
  fire: '#f28a24',
  plant: '#4f8f38',
  construction: '#b69a56',
};

const VISUAL_IDENTITY_VERSION = 1;

class TileMapRenderer {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.width = 0;
    this.height = 0;
    this.terrain = null;    // Uint8Array
    this.fog = null;        // packed bitset
    this.fogCount = 0;
    this.chunkApplyCount = 0;
    this.terrainCanvas = null; // pre-rendered terrain
    this.pawns = [];
    this.buildings = [];
    this.items = [];
    this.worldEntities = [];
    this.entities = new Map();
    this.entityVisuals = new Map();
    this.entityInterpolationMs = 180;
    this.entitySnapDistance = 12;
    this.removedEntities = [];
    this.entityKeyframe = false;
    this.entityCount = 0;
    this.entitiesTruncated = false;
    this.itemCount = 0;
    this.itemsTruncated = false;
    this.viewerPawnId = -1;

    // Camera
    this.camX = 0;
    this.camY = 0;
    this.zoom = 4; // pixels per tile
    this.targetZoom = 4;
    this.minZoom = 2;
    this.maxZoom = 48;

    // Input state
    this.keys = {};
    this.dragging = false;
    this.dragStart = null;
    this.lastTouch = null;
    this.inputListeners = [];
    this.hasCenteredOnPawn = false;
    this.userMovedCamera = false;
    this.followPawn = true;
    this.onFollowChanged = null;
    this.entities.clear();
    this.entityVisuals.clear();
    this.removedEntities = [];
    this.entityKeyframe = false;
    this.entityCount = 0;
    this.entitiesTruncated = false;

    // Click callback
    this.onTileClick = null; // (x, z) => {}
    this.onTileRightClick = null;
    this.onTileHover = null;
    this.hoverCell = null;
    this.hoverEntity = null;
    this.targetMode = null;
    this.recentTarget = null;

    this.active = false;
    this.animFrame = null;

    this._bindEvents();
  }

  setFullMap(msg) {
    this.width = msg.width;
    this.height = msg.height;
    this.hasCenteredOnPawn = false;
    this.userMovedCamera = !this.followPawn;

    this.terrain = this._decodeBase64Bytes(msg.terrain) || new Uint8Array(0);
    this.fog = this._decodeBase64Bytes(msg.fog);
    this.fogCount = this._countFoggedCells();

    this.terrainCanvas = document.createElement('canvas');
    this.terrainCanvas.width = this.width;
    this.terrainCanvas.height = this.height;
    this.chunkApplyCount = 0;
    this._renderTerrainCanvas();

    if (!this.active) this.start();
  }

  applyChunk(msg) {
    if (!msg || !this.terrainCanvas || !this.terrain || !this.width || !this.height) return false;

    const chunkSize = Number(msg.chunkSize) || 32;
    const startX = Number.isFinite(Number(msg.x)) ? Number(msg.x) : (Number(msg.chunkX) || 0) * chunkSize;
    const startZ = Number.isFinite(Number(msg.z)) ? Number(msg.z) : (Number(msg.chunkZ) || 0) * chunkSize;
    const width = Number(msg.width) || chunkSize;
    const height = Number(msg.height) || chunkSize;
    const terrain = this._decodeBase64Bytes(msg.terrain || msg.layers?.terrain);
    const fog = this._decodeBase64Bytes(msg.fog || msg.layers?.fog);
    if (!terrain && !fog) return false;

    for (let lz = 0; lz < height; lz++) {
      for (let lx = 0; lx < width; lx++) {
        const x = startX + lx;
        const z = startZ + lz;
        if (x < 0 || z < 0 || x >= this.width || z >= this.height) continue;

        const local = lz * width + lx;
        const global = z * this.width + x;
        if (terrain && terrain[local] !== undefined) this.terrain[global] = terrain[local];
        if (fog) this._setFoggedIndex(global, this._isBitSet(fog, local));
      }
    }

    this.fogCount = this._countFoggedCells();
    this.chunkApplyCount = (this.chunkApplyCount || 0) + 1;
    this._renderTerrainCanvas();
    return true;
  }

  setDelta(msg) {
    const hasEntityPayload = Array.isArray(msg.entities) || Array.isArray(msg.removedEntities);
    if (hasEntityPayload) {
      this._applyEntityDelta(msg);
      this._syncLegacyListsFromEntities(msg);
    } else {
      this.pawns = msg.pawns || [];
      this.buildings = msg.buildings || [];
      this.items = Array.isArray(msg.items) ? msg.items : [];
      this._seedEntitiesFromLegacyLists();
    }
    this.itemCount = Number.isFinite(Number(msg.itemCount)) ? Number(msg.itemCount) : this.items.length;
    this.itemsTruncated = msg.itemsTruncated === true;
    this.entityCount = Number.isFinite(Number(msg.entityCount)) ? Number(msg.entityCount) : this.entities.size;
    this.entitiesTruncated = msg.entitiesTruncated === true;
    this.entityKeyframe = msg.entityKeyframe === true;
    if (msg.viewerPawnId != null) this.viewerPawnId = msg.viewerPawnId;
    if (this.active && !this.hasCenteredOnPawn && !this.userMovedCamera) {
      this.centerOnPawn();
    }
  }

  _applyEntityDelta(msg) {
    if (msg.entityKeyframe === true) {
      this.entities.clear();
      this.entityVisuals.clear();
    }

    this.removedEntities = Array.isArray(msg.removedEntities)
      ? msg.removedEntities.map(id => Number(id)).filter(Number.isFinite)
      : [];
    for (const id of this.removedEntities) {
      this.entities.delete(id);
      this.entityVisuals.delete(id);
    }

    if (Array.isArray(msg.entities)) {
      for (const entity of msg.entities) {
        if (!entity || typeof entity !== 'object') continue;
        const id = Number(entity.id);
        if (!Number.isFinite(id)) continue;
        const previous = this.entities.get(id);
        const next = { ...entity, id };
        this.entities.set(id, next);
        this._updateEntityVisual(id, previous, next);
      }
    }
  }

  _seedEntitiesFromLegacyLists() {
    this.entities.clear();
    this.entityVisuals.clear();
    this.worldEntities = [];
    for (const pawn of this.pawns) {
      if (!Array.isArray(pawn) || !Number.isFinite(Number(pawn[3]))) continue;
      const entity = {
        id: Number(pawn[3]),
        kind: Number(pawn[2]) === 3 ? 'animal' : 'pawn',
        x: Number(pawn[0]),
        z: Number(pawn[1]),
        faction: Number(pawn[2]) || 0,
        label: pawn[4] || ''
      };
      this.entities.set(entity.id, entity);
      this._updateEntityVisual(entity.id, null, entity);
    }
    for (const building of this.buildings) {
      if (!Array.isArray(building) || !Number.isFinite(Number(building[5]))) continue;
      this.entities.set(Number(building[5]), {
        id: Number(building[5]),
        kind: 'building',
        x: Number(building[0]),
        z: Number(building[1]),
        sizeX: Number(building[2]),
        sizeZ: Number(building[3]),
        buildingType: Number(building[4]) || 0,
        defName: building[6] || '',
        label: building[7] || '',
        rotation: Number(building[8]) || 0
      });
    }
    for (const item of this.items) {
      if (!Array.isArray(item) || !Number.isFinite(Number(item[4]))) continue;
      this.entities.set(Number(item[4]), {
        id: Number(item[4]),
        kind: 'item',
        x: Number(item[0]),
        z: Number(item[1]),
        itemKind: Number(item[2]) || 0,
        stack: Number(item[3]) || 1,
        defName: item[5] || '',
        label: item[6] || '',
        flags: Number(item[7]) || 0
      });
    }
    this.entityCount = this.entities.size;
    this.entitiesTruncated = false;
    this.entityKeyframe = true;
    this.removedEntities = [];
  }

  _updateEntityVisual(id, previous, next) {
    if (!this._isInterpolatedEntity(next)) {
      this.entityVisuals.delete(id);
      return;
    }

    const target = this._entityPosition(next);
    if (!target) {
      this.entityVisuals.delete(id);
      return;
    }

    const now = this._now();
    const existing = this.entityVisuals.get(id);
    if (!previous || !existing || !this._isInterpolatedEntity(previous)) {
      this.entityVisuals.set(id, {
        fromX: target.x,
        fromZ: target.z,
        targetX: target.x,
        targetZ: target.z,
        startedAt: now,
        duration: 0
      });
      return;
    }

    if (Math.abs(existing.targetX - target.x) < 0.001 && Math.abs(existing.targetZ - target.z) < 0.001) {
      return;
    }

    const current = this._getEntityVisualPosition(id, previous.x, previous.z, now);
    const distance = Math.hypot(target.x - current.x, target.z - current.z);
    if (!Number.isFinite(distance) || distance > this.entitySnapDistance) {
      this.entityVisuals.set(id, {
        fromX: target.x,
        fromZ: target.z,
        targetX: target.x,
        targetZ: target.z,
        startedAt: now,
        duration: 0
      });
      return;
    }

    this.entityVisuals.set(id, {
      fromX: current.x,
      fromZ: current.z,
      targetX: target.x,
      targetZ: target.z,
      startedAt: now,
      duration: this.entityInterpolationMs
    });
  }

  _isInterpolatedEntity(entity) {
    return entity && (entity.kind === 'pawn' || entity.kind === 'animal' || entity.kind === 'mech');
  }

  _entityPosition(entity) {
    const x = Number(entity?.x);
    const z = Number(entity?.z);
    return Number.isFinite(x) && Number.isFinite(z) ? { x, z } : null;
  }

  _getEntityVisualPosition(id, fallbackX, fallbackZ, now = this._now()) {
    const visual = this.entityVisuals.get(Number(id));
    const fx = Number.isFinite(Number(fallbackX)) ? Number(fallbackX) : 0;
    const fz = Number.isFinite(Number(fallbackZ)) ? Number(fallbackZ) : 0;
    if (!visual) return { x: fx, z: fz, targetX: fx, targetZ: fz, interpolating: false };

    const duration = Number(visual.duration) || 0;
    if (duration <= 0) {
      return {
        x: visual.targetX,
        z: visual.targetZ,
        targetX: visual.targetX,
        targetZ: visual.targetZ,
        interpolating: false
      };
    }

    const t = Math.max(0, Math.min(1, (now - visual.startedAt) / duration));
    const eased = t * (2 - t);
    const x = visual.fromX + (visual.targetX - visual.fromX) * eased;
    const z = visual.fromZ + (visual.targetZ - visual.fromZ) * eased;
    if (t >= 1) {
      visual.fromX = visual.targetX;
      visual.fromZ = visual.targetZ;
      visual.duration = 0;
    }
    return {
      x,
      z,
      targetX: visual.targetX,
      targetZ: visual.targetZ,
      interpolating: t < 1
    };
  }

  _now() {
    return typeof performance !== 'undefined' && performance.now ? performance.now() : Date.now();
  }

  _decodeBase64Bytes(value) {
    if (typeof value !== 'string' || !value) return null;
    try {
      const raw = atob(value);
      const bytes = new Uint8Array(raw.length);
      for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
      return bytes;
    } catch (_) {
      return null;
    }
  }

  _isFoggedIndex(index) {
    if (!this.fog || index < 0) return false;
    const byte = this.fog[index >> 3];
    if (byte === undefined) return false;
    return (byte & (1 << (index & 7))) !== 0;
  }

  _setFoggedIndex(index, fogged) {
    if (index < 0) return;
    if (!this.fog) this.fog = new Uint8Array(Math.ceil((this.width * this.height) / 8));
    const byteIndex = index >> 3;
    if (byteIndex >= this.fog.length) return;
    const mask = 1 << (index & 7);
    if (fogged) this.fog[byteIndex] |= mask;
    else this.fog[byteIndex] &= ~mask;
  }

  _isBitSet(bytes, index) {
    if (!bytes || index < 0) return false;
    const byte = bytes[index >> 3];
    if (byte === undefined) return false;
    return (byte & (1 << (index & 7))) !== 0;
  }

  _countFoggedCells() {
    if (!this.fog || !this.width || !this.height) return 0;
    let count = 0;
    const total = this.width * this.height;
    for (let i = 0; i < total; i++) {
      if (this._isFoggedIndex(i)) count++;
    }
    return count;
  }

  _renderTerrainCanvas() {
    if (!this.terrainCanvas || !this.width || !this.height) return;
    const tctx = this.terrainCanvas.getContext('2d');
    const imgData = tctx.createImageData(this.width, this.height);

    const total = this.width * this.height;
    for (let i = 0; i < total; i++) {
      const terrainByte = this.terrain && this.terrain[i] !== undefined ? this.terrain[i] : 0;
      const color = this._isFoggedIndex(i) ? '#040505' : (TILE_COLORS[terrainByte] || TILE_COLORS[0]);
      const r = parseInt(color.slice(1, 3), 16);
      const g = parseInt(color.slice(3, 5), 16);
      const b = parseInt(color.slice(5, 7), 16);
      imgData.data[i * 4] = r;
      imgData.data[i * 4 + 1] = g;
      imgData.data[i * 4 + 2] = b;
      imgData.data[i * 4 + 3] = 255;
    }

    tctx.putImageData(imgData, 0, 0);
  }

  _syncLegacyListsFromEntities(msg = {}) {
    const pawns = [];
    const buildings = [];
    const items = [];
    const worldEntities = [];
    for (const entity of this.entities.values()) {
      if (!entity) continue;
      if (entity.kind === 'pawn' || entity.kind === 'animal') {
        const faction = Number(entity.faction) || (entity.kind === 'animal' ? 3 : 0);
        const row = [Number(entity.x) || 0, Number(entity.z) || 0, faction, Number(entity.id)];
        if (entity.label) row.push(String(entity.label));
        pawns.push(row);
      } else if (entity.kind === 'building' || entity.kind === 'door') {
        buildings.push([
          Number(entity.x) || 0,
          Number(entity.z) || 0,
          Number(entity.sizeX) || 1,
          Number(entity.sizeZ) || 1,
          Number(entity.buildingType) || 0,
          Number(entity.id),
          entity.defName || '',
          entity.label || '',
          Number(entity.rotation) || 0,
          Number(entity.flags) || 0,
          Number.isFinite(Number(entity.interactionX)) ? Number(entity.interactionX) : -1,
          Number.isFinite(Number(entity.interactionZ)) ? Number(entity.interactionZ) : -1,
          entity.role || '',
          entity.open === true ? 1 : 0,
          Array.isArray(entity.owners) ? entity.owners.join(', ') : '',
          Number(entity.billCount) || 0,
          Array.isArray(entity.billLabels) ? entity.billLabels.join(', ') : '',
          entity.reservedByLabel || '',
          Number.isFinite(Number(entity.reservedById)) ? Number(entity.reservedById) : -1,
          entity.reservationJobDef || '',
          Number.isFinite(Number(entity.reservationTargetId)) ? Number(entity.reservationTargetId) : -1,
          Number.isFinite(Number(entity.reservationTargetX)) ? Number(entity.reservationTargetX) : -1,
          Number.isFinite(Number(entity.reservationTargetZ)) ? Number(entity.reservationTargetZ) : -1,
          entity.billDetailSummary || this._billDetailSummary(entity.billDetails),
          entity.activeJobSummary || this._activeJobSummary(entity.activeJob),
          this._activeJobProgressPercent(entity.activeJob)
        ]);
      } else if (entity.kind === 'item') {
        items.push([
          Number(entity.x) || 0,
          Number(entity.z) || 0,
          Number(entity.itemKind) || 0,
          Number(entity.stack) || 1,
          Number(entity.id),
          entity.defName || '',
          entity.label || '',
          Number(entity.flags) || 0,
          entity.reservedByLabel || '',
          Number.isFinite(Number(entity.reservedById)) ? Number(entity.reservedById) : -1,
          entity.reservationJobDef || '',
          Number.isFinite(Number(entity.reservationTargetId)) ? Number(entity.reservationTargetId) : -1,
          Number.isFinite(Number(entity.reservationTargetX)) ? Number(entity.reservationTargetX) : -1,
          Number.isFinite(Number(entity.reservationTargetZ)) ? Number(entity.reservationTargetZ) : -1
        ]);
      } else if (entity.kind === 'fire' || entity.kind === 'plant' || entity.kind === 'construction') {
        worldEntities.push(entity);
      }
    }

    this.pawns = pawns;
    this.buildings = buildings;
    this.items = items;
    this.worldEntities = worldEntities;

    if (Array.isArray(msg.pawns) && msg.pawns.length > 0 && pawns.length === 0) this.pawns = msg.pawns;
    if (Array.isArray(msg.buildings) && msg.buildings.length > 0 && buildings.length === 0) this.buildings = msg.buildings;
    if (Array.isArray(msg.items) && msg.items.length > 0 && items.length === 0) this.items = msg.items;
  }

  centerOnPawn(fallbackX, fallbackZ) {
    const vp = this.pawns.find(p => p[3] === this.viewerPawnId)
      || (this.viewerPawnId < 0 ? this.pawns[0] : null);
    if (vp) {
      this.camX = Number(vp[0]) || 0;
      this.camY = Number(vp[1]) || 0;
      this.hasCenteredOnPawn = true;
      this.userMovedCamera = false;
      this.followPawn = true;
      return true;
    }
    if (Number.isFinite(fallbackX) && Number.isFinite(fallbackZ)) {
      this.camX = fallbackX;
      this.camY = fallbackZ;
      this.hasCenteredOnPawn = true;
      this.userMovedCamera = false;
      this.followPawn = true;
      return true;
    }
    // Avoid staring at fogged map corner (0,0) before pawn data arrives.
    if (this.width > 0 && this.height > 0 && !this.hasCenteredOnPawn) {
      this.camX = this.width / 2;
      this.camY = this.height / 2;
    }
    return false;
  }

  setFollowPawn(enabled) {
    this.followPawn = !!enabled;
    if (this.followPawn) {
      this.userMovedCamera = false;
      this.centerOnPawn();
    } else {
      this.userMovedCamera = true;
    }
  }

  _markUserMovedCamera() {
    if (!this.userMovedCamera || this.followPawn) {
      this.userMovedCamera = true;
      if (this.followPawn) {
        this.followPawn = false;
        if (typeof this.onFollowChanged === 'function') this.onFollowChanged(false);
      }
    }
  }

  start() {
    this.active = true;
    if (this.followPawn) this.centerOnPawn();
    else if (this.width > 0 && this.height > 0 && !this.hasCenteredOnPawn) {
      this.camX = this.width / 2;
      this.camY = this.height / 2;
    }
    this._loop();
  }

  stop() {
    this.active = false;
    if (this.animFrame) cancelAnimationFrame(this.animFrame);
  }

  destroy() {
    this.stop();
    this.onTileClick = null;
    this.onTileRightClick = null;
    this.onTileHover = null;
    this.keys = {};
    this.dragging = false;
    this.dragStart = null;
    this.lastTouch = null;
    this.hoverCell = null;
    this.hoverEntity = null;
    this.targetMode = null;
    this.recentTarget = null;
    this.hasCenteredOnPawn = false;
    this.userMovedCamera = false;
    this.entities.clear();
    this.entityVisuals.clear();
    this.worldEntities = [];
    this.removedEntities = [];

    for (const listener of this.inputListeners) {
      listener.target.removeEventListener(listener.type, listener.handler, listener.options);
    }
    this.inputListeners = [];
  }

  screenToCell(clientX, clientY) {
    return this._screenToTile(clientX, clientY);
  }

  setTargetMode(action) {
    this.targetMode = action || null;
  }

  markTarget(x, z, action = 'move') {
    const cx = Number(x);
    const cz = Number(z);
    if (!Number.isFinite(cx) || !Number.isFinite(cz)) return;
    this.recentTarget = {
      x: cx,
      z: cz,
      action: action || 'move',
      expiresAt: this._now() + 1600
    };
  }

  getHoverTarget() {
    return this.hoverEntity ? this._debugEntity(this.hoverEntity) : null;
  }

  getDebugState() {
    const pawns = Array.isArray(this.pawns) ? this.pawns : [];
    const buildings = Array.isArray(this.buildings) ? this.buildings : [];
    const items = Array.isArray(this.items) ? this.items : [];
    const worldEntities = Array.isArray(this.worldEntities) ? this.worldEntities : [];
    const viewerPawn = pawns.find(p => Array.isArray(p) && p[3] === this.viewerPawnId);

    return {
      active: !!this.active,
      hasFullMap: !!this.terrainCanvas,
      width: this.width,
      height: this.height,
      hasFog: !!this.fog,
      fogCount: this.fogCount,
      chunkApplyCount: this.chunkApplyCount || 0,
      visualIdentityVersion: VISUAL_IDENTITY_VERSION,
      camX: this.camX,
      camY: this.camY,
      zoom: this.zoom,
      targetZoom: this.targetZoom,
      hasCenteredOnPawn: this.hasCenteredOnPawn,
      userMovedCamera: this.userMovedCamera,
      followPawn: this.followPawn,
      hoverCell: this.hoverCell ? { ...this.hoverCell } : null,
      hoverEntity: this.hoverEntity ? this._debugEntity(this.hoverEntity) : null,
      targetMode: this.targetMode,
      recentTarget: this.recentTarget && this.recentTarget.expiresAt > this._now()
        ? { x: this.recentTarget.x, z: this.recentTarget.z, action: this.recentTarget.action }
        : null,
      pawnCount: pawns.length,
      buildingCount: buildings.length,
      buildingSample: buildings.slice(0, 4),
      itemCount: items.length,
      itemTotal: this.itemCount,
      itemsTruncated: this.itemsTruncated,
      itemSample: items.slice(0, 3),
      worldEntityCount: worldEntities.length,
      worldEntitySample: worldEntities.slice(0, 4),
      entityCount: this.entities.size,
      entityTotal: this.entityCount,
      entityKeyframe: this.entityKeyframe,
      entitiesTruncated: this.entitiesTruncated,
      removedEntities: this.removedEntities.slice(0, 12),
      entitySample: Array.from(this.entities.values()).slice(0, 3),
      pawnVisualSample: this._getPawnVisualSample(pawns),
      interpolatingPawnCount: this._countInterpolatingPawns(pawns),
      viewerPawnId: this.viewerPawnId,
      viewerPawnPosition: viewerPawn ? { x: viewerPawn[0], z: viewerPawn[1] } : null
    };
  }

  _getPawnVisualSample(pawns) {
    const now = this._now();
    return pawns.slice(0, 4).map(pawn => {
      if (!Array.isArray(pawn)) return null;
      const id = Number(pawn[3]);
      const visual = this._getEntityVisualPosition(id, pawn[0], pawn[1], now);
      return {
        id,
        x: Number(visual.x.toFixed(3)),
        z: Number(visual.z.toFixed(3)),
        targetX: Number(visual.targetX.toFixed(3)),
        targetZ: Number(visual.targetZ.toFixed(3)),
        interpolating: visual.interpolating
      };
    }).filter(Boolean);
  }

  _debugEntity(hit) {
    if (!hit) return null;
    return {
      id: Number.isFinite(Number(hit.id)) ? Number(hit.id) : null,
      kind: hit.kind || '',
      label: hit.label || '',
      x: Number.isFinite(Number(hit.x)) ? Number(hit.x) : null,
      z: Number.isFinite(Number(hit.z)) ? Number(hit.z) : null
    };
  }

  _billDetailSummary(details) {
    if (!Array.isArray(details) || details.length === 0) return '';
    return details.slice(0, 2).map(detail => {
      if (!detail || typeof detail !== 'object') return '';
      const label = detail.label || '';
      const repeat = detail.repeatInfo || '';
      const state = detail.suspended ? 'suspended' : detail.paused ? 'paused' : detail.shouldDoNow ? 'active' : 'waiting';
      return `${label} ${repeat} ${state}`.trim();
    }).filter(Boolean).join(' | ');
  }

  _activeJobSummary(activeJob) {
    if (!activeJob || typeof activeJob !== 'object' || !activeJob.active) return '';
    const worker = activeJob.workerLabel || '';
    const bill = activeJob.billLabel || activeJob.recipeDef || '';
    const progress = this._activeJobProgressPercent(activeJob);
    return [worker, bill, progress >= 0 ? `${progress}%` : ''].filter(Boolean).join(' ');
  }

  _activeJobProgressPercent(activeJob) {
    if (!activeJob || typeof activeJob !== 'object') return -1;
    const direct = Number(activeJob.progressPercent);
    if (Number.isFinite(direct)) return Math.max(0, Math.min(100, Math.round(direct)));
    const progress = Number(activeJob.progress);
    if (!Number.isFinite(progress)) return -1;
    return Math.max(0, Math.min(100, Math.round(progress * 100)));
  }

  _countInterpolatingPawns(pawns) {
    const now = this._now();
    let count = 0;
    for (const pawn of pawns) {
      if (!Array.isArray(pawn)) continue;
      const visual = this._getEntityVisualPosition(pawn[3], pawn[0], pawn[1], now);
      if (visual.interpolating) count++;
    }
    return count;
  }

  _loop() {
    if (!this.active) return;
    this._handleKeys();
    this._render();
    this.animFrame = requestAnimationFrame(() => this._loop());
  }

  _handleKeys() {
    const speed = 0.5 / (this.zoom / 8);
    const panKeyDown = this.keys['w'] || this.keys['arrowup'] ||
      this.keys['s'] || this.keys['arrowdown'] ||
      this.keys['a'] || this.keys['arrowleft'] ||
      this.keys['d'] || this.keys['arrowright'];
    if (panKeyDown) this._markUserMovedCamera();
    if (this.keys['w'] || this.keys['arrowup'])    this.camY -= speed;
    if (this.keys['s'] || this.keys['arrowdown'])  this.camY += speed;
    if (this.keys['a'] || this.keys['arrowleft'])  this.camX -= speed;
    if (this.keys['d'] || this.keys['arrowright']) this.camX += speed;

    // Smooth zoom
    this.zoom += (this.targetZoom - this.zoom) * 0.15;

    // Auto-follow viewer pawn when follow mode is on
    if (this.followPawn && !this.userMovedCamera && this.hasCenteredOnPawn && this.viewerPawnId >= 0) {
      const pos = this._viewerPawnCell(this._now());
      if (pos) {
        const tx = pos.x + 0.5;
        const tz = pos.z + 0.5;
        // Soft lerp — keeps camera centred without snapping on each server tick
        this.camX += (tx - this.camX) * 0.08;
        this.camY += (tz - this.camY) * 0.08;
      }
    }
  }

  _render() {
    const c = this.canvas;
    const ctx = this.ctx;
    const cw = c.offsetWidth;
    const ch = c.offsetHeight;
    if (c.width !== cw || c.height !== ch) { c.width = cw; c.height = ch; }

    ctx.fillStyle = '#0a0a0a';
    ctx.fillRect(0, 0, cw, ch);

    if (!this.terrainCanvas) return;

    const z = this.zoom;
    const halfW = cw / 2 / z;
    const halfH = ch / 2 / z;

    ctx.save();
    ctx.translate(cw / 2, ch / 2);
    ctx.scale(z, z);
    ctx.translate(-this.camX, -this.camY);

    // Terrain
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(this.terrainCanvas, 0, 0);

    // Buildings
    for (const b of this.buildings) {
      const [bx, bz, sx, sz, btype] = b;
      const flags = Number(b[9]) || 0;
      const interactionX = Number.isFinite(Number(b[10])) ? Number(b[10]) : -1;
      const interactionZ = Number.isFinite(Number(b[11])) ? Number(b[11]) : -1;
      const role = b[12] || '';
      const open = Number(b[13]) === 1;
      const ownerText = b[14] || '';
      const billCount = Number(b[15]) || 0;
      const billLabelText = b[16] || '';
      const reservedByLabel = b[17] || '';
      const reservationJobDef = b[19] || '';
      const billDetailText = b[23] || '';
      const activeJobText = b[24] || '';
      const activeJobProgress = Number(b[25]);
      ctx.fillStyle = BUILDING_COLORS[btype] || BUILDING_COLORS[0];
      ctx.globalAlpha = 0.7;
      ctx.fillRect(bx, bz, sx, sz);
      ctx.globalAlpha = 1;

      const reserved = (Number(flags) & 2) === 2;
      const forbidden = (Number(flags) & 1) === 1;
      if (reserved || forbidden) {
        ctx.strokeStyle = forbidden ? '#b84a4a' : '#d0a448';
        ctx.lineWidth = Math.max(0.04, 1.2 / z);
        ctx.strokeRect(bx + 0.04, bz + 0.04, Math.max(0.1, sx - 0.08), Math.max(0.1, sz - 0.08));
      }
      if (reserved && reservedByLabel && z >= 18) {
        ctx.fillStyle = '#d8be74';
        ctx.font = `${0.45}px system-ui`;
        ctx.textAlign = 'left';
        const reservationLabel = reservationJobDef
          ? `${String(reservedByLabel).slice(0, 10)} ${String(reservationJobDef).slice(0, 8)}`
          : String(reservedByLabel).slice(0, 14);
        ctx.fillText(reservationLabel, bx + 0.08, bz + 0.48);
      }

      if (role === 'door' || btype === 2) {
        ctx.strokeStyle = '#d0b276';
        ctx.lineWidth = Math.max(0.05, 1 / z);
        ctx.beginPath();
        if (open) {
          ctx.moveTo(bx + sx * 0.25, bz + sz * 0.75);
          ctx.lineTo(bx + sx * 0.75, bz + sz * 0.25);
        } else {
          ctx.moveTo(bx + sx * 0.25, bz + sz * 0.5);
          ctx.lineTo(bx + sx * 0.75, bz + sz * 0.5);
        }
        ctx.stroke();
      } else if (role === 'bed' || btype === 3) {
        ctx.fillStyle = '#c9d7e2';
        ctx.globalAlpha = 0.75;
        ctx.fillRect(bx + sx * 0.15, bz + sz * 0.12, Math.max(0.15, sx * 0.7), Math.max(0.12, sz * 0.25));
        ctx.globalAlpha = 1;
        if (ownerText && z >= 16) {
          ctx.fillStyle = '#d8be74';
          ctx.font = `${0.45}px system-ui`;
          ctx.textAlign = 'center';
          ctx.fillText(String(ownerText).slice(0, 10), bx + sx * 0.5, bz + sz + 0.48);
        }
      } else if (role === 'workbench' || btype === 4) {
        ctx.fillStyle = '#1a1510';
        ctx.globalAlpha = 0.45;
        ctx.fillRect(bx + sx * 0.2, bz + sz * 0.2, Math.max(0.2, sx * 0.6), Math.max(0.2, sz * 0.6));
        ctx.globalAlpha = 1;
        if (billCount > 0 && z >= 10) {
          ctx.fillStyle = '#d8be74';
          const pips = Math.min(4, billCount);
          for (let i = 0; i < pips; i++) {
            ctx.fillRect(bx + 0.25 + i * 0.22, bz + sz - 0.32, 0.12, 0.12);
          }
        }
        if (billLabelText && z >= 18) {
          ctx.fillStyle = '#d8be74';
          ctx.font = `${0.45}px system-ui`;
          ctx.textAlign = 'center';
          ctx.fillText(String(billLabelText).slice(0, 18), bx + sx * 0.5, bz - 0.16);
        }
        if (billDetailText && z >= 22) {
          ctx.fillStyle = '#e3ded0';
          ctx.font = `${0.42}px system-ui`;
          ctx.textAlign = 'center';
          ctx.fillText(String(billDetailText).slice(0, 22), bx + sx * 0.5, bz + sz + 0.42);
        }
        if (Number.isFinite(activeJobProgress) && activeJobProgress >= 0) {
          const barW = Math.max(0.35, sx * 0.7);
          const barX = bx + (sx - barW) * 0.5;
          const barY = bz + sz * 0.5;
          ctx.fillStyle = '#0b0d0c';
          ctx.globalAlpha = 0.8;
          ctx.fillRect(barX, barY, barW, 0.12);
          ctx.globalAlpha = 1;
          ctx.fillStyle = '#71d78b';
          ctx.fillRect(barX, barY, barW * Math.max(0, Math.min(100, activeJobProgress)) / 100, 0.12);
        }
        if (activeJobText && z >= 20) {
          ctx.fillStyle = '#71d78b';
          ctx.font = `${0.42}px system-ui`;
          ctx.textAlign = 'center';
          ctx.fillText(String(activeJobText).slice(0, 24), bx + sx * 0.5, bz + sz + 0.88);
        }
      }

      const buildingGlyph = this._buildingGlyph(role, btype, b[6]);
      if (buildingGlyph && z >= 10) {
        ctx.fillStyle = '#090a0a';
        ctx.globalAlpha = 0.55;
        ctx.fillRect(bx + sx * 0.5 - 0.34, bz + sz * 0.5 - 0.34, 0.68, 0.68);
        ctx.globalAlpha = 1;
        ctx.fillStyle = '#f0e6c8';
        ctx.font = `${0.58}px system-ui`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(buildingGlyph, bx + sx * 0.5, bz + sz * 0.5);
        ctx.textBaseline = 'alphabetic';
      }

      if (Number(interactionX) >= 0 && Number(interactionZ) >= 0 && z >= 8) {
        ctx.fillStyle = '#d8be74';
        ctx.fillRect(Number(interactionX) + 0.35, Number(interactionZ) + 0.35, 0.3, 0.3);
      }
    }

    // Ground items
    for (const item of this.items) {
      if (!Array.isArray(item)) continue;
      const [ix, iz, kind, stack] = item;
      const itemColor = ITEM_COLORS[kind] || ITEM_COLORS[0];
      const flags = Number(item[7]) || 0;
      const forbidden = (flags & 1) === 1;
      const reserved = (flags & 2) === 2;
      const reservedByLabel = item[8] || '';
      const r = z >= 10 ? 0.28 : 0.20;

      ctx.fillStyle = itemColor;
      ctx.strokeStyle = forbidden ? '#b84a4a' : reserved ? '#d0a448' : '#2a2418';
      ctx.lineWidth = Math.max(0.04, 1 / z);
      ctx.beginPath();
      ctx.moveTo(ix + 0.5, iz + 0.5 - r);
      ctx.lineTo(ix + 0.5 + r, iz + 0.5);
      ctx.lineTo(ix + 0.5, iz + 0.5 + r);
      ctx.lineTo(ix + 0.5 - r, iz + 0.5);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();

      const itemGlyph = this._itemGlyph(kind, item[5], item[6]);
      if (itemGlyph && z >= 10) {
        ctx.fillStyle = '#111313';
        ctx.globalAlpha = 0.65;
        ctx.fillRect(ix + 0.22, iz + 0.22, 0.56, 0.56);
        ctx.globalAlpha = 1;
        ctx.fillStyle = '#f0e6c8';
        ctx.font = `${0.44}px system-ui`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(itemGlyph, ix + 0.5, iz + 0.51);
        ctx.textBaseline = 'alphabetic';
      }

      if (stack > 1 && z >= 12) {
        ctx.fillStyle = '#f0e6c8';
        ctx.font = `${0.55}px system-ui`;
        ctx.textAlign = 'center';
        ctx.fillText(String(stack), ix + 0.5, iz + 1.15);
      }

      if (item[6] && z >= 20) {
        ctx.fillStyle = '#e3ded0';
        ctx.font = `${0.55}px system-ui`;
        ctx.textAlign = 'center';
        ctx.fillText(String(item[6]), ix + 0.5, iz - 0.15);
      }
      if (reserved && reservedByLabel && z >= 18) {
        ctx.fillStyle = '#d8be74';
        ctx.font = `${0.5}px system-ui`;
        ctx.textAlign = 'center';
        ctx.fillText(String(reservedByLabel).slice(0, 12), ix + 0.5, iz + 1.7);
      }
    }

    // Fire, plants, blueprints, and construction frames
    for (const entity of this.worldEntities) {
      if (!entity || !entity.kind) continue;
      const ex = Number(entity.x) || 0;
      const ez = Number(entity.z) || 0;
      if (entity.kind === 'fire') {
        const pulse = 0.08 * Math.sin(this._now() / 90 + ex + ez);
        ctx.fillStyle = WORLD_ENTITY_COLORS.fire;
        ctx.globalAlpha = 0.85;
        ctx.beginPath();
        ctx.arc(ex + 0.5, ez + 0.5, 0.28 + pulse, 0, Math.PI * 2);
        ctx.fill();
        ctx.globalAlpha = 1;
      } else if (entity.kind === 'plant') {
        const growth = Math.max(0.15, Math.min(1, Number(entity.growth) || 0.55));
        ctx.fillStyle = WORLD_ENTITY_COLORS.plant;
        ctx.beginPath();
        ctx.arc(ex + 0.5, ez + 0.5, 0.16 + growth * 0.18, 0, Math.PI * 2);
        ctx.fill();
      } else if (entity.kind === 'construction') {
        const sx = Math.max(1, Number(entity.sizeX) || 1);
        const sz = Math.max(1, Number(entity.sizeZ) || 1);
        const progress = Math.max(0, Math.min(1, Number(entity.progress) || 0));
        ctx.strokeStyle = WORLD_ENTITY_COLORS.construction;
        ctx.lineWidth = Math.max(0.04, 1 / z);
        ctx.globalAlpha = 0.85;
        ctx.strokeRect(ex + 0.08, ez + 0.08, sx - 0.16, sz - 0.16);
        if (progress > 0) {
          ctx.fillStyle = WORLD_ENTITY_COLORS.construction;
          ctx.globalAlpha = 0.35;
          ctx.fillRect(ex + 0.12, ez + sz - 0.24, Math.max(0.08, (sx - 0.24) * progress), 0.12);
          ctx.globalAlpha = 0.85;
        }
        ctx.beginPath();
        ctx.moveTo(ex + 0.15, ez + 0.15);
        ctx.lineTo(ex + sx - 0.15, ez + sz - 0.15);
        ctx.moveTo(ex + sx - 0.15, ez + 0.15);
        ctx.lineTo(ex + 0.15, ez + sz - 0.15);
        ctx.stroke();
        ctx.globalAlpha = 1;
      }

      if (entity.label && z >= 24) {
        ctx.fillStyle = '#d9d1b5';
        ctx.font = `${0.5}px system-ui`;
        ctx.textAlign = 'center';
        ctx.fillText(String(entity.label), ex + 0.5, ez - 0.12);
      }
    }

    // Pawns
    const now = this._now();
    for (const p of this.pawns) {
      const [px, pz, faction, pid] = p;
      const isViewer = pid === this.viewerPawnId;
      const visual = this._getEntityVisualPosition(pid, px, pz, now);
      const drawX = visual.x;
      const drawZ = visual.z;

      if (isViewer) {
        // Viewer pawn: bright ring
        ctx.fillStyle = '#ffffff';
        ctx.beginPath();
        ctx.arc(drawX + 0.5, drawZ + 0.5, 0.7, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = '#00ccff';
        ctx.beginPath();
        ctx.arc(drawX + 0.5, drawZ + 0.5, 0.5, 0, Math.PI * 2);
        ctx.fill();
      } else {
        ctx.fillStyle = PAWN_COLORS[faction] || PAWN_COLORS[0];
        ctx.beginPath();
        ctx.arc(drawX + 0.5, drawZ + 0.5, 0.4, 0, Math.PI * 2);
        ctx.fill();
      }

      // Name label for humanlike
      if (p.length > 4 && p[4] && z >= 5) {
        ctx.fillStyle = isViewer ? '#00ccff' : '#ccc';
        ctx.font = `${0.8}px system-ui`;
        ctx.textAlign = 'center';
        ctx.fillText(p[4], drawX + 0.5, drawZ - 0.3);
      }
    }

    this._drawTargetAffordances(ctx, z, now);

    ctx.restore();

    this._drawHoverTooltip(ctx, cw, ch);

    // HUD: zoom level
    ctx.fillStyle = '#555';
    ctx.font = '10px system-ui';
    ctx.textAlign = 'left';
    const zoom = Number.isFinite(Number(this.zoom)) ? Number(this.zoom) : 1;
    ctx.fillText(`${zoom.toFixed(1)}x`, 4, ch - 4);
  }

  _drawTargetAffordances(ctx, z, now) {
    const hover = this.hoverCell;
    if (hover) {
      const color = this.targetMode === 'attack'
        ? '#d45b4f'
        : this.targetMode === 'move'
          ? '#69d2e7'
          : '#d8be74';
      this._drawCellOutline(ctx, hover.x, hover.z, color, Math.max(0.06, 1.4 / z), 0.9);

      if (this.targetMode && this.viewerPawnId >= 0) {
        const viewer = this._viewerPawnCell(now);
        if (viewer) {
          ctx.strokeStyle = color;
          ctx.globalAlpha = 0.42;
          ctx.lineWidth = Math.max(0.04, 1 / z);
          ctx.beginPath();
          ctx.moveTo(viewer.x + 0.5, viewer.z + 0.5);
          ctx.lineTo(hover.x + 0.5, hover.z + 0.5);
          ctx.stroke();
          ctx.globalAlpha = 1;
        }
      }
    }

    if (this.hoverEntity) {
      this._drawEntityOutline(ctx, this.hoverEntity, '#f0e6c8', Math.max(0.06, 1.5 / z), 0.95);
    }

    if (this.recentTarget && this.recentTarget.expiresAt > now) {
      const alpha = Math.max(0, Math.min(1, (this.recentTarget.expiresAt - now) / 1600));
      const color = this.recentTarget.action === 'attack'
        ? '#d45b4f'
        : this.recentTarget.action === 'context_menu'
          ? '#d8be74'
          : '#69d2e7';
      this._drawCellOutline(ctx, this.recentTarget.x, this.recentTarget.z, color, Math.max(0.08, 1.8 / z), 0.25 + alpha * 0.55);
      ctx.fillStyle = color;
      ctx.globalAlpha = 0.2 + alpha * 0.28;
      ctx.beginPath();
      ctx.arc(this.recentTarget.x + 0.5, this.recentTarget.z + 0.5, 0.42 + (1 - alpha) * 0.45, 0, Math.PI * 2);
      ctx.fill();
      ctx.globalAlpha = 1;
    }
  }

  _drawCellOutline(ctx, x, z, color, lineWidth, alpha = 1) {
    if (!Number.isFinite(Number(x)) || !Number.isFinite(Number(z))) return;
    ctx.strokeStyle = color;
    ctx.globalAlpha = alpha;
    ctx.lineWidth = lineWidth;
    ctx.strokeRect(Number(x) + 0.05, Number(z) + 0.05, 0.9, 0.9);
    ctx.globalAlpha = 1;
  }

  _drawEntityOutline(ctx, hit, color, lineWidth, alpha = 1) {
    if (!hit) return;
    const x = Number(hit.drawX ?? hit.x);
    const z = Number(hit.drawZ ?? hit.z);
    if (!Number.isFinite(x) || !Number.isFinite(z)) return;

    ctx.strokeStyle = color;
    ctx.globalAlpha = alpha;
    ctx.lineWidth = lineWidth;
    if (hit.kind === 'pawn' || hit.kind === 'animal' || hit.kind === 'mech') {
      ctx.beginPath();
      ctx.arc(x + 0.5, z + 0.5, 0.72, 0, Math.PI * 2);
      ctx.stroke();
    } else if (hit.kind === 'item' || hit.kind === 'fire' || hit.kind === 'plant') {
      ctx.strokeRect(x + 0.18, z + 0.18, 0.64, 0.64);
    } else {
      const width = Math.max(1, Number(hit.sizeX) || 1);
      const height = Math.max(1, Number(hit.sizeZ) || 1);
      ctx.strokeRect(x + 0.04, z + 0.04, Math.max(0.1, width - 0.08), Math.max(0.1, height - 0.08));
    }
    ctx.globalAlpha = 1;
  }

  _drawHoverTooltip(ctx, cw, ch) {
    if (!this.hoverCell) return;
    const label = this._hoverLabel();
    if (!label) return;

    const point = this._cellToScreen(this.hoverCell.x, this.hoverCell.z);
    if (!point) return;

    ctx.save();
    ctx.font = '12px system-ui';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    const paddingX = 8;
    const paddingY = 5;
    const metrics = ctx.measureText(label);
    const width = Math.ceil(metrics.width) + paddingX * 2;
    const height = 24;
    let x = Math.round(point.x + 12);
    let y = Math.round(point.y - height - 8);
    if (x + width > cw - 8) x = Math.max(8, cw - width - 8);
    if (y < 8) y = Math.min(ch - height - 8, Math.round(point.y + 12));

    ctx.fillStyle = 'rgba(8, 10, 10, 0.88)';
    ctx.strokeStyle = this.targetMode === 'attack' ? '#7d4038' : this.targetMode === 'move' ? '#356b76' : '#66542b';
    ctx.lineWidth = 1;
    ctx.fillRect(x, y, width, height);
    ctx.strokeRect(x + 0.5, y + 0.5, width - 1, height - 1);
    ctx.fillStyle = '#e8e1d0';
    ctx.fillText(label, x + paddingX, y + height / 2 + 0.5);
    ctx.restore();
  }

  _hoverLabel() {
    const cell = this.hoverCell;
    if (!cell) return '';
    const coords = `${cell.x}, ${cell.z}`;
    if (this._isFoggedCell(cell.x, cell.z)) return `Unseen ${coords}`;

    const entity = this.hoverEntity;
    const label = entity
      ? String(entity.label || entity.defName || entity.kind || '').trim()
      : '';

    if (this.targetMode === 'attack') return label ? `Attack ${label}` : `Attack ${coords}`;
    if (this.targetMode === 'move') return label ? `Move near ${label}` : `Move ${coords}`;
    if (label) return `${label} (${coords})`;
    return coords;
  }

  _cellToScreen(x, z) {
    const sx = (Number(x) + 0.5 - this.camX) * this.zoom + this.canvas.width / 2;
    const sy = (Number(z) + 0.5 - this.camY) * this.zoom + this.canvas.height / 2;
    if (!Number.isFinite(sx) || !Number.isFinite(sy)) return null;
    return { x: sx, y: sy };
  }

  _viewerPawnCell(now = this._now()) {
    const pawn = this.pawns.find(p => Array.isArray(p) && Number(p[3]) === Number(this.viewerPawnId));
    if (!pawn) return null;
    const visual = this._getEntityVisualPosition(pawn[3], pawn[0], pawn[1], now);
    return { x: visual.x, z: visual.z };
  }

  _isFoggedCell(x, z) {
    const ix = Number(x);
    const iz = Number(z);
    if (!Number.isInteger(ix) || !Number.isInteger(iz) || ix < 0 || iz < 0 || ix >= this.width || iz >= this.height) return false;
    return this._isFoggedIndex(iz * this.width + ix);
  }

  _entityAtCell(x, z) {
    const ix = Number(x);
    const iz = Number(z);
    if (!Number.isInteger(ix) || !Number.isInteger(iz)) return null;

    const now = this._now();
    for (let i = this.pawns.length - 1; i >= 0; i--) {
      const pawn = this.pawns[i];
      if (!Array.isArray(pawn)) continue;
      const px = Math.floor(Number(pawn[0]));
      const pz = Math.floor(Number(pawn[1]));
      if (px !== ix || pz !== iz) continue;
      const visual = this._getEntityVisualPosition(pawn[3], pawn[0], pawn[1], now);
      return {
        id: Number(pawn[3]),
        kind: Number(pawn[2]) === 3 ? 'animal' : 'pawn',
        label: pawn[4] || 'pawn',
        x: px,
        z: pz,
        drawX: visual.x,
        drawZ: visual.z
      };
    }

    for (let i = this.items.length - 1; i >= 0; i--) {
      const item = this.items[i];
      if (!Array.isArray(item)) continue;
      const itemX = Math.floor(Number(item[0]));
      const itemZ = Math.floor(Number(item[1]));
      if (itemX !== ix || itemZ !== iz) continue;
      return {
        id: Number(item[4]),
        kind: 'item',
        label: item[6] || item[5] || 'item',
        defName: item[5] || '',
        x: itemX,
        z: itemZ
      };
    }

    for (let i = this.worldEntities.length - 1; i >= 0; i--) {
      const entity = this.worldEntities[i];
      if (!entity) continue;
      const ex = Math.floor(Number(entity.x));
      const ez = Math.floor(Number(entity.z));
      const sx = Math.max(1, Number(entity.sizeX) || 1);
      const sz = Math.max(1, Number(entity.sizeZ) || 1);
      if (ix < ex || iz < ez || ix >= ex + sx || iz >= ez + sz) continue;
      return {
        id: Number(entity.id),
        kind: entity.kind || 'entity',
        label: entity.label || entity.defName || entity.kind || 'thing',
        defName: entity.defName || '',
        x: ex,
        z: ez,
        sizeX: sx,
        sizeZ: sz
      };
    }

    for (let i = this.buildings.length - 1; i >= 0; i--) {
      const building = this.buildings[i];
      if (!Array.isArray(building)) continue;
      const bx = Math.floor(Number(building[0]));
      const bz = Math.floor(Number(building[1]));
      const sx = Math.max(1, Number(building[2]) || 1);
      const sz = Math.max(1, Number(building[3]) || 1);
      if (ix < bx || iz < bz || ix >= bx + sx || iz >= bz + sz) continue;
      return {
        id: Number(building[5]),
        kind: building[12] || 'building',
        label: building[7] || building[6] || 'building',
        defName: building[6] || '',
        x: bx,
        z: bz,
        sizeX: sx,
        sizeZ: sz
      };
    }

    return null;
  }

  _buildingGlyph(role, btype, defName) {
    const text = String(`${role || ''} ${defName || ''}`).toLowerCase();
    if (role === 'door' || Number(btype) === 2 || text.includes('door')) return 'D';
    if (role === 'bed' || Number(btype) === 3 || text.includes('bed')) return 'B';
    if (role === 'workbench' || Number(btype) === 4 || text.includes('table') || text.includes('bench')) return 'W';
    if (Number(btype) === 5) return '!';
    return '';
  }

  _itemGlyph(kind, defName, label) {
    const text = String(`${defName || ''} ${label || ''}`).toLowerCase();
    if (Number(kind) === 1 || text.includes('gun') || text.includes('rifle') || text.includes('knife')) return 'W';
    if (Number(kind) === 2 || text.includes('apparel') || text.includes('pants') || text.includes('shirt')) return 'A';
    if (Number(kind) === 3 || text.includes('meal') || text.includes('food')) return 'F';
    if (Number(kind) === 4 || text.includes('medicine') || text.includes('drug')) return '+';
    if (Number(kind) === 5 || text.includes('steel') || text.includes('wood') || text.includes('component')) return 'M';
    return '';
  }

  _bindEvents() {
    // Keyboard
    this._addInputListener(window, 'keydown', e => {
      // Don't capture if typing in an input
      if (this._shouldIgnoreKeyboardEvent(e)) return;
      this.keys[e.key.toLowerCase()] = true;
    });
    this._addInputListener(window, 'keyup', e => {
      this.keys[e.key.toLowerCase()] = false;
    });

    // Mouse wheel zoom — keep follow mode; only pan unlocks it
    this._addInputListener(this.canvas, 'wheel', e => {
      e.preventDefault();
      const delta = e.deltaY > 0 ? -1 : 1;
      this.targetZoom = Math.max(this.minZoom, Math.min(this.maxZoom, this.targetZoom + delta));
    }, { passive: false });

    // Mouse drag to pan
    this._addInputListener(this.canvas, 'mousedown', e => {
      this._updateHoverFromPoint(e.clientX, e.clientY);
      if (e.button === 0) { this.dragging = true; this.dragStart = { x: e.clientX, y: e.clientY, camX: this.camX, camY: this.camY }; }
    });
    this._addInputListener(this.canvas, 'mousemove', e => {
      this._updateHoverFromPoint(e.clientX, e.clientY);
    });
    this._addInputListener(this.canvas, 'mouseleave', () => {
      this.hoverCell = null;
      this.hoverEntity = null;
      if (!this.targetMode) this.canvas.style.cursor = '';
      if (this.onTileHover) this.onTileHover(null, null);
    });
    this._addInputListener(window, 'mousemove', e => {
      if (this.dragging) this._updateHoverFromPoint(e.clientX, e.clientY);
      if (!this.dragging || !this.dragStart) return;
      const dx = (e.clientX - this.dragStart.x) / this.zoom;
      const dy = (e.clientY - this.dragStart.y) / this.zoom;
      if (Math.hypot(e.clientX - this.dragStart.x, e.clientY - this.dragStart.y) >= 5) {
        this._markUserMovedCamera();
      }
      this.camX = this.dragStart.camX - dx;
      this.camY = this.dragStart.camY - dy;
    });
    this._addInputListener(window, 'mouseup', e => {
      if (e.button === 0 && this.dragging) {
        const dist = this.dragStart ? Math.hypot(e.clientX - this.dragStart.x, e.clientY - this.dragStart.y) : 0;
        this.dragging = false;
        // If it was a click (not a drag), fire tile click
        if (dist < 5 && this.onTileClick) {
          const tile = this._screenToTile(e.clientX, e.clientY);
          if (tile) {
            this._updateHoverFromTile(tile);
            this.onTileClick(tile.x, tile.z);
          }
        }
      }
    });

    // Right click
    this._addInputListener(this.canvas, 'contextmenu', e => {
      e.preventDefault();
      if (this.onTileRightClick) {
        const tile = this._screenToTile(e.clientX, e.clientY);
        if (tile) {
          this._updateHoverFromTile(tile);
          this.onTileRightClick(tile.x, tile.z, e.clientX, e.clientY);
        }
      }
    });

    // Touch support
    this._addInputListener(this.canvas, 'touchstart', e => {
      if (e.touches.length === 1) {
        const t = e.touches[0];
        this.dragging = true;
        this.dragStart = { x: t.clientX, y: t.clientY, camX: this.camX, camY: this.camY };
      }
    }, { passive: true });
    this._addInputListener(this.canvas, 'touchmove', e => {
      if (this.dragging && this.dragStart && e.touches.length === 1) {
        const t = e.touches[0];
        const dx = (t.clientX - this.dragStart.x) / this.zoom;
        const dy = (t.clientY - this.dragStart.y) / this.zoom;
        if (Math.hypot(t.clientX - this.dragStart.x, t.clientY - this.dragStart.y) >= 10) {
          this._markUserMovedCamera();
        }
        this.camX = this.dragStart.camX - dx;
        this.camY = this.dragStart.camY - dy;
      }
      // Pinch zoom
      if (e.touches.length === 2) {
        const dist = Math.hypot(e.touches[0].clientX - e.touches[1].clientX, e.touches[0].clientY - e.touches[1].clientY);
        if (this.lastTouch) {
          const delta = (dist - this.lastTouch) * 0.02;
          this.targetZoom = Math.max(this.minZoom, Math.min(this.maxZoom, this.targetZoom + delta));
        }
        this.lastTouch = dist;
      }
    }, { passive: true });
    this._addInputListener(this.canvas, 'touchend', e => {
      this.dragging = false;
      this.lastTouch = null;
      if (e.changedTouches.length === 1 && this.dragStart) {
        const t = e.changedTouches[0];
        const dist = Math.hypot(t.clientX - this.dragStart.x, t.clientY - this.dragStart.y);
        if (dist < 10 && this.onTileClick) {
          const tile = this._screenToTile(t.clientX, t.clientY);
          if (tile) {
            this._updateHoverFromTile(tile);
            this.onTileClick(tile.x, tile.z);
          }
        }
      }
    });

    // Recenter hotkey
    this._addInputListener(window, 'keydown', e => {
      if (this._shouldIgnoreKeyboardEvent(e)) return;
      if (e.key === ' ' || e.key === 'c') {
        this.setFollowPawn(true);
        if (typeof this.onFollowChanged === 'function') this.onFollowChanged(true);
      }
    });
  }

  _addInputListener(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    this.inputListeners.push({ target, type, handler, options });
  }

  _updateHoverFromPoint(clientX, clientY) {
    const tile = this._screenToTile(clientX, clientY);
    this._updateHoverFromTile(tile);
  }

  _updateHoverFromTile(tile) {
    if (!tile) {
      this.hoverCell = null;
      this.hoverEntity = null;
      if (!this.targetMode) this.canvas.style.cursor = '';
      if (this.onTileHover) this.onTileHover(null, null);
      return;
    }

    this.hoverCell = { x: tile.x, z: tile.z };
    this.hoverEntity = this._isFoggedCell(tile.x, tile.z) ? null : this._entityAtCell(tile.x, tile.z);
    this.canvas.style.cursor = this.targetMode ? 'crosshair' : (this.hoverEntity ? 'pointer' : '');
    if (this.onTileHover) this.onTileHover(this.hoverCell, this.hoverEntity);
  }

  _shouldIgnoreKeyboardEvent(e) {
    const target = e.target;
    if (!target) return false;
    if (target.isContentEditable) return true;
    const tagName = target.tagName;
    return tagName === 'INPUT' || tagName === 'TEXTAREA' || tagName === 'SELECT' || tagName === 'BUTTON';
  }

  _screenToTile(clientX, clientY) {
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return null;
    const sx = ((clientX - rect.left) / rect.width) * this.canvas.width;
    const sy = ((clientY - rect.top) / rect.height) * this.canvas.height;
    const x = Math.floor((sx - this.canvas.width / 2) / this.zoom + this.camX);
    const z = Math.floor((sy - this.canvas.height / 2) / this.zoom + this.camY);
    if (x < 0 || z < 0 || x >= this.width || z >= this.height) return null;
    return { x, z };
  }
}
