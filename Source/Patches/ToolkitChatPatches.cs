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
}
