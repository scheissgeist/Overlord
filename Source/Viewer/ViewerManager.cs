using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace Overlord
{
    public class PendingClaim
    {
        public string username;
        public string displayName;
        public int pawnId;
        public string pawnName;
        public int requestedTick;
    }

    /// <summary>
    /// Central registry of all viewer sessions. Handles assignment, lookup,
    /// state sync ticking, and save/load.
    /// </summary>
    public class ViewerManager : IExposable
    {
        private Dictionary<string, ViewerSession> sessions = new Dictionary<string, ViewerSession>();

        // Reverse lookup: pawn thingID -> username
        private Dictionary<int, string> pawnToViewer = new Dictionary<int, string>();
        private Dictionary<string, PendingClaim> pendingClaims = new Dictionary<string, PendingClaim>();

        // Last viewer owner for dead pawns (thingID -> username). Used by host revive auto-reassign.
        private Dictionary<int, string> lastOwnerByPawnId = new Dictionary<int, string>();

        // Moderation state — saved across reloads
        private HashSet<string> bannedUsernames = new HashSet<string>();
        private Dictionary<string, int> timeoutUntilTick = new Dictionary<string, int>();

        // State sync interval (ticks)
        private const int StateSyncInterval = 10; // ~167ms
        private const int ResourceReadoutSyncCycles = 6; // ~1s at StateSyncInterval
        private int tickCounter;
        private int resourceReadoutCycleCounter;
        private int lastResourceReadoutHash;

        private static string NormalizeUsername(string username)
        {
            username = (username ?? "").Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(username) ? null : username;
        }

        private static string NormalizeNameKey(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        public ViewerSession GetSession(string username)
        {
            username = NormalizeUsername(username);
            if (username == null) return null;
            sessions.TryGetValue(username, out var session);
            return session;
        }

        public ViewerSession GetSessionForPawn(Pawn pawn)
        {
            if (pawn == null) return null;

            // Fast path: O(1) reverse-map lookup, but only trust it when the session's
            // live assignedPawn still matches — that validation is exactly what made the
            // old linear-first scan correct. A validated hit is authoritative; a miss or
            // a stale entry falls through to the linear scan below, same as before. This
            // turns the common (correctly-mapped) case into O(1), which matters because
            // ColonistBarOverlay.Draw calls this once per colonist every GUI event.
            if (pawnToViewer.TryGetValue(pawn.thingIDNumber, out string mappedUser))
            {
                var mappedSession = GetSession(mappedUser);
                if (mappedSession?.assignedPawn == pawn)
                    return mappedSession;
                // Stale reverse map entry — drop it so claim/assign don't treat this as taken.
                pawnToViewer.Remove(pawn.thingIDNumber);
            }

            // Fallback: the reverse map missed or was stale. Scan live session state, which
            // is the source of truth for ownership.
            foreach (var session in sessions.Values)
            {
                if (session?.assignedPawn == pawn)
                    return session;
            }

            return null;
        }

        public ViewerSession GetOrCreateSession(string username, string displayName)
        {
            username = NormalizeUsername(username);
            if (username == null)
                return null;

            if (IsBanned(username))
            {
                // Banned users don't get a session. Push the moderation message regardless
                // so a relogged client sees why nothing works.
                SendModerationMessage(username, StateProtocol.Banned, "Banned by streamer");
                return null;
            }

            if (sessions.TryGetValue(username, out var existing))
            {
                existing.displayName = displayName ?? existing.displayName ?? username;
                existing.isConnected = true;
                return existing;
            }

            var session = new ViewerSession(username, displayName);
            session.isConnected = true;
            sessions[username] = session;
            return session;
        }

        public void MarkDisconnected(string username)
        {
            username = NormalizeUsername(username);
            var session = GetSession(username);
            if (session != null)
                session.isConnected = false;
        }

        public bool AssignPawn(string username, Pawn pawn)
        {
            username = NormalizeUsername(username);
            if (username == null || pawn == null)
                return false;

            if (IsBanned(username))
            {
                Messages.Message($"[Overlord] {username} is banned — unban first to assign", MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }

            var session = GetSession(username);
            if (session == null)
                return false;

            if (session.assignedPawn == pawn)
            {
                pendingClaims.Remove(username);
                foreach (var claim in pendingClaims.Values.Where(c => c.pawnId == pawn.thingIDNumber).ToList())
                    pendingClaims.Remove(claim.username);
                pawnToViewer[pawn.thingIDNumber] = username;
                session.lastStateHash = 0;
                TwitchToolkitBridge.TrySyncViewerPawn(username, pawn, out _);
                SendPermissions(username);
                SendTacticalMapSnapshot(username);
                SendResourceReadout(username, force: true);
                return true;
            }

            // Unassign any viewer currently on this pawn
            if (pawnToViewer.TryGetValue(pawn.thingIDNumber, out string existingViewer))
            {
                if (existingViewer != username)
                {
                    var existingSession = GetSession(existingViewer);
                    if (existingSession != null)
                    {
                        existingSession.assignedPawn = null;
                        existingSession.lastStateHash = 0;
                        existingSession.ResetTacticalMapStream();
                    }
                    pawnToViewer.Remove(pawn.thingIDNumber);
                }
            }

            // Unassign viewer's previous pawn
            if (session.assignedPawn != null && session.assignedPawn != pawn)
            {
                RestoreOriginalPawnName(session);
                pawnToViewer.Remove(session.assignedPawn.thingIDNumber);
            }

            session.assignedPawn = pawn;
            session.lastStateHash = 0; // Force full state send
            session.ResetTacticalMapStream();
            pawnToViewer[pawn.thingIDNumber] = username;
            pendingClaims.Remove(username);
            foreach (var claim in pendingClaims.Values.Where(c => c.pawnId == pawn.thingIDNumber).ToList())
                pendingClaims.Remove(claim.username);

            // Rename pawn to viewer's display name
            if (pawn.Name is NameTriple nameTriple)
            {
                session.originalPawnNickname = nameTriple.Nick;
                pawn.Name = new NameTriple(nameTriple.First, session.displayName ?? username, nameTriple.Last);
            }

            LogUtil.Log($"Assigned {pawn.LabelShort} to viewer {username}");
            ActionLog.Append(ActionLogKind.Assignment, username, "assign", $"Assigned to {pawn.LabelShort}", pawn.thingIDNumber);
            TwitchToolkitBridge.TrySyncViewerPawn(username, pawn, out _);
            SendPermissions(username);

            SendTacticalMapSnapshot(username);
            SendResourceReadout(username, force: true);

            return true;
        }

        private static void AnnotateTacticalMapMessage(ViewerSession session, Map map, Dictionary<string, object> msg, bool snapshot)
        {
            if (session == null || msg == null)
                return;

            if (snapshot)
            {
                session.tacticalMapEpoch++;
                if (session.tacticalMapEpoch <= 0)
                    session.tacticalMapEpoch = 1;
                session.tacticalMapSeq = 0;
            }
            else if (session.tacticalMapEpoch <= 0)
            {
                session.tacticalMapEpoch = 1;
            }

            int baseSeq = session.tacticalMapSeq;
            int seq = baseSeq + 1;
            session.tacticalMapSeq = seq;

            msg["protocol"] = "vdr/0";
            msg["mapEpoch"] = session.tacticalMapEpoch;
            msg["seq"] = seq;
            msg["baseSeq"] = snapshot ? 0 : baseSeq;
            msg["snapshot"] = snapshot;
            msg["tick"] = Find.TickManager?.TicksGame ?? 0;
            msg["mapId"] = map != null && Find.Maps != null ? Find.Maps.IndexOf(map) : -1;
        }

        private static void AnnotateMapChunkMessage(ViewerSession session, Map map, Dictionary<string, object> msg)
        {
            if (session == null || msg == null)
                return;

            if (session.tacticalMapEpoch <= 0)
                session.tacticalMapEpoch = 1;

            int baseSeq = session.tacticalMapChunkSeq;
            int seq = baseSeq + 1;
            session.tacticalMapChunkSeq = seq;

            msg["protocol"] = "vdr/0";
            msg["mapEpoch"] = session.tacticalMapEpoch;
            msg["chunkSeq"] = seq;
            msg["chunkBaseSeq"] = baseSeq;
            msg["tick"] = Find.TickManager?.TicksGame ?? 0;
            msg["mapId"] = map != null && Find.Maps != null ? Find.Maps.IndexOf(map) : -1;
        }

        public bool SendTacticalMapSnapshot(string username)
        {
            username = NormalizeUsername(username);
            if (username == null || OverlordMod.Settings?.allowViewerTacticalMap != true)
                return false;

            var comp = OverlordGameComponent.Instance;
            var session = GetSession(username);
            var pawn = session?.assignedPawn;
            var map = pawn?.Map;
            if (comp == null || pawn == null || pawn.Dead || pawn.Destroyed || map == null)
                return false;

            var fullMap = TileMapSerializer.SerializeFullMap(map);
            if (fullMap == null)
                return false;

            session.ResetTacticalMapEntities();
            AnnotateTacticalMapMessage(session, map, fullMap, snapshot: true);
            session.tacticalMapChunkSeq = 0;
            session.tacticalMapChunkHashes = TileMapSerializer.BuildChunkHashSnapshot(map);
            comp.SendToViewerPublic(username, fullMap);

            HashSet<int> entityIds;
            Dictionary<int, int> entityHashes;
            var delta = TileMapSerializer.SerializeDelta(map, pawn, session.tacticalMapEntityIds, session.tacticalEntityHashes, out entityIds, out entityHashes);
            if (delta != null)
            {
                AnnotateTacticalMapMessage(session, map, delta, snapshot: false);
                comp.SendToViewerPublic(username, delta);
                SendEntityStateFromDelta(username, session, delta, comp);
                session.tacticalMapEntityIds = entityIds;
                session.tacticalEntityHashes = entityHashes;
            }

            return true;
        }

        private bool SendTacticalMapDelta(ViewerSession session, Pawn pawn, OverlordGameComponent comp)
        {
            if (session == null || pawn?.Map == null || comp == null)
                return false;

            if (session.tacticalMapEpoch <= 0 || session.tacticalMapSeq <= 0)
                return SendTacticalMapSnapshot(session.username);

            HashSet<int> entityIds;
            Dictionary<int, int> entityHashes;
            var delta = TileMapSerializer.SerializeDelta(pawn.Map, pawn, session.tacticalMapEntityIds, session.tacticalEntityHashes, out entityIds, out entityHashes);
            if (delta == null)
                return false;

            AnnotateTacticalMapMessage(session, pawn.Map, delta, snapshot: false);
            comp.SendToViewerPublic(session.username, delta);
            SendEntityStateFromDelta(session.username, session, delta, comp);
            session.tacticalMapEntityIds = entityIds;
            session.tacticalEntityHashes = entityHashes;
            SendChangedMapChunks(session, pawn.Map, comp);
            return true;
        }

        private static void SendChangedMapChunks(ViewerSession session, Map map, OverlordGameComponent comp)
        {
            if (session == null || map == null || comp == null || string.IsNullOrEmpty(session.username))
                return;

            Dictionary<string, int> nextChunkHashes;
            var chunks = TileMapSerializer.SerializeChangedMapChunks(map, session.tacticalMapChunkHashes, out nextChunkHashes);
            if (chunks == null || chunks.Count == 0)
            {
                session.tacticalMapChunkHashes = nextChunkHashes;
                return;
            }

            foreach (var chunk in chunks)
            {
                AnnotateMapChunkMessage(session, map, chunk);
                comp.SendToViewerPublic(session.username, chunk);
            }

            session.tacticalMapChunkHashes = nextChunkHashes;
        }

        private static void SendEntityStateFromDelta(string username, ViewerSession session, Dictionary<string, object> delta, OverlordGameComponent comp)
        {
            if (string.IsNullOrEmpty(username) || session == null || delta == null || comp == null)
                return;

            var entityState = TileMapSerializer.BuildEntityStateMessage(delta);
            if (entityState == null)
                return;

            AnnotateEntityStateMessage(session, entityState);
            comp.SendToViewerPublic(username, entityState);
        }

        private static void AnnotateEntityStateMessage(ViewerSession session, Dictionary<string, object> msg)
        {
            if (session == null || msg == null)
                return;

            bool snapshot = msg.TryGetValue("entityKeyframe", out object keyframeValue) &&
                keyframeValue is bool isKeyframe &&
                isKeyframe;

            if (snapshot)
            {
                session.tacticalEntityEpoch++;
                if (session.tacticalEntityEpoch <= 0)
                    session.tacticalEntityEpoch = 1;
                session.tacticalEntitySeq = 0;
            }
            else if (session.tacticalEntityEpoch <= 0)
            {
                session.tacticalEntityEpoch = 1;
            }

            int baseSeq = session.tacticalEntitySeq;
            int seq = baseSeq + 1;
            session.tacticalEntitySeq = seq;

            msg["entityProtocol"] = "vdr/entity/0";
            msg["entityEpoch"] = session.tacticalEntityEpoch;
            msg["entitySeq"] = seq;
            msg["entityBaseSeq"] = snapshot ? 0 : baseSeq;
            msg["entitySnapshot"] = snapshot;
        }

        public void UnassignPawn(string username)
        {
            username = NormalizeUsername(username);
            var session = GetSession(username);
            if (session?.assignedPawn != null)
            {
                var pawn = session.assignedPawn;
                RestoreOriginalPawnName(session);

                pawnToViewer.Remove(pawn.thingIDNumber);
                LogUtil.Log($"Unassigned {pawn.LabelShort} from viewer {username}");
                ActionLog.Append(ActionLogKind.Unassignment, username, "unassign", $"Unassigned from {pawn.LabelShort}", pawn.thingIDNumber);
                session.assignedPawn = null;
                session.lastStateHash = 0;
                session.ResetTacticalMapStream();
                TwitchToolkitBridge.ClearViewerPawn(username);
            }
        }

        private static void RestoreOriginalPawnName(ViewerSession session)
        {
            var pawn = session?.assignedPawn;
            if (pawn == null || session.originalPawnNickname == null || !(pawn.Name is NameTriple nt))
                return;

            pawn.Name = new NameTriple(nt.First, session.originalPawnNickname, nt.Last);
            session.originalPawnNickname = null;
        }

        public void RemoveSession(string username)
        {
            username = NormalizeUsername(username);
            UnassignPawn(username);
            if (username != null)
                sessions.Remove(username);
        }

        // ── Moderation ────────────────────────────────────────────────────────

        public bool IsBanned(string username)
        {
            username = NormalizeUsername(username);
            return !string.IsNullOrEmpty(username) && bannedUsernames.Contains(username);
        }

        public bool IsInTimeout(string username, out int secondsRemaining)
        {
            username = NormalizeUsername(username);
            secondsRemaining = 0;
            if (string.IsNullOrEmpty(username) || !timeoutUntilTick.TryGetValue(username, out int untilTick))
                return false;
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now >= untilTick)
            {
                timeoutUntilTick.Remove(username);
                return false;
            }
            secondsRemaining = (untilTick - now) / 60;
            return true;
        }

        /// <summary>
        /// Disconnect viewer's socket without persisting a ban. They can re-log.
        /// </summary>
        public void KickViewer(string username, string reason = null)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username)) return;
            UnassignPawn(username);
            pendingClaims.Remove(username);
            SendModerationMessage(username, StateProtocol.ViewerKick, reason ?? "Kicked by streamer");
            LogUtil.Log($"Streamer kicked {username}: {reason ?? "no reason"}");
            ActionLog.Append(ActionLogKind.Moderation, username, "kick", reason ?? "Kicked");
            Messages.Message($"[Overlord] Kicked {username}", MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>
        /// Permanent ban: disconnect, persist username, block re-claim and re-assignment.
        /// </summary>
        public void BanViewer(string username, string reason = null)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username)) return;
            bannedUsernames.Add(username);
            UnassignPawn(username);
            pendingClaims.Remove(username);
            SendModerationMessage(username, StateProtocol.Banned, reason ?? "Banned by streamer");
            LogUtil.Log($"Streamer banned {username}: {reason ?? "no reason"}");
            ActionLog.Append(ActionLogKind.Moderation, username, "ban", reason ?? "Banned");
            Messages.Message($"[Overlord] Banned {username}", MessageTypeDefOf.NegativeEvent, historical: false);
        }

        public void UnbanViewer(string username)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username)) return;
            if (bannedUsernames.Remove(username))
            {
                LogUtil.Log($"Streamer unbanned {username}");
                ActionLog.Append(ActionLogKind.Moderation, username, "unban", "Unbanned");
                Messages.Message($"[Overlord] Unbanned {username}", MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        /// <summary>
        /// Suspend command acceptance for the viewer for N seconds. They stay connected.
        /// </summary>
        public void TimeoutViewer(string username, int seconds, string reason = null)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username) || seconds <= 0) return;
            int now = Find.TickManager?.TicksGame ?? 0;
            timeoutUntilTick[username] = now + (seconds * 60);
            SendModerationMessage(username, StateProtocol.Timeout, reason ?? $"Timed out for {seconds}s",
                extra: new KeyValuePair<string, object>("seconds", seconds));
            LogUtil.Log($"Streamer timed out {username} for {seconds}s");
            ActionLog.Append(ActionLogKind.Moderation, username, "timeout", $"Timed out {seconds}s");
            Messages.Message($"[Overlord] Timed out {username} for {seconds}s", MessageTypeDefOf.NeutralEvent, historical: false);
        }

        public void ClearTimeout(string username)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username)) return;
            if (timeoutUntilTick.Remove(username))
                LogUtil.Log($"Streamer cleared timeout for {username}");
        }

        public IEnumerable<string> BannedUsernames => bannedUsernames;
        public IEnumerable<KeyValuePair<string, int>> ActiveTimeouts => timeoutUntilTick;

        private void SendModerationMessage(string username, string type, string message, KeyValuePair<string, object>? extra = null)
        {
            var comp = OverlordGameComponent.Instance;
            if (comp == null) return;
            var msg = new Dictionary<string, object>
            {
                ["type"] = type,
                ["message"] = message
            };
            if (extra.HasValue)
                msg[extra.Value.Key] = extra.Value.Value;
            comp.SendToViewerPublic(username, msg);
        }

        public void OnPawnKilled(Pawn pawn)
        {
            var session = GetSessionForPawn(pawn);
            if (session == null) return;

            lastOwnerByPawnId[pawn.thingIDNumber] = session.username;
            pawnToViewer.Remove(pawn.thingIDNumber);
            session.assignedPawn = null;
            session.lastStateHash = 0;
            session.ResetTacticalMapStream();
            LogUtil.Log($"Viewer {session.username}'s pawn {pawn.LabelShort} died");
            ActionLog.Append(ActionLogKind.Death, session.username, "death", $"{pawn.LabelShort} died", pawn.thingIDNumber);

            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.PawnDied,
                ["username"] = session.username,
                ["displayName"] = session.displayName ?? session.username,
                ["pawnName"] = pawn.LabelShort ?? "unknown",
                ["ticketsRemaining"] = session.tickets
            };

            var comp = OverlordGameComponent.Instance;
            comp?.SendToViewerPublic(session.username, msg);
            if (comp?.Relay != null && comp.Relay.IsConnected)
            {
                var adminMsg = new Dictionary<string, object>(msg)
                {
                    ["adminOnly"] = true
                };
                comp.Relay.Broadcast(adminMsg);
            }

            // Also broadcast updated colonist list so lobby shows current state
            SendColonistList();

            // Tell this viewer whether a respawn portal is currently available
            NotifyPortalState(session.username, comp);
        }

        public void Tick()
        {
            tickCounter++;
            if (tickCounter < StateSyncInterval)
                return;
            tickCounter = 0;

            var comp  = OverlordGameComponent.Instance;
            var settings = OverlordMod.Settings;
            int now   = Find.TickManager.TicksGame;

            // Send game info every sync cycle (broadcast to all)
            SendGameInfo(comp);
            MaybeSendResourceReadout(comp);
            int earnInterval = settings?.ticketEarnIntervalTicks ?? 0;
            int maxT  = settings?.maxTickets ?? 5;

            // Snapshot to avoid modification during enumeration
            var sessionSnapshot = sessions.Values.ToList();

            foreach (var session in sessionSnapshot)
            {
                // Time-based ticket earn (applies whether or not viewer has a pawn)
                if (earnInterval > 0 && session.tickets < maxT)
                {
                    if (session.lastTicketEarnTick < 0)
                        session.lastTicketEarnTick = now; // initialise on first tick
                    else if (now - session.lastTicketEarnTick >= earnInterval)
                    {
                        session.tickets = System.Math.Min(session.tickets + 1, maxT);
                        session.lastTicketEarnTick = now;
                        LogUtil.Log($"Viewer {session.username} earned a ticket ({session.tickets}/{maxT})");

                        var ticketMsg = new Dictionary<string, object>
                        {
                            ["type"]    = "ticket_update",
                            ["tickets"] = session.tickets
                        };
                        comp?.SendToViewerPublic(session.username, ticketMsg);
                    }
                }

                if (!session.HasPawn)
                    continue;

                if (!session.isConnected)
                    continue;

                var pawn = session.assignedPawn;

                // ── Action log detection ──────────────────────────────────────
                string jobLabel = pawn.jobs?.curDriver?.GetReport() ?? "";
                if (!string.IsNullOrEmpty(jobLabel) && jobLabel != session.lastJobLabel)
                {
                    session.lastJobLabel = jobLabel;
                    session.pendingLogEntries.Add(jobLabel);
                }

                float hp = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
                if (session.lastHealthPct > 0f && hp < session.lastHealthPct - 0.05f)
                {
                    int dmg = (int)((session.lastHealthPct - hp) * 100f);
                    session.pendingLogEntries.Add($"Took {dmg}% damage");
                }
                session.lastHealthPct = hp;

                // Send pending log entries
                if (session.pendingLogEntries.Count > 0)
                {
                    var entries = new List<object>(session.pendingLogEntries.Count);
                    foreach (var e in session.pendingLogEntries) entries.Add(e);
                    session.pendingLogEntries.Clear();

                    var logMsg = new Dictionary<string, object>
                    {
                        ["type"]    = StateProtocol.ActionLog,
                        ["entries"] = entries
                    };
                    comp?.SendToViewerPublic(session.username, logMsg);
                }

                // ── Delta state sync ──────────────────────────────────────────
                // Cheap fingerprint first; only serialize on change. Saves ~18
                // full pawn-state JSON builds/sec at 9 viewers when nothing moved.
                int signature = PawnStateSerializer.ComputeStateSignature(pawn);
                if (signature != session.lastStateHash)
                {
                    var stateJson = PawnStateSerializer.Serialize(pawn);
                    session.lastStateHash = signature;
                    var msg = new Dictionary<string, object>
                    {
                        ["type"]  = StateProtocol.PawnState,
                        ["state"] = new JsonHelper.RawJson(stateJson)
                    };
                    comp?.SendToViewerPublic(session.username, msg);
                }

                // Send tile map delta (pawn/building positions)
                if (OverlordMod.Settings?.allowViewerTacticalMap == true && pawn.Map != null)
                {
                    SendTacticalMapDelta(session, pawn, comp);
                }
            }
        }

        /// <summary>
        /// Sends full colonist list to a specific viewer or broadcast to all.
        /// </summary>
        public void SendColonistList(string username = null)
        {
            var comp = OverlordGameComponent.Instance;
            if (comp == null) return;

            var map = ResolveColonistMap();
            if (map == null)
            {
                var empty = new Dictionary<string, object>
                {
                    ["type"] = StateProtocol.ColonistList,
                    ["colonists"] = new List<Dictionary<string, object>>(),
                    ["hostMap"] = false
                };
                SendColonistListMessage(comp, empty, username);
                return;
            }

            var colonists = map.mapPawns.FreeColonists;
            var list = new List<Dictionary<string, object>>();

            foreach (var pawn in colonists)
            {
                var entry = new Dictionary<string, object>
                {
                    ["id"]   = pawn.thingIDNumber,
                    ["name"] = pawn.LabelShort ?? "unknown",
                };
                if (pawnToViewer.TryGetValue(pawn.thingIDNumber, out string viewer))
                {
                    entry["assignedTo"] = viewer;
                    var assignedSession = GetSession(viewer);
                    if (!string.IsNullOrEmpty(assignedSession?.displayName))
                        entry["assignedDisplayName"] = assignedSession.displayName;
                }
                list.Add(entry);
            }

            var msg = new Dictionary<string, object>
            {
                ["type"]      = StateProtocol.ColonistList,
                ["colonists"] = list,
                ["hostMap"]   = true
            };

            SendColonistListMessage(comp, msg, username);
        }

        private static Map ResolveColonistMap()
        {
            if (Find.CurrentMap != null)
                return Find.CurrentMap;
            return Find.Maps?.FirstOrDefault(m => m?.mapPawns?.FreeColonists?.Any() == true);
        }

        private static void SendColonistListMessage(OverlordGameComponent comp, Dictionary<string, object> msg, string username)
        {
            if (username != null)
            {
                comp.SendToViewerPublic(username, msg);
                return;
            }

            var relay = comp.Relay;
            if (relay != null && relay.IsConnected)
                relay.Broadcast(msg);
            var embedded = comp.EmbeddedServer;
            embedded?.Broadcast(JsonHelper.ToJson(msg));
        }

        public void SendPermissions(string username)
        {
            username = NormalizeUsername(username);
            var session = GetSession(username);
            if (session?.permissions == null)
                return;

            var p = session.permissions;
            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.Permissions,
                ["draft"] = p.draft,
                ["move"] = p.move,
                ["attack"] = p.attack,
                ["work"] = p.work,
                ["schedule"] = p.schedule,
                ["outfit"] = p.outfit,
                ["drugPolicy"] = p.drugPolicy,
                ["foodPolicy"] = p.foodPolicy,
                ["area"] = p.area,
                ["equip"] = p.equip,
                ["appearance"] = p.appearance,
                ["freeAppearanceAvailable"] = !session.freeAppearanceUsed
            };

            OverlordGameComponent.Instance?.SendToViewerPublic(username, msg);
        }

        public PendingClaim AddClaimRequest(string username, string displayName, Pawn pawn)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username) || pawn == null)
                return null;
            if (IsBanned(username))
                return null;

            var claim = new PendingClaim
            {
                username = username,
                displayName = displayName ?? username,
                pawnId = pawn.thingIDNumber,
                pawnName = pawn.LabelShort ?? "unknown",
                requestedTick = Find.TickManager?.TicksGame ?? 0
            };
            pendingClaims[username] = claim;
            ActionLog.Append(ActionLogKind.Claim, username, "claim_request", $"Wants to claim {claim.pawnName}", pawn.thingIDNumber);
            return claim;
        }

        public PendingClaim GetPendingClaim(string username)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username)) return null;
            pendingClaims.TryGetValue(username, out var claim);
            return claim;
        }

        public void RejectClaim(string username)
        {
            username = NormalizeUsername(username);
            var claim = GetPendingClaim(username);
            if (claim == null)
                return;

            pendingClaims.Remove(username);
            ActionLog.Append(ActionLogKind.Claim, username, "claim_rejected", $"Claim for {claim.pawnName} rejected", claim.pawnId);
            OverlordGameComponent.Instance?.SendToViewerPublic(username, new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = false,
                ["message"] = $"Claim rejected for {claim.pawnName}"
            });
        }

        public IEnumerable<PendingClaim> PendingClaims => pendingClaims.Values;

        /// <summary>
        /// Grants one ticket to a viewer (streamer action). Capped at maxTickets.
        /// </summary>
        public bool GrantTicket(string username)
        {
            username = NormalizeUsername(username);
            var session = GetSession(username);
            if (session == null) return false;

            int max = OverlordMod.Settings?.maxTickets ?? 5;
            if (session.tickets >= max) return false;

            session.tickets++;
            LogUtil.Log($"Streamer granted ticket to {username} ({session.tickets}/{max})");

            var msg = new Dictionary<string, object>
            {
                ["type"]    = "ticket_update",
                ["tickets"] = session.tickets
            };
            OverlordGameComponent.Instance?.SendToViewerPublic(username, msg);
            return true;
        }

        private static void NotifyPortalState(string username, OverlordGameComponent comp)
        {
            if (string.IsNullOrEmpty(username) || comp == null) return;

            var map = Find.CurrentMap;
            var portal = map?.listerBuildings?.AllBuildingsColonistOfDef(
                DefDatabase<ThingDef>.GetNamed("Overlord_RespawnPortal", errorOnFail: false)
            )?.FirstOrDefault();

            var msg = new Dictionary<string, object>
            {
                ["type"]      = "respawn_portal",
                ["available"] = portal != null,
                ["portalId"]  = portal?.thingIDNumber ?? -1
            };
            comp.SendToViewerPublic(username, msg);
        }

        public IEnumerable<ViewerSession> AllSessions => sessions.Values;
        public int SessionCount => sessions.Count;
        public int ConnectedCount => sessions.Values.Count(s => s != null && s.isConnected);

        public string GetLastOwnerUsername(int pawnThingId)
        {
            if (lastOwnerByPawnId != null && lastOwnerByPawnId.TryGetValue(pawnThingId, out string username))
                return username;
            return null;
        }

        public void ClearLastOwner(int pawnThingId)
        {
            lastOwnerByPawnId?.Remove(pawnThingId);
        }

        public void RememberLastOwner(int pawnThingId, string username)
        {
            username = NormalizeUsername(username);
            if (username == null) return;
            if (lastOwnerByPawnId == null)
                lastOwnerByPawnId = new Dictionary<int, string>();
            lastOwnerByPawnId[pawnThingId] = username;
        }

        public bool TryAssignExistingPawnForViewer(string username, out Pawn pawn)
        {
            username = NormalizeUsername(username);
            pawn = null;
            if (username == null)
                return false;

            var session = GetSession(username);
            if (session?.HasPawn == true)
            {
                pawn = session.assignedPawn;
                return true;
            }

            pawn = FindUnassignedPawnNamedForViewer(username, session?.displayName);
            if (pawn == null)
                return false;

            return AssignPawn(username, pawn);
        }

        public Pawn FindPawnById(int thingId)
        {
            var map = Find.CurrentMap;
            if (map == null) return null;
            return map.mapPawns.FreeColonists.FirstOrDefault(p => p.thingIDNumber == thingId);
        }

        private Pawn FindUnassignedPawnNamedForViewer(string username, string displayName)
        {
            var map = ResolveColonistMap();
            if (map?.mapPawns?.FreeColonists == null)
                return null;

            var keys = new HashSet<string>();
            var userKey = NormalizeNameKey(username);
            var displayKey = NormalizeNameKey(displayName);
            if (userKey != null) keys.Add(userKey);
            if (displayKey != null) keys.Add(displayKey);
            if (keys.Count == 0)
                return null;

            foreach (var pawn in map.mapPawns.FreeColonists)
            {
                if (pawn == null || pawn.Dead || pawn.Destroyed)
                    continue;
                if (pawnToViewer.ContainsKey(pawn.thingIDNumber))
                    continue;

                if (keys.Contains(NormalizeNameKey(pawn.LabelShort)) ||
                    keys.Contains(NormalizeNameKey(pawn.Name?.ToStringShort)) ||
                    keys.Contains(NormalizeNameKey((pawn.Name as NameTriple)?.Nick)))
                {
                    return pawn;
                }
            }

            return null;
        }


        private int lastGameInfoHash;

        /// <summary>
        /// Late joiners never see game_info if hour/speed hasn't changed since the last
        /// broadcast — force a send on join/resync so the viewer pill leaves "Host waiting".
        /// </summary>
        public void SendGameInfoNow(string username = null)
        {
            SendGameInfo(OverlordGameComponent.Instance, force: true, username: username);
        }

        public void SendResourceReadout(string username, bool force = false)
        {
            if (OverlordMod.Settings?.allowViewerResourceReadout != true)
                return;

            username = NormalizeUsername(username);
            if (username == null)
                return;

            var session = GetSession(username);
            if (session == null || !session.HasPawn || !session.isConnected)
                return;

            var comp = OverlordGameComponent.Instance;
            if (comp == null)
                return;

            var map = session.assignedPawn?.Map ?? Find.CurrentMap;
            var msg = ResourceReadoutSerializer.Serialize(map, out int hash);
            if (msg == null)
                return;

            if (!force && hash == lastResourceReadoutHash)
                return;

            lastResourceReadoutHash = hash;
            comp.SendToViewerPublic(username, msg);
        }

        private void MaybeSendResourceReadout(OverlordGameComponent comp)
        {
            if (comp == null || OverlordMod.Settings?.allowViewerResourceReadout != true)
            {
                lastResourceReadoutHash = 0;
                return;
            }

            resourceReadoutCycleCounter++;
            if (resourceReadoutCycleCounter < ResourceReadoutSyncCycles)
                return;
            resourceReadoutCycleCounter = 0;

            var map = Find.CurrentMap;
            var msg = ResourceReadoutSerializer.Serialize(map, out int hash);
            if (msg == null)
                return;
            if (hash == lastResourceReadoutHash)
                return;
            lastResourceReadoutHash = hash;

            foreach (var session in sessions.Values)
            {
                if (session == null || !session.HasPawn || !session.isConnected)
                    continue;
                comp.SendToViewerPublic(session.username, msg);
            }
        }

        private void SendGameInfo(OverlordGameComponent comp, bool force = false, string username = null)
        {
            if (comp == null) return;
            if (!force && sessions.Count == 0) return;

            var map = Find.CurrentMap;
            if (map == null) return;

            try
            {
                var tickMgr = Find.TickManager;
                int hour = GenLocalDate.HourInteger(map);
                int day = GenLocalDate.DayOfYear(map);
                var season = GenLocalDate.Season(map);
                float temp = map.mapTemperature?.OutdoorTemp ?? 0f;
                int speed = (int)tickMgr.CurTimeSpeed;
                bool paused = tickMgr.Paused;

                int hash = hour * 100000 + day * 100 + speed + (paused ? 1 : 0);
                if (!force && hash == lastGameInfoHash) return;
                lastGameInfoHash = hash;

                var msg = new Dictionary<string, object>
                {
                    ["type"] = StateProtocol.GameInfo,
                    ["hour"] = hour,
                    ["day"] = day,
                    ["season"] = season.Label(),
                    ["year"] = GenLocalDate.Year(map),
                    ["temperature"] = (int)temp,
                    ["speed"] = speed,
                    ["paused"] = paused,
                    ["mapName"] = map.info?.parent?.LabelCap ?? "Colony"
                };

                if (!string.IsNullOrEmpty(username))
                {
                    comp.SendToViewerPublic(username, msg);
                    return;
                }

                var relay = comp.Relay;
                if (relay != null && relay.IsConnected)
                    relay.Broadcast(msg);
                var embedded = comp.EmbeddedServer;
                embedded?.Broadcast(JsonHelper.ToJson(msg));
            }
            catch { }
        }

        public void ExposeData()
        {
            // Save/load sessions as a list (Dictionary not directly exposable)
            var sessionList = sessions.Values.ToList();
            Scribe_Collections.Look(ref sessionList, "sessions", LookMode.Deep);

            // Bans persist; timeouts do not (they're tick-relative and short)
            var bannedList = bannedUsernames?.ToList() ?? new List<string>();
            Scribe_Collections.Look(ref bannedList, "bannedUsernames", LookMode.Value);

            List<int> lastOwnerPawnIds = lastOwnerByPawnId?.Keys.ToList() ?? new List<int>();
            List<string> lastOwnerUsernames = lastOwnerPawnIds.Select(id => lastOwnerByPawnId[id]).ToList();
            Scribe_Collections.Look(ref lastOwnerPawnIds, "lastOwnerPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref lastOwnerUsernames, "lastOwnerUsernames", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (sessionList != null)
                {
                    sessions = new Dictionary<string, ViewerSession>();
                    pawnToViewer = new Dictionary<int, string>();
                    foreach (var s in sessionList)
                    {
                        string key = NormalizeUsername(s?.username);
                        if (key == null) continue;
                        s.username = key;
                        if (sessions.ContainsKey(key))
                        {
                            if (!sessions[key].HasPawn && s.HasPawn)
                                sessions[key] = s;
                        }
                        else
                        {
                            sessions[key] = s;
                        }
                        if (s.assignedPawn != null && s.assignedPawn.Spawned && !s.assignedPawn.Dead)
                            pawnToViewer[s.assignedPawn.thingIDNumber] = key;
                    }
                }

                bannedUsernames = bannedList != null
                    ? new HashSet<string>(bannedList.Select(NormalizeUsername).Where(u => !string.IsNullOrEmpty(u)))
                    : new HashSet<string>();
                timeoutUntilTick = new Dictionary<string, int>();

                lastOwnerByPawnId = new Dictionary<int, string>();
                if (lastOwnerPawnIds != null && lastOwnerUsernames != null)
                {
                    int n = System.Math.Min(lastOwnerPawnIds.Count, lastOwnerUsernames.Count);
                    for (int i = 0; i < n; i++)
                    {
                        string user = NormalizeUsername(lastOwnerUsernames[i]);
                        if (user != null)
                            lastOwnerByPawnId[lastOwnerPawnIds[i]] = user;
                    }
                }
            }
        }
    }
}
