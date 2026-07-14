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

                // "Back to life at full health": resurrection can leave the injuries
                // and illnesses that killed the pawn — clear them.
                try { FullHeal(pawn); } catch { }

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

        /// <summary>
        /// Restores a living pawn to full health: heals injuries, cures diseases and
        /// other harmful conditions, regrows missing body parts, and clears blood
        /// loss. Deliberately NEVER removes non-harmful hediffs (bionics, implants,
        /// beneficial states) or item-uncurable states (Anomaly metalhorror/inhumanized
        /// are fought out, not wiped). Returns a short summary.
        /// </summary>
        public static string FullHeal(Pawn pawn)
        {
            if (pawn == null) return "No pawn.";
            if (pawn.Dead) return $"{pawn.LabelShort} is dead — use revive.";
            if (pawn.health?.hediffSet == null) return $"{pawn.LabelShort} has no health tracker.";

            int cured = 0;
            try
            {
                // Regrow missing parts first (restores all child parts too).
                var missing = pawn.health.hediffSet.GetMissingPartsCommonAncestors()?.ToList();
                if (missing != null)
                {
                    foreach (var part in missing)
                    {
                        if (part?.Part == null) continue;
                        pawn.health.RestorePart(part.Part);
                        cured++;
                    }
                }

                // Remove injuries and harmful conditions. isBad excludes bionics and
                // implants (they are beneficial), so upgrades are never stripped.
                foreach (var hediff in pawn.health.hediffSet.hediffs.ToList())
                {
                    if (hediff?.def == null) continue;
                    if (hediff is Hediff_Injury)
                    {
                        pawn.health.RemoveHediff(hediff);
                        cured++;
                        continue;
                    }
                    if (hediff.def.isBad != true) continue;         // keep bionics/beneficial
                    // Don't strip states the game itself never lets you item-cure —
                    // e.g. Anomaly's MetalhorrorImplant / Inhumanized default isBad=true
                    // but are meant to be fought out, not wiped by a heal button.
                    if (hediff.def.everCurableByItem == false) continue;
                    pawn.health.RemoveHediff(hediff);
                    cured++;
                }
            }
            catch (System.Exception ex)
            {
                LogUtil.Error($"FullHeal failed for {pawn.LabelShort}: {ex.Message}");
                return $"Heal failed: {ex.Message}";
            }

            if (cured == 0)
                return $"{pawn.LabelShort} is already healthy.";

            LogUtil.Log($"Host healed {pawn.LabelShort}: {cured} condition{(cured == 1 ? "" : "s")} cleared");
            ActionLog.Append(ActionLogKind.Assignment, "host", "heal",
                $"Healed {pawn.LabelShort} ({cured} conditions)", pawn.thingIDNumber);
            Messages.Message($"[Overlord] Healed {pawn.LabelShort}.", pawn, MessageTypeDefOf.PositiveEvent, historical: false);
            return $"Healed {pawn.LabelShort}: {cured} condition{(cured == 1 ? "" : "s")} cleared.";
        }

        // Anomaly cube-obsession hediffs. Looked up by name (not HediffDefOf) so a
        // save without the Anomaly DLC never NREs — the defs simply don't exist and
        // the cure no-ops. CubeComa is a timed DisappearsDisableable coma from being
        // severed from the golden cube; removing it ends the coma and it does NOT
        // re-trigger. CubeInterest is the stateful obsession — zero its severity
        // (don't remove: keeps the cube's tracking intact) so the pawn is no longer
        // obsessed and won't be pulled back into the coma cycle.
        private static readonly string[] CubeBadHediffs = { "CubeComa", "CubeWithdrawal", "CubeRage" };

        public static bool HasCubeComa(Pawn pawn)
        {
            var def = DefDatabase<HediffDef>.GetNamedSilentFail("CubeComa");
            return def != null && pawn?.health?.hediffSet?.GetFirstHediffOfDef(def) != null;
        }

        public static string CureCubeObsession(Pawn pawn)
        {
            if (pawn == null) return "No pawn.";
            if (pawn.health?.hediffSet == null) return $"{pawn.LabelShort} has no health tracker.";

            int cleared = 0;
            try
            {
                foreach (var name in CubeBadHediffs)
                {
                    var def = DefDatabase<HediffDef>.GetNamedSilentFail(name);
                    if (def == null) continue;
                    var h = pawn.health.hediffSet.GetFirstHediffOfDef(def);
                    if (h != null) { pawn.health.RemoveHediff(h); cleared++; }
                }

                // Zero the obsession itself so coma can't recur — keep the hediff so
                // the golden cube's link tracking stays valid.
                var interestDef = DefDatabase<HediffDef>.GetNamedSilentFail("CubeInterest");
                if (interestDef != null)
                {
                    var interest = pawn.health.hediffSet.GetFirstHediffOfDef(interestDef);
                    if (interest != null && interest.Severity > 0.01f)
                    {
                        interest.Severity = 0f;
                        cleared++;
                    }
                }
            }
            catch (System.Exception ex)
            {
                LogUtil.Error($"CureCubeObsession failed for {pawn.LabelShort}: {ex.Message}");
                return $"Cure failed: {ex.Message}";
            }

            if (cleared == 0)
                return $"{pawn.LabelShort} has no cube coma / obsession.";

            LogUtil.Log($"Host cured {pawn.LabelShort} of cube obsession ({cleared})");
            ActionLog.Append(ActionLogKind.Assignment, "host", "cure_cube",
                $"Cured {pawn.LabelShort} of cube coma", pawn.thingIDNumber);
            Messages.Message($"[Overlord] Cured {pawn.LabelShort} of cube coma.", pawn, MessageTypeDefOf.PositiveEvent, historical: false);
            return $"Cured {pawn.LabelShort} of cube coma.";
        }

        public static int CureAllCubeComa(Map map = null)
        {
            map = map ?? Find.CurrentMap;
            var def = DefDatabase<HediffDef>.GetNamedSilentFail("CubeComa");
            if (def == null || map?.mapPawns?.FreeColonists == null) return 0;
            int n = 0;
            foreach (var p in map.mapPawns.FreeColonists.ToList())
            {
                if (p?.health?.hediffSet?.GetFirstHediffOfDef(def) != null)
                {
                    CureCubeObsession(p);
                    n++;
                }
            }
            return n;
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
