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

        // ── Adaptive bandwidth pressure (main-thread only) ──────────────────────
        // Viewer COUNT is the wrong axis for load: 8 wide-viewport viewers backed the
        // send queue up worse than 15 narrow ones (2026-07-10 telemetry). The real
        // signal is queue depth — how many encoded frames are waiting to go out. This
        // 0..1 value rises when the pipe is backed up and decays when it drains; it
        // pushes quality down and interval up ON TOP of the count-based floors, so a
        // congested feed degrades gracefully instead of stalling. Sampled once per
        // pump from the pre-render queue depth.
        private float bandwidthPressure;
        private float lastPressureSampleTime;

        private void SampleBandwidthPressure(int inFlight, int activeViewerCount)
        {
            float now = Time.time;
            float dt = now - lastPressureSampleTime;
            lastPressureSampleTime = now;
            if (dt <= 0f || dt > 1f) dt = 0.1f; // clamp first-sample / long-gap spikes

            // Healthy depth scales with audience (each viewer legitimately has ~1-2
            // frames in flight). Above that, the pipe is falling behind.
            float healthy = Math.Max(2, activeViewerCount);
            float overshoot = (inFlight - healthy) / Math.Max(4f, healthy); // 0 at healthy, 1 at ~2x
            float target = Mathf.Clamp01(overshoot);

            // Asymmetric: react fast to congestion, recover slowly (avoids quality
            // oscillation that reads as flicker).
            float rate = target > bandwidthPressure ? 6f : 1.5f;
            bandwidthPressure = Mathf.Clamp01(bandwidthPressure + (target - bandwidthPressure) * Mathf.Clamp01(rate * dt));
        }

        // ── Dedicated capture camera ────────────────────────────────────────────
        // Captures render through OUR OWN invisible camera, never the live game
        // camera. The live camera's transform/ortho/target/projection are never
        // touched, so no capture bug can corrupt the streamer's view or zoom — the
        // entire borrow/restore class of failures is structurally gone.
        private static Camera captureCamera;

        private static Camera GetCaptureCamera(Camera template)
        {
            if (captureCamera != null)
                return captureCamera;
            var go = new GameObject("Overlord_CaptureCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            captureCamera = go.AddComponent<Camera>();
            captureCamera.CopyFrom(template);   // culling mask, depth, clear settings
            captureCamera.enabled = false;      // manual Render() only — never auto-renders
            return captureCamera;
        }

        private static void DestroyCaptureCamera()
        {
            if (captureCamera != null)
            {
                UnityEngine.Object.Destroy(captureCamera.gameObject);
                captureCamera = null;
            }
        }

        // Orientation-proof probe: when <persistentDataPath>/overlord_dump_frames
        // exists, the encode worker dumps up to MaxFrameDumps raw pre-encode
        // buffers labeled current/other map — the ground-truth artifact for any
        // future "inverted map" report instead of another mechanism theory.
        private static string probeDir;           // captured on main thread in Start()
        private static int framesDumped;
        private const int MaxFrameDumps = 12;

        private static void DumpFrameProbe(byte[] pixels, int width, int height, Dictionary<string, object> metadata)
        {
            try
            {
                if (probeDir == null || framesDumped >= MaxFrameDumps)
                    return;
                if (!System.IO.File.Exists(System.IO.Path.Combine(probeDir, "overlord_dump_frames")))
                    return;

                int n = Interlocked.Increment(ref framesDumped);
                if (n > MaxFrameDumps)
                    return;
                object cur;
                string curLabel = metadata != null && metadata.TryGetValue("mapIsCurrent", out cur) && cur is bool b
                    ? (b ? "curmap" : "othermap") : "unknown";
                string mode = metadata != null && metadata.TryGetValue("cameraMode", out object cm) ? cm as string : "?";
                string path = System.IO.Path.Combine(probeDir, $"frame_{n:D2}_{mode}_{curLabel}_{width}x{height}.rgba");
                System.IO.File.WriteAllBytes(path, pixels);
            }
            catch { }
        }
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
            try { probeDir = Application.persistentDataPath; } catch { probeDir = null; }
            asyncPipelineBroken = false;
            pendingReadbacks = 0;
            string buildStamp = "unknown";
            try
            {
                var loc = typeof(MapRenderer).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                    buildStamp = System.IO.File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { }
            LogUtil.Log($"Map capture pipeline: asyncReadback={asyncSupported} uvTopDown={gpuTopDown} build={buildStamp} captureDisabled={OverlordMod.Settings?.disableMapCapture == true}");

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
            DestroyCaptureCamera();
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

        private float lastSpectatorFrameTime;
        private const float SpectatorIntervalSec = 0.5f;

        /// <summary>
        /// Broadcasts a colony-overview frame for viewers waiting in the lobby.
        /// Sent un-targeted (one host→relay upload regardless of audience size)
        /// with cameraMode="spectate"; clients in the lobby render it, assigned
        /// viewers discard it. Waiting viewers watch the colony live instead of
        /// staring at a queue message.
        /// </summary>
        private void MaybeRenderSpectatorFrame(ViewerManager viewers)
        {
            if (!asyncSupported || asyncPipelineBroken)
                return;
            if (OverlordMod.Settings?.mirrorHostCameraToViewers == true)
                return; // that mode already broadcasts a shared view
            if (Time.time - lastSpectatorFrameTime < SpectatorIntervalSec)
                return;
            if (sendQueue.Count + pendingReadbacks > 4)
                return;

            var lobbyTargets = new List<string>();
            foreach (var s in viewers.AllSessions)
            {
                if (s != null && s.isConnected && !s.HasPawn && !string.IsNullOrEmpty(s.username))
                    lobbyTargets.Add(s.username);
            }
            if (lobbyTargets.Count == 0)
                return;

            var map = Find.CurrentMap;
            if (map == null)
                return;

            // Frame the colony: colonist bounding box with margin, sane clamps.
            const float aspect = 16f / 9f;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            int count = 0;
            foreach (var colonist in map.mapPawns.FreeColonists)
            {
                if (colonist == null || !colonist.Spawned) continue;
                var pos = colonist.DrawPos;
                minX = Math.Min(minX, pos.x); maxX = Math.Max(maxX, pos.x);
                minZ = Math.Min(minZ, pos.z); maxZ = Math.Max(maxZ, pos.z);
                count++;
            }

            Vector3 center;
            float radiusZ;
            if (count == 0)
            {
                center = new Vector3(map.Size.x / 2f, 0f, map.Size.z / 2f);
                radiusZ = 40f;
            }
            else
            {
                center = new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
                radiusZ = Mathf.Clamp(Mathf.Max((maxZ - minZ) / 2f, ((maxX - minX) / 2f) / aspect) + 10f, 24f, 48f);
            }

            lastSpectatorFrameTime = Time.time;

            const int specW = 960, specH = 540, specQuality = 55;
            var watch = Stopwatch.StartNew();
            var rt = RenderMapArea(map, center, radiusZ, aspect, specW, specH);
            if (rt == null)
                return;

            var metadata = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.MapFrame,
                ["centerX"] = center.x,
                ["centerZ"] = center.z,
                ["radiusX"] = radiusZ * aspect,
                ["radiusZ"] = radiusZ,
                ["sourceWidth"] = specW,
                ["sourceHeight"] = specH,
                ["quality"] = specQuality,
                ["cameraMode"] = "spectate",
                ["zoom"] = 1f,
                ["mapWidth"] = map.Size.x,
                ["mapHeight"] = map.Size.z
            };

            // Targeted per lobby viewer: assigned viewers download nothing, old
            // cached clients never see broadcast spectate frames, and tactical-map
            // audiences aren't re-exposed to JPEG traffic. Lobby counts are small.
            DispatchFrame(rt, specW, specH, specQuality, new List<MapOverlayPainter.DrawOp>(), metadata, null, watch, "spectate", lobbyTargets);
        }

        private void RenderDueFrames(ViewerManager viewers)
        {
            RefreshSettings();

            // Troubleshooting kill-switch: no captures at all — no camera borrow, no
            // map-draw commands, no shader-global writes. Total isolation lever.
            if (OverlordMod.Settings?.disableMapCapture == true)
                return;

            var activeSessions = viewers.AllSessions
                .Where(s => s != null && s.isConnected && s.HasPawn && s.assignedPawn.Map != null && s.assignedPawn.Spawned)
                .ToList();

            if (activeSessions.Count == 0)
            {
                // Nobody assigned — the pump is idle, so a spectate render (for
                // lobby-only audiences, e.g. stream start) can never stack with
                // group renders in the same frame.
                MaybeRenderSpectatorFrame(viewers);
                return;
            }

            if (OverlordMod.Settings?.allowViewerTacticalMap == true &&
                OverlordMod.Settings?.mirrorHostCameraToViewers != true)
            {
                return;
            }

            // Backpressure counts frames still in flight on the GPU (async readbacks)
            // as well as frames waiting on the encode/send worker.
            int queueDepth = sendQueue.Count + pendingReadbacks;
            // Update the adaptive controller from the true in-flight depth BEFORE
            // computing this pump's interval/quality, so both react to live congestion.
            SampleBandwidthPressure(queueDepth, activeSessions.Count);
            float effectiveInterval = GetEffectiveInterval(activeSessions.Count);
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
                // Idle pump — no group renders this frame, so the spectate render
                // never exceeds the per-pump render budget (review finding).
                MaybeRenderSpectatorFrame(viewers);
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
            // Congestion also stretches the interval up to +60ms — fewer frames/sec
            // per viewer when the pipe can't keep up. Recovers as pressure decays.
            minimum += bandwidthPressure * 0.06f;
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
            // Under measured congestion, shed up to 18 quality points before the
            // hard floor — smaller frames drain the queue instead of stalling it.
            quality -= Mathf.RoundToInt(bandwidthPressure * 18f);
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
                $"Map stats viewers={activeViewerCount} mode={cameraMode} render={width}x{height} quality={quality} interval={effectiveInterval:F2}s frames={frames} skipped={skipped} avgRenderMs={avgRender} avgEncodeMs={avgEncode} avgJpegKB={avgKb} sendQueue={queueDepth} pressure={bandwidthPressure:F2}"
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
            var liveCamera = Find.Camera;
            if (liveCamera == null || map == null)
                return null;

            // Resolved BEFORE the TemporarilySetCurrentMap swap below: is this
            // capture of the map the streamer is actually looking at?
            bool sameAsLiveMap = map == Find.CurrentMap;

            // Render through OUR dedicated camera. The live camera is used only as a
            // template (culling mask, depth, HDR settings) and is NEVER mutated —
            // the whole borrow/restore failure class (leaked transform/ortho/target/
            // projection corrupting the streamer's view or zoom) is structurally gone.
            var cam = GetCaptureCamera(liveCamera);
            var savedActive = RenderTexture.active;

            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;

            try
            {
                cam.CopyFrom(liveCamera);       // keep culling/post settings current
                cam.enabled = false;            // CopyFrom copies enabled; stay manual
                cam.transform.position = new Vector3(center.x, 40f, center.z);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.orthographic = true;
                cam.orthographicSize = radiusZ;
                cam.aspect = aspect;
                cam.targetTexture = rt;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.08f, 0.08f, 1f);
                // CopyFrom copies a custom projection matrix too (Camera+ sets one on
                // the live camera) — reset so our ortho fields define the projection.
                cam.ResetProjectionMatrix();

                CellRect viewRect = BuildViewRect(center, radiusZ, aspect, map);
                using (RimWorldCompat.TemporarilySetCurrentMap(map))
                using (MapRenderContext.Begin(viewRect, closeZoom: true))
                {
                    // The override must span the DRAW COMMANDS too: DrawMapMesh selects
                    // which terrain sections to draw by reading CurrentViewRect at call
                    // time (gating it to Render()-only made viewer frames draw the
                    // STREAMER's visible sections instead of the viewer's area). With a
                    // dedicated camera there is no live camera state for the override
                    // window to corrupt, and by end-of-frame the live cameras have
                    // already rendered.
                    MapRenderContext.MarkCaptureActive(true);
                    try
                    {
                        QueueMapDrawCommands(map, sameAsLiveMap);
                        cam.Render();
                    }
                    finally
                    {
                        MapRenderContext.MarkCaptureActive(false);
                    }
                }

                // QueueMapDrawCommands set GLOBAL shader state (fall colors, water
                // textures) for the CAPTURE map on cross-map captures. RimWorld only
                // re-sets those on map/season change, so restore them for the map the
                // streamer is actually looking at.
                var liveMap = Find.CurrentMap;
                if (liveMap != null && liveMap != map)
                {
                    try
                    {
                        PlantFallColors.SetFallShaderGlobals(liveMap);
                        liveMap.waterInfo?.SetTextures();
                    }
                    catch { }
                }
            }
            finally
            {
                RenderTexture.active = savedActive;
                cam.targetTexture = null; // never hold the temp RT across frames
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

        private static void QueueMapDrawCommands(Map map, bool sameAsLiveMap)
        {
            if (map == null)
                return;

            // PURE-DRAW captures for the streamer's own map. The kill-switch test
            // (2026-07-13) proved the capture pipeline corrupts the live view; the
            // mutating per-frame updates below were the remaining mutation surface.
            // The game ALREADY runs all of them every frame for the current map —
            // re-running them ~10x/s under our swapped camera/map context was never
            // necessary. Every mutation the capture doesn't perform is corruption it
            // cannot cause. Cross-map captures (caravan viewers) still need them,
            // because nothing else updates a non-current map's drawer state.
            // Bisect ladder (settings, live-read): 0=full, 1=camera+render only,
            // 2=+terrain, 3=+pawns, 4=+conditions. Whichever level first reproduces
            // a live-view bug names the culprit call — no theorizing required.
            int bisect = OverlordMod.Settings?.captureBisectLevel ?? 0;
            if (bisect == 1)
                return; // render an empty frame: isolates the camera borrow itself

            if (!sameAsLiveMap)
            {
                map.powerNetManager?.UpdatePowerNetsAndConnections_First();
                map.glowGrid?.GlowGridUpdate_First();
                PlantFallColors.SetFallShaderGlobals(map);
                map.waterInfo?.SetTextures();
                map.mapDrawer?.MapMeshDrawerUpdate_First();
            }

            map.mapDrawer?.DrawMapMesh();
            if (bisect == 2) return;
            map.dynamicDrawManager?.DrawDynamicThings();
            if (bisect == 3) return;
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
                    ["mapHeight"] = m.map.Size.z,
                    ["mapIsCurrent"] = ReferenceEquals(m.map, Find.CurrentMap)
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
                for (int c = 0; c < crops.Count; c++)
                {
                    var crop = crops[c];
                    byte[] jpeg;
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

                        DumpFrameProbe(pixels, crop.width, crop.height, crop.metadata);

                        var encodeWatch = Stopwatch.StartNew();
                        jpeg = ImageConversion.EncodeArrayToJPG(
                            pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)crop.width, (uint)crop.height, 0, crop.quality);
                        encodeWatch.Stop();

                        RecordFrameStats(c == 0 ? renderMs : 0, encodeWatch.ElapsedMilliseconds, jpeg.Length,
                            crop.width, crop.height, crop.quality, "pawn");
                    }
                    catch (Exception ex)
                    {
                        // Rasterize/encode failure latches the fallback; count the
                        // group's undelivered crops so the drop is visible in stats.
                        asyncPipelineBroken = true;
                        lock (statsLock) { statsSkipped += crops.Count - c; }
                        LogUtil.Warn($"Union crop encode failed — reverting to main-thread capture: {ex.Message}");
                        return;
                    }

                    // Send failures (relay blip) must NOT latch the pipeline —
                    // same invariant as the solo path.
                    try
                    {
                        SendMapFrame(crop.metadata, jpeg, crop.target);
                    }
                    catch { }
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
                ["mapHeight"] = p.map.Size.z,
                ["mapIsCurrent"] = ReferenceEquals(p.map, Find.CurrentMap)
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
            string embeddedTarget, Stopwatch renderWatch, string cameraMode,
            List<string> fanoutTargets = null)
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
                        OnReadbackComplete(req, rt, width, height, quality, ops, metadata, embeddedTarget, renderMs, cameraMode, topDown, fanoutTargets));
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
            string embeddedTarget, long renderMs, string cameraMode, bool topDown,
            List<string> fanoutTargets = null)
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

                    DumpFrameProbe(pixels, width, height, metadata);

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
                    if (fanoutTargets != null)
                    {
                        // One encode, many recipients (spectator frames): clone the
                        // metadata with each target so routing stays per-viewer.
                        foreach (var target in fanoutTargets)
                        {
                            var targeted = new Dictionary<string, object>(metadata) { ["target"] = target };
                            SendMapFrame(targeted, jpeg, target);
                        }
                    }
                    else
                    {
                        SendMapFrame(metadata, jpeg, embeddedTarget);
                    }
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

            // Same dedicated-camera rule as RenderMapArea: mirror the live view by
            // COPYING the live camera, never mutating it.
            var cam = GetCaptureCamera(gameCamera);
            RenderTexture savedActive = RenderTexture.active;
            try
            {
                cam.CopyFrom(gameCamera);
                cam.enabled = false;
                cam.transform.position = gameCamera.transform.position;
                cam.transform.rotation = gameCamera.transform.rotation;
                cam.aspect = aspect;
                cam.targetTexture = rt;
                cam.Render();
            }
            finally
            {
                RenderTexture.active = savedActive;
                cam.targetTexture = null;
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
