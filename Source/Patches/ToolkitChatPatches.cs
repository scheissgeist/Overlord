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

        [HarmonyPrefix]
        static bool Prefix(string message)
        {
            bool suppressed = TwitchToolkitBridge.SuppressToolkitChat;

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
