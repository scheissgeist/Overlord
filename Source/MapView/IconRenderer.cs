using System;
using UnityEngine;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Renders a ThingDef's UI icon (optionally tinted for a stuff material) to a
    /// base64 PNG for the viewer client. Icons are STATIC per (def, stuff) — unlike
    /// pawn portraits they never change once loaded — so the caller can cache them
    /// permanently with no invalidation. Kept in one place so any RimWorld icon-API
    /// drift stays isolated (same discipline as PortraitRenderer).
    /// </summary>
    public static class IconRenderer
    {
        private const int IconSize = 64;

        /// <summary>
        /// Returns a base64 PNG of def's uiIcon tinted by stuff (if any), or null if
        /// the def has no usable icon. Must run on the main thread (Unity texture ops).
        /// </summary>
        public static string GetIconBase64(ThingDef def, ThingDef stuff)
        {
            if (def == null)
                return null;

            try
            {
                Texture2D icon = def.uiIcon;
                if (icon == null)
                    return null;

                // Tint: stuff colour when the item is made from stuff, else the def's
                // own uiIconColor. Matches how the game colours the icon in-game.
                Color tint = def.MadeFromStuff && stuff != null
                    ? def.GetColorForStuff(stuff)
                    : def.uiIconColor;

                // Blit the (possibly non-readable) icon into a temporary RenderTexture
                // so we can ReadPixels — the source Texture2D is usually not
                // CPU-readable directly. A tint material multiplies the colour in.
                RenderTexture rt = RenderTexture.GetTemporary(IconSize, IconSize, 0, RenderTextureFormat.ARGB32);
                RenderTexture prev = RenderTexture.active;
                try
                {
                    GL.Clear(true, true, Color.clear);
                    // Draw the icon into the RT with the tint via GUI blit.
                    RenderTexture.active = rt;
                    GL.Clear(true, true, Color.clear);
                    GL.PushMatrix();
                    GL.LoadPixelMatrix(0, IconSize, IconSize, 0);
                    var rect = new Rect(0, 0, IconSize, IconSize);
                    Graphics.DrawTexture(rect, icon, new Rect(0, 0, 1, 1), 0, 0, 0, 0, tint);
                    GL.PopMatrix();

                    var readable = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                    readable.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                    readable.Apply();

                    byte[] png = readable.EncodeToPNG();
                    UnityEngine.Object.Destroy(readable);
                    return Convert.ToBase64String(png);
                }
                finally
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            catch (Exception ex)
            {
                LogUtil.Warn("Icon render failed for " + (def.defName ?? "?") + ": " + ex.Message);
                return null;
            }
        }
    }
}
