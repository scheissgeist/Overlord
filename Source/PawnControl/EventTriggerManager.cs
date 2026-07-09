using System.Collections.Generic;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Viewers can spend tickets to trigger in-game events.
    /// Each event has a configurable ticket cost.
    /// </summary>
    public static class EventTriggerManager
    {
        public static readonly Dictionary<string, int> EventCosts = new Dictionary<string, int>
        {
            ["raid_small"] = 2,
            ["drop_pod"] = 1,
            ["manhunter"] = 2,
            ["psychic_drone"] = 3,
            ["solar_flare"] = 1,
            ["short_circuit"] = 1,
            ["self_tame"] = 1,
            ["wanderer"] = 1,
        };

        public static string TryTrigger(string username, string eventId)
        {
            var vm = OverlordGameComponent.Instance?.Viewers;
            if (vm == null) return "Not active";

            var session = vm.GetSession(username);
            if (session == null) return "Not connected";

            if (!EventCosts.TryGetValue(eventId, out int cost))
                return $"Unknown event: {eventId}";

            if (session.tickets < cost)
                return $"Not enough tickets (need {cost}, have {session.tickets})";

            var map = Find.CurrentMap;
            if (map == null) return "No map";

            bool fired = false;
            fired = TryFireByDefName(eventId, map);

            if (!fired)
                return "Event could not fire (conditions not met)";

            session.tickets -= cost;
            LogUtil.Log($"Viewer {username} triggered {eventId} (cost {cost}, remaining {session.tickets})");

            // Broadcast to OBS overlay
            var comp = OverlordGameComponent.Instance;
            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.GameEvent,
                ["message"] = $"{session.displayName ?? username} triggered {FormatEventName(eventId)}!",
                ["event"] = eventId,
                ["username"] = username
            };
            comp?.Relay?.Broadcast(msg);
            comp?.EmbeddedServer?.Broadcast(JsonHelper.ToJson(msg));

            // Update ticket count
            var ticketMsg = new Dictionary<string, object>
            {
                ["type"] = "ticket_update",
                ["tickets"] = session.tickets
            };
            comp?.SendToViewerPublic(username, ticketMsg);

            return $"Triggered {FormatEventName(eventId)}! ({session.tickets} tickets remaining)";
        }

        private static readonly Dictionary<string, string> EventDefNames = new Dictionary<string, string>
        {
            ["raid_small"] = "RaidEnemy",
            ["drop_pod"] = "RefugeePodCrash",
            ["manhunter"] = "ManhunterPack",
            ["psychic_drone"] = "PsychicDrone",
            ["solar_flare"] = "SolarFlare",
            ["short_circuit"] = "ShortCircuit",
            ["self_tame"] = "SelfTame",
            ["wanderer"] = "WandererJoin",
        };

        private static bool TryFireByDefName(string eventId, Map map)
        {
            if (!EventDefNames.TryGetValue(eventId, out string defName)) return false;
            var def = DefDatabase<IncidentDef>.GetNamed(defName, errorOnFail: false);
            if (def == null) return false;
            float points = eventId == "raid_small" ? 200f : -1f;
            return TryFireIncident(def, map, points);
        }

        private static bool TryFireIncident(IncidentDef def, Map map, float points = -1f)
        {
            if (def == null) return false;
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            if (points > 0f) parms.points = points;
            return def.Worker.TryExecute(parms);
        }

        private static string FormatEventName(string id)
        {
            switch (id)
            {
                case "raid_small": return "a raid";
                case "drop_pod": return "a refugee pod";
                case "manhunter": return "a manhunter pack";
                case "psychic_drone": return "a psychic drone";
                case "solar_flare": return "a solar flare";
                case "short_circuit": return "a short circuit";
                case "self_tame": return "an animal self-tame";
                case "wanderer": return "a wanderer";
                default: return id;
            }
        }
    }
}
