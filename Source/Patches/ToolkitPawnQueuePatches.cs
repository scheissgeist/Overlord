using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Twitch Toolkit's installed GameComponentPawns.GameComponentTick removes
    /// entries from pawnHistory inside foreach, which invalidates its enumerator.
    /// Replace only that cleanup loop with the same cleanup done in two phases.
    /// Reflection keeps Toolkit optional.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_Toolkit_PawnHistoryCleanup
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return AccessTools.TypeByName("TwitchToolkit.PawnQueue.GameComponentPawns") != null;
        }

        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                AccessTools.TypeByName("TwitchToolkit.PawnQueue.GameComponentPawns"),
                "GameComponentTick");
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            if (Find.TickManager == null || Find.TickManager.TicksGame % 1000 != 0)
                return false;

            try
            {
                var field = __instance?.GetType().GetField("pawnHistory", BindingFlags.Public | BindingFlags.Instance);
                var history = field?.GetValue(__instance) as IDictionary;
                var colonists = Find.ColonistBar?.GetColonistsInOrder();
                if (history == null || colonists == null)
                    return false;

                var current = new HashSet<Pawn>(colonists);
                var staleKeys = new List<object>();
                foreach (DictionaryEntry entry in history)
                {
                    if (!(entry.Value is Pawn pawn) || !current.Contains(pawn))
                        staleKeys.Add(entry.Key);
                }
                foreach (object key in staleKeys)
                    history.Remove(key);
            }
            catch (Exception ex)
            {
                LogUtil.Warn("Toolkit pawn history cleanup failed safely: " + ex.Message);
            }

            // Never run Toolkit's original foreach/remove implementation.
            return false;
        }
    }
}
