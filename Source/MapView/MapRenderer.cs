using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Renders map sections around each viewer's pawn as JPEG frames.
    ///
    /// Approach: viewer captures still borrow RimWorld's main game camera because
    /// that camera is the one RimWorld's map mesh queue is built for.
    ///
    /// Capture is delayed until WaitForEndOfFrame so temporary camera moves and
    /// Graphics draw calls happen after the streamer's live frame has already rendered.
    /// </summary>
    public class MapRenderer
    {
        private int preferredRenderHeight;
        private int preferredJpegQuality;
        private float updateInterval;
        private float lastUpdateTime;
        private const float ViewAspect = 16f / 9f;

        // Round-robin: only render one viewer per update cycle
        private int currentViewerIndex;
        private readonly Dictionary<string, float> lastFrameTimeByViewer = new Dictionary<string, float>();

        private readonly Dictionary<string, int> frameCounter = new Dictionary<string, int>();
        private const int ForceFrameEvery = 10;

        private readonly ThreadSafeQueue<Action> sendQueue = new ThreadSafeQueue<Action>();
        private Thread sendThread;
        private volatile bool threadRunning;

        private bool initialized;
        private ViewerManager currentViewers;
        private GameObject renderPumpObject;
        private EndOfFrameRenderPump renderPump;
        private bool captureInProgress;
        private int initFailCount;
        private float lastStatsLogTime;
        private int statsFrames;
        private int statsSkipped;
        private long statsRenderMs;
        private long statsEncodeMs;
        private long statsJpegBytes;
        private int statsLastRenderWidth;
        private int statsLastRenderHeight;
        private int statsLastQuality;
        private string statsLastCameraMode = "pawn";
        private const float StatsIntervalSec = 10f;

        public void Start()
        {
            RefreshSettings();
            EnsureRenderPump();

            threadRunning = true;
            sendThread = new Thread(SendWorker)
            {
                IsBackground = true,
                Name = "Overlord_MapSend"
            };
            sendThread.Start();
            initialized = true;
        }

        public void Shutdown()
        {
            if (renderPump != null)
            {
                renderPump.StopPump();
                renderPump = null;
            }

            if (renderPumpObject != null)
            {
                UnityEngine.Object.Destroy(renderPumpObject);
                renderPumpObject = null;
            }

            currentViewers = null;
            threadRunning = false;
            sendThread?.Join(2000);
            frameCounter.Clear();
            initialized = false;
        }

        /// <summary>
        /// Called from GameComponentUpdate. This only refreshes state; actual map
        /// capture is performed by the end-of-frame pump after the live frame draws.
        /// </summary>
        public void Update(ViewerManager viewers)
        {
            if (!initialized)
                return;

            currentViewers = viewers;
            RefreshSettings();
        }

        private void EnsureRenderPump()
        {
            if (renderPump != null)
                return;

            renderPumpObject = new GameObject("Overlord_MapRenderPump");
            renderPumpObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(renderPumpObject);
            renderPump = renderPumpObject.AddComponent<EndOfFrameRenderPump>();
            renderPump.StartPump(this);
        }

        private void CaptureAfterLiveFrame()
        {
            if (!initialized || captureInProgress)
                return;

            var viewers = currentViewers;
            if (viewers == null)
                return;

            if (Current.ProgramState != ProgramState.Playing)
                return;

            captureInProgress = true;
            try
            {
                RenderDueFrames(viewers);
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Map end-of-frame capture error: {ex.Message}");
            }
            finally
            {
                captureInProgress = false;
            }
        }

        private void RenderDueFrames(ViewerManager viewers)
        {
            RefreshSettings();

            var activeSessions = viewers.AllSessions
                .Where(s => s != null && s.isConnected && s.HasPawn && s.assignedPawn.Map != null && s.assignedPawn.Spawned)
                .ToList();

            if (activeSessions.Count == 0)
                return;

            if (OverlordMod.Settings?.allowViewerTacticalMap == true &&
                OverlordMod.Settings?.mirrorHostCameraToViewers != true)
            {
                return;
            }

            float effectiveInterval = GetEffectiveInterval(activeSessions.Count);
            int queueDepth = sendQueue.Count;
            if (queueDepth > Math.Max(2, activeSessions.Count * 2))
            {
                statsSkipped++;
                MaybeLogStats(activeSessions.Count, queueDepth, effectiveInterval);
                return;
            }

            if (OverlordMod.Settings?.mirrorHostCameraToViewers == true)
            {
                if (Time.time - lastUpdateTime < effectiveInterval)
                    return;
                lastUpdateTime = Time.time;

                try
                {
                    RenderHostCameraFrame(activeSessions);
                    MaybeLogStats(activeSessions.Count, sendQueue.Count, effectiveInterval);
                }
                catch (Exception ex)
                {
                    LogUtil.Warn($"Map host-camera render error: {ex.Message}");
                    initFailCount++;
                    if (initFailCount > 5)
                        LogUtil.Warn("Map renderer: repeated host-camera errors");
                }
                return;
            }

            // Per-viewer cadence: render every due viewer up to a budget so
            // multi-viewer streams do not starve under shared round-robin.
            float now = Time.time;
            int framesThisUpdate = GetFramesPerUpdate(activeSessions.Count);
            int rendered = 0;

            // Prefer viewers that are most overdue.
            var due = activeSessions
                .Select((s, idx) =>
                {
                    float last;
                    if (!lastFrameTimeByViewer.TryGetValue(s.username, out last))
                        last = 0f;
                    return new { Session = s, Index = idx, Overdue = now - last };
                })
                .Where(x => x.Overdue >= effectiveInterval)
                .OrderByDescending(x => x.Overdue)
                .ToList();

            if (due.Count == 0)
            {
                MaybeLogStats(activeSessions.Count, sendQueue.Count, effectiveInterval);
                return;
            }

            lastUpdateTime = now;

            for (int i = 0; i < due.Count && rendered < framesThisUpdate; i++)
            {
                if (sendQueue.Count > Math.Max(2, activeSessions.Count * 2))
                {
                    statsSkipped++;
                    break;
                }

                var session = due[i].Session;
                currentViewerIndex = (due[i].Index + 1) % activeSessions.Count;

                if (!frameCounter.ContainsKey(session.username))
                    frameCounter[session.username] = 0;
                frameCounter[session.username]++;
                lastFrameTimeByViewer[session.username] = now;

                try
                {
                    RenderForViewer(session, activeSessions.Count);
                    rendered++;
                }
                catch (Exception ex)
                {
                    LogUtil.Warn($"Map render error for {session.username}: {ex.Message}");
                    initFailCount++;
                    if (initFailCount > 5)
                    {
                        initFailCount = 0;
                        LogUtil.Warn("Map renderer: repeated render errors");
                    }
                }
            }
            MaybeLogStats(activeSessions.Count, sendQueue.Count, effectiveInterval);
        }

        private sealed class EndOfFrameRenderPump : MonoBehaviour
        {
            private MapRenderer owner;
            private bool running;

            public void StartPump(MapRenderer renderer)
            {
                owner = renderer;
                running = true;
                StartCoroutine(RenderLoop());
            }

            public void StopPump()
            {
                running = false;
                StopAllCoroutines();
                owner = null;
            }

            private IEnumerator RenderLoop()
            {
                while (running)
                {
                    yield return new WaitForEndOfFrame();
                    owner?.CaptureAfterLiveFrame();
                }
            }

            private void OnDestroy()
            {
                running = false;
                owner = null;
            }
        }

        private float GetEffectiveInterval(int activeViewerCount)
        {
            // Soft floors only — keep multi-viewer playable without capping solo ~10–12 FPS.
            float minimum = 0.08f;
            if (activeViewerCount > 12)
                minimum = 0.14f;
            else if (activeViewerCount > 8)
                minimum = 0.12f;
            else if (activeViewerCount > 4)
                minimum = 0.10f;
            else if (activeViewerCount > 2)
                minimum = 0.09f;
            return Mathf.Max(updateInterval, minimum);
        }

        private int GetFramesPerUpdate(int activeViewerCount)
        {
            // How many full off-screen map re-renders may run in a SINGLE end-of-frame
            // pump. Each render is a Camera.Render + ReadPixels (GPU stall) + EncodeToJPG
            // on the main thread, so allowing 5-6 in one frame produces a visible host
            // hitch under multi-viewer load. Cap at 2 (3 for large audiences) — overdue
            // viewers still catch up across consecutive pumps (~10/s), they just don't all
            // re-render in the same frame. Solo/2-viewer behaviour is unchanged.
            if (activeViewerCount <= 2)
                return activeViewerCount;
            if (activeViewerCount <= 8)
                return 2;
            return 3;
        }

        private void RefreshSettings()
        {
            var s = OverlordMod.Settings;
            if (s == null) return;
            preferredRenderHeight = Mathf.Clamp(s.mapImageSize, 360, 1440);
            preferredJpegQuality = Mathf.Clamp(s.mapImageQuality, 45, 88);
            updateInterval = Mathf.Max(s.mapUpdateInterval, 0.08f);
        }

        private int GetEffectiveRenderHeight(int activeViewerCount, float cameraZoom = 1f, int viewportHeight = 0)
        {
            int height = viewportHeight > 0
                ? Math.Min(preferredRenderHeight, viewportHeight)
                : preferredRenderHeight;
            if (activeViewerCount <= 2 && cameraZoom > 0f && cameraZoom < 1f)
                height = Math.Min(1440, Mathf.RoundToInt(height / Mathf.Sqrt(cameraZoom)));
            if (activeViewerCount > 12)       height = Math.Min(height, 720);
            else if (activeViewerCount > 8)   height = Math.Min(height, 840);
            else if (activeViewerCount > 4)   height = Math.Min(height, 960);
            else if (activeViewerCount > 2)   height = Math.Min(height, 1080);
            return Mathf.Clamp(height, 360, 1440);
        }

        private int GetEffectiveJpegQuality(int activeViewerCount)
        {
            int quality = preferredJpegQuality;
            if (activeViewerCount > 12)       quality = Math.Min(quality, 64);
            else if (activeViewerCount > 8)   quality = Math.Min(quality, 70);
            else if (activeViewerCount > 4)   quality = Math.Min(quality, 76);
            else if (activeViewerCount > 2)   quality = Math.Min(quality, 80);
            return Mathf.Clamp(quality, 45, 88);
        }

        private void RecordFrameStats(long renderMs, long encodeMs, int jpegBytes, int frameWidth, int frameHeight, int quality, string cameraMode)
        {
            statsFrames++;
            statsRenderMs += renderMs;
            statsEncodeMs += encodeMs;
            statsJpegBytes += jpegBytes;
            statsLastRenderWidth = frameWidth;
            statsLastRenderHeight = frameHeight;
            statsLastQuality = quality;
            statsLastCameraMode = cameraMode ?? "pawn";
        }

        private void MaybeLogStats(int activeViewerCount, int queueDepth, float effectiveInterval)
        {
            if (Time.time - lastStatsLogTime < StatsIntervalSec)
                return;
            lastStatsLogTime = Time.time;
            if (statsFrames == 0 && statsSkipped == 0)
                return;

            long avgRender = statsFrames > 0 ? statsRenderMs / statsFrames : 0;
            long avgEncode = statsFrames > 0 ? statsEncodeMs / statsFrames : 0;
            long avgKb = statsFrames > 0 ? (statsJpegBytes / statsFrames) / 1024 : 0;
            LogUtil.Log(
                $"Map stats viewers={activeViewerCount} mode={statsLastCameraMode} render={statsLastRenderWidth}x{statsLastRenderHeight} quality={statsLastQuality} interval={effectiveInterval:F2}s frames={statsFrames} skipped={statsSkipped} avgRenderMs={avgRender} avgEncodeMs={avgEncode} avgJpegKB={avgKb} sendQueue={queueDepth}"
            );

            var msg = new Dictionary<string, object>
            {
                ["type"] = "diagnostics",
                ["adminOnly"] = true,
                ["category"] = "map",
                ["viewers"] = activeViewerCount,
                ["renderWidth"] = statsLastRenderWidth,
                ["renderHeight"] = statsLastRenderHeight,
                ["cameraMode"] = statsLastCameraMode,
                ["quality"] = statsLastQuality,
                ["interval"] = effectiveInterval,
                ["frames"] = statsFrames,
                ["skipped"] = statsSkipped,
                ["avgRenderMs"] = avgRender,
                ["avgEncodeMs"] = avgEncode,
                ["avgJpegKB"] = avgKb,
                ["sendQueue"] = queueDepth
            };
            OverlordGameComponent.Instance?.Relay?.Broadcast(msg);
            OverlordGameComponent.Instance?.EmbeddedServer?.Broadcast(JsonHelper.ToJson(msg));

            statsFrames = 0;
            statsSkipped = 0;
            statsRenderMs = 0;
            statsEncodeMs = 0;
            statsJpegBytes = 0;
        }

        private static void SendMapFrame(Dictionary<string, object> metadata, byte[] jpeg, string embeddedTarget)
        {
            var relay = OverlordGameComponent.Instance?.Relay;
            relay?.SendBinaryMapFrame(metadata, jpeg);

            var embedded = EmbeddedWebServer.Instance;
            if (embedded == null || !embedded.IsRunning)
                return;

            var jsonMsg = new Dictionary<string, object>(metadata) { ["data"] = Convert.ToBase64String(jpeg) };
            string json = JsonHelper.ToJson(jsonMsg);
            if (string.IsNullOrEmpty(embeddedTarget))
                embedded.Broadcast(json);
            else
                embedded.SendToViewer(embeddedTarget, json);
        }

        /// <summary>
        /// Temporarily borrows the main camera to render a specific map area to a RenderTexture.
        /// Returns null if the game camera is unavailable. Caller must release the returned texture.
        /// </summary>
        private static RenderTexture RenderMapArea(Map map, Vector3 center, float radiusZ, float aspect, int width, int height)
        {
            var gameCamera = Find.Camera;
            if (gameCamera == null || map == null)
                return null;

            // Save main camera state
            var savedPos = gameCamera.transform.position;
            var savedRot = gameCamera.transform.rotation;
            bool savedOrtho = gameCamera.orthographic;
            float savedOrthoSize = gameCamera.orthographicSize;
            float savedAspect = gameCamera.aspect;
            RenderTexture savedTarget = gameCamera.targetTexture;
            CameraClearFlags savedClearFlags = gameCamera.clearFlags;
            Color savedBgColor = gameCamera.backgroundColor;
            RenderTexture savedActive = RenderTexture.active;

            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;

            try
            {
                // Reposition for the viewer's pawn, then explicitly queue the
                // RimWorld map layers for that same off-screen view.
                gameCamera.transform.position = new Vector3(center.x, 40f, center.z);
                gameCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                gameCamera.orthographic = true;
                gameCamera.orthographicSize = radiusZ;
                gameCamera.aspect = aspect;
                gameCamera.targetTexture = rt;
                gameCamera.clearFlags = CameraClearFlags.SolidColor;
                gameCamera.backgroundColor = new Color(0.08f, 0.08f, 0.08f, 1f);

                CellRect viewRect = BuildViewRect(center, radiusZ, aspect, map);
                using (RimWorldCompat.TemporarilySetCurrentMap(map))
                using (MapRenderContext.Begin(viewRect, closeZoom: true))
                {
                    QueueMapDrawCommands(map);
                    gameCamera.Render();
                }
            }
            finally
            {
                // Always restore the main camera
                RenderTexture.active = savedActive;
                gameCamera.transform.position = savedPos;
                gameCamera.transform.rotation = savedRot;
                gameCamera.orthographic = savedOrtho;
                gameCamera.orthographicSize = savedOrthoSize;
                gameCamera.aspect = savedAspect;
                gameCamera.targetTexture = savedTarget;
                gameCamera.clearFlags = savedClearFlags;
                gameCamera.backgroundColor = savedBgColor;
            }

            return rt;
        }

        private static CellRect BuildViewRect(Vector3 center, float radiusZ, float aspect, Map map)
        {
            int minX = Mathf.FloorToInt(center.x - radiusZ * aspect - 2f);
            int maxX = Mathf.CeilToInt(center.x + radiusZ * aspect + 2f);
            int minZ = Mathf.FloorToInt(center.z - radiusZ - 2f);
            int maxZ = Mathf.CeilToInt(center.z + radiusZ + 2f);
            return new CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1).ClipInsideMap(map);
        }

        private static void QueueMapDrawCommands(Map map)
        {
            if (map == null)
                return;

            map.powerNetManager?.UpdatePowerNetsAndConnections_First();
            map.glowGrid?.GlowGridUpdate_First();
            PlantFallColors.SetFallShaderGlobals(map);
            map.waterInfo?.SetTextures();
            map.mapDrawer?.MapMeshDrawerUpdate_First();
            map.mapDrawer?.DrawMapMesh();
            map.dynamicDrawManager?.DrawDynamicThings();
            map.gameConditionManager?.GameConditionManagerDraw(map);
            // Keep the capture draw set minimal. Edge clippers, designations, and
            // overlays have screen-space side effects that are more likely to leak
            // into other cameras or visible UI when several viewer frames are active.
        }

        private void RenderForViewer(ViewerSession session, int activeViewerCount)
        {
            var renderWatch = Stopwatch.StartNew();
            var pawn = session.assignedPawn;
            var map = pawn.Map;
            if (map == null || !pawn.Spawned)
                return;

            float cameraZoom = Mathf.Clamp(session.cameraZoom <= 0f ? 1f : session.cameraZoom, 0.45f, 5f);
            float viewerAspect = session.viewportAspect > 0f
                ? Mathf.Clamp(session.viewportAspect, 0.5f, 3f)
                : ViewAspect;
            int frameHeight = GetEffectiveRenderHeight(activeViewerCount, cameraZoom, session.viewportHeight);
            int frameWidth = Mathf.RoundToInt(frameHeight * viewerAspect);
            const int maxFrameWidth = 2560;
            if (frameWidth > maxFrameWidth)
            {
                frameWidth = maxFrameWidth;
                frameHeight = Mathf.Clamp(Mathf.RoundToInt(frameWidth / viewerAspect), 360, 1440);
            }
            int frameQuality = GetEffectiveJpegQuality(activeViewerCount);

            float baseRadiusZ = Mathf.Clamp(frameHeight / 38f, 10f, 24f);
            float radiusZ = Mathf.Clamp(baseRadiusZ / cameraZoom, 3.5f, 54f);
            float radiusX = radiusZ * viewerAspect;

            Vector3 pawnPos = pawn.DrawPos;
            Vector3 cameraCenter = pawnPos;
            if (session.cameraHasCenter)
            {
                cameraCenter = new Vector3(
                    Mathf.Clamp(session.cameraCenterX, 0f, map.Size.x - 1f),
                    pawnPos.y,
                    Mathf.Clamp(session.cameraCenterZ, 0f, map.Size.z - 1f));
            }

            var rt = RenderMapArea(map, cameraCenter, radiusZ, viewerAspect, frameWidth, frameHeight);
            if (rt == null)
                return;

            var tex = new Texture2D(frameWidth, frameHeight, TextureFormat.RGB24, false);
            tex.filterMode = FilterMode.Bilinear;
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, frameWidth, frameHeight), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prevActive;
            renderWatch.Stop();

            RenderTexture.ReleaseTemporary(rt);

            try
            {
                MapOverlayPainter.Paint(tex, pawn, cameraCenter.x, cameraCenter.z, radiusX, radiusZ);
            }
            catch { }

            byte[] jpeg;
            var encodeWatch = Stopwatch.StartNew();
            try
            {
                jpeg = tex.EncodeToJPG(frameQuality);
            }
            finally
            {
                encodeWatch.Stop();
                UnityEngine.Object.Destroy(tex);
            }
            RecordFrameStats(renderWatch.ElapsedMilliseconds, encodeWatch.ElapsedMilliseconds, jpeg.Length, frameWidth, frameHeight, frameQuality, "pawn");

            string username = session.username;
            float centerX = cameraCenter.x;
            float centerZ = cameraCenter.z;
            int mapWidth = map.Size.x;
            int mapHeight = map.Size.z;
            sendQueue.Enqueue(() =>
            {
                try
                {
                    var msg = new Dictionary<string, object>
                    {
                        ["type"] = StateProtocol.MapFrame,
                        ["target"] = username,
                        ["centerX"] = centerX,
                        ["centerZ"] = centerZ,
                        ["radiusX"] = radiusX,
                        ["radiusZ"] = radiusZ,
                        ["sourceWidth"] = frameWidth,
                        ["sourceHeight"] = frameHeight,
                        ["quality"] = frameQuality,
                        ["cameraMode"] = "pawn",
                        ["zoom"] = cameraZoom,
                        ["mapWidth"] = mapWidth,
                        ["mapHeight"] = mapHeight
                    };
                    SendMapFrame(msg, jpeg, username);
                }
                catch { }
            });

            initFailCount = 0;
        }

        private Map ResolveHostCameraMap(List<ViewerSession> activeSessions)
        {
            if (Find.CurrentMap != null)
                return Find.CurrentMap;
            foreach (var session in activeSessions)
            {
                if (session?.assignedPawn?.Map != null)
                    return session.assignedPawn.Map;
            }
            return null;
        }

        private static float GetHostCameraAspect(Camera gameCamera)
        {
            if (gameCamera == null || gameCamera.aspect <= 0.01f)
                return ViewAspect;
            return Mathf.Clamp(gameCamera.aspect, 1.25f, 2.4f);
        }

        private void RenderHostCameraFrame(List<ViewerSession> activeSessions)
        {
            var renderWatch = Stopwatch.StartNew();
            var gameCamera = Find.Camera;
            var map = ResolveHostCameraMap(activeSessions);
            if (gameCamera == null || map == null)
                return;

            int activeViewerCount = activeSessions.Count;
            float aspect = GetHostCameraAspect(gameCamera);
            int frameHeight = GetEffectiveRenderHeight(activeViewerCount);
            int frameWidth = Mathf.RoundToInt(frameHeight * aspect);
            int frameQuality = GetEffectiveJpegQuality(activeViewerCount);

            // For host camera mode, use current position
            Vector3 hostPos = gameCamera.transform.position;
            float hostOrthoSize = gameCamera.orthographic ? Mathf.Max(1f, gameCamera.orthographicSize) : 12f;

            var rt = RenderTexture.GetTemporary(frameWidth, frameHeight, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;

            RenderTexture savedTarget = gameCamera.targetTexture;
            float savedAspect = gameCamera.aspect;
            RenderTexture savedActive = RenderTexture.active;
            try
            {
                gameCamera.aspect = aspect;
                gameCamera.targetTexture = rt;
                gameCamera.Render();
            }
            finally
            {
                RenderTexture.active = savedActive;
                gameCamera.aspect = savedAspect;
                gameCamera.targetTexture = savedTarget;
            }

            var tex = new Texture2D(frameWidth, frameHeight, TextureFormat.RGB24, false);
            tex.filterMode = FilterMode.Bilinear;
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, frameWidth, frameHeight), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prevActive;
            renderWatch.Stop();

            RenderTexture.ReleaseTemporary(rt);

            byte[] jpeg;
            var encodeWatch = Stopwatch.StartNew();
            try
            {
                jpeg = tex.EncodeToJPG(frameQuality);
            }
            finally
            {
                encodeWatch.Stop();
                UnityEngine.Object.Destroy(tex);
            }

            RecordFrameStats(renderWatch.ElapsedMilliseconds, encodeWatch.ElapsedMilliseconds, jpeg.Length, frameWidth, frameHeight, frameQuality, "host");

            float radiusZ = gameCamera.orthographic ? Mathf.Max(1f, gameCamera.orthographicSize) : Mathf.Clamp(frameHeight / 32f, 8f, 24f);
            float radiusX = radiusZ * aspect;
            int mapWidth = map.Size.x;
            int mapHeight = map.Size.z;

            sendQueue.Enqueue(() =>
            {
                try
                {
                    var msg = new Dictionary<string, object>
                    {
                        ["type"] = StateProtocol.MapFrame,
                        ["centerX"] = (float)hostPos.x,
                        ["centerZ"] = (float)hostPos.z,
                        ["radiusX"] = radiusX,
                        ["radiusZ"] = radiusZ,
                        ["sourceWidth"] = frameWidth,
                        ["sourceHeight"] = frameHeight,
                        ["quality"] = frameQuality,
                        ["cameraMode"] = "host",
                        ["zoom"] = 1f,
                        ["mapWidth"] = mapWidth,
                        ["mapHeight"] = mapHeight
                    };
                    SendMapFrame(msg, jpeg, null);
                }
                catch { }
            });

            initFailCount = 0;
        }

        private void SendWorker()
        {
            while (threadRunning)
            {
                if (sendQueue.Count > 0)
                    sendQueue.DrainTo(task => task());
                else
                    Thread.Sleep(10);
            }
        }
    }
}
