using Verse;
using Verse.AI;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Standing order: a viewer picks a preferred weapon defName; whenever a
    /// matching weapon becomes available in the colony (stockpile / on ground,
    /// unclaimed, reachable) and the pawn isn't already holding one, the pawn goes
    /// and equips it. "Grab if available, keep current otherwise" — it never
    /// disarms a pawn that already has the preferred weapon, never buys anything,
    /// and never fights the streamer.
    ///
    /// Runs from the existing per-pawn sweep in ViewerManager.Tick (no new hot
    /// loop). Throttled per session so an unreachable/claimed weapon isn't
    /// re-attempted every sweep.
    /// </summary>
    public static class PreferredWeaponController
    {
        // Minimum ticks between auto-equip attempts per pawn (~4s at 60 tps).
        private const int RetryIntervalTicks = 240;

        public static void Evaluate(ViewerSession session, Pawn pawn, int now)
        {
            if (session == null || string.IsNullOrEmpty(session.preferredWeaponDef))
                return;
            if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned || pawn.Downed)
                return;

            // Already holding the preferred weapon → nothing to do (and cheap to check
            // every sweep, so no throttle needed for the satisfied case).
            var primary = pawn.equipment?.Primary;
            if (primary != null && primary.def?.defName == session.preferredWeaponDef)
                return;

            // Throttle the search/equip attempt itself.
            if (now - session.lastPreferredWeaponTick < RetryIntervalTicks)
                return;
            session.lastPreferredWeaponTick = now;

            // A viewer-issued manual order should not be interrupted by the standing
            // order — only act when the pawn is idle or doing background work.
            if (pawn.Drafted)
                return;

            Thing weapon = ArmoryCatalog.FindEquippableWeaponOfDef(pawn, session.preferredWeaponDef);
            if (weapon == null)
                return;

            if (!ArmoryCatalog.TryClaimEquipTarget(pawn, weapon, out _))
                return;

            // Weapons only, via JobDefOf.Equip (never the apparel Wear path).
            if (!(weapon is ThingWithComps) || !weapon.def.IsWeapon)
            {
                ArmoryCatalog.ReleaseEquipClaim(pawn, weapon);
                return;
            }

            Job job = JobMaker.MakeJob(JobDefOf.Equip, weapon);
            if (!pawn.jobs.TryTakeOrderedJob(job))
            {
                ArmoryCatalog.ReleaseEquipClaim(pawn, weapon);
                return;
            }

            LogUtil.Log($"Preferred-weapon: {pawn.LabelShort} auto-equipping {weapon.LabelShort} for {session.username}");
        }
    }
}
