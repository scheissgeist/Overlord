using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Paints control markers (pawn ring, hostiles, friendlies, ground items) onto
    /// rendered map frames using RimWorld map-cell coordinates.
    ///
    /// Two-phase design so the expensive part can leave the main thread:
    ///  1. CollectOps — reads live game state (pawn positions, thing grid) and must run
    ///     on the MAIN thread at capture time. Produces a list of primitive draw ops.
    ///  2. RasterizeToBuffer — pure pixel math into a raw RGBA32 buffer; safe on ANY
    ///     thread. Used by the async-readback pipeline's encode worker.
    /// RasterizeToTexture is the main-thread fallback path (ReadPixels route) and
    /// shares the same op collection + draw math.
    /// </summary>
    public static class MapOverlayPainter
    {
        private const byte KindRing = 0;
        private const byte KindDot = 1;
        private const byte KindDiamond = 2;

        public struct DrawOp
        {
            public byte kind;
            public int px;      // bottom-up pixel coords (Texture2D convention)
            public int py;
            public int size;
            public Color32 color;
        }

        private static readonly Color32 ItemColor = new Color32(255, 184, 66, 255);
        private static readonly Color32 DiamondOutline = new Color32(20, 15, 8, 255);

        /// <summary>
        /// Gathers all overlay draw ops for a frame. MAIN THREAD ONLY — reads live
        /// pawn/map state. Coordinates are bottom-up (Texture2D convention); the
        /// rasterizer flips rows for top-down buffers.
        /// </summary>
        public static List<DrawOp> CollectOps(Pawn pawn, float centerX, float centerZ, float radiusX, float radiusZ, int texW, int texH)
        {
            var ops = new List<DrawOp>(64);
            var map = pawn?.Map;
            if (map == null)
                return ops;

            float hp = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            Color pawnColor = hp > 0.5f ? Color.green : (hp > 0.25f ? Color.yellow : Color.red);
            if (TryWorldToPixel(texW, texH, centerX, centerZ, radiusX, radiusZ, pawn.DrawPos.x, pawn.DrawPos.z, out int cx, out int cy))
                ops.Add(new DrawOp { kind = KindRing, px = cx, py = cy, size = 8, color = pawnColor });

            var enemies = map.mapPawns.AllPawns
                .Where(p => p != pawn && p.Spawned && !p.Dead &&
                       p.Faction != null && pawn.Faction != null &&
                       FactionUtility.HostileTo(p.Faction, pawn.Faction))
                .Take(40);

            foreach (var enemy in enemies)
            {
                if (TryWorldToPixel(texW, texH, centerX, centerZ, radiusX, radiusZ, enemy.DrawPos.x, enemy.DrawPos.z, out int px, out int py))
                    ops.Add(new DrawOp { kind = KindDot, px = px, py = py, size = 3, color = Color.red });
            }

            return ops;
        }

        private static void CollectGroundItems(List<DrawOp> ops, Map map, float centerX, float centerZ, float radiusX, float radiusZ, int texW, int texH)
        {
            int drawn = 0;
            int minX = Mathf.Max(0, Mathf.FloorToInt(centerX - radiusX) - 1);
            int maxX = Mathf.Min(map.Size.x - 1, Mathf.CeilToInt(centerX + radiusX) + 1);
            int minZ = Mathf.Max(0, Mathf.FloorToInt(centerZ - radiusZ) - 1);
            int maxZ = Mathf.Min(map.Size.z - 1, Mathf.CeilToInt(centerZ + radiusZ) + 1);

            float pixelsPerCell = texH / Mathf.Max(1f, radiusZ * 2f);
            int size = pixelsPerCell > 24f ? 3 : 2;

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
                        if (!TryWorldToPixel(texW, texH, centerX, centerZ, radiusX, radiusZ, pos.x, pos.z, out int px, out int py))
                            continue;

                        ops.Add(new DrawOp { kind = KindDiamond, px = px, py = py, size = size, color = ItemColor });
                        drawn++;
                    }
                }
            }
        }

        /// <summary>
        /// Rasterizes ops into a raw RGBA32 buffer. Pure pixel math — safe on any
        /// thread. topDown = buffer row 0 is the visual TOP (D3D readback layout);
        /// false = row 0 is the bottom (GL / Texture2D layout).
        /// </summary>
        // ── Viewer frame brightness ─────────────────────────────────────────────
        // Reported 2026-08-03: "when it's night and there's lots of enemies the
        // Overlord window gets really black, it's hard to see."
        //
        // Cause is not the capture — it is the game itself. The capture draws
        // gameConditionManager (night/eclipse/toxic fallout darkening), so a
        // viewer frame is as dark as the streamer's own screen. The streamer can
        // live with that because they have the whole UI for context; a viewer
        // staring at a 1400px JPEG of a night map cannot see their pawn at all.
        //
        // A FIXED gamma is the wrong fix — it would blow out daylight frames to
        // wash. This measures the frame's own luminance and only lifts what is
        // actually dark, so day frames pass through ~untouched and night frames
        // get pulled up to a readable floor. Applied on the SEND WORKER, on a
        // CPU buffer that already exists, immediately before JPEG encode: zero
        // main-thread cost, which PROJECT_NORTH_STAR requires.
        //
        // Runs on a subsample (every Nth pixel) to measure, then one linear pass
        // to apply through a 256-entry lookup table — no per-pixel pow().

        /// Frames whose mean luma sits below this are considered "dark" and lifted.
        private const float DarkFrameLumaThreshold = 0.42f;
        /// Target mean luma for a fully-dark frame. Not 1.0 — night should still
        /// READ as night, just legibly.
        private const float DarkFrameTargetLuma = 0.46f;
        /// Hard cap on correction so a near-black frame cannot be amplified into
        /// pure noise (JPEG artifacts in shadow amplify badly).
        private const float MaxGammaLift = 2.2f;
        /// Measure every Nth pixel. 37 is coprime with common widths so the
        /// sample walks across columns instead of sampling one vertical stripe.
        private const int LumaSampleStride = 37;

        /// <summary>
        /// Measures mean luminance of an RGBA32 buffer and, if the frame is dark,
        /// applies an adaptive gamma lift in place. Returns the gamma used (1.0 =
        /// untouched) so callers can log/telemeter it.
        /// Thread-safe: touches only the caller's buffer.
        /// </summary>
        public static float ApplyAdaptiveBrightness(byte[] rgba, int pixelCount, bool enabled)
        {
            if (!enabled || rgba == null || pixelCount <= 0)
                return 1f;

            // ── measure ──
            long lumaSum = 0;
            int samples = 0;
            for (int p = 0; p < pixelCount; p += LumaSampleStride)
            {
                int idx = p * 4;
                if (idx + 2 >= rgba.Length) break;
                // Rec.601 luma in integer space (r*299 + g*587 + b*114) / 1000.
                lumaSum += (rgba[idx] * 299 + rgba[idx + 1] * 587 + rgba[idx + 2] * 114) / 1000;
                samples++;
            }
            if (samples == 0)
                return 1f;

            float meanLuma = (lumaSum / (float)samples) / 255f;
            if (meanLuma >= DarkFrameLumaThreshold || meanLuma <= 0.0005f)
                return 1f; // bright enough, or a black frame with nothing to recover

            // ── choose gamma ──
            // Solve mean^(1/g) = target  =>  g = ln(mean) / ln(target).
            float gamma = Mathf.Log(meanLuma) / Mathf.Log(DarkFrameTargetLuma);
            if (float.IsNaN(gamma) || float.IsInfinity(gamma) || gamma <= 1f)
                return 1f;
            gamma = Mathf.Min(gamma, MaxGammaLift);

            // ── apply ──
            // 256-entry LUT: one pow per level instead of one per subpixel.
            var lut = new byte[256];
            float invGamma = 1f / gamma;
            for (int v = 0; v < 256; v++)
                lut[v] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(v / 255f, invGamma) * 255f), 0, 255);

            int end = Math.Min(pixelCount * 4, rgba.Length - 3);
            for (int idx = 0; idx <= end; idx += 4)
            {
                rgba[idx] = lut[rgba[idx]];
                rgba[idx + 1] = lut[rgba[idx + 1]];
                rgba[idx + 2] = lut[rgba[idx + 2]];
                // alpha untouched
            }
            return gamma;
        }

        /// <summary>
        /// Texture2D variant for the legacy synchronous capture path (RGB24, no
        /// alpha). Main thread only — Texture2D access requires it.
        /// </summary>
        public static void ApplyAdaptiveBrightnessToTexture(Texture2D tex, bool enabled)
        {
            if (!enabled || tex == null)
                return;

            var pixels = tex.GetPixels32();
            if (pixels == null || pixels.Length == 0)
                return;

            long lumaSum = 0;
            int samples = 0;
            for (int p = 0; p < pixels.Length; p += LumaSampleStride)
            {
                var c = pixels[p];
                lumaSum += (c.r * 299 + c.g * 587 + c.b * 114) / 1000;
                samples++;
            }
            if (samples == 0) return;

            float meanLuma = (lumaSum / (float)samples) / 255f;
            if (meanLuma >= DarkFrameLumaThreshold || meanLuma <= 0.0005f)
                return;

            float gamma = Mathf.Log(meanLuma) / Mathf.Log(DarkFrameTargetLuma);
            if (float.IsNaN(gamma) || float.IsInfinity(gamma) || gamma <= 1f)
                return;
            gamma = Mathf.Min(gamma, MaxGammaLift);

            var lut = new byte[256];
            float invGamma = 1f / gamma;
            for (int v = 0; v < 256; v++)
                lut[v] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(v / 255f, invGamma) * 255f), 0, 255);

            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                c.r = lut[c.r];
                c.g = lut[c.g];
                c.b = lut[c.b];
                pixels[i] = c;
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
        }

        public static void RasterizeToBuffer(byte[] rgba, int w, int h, bool topDown, List<DrawOp> ops)
        {
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                RasterizeOp(op, w, h, (x, py, c) =>
                {
                    int row = topDown ? (h - 1 - py) : py;
                    int idx = (row * w + x) * 4;
                    rgba[idx] = c.r;
                    rgba[idx + 1] = c.g;
                    rgba[idx + 2] = c.b;
                    rgba[idx + 3] = 255;
                });
            }
        }

        /// <summary>
        /// Vertically flips an RGBA32 buffer in place (bottom-up → top-down for JPEG
        /// encoding on GL-layout readbacks). Any thread.
        /// </summary>
        public static void FlipRowsInPlace(byte[] rgba, int w, int h)
        {
            int stride = w * 4;
            var tmp = new byte[stride];
            for (int top = 0, bottom = h - 1; top < bottom; top++, bottom--)
            {
                Buffer.BlockCopy(rgba, top * stride, tmp, 0, stride);
                Buffer.BlockCopy(rgba, bottom * stride, rgba, top * stride, stride);
                Buffer.BlockCopy(tmp, 0, rgba, bottom * stride, stride);
            }
        }

        /// <summary>
        /// Main-thread fallback (ReadPixels route): rasterize pre-collected ops onto a
        /// Texture2D, then Apply. Same draw math as the buffer path.
        /// </summary>
        public static void RasterizeToTexture(Texture2D texture, List<DrawOp> ops)
        {
            for (int i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                RasterizeOp(op, texture.width, texture.height, (x, y, c) => texture.SetPixel(x, y, c));
            }
            texture.Apply();
        }

        // ── Shared draw math ────────────────────────────────────────────────────

        private static void RasterizeOp(DrawOp op, int w, int h, Action<int, int, Color32> plot)
        {
            switch (op.kind)
            {
                case KindRing: DrawRing(op, w, h, plot); break;
                case KindDot: DrawDot(op, w, h, plot); break;
                case KindDiamond: DrawDiamond(op, w, h, plot); break;
            }
        }

        private static void DrawRing(DrawOp op, int w, int h, Action<int, int, Color32> plot)
        {
            const int thickness = 2;
            int radius = op.size;
            for (int x = op.px - radius - thickness; x <= op.px + radius + thickness; x++)
            {
                for (int y = op.py - radius - thickness; y <= op.py + radius + thickness; y++)
                {
                    if (x < 0 || x >= w || y < 0 || y >= h)
                        continue;
                    float dist = Mathf.Sqrt((x - op.px) * (x - op.px) + (y - op.py) * (y - op.py));
                    if (dist >= radius - thickness && dist <= radius)
                        plot(x, y, op.color);
                }
            }
        }

        private static void DrawDot(DrawOp op, int w, int h, Action<int, int, Color32> plot)
        {
            for (int x = op.px - op.size; x <= op.px + op.size; x++)
            {
                for (int y = op.py - op.size; y <= op.py + op.size; y++)
                {
                    if (x >= 0 && x < w && y >= 0 && y < h)
                        plot(x, y, op.color);
                }
            }
        }

        private static void DrawDiamond(DrawOp op, int w, int h, Action<int, int, Color32> plot)
        {
            int radius = op.size;
            for (int x = op.px - radius - 1; x <= op.px + radius + 1; x++)
            {
                for (int y = op.py - radius - 1; y <= op.py + radius + 1; y++)
                {
                    if (x < 0 || x >= w || y < 0 || y >= h)
                        continue;

                    int dist = Mathf.Abs(x - op.px) + Mathf.Abs(y - op.py);
                    if (dist <= radius)
                        plot(x, y, op.color);
                    else if (dist == radius + 1)
                        plot(x, y, DiamondOutline);
                }
            }
        }

        private static bool TryWorldToPixel(int texW, int texH, float centerX, float centerZ, float radiusX, float radiusZ, float worldX, float worldZ, out int px, out int py)
        {
            float nx = 0.5f + ((worldX - centerX) / (radiusX * 2f));
            float ny = 0.5f + ((worldZ - centerZ) / (radiusZ * 2f));

            px = Mathf.RoundToInt(nx * texW);
            py = Mathf.RoundToInt(ny * texH);

            return px >= 0 && px < texW && py >= 0 && py < texH;
        }
    }
}
