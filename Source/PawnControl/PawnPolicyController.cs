using System.Linq;
using RimWorld;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Applies policy changes to pawns: schedule, outfit, drug, food, area.
    /// Version-sensitive API calls are routed through RimWorldCompat.
    /// </summary>
    public static class PawnPolicyController
    {
        public static bool SetWorkPriority(Pawn pawn, string workDefName, int priority)
        {
            if (pawn?.workSettings == null)
                return false;

            var workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workDefName);
            if (workType == null)
                return false;

            if (pawn.WorkTypeIsDisabled(workType))
                return false;

            // Clamp to valid range (0 = disabled, 1-4 = priority)
            priority = UnityEngine.Mathf.Clamp(priority, 0, 4);
            return RimWorldCompat.TrySetWorkPriority(pawn, workType, priority);
        }

        public static bool SetSchedule(Pawn pawn, int hour, string assignmentDefName)
        {
            if (pawn?.timetable == null)
                return false;

            if (hour < 0 || hour > 23)
                return false;

            var assignment = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(assignmentDefName);
            if (assignment == null)
                return false;

            return RimWorldCompat.TrySetScheduleAssignment(pawn, hour, assignment);
        }

        public static bool SetOutfit(Pawn pawn, string policyLabel)
        {
            if (pawn?.outfits == null)
                return false;

            var policies = Current.Game?.outfitDatabase?.AllOutfits;
            if (policies == null)
                return false;

            var policy = policies.FirstOrDefault(p => p.label == policyLabel);
            if (policy == null)
                return false;

            return RimWorldCompat.TrySetOutfitPolicy(pawn, policy);
        }

        public static bool SetDrugPolicy(Pawn pawn, string policyLabel)
        {
            if (pawn?.drugs == null)
                return false;

            var policies = Current.Game?.drugPolicyDatabase?.AllPolicies;
            if (policies == null)
                return false;

            var policy = policies.FirstOrDefault(p => p.label == policyLabel);
            if (policy == null)
                return false;

            return RimWorldCompat.TrySetDrugPolicy(pawn, policy);
        }

        public static bool SetFoodPolicy(Pawn pawn, string policyLabel)
        {
            if (pawn?.foodRestriction == null)
                return false;

            var policies = Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions;
            if (policies == null)
                return false;

            var policy = policies.FirstOrDefault(p => p.label == policyLabel);
            if (policy == null)
                return false;

            return RimWorldCompat.TrySetFoodPolicy(pawn, policy);
        }

        public static bool SetArea(Pawn pawn, string areaLabel)
        {
            if (pawn?.playerSettings == null)
                return false;

            if (string.IsNullOrEmpty(areaLabel) || areaLabel == "Unrestricted")
                return RimWorldCompat.TrySetAreaRestriction(pawn, null);

            var map = pawn.Map;
            if (map == null)
                return false;

            var area = map.areaManager.AllAreas.FirstOrDefault(a => a.Label == areaLabel);
            if (area == null)
                return false;

            return RimWorldCompat.TrySetAreaRestriction(pawn, area);
        }
    }
}
