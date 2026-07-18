using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Overlord
{
    /// <summary>
    /// Builds bounded, on-demand views of weapons and apparel in valid colony
    /// storage. The catalog is intentionally separate from pawn_state: it can be
    /// searched without making every pawn sync scan and resend the whole armory.
    /// </summary>
    public static class ArmoryCatalog
    {
        private const float NearbyEquipmentRadius = 24f;
        private const int DefaultPageSize = 3;
        private const int MaxPageSize = 12;
        private const float EquipClaimSeconds = 12f;

        private sealed class EquipClaim
        {
            public int pawnId;
            public string pawnLabel;
            public float expiresAt;
        }

        private static readonly Dictionary<int, EquipClaim> equipClaims =
            new Dictionary<int, EquipClaim>();

        public static Dictionary<string, object> BuildResponse(
            Pawn pawn,
            int requestId,
            string search,
            string slot,
            string sort,
            int page,
            int pageSize)
        {
            string normalizedSearch = (search ?? "").Trim();
            string normalizedSlot = NormalizeSlot(slot);
            string normalizedSort = NormalizeSort(sort);
            int boundedPageSize = Math.Max(1, Math.Min(MaxPageSize,
                pageSize > 0 ? pageSize : DefaultPageSize));

            var response = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ArmoryState,
                ["ok"] = false,
                ["requestId"] = requestId,
                ["search"] = normalizedSearch,
                ["slot"] = normalizedSlot,
                ["sort"] = normalizedSort,
                ["page"] = 0,
                ["pageSize"] = boundedPageSize,
                ["pageCount"] = 1,
                ["total"] = 0,
                ["items"] = new List<Dictionary<string, object>>()
            };

            Map map = pawn?.Map;
            if (pawn == null || map == null || !pawn.Spawned || pawn.Dead || pawn.Destroyed)
            {
                response["message"] = "Assigned colonist is not available on this map";
                return response;
            }

            var candidates = EnumerateStoredEquipment(map)
                .Where(thing => normalizedSlot == "all" ||
                    PawnStateSerializer.GearSlotKeyForDef(thing.def) == normalizedSlot)
                .Where(thing => string.IsNullOrEmpty(normalizedSearch) ||
                    MatchesSearch(thing, normalizedSearch));

            var ordered = Order(candidates, pawn, normalizedSort).ToList();
            int total = ordered.Count;
            int pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)boundedPageSize));
            int boundedPage = Math.Max(0, Math.Min(pageCount - 1, page));
            var visible = ordered
                .Skip(boundedPage * boundedPageSize)
                .Take(boundedPageSize)
                .Select(thing => BuildItem(pawn, thing))
                .ToList();

            response["ok"] = true;
            response["page"] = boundedPage;
            response["pageCount"] = pageCount;
            response["total"] = total;
            response["items"] = visible;
            return response;
        }

        public static bool ValidateEquipTarget(Pawn pawn, Thing thing, out string error)
        {
            error = null;
            if (pawn?.Map == null || !pawn.Spawned)
            {
                error = "Pawn not on a map";
                return false;
            }
            if (thing == null || thing.Destroyed || !thing.Spawned || thing.Map != pawn.Map)
            {
                error = "Item is no longer on this map";
                return false;
            }
            if (!(thing is Apparel) && !(thing.def?.IsWeapon ?? false))
            {
                error = $"{thing.LabelShort ?? "Item"} cannot be equipped";
                return false;
            }

            bool nearby = (thing.Position - pawn.Position).LengthHorizontalSquared <=
                NearbyEquipmentRadius * NearbyEquipmentRadius;
            bool stored = false;
            try
            {
                stored = StoreUtility.IsInValidStorage(thing);
            }
            catch
            {
                stored = false;
            }
            if (!nearby && !stored)
            {
                error = "Item is not nearby or in valid colony storage";
                return false;
            }

            string blocked = GetAvailabilityBlock(pawn, thing);
            if (!string.IsNullOrEmpty(blocked))
            {
                error = blocked;
                return false;
            }
            return true;
        }

        public static bool TryClaimEquipTarget(Pawn pawn, Thing thing, out string error)
        {
            error = null;
            if (pawn == null || thing == null)
            {
                error = "Pawn or item is unavailable";
                return false;
            }

            float now = Time.realtimeSinceStartup;
            PruneClaims(now);
            if (equipClaims.TryGetValue(thing.thingIDNumber, out EquipClaim existing) &&
                existing.pawnId != pawn.thingIDNumber)
            {
                error = $"Already being collected by {existing.pawnLabel ?? "another colonist"}";
                return false;
            }

            equipClaims[thing.thingIDNumber] = new EquipClaim
            {
                pawnId = pawn.thingIDNumber,
                pawnLabel = pawn.LabelShort ?? "another colonist",
                expiresAt = now + EquipClaimSeconds
            };
            return true;
        }

        public static void ReleaseEquipClaim(Pawn pawn, Thing thing)
        {
            if (pawn == null || thing == null)
                return;
            if (equipClaims.TryGetValue(thing.thingIDNumber, out EquipClaim existing) &&
                existing.pawnId == pawn.thingIDNumber)
            {
                equipClaims.Remove(thing.thingIDNumber);
            }
        }

        public static void ClearClaims()
        {
            equipClaims.Clear();
        }

        private static IEnumerable<Thing> EnumerateStoredEquipment(Map map)
        {
            var seen = new HashSet<int>();
            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
            {
                if (IsStoredCandidate(thing) && seen.Add(thing.thingIDNumber))
                    yield return thing;
            }
            foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
            {
                if (IsStoredCandidate(thing) && seen.Add(thing.thingIDNumber))
                    yield return thing;
            }
        }

        /// <summary>
        /// Finds a spawned weapon of the given def that this pawn could equip right
        /// now (validates + not claimed by another pawn), preferring the closest.
        /// Used by the preferred-weapon standing order. Returns null if none available.
        /// Scans ThingsInGroup(Weapon) (bounded) — safe on the ~2s per-pawn sweep.
        /// </summary>
        public static Thing FindEquippableWeaponOfDef(Pawn pawn, string weaponDefName)
        {
            if (pawn?.Map == null || !pawn.Spawned || string.IsNullOrEmpty(weaponDefName))
                return null;

            Thing best = null;
            float bestDistSq = float.MaxValue;
            foreach (Thing thing in pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
            {
                if (thing?.def == null || thing.def.defName != weaponDefName)
                    continue;
                if (thing.Destroyed || !thing.Spawned)
                    continue;
                // Skip weapons another pawn already holds/carries.
                if (thing.ParentHolder is Pawn_EquipmentTracker || thing.ParentHolder is Pawn_InventoryTracker)
                    continue;
                if (!ValidateEquipTarget(pawn, thing, out _))
                    continue;
                // Respect an active equip claim by a different pawn.
                if (equipClaims.TryGetValue(thing.thingIDNumber, out EquipClaim claim) &&
                    claim.pawnId != pawn.thingIDNumber && claim.expiresAt > Time.realtimeSinceStartup)
                    continue;

                float d = (thing.Position - pawn.Position).LengthHorizontalSquared;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = thing;
                }
            }
            return best;
        }

        private static bool IsStoredCandidate(Thing thing)
        {
            if (thing?.def == null || thing.Destroyed || !thing.Spawned)
                return false;
            if (!(thing is Apparel) && !thing.def.IsWeapon)
                return false;
            try
            {
                return StoreUtility.IsInValidStorage(thing);
            }
            catch
            {
                return false;
            }
        }

        private static bool MatchesSearch(Thing thing, string search)
        {
            return (thing.LabelCap ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (thing.def?.defName ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IOrderedEnumerable<Thing> Order(
            IEnumerable<Thing> candidates,
            Pawn pawn,
            string sort)
        {
            switch (sort)
            {
                case "quality":
                    return candidates.OrderByDescending(QualityRank)
                        .ThenBy(thing => DistanceFrom(pawn, thing))
                        .ThenBy(thing => thing.LabelCap ?? "");
                case "value":
                    return candidates.OrderByDescending(thing => thing.MarketValue)
                        .ThenBy(thing => DistanceFrom(pawn, thing))
                        .ThenBy(thing => thing.LabelCap ?? "");
                case "condition":
                    return candidates.OrderByDescending(ConditionPercent)
                        .ThenBy(thing => DistanceFrom(pawn, thing))
                        .ThenBy(thing => thing.LabelCap ?? "");
                case "alpha":
                    return candidates.OrderBy(thing => thing.LabelCap ?? "")
                        .ThenBy(thing => DistanceFrom(pawn, thing));
                default:
                    return candidates.OrderBy(thing => DistanceFrom(pawn, thing))
                        .ThenBy(thing => thing.LabelCap ?? "");
            }
        }

        private static Dictionary<string, object> BuildItem(Pawn pawn, Thing thing)
        {
            CompQuality qualityComp = thing.TryGetComp<CompQuality>();
            string blocked = GetAvailabilityBlock(pawn, thing);
            return new Dictionary<string, object>
            {
                ["id"] = thing.thingIDNumber,
                ["label"] = thing.LabelCap ?? thing.def.defName,
                ["defName"] = thing.def.defName,
                ["type"] = thing.def.IsWeapon ? "weapon" : "apparel",
                ["slotKey"] = PawnStateSerializer.GearSlotKeyForDef(thing.def),
                ["hp"] = ConditionPercent(thing),
                ["distance"] = DistanceFrom(pawn, thing),
                ["marketValue"] = (int)thing.MarketValue,
                ["quality"] = qualityComp != null ? qualityComp.Quality.GetLabel() : "",
                ["qualityRank"] = qualityComp != null ? (int)qualityComp.Quality : -1,
                ["available"] = string.IsNullOrEmpty(blocked),
                ["blockedReason"] = blocked ?? ""
            };
        }

        private static string GetAvailabilityBlock(Pawn pawn, Thing thing)
        {
            try
            {
                if (thing.IsForbidden(pawn))
                    return "Forbidden by the colony";
                if (!pawn.CanReserveAndReach(thing, PathEndMode.Touch, Danger.Deadly))
                    return "Reserved or unreachable";
            }
            catch
            {
                return "Availability changed";
            }
            return "";
        }

        private static int ConditionPercent(Thing thing)
        {
            return thing?.MaxHitPoints > 0
                ? (int)((float)thing.HitPoints / thing.MaxHitPoints * 100f)
                : 100;
        }

        private static int QualityRank(Thing thing)
        {
            CompQuality comp = thing?.TryGetComp<CompQuality>();
            return comp != null ? (int)comp.Quality : -1;
        }

        private static int DistanceFrom(Pawn pawn, Thing thing)
        {
            return pawn != null && thing != null
                ? (int)thing.Position.DistanceTo(pawn.Position)
                : int.MaxValue;
        }

        private static string NormalizeSlot(string slot)
        {
            string value = (slot ?? "all").Trim().ToLowerInvariant();
            switch (value)
            {
                case "weapon":
                case "head":
                case "outer":
                case "torso":
                case "hands":
                case "legs":
                case "other":
                    return value;
                default:
                    return "all";
            }
        }

        private static string NormalizeSort(string sort)
        {
            string value = (sort ?? "distance").Trim().ToLowerInvariant();
            switch (value)
            {
                case "quality":
                case "value":
                case "condition":
                case "alpha":
                    return value;
                default:
                    return "distance";
            }
        }

        private static void PruneClaims(float now)
        {
            foreach (int thingId in equipClaims
                .Where(pair => pair.Value == null || pair.Value.expiresAt <= now)
                .Select(pair => pair.Key)
                .ToList())
            {
                equipClaims.Remove(thingId);
            }
        }
    }
}
