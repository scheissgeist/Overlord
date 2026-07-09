using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Manages viewer votes. Streamer creates a vote from the Overlord tab,
    /// viewers vote from their browser, results broadcast live.
    /// </summary>
    public class VoteManager
    {
        public string question;
        public List<string> options = new List<string>();
        public Dictionary<string, int> votes = new Dictionary<string, int>(); // username -> option index
        public bool active;

        public void StartVote(string q, List<string> opts)
        {
            question = q;
            options = opts ?? new List<string>();
            votes.Clear();
            active = true;
            BroadcastState();
            LogUtil.Log($"Vote started: {q}");
        }

        public void CastVote(string username, int optionIndex)
        {
            if (!active || optionIndex < 0 || optionIndex >= options.Count) return;
            votes[username] = optionIndex;
            BroadcastState();
        }

        public void EndVote()
        {
            active = false;
            BroadcastState();

            if (options.Count > 0)
            {
                var counts = GetCounts();
                int maxVotes = counts.Max();
                int winnerIdx = counts.ToList().IndexOf(maxVotes);
                string winner = options[winnerIdx];
                Messages.Message(
                    $"[Overlord] Vote result: \"{winner}\" wins with {maxVotes} vote{(maxVotes != 1 ? "s" : "")}",
                    MessageTypeDefOf.NeutralEvent, historical: false
                );
                LogUtil.Log($"Vote ended: {winner} ({maxVotes} votes)");
            }

            question = null;
            options.Clear();
            votes.Clear();
        }

        public int[] GetCounts()
        {
            var counts = new int[options.Count];
            foreach (var kvp in votes)
            {
                if (kvp.Value >= 0 && kvp.Value < counts.Length)
                    counts[kvp.Value]++;
            }
            return counts;
        }

        public void BroadcastState()
        {
            var comp = OverlordGameComponent.Instance;
            if (comp == null) return;

            var counts = active ? GetCounts() : new int[0];
            var optList = new List<object>();
            for (int i = 0; i < options.Count; i++)
            {
                optList.Add(new Dictionary<string, object>
                {
                    ["label"] = options[i],
                    ["votes"] = i < counts.Length ? counts[i] : 0
                });
            }

            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.VoteUpdate,
                ["active"] = active,
                ["question"] = question ?? "",
                ["options"] = optList
            };

            comp.Relay?.Broadcast(msg);
            comp.EmbeddedServer?.Broadcast(JsonHelper.ToJson(msg));
        }
    }
}
