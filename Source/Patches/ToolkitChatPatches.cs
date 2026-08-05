using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Overlord
{
    /// <summary>
    /// Optional, reflection-only ToolkitCore hook. Overlord still loads normally
    /// when ToolkitCore is absent.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Toolkit_RepairGearChat
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return AccessTools.TypeByName("ToolkitCore.TwitchWrapper") != null;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("ToolkitCore.TwitchWrapper"), "OnChatCommandReceived");
        }

        [HarmonyPrefix]
        static bool Prefix(object e)
        {
            return !TwitchToolkitBridge.TryQueueRepairChatCommand(e);
        }
    }

    /// <summary>
    /// Suppress Toolkit's chat replies for purchases that came from Overlord's UI.
    ///
    /// Overlord buys by synthesising a fake ITwitchMessage and calling
    /// Purchase_Handler.ResolvePurchase, so Toolkit believes the viewer typed
    /// "!buy x" in chat and answers IN CHAT — via TwitchWrapper.SendChatMessage,
    /// which posts to the streamer's own joined channel as the authenticated
    /// account (verified in ToolkitCore 1.6: Client.SendMessage(GetJoinedChannel(
    /// ToolkitCoreSettings.channel_username), message), no rate limit, no dedupe,
    /// no off switch).
    ///
    /// Toolkit 1.6 has at least 7 such branches on the purchase path — confirm,
    /// "not possible", not-enough-coins, maxed-from-karma, care-package cooldown,
    /// "is maxed, wait N days", and game-paused. A maxed item therefore emits the
    /// SAME line every time a viewer taps Buy, which is exactly what Twitch's
    /// duplicate-message and rate limits act on. It is the STREAMER's account that
    /// gets dinged, not the viewer's.
    ///
    /// The viewer already sees the outcome in the Overlord UI, so these messages
    /// are pure duplication. Suppressed only while an Overlord purchase is in
    /// flight — chat commands typed by real viewers still get their normal replies.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Toolkit_SuppressOverlordPurchaseChat
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return AccessTools.TypeByName("ToolkitCore.TwitchWrapper") != null;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("ToolkitCore.TwitchWrapper"), "SendChatMessage");
        }

        // ── Duplicate / flood guard ────────────────────────────────────────────
        // Live chat 2026-08-04 showed "@missrox Item is maxed, wait 13.4 days to
        // purchase." posted FOUR times as the streamer, right after a "Toolkit Core
        // has Connected to Chat" reconnect, while Overlord had run ZERO purchases
        // that session (30 commands, none a purchase) and the viewer was idle. So a
        // Toolkit-side path re-resolves a stale purchase on reconnect. We cannot
        // safely rewrite Toolkit's internals mid-stream, but every outgoing message
        // funnels through this one method — so the duplicate is stopped HERE.
        //
        // This matters beyond tidiness: identical repeated messages are exactly what
        // Twitch's duplicate-message and rate limits act on, and the account taking
        // the hit is the STREAMER's, not the viewer's.
        // THREAD SAFETY: this prefix does NOT run only on the game thread. ToolkitCore
        // 1.6 TwitchWrapper.OnChatCommandReceived wraps command handling in Task.Run,
        // so a chat-typed !buy reaches SendChatMessage — and therefore this method — on
        // a THREAD-POOL thread. Two consequences, both handled here:
        //   * the map must be locked; an unsynchronised Dictionary can corrupt or spin
        //     forever under concurrent writes.
        //   * the clock must NOT be UnityEngine.Time. Time.realtimeSinceStartup throws
        //     when touched off the main thread, and this is exactly a path that runs
        //     off it. DateTime.UtcNow is thread-safe and monotonic enough for a 60s
        //     duplicate window.
        private static readonly Dictionary<string, DateTime> recentSends =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(60);
        private const int RecentSendsCap = 128;

        private static bool IsDuplicate(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;
            DateTime now = DateTime.UtcNow;

            lock (recentSends)
            {
                if (recentSends.TryGetValue(message, out DateTime last) && now - last < DuplicateWindow)
                    return true;

                // Evict expired entries before inserting so the map cannot grow without
                // bound over a long stream. Only sweeps when it actually gets large.
                if (recentSends.Count >= RecentSendsCap)
                {
                    var stale = new List<string>();
                    foreach (var kv in recentSends)
                        if (now - kv.Value >= DuplicateWindow)
                            stale.Add(kv.Key);
                    foreach (var key in stale)
                        recentSends.Remove(key);
                    // Still full of live entries — drop it wholesale rather than leak.
                    if (recentSends.Count >= RecentSendsCap)
                        recentSends.Clear();
                }

                recentSends[message] = now;
                return false;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(string message)
        {
            // Keyed by the "@username" the message is addressed to, so one viewer's
            // Overlord purchase can no longer eat a DIFFERENT viewer's chat reply.
            bool suppressed = TwitchToolkitBridge.ShouldSuppressChat(message);

            // An exact repeat inside the window is dropped even when it did not come
            // from Overlord — the streamer's account is what is at risk either way.
            bool duplicate = !suppressed && IsDuplicate(message);
            if (duplicate)
            {
                LogUtil.Log($"ToolkitChat DUPLICATE-BLOCKED (repeat within {DuplicateWindow.TotalSeconds:F0}s): \"{message}\"");
                suppressed = true;
            }

            // INSTRUMENTATION. The streamer reports viewers "spamming buy requests"
            // in Twitch chat while those viewers say they are doing nothing, and
            // Overlord's own command log shows ZERO toolkit_purchase for the whole
            // session — so something OUTSIDE Overlord is posting as the streamer.
            // ToolkitCore.SendChatMessage does not log what it sends, which is why
            // Player.log could not answer this. This patch is the only place that
            // sees every outgoing message, so it records the text AND the managed
            // call stack — the stack names the actual caller (Toolkit incident,
            // ToolkitUtils, a coin/karma timer, our bridge) instead of guessing.
            // Off by default; enable with the "Log Toolkit chat sends" setting.
            if (OverlordMod.Settings?.logToolkitChatSends == true)
            {
                try
                {
                    // Trim to the frames above Harmony's own plumbing — those name
                    // the real caller. Full traces here would flood Player.log.
                    string caller = new System.Diagnostics.StackTrace(1, false).ToString();
                    string[] frames = caller.Split('\n');
                    var interesting = new System.Text.StringBuilder();
                    for (int i = 0; i < frames.Length && interesting.Length < 400; i++)
                    {
                        string f = frames[i].Trim();
                        if (f.Length == 0 || f.IndexOf("HarmonyLib", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        interesting.Append(f).Append(" | ");
                    }
                    LogUtil.Log($"ToolkitChat {(suppressed ? "SUPPRESSED" : "SENT")}: \"{message}\" <- {interesting}");
                }
                catch { }
            }

            // false = skip the original send.
            return !suppressed;
        }
    }
}
