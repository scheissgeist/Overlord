using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Core GameComponent. Handles lifecycle, state sync ticking, relay connection,
    /// map rendering, and colonist bar overlay.
    /// </summary>
    public class OverlordGameComponent : GameComponent
    {
        public static OverlordGameComponent Instance { get; private set; }

        private RelayClient relayClient;
        private EmbeddedWebServer embeddedServer;
        private MapRenderer mapRenderer;
        private ViewerManager viewerManager;
        private bool initialized;
        private readonly ThreadSafeQueue<Action> embeddedQueue = new ThreadSafeQueue<Action>();
        private readonly Queue<PortraitRequest> portraitQueue = new Queue<PortraitRequest>();
        private readonly HashSet<string> pendingPortraitKeys = new HashSet<string>();
        private readonly Dictionary<int, string> portraitCache = new Dictionary<int, string>();
        private float nextPortraitTime;

        private class PortraitRequest
        {
            public string username;
            public Pawn pawn;
            public int pawnId;
            public string key;
        }

        public OverlordGameComponent(Game game)
        {
            Instance = this;
        }

        public void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            RespawnManager.ClearCooldowns();
            if (viewerManager == null)
                viewerManager = new ViewerManager();

            var settings = OverlordMod.Settings;

            // Start relay client if URL configured
            if (!string.IsNullOrEmpty(settings.relayUrl))
            {
                relayClient = new RelayClient(settings.relayUrl, settings.hostSecret);
                relayClient.OnConnected += () =>
                {
                    LogUtil.Log("Connected to relay server");
                    SendHostCapabilities(adminOnly: true);
                    viewerManager?.SendColonistList();
                };
                relayClient.OnDisconnected += () => LogUtil.Warn("Disconnected from relay server");
                relayClient.OnMessageReceived += OnRelayMessage;
                relayClient.Connect();
            }
            else
            {
                // No relay — start embedded local server
                embeddedServer = new EmbeddedWebServer(settings.localPort);
                // Queue all embedded server callbacks to main thread
                embeddedServer.OnViewerConnected += (user, display) =>
                {
                    embeddedQueue.Enqueue(() =>
                    {
                        viewerManager.GetOrCreateSession(user, display);
                        LogUtil.Log($"Local viewer connected: {display}");
                        viewerManager.SendColonistList(user);
                        SendHostCapabilities(user);
                        viewerManager.SendPermissions(user);
                        SendToolkitStatePublic(user);
                        viewerManager.SendGameInfoNow(user);
                    });
                };
                embeddedServer.OnViewerDisconnected += user =>
                {
                    embeddedQueue.Enqueue(() =>
                    {
                        viewerManager?.MarkDisconnected(user);
                        LogUtil.Log($"Local viewer disconnected: {user}");
                    });
                };
                embeddedServer.OnCommandReceived += (user, json) =>
                {
                    embeddedQueue.Enqueue(() =>
                    {
                        string type = JsonHelper.ExtractString(json, "type");
                        if (type == StateProtocol.Command || type == "command")
                            HandleCommand(json);
                        else if (type == StateProtocol.RequestState || type == StateProtocol.StateResyncRequest)
                            HandleRequestState(json);
                        else if (type == StateProtocol.Assign && IsAdminMessage(json))
                            HandleAssign(json);
                        else if (type == StateProtocol.Unassign && IsAdminMessage(json))
                            HandleUnassign(json);
                        else if (type == StateProtocol.ClaimResponse && IsAdminMessage(json))
                            HandleClaimResponse(json);
                        else if (type == "chat")
                            HandleChat(json);
                    });
                };
                embeddedServer.Start();
                LogUtil.Log($"Embedded server started on port {settings.localPort}");
            }

            // Map renderer (used by both relay and embedded paths)
            mapRenderer = new MapRenderer();
            mapRenderer.Start();

            RimWorldCompat.LogCapabilitiesOnce();
            LogUtil.Log("Overlord initialized");
        }

        public override void GameComponentTick()
        {
            if (!initialized)
                return;

            relayClient?.ProcessQueue();
            embeddedQueue.DrainTo(action => action());
            viewerManager?.Tick();
        }

        public override void GameComponentUpdate()
        {
            if (!initialized || viewerManager == null || mapRenderer == null)
                return;

            mapRenderer.Update(viewerManager);
            ProcessPortraitQueue();
        }

        public override void GameComponentOnGUI()
        {
            if (!initialized || viewerManager == null)
                return;

            // Only draw overlay outside of loading events
            if (Event.current.type == EventType.Repaint || Event.current.type == EventType.Layout)
                ColonistBarOverlay.Draw(viewerManager);

            ClaimAlertOverlay.Draw(this, viewerManager);
        }

        public override void ExposeData()
        {
            if (viewerManager == null)
                viewerManager = new ViewerManager();

            Scribe_Deep.Look(ref viewerManager, "viewerManager");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (viewerManager == null)
                    viewerManager = new ViewerManager();
            }
        }

        public void Shutdown()
        {
            mapRenderer?.Shutdown();
            mapRenderer = null;

            embeddedServer?.Stop();
            embeddedServer = null;

            relayClient?.Disconnect();
            relayClient = null;

            portraitQueue.Clear();
            pendingPortraitKeys.Clear();
            portraitCache.Clear();

            initialized = false;
            Instance = null;
            LogUtil.Log("Overlord shut down");
        }

        private void OnRelayMessage(string json)
        {
            try
            {
                string type = JsonHelper.ExtractString(json, "type");
                if (type == null) return;

                switch (type)
                {
                    case StateProtocol.ViewerJoined:   HandleViewerJoined(json); break;
                    case StateProtocol.ViewerLeft:     HandleViewerLeft(json);   break;
                    case StateProtocol.Command:        HandleCommand(json);      break;
                    case StateProtocol.RequestState:
                    case StateProtocol.StateResyncRequest:
                        HandleRequestState(json);
                        break;
                    case StateProtocol.Assign:         if (IsAdminMessage(json)) HandleAssign(json);       break;
                    case StateProtocol.Unassign:       if (IsAdminMessage(json)) HandleUnassign(json);     break;
                    case StateProtocol.ClaimResponse:  if (IsAdminMessage(json)) HandleClaimResponse(json); break;
                    case "chat":                       HandleChat(json);         break;
                    case "request_colonist_list":       viewerManager?.SendColonistList(JsonHelper.ExtractLastString(json, "username")); break;
                }
            }
            catch (Exception ex)
            {
                LogUtil.Error($"Error processing relay message: {ex.Message}");
            }
        }

        private void HandleViewerJoined(string json)
        {
            string username = JsonHelper.ExtractString(json, "username");
            string displayName = JsonHelper.ExtractString(json, "displayName") ?? username;
            if (username == null) return;

            var session = viewerManager.GetOrCreateSession(username, displayName);
            LogUtil.Log($"Viewer joined: {displayName} ({username})");
            viewerManager.SendColonistList(username);
            SendHostCapabilities(username);
            viewerManager.SendPermissions(username);
            SendToolkitStatePublic(username);
            // Force game_info so late joiners don't stay stuck on "Host waiting".
            viewerManager.SendGameInfoNow(username);

            // Alert the streamer when a connected viewer is waiting for a pawn.
            if (session != null && session.isConnected && !session.HasPawn
                && viewerManager.GetPendingClaim(username) == null)
            {
                ClaimAlertOverlay.NotifyWaitingViewer(session);
                Messages.Message(
                    $"[Overlord] {displayName} is waiting for a colonist.",
                    MessageTypeDefOf.NeutralEvent,
                    historical: false
                );
            }
        }

        private void HandleViewerLeft(string json)
        {
            string username = JsonHelper.ExtractString(json, "username");
            if (username == null) return;
            viewerManager?.MarkDisconnected(username);
            LogUtil.Log($"Viewer left: {username}");
        }

        private void HandleCommand(string json)
        {
            bool isAdminCommand = IsAdminMessage(json);
            string action = JsonHelper.ExtractLastString(json, "action") ?? "unknown";
            string username = JsonHelper.ExtractLastString(json, "username");
            var result = PawnCommandRouter.Execute(json, viewerManager);
            bool silent = result != null &&
                result.TryGetValue("silent", out object silentValue) &&
                silentValue is bool silentBool &&
                silentBool;
            bool ok = result != null && result.TryGetValue("ok", out object okValue) && okValue is bool okBool && okBool;
            // Commands are the only way slow-tier signature fields (work priorities,
            // schedule, policies, gear) change instantly — drop the cached slow sub-hash
            // so the next sync cycle reflects the command without waiting out the cache.
            if (ok && !string.IsNullOrEmpty(username))
                PawnStateSerializer.InvalidateSignatureCache(viewerManager?.GetSession(username)?.assignedPawn);
            string resultMessage = result != null && result.TryGetValue("message", out object messageValue)
                ? messageValue?.ToString()
                : "";
            if (result != null)
                result["action"] = action;
            if (!silent)
            {
                LogUtil.Log($"Command source={(isAdminCommand ? "admin" : "viewer")} user={username ?? ""} action={action} ok={ok} message={resultMessage}");
                if (!isAdminCommand && !string.IsNullOrEmpty(username))
                {
                    ActionLog.Append(
                        ok ? ActionLogKind.Command : ActionLogKind.CommandFailed,
                        username, action, resultMessage,
                        viewerManager?.GetSession(username)?.assignedPawn?.thingIDNumber);
                }
            }

            if (isAdminCommand && !silent)
            {
                var adminResult = new Dictionary<string, object>(result)
                {
                    ["adminOnly"] = true,
                    ["username"] = username ?? ""
                };
                relayClient?.Broadcast(adminResult);
            }

            if (silent || string.IsNullOrEmpty(username)) return;

            SendToViewer(username, result);
        }

        private void HandleRequestState(string json)
        {
            string username = JsonHelper.ExtractLastString(json, "username");
            if (username == null) return;
            SendToolkitStatePublic(username);
            HandleRequestStatePublic(username);
        }

        private void HandleChat(string json)
        {
            string username = JsonHelper.ExtractLastString(json, "username");
            string message = JsonHelper.ExtractString(json, "message");
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(message)) return;

            // Show in-game as a message
            Messages.Message(
                $"[{username}] {message}",
                MessageTypeDefOf.NeutralEvent,
                historical: false
            );

            // Broadcast to all viewers
            var chatMsg = new Dictionary<string, object>
            {
                ["type"] = "chat",
                ["username"] = username,
                ["message"] = message
            };
            relayClient?.Broadcast(chatMsg);
            embeddedServer?.Broadcast(JsonHelper.ToJson(chatMsg));
        }

        public void HandleRequestStatePublic(string username)
        {
            var session = viewerManager.GetSession(username);
            // Always refresh host clock/speed for this viewer, even before assignment.
            viewerManager?.SendGameInfoNow(username);
            if (session == null || !session.HasPawn) return;

            var stateJson = PawnStateSerializer.Serialize(session.assignedPawn);
            session.lastStateHash = PawnStateSerializer.ComputeStateSignature(session.assignedPawn);
            viewerManager.SendPermissions(username);

            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.PawnState,
                ["state"] = new JsonHelper.RawJson(stateJson)
            };
            SendToViewer(username, msg);

            QueuePawnPortrait(username, session.assignedPawn);
            viewerManager.SendTacticalMapSnapshot(username);
            viewerManager.SendResourceReadout(username, force: true);
        }

        private void QueuePawnPortrait(string username, Pawn pawn)
        {
            if (string.IsNullOrEmpty(username) || pawn == null || pawn.Dead || pawn.Destroyed)
                return;

            int pawnId = pawn.thingIDNumber;
            if (portraitCache.TryGetValue(pawnId, out string cachedPortrait))
            {
                SendPortrait(username, cachedPortrait);
                return;
            }

            string key = username + ":" + pawnId;
            if (pendingPortraitKeys.Contains(key))
                return;

            pendingPortraitKeys.Add(key);
            portraitQueue.Enqueue(new PortraitRequest
            {
                username = username,
                pawn = pawn,
                pawnId = pawnId,
                key = key
            });
        }

        private void ProcessPortraitQueue()
        {
            if (portraitQueue.Count == 0)
                return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextPortraitTime)
                return;
            nextPortraitTime = now + 0.5f; // process one portrait every 0.5s regardless of pause state

            var request = portraitQueue.Dequeue();
            pendingPortraitKeys.Remove(request.key);

            if (request.pawn == null || request.pawn.Destroyed || request.pawn.Dead)
                return;

            if (!portraitCache.TryGetValue(request.pawnId, out string portrait))
            {
                portrait = PortraitRenderer.GetPortraitBase64(request.pawn);
                if (!string.IsNullOrEmpty(portrait))
                    portraitCache[request.pawnId] = portrait;
            }

            if (!string.IsNullOrEmpty(portrait))
                SendPortrait(request.username, portrait);
        }

        private void SendPortrait(string username, string portrait)
        {
            SendToViewer(username, new Dictionary<string, object>
            {
                ["type"] = StateProtocol.PawnPortrait,
                ["data"] = portrait
            });
        }

        public void InvalidatePawnPortrait(Pawn pawn)
        {
            if (pawn == null)
                return;

            portraitCache.Remove(pawn.thingIDNumber);
        }

        private void HandleAssign(string json)
        {
            string username = JsonHelper.ExtractLastString(json, "username");
            int pawnId = JsonHelper.ExtractInt(json, "pawnId", -1);
            if (username == null || pawnId < 0) return;

            var pawn = viewerManager.FindPawnById(pawnId);
            if (pawn == null)
            {
                LogUtil.Warn($"Assign failed: pawn {pawnId} not found");
                return;
            }

            if (viewerManager.GetSession(username) == null)
                viewerManager.GetOrCreateSession(username, username);

            if (viewerManager.AssignPawn(username, pawn))
            {
                viewerManager.SendColonistList();
                HandleRequestState(json);
            }
        }

        private void HandleUnassign(string json)
        {
            string username = JsonHelper.ExtractLastString(json, "username");
            if (username == null) return;

            viewerManager.UnassignPawn(username);
            viewerManager.SendColonistList();
        }

        private void HandleClaimResponse(string json)
        {
            string username = JsonHelper.ExtractLastString(json, "username");
            bool approved = JsonHelper.ExtractBool(json, "approved", false);
            if (string.IsNullOrEmpty(username)) return;

            if (approved)
            {
                int pawnId = JsonHelper.ExtractInt(json, "pawnId", -1);
                var pawn = viewerManager.FindPawnById(pawnId);
                if (viewerManager.GetSession(username) == null)
                    viewerManager.GetOrCreateSession(username, username);
                if (pawn != null && viewerManager.AssignPawn(username, pawn))
                {
                    viewerManager.SendColonistList();
                    HandleRequestStatePublic(username);
                }
            }
            else
            {
                viewerManager.RejectClaim(username);
                viewerManager.SendColonistList();
            }
        }

        private static bool IsAdminMessage(string json)
        {
            return JsonHelper.ExtractLastBool(json, "adminCommand", false) &&
                   JsonHelper.ExtractLastString(json, "source") == "admin";
        }

        private void SendToViewer(string username, Dictionary<string, object> msg)
        {
            string json = JsonHelper.ToJson(msg);
            relayClient?.SendToViewer(username, msg);
            embeddedServer?.SendToViewer(username, json);
        }

        public void SendToViewerPublic(string username, Dictionary<string, object> msg)
        {
            SendToViewer(username, msg);
        }

        public void SendToolkitStatePublic(string username)
        {
            if (string.IsNullOrEmpty(username))
                return;
            SendToViewer(username, TwitchToolkitBridge.BuildViewerState(username));
        }

        private void SendHostCapabilities(string username = null, bool adminOnly = false)
        {
            var msg = RimWorldCompat.BuildCapabilityMessage();
            if (adminOnly)
            {
                msg["adminOnly"] = true;
                relayClient?.Broadcast(msg);
                return;
            }

            if (!string.IsNullOrEmpty(username))
            {
                SendToViewer(username, msg);
                return;
            }

            relayClient?.Broadcast(msg);
            embeddedServer?.Broadcast(JsonHelper.ToJson(msg));
        }

        public void OnPawnKilled(Pawn pawn)
        {
            viewerManager?.OnPawnKilled(pawn);
        }

        public void NotifyClaimRequest(PendingClaim claim)
        {
            ClaimAlertOverlay.NotifyClaimRequest(claim);
        }

        public RelayClient Relay => relayClient;
        public ViewerManager Viewers => viewerManager;
        public EmbeddedWebServer EmbeddedServer => embeddedServer;
        public bool IsInitialized => initialized;

        private VoteManager voteManager = new VoteManager();
        public VoteManager VoteManager => voteManager;
    }
}
