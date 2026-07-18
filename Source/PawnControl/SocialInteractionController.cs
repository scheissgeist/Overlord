using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace Overlord
{
    /// <summary>
    /// Walk-to-target social interactions. Viewers' social buttons used to fire
    /// TryInteractWith directly and fail ~always ("Cannot interact right now")
    /// because RimWorld demands adjacency + availability at that instant. Now the
    /// command walks the pawn over (JobDefOf.Goto) and this controller fires the
    /// remembered interaction from the existing per-pawn sweep once in range.
    /// Pending intents are transient (not saved) and expire after ~30s.
    /// </summary>
    public static class SocialInteractionController
    {
        public const int PendingExpireTicks = 1800; // ~30s at 60tps
        private const float InteractRangeSq = 6f * 6f; // social chatter range

        /// <summary>Attempt the interaction right now if in range. Returns true when
        /// handled (success message set); failMsg is set only for hard failures that
        /// should NOT fall through to the walk (bad target state).</summary>
        public static bool TryInteractNow(Pawn pawn, Pawn target, InteractionDef interaction, out string doneMsg, out string failMsg)
        {
            doneMsg = null;
            failMsg = null;
            if ((pawn.Position - target.Position).LengthHorizontalSquared > InteractRangeSq)
                return false; // out of range — caller walks over

            try
            {
                if (!pawn.interactions.CanInteractNowWith(target, interaction))
                    return false; // in range but busy — let the sweep retry on arrival
                if (!pawn.interactions.TryInteractWith(target, interaction))
                    return false;
            }
            catch (System.Exception ex)
            {
                failMsg = "Interaction failed: " + ex.Message;
                return false;
            }

            doneMsg = $"{interaction.label ?? interaction.defName} with {target.LabelShort ?? "colonist"}";
            return true;
        }

        /// <summary>Called from the per-pawn sweep. Fires or expires a pending intent.</summary>
        public static void Resolve(ViewerSession session, Pawn pawn, int now)
        {
            if (session == null || session.pendingSocialTargetId < 0)
                return;

            if (now > session.pendingSocialExpireTick || pawn == null || pawn.Dead || pawn.Downed || pawn.Map == null)
            {
                if (now > session.pendingSocialExpireTick)
                    session.pendingLogEntries.Add("Social: gave up (couldn't reach them in time)");
                Clear(session);
                return;
            }

            Pawn target = pawn.Map.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p.thingIDNumber == session.pendingSocialTargetId);
            if (target == null || target.Dead)
            {
                session.pendingLogEntries.Add("Social: target is gone");
                Clear(session);
                return;
            }

            InteractionDef interaction = DefDatabase<InteractionDef>.GetNamedSilentFail(session.pendingSocialInteraction);
            if (interaction == null)
            {
                Clear(session);
                return;
            }

            if (TryInteractNow(pawn, target, interaction, out string doneMsg, out string failMsg))
            {
                session.pendingLogEntries.Add(doneMsg);
                Clear(session);
                return;
            }
            if (failMsg != null)
            {
                session.pendingLogEntries.Add(failMsg);
                Clear(session);
                return;
            }

            // In range but target busy → keep waiting until expiry. Out of range →
            // still walking; if the walk job got displaced, re-order it once per ~4s.
            if ((pawn.Position - target.Position).LengthHorizontalSquared > InteractRangeSq &&
                pawn.jobs?.curJob?.def != JobDefOf.Goto && now % 240 < 8)
            {
                var job = JobMaker.MakeJob(JobDefOf.Goto, target);
                job.locomotionUrgency = LocomotionUrgency.Jog;
                pawn.jobs.TryTakeOrderedJob(job);
            }
        }

        private static void Clear(ViewerSession session)
        {
            session.pendingSocialTargetId = -1;
            session.pendingSocialInteraction = null;
            session.pendingSocialExpireTick = -1;
        }
    }
}
