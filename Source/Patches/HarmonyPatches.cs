using System;
using HarmonyLib;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Lifecycle and pawn-event patches. Rendering-specific patches live in
    /// MapRenderingPatches because they are only active during viewer captures.
    /// </summary>

    // Patch 1: Initialize on new game
    [HarmonyPatch(typeof(Game), "InitNewGame")]
    public static class Patch_Game_InitNewGame
    {
        [HarmonyPostfix]
        static void Postfix(Game __instance)
        {
            try
            {
                EnsureGameComponent(__instance);
                OverlordGameComponent.Instance?.Initialize();
            }
            catch (Exception ex)
            {
                LogUtil.Error($"InitNewGame: {ex.Message}");
            }
        }

        internal static void EnsureGameComponent(Game game)
        {
            // Check under lock-free read — GameComponent list is only mutated on main thread
            if (game.GetComponent<OverlordGameComponent>() != null)
                return;
            var component = new OverlordGameComponent(game);
            game.components.Add(component);
        }
    }

    // Patch 2: Initialize on load game
    [HarmonyPatch(typeof(Game), "LoadGame")]
    public static class Patch_Game_LoadGame
    {
        [HarmonyPostfix]
        static void Postfix(Game __instance)
        {
            try
            {
                Patch_Game_InitNewGame.EnsureGameComponent(__instance);
                OverlordGameComponent.Instance?.Initialize();
            }
            catch (Exception ex)
            {
                LogUtil.Error($"LoadGame: {ex.Message}");
            }
        }
    }

    // Patch 3: Cleanup on map removal
    // Only shuts down if this is the last map (full game exit), not caravan/multi-map drop
    [HarmonyPatch(typeof(Game), "DeinitAndRemoveMap")]
    public static class Patch_Game_DeinitAndRemoveMap
    {
        [HarmonyPrefix]
        static void Prefix(Map map)
        {
            try
            {
                var game = Current.Game;
                if (game == null)
                    return;

                // Only shutdown if this is the last map
                if (game.Maps != null && game.Maps.Count <= 1)
                {
                    OverlordGameComponent.Instance?.Shutdown();
                }
            }
            catch (Exception ex)
            {
                LogUtil.Error($"DeinitAndRemoveMap: {ex.Message}");
            }
        }
    }

    // Patch 4: Track pawn deaths for respawn system
    [HarmonyPatch(typeof(Pawn), "Kill")]
    public static class Patch_Pawn_Kill
    {
        [HarmonyPostfix]
        static void Postfix(Pawn __instance, DamageInfo? dinfo)
        {
            try
            {
                if (__instance == null)
                    return;

                if (__instance.Faction?.IsPlayer != true)
                    return;

                OverlordGameComponent.Instance?.OnPawnKilled(__instance);
            }
            catch (Exception ex)
            {
                LogUtil.Error($"Pawn.Kill: {ex.Message}");
            }
        }
    }
}
