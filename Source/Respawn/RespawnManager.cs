using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Handles viewer respawn requests. Called from Building_RespawnPortal.
    /// A viewer spends one ticket to spawn a new colonist at the portal location.
    /// </summary>
    public static class RespawnManager
    {
        // Per-viewer cooldown tracker: username -> absolute tick when cooldown expires
        private static readonly Dictionary<string, int> cooldowns = new Dictionary<string, int>();

        /// <summary>
        /// Attempt to respawn a viewer's colonist at the given portal.
        /// Returns a human-readable result message.
        /// </summary>
        public static string TryRespawn(string username, Building portal)
        {
            var vm = OverlordGameComponent.Instance?.Viewers;
            if (vm == null) return "Overlord not active.";

            var session = vm.GetSession(username);
            if (session == null) return "Viewer not connected.";

            if (session.OwnsPawn) return "You already control a colonist.";

            if (vm.TryAssignExistingPawnForViewer(username, out Pawn existingPawn))
            {
                vm.SendColonistList(username);
                OverlordGameComponent.Instance?.HandleRequestStatePublic(username);
                return $"Reconnected to existing colonist {existingPawn.LabelShort}.";
            }

            if (session.tickets <= 0) return "No respawn tickets remaining.";

            int cooldownTicks = OverlordMod.Settings?.respawnCooldownTicks ?? 2500;
            if (cooldowns.TryGetValue(username, out int expiry) && Find.TickManager.TicksGame < expiry)
            {
                int remaining = expiry - Find.TickManager.TicksGame;
                return $"Respawn on cooldown ({remaining / 60f:F0}s remaining).";
            }

            var map = portal?.Map;
            if (map == null) return "Portal has no map.";

            // Find a valid spawn cell near the portal
            IntVec3 spawnCell = CellFinder.RandomClosewalkCellNear(portal.Position, map, 3);
            if (!spawnCell.IsValid) return "No valid spawn location.";

            try
            {
                // Generate a new colonist
                var request = new PawnGenerationRequest(
                    PawnKindDefOf.Colonist,
                    Faction.OfPlayer,
                    PawnGenerationContext.NonPlayer,
                    developmentalStages: DevelopmentalStage.Adult
                );
                Pawn newPawn = PawnGenerator.GeneratePawn(request);

                // Spawn at portal
                GenSpawn.Spawn(newPawn, spawnCell, map, Rot4.South, WipeMode.Vanish);

                // Assign to viewer
                vm.AssignPawn(username, newPawn);
                session.tickets--;
                cooldowns[username] = Find.TickManager.TicksGame + cooldownTicks;

                LogUtil.Log($"Viewer {username} respawned as {newPawn.LabelShort} at {spawnCell}");

                // Notify viewer
                var comp = OverlordGameComponent.Instance;
                if (comp != null)
                {
                    vm.SendColonistList(username);
                    comp.HandleRequestStatePublic(username);
                }

                Messages.Message(
                    $"[Overlord] {session.displayName ?? username} respawned as {newPawn.LabelShort}.",
                    newPawn,
                    MessageTypeDefOf.NeutralEvent,
                    historical: false
                );

                return $"Respawned as {newPawn.LabelShort}. Tickets remaining: {session.tickets}.";
            }
            catch (System.Exception ex)
            {
                LogUtil.Error($"Respawn failed for {username}: {ex.Message}");
                return "Respawn failed (internal error).";
            }
        }

        /// <summary>
        /// Returns list of viewers who can currently respawn (no pawn, have tickets, off cooldown).
        /// </summary>
        public static List<ViewerSession> GetEligibleViewers()
        {
            var vm = OverlordGameComponent.Instance?.Viewers;
            if (vm == null) return new List<ViewerSession>();

            int now = Find.TickManager.TicksGame;
            return vm.AllSessions
                .Where(s => !s.OwnsPawn && s.tickets > 0 &&
                            (!cooldowns.TryGetValue(s.username, out int exp) || now >= exp))
                .ToList();
        }

        public static void ClearCooldowns()
        {
            cooldowns.Clear();
        }
    }
}
