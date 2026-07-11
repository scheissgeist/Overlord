using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Overlord
{
    /// <summary>
    /// Watchdog for viewer-controlled pawns that have gone inert for a reason the
    /// mod can't name — the general case behind the "collecting hats" incident,
    /// where a corrupted equipment tracker suppressed the pawn's tick and it sat
    /// uncontrollable for hours while everything LOOKED fine.
    ///
    /// The known cause is already auto-repaired (RepairEquipmentTracker). This
    /// monitor catches the SYMPTOM regardless of cause. Crucially it keys on JOB
    /// PROGRESS, not job presence: a tick-suppressed pawn most often FREEZES a
    /// valid non-null job (the driver throws mid-toil, RimWorld swallows the repeat
    /// exception, the job never advances), so "has a CurJob" is NOT health. The
    /// detector flags a pawn whose job id + cell haven't changed and that isn't
    /// moving, for a sustained window — that is the frozen signature, and it also
    /// covers the jobless-and-frozen variant.
    ///
    /// Pure classifier: per-pawn progress state lives on the ViewerSession.
    /// </summary>
    public static class PawnHealthMonitor
    {
        // ~8s at 1x (480 game ticks). Long enough that a real job legitimately
        // sitting in place briefly (a long crafting toil at a bench) is handled by
        // the id-change check below rather than the timer, and short enough that a
        // frozen pawn is caught fast.
        public const int StuckThresholdTicks = 480;

        public enum Verdict { Healthy, Progressing, FrozenCandidate, Alerting }

        /// <summary>
        /// Advances the watchdog for one pawn using the session's stored progress
        /// state, mutating it. Returns whether the streamer should be alerted THIS
        /// cycle (i.e. the frozen episode just crossed the threshold and hasn't been
        /// alerted yet). Never alerts for legitimately-inactive pawns.
        /// </summary>
        public static bool Evaluate(ViewerSession session, Pawn pawn, int now)
        {
            if (session == null)
                return false;

            if (!CanBeStuck(pawn))
            {
                ResetProgress(session);
                return false;
            }

            // Progress fingerprint: job instance id + cell + the driver's toil
            // countdown. A healthy pawn either moves (cell changes), finishes toils
            // (job id changes), is pathing (Moving), OR is doing a stationary job
            // whose driver is actively ticking (ticksLeftThisToil decrements —
            // crafting, research). A tick-frozen pawn shows NONE of these.
            int jobId = pawn.CurJob?.loadID ?? -1;
            IntVec3 cell = pawn.Position;
            bool moving = pawn.pather != null && pawn.pather.Moving;
            int toilTicks = pawn.jobs?.curDriver?.ticksLeftThisToil ?? int.MinValue;

            bool progressed =
                moving ||
                jobId != session.watchdogLastJobId ||
                cell != session.watchdogLastCell ||
                toilTicks != session.watchdogLastToilTicks;

            session.watchdogLastJobId = jobId;
            session.watchdogLastCell = cell;
            session.watchdogLastToilTicks = toilTicks;

            if (progressed)
            {
                ResetProgress(session, keepFingerprint: true);
                return false;
            }

            // No progress since last sample. Start / continue the stuck timer.
            if (session.stuckSinceTick < 0)
                session.stuckSinceTick = now;

            if (now - session.stuckSinceTick >= StuckThresholdTicks && !session.stuckAlertRaised)
            {
                session.stuckAlertRaised = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a pawn is even a candidate for "stuck" — i.e. it SHOULD be doing
        /// something. Excludes every legitimate reason to be still so the watchdog
        /// never cries wolf on a normally-inactive pawn.
        /// </summary>
        private static bool CanBeStuck(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned)
                return false;
            if (pawn.jobs == null)
                return true; // no job tracker at all — genuinely broken

            if (pawn.Downed) return false;
            if (pawn.InMentalState) return false;
            if (pawn.InBed()) return false;
            if (pawn.needs?.rest != null && !pawn.Awake()) return false; // sleeping
            // Lord-directed (rituals, parties, caravan muster, quests): behavior is
            // externally scripted and transitional null/held jobs are valid.
            if (pawn.GetLord() != null) return false;
            // Guests / prisoners aren't the viewer-control target and idle by design.
            if (pawn.HostFaction != null || pawn.IsPrisoner) return false;

            return true;
        }

        private static void ResetProgress(ViewerSession session, bool keepFingerprint = false)
        {
            session.stuckSinceTick = -1;
            session.stuckAlertRaised = false;
            if (!keepFingerprint)
            {
                session.watchdogLastJobId = -1;
                session.watchdogLastCell = IntVec3.Invalid;
                session.watchdogLastToilTicks = int.MinValue;
            }
        }
    }
}
