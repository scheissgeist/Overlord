using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Dev-mode actions for exercising code paths that are otherwise expensive to
    /// reach by hand. These only appear in RimWorld's debug menu (dev mode on) and
    /// do nothing in a normal streamer's game.
    ///
    /// Why this exists: the Odyssey map-transition fixes (viewer's pawn moving to a
    /// gravship / space map) were previously only reachable by actually building a
    /// gravship and launching it, which takes hours of play. Without a way to force
    /// the transition, a silent failure is indistinguishable from success — so the
    /// fix would ship on reasoning alone. These actions make the transition
    /// reproducible in seconds and print what the viewer stream actually did.
    /// </summary>
    public static class OverlordDebugActions
    {
        /// Exercises auto-reconnect without waiting for a viewer to actually
        /// rejoin: drops each assigned viewer's pawn (as a restart would), then
        /// runs the reconnect path and reports whether each got the SAME pawn
        /// back. Also names anyone the reconnect deliberately refused.
        [DebugAction("Overlord", "Test viewer auto-reconnect", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void TestViewerAutoReconnect()
        {
            var vm = OverlordGameComponent.Instance?.Viewers;
            if (vm == null)
            {
                Log.Warning("[Overlord] No viewer manager — is the mod running?");
                return;
            }

            var before = new List<KeyValuePair<string, Pawn>>();
            foreach (var s in vm.AllSessions)
            {
                if (s?.assignedPawn != null)
                    before.Add(new KeyValuePair<string, Pawn>(s.username, s.assignedPawn));
            }

            if (before.Count == 0)
            {
                Log.Message("[Overlord] Auto-reconnect test: no viewers currently hold a colonist.");
                return;
            }

            // Simulate the restart: release every pawn, keeping the persisted
            // owner record that reconnect is supposed to read.
            foreach (var pair in before)
                vm.UnassignPawn(pair.Key);

            var sb = new StringBuilder();
            sb.AppendLine($"[Overlord] Auto-reconnect test ({before.Count} viewer(s)):");
            int restored = 0;
            foreach (var pair in before)
            {
                var got = vm.TryReconnectPreviousPawn(pair.Key);
                bool same = got == pair.Value;
                if (same) restored++;
                sb.AppendLine($"  {pair.Key}: had {pair.Value.LabelShort} -> " +
                    (got == null ? "REFUSED (banned/timed out/pawn gone)" : $"{got.LabelShort}{(same ? " OK" : " MISMATCH")}"));
            }
            sb.AppendLine($"  restored {restored}/{before.Count}");
            Log.Message(sb.ToString());
        }

        [DebugAction("Overlord", "Report viewer map state", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ReportViewerMapState()
        {
            var comp = OverlordGameComponent.Instance;
            var vm = comp?.Viewers;
            if (vm == null)
            {
                Log.Warning("[Overlord] No viewer manager — is the mod running?");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[Overlord] Viewer map state:");
            sb.AppendLine($"  RimWorld: {RimWorldCompat.RimWorldVersion}");
            sb.AppendLine($"  Find.CurrentMap: {DescribeMap(Find.CurrentMap)}");
            sb.AppendLine($"  All maps: {Find.Maps?.Count ?? 0}");
            if (Find.Maps != null)
            {
                foreach (Map m in Find.Maps)
                    sb.AppendLine($"    - {DescribeMap(m)}");
            }
            sb.AppendLine($"  tacticalMap enabled: {OverlordMod.Settings?.allowViewerTacticalMap == true}");

            int n = 0;
            foreach (var s in vm.AllSessions)
            {
                if (s == null || !s.HasPawn)
                    continue;
                n++;
                Pawn p = s.assignedPawn;
                sb.AppendLine(
                    $"  viewer '{s.username}' -> {p?.LabelShort ?? "?"} " +
                    $"| pawn.Map={DescribeMap(p?.Map)} " +
                    $"| cachedMapUid={s.tacticalMapUniqueId} " +
                    $"| epoch={s.tacticalMapEpoch} seq={s.tacticalMapSeq}" +
                    $"{(p != null && p.Map != null && s.tacticalMapUniqueId != p.Map.uniqueID && s.tacticalMapUniqueId != -1 ? "  <-- STALE, next cycle must resnapshot" : "")}");
            }
            if (n == 0)
                sb.AppendLine("  (no viewers with assigned pawns)");

            Log.Message(sb.ToString());
        }

        /// <summary>
        /// Generate a real space pocket map and move every assigned viewer pawn onto
        /// it. This is the same shape of transition a gravship launch produces (the
        /// pawn's Map changes underneath a live viewer stream) without needing to
        /// build and fly an actual gravship.
        /// </summary>
        [DebugAction("Overlord", "Send viewer pawns to space map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SendViewerPawnsToSpace()
        {
            var comp = OverlordGameComponent.Instance;
            var vm = comp?.Viewers;
            if (vm == null)
            {
                Log.Warning("[Overlord] No viewer manager.");
                return;
            }

            var movers = vm.AllSessions
                .Where(s => s != null && s.HasPawn && s.assignedPawn != null &&
                            s.assignedPawn.Spawned && !s.assignedPawn.Dead)
                .ToList();

            if (movers.Count == 0)
            {
                Log.Warning("[Overlord] No assigned viewer pawns to move.");
                return;
            }

            Map space;
            try
            {
                var gen = DefDatabase<MapGeneratorDef>.GetNamedSilentFail("SpacePocket")
                          ?? DefDatabase<MapGeneratorDef>.GetNamedSilentFail("Space");
                if (gen == null)
                {
                    Log.Error("[Overlord] No Space/SpacePocket map generator — is Odyssey active?");
                    return;
                }

                space = PocketMapUtility.GeneratePocketMap(new IntVec3(75, 1, 75), gen, null, Find.CurrentMap);
            }
            catch (Exception ex)
            {
                Log.Error($"[Overlord] Could not generate space pocket map: {ex}");
                return;
            }

            if (space == null)
            {
                Log.Error("[Overlord] Space map generation returned null.");
                return;
            }

            foreach (var s in movers)
            {
                Pawn p = s.assignedPawn;
                try
                {
                    IntVec3 cell = RimWorldCompat.FindColonistSpawnCell(space, p);
                    p.DeSpawn();
                    GenSpawn.Spawn(p, cell, space, Rot4.South, WipeMode.Vanish);
                    Log.Message($"[Overlord] moved {p.LabelShort} ({s.username}) to space map {space.uniqueID} at {cell}");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Overlord] Failed moving {p?.LabelShort}: {ex.Message}");
                }
            }

            Log.Message(
                "[Overlord] Viewers moved. Expected: each viewer's browser re-snapshots " +
                "(epoch bumps) and draws hull/space terrain, NOT a black canvas. " +
                "Run 'Report viewer map state' to confirm cachedMapUid caught up.");
        }

        [DebugAction("Overlord", "Return viewer pawns to home map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ReturnViewerPawnsHome()
        {
            var vm = OverlordGameComponent.Instance?.Viewers;
            if (vm == null)
                return;

            Map home = Find.Maps?.FirstOrDefault(m => m != null && m.IsPlayerHome);
            if (home == null)
            {
                Log.Warning("[Overlord] No player home map to return to.");
                return;
            }

            foreach (var s in vm.AllSessions)
            {
                Pawn p = s?.assignedPawn;
                if (p == null || !p.Spawned || p.Dead || p.Map == home)
                    continue;
                try
                {
                    IntVec3 cell = RimWorldCompat.FindColonistSpawnCell(home, null);
                    p.DeSpawn();
                    GenSpawn.Spawn(p, cell, home, Rot4.South, WipeMode.Vanish);
                    Log.Message($"[Overlord] returned {p.LabelShort} to home map at {cell}");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Overlord] Failed returning {p?.LabelShort}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Print how every TerrainDef currently loaded maps into the viewer palette.
        /// Anything reported as byte 0 renders to viewers as near-black "unknown",
        /// which is the defect that made gravships/space look like a void. Covers
        /// modded terrain too, not just Odyssey's.
        /// </summary>
        [DebugAction("Overlord", "Audit terrain palette coverage", allowedGameStates = AllowedGameStates.Playing)]
        public static void AuditTerrainPalette()
        {
            var unmapped = DefDatabase<TerrainDef>.AllDefsListForReading
                .Where(d => d != null && TileMapSerializer.DebugTerrainByte(d) == 0)
                .OrderBy(d => d.defName)
                .ToList();

            int total = DefDatabase<TerrainDef>.AllDefsListForReading.Count;
            if (unmapped.Count == 0)
            {
                Log.Message($"[Overlord] Terrain palette: all {total} loaded TerrainDefs map to a visible colour.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[Overlord] Terrain palette: {unmapped.Count}/{total} TerrainDefs render as byte 0 (near-black 'unknown') to viewers:");
            foreach (TerrainDef d in unmapped)
                sb.AppendLine($"    {d.defName}  ({d.modContentPack?.Name ?? "core"})");
            Log.Warning(sb.ToString());
        }

        private static string DescribeMap(Map map)
        {
            if (map == null)
                return "null";
            string kind = map.IsPlayerHome ? "home" : (map.IsPocketMap ? "pocket" : "other");
            return $"uid={map.uniqueID} {map.Size.x}x{map.Size.z} {kind}";
        }
    }
}
