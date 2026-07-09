using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Host-side true resurrection for dead player colonists.
    /// Does not spend viewer tickets. Auto-reassigns waiting previous owners.
    /// </summary>
    public static class ReviveManager
    {
        public struct DeadColonistEntry
        {
            public Corpse corpse;
            public Pawn pawn;
            public string lastOwnerUsername;
            public string lastOwnerDisplayName;
        }

        public static List<DeadColonistEntry> GetDeadColonists(Map map = null)
        {
            var results = new List<DeadColonistEntry>();
            var maps = map != null
                ? new List<Map> { map }
                : (Find.Maps?.Where(m => m != null).ToList() ?? new List<Map>());

            var vm = OverlordGameComponent.Instance?.Viewers;
            var seen = new HashSet<int>();

            foreach (var m in maps)
            {
                var things = m.listerThings?.ThingsInGroup(ThingRequestGroup.Corpse);
                if (things == null) continue;

                foreach (var thing in things)
                {
                    var corpse = thing as Corpse;
                    if (corpse == null || corpse.Destroyed) continue;

                    var pawn = corpse.InnerPawn;
                    if (pawn == null || pawn.Destroyed) continue;
                    if (pawn.Faction != Faction.OfPlayer) continue;
                    if (!pawn.RaceProps.Humanlike) continue;
                    if (!seen.Add(pawn.thingIDNumber)) continue;

                    string owner = vm?.GetLastOwnerUsername(pawn.thingIDNumber);
                    string display = null;
                    if (!string.IsNullOrEmpty(owner))
                        display = vm.GetSession(owner)?.displayName ?? owner;

                    results.Add(new DeadColonistEntry
                    {
                        corpse = corpse,
                        pawn = pawn,
                        lastOwnerUsername = owner,
                        lastOwnerDisplayName = display
                    });
                }
            }

            return results.OrderBy(e => e.pawn.LabelShort ?? "").ToList();
        }

        public static int CountDeadColonists(Map map = null) => GetDeadColonists(map).Count;

        public static string TryRevive(Pawn pawn)
        {
            if (pawn == null) return "No pawn.";
            if (!pawn.Dead) return $"{pawn.LabelShort} is not dead.";

            Corpse corpse = pawn.Corpse;
            if (corpse == null || corpse.Destroyed)
                return $"No corpse found for {pawn.LabelShort}.";

            return TryReviveCorpse(corpse);
        }

        public static string TryReviveByThingId(int thingId)
        {
            foreach (var entry in GetDeadColonists())
            {
                if (entry.pawn != null && entry.pawn.thingIDNumber == thingId)
                    return TryReviveCorpse(entry.corpse);
                if (entry.corpse != null && entry.corpse.thingIDNumber == thingId)
                    return TryReviveCorpse(entry.corpse);
            }
            return $"No dead colonist with id {thingId}.";
        }

        public static string TryReviveCorpse(Corpse corpse)
        {
            if (corpse == null || corpse.Destroyed)
                return "Corpse is gone.";

            var pawn = corpse.InnerPawn;
            if (pawn == null)
                return "Corpse has no pawn.";
            if (pawn.Faction != Faction.OfPlayer)
                return $"{pawn.LabelShort} is not a colony pawn.";
            if (!pawn.Dead)
                return $"{pawn.LabelShort} is not dead.";

            var vm = OverlordGameComponent.Instance?.Viewers;
            string lastOwner = vm?.GetLastOwnerUsername(pawn.thingIDNumber);

            try
            {
                var parms = new ResurrectionParams
                {
                    restoreMissingParts = true,
                    removeDiedThoughts = true,
                    gettingScarsChance = 0f
                };

                if (!ResurrectionUtility.TryResurrect(pawn, parms))
                    return $"Failed to resurrect {pawn.LabelShort}.";

                if (pawn.Dead)
                    return $"Resurrection did not revive {pawn.LabelShort}.";

                // Ensure they are on the map and usable.
                if (!pawn.Spawned)
                {
                    Map map = corpse.Map ?? Find.CurrentMap;
                    IntVec3 cell = corpse.PositionHeld;
                    if (map != null && cell.IsValid)
                        GenSpawn.Spawn(pawn, cell, map, Rot4.South, WipeMode.Vanish);
                }

                vm?.ClearLastOwner(pawn.thingIDNumber);

                string assignNote = "";
                if (vm != null && !string.IsNullOrEmpty(lastOwner))
                {
                    var session = vm.GetSession(lastOwner);
                    if (session != null && session.isConnected && !session.HasPawn)
                    {
                        if (vm.AssignPawn(lastOwner, pawn))
                        {
                            assignNote = $" Reassigned to {session.displayName ?? lastOwner}.";
                            vm.SendColonistList();
                            OverlordGameComponent.Instance?.HandleRequestStatePublic(lastOwner);
                        }
                    }
                }

                if (string.IsNullOrEmpty(assignNote))
                    vm?.SendColonistList();

                LogUtil.Log($"Host revived {pawn.LabelShort} (id={pawn.thingIDNumber}){assignNote}");
                ActionLog.Append(ActionLogKind.Assignment, lastOwner ?? "host", "revive",
                    $"Revived {pawn.LabelShort}{assignNote}", pawn.thingIDNumber);

                Messages.Message(
                    $"[Overlord] Revived {pawn.LabelShort}.{assignNote}",
                    pawn,
                    MessageTypeDefOf.PositiveEvent,
                    historical: false
                );

                return $"Revived {pawn.LabelShort}.{assignNote}";
            }
            catch (System.Exception ex)
            {
                LogUtil.Error($"Revive failed for {pawn.LabelShort}: {ex.Message}");
                return $"Revive failed: {ex.Message}";
            }
        }

        public static string ReviveAllDeadColonists(Map map = null)
        {
            var dead = GetDeadColonists(map);
            if (dead.Count == 0)
                return "No dead colonists to revive.";

            int ok = 0;
            int fail = 0;
            foreach (var entry in dead.ToList())
            {
                string result = TryReviveCorpse(entry.corpse);
                if (result != null && result.StartsWith("Revived"))
                    ok++;
                else
                    fail++;
            }

            string summary = $"Revived {ok} colonist{(ok == 1 ? "" : "s")}.";
            if (fail > 0)
                summary += $" {fail} failed.";

            Messages.Message($"[Overlord] {summary}", MessageTypeDefOf.PositiveEvent, historical: false);
            return summary;
        }
    }
}
