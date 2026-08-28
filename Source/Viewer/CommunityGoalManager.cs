using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Save-authoritative, room-wide progress counter. Successful viewer actions
    /// advance it in the game; the relay only caches the latest rendered state.
    /// </summary>
    public class CommunityGoalManager : IExposable
    {
        public string title = "";
        public int target = 25;
        public int progress;
        public bool active;
        public bool completed;
        public int startedTick;
        public int completedTick;

        public void StartGoal(string requestedTitle, int requestedTarget)
        {
            title = (requestedTitle ?? "").Trim();
            if (title.Length == 0) title = "Community goal";
            if (title.Length > 80) title = title.Substring(0, 80);
            target = Math.Max(1, Math.Min(100000, requestedTarget));
            progress = 0;
            active = true;
            completed = false;
            startedTick = Find.TickManager?.TicksGame ?? 0;
            completedTick = 0;
            BroadcastState();
            ActionLog.Append(ActionLogKind.System, "host", "community_goal", $"Started {title} (0/{target})");
            LogUtil.Log($"Community goal started: {title} (target {target})");
        }

        public bool Advance(string username, string action)
        {
            if (!active) return false;
            progress = Math.Min(target, progress + 1);
            if (progress >= target)
            {
                active = false;
                completed = true;
                completedTick = Find.TickManager?.TicksGame ?? 0;
                Messages.Message($"[Overlord] Community goal complete: {title}", MessageTypeDefOf.PositiveEvent, historical: false);
                ActionLog.Append(ActionLogKind.System, username ?? "viewer", "community_goal_complete", title);
                LogUtil.Log($"Community goal complete: {title} ({target}/{target})");
            }
            BroadcastState(username, action);
            return true;
        }

        public void ClearGoal()
        {
            string previous = title;
            active = false;
            completed = false;
            title = "";
            progress = 0;
            target = 25;
            startedTick = 0;
            completedTick = 0;
            BroadcastState();
            if (!string.IsNullOrEmpty(previous))
                ActionLog.Append(ActionLogKind.System, "host", "community_goal_end", $"Ended {previous}");
        }

        public Dictionary<string, object> BuildMessage(string contributor = null, string action = null)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.CommunityGoal,
                ["active"] = active,
                ["completed"] = completed,
                ["title"] = title ?? "",
                ["progress"] = progress,
                ["required"] = Math.Max(1, target),
                ["startedTick"] = startedTick,
                ["completedTick"] = completedTick
            };
            if (!string.IsNullOrEmpty(contributor)) msg["contributor"] = contributor;
            if (!string.IsNullOrEmpty(action)) msg["action"] = action;
            return msg;
        }

        public void BroadcastState(string contributor = null, string action = null)
        {
            var comp = OverlordGameComponent.Instance;
            if (comp == null) return;
            var msg = BuildMessage(contributor, action);
            comp.Relay?.Broadcast(msg);
            comp.EmbeddedServer?.Broadcast(JsonHelper.ToJson(msg));
        }

        public void SendState(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            OverlordGameComponent.Instance?.SendToViewerPublic(username, BuildMessage());
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref title, "title", "");
            Scribe_Values.Look(ref target, "target", 25);
            Scribe_Values.Look(ref progress, "progress", 0);
            Scribe_Values.Look(ref active, "active", false);
            Scribe_Values.Look(ref completed, "completed", false);
            Scribe_Values.Look(ref startedTick, "startedTick", 0);
            Scribe_Values.Look(ref completedTick, "completedTick", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                target = Math.Max(1, Math.Min(100000, target));
                progress = Math.Max(0, Math.Min(target, progress));
                if (string.IsNullOrEmpty(title))
                {
                    active = false;
                    completed = false;
                }
            }
        }
    }
}
