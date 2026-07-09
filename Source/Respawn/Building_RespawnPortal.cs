using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Buildable respawn portal. Viewers with tickets can interact with this
    /// to spawn as a new colonist. The streamer can also manage viewers from here.
    /// Appears as a gizmo button when the building is selected.
    /// </summary>
    public class Building_RespawnPortal : Building
    {
        // Tick interval for checking if eligible viewers should be auto-notified
        private const int NotifyInterval = 300; // 5 seconds
        private int ticksSinceNotify;

        protected override void Tick()
        {
            base.Tick();

            ticksSinceNotify++;
            if (ticksSinceNotify < NotifyInterval) return;
            ticksSinceNotify = 0;

            // Notify eligible viewers that the portal is available
            var eligible = RespawnManager.GetEligibleViewers();
            if (eligible.Count == 0) return;

            var comp = OverlordGameComponent.Instance;
            if (comp == null) return;

            var msg = new Dictionary<string, object>
            {
                ["type"]     = "respawn_portal",
                ["portalId"] = this.thingIDNumber,
                ["available"] = true
            };

            foreach (var session in eligible)
            {
                comp.SendToViewerPublic(session.username, msg);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;

            // Streamer-only: open viewer management dialog
            yield return new Command_Action
            {
                defaultLabel = "Manage Viewers",
                defaultDesc  = "Open the Overlord viewer assignment panel.",
                icon         = TexButton.Paste,
                action       = () => Find.WindowStack.Add(new AssignmentDialog()),
            };

            // Trigger eligible viewer respawns from in-game
            var eligible = RespawnManager.GetEligibleViewers();
            if (eligible.Count > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = $"Respawn Viewers ({eligible.Count})",
                    defaultDesc  = "Respawn all connected viewers who are waiting and have tickets.",
                    icon         = TexButton.SpeedButtonTextures[1],
                    action       = () =>
                    {
                        foreach (var session in eligible)
                            RespawnManager.TryRespawn(session.username, this);
                    },
                };
            }

            int deadCount = ReviveManager.CountDeadColonists(Map);
            if (deadCount > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = $"Revive Dead ({deadCount})",
                    defaultDesc  = "Truly resurrect all dead colony pawns. Waiting previous owners are reassigned.",
                    icon         = TexButton.ReorderUp,
                    action       = () =>
                    {
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            $"Revive all {deadCount} dead colonist{(deadCount == 1 ? "" : "s")} on this map?",
                            () => ReviveManager.ReviveAllDeadColonists(Map),
                            false));
                    },
                };
            }
        }

        public override string GetInspectString()
        {
            var base_ = base.GetInspectString();
            var eligible = RespawnManager.GetEligibleViewers();
            string status = eligible.Count > 0
                ? $"{eligible.Count} viewer(s) waiting to respawn"
                : "No viewers waiting";
            return string.IsNullOrEmpty(base_) ? status : base_ + "\n" + status;
        }
    }
}
