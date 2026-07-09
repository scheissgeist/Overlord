using HarmonyLib;
using Verse;

namespace Overlord
{
    [HarmonyPatch(typeof(CameraDriver), "CurrentViewRect", MethodType.Getter)]
    public static class Patch_CameraDriver_CurrentViewRect
    {
        [HarmonyPriority(Priority.High)]
        static bool Prefix(ref CellRect __result)
        {
            if (!MapRenderContext.HasViewRectOverride)
                return true;

            __result = MapRenderContext.ViewRectOverride;
            return false;
        }
    }

    [HarmonyPatch(typeof(CameraDriver), "CurrentZoom", MethodType.Getter)]
    public static class Patch_CameraDriver_CurrentZoom
    {
        [HarmonyPriority(Priority.High)]
        static bool Prefix(ref CameraZoomRange __result)
        {
            if (!MapRenderContext.ForceCloseZoom)
                return true;

            __result = CameraZoomRange.Closest;
            return false;
        }
    }
}
