using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Extracts map tile data as compact arrays for browser-side rendering.
    /// Sends a full terrain grid once, then streams position deltas.
    /// </summary>
    public static class TileMapSerializer
    {
        private const int MaxItemsPerDelta = 700;
        private const int MaxWorldEntitiesPerDelta = 900;
        public const int DefaultChunkSize = 32;
        private const int MaxMapChunksPerDelta = 4;

        // Terrain type → color byte (compact palette)
        private static readonly Dictionary<string, byte> terrainPalette = new Dictionary<string, byte>
        {
            // Natural terrain
            ["Soil"] = 1, ["MossyTerrain"] = 1, ["Gravel"] = 2,
            ["Sand"] = 3, ["MarshyTerrain"] = 4,
            ["Mud"] = 5, ["Ice"] = 6, ["PackedDirt"] = 1,
            ["SoftSand"] = 3, ["RoughStone"] = 7,
            ["RoughGranite"] = 7, ["RoughLimestone"] = 7, ["RoughSandstone"] = 8,
            ["RoughSlate"] = 7, ["RoughMarble"] = 9,
            ["SmoothGranite"] = 10, ["SmoothLimestone"] = 10, ["SmoothSandstone"] = 10,
            ["SmoothSlate"] = 10, ["SmoothMarble"] = 10,
            // Water
            ["WaterDeep"] = 20, ["WaterShallow"] = 21, ["WaterMovingShallow"] = 21,
            ["WaterMovingChestDeep"] = 20, ["WaterOceanDeep"] = 22, ["WaterOceanShallow"] = 21,
            // Built floors
            ["WoodPlankFloor"] = 30, ["MetalTile"] = 31, ["SterileTile"] = 32,
            ["PavedTile"] = 33, ["CarpetDark"] = 34, ["FlagstoneGranite"] = 35,
            ["ConcreteTile"] = 33,
        };

        private static byte GetTerrainByte(TerrainDef def)
        {
            if (def == null) return 0;
            if (terrainPalette.TryGetValue(def.defName, out byte val)) return val;
            // Fallback heuristics
            string name = def.defName.ToLower();
            if (name.Contains("water")) return 20;
            if (name.Contains("sand")) return 3;
            if (name.Contains("rough")) return 7;
            if (name.Contains("smooth")) return 10;
            if (name.Contains("carpet") || name.Contains("floor") || name.Contains("tile")) return 30;
            if (name.Contains("soil") || name.Contains("dirt")) return 1;
            return 0;
        }

        /// <summary>
        /// Serialize the full terrain grid as a compact byte array (base64).
        /// Called once per viewer on assignment or map change.
        /// </summary>
        public static Dictionary<string, object> SerializeFullMap(Map map)
        {
            if (map == null) return null;

            int w = map.Size.x;
            int h = map.Size.z;
            int hash;
            byte[] terrain;
            byte[] roofs;
            byte[] fog;
            BuildChunkLayers(map, 0, 0, w, h, out terrain, out roofs, out fog, out hash);

            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.MapFull,
                ["width"] = w,
                ["height"] = h,
                ["chunkSize"] = DefaultChunkSize,
                ["terrain"] = Convert.ToBase64String(terrain),
                ["roofs"] = Convert.ToBase64String(roofs),
                ["fog"] = Convert.ToBase64String(fog),
                ["visibilityFilteredMap"] = true,
            };
        }

        public static Dictionary<string, int> BuildChunkHashSnapshot(Map map, int chunkSize = DefaultChunkSize)
        {
            var hashes = new Dictionary<string, int>();
            if (map == null || chunkSize <= 0)
                return hashes;

            int chunksX = (map.Size.x + chunkSize - 1) / chunkSize;
            int chunksZ = (map.Size.z + chunkSize - 1) / chunkSize;
            for (int cz = 0; cz < chunksZ; cz++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    hashes[BuildChunkKey(cx, cz)] = ComputeChunkHash(map, cx, cz, chunkSize);
                }
            }

            return hashes;
        }

        public static List<Dictionary<string, object>> SerializeChangedMapChunks(
            Map map,
            IDictionary<string, int> previousChunkHashes,
            out Dictionary<string, int> nextChunkHashes,
            int chunkSize = DefaultChunkSize,
            int maxChunks = MaxMapChunksPerDelta)
        {
            var chunks = new List<Dictionary<string, object>>();
            nextChunkHashes = previousChunkHashes != null
                ? new Dictionary<string, int>(previousChunkHashes)
                : BuildChunkHashSnapshot(map, chunkSize);

            if (map == null || previousChunkHashes == null || chunkSize <= 0 || maxChunks <= 0)
                return chunks;

            int chunksX = (map.Size.x + chunkSize - 1) / chunkSize;
            int chunksZ = (map.Size.z + chunkSize - 1) / chunkSize;
            for (int cz = 0; cz < chunksZ; cz++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    string key = BuildChunkKey(cx, cz);
                    int hash = ComputeChunkHash(map, cx, cz, chunkSize);
                    if (previousChunkHashes.TryGetValue(key, out int previousHash) && previousHash == hash)
                        continue;

                    if (chunks.Count >= maxChunks)
                        continue;

                    var chunk = SerializeMapChunk(map, cx, cz, chunkSize, hash);
                    if (chunk == null)
                        continue;

                    chunks.Add(chunk);
                    nextChunkHashes[key] = hash;
                }
            }

            return chunks;
        }

        private static Dictionary<string, object> SerializeMapChunk(Map map, int chunkX, int chunkZ, int chunkSize, int hash)
        {
            if (map == null || chunkSize <= 0)
                return null;

            int x = chunkX * chunkSize;
            int z = chunkZ * chunkSize;
            int width = Math.Min(chunkSize, map.Size.x - x);
            int height = Math.Min(chunkSize, map.Size.z - z);
            if (width <= 0 || height <= 0)
                return null;

            byte[] terrain;
            byte[] roofs;
            byte[] fog;
            int computedHash;
            BuildChunkLayers(map, x, z, width, height, out terrain, out roofs, out fog, out computedHash);
            if (hash == 0)
                hash = computedHash;

            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.MapChunk,
                ["chunkX"] = chunkX,
                ["chunkZ"] = chunkZ,
                ["chunkSize"] = chunkSize,
                ["x"] = x,
                ["z"] = z,
                ["width"] = width,
                ["height"] = height,
                ["terrain"] = Convert.ToBase64String(terrain),
                ["roofs"] = Convert.ToBase64String(roofs),
                ["fog"] = Convert.ToBase64String(fog),
                ["hash"] = hash,
                ["visibilityFilteredMap"] = true
            };
        }

        private static int ComputeChunkHash(Map map, int chunkX, int chunkZ, int chunkSize)
        {
            if (map == null || chunkSize <= 0)
                return 0;

            int x = chunkX * chunkSize;
            int z = chunkZ * chunkSize;
            int width = Math.Min(chunkSize, map.Size.x - x);
            int height = Math.Min(chunkSize, map.Size.z - z);
            if (width <= 0 || height <= 0)
                return 0;

            byte[] terrain;
            byte[] roofs;
            byte[] fog;
            int hash;
            BuildChunkLayers(map, x, z, width, height, out terrain, out roofs, out fog, out hash);
            return hash;
        }

        private static void BuildChunkLayers(
            Map map,
            int startX,
            int startZ,
            int width,
            int height,
            out byte[] terrain,
            out byte[] roofs,
            out byte[] fog,
            out int hash)
        {
            var grid = map.terrainGrid;
            var roofGrid = map.roofGrid;
            int cells = width * height;
            int bitBytes = (cells + 7) / 8;
            terrain = new byte[cells];
            roofs = new byte[bitBytes];
            fog = new byte[bitBytes];

            unchecked
            {
                hash = 17;
                for (int local = 0; local < cells; local++)
                {
                    int x = startX + (local % width);
                    int z = startZ + (local / width);
                    var cell = new IntVec3(x, 0, z);
                    bool visible = RimWorldCompat.IsTacticalMapCellVisible(map, cell, null);
                    byte terrainByte = 0;
                    bool roofed = false;
                    bool fogged = !visible;
                    if (visible)
                    {
                        terrainByte = GetTerrainByte(grid.TerrainAt(cell));
                        roofed = roofGrid.Roofed(cell);
                        terrain[local] = terrainByte;
                        if (roofed)
                            roofs[local / 8] |= (byte)(1 << (local % 8));
                    }
                    else
                    {
                        fog[local / 8] |= (byte)(1 << (local % 8));
                    }

                    hash = hash * 31 + terrainByte;
                    hash = hash * 31 + (roofed ? 1 : 0);
                    hash = hash * 31 + (fogged ? 1 : 0);
                }
            }
        }

        private static string BuildChunkKey(int chunkX, int chunkZ)
        {
            return chunkX.ToString(CultureInfo.InvariantCulture) + "," + chunkZ.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Serialize dynamic entities (pawns, buildings, items) as compact lists.
        /// Called every sync cycle (~0.5s).
        /// </summary>
        public static Dictionary<string, object> SerializeDelta(Map map, Pawn viewerPawn)
        {
            HashSet<int> currentEntityIds;
            return SerializeDelta(map, viewerPawn, null, out currentEntityIds);
        }

        public static Dictionary<string, object> SerializeDelta(
            Map map,
            Pawn viewerPawn,
            ISet<int> previousEntityIds,
            out HashSet<int> currentEntityIds)
        {
            Dictionary<int, int> currentEntityHashes;
            return SerializeDelta(map, viewerPawn, previousEntityIds, null, out currentEntityIds, out currentEntityHashes);
        }

        public static Dictionary<string, object> SerializeDelta(
            Map map,
            Pawn viewerPawn,
            ISet<int> previousEntityIds,
            IDictionary<int, int> previousEntityHashes,
            out HashSet<int> currentEntityIds,
            out Dictionary<int, int> currentEntityHashes)
        {
            currentEntityIds = new HashSet<int>();
            currentEntityHashes = new Dictionary<int, int>();
            if (map == null) return null;

            // Pawns
            var pawns = new List<object>();
            var entities = new List<object>();
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn == null || !pawn.Spawned) continue;
                if (!RimWorldCompat.IsTacticalMapThingVisible(pawn, viewerPawn)) continue;
                currentEntityIds.Add(pawn.thingIDNumber);
                byte faction = 0; // neutral
                if (pawn.Faction == Faction.OfPlayer) faction = 1;
                else if (pawn.Faction != null && FactionUtility.HostileTo(pawn.Faction, Faction.OfPlayer)) faction = 2;
                else if (pawn.RaceProps?.Animal == true) faction = 3;

                string label = pawn.RaceProps?.Humanlike == true
                    ? pawn.LabelShort ?? ""
                    : RimWorldCompat.SafeThingLabel(pawn);
                var entry = new List<object>
                {
                    pawn.Position.x,
                    pawn.Position.z,
                    faction,
                    pawn.thingIDNumber
                };
                // Add name for humanlike
                if (!string.IsNullOrEmpty(label) && pawn.RaceProps?.Humanlike == true)
                    entry.Add(label);
                pawns.Add(entry);

                entities.Add(new Dictionary<string, object>
                {
                    ["id"] = pawn.thingIDNumber,
                    ["kind"] = pawn.RaceProps?.Animal == true ? "animal" : "pawn",
                    ["x"] = pawn.Position.x,
                    ["z"] = pawn.Position.z,
                    ["faction"] = faction,
                    ["defName"] = RimWorldCompat.SafeThingDefName(pawn),
                    ["label"] = label,
                    ["dead"] = pawn.Dead,
                    ["downed"] = pawn.Downed
                });
            }

            // Buildings. Keep the first five fields stable for older browsers:
            // [x, z, sizeX, sizeZ, type, id, defName, label, rotation]
            var buildings = new List<object>();
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (b == null || !b.Spawned) continue;
                if (!RimWorldCompat.IsTacticalMapThingVisible(b, viewerPawn)) continue;
                currentEntityIds.Add(b.thingIDNumber);
                byte btype = RimWorldCompat.ClassifyMapBuilding(b);
                string defName = RimWorldCompat.SafeThingDefName(b);
                string label = RimWorldCompat.SafeThingLabel(b);
                int rotation = RimWorldCompat.SafeThingRotation(b);
                string role = RimWorldCompat.MapBuildingRole(b);
                int flags = RimWorldCompat.MapThingFlags(b, viewerPawn);
                bool hasInteractionCell = RimWorldCompat.TryGetInteractionCell(b, out int interactionX, out int interactionZ);
                bool open = RimWorldCompat.SafeDoorOpen(b);
                var owners = RimWorldCompat.SafeBedOwners(b);
                int billCount = RimWorldCompat.SafeBillCount(b);
                var billLabels = RimWorldCompat.SafeBillLabels(b);
                var billDetails = RimWorldCompat.SafeBillDetails(b);
                string billDetailSummary = RimWorldCompat.SummarizeBillDetails(billDetails);
                var activeJob = RimWorldCompat.SafeActiveWorkbenchJobMetadata(b);
                string activeJobSummary = RimWorldCompat.SummarizeActiveWorkbenchJob(activeJob);
                var reservation = RimWorldCompat.SafeReservationMetadata(b);
                string ownerLabel = owners.Count > 0 ? string.Join(", ", owners.ToArray()) : "";
                string billLabel = billLabels.Count > 0 ? string.Join(", ", billLabels.ToArray()) : "";

                var row = new List<object>
                {
                    b.Position.x,
                    b.Position.z,
                    b.def.size.x,
                    b.def.size.z,
                    btype,
                    b.thingIDNumber,
                    defName,
                    label,
                    rotation,
                    flags,
                    hasInteractionCell ? interactionX : -1,
                    hasInteractionCell ? interactionZ : -1,
                    role,
                    open ? 1 : 0,
                    ownerLabel,
                    billCount,
                    billLabel
                };
                AppendReservationFields(row, reservation);
                row.Add(billDetailSummary);
                row.Add(activeJobSummary);
                row.Add(GetActiveJobProgressPercent(activeJob));
                buildings.Add(row);
                var entity = new Dictionary<string, object>
                {
                    ["id"] = b.thingIDNumber,
                    ["kind"] = "building",
                    ["x"] = b.Position.x,
                    ["z"] = b.Position.z,
                    ["sizeX"] = b.def.size.x,
                    ["sizeZ"] = b.def.size.z,
                    ["buildingType"] = btype,
                    ["role"] = role,
                    ["defName"] = defName,
                    ["label"] = label,
                    ["rotation"] = rotation,
                    ["flags"] = flags,
                    ["hasInteractionCell"] = hasInteractionCell,
                    ["open"] = open,
                    ["owners"] = owners,
                    ["billCount"] = billCount,
                    ["billLabels"] = billLabels,
                    ["billDetails"] = billDetails,
                    ["billDetailSummary"] = billDetailSummary,
                    ["activeJob"] = activeJob,
                    ["activeJobSummary"] = activeJobSummary,
                    ["faction"] = b.Faction == Faction.OfPlayer ? 1 : 0
                };
                AddReservationMetadata(entity, reservation);
                entities.Add(entity);
                if (hasInteractionCell)
                {
                    entity["interactionX"] = interactionX;
                    entity["interactionZ"] = interactionZ;
                }
            }

            // Hostile buildings
            foreach (var b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b == null || !b.Spawned) continue;
                if (b.Faction == null || !FactionUtility.HostileTo(b.Faction, Faction.OfPlayer)) continue;
                if (!RimWorldCompat.IsTacticalMapThingVisible(b, viewerPawn)) continue;
                currentEntityIds.Add(b.thingIDNumber);
                byte btype = RimWorldCompat.ClassifyMapBuilding(b);
                string defName = RimWorldCompat.SafeThingDefName(b);
                string label = RimWorldCompat.SafeThingLabel(b);
                int rotation = RimWorldCompat.SafeThingRotation(b);
                string role = RimWorldCompat.MapBuildingRole(b);
                int flags = RimWorldCompat.MapThingFlags(b, viewerPawn);
                bool hasInteractionCell = RimWorldCompat.TryGetInteractionCell(b, out int interactionX, out int interactionZ);
                bool open = RimWorldCompat.SafeDoorOpen(b);
                var owners = RimWorldCompat.SafeBedOwners(b);
                int billCount = RimWorldCompat.SafeBillCount(b);
                var billLabels = RimWorldCompat.SafeBillLabels(b);
                var billDetails = RimWorldCompat.SafeBillDetails(b);
                string billDetailSummary = RimWorldCompat.SummarizeBillDetails(billDetails);
                var activeJob = RimWorldCompat.SafeActiveWorkbenchJobMetadata(b);
                string activeJobSummary = RimWorldCompat.SummarizeActiveWorkbenchJob(activeJob);
                var reservation = RimWorldCompat.SafeReservationMetadata(b);
                string ownerLabel = owners.Count > 0 ? string.Join(", ", owners.ToArray()) : "";
                string billLabel = billLabels.Count > 0 ? string.Join(", ", billLabels.ToArray()) : "";
                var row = new List<object>
                {
                    b.Position.x,
                    b.Position.z,
                    b.def.size.x,
                    b.def.size.z,
                    btype,
                    b.thingIDNumber,
                    defName,
                    label,
                    rotation,
                    flags,
                    hasInteractionCell ? interactionX : -1,
                    hasInteractionCell ? interactionZ : -1,
                    role,
                    open ? 1 : 0,
                    ownerLabel,
                    billCount,
                    billLabel
                };
                AppendReservationFields(row, reservation);
                row.Add(billDetailSummary);
                row.Add(activeJobSummary);
                row.Add(GetActiveJobProgressPercent(activeJob));
                buildings.Add(row);
                var entity = new Dictionary<string, object>
                {
                    ["id"] = b.thingIDNumber,
                    ["kind"] = "building",
                    ["x"] = b.Position.x,
                    ["z"] = b.Position.z,
                    ["sizeX"] = b.def.size.x,
                    ["sizeZ"] = b.def.size.z,
                    ["buildingType"] = btype,
                    ["role"] = role,
                    ["defName"] = defName,
                    ["label"] = label,
                    ["rotation"] = rotation,
                    ["flags"] = flags,
                    ["hasInteractionCell"] = hasInteractionCell,
                    ["open"] = open,
                    ["owners"] = owners,
                    ["billCount"] = billCount,
                    ["billLabels"] = billLabels,
                    ["billDetails"] = billDetails,
                    ["billDetailSummary"] = billDetailSummary,
                    ["activeJob"] = activeJob,
                    ["activeJobSummary"] = activeJobSummary,
                    ["faction"] = 2
                };
                AddReservationMetadata(entity, reservation);
                entities.Add(entity);
                if (hasInteractionCell)
                {
                    entity["interactionX"] = interactionX;
                    entity["interactionZ"] = interactionZ;
                }
            }

            // Ground items. Compact shape:
            // [x, z, kind, stack, id, defName, label, flags]
            // flags bit 1 = forbidden for the viewer pawn; bit 2 = reserved.
            var items = new List<object>();
            int itemTotal = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing?.def == null || !thing.Spawned || thing.Destroyed)
                    continue;
                if (thing.def.category != ThingCategory.Item)
                    continue;
                if (!RimWorldCompat.IsTacticalMapThingVisible(thing, viewerPawn))
                    continue;

                currentEntityIds.Add(thing.thingIDNumber);
                itemTotal++;
                if (items.Count >= MaxItemsPerDelta)
                    continue;

                byte itemKind = RimWorldCompat.ClassifyMapItem(thing);
                int flags = RimWorldCompat.MapThingFlags(thing, viewerPawn);
                string defName = RimWorldCompat.SafeThingDefName(thing);
                string label = RimWorldCompat.SafeThingLabel(thing);
                var reservation = RimWorldCompat.SafeReservationMetadata(thing);
                var row = new List<object>
                {
                    thing.Position.x,
                    thing.Position.z,
                    itemKind,
                    thing.stackCount,
                    thing.thingIDNumber,
                    defName,
                    label,
                    flags
                };
                AppendReservationFields(row, reservation);
                items.Add(row);
                var entity = new Dictionary<string, object>
                {
                    ["id"] = thing.thingIDNumber,
                    ["kind"] = "item",
                    ["x"] = thing.Position.x,
                    ["z"] = thing.Position.z,
                    ["itemKind"] = itemKind,
                    ["stack"] = thing.stackCount,
                    ["defName"] = defName,
                    ["label"] = label,
                    ["flags"] = flags
                };
                AddReservationMetadata(entity, reservation);
                entities.Add(entity);
            }

            int worldEntityTotal = 0;
            int worldEntityAdded = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing?.def == null || !thing.Spawned || thing.Destroyed)
                    continue;
                if (!RimWorldCompat.IsTacticalMapThingVisible(thing, viewerPawn))
                    continue;

                string kind = RimWorldCompat.ClassifyMapWorldEntityKind(thing);
                if (string.IsNullOrEmpty(kind))
                    continue;

                currentEntityIds.Add(thing.thingIDNumber);
                worldEntityTotal++;
                if (worldEntityAdded >= MaxWorldEntitiesPerDelta)
                    continue;

                worldEntityAdded++;
                string defName = RimWorldCompat.SafeThingDefName(thing);
                string label = RimWorldCompat.SafeThingLabel(thing);
                var entity = new Dictionary<string, object>
                {
                    ["id"] = thing.thingIDNumber,
                    ["kind"] = kind,
                    ["x"] = thing.Position.x,
                    ["z"] = thing.Position.z,
                    ["defName"] = defName,
                    ["label"] = label,
                    ["flags"] = RimWorldCompat.MapThingFlags(thing, viewerPawn),
                    ["rotation"] = RimWorldCompat.SafeThingRotation(thing)
                };
                AddReservationMetadata(entity, RimWorldCompat.SafeReservationMetadata(thing));

                if (kind == "plant")
                    entity["growth"] = RimWorldCompat.SafePlantGrowth(thing);

                if (kind == "construction")
                {
                    entity["sizeX"] = thing.def.size.x;
                    entity["sizeZ"] = thing.def.size.z;
                    entity["progress"] = RimWorldCompat.SafeConstructionProgress(thing);
                }

                entities.Add(entity);
            }

            var removedEntities = new List<object>();
            if (previousEntityIds != null)
            {
                foreach (int id in previousEntityIds)
                {
                    if (!currentEntityIds.Contains(id))
                        removedEntities.Add(id);
                }
            }

            bool entityKeyframe = previousEntityIds == null || previousEntityHashes == null;
            var entityUpdates = BuildEntityUpdates(entities, previousEntityHashes, currentEntityHashes, entityKeyframe);
            var result = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.MapDelta,
                ["pawns"] = pawns,
                ["buildings"] = buildings,
                ["items"] = items,
                ["itemCount"] = itemTotal,
                ["itemsTruncated"] = itemTotal > items.Count,
                ["worldEntityCount"] = worldEntityTotal,
                ["worldEntitiesTruncated"] = worldEntityTotal > worldEntityAdded,
                ["entities"] = entities,
                ["entityUpdates"] = entityUpdates,
                ["entityUpdateCount"] = entityUpdates.Count,
                ["entityCount"] = currentEntityIds.Count,
                ["entitiesTruncated"] = itemTotal > items.Count || worldEntityTotal > worldEntityAdded,
                ["entityKeyframe"] = entityKeyframe,
                ["removedEntities"] = removedEntities
            };

            // Viewer pawn ID for highlighting
            if (viewerPawn != null)
                result["viewerPawnId"] = viewerPawn.thingIDNumber;

            return result;
        }

        public static Dictionary<string, object> BuildEntityStateMessage(Dictionary<string, object> mapDelta)
        {
            if (mapDelta == null)
                return null;

            bool keyframe = TryGetBool(mapDelta, "entityKeyframe");
            object entities = mapDelta.TryGetValue("entities", out object fullEntities) ? fullEntities : new List<object>();
            object entityUpdates = mapDelta.TryGetValue("entityUpdates", out object updates) ? updates : entities;
            var msg = new Dictionary<string, object>
            {
                ["type"] = keyframe ? StateProtocol.EntityKeyframe : StateProtocol.EntityDelta,
                ["entities"] = keyframe ? entities : entityUpdates,
                ["removedEntities"] = mapDelta.TryGetValue("removedEntities", out object removed) ? removed : new List<object>(),
                ["entityCount"] = mapDelta.TryGetValue("entityCount", out object entityCount) ? entityCount : 0,
                ["entityUpdateCount"] = mapDelta.TryGetValue("entityUpdateCount", out object entityUpdateCount) ? entityUpdateCount : 0,
                ["entitiesTruncated"] = mapDelta.TryGetValue("entitiesTruncated", out object entitiesTruncated) ? entitiesTruncated : false,
                ["entityKeyframe"] = keyframe
            };

            CopyIfPresent(mapDelta, msg, "viewerPawnId");
            CopyIfPresent(mapDelta, msg, "itemCount");
            CopyIfPresent(mapDelta, msg, "itemsTruncated");
            CopyIfPresent(mapDelta, msg, "worldEntityCount");
            CopyIfPresent(mapDelta, msg, "worldEntitiesTruncated");
            CopyIfPresent(mapDelta, msg, "protocol");
            CopyIfPresent(mapDelta, msg, "mapEpoch");
            CopyIfPresent(mapDelta, msg, "seq");
            CopyIfPresent(mapDelta, msg, "baseSeq");
            CopyIfPresent(mapDelta, msg, "snapshot");
            CopyIfPresent(mapDelta, msg, "tick");
            CopyIfPresent(mapDelta, msg, "mapId");

            return msg;
        }

        private static List<object> BuildEntityUpdates(
            List<object> entities,
            IDictionary<int, int> previousEntityHashes,
            Dictionary<int, int> currentEntityHashes,
            bool keyframe)
        {
            var updates = new List<object>();
            if (entities == null)
                return updates;

            foreach (object entity in entities)
            {
                if (!TryGetEntityId(entity, out int id))
                    continue;

                int hash = StableEntityHash(entity);
                currentEntityHashes[id] = hash;

                if (keyframe ||
                    previousEntityHashes == null ||
                    !previousEntityHashes.TryGetValue(id, out int previousHash) ||
                    previousHash != hash)
                {
                    updates.Add(entity);
                }
            }

            return updates;
        }

        private static bool TryGetEntityId(object entity, out int id)
        {
            id = -1;
            if (entity is Dictionary<string, object> dict &&
                dict.TryGetValue("id", out object value) &&
                value != null)
            {
                try
                {
                    id = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return id >= 0;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static int StableEntityHash(object value)
        {
            unchecked
            {
                int hash = 17;
                AddStableHash(ref hash, value);
                return hash;
            }
        }

        private static void AddStableHash(ref int hash, object value)
        {
            unchecked
            {
                if (value == null)
                {
                    hash = hash * 31;
                    return;
                }

                if (value is string text)
                {
                    hash = hash * 31 + text.GetHashCode();
                    return;
                }

                if (value is bool boolValue)
                {
                    hash = hash * 31 + (boolValue ? 1 : 0);
                    return;
                }

                if (value is IFormattable formattable &&
                    !(value is IEnumerable) &&
                    !(value is Dictionary<string, object>))
                {
                    hash = hash * 31 + formattable.ToString(null, CultureInfo.InvariantCulture).GetHashCode();
                    return;
                }

                if (value is Dictionary<string, object> dict)
                {
                    foreach (string key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        hash = hash * 31 + key.GetHashCode();
                        AddStableHash(ref hash, dict[key]);
                    }
                    return;
                }

                if (value is IEnumerable enumerable)
                {
                    foreach (object entry in enumerable)
                        AddStableHash(ref hash, entry);
                    return;
                }

                hash = hash * 31 + value.ToString().GetHashCode();
            }
        }

        private static void CopyIfPresent(Dictionary<string, object> from, Dictionary<string, object> to, string key)
        {
            if (from != null && to != null && from.TryGetValue(key, out object value))
                to[key] = value;
        }

        private static bool TryGetBool(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out object value))
                return false;
            return value is bool boolValue && boolValue;
        }

        private static void AppendReservationFields(List<object> row, RimWorldCompat.ReservationMetadata reservation)
        {
            if (row == null)
                return;

            row.Add(reservation?.Reserved == true ? reservation.ReservedByLabel ?? "" : "");
            row.Add(reservation?.Reserved == true ? reservation.ReservedById : -1);
            row.Add(reservation?.Reserved == true ? reservation.ReservationJobDef ?? "" : "");
            row.Add(reservation?.Reserved == true ? reservation.ReservationTargetId : -1);
            row.Add(reservation?.Reserved == true && reservation.HasReservationTargetCell ? reservation.ReservationTargetX : -1);
            row.Add(reservation?.Reserved == true && reservation.HasReservationTargetCell ? reservation.ReservationTargetZ : -1);
        }

        private static void AddReservationMetadata(Dictionary<string, object> entity, RimWorldCompat.ReservationMetadata reservation)
        {
            if (entity == null || reservation?.Reserved != true)
                return;

            entity["reserved"] = true;
            if (reservation.ReservedById >= 0)
                entity["reservedById"] = reservation.ReservedById;
            if (!string.IsNullOrEmpty(reservation.ReservedByLabel))
                entity["reservedByLabel"] = reservation.ReservedByLabel;
            if (!string.IsNullOrEmpty(reservation.ReservationJobDef))
                entity["reservationJobDef"] = reservation.ReservationJobDef;
            if (reservation.ReservationTargetId >= 0)
                entity["reservationTargetId"] = reservation.ReservationTargetId;
            if (reservation.HasReservationTargetCell)
            {
                entity["reservationTargetX"] = reservation.ReservationTargetX;
                entity["reservationTargetZ"] = reservation.ReservationTargetZ;
            }
        }

        private static int GetActiveJobProgressPercent(Dictionary<string, object> activeJob)
        {
            if (activeJob == null || !activeJob.TryGetValue("progressPercent", out object value) || value == null)
                return -1;

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return -1;
            }
        }
    }
}
