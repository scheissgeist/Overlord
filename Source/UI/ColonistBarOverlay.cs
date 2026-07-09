using UnityEngine;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Draws connection status badges on the colonist bar.
    /// Called from GameComponentOnGUI (main thread, GUI event).
    /// Version-sensitive bar access is routed through RimWorldCompat.
    /// </summary>
    public static class ColonistBarOverlay
    {
        private static readonly Color ConnectedColor = new Color(0.3f, 0.9f, 0.3f, 0.9f);
        private static readonly Color AssignedColor = new Color(0.9f, 0.8f, 0.2f, 0.9f);

        private const float BadgeSize = 10f;
        private const float BadgeOffset = 2f;

        public static void Draw(ViewerManager viewers)
        {
            if (viewers == null)
                return;

            if (!RimWorldCompat.TryGetColonistBarDrawData(out var pawns, out var drawLocs, out float size))
                return;

            for (int i = 0; i < pawns.Count && i < drawLocs.Count; i++)
            {
                var pawn = pawns[i];
                if (pawn == null || !pawn.IsColonist)
                    continue;

                var session = viewers.GetSessionForPawn(pawn);
                if (session == null)
                    continue;

                var loc = drawLocs[i];
                var portraitRect = new Rect(loc.x, loc.y, size, size);

                var badge = new Rect(
                    portraitRect.xMax - BadgeSize - BadgeOffset,
                    portraitRect.y + BadgeOffset,
                    BadgeSize,
                    BadgeSize
                );

                Color color = session.HasPawn ? ConnectedColor : AssignedColor;
                DrawBadge(badge, color, session.displayName ?? session.username);
            }
        }

        private static void DrawBadge(Rect rect, Color color, string tooltip)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, BaseContent.WhiteTex);
            GUI.color = previousColor;

            if (!string.IsNullOrEmpty(tooltip))
                TooltipHandler.TipRegion(rect, tooltip);
        }
    }
}
