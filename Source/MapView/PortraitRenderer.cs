using System;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Renders pawn portraits through RimWorldCompat so portrait API drift stays isolated.
    /// Returns base64-encoded PNG for sending to viewers.
    /// </summary>
    public static class PortraitRenderer
    {
        private static readonly Vector2 PortraitSize = new Vector2(100f, 140f);

        public static string GetPortraitBase64(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed)
                return null;

            try
            {
                var tex = RimWorldCompat.GetPortraitTexture(pawn, PortraitSize, Rot4.South);
                if (tex == null)
                    return null;

                // Read pixels from render texture
                var readable = new Texture2D((int)PortraitSize.x, (int)PortraitSize.y, TextureFormat.RGBA32, false);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = tex;
                readable.ReadPixels(new Rect(0, 0, PortraitSize.x, PortraitSize.y), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;

                byte[] png = readable.EncodeToPNG();
                UnityEngine.Object.Destroy(readable);

                return Convert.ToBase64String(png);
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Portrait render failed for {pawn.LabelShort}: {ex.Message}");
                return null;
            }
        }
    }
}
