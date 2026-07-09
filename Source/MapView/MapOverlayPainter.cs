using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Paints control markers onto a rendered map texture using RimWorld map-cell coordinates.
    /// The base image remains the real world render; these markers keep small objects readable at stream scale.
    /// </summary>
    public static class MapOverlayPainter
    {
        public static void Paint(Texture2D texture, Pawn pawn, float gridRadius)
        {
            Paint(texture, pawn, gridRadius, gridRadius);
        }

        public static void Paint(Texture2D texture, Pawn pawn, float radiusX, float radiusZ)
        {
            Vector3 center = pawn.DrawPos;
            Paint(texture, pawn, center.x, center.z, radiusX, radiusZ);
        }

        public static void Paint(Texture2D texture, Pawn pawn, float centerX, float centerZ, float radiusX, float radiusZ)
        {
            var map = pawn.Map;
            if (map == null)
            {
                texture.Apply();
                return;
            }

            PaintGroundItems(texture, map, centerX, centerZ, radiusX, radiusZ);

            float hp = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            Color pawnColor = hp > 0.5f ? Color.green : (hp > 0.25f ? Color.yellow : Color.red);
            if (TryWorldToPixel(texture, centerX, centerZ, radiusX, radiusZ, pawn.DrawPos.x, pawn.DrawPos.z, out int cx, out int cy))
                DrawRing(texture, cx, cy, 8, 2, pawnColor);

            var enemies = map.mapPawns.AllPawns
                .Where(p => p != pawn && p.Spawned && !p.Dead &&
                       p.Faction != null && pawn.Faction != null &&
                       FactionUtility.HostileTo(p.Faction, pawn.Faction))
                .Take(40);

            foreach (var enemy in enemies)
            {
                if (TryWorldToPixel(texture, centerX, centerZ, radiusX, radiusZ, enemy.DrawPos.x, enemy.DrawPos.z, out int px, out int py))
                    DrawDot(texture, px, py, 3, Color.red);
            }

            var friendlies = map.mapPawns.FreeColonists
                .Where(p => p != pawn && p.Spawned && !p.Dead)
                .Take(20);

            foreach (var friendly in friendlies)
            {
                if (TryWorldToPixel(texture, centerX, centerZ, radiusX, radiusZ, friendly.DrawPos.x, friendly.DrawPos.z, out int px, out int py))
                    DrawDot(texture, px, py, 3, Color.cyan);
            }

            texture.Apply();
        }

        private static void PaintGroundItems(Texture2D texture, Map map, float centerX, float centerZ, float radiusX, float radiusZ)
        {
            int drawn = 0;
            int minX = Mathf.Max(0, Mathf.FloorToInt(centerX - radiusX) - 1);
            int maxX = Mathf.Min(map.Size.x - 1, Mathf.CeilToInt(centerX + radiusX) + 1);
            int minZ = Mathf.Max(0, Mathf.FloorToInt(centerZ - radiusZ) - 1);
            int maxZ = Mathf.Min(map.Size.z - 1, Mathf.CeilToInt(centerZ + radiusZ) + 1);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var things = map.thingGrid.ThingsListAt(new IntVec3(x, 0, z));
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (drawn >= 90)
                            return;

                        var thing = things[i];
                        if (thing == null || thing.Destroyed || !thing.Spawned || thing.def == null)
                            continue;

                        if (thing.def.category != ThingCategory.Item)
                            continue;

                        Vector3 pos = thing.DrawPos;
                        if (!TryWorldToPixel(texture, centerX, centerZ, radiusX, radiusZ, pos.x, pos.z, out int px, out int py))
                            continue;

                        float pixelsPerCell = texture.height / Mathf.Max(1f, radiusZ * 2f);
                        int size = pixelsPerCell > 24f ? 3 : 2;
                        DrawDiamond(texture, px, py, size, new Color(1f, 0.72f, 0.26f, 1f));
                        drawn++;
                    }
                }
            }
        }

        private static bool TryWorldToPixel(Texture2D texture, float centerX, float centerZ, float radiusX, float radiusZ, float worldX, float worldZ, out int px, out int py)
        {
            float nx = 0.5f + ((worldX - centerX) / (radiusX * 2f));
            float ny = 0.5f + ((worldZ - centerZ) / (radiusZ * 2f));

            px = Mathf.RoundToInt(nx * texture.width);
            py = Mathf.RoundToInt(ny * texture.height);

            return px >= 0 && px < texture.width && py >= 0 && py < texture.height;
        }

        private static void DrawRing(Texture2D tex, int cx, int cy, int radius, int thickness, Color color)
        {
            for (int x = cx - radius - thickness; x <= cx + radius + thickness; x++)
            {
                for (int y = cy - radius - thickness; y <= cy + radius + thickness; y++)
                {
                    if (x < 0 || x >= tex.width || y < 0 || y >= tex.height)
                        continue;
                    float dist = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (dist >= radius - thickness && dist <= radius)
                        tex.SetPixel(x, y, color);
                }
            }
        }

        private static void DrawDot(Texture2D tex, int cx, int cy, int size, Color color)
        {
            for (int x = cx - size; x <= cx + size; x++)
            {
                for (int y = cy - size; y <= cy + size; y++)
                {
                    if (x >= 0 && x < tex.width && y >= 0 && y < tex.height)
                        tex.SetPixel(x, y, color);
                }
            }
        }

        private static void DrawDiamond(Texture2D tex, int cx, int cy, int radius, Color color)
        {
            Color outline = new Color(0.08f, 0.06f, 0.03f, 1f);
            for (int x = cx - radius - 1; x <= cx + radius + 1; x++)
            {
                for (int y = cy - radius - 1; y <= cy + radius + 1; y++)
                {
                    if (x < 0 || x >= tex.width || y < 0 || y >= tex.height)
                        continue;

                    int dist = Mathf.Abs(x - cx) + Mathf.Abs(y - cy);
                    if (dist <= radius)
                        tex.SetPixel(x, y, color);
                    else if (dist == radius + 1)
                        tex.SetPixel(x, y, outline);
                }
            }
        }
    }
}
