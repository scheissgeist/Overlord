using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
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

        // ── Async capture pipeline ──────────────────────────────────────────────
        // AsyncGPUReadback removes the ReadPixels GPU→CPU pipeline stall, and the
        // JPEG encode runs on the send worker via ImageConversion.EncodeArrayToJPG
        // (raw-buffer API, no Texture2D). If the off-thread encode ever throws, the
        // pipeline flips to the legacy main-thread ReadPixels+EncodeToJPG path.
        private bool asyncSupported;              // SystemInfo, resolved once on main thread
        private bool gpuTopDown;                  // D3D readback rows are top-down
        private volatile bool asyncPipelineBroken;
        private int pendingReadbacks;             // main-thread only (callbacks on main thread)
        private int consecutiveReadbackErrors;    // main-thread only
        private readonly object statsLock = new object();
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

            asyncSupported = SystemInfo.supportsAsyncGPUReadback;
            gpuTopDown = SystemInfo.graphicsUVStartsAtTop;
            asyncPipelineBroken = false;
            pendingReadbacks = 0;
            LogUtil.Log($"Map capture pipeline: asyncReadback={asyncSupported} uvTopDown={gpuTopDown}");

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
            sendQueue.Clear(); // drop un-encoded frames from the ending session
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
            // Backpressure counts frames still in flight on the GPU (async readbacks)
            // as well as frames waiting on the encode/send worker.
            int queueDepth = sendQueue.Count + pendingReadbacks;
            if (queueDepth > Math.Max(2, activeSessions.Count * 2))
            {
                lock (statsLock) { statsSkipped++; }
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
                .Select(s =>
                {
                    float last;
                    if (!lastFrameTimeByViewer.TryGetValue(s.username, out last))
                        last = 0f;
                    return new { Session = s, Overdue = now - last };
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

            // Build frame params for every due viewer, then group compatible views —
            // multi-viewer streams cluster on the same base, so one expensive
            // map-mesh render can serve several viewers via GPU crops at native
            // resolution. A group consumes ONE unit of the per-pump render budget
            // because the map render is the dominant cost, not the crops.
            var dueParams = new List<ViewerFrameParams>(due.Count);
            for (int i = 0; i < due.Count; i++)
            {
                var p = BuildFrameParams(due[i].Session, activeSessions.Count);
                if (p != null)
                    dueParams.Add(p);
            }

            // Grouping's "one budget unit per group" premise only holds on the async
            // path (one map render, GPU crops). On the sync fallback each member costs
            // a ReadPixels stall + main-thread encode, so grouping would smuggle the
            // exact multi-encode frame spike the budget cap exists to prevent.
            // Grouping re-enabled 2026-07-09 (late): the Blit crops that decoded
            // upside-down on D3D are replaced by CPU row-crops from a single union
            // readback, which preserve the union buffer's row order — orientation is
            // identical to the (production-soaked) solo path by construction. Also
            // one readback per GROUP now, not per member. Async path only.
            bool groupingAllowed = asyncSupported && !asyncPipelineBroken;
            var groups = groupingAllowed
                ? GroupCompatibleViews(dueParams)
                : dueParams.Select(p => new List<ViewerFrameParams> { p }).ToList();

            foreach (var group in groups)
            {
                if (rendered >= framesThisUpdate)
                    break;

                if (sendQueue.Count + pendingReadbacks > Math.Max(2, activeSessions.Count * 2))
                {
                    lock (statsLock) { statsSkipped++; }
                    break;
                }

                try
                {
                    RenderGroup(group);
                    rendered++;
                    // Stamp members as served only after the render actually ran, so a
                    // transient failure retries next pump instead of waiting a full
                    // interval for the whole group.
                    foreach (var m in group)
                    {
                        if (!frameCounter.ContainsKey(m.session.username))
                            frameCounter[m.session.username] = 0;
                        frameCounter[m.session.username]++;
                        lastFrameTimeByViewer[m.session.username] = now;
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.Warn($"Map render error (group of {group.Count}): {ex.Message}");
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
            // Called from the encode worker in the async pipeline — guard the counters.
            lock (statsLock)
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
        }

        private void MaybeLogStats(int activeViewerCount, int queueDepth, float effectiveInterval)
        {
            if (Time.time - lastStatsLogTime < StatsIntervalSec)
                return;
            lastStatsLogTime = Time.time;

            int frames, skipped, width, height, quality;
            long avgRender, avgEncode, avgKb;
            string cameraMode;
            lock (statsLock)
            {
                if (statsFrames == 0 && statsSkipped == 0)
                    return;

                frames = statsFrames;
                skipped = statsSkipped;
                width = statsLastRenderWidth;
                height = statsLastRenderHeight;
                quality = statsLastQuality;
                cameraMode = statsLastCameraMode;
                avgRender = statsFrames > 0 ? statsRenderMs / statsFrames : 0;
                avgEncode = statsFrames > 0 ? statsEncodeMs / statsFrames : 0;
                avgKb = statsFrames > 0 ? (statsJpegBytes / statsFrames) / 1024 : 0;

                statsFrames = 0;
                statsSkipped = 0;
                statsRenderMs = 0;
                statsEncodeMs = 0;
                statsJpegBytes = 0;
            }

            LogUtil.Log(
                $"Map stats viewers={activeViewerCount} mode={cameraMode} render={width}x{height} quality={quality} interval={effectiveInterval:F2}s frames={frames} skipped={skipped} avgRenderMs={avgRender} avgEncodeMs={avgEncode} avgJpegKB={avgKb} sendQueue={queueDepth}"
            );

            var msg = new Dictionary<string, object>
            {
                ["type"] = "diagnostics",
                ["adminOnly"] = true,
                ["category"] = "map",
                ["viewers"] = activeViewerCount,
                ["renderWidth"] = width,
                ["renderHeight"] = height,
                ["cameraMode"] = cameraMode,
                ["quality"] = quality,
                ["interval"] = effectiveInterval,
                ["frames"] = frames,
                ["skipped"] = skipped,
                ["avgRenderMs"] = avgRender,
                ["avgEncodeMs"] = avgEncode,
                ["avgJpegKB"] = avgKb,
                ["sendQueue"] = queueDepth
            };
            OverlordGameComponent.Instance?.Relay?.Broadcast(msg);
            OverlordGameComponent.Instance?.EmbeddedServer?.Broadcast(JsonHelper.ToJson(msg));
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

        private sealed class ViewerFrameParams
        {
            public ViewerSession session;
            public Pawn pawn;
            public Map map;
            public Vector3 center;
            public float radiusX;
            public float radiusZ;
            public float aspect;
            public float zoom;
            public int width;
            public int height;
            public int quality;
        }

        private ViewerFrameParams BuildFrameParams(ViewerSession session, int activeViewerCount)
        {
            var pawn = session.assignedPawn;
            var map = pawn?.Map;
            if (map == null || !pawn.Spawned)
                return null;

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

            return new ViewerFrameParams
            {
                session = session,
                pawn = pawn,
                map = map,
                center = cameraCenter,
                radiusX = radiusX,
                radiusZ = radiusZ,
                aspect = viewerAspect,
                zoom = cameraZoom,
                width = frameWidth,
                height = frameHeight,
                quality = frameQuality
            };
        }

        private const int MaxUnionRenderHeight = 1440;
        private const int MaxUnionRenderWidth = 2560;
        private const float MaxUnionRadiusFactor = 2.2f;

        /// <summary>
        /// Groups due viewers whose view rects overlap enough that a single map-mesh
        /// render can serve every member with a crop at native-or-better resolution.
        /// Members that would force an oversized union render (far-apart pawns,
        /// zoomed-in viewers) stay in their own group — grouping never trades
        /// visual quality, only redundant renders.
        /// </summary>
        private static List<List<ViewerFrameParams>> GroupCompatibleViews(List<ViewerFrameParams> dueParams)
        {
            var groups = new List<List<ViewerFrameParams>>();
            var used = new bool[dueParams.Count];
            for (int i = 0; i < dueParams.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var group = new List<ViewerFrameParams> { dueParams[i] };
                for (int j = i + 1; j < dueParams.Count; j++)
                {
                    if (used[j]) continue;
                    if (!ReferenceEquals(dueParams[j].map, dueParams[i].map)) continue;
                    group.Add(dueParams[j]);
                    if (TryComputeUnion(group, out _, out _, out _, out _, out _))
                        used[j] = true;
                    else
                        group.RemoveAt(group.Count - 1);
                }
                groups.Add(group);
            }
            return groups;
        }

        private static bool TryComputeUnion(List<ViewerFrameParams> group,
            out Vector3 unionCenter, out float unionRadiusX, out float unionRadiusZ,
            out int unionWidth, out int unionHeight)
        {
            unionCenter = default(Vector3);
            unionRadiusX = 0f;
            unionRadiusZ = 0f;
            unionWidth = 0;
            unionHeight = 0;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            float maxMemberRadiusZ = 0f;
            foreach (var m in group)
            {
                minX = Math.Min(minX, m.center.x - m.radiusX);
                maxX = Math.Max(maxX, m.center.x + m.radiusX);
                minZ = Math.Min(minZ, m.center.z - m.radiusZ);
                maxZ = Math.Max(maxZ, m.center.z + m.radiusZ);
                maxMemberRadiusZ = Math.Max(maxMemberRadiusZ, m.radiusZ);
            }

            unionRadiusX = (maxX - minX) / 2f;
            unionRadiusZ = (maxZ - minZ) / 2f;
            if (unionRadiusX <= 0f || unionRadiusZ <= 0f)
                return false;

            // One shared render must not cover a huge map area — mapDrawer cost
            // scales with the view rect.
            if (unionRadiusZ > maxMemberRadiusZ * MaxUnionRadiusFactor)
                return false;

            // The union render must give every member a crop of at least its native
            // frame size in BOTH axes, else the member's feed would upscale (blurry).
            float neededHeight = 0f;
            float neededWidth = 0f;
            foreach (var m in group)
            {
                neededHeight = Math.Max(neededHeight, m.height * (unionRadiusZ / m.radiusZ));
                neededWidth = Math.Max(neededWidth, m.width * (unionRadiusX / m.radiusX));
            }
            float unionAspect = unionRadiusX / unionRadiusZ;
            int h = Mathf.CeilToInt(Math.Max(neededHeight, neededWidth / unionAspect));
            if (h > MaxUnionRenderHeight)
                return false;
            int w = Mathf.CeilToInt(h * unionAspect);
            if (w > MaxUnionRenderWidth)
                return false;

            unionCenter = new Vector3((minX + maxX) / 2f, group[0].center.y, (minZ + maxZ) / 2f);
            unionWidth = w;
            unionHeight = h;
            return true;
        }

        private void RenderGroup(List<ViewerFrameParams> group)
        {
            if (group.Count == 1)
            {
                var p = group[0];
                var singleWatch = Stopwatch.StartNew();
                var singleRt = RenderMapArea(p.map, p.center, p.radiusZ, p.aspect, p.width, p.height);
                if (singleRt == null)
                    return;
                FinishViewerFrame(p, singleRt, singleWatch);
                initFailCount = 0;
                return;
            }

            if (!TryComputeUnion(group, out Vector3 unionCenter, out float unionRadiusX, out float unionRadiusZ,
                    out int unionWidth, out int unionHeight))
            {
                // Grouping already validated this; defensive fallback to singles.
                foreach (var m in group)
                    RenderGroup(new List<ViewerFrameParams> { m });
                return;
            }

            // One render + ONE readback for the whole group; per-member crops are
            // CPU row-copies on the worker. Crops preserve the union buffer's row
            // order, so orientation is identical to the solo path by construction —
            // this is the replacement for the Graphics.Blit crops that decoded
            // upside-down on D3D (Blit-written RTs have the opposite layout).
            var renderWatch = Stopwatch.StartNew();
            float unionAspect = unionRadiusX / unionRadiusZ;
            var unionRt = RenderMapArea(group[0].map, unionCenter, unionRadiusZ, unionAspect, unionWidth, unionHeight);
            if (unionRt == null)
                return;
            renderWatch.Stop();
            long renderMs = renderWatch.ElapsedMilliseconds;

            float unionMinX = unionCenter.x - unionRadiusX;
            float unionMinZ = unionCenter.z - unionRadiusZ;
            float unionWorldW = unionRadiusX * 2f;
            float unionWorldH = unionRadiusZ * 2f;

            // Main-thread phase: crop geometry + overlay ops + metadata (game reads).
            var crops = new List<MemberCrop>(group.Count);
            foreach (var m in group)
            {
                int cw = Mathf.Clamp(Mathf.RoundToInt(m.radiusX * 2f / unionWorldW * unionWidth), 16, unionWidth);
                int ch = Mathf.Clamp(Mathf.RoundToInt(m.radiusZ * 2f / unionWorldH * unionHeight), 16, unionHeight);
                int cx = Mathf.Clamp(Mathf.RoundToInt((m.center.x - m.radiusX - unionMinX) / unionWorldW * unionWidth), 0, unionWidth - cw);
                int cy = Mathf.Clamp(Mathf.RoundToInt((m.center.z - m.radiusZ - unionMinZ) / unionWorldH * unionHeight), 0, unionHeight - ch);

                List<MapOverlayPainter.DrawOp> ops;
                try
                {
                    ops = MapOverlayPainter.CollectOps(m.pawn, m.center.x, m.center.z, m.radiusX, m.radiusZ, cw, ch);
                }
                catch
                {
                    ops = new List<MapOverlayPainter.DrawOp>();
                }

                var metadata = new Dictionary<string, object>
                {
                    ["type"] = StateProtocol.MapFrame,
                    ["target"] = m.session.username,
                    ["centerX"] = m.center.x,
                    ["centerZ"] = m.center.z,
                    ["radiusX"] = m.radiusX,
                    ["radiusZ"] = m.radiusZ,
                    ["sourceWidth"] = cw,
                    ["sourceHeight"] = ch,
                    ["quality"] = m.quality,
                    ["cameraMode"] = "pawn",
                    ["zoom"] = m.zoom,
                    ["mapWidth"] = m.map.Size.x,
                    ["mapHeight"] = m.map.Size.z
                };

                crops.Add(new MemberCrop
                {
                    x = cx, yBottom = cy, width = cw, height = ch,
                    quality = m.quality, ops = ops, metadata = metadata,
                    target = m.session.username
                });
            }

            bool topDown = gpuTopDown;
            try
            {
                pendingReadbacks++;
                AsyncGPUReadback.Request(unionRt, 0, TextureFormat.RGBA32, req =>
                    OnUnionReadbackComplete(req, unionRt, unionWidth, unionHeight, crops, renderMs, topDown));
            }
            catch (Exception ex)
            {
                pendingReadbacks--;
                RenderTexture.ReleaseTemporary(unionRt);
                asyncPipelineBroken = true;
                LogUtil.Warn($"Union readback request failed — reverting to main-thread capture: {ex.Message}");
                return;
            }

            initFailCount = 0;
        }

        private sealed class MemberCrop
        {
            public int x;
            public int yBottom;   // crop origin measured from the world-bottom row
            public int width;
            public int height;
            public int quality;
            public List<MapOverlayPainter.DrawOp> ops;
            public Dictionary<string, object> metadata;
            public string target;
        }

        // Main thread (readback callbacks run in the player loop).
        private void OnUnionReadbackComplete(AsyncGPUReadbackRequest req, RenderTexture rt, int unionWidth, int unionHeight,
            List<MemberCrop> crops, long renderMs, bool topDown)
        {
            pendingReadbacks--;
            RenderTexture.ReleaseTemporary(rt);

            if (!initialized)
                return;

            if (req.hasError)
            {
                lock (statsLock) { statsSkipped += crops.Count; }
                consecutiveReadbackErrors++;
                if (consecutiveReadbackErrors >= 8 && !asyncPipelineBroken)
                {
                    asyncPipelineBroken = true;
                    LogUtil.Warn("Async readbacks repeatedly erroring — reverting to main-thread capture");
                }
                return;
            }
            consecutiveReadbackErrors = 0;

            byte[] unionPixels;
            try
            {
                unionPixels = req.GetData<byte>().ToArray();
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Union readback copy failed: {ex.Message}");
                return;
            }

            sendQueue.Enqueue(() =>
            {
                bool first = true;
                foreach (var crop in crops)
                {
                    try
                    {
                        var pixels = new byte[crop.width * crop.height * 4];
                        int unionStride = unionWidth * 4;
                        int cropStride = crop.width * 4;
                        // Preserve union row ORDER: for a top-down union buffer, the
                        // crop's first stored row is its visually-topmost row, i.e.
                        // union row (H - yBottom - height); bottom-up starts at yBottom.
                        int startRow = topDown ? (unionHeight - crop.yBottom - crop.height) : crop.yBottom;
                        for (int r = 0; r < crop.height; r++)
                        {
                            Buffer.BlockCopy(unionPixels,
                                (startRow + r) * unionStride + crop.x * 4,
                                pixels, r * cropStride, cropStride);
                        }

                        MapOverlayPainter.RasterizeToBuffer(pixels, crop.width, crop.height, topDown, crop.ops);
                        if (topDown)
                            MapOverlayPainter.FlipRowsInPlace(pixels, crop.width, crop.height);

                        var encodeWatch = Stopwatch.StartNew();
                        byte[] jpeg = ImageConversion.EncodeArrayToJPG(
                            pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)crop.width, (uint)crop.height, 0, crop.quality);
                        encodeWatch.Stop();

                        RecordFrameStats(first ? renderMs : 0, encodeWatch.ElapsedMilliseconds, jpeg.Length,
                            crop.width, crop.height, crop.quality, "pawn");
                        first = false;
                        SendMapFrame(crop.metadata, jpeg, crop.target);
                    }
                    catch (Exception ex)
                    {
                        asyncPipelineBroken = true;
                        LogUtil.Warn($"Union crop encode failed — reverting to main-thread capture: {ex.Message}");
                        return;
                    }
                }
            });
        }

        private void FinishViewerFrame(ViewerFrameParams p, RenderTexture rt, Stopwatch renderWatch)
        {
            // Overlay ops read live game state — collect on the main thread at capture
            // time; rasterization happens wherever the pixels end up (worker or here).
            List<MapOverlayPainter.DrawOp> ops;
            try
            {
                ops = MapOverlayPainter.CollectOps(p.pawn, p.center.x, p.center.z, p.radiusX, p.radiusZ, p.width, p.height);
            }
            catch
            {
                ops = new List<MapOverlayPainter.DrawOp>();
            }

            var metadata = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.MapFrame,
                ["target"] = p.session.username,
                ["centerX"] = p.center.x,
                ["centerZ"] = p.center.z,
                ["radiusX"] = p.radiusX,
                ["radiusZ"] = p.radiusZ,
                ["sourceWidth"] = p.width,
                ["sourceHeight"] = p.height,
                ["quality"] = p.quality,
                ["cameraMode"] = "pawn",
                ["zoom"] = p.zoom,
                ["mapWidth"] = p.map.Size.x,
                ["mapHeight"] = p.map.Size.z
            };

            DispatchFrame(rt, p.width, p.height, p.quality, ops, metadata, p.session.username, renderWatch, "pawn");
        }

        /// <summary>
        /// Hands a rendered frame to the capture pipeline. Async path: request a GPU
        /// readback (no ReadPixels stall) and rasterize+encode+send on the worker.
        /// Fallback path: legacy synchronous ReadPixels + main-thread EncodeToJPG,
        /// used when async readback is unsupported or the off-thread encode failed.
        /// </summary>
        private void DispatchFrame(RenderTexture rt, int width, int height, int quality,
            List<MapOverlayPainter.DrawOp> ops, Dictionary<string, object> metadata,
            string embeddedTarget, Stopwatch renderWatch, string cameraMode)
        {
            if (asyncSupported && !asyncPipelineBroken)
            {
                renderWatch.Stop();
                long renderMs = renderWatch.ElapsedMilliseconds;
                bool topDown = gpuTopDown;
                try
                {
                    pendingReadbacks++;
                    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
                        OnReadbackComplete(req, rt, width, height, quality, ops, metadata, embeddedTarget, renderMs, cameraMode, topDown));
                }
                catch (Exception ex)
                {
                    // A throwing Request leaks the counter and the RT if unhandled —
                    // and means async readback doesn't actually work here. Revert.
                    pendingReadbacks--;
                    RenderTexture.ReleaseTemporary(rt);
                    asyncPipelineBroken = true;
                    LogUtil.Warn($"AsyncGPUReadback.Request failed — reverting to main-thread capture: {ex.Message}");
                }
                return;
            }

            SyncCaptureAndSend(rt, width, height, quality, ops, metadata, embeddedTarget, renderWatch, cameraMode);
        }

        // Unity invokes readback callbacks on the main thread during the player loop.
        private void OnReadbackComplete(AsyncGPUReadbackRequest req, RenderTexture rt, int width, int height, int quality,
            List<MapOverlayPainter.DrawOp> ops, Dictionary<string, object> metadata,
            string embeddedTarget, long renderMs, string cameraMode, bool topDown)
        {
            pendingReadbacks--;
            RenderTexture.ReleaseTemporary(rt);

            if (!initialized)
                return;

            if (req.hasError)
            {
                lock (statsLock) { statsSkipped++; }
                // A driver that consistently errors readbacks would otherwise drop
                // every frame silently, forever. Latch to the legacy path after a
                // streak; one-off errors just skip a frame.
                consecutiveReadbackErrors++;
                if (consecutiveReadbackErrors >= 8 && !asyncPipelineBroken)
                {
                    asyncPipelineBroken = true;
                    LogUtil.Warn("Async readbacks repeatedly erroring — reverting to main-thread capture");
                }
                return;
            }
            consecutiveReadbackErrors = 0;

            byte[] pixels;
            try
            {
                pixels = req.GetData<byte>().ToArray();
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Readback copy failed: {ex.Message}");
                return;
            }

            sendQueue.Enqueue(() =>
            {
                byte[] jpeg;
                try
                {
                    MapOverlayPainter.RasterizeToBuffer(pixels, width, height, topDown, ops);
                    // Unity's image encoders consume Texture2D raw-data convention
                    // (row 0 = BOTTOM). D3D readbacks arrive top-down and must be
                    // flipped; GL readbacks are already bottom-up.
                    if (topDown)
                        MapOverlayPainter.FlipRowsInPlace(pixels, width, height);

                    var encodeWatch = Stopwatch.StartNew();
                    jpeg = ImageConversion.EncodeArrayToJPG(
                        pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height, 0, quality);
                    encodeWatch.Stop();

                    RecordFrameStats(renderMs, encodeWatch.ElapsedMilliseconds, jpeg.Length, width, height, quality, cameraMode);
                }
                catch (Exception ex)
                {
                    // EncodeArrayToJPG off the main thread is the one unproven Unity call
                    // in this pipeline. An encode/rasterize failure permanently reverts
                    // to the legacy main-thread path; the current frame is dropped.
                    asyncPipelineBroken = true;
                    LogUtil.Warn($"Async frame encode failed — reverting to main-thread capture: {ex.Message}");
                    return;
                }

                // Send failures (relay blip mid-reconnect) must NOT latch the
                // pipeline-broken flag — the legacy path swallows them too.
                try
                {
                    SendMapFrame(metadata, jpeg, embeddedTarget);
                }
                catch { }
            });
        }

        // Legacy path: synchronous ReadPixels (GPU pipeline stall) + main-thread encode.
        private void SyncCaptureAndSend(RenderTexture rt, int width, int height, int quality,
            List<MapOverlayPainter.DrawOp> ops, Dictionary<string, object> metadata,
            string embeddedTarget, Stopwatch renderWatch, string cameraMode)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.filterMode = FilterMode.Bilinear;
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prevActive;
            renderWatch.Stop();

            RenderTexture.ReleaseTemporary(rt);

            try
            {
                MapOverlayPainter.RasterizeToTexture(tex, ops);
            }
            catch { }

            byte[] jpeg;
            var encodeWatch = Stopwatch.StartNew();
            try
            {
                jpeg = tex.EncodeToJPG(quality);
            }
            finally
            {
                encodeWatch.Stop();
                UnityEngine.Object.Destroy(tex);
            }

            RecordFrameStats(renderWatch.ElapsedMilliseconds, encodeWatch.ElapsedMilliseconds, jpeg.Length, width, height, quality, cameraMode);

            sendQueue.Enqueue(() =>
            {
                try
                {
                    SendMapFrame(metadata, jpeg, embeddedTarget);
                }
                catch { }
            });
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

            float radiusZ = gameCamera.orthographic ? Mathf.Max(1f, gameCamera.orthographicSize) : Mathf.Clamp(frameHeight / 32f, 8f, 24f);
            float radiusX = radiusZ * aspect;

            var metadata = new Dictionary<string, object>
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
                ["mapWidth"] = map.Size.x,
                ["mapHeight"] = map.Size.z
            };

            // Host-camera frames carry no overlay markers (same as the legacy path).
            DispatchFrame(rt, frameWidth, frameHeight, frameQuality,
                new List<MapOverlayPainter.DrawOp>(), metadata, null, renderWatch, "host");

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
