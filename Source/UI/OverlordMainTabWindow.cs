using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Streamer operations console. Runtime control lives here; technical install/config
    /// stays in mod settings.
    /// </summary>
    public class OverlordMainTabWindow : MainTabWindow
    {
        private static readonly Color WindowFill    = new Color(0.025f, 0.04f,  0.055f, 0.96f);
        private static readonly Color PanelFill     = new Color(0.035f, 0.055f, 0.07f,  0.93f);
        private static readonly Color HeaderFill    = new Color(0.06f,  0.075f, 0.085f, 0.98f);
        private static readonly Color SelectedFill  = new Color(0.34f,  0.24f,  0.10f,  0.60f);
        private static readonly Color RowFill       = new Color(0.95f,  0.80f,  0.52f,  0.035f);
        // Exact brand gold #d2a95d / bright #f0cf82 (BRAND_SYSTEM.md) — was a duller brass.
        private static readonly Color BrassColor    = new Color(0.824f, 0.663f, 0.365f);
        private static readonly Color BrassDimColor = new Color(0.45f,  0.32f,  0.16f);
        private static readonly Color BrassSoftColor= new Color(0.824f, 0.663f, 0.365f, 0.30f);
        private static readonly Color TextColor     = new Color(0.88f,  0.84f,  0.75f);
        private static readonly Color OnlineColor   = new Color(0.44f,  0.90f,  0.54f);
        private static readonly Color WaitingColor  = new Color(0.95f,  0.75f,  0.28f);
        private static readonly Color OfflineColor  = new Color(0.78f,  0.35f,  0.32f);
        private static readonly Color MutedColor    = new Color(0.58f,  0.57f,  0.55f);
        private static readonly Color WarnFill      = new Color(0.47f,  0.29f,  0.08f,  0.62f);
        private static readonly Color ActionFill    = new Color(0.11f,  0.22f,  0.26f,  0.70f);
        private static readonly Color DangerFill    = new Color(0.42f,  0.08f,  0.08f,  0.88f);
        private static readonly Color DangerBorder  = new Color(0.72f,  0.28f,  0.28f);

        public override Vector2 RequestedTabSize => new Vector2(1000f, 700f);

        private Vector2 viewerScroll;
        private Vector2 colonistScroll;
        private Vector2 inspectorScroll;
        private string selectedViewer;
        private int selectedPawnId = -1;

        private string broadcastText = "";
        private string voteQuestion  = "";
        private string voteOptions   = "";

        private bool advancedInspectorExpanded = false;

        public override void DoWindowContents(Rect inRect)
        {
            var comp = OverlordGameComponent.Instance;
            var vm   = comp?.Viewers;

            Text.Font = GameFont.Small;

            if (comp == null || !comp.IsInitialized || vm == null)
            {
                Widgets.Label(inRect, "Overlord not active. Load a save first.");
                return;
            }

            var colonists = GetColonists();
            SyncSelection(vm, colonists);

            Widgets.DrawBoxSolid(inRect, WindowFill);
            DrawFineBorder(inRect, BrassDimColor);

            float summaryHeight = inRect.width < 960f ? 128f : 108f;
            float actionHeight  = inRect.width < 720f ? 126f : 102f;

            var summaryRect = new Rect(inRect.x, inRect.y, inRect.width, summaryHeight);
            DrawSummaryBar(summaryRect, comp, vm, colonists);

            float operationsHeight = Mathf.Max(220f, inRect.height - summaryHeight - actionHeight - 16f);
            var operationsRect = new Rect(inRect.x, summaryRect.yMax + 8f, inRect.width, operationsHeight);
            DrawOperationsConsole(operationsRect, comp, vm, colonists);

            var actionsRect = new Rect(inRect.x, inRect.yMax - actionHeight, inRect.width, actionHeight);
            DrawActionStrip(actionsRect, comp, vm);
        }

        // Brand seal, loaded once. Silent-fail if the texture is missing so the tab
        // never errors over cosmetics.
        private static Texture2D sealTex;
        private static bool sealResolved;
        private static Texture2D Seal
        {
            get
            {
                if (!sealResolved)
                {
                    sealResolved = true;
                    try { sealTex = ContentFinder<Texture2D>.Get("UI/Overlord", reportFailure: false); }
                    catch { sealTex = null; }
                }
                return sealTex;
            }
        }

        private void DrawSummaryBar(Rect rect, OverlordGameComponent comp, ViewerManager vm, List<Pawn> colonists)
        {
            DrawPanel(rect, "Runtime");

            // Star seal in the panel header corner — the brand mark on the streamer's
            // most-used surface.
            var seal = Seal;
            if (seal != null)
            {
                var sealRect = new Rect(rect.xMax - 22f, rect.y + 3f, 18f, 18f);
                GUI.DrawTexture(sealRect, seal, ScaleMode.ScaleToFit);
            }

            var sessions = vm.AllSessions.ToList();
            int online        = vm.ConnectedCount;
            int assigned      = sessions.Count(s => s != null && s.HasPawn);
            int waiting       = sessions.Count(s => s != null && !s.HasPawn && s.isConnected);
            int openColonists = colonists.Count(p => vm.GetSessionForPawn(p) == null);
            int pendingClaims = vm.PendingClaims.Count();
            int deadCount     = ReviveManager.CountDeadColonists();

            string mode = comp.Relay?.IsConnected == true
                ? "Relay connected"
                : comp.EmbeddedServer?.IsRunning == true
                    ? "Embedded host"
                    : "Offline";

            bool compact = rect.width < 960f;
            float contentY = rect.y + 30f;

            // Status line + attention summary in one row
            string statusLine = $"{mode}  ·  online {online}  ·  assigned {assigned}  ·  waiting {waiting}  ·  open {openColonists}";
            if (pendingClaims > 0)
                statusLine += $"  ·  {pendingClaims} pending claim{(pendingClaims > 1 ? "s" : "")}";
            if (deadCount > 0)
                statusLine += $"  ·  {deadCount} dead";

            var topLineRect = new Rect(rect.x + 12f, contentY, rect.width - 24f, 22f);
            if (pendingClaims > 0 || deadCount > 0)
                GUI.color = WaitingColor;
            Widgets.Label(topLineRect, statusLine);
            GUI.color = Color.white;

            // Hint / warning line
            string hostHint = comp.EmbeddedServer?.IsRunning == true
                ? $"Local join: http://localhost:{OverlordMod.Settings.localPort}"
                : "Relay auth managed by the web server";
            string warnings  = BuildCapabilityWarnings();
            string liveRisk  = BuildLiveRiskSummary();
            string hintLine  = !string.IsNullOrEmpty(liveRisk) ? liveRisk
                             : !string.IsNullOrEmpty(warnings) ? warnings
                             : hostHint;

            var hintRect = new Rect(rect.x + 12f, topLineRect.yMax + 2f, rect.width - 24f, 18f);
            GUI.color = !string.IsNullOrEmpty(liveRisk) || !string.IsNullOrEmpty(warnings) ? WaitingColor : MutedColor;
            Widgets.Label(hintRect, hintLine);
            GUI.color = Color.white;

            // Button row — remove "Send Colonist List" (auto-managed)
            var buttons = new List<ConsoleButton>
            {
                new ConsoleButton { Label = compact ? "Assign" : "Assign Viewer", OnClick = () => Find.WindowStack.Add(new AssignmentDialog()) },
                new ConsoleButton { Label = compact ? "Spawn" : "Spawn Colonist", OnClick = () => SpawnNewColonist(vm) },
                new ConsoleButton { Label = compact ? "Log" : "Action Log",       OnClick = () => Find.WindowStack.Add(new ActionLogWindow()) }
            };

            if (deadCount > 0)
            {
                string reviveLabel = compact ? $"Revive ({deadCount})" : $"Revive All Dead ({deadCount})";
                buttons.Add(new ConsoleButton
                {
                    Label = reviveLabel,
                    OnClick = () =>
                    {
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                            $"Revive all {deadCount} dead colonist{(deadCount == 1 ? "" : "s")}? Previous waiting owners will be reassigned.",
                            () => ReviveManager.ReviveAllDeadColonists(),
                            false));
                    }
                });
            }

            if (!string.IsNullOrEmpty(selectedViewer))
            {
                string spawnLabel = compact ? "Spawn" : $"Spawn for {selectedViewer}";
                buttons.Add(new ConsoleButton { Label = spawnLabel, OnClick = () => SpawnAndAssign(selectedViewer, vm) });
            }

            DrawButtonRow(new Rect(rect.x + 12f, rect.yMax - 34f, rect.width - 24f, 28f), buttons);
        }

        private void DrawOperationsConsole(Rect rect, OverlordGameComponent comp, ViewerManager vm, List<Pawn> colonists)
        {
            float gap = 8f;
            if (rect.width < 640f)
            {
                float paneH = Mathf.Max(120f, (rect.height - gap * 2f) / 3f);
                var r1 = new Rect(rect.x, rect.y, rect.width, paneH);
                var r2 = new Rect(rect.x, r1.yMax + gap, rect.width, paneH);
                var r3 = new Rect(rect.x, r2.yMax + gap, rect.width, Mathf.Max(120f, rect.yMax - r2.yMax - gap));
                DrawViewerPane(r1, comp, vm);
                DrawColonistPane(r2, comp, vm, colonists);
                DrawInspectorPane(r3, comp, vm, colonists);
                return;
            }

            if (rect.width < 960f)
            {
                float topH  = Mathf.Clamp(Mathf.Max(170f, rect.height * 0.56f), 100f, rect.height - gap - 130f);
                float listW = (rect.width - gap) / 2f;
                var r1 = new Rect(rect.x,             rect.y,            listW, topH);
                var r2 = new Rect(r1.xMax + gap,      rect.y,            listW, topH);
                var r3 = new Rect(rect.x,             r1.yMax + gap,     rect.width, Mathf.Max(130f, rect.yMax - r1.yMax - gap));
                DrawViewerPane(r1, comp, vm);
                DrawColonistPane(r2, comp, vm, colonists);
                DrawInspectorPane(r3, comp, vm, colonists);
                return;
            }

            float leftW   = Mathf.Clamp(rect.width * 0.30f, 280f, 330f);
            float centerW = Mathf.Clamp(rect.width * 0.32f, 300f, 360f);
            float rightW  = Mathf.Max(280f, rect.width - leftW - centerW - gap * 2f);

            DrawViewerPane  (new Rect(rect.x,                       rect.y, leftW,   rect.height), comp, vm);
            DrawColonistPane(new Rect(rect.x + leftW + gap,         rect.y, centerW, rect.height), comp, vm, colonists);
            DrawInspectorPane(new Rect(rect.x + leftW + centerW + gap * 2f, rect.y, rightW, rect.height), comp, vm, colonists);
        }

        private void DrawViewerPane(Rect rect, OverlordGameComponent comp, ViewerManager vm)
        {
            DrawPanel(rect, "Viewer Sessions");

            var sessions = vm.AllSessions
                .Where(s => s != null)
                .OrderByDescending(s => s.isConnected)
                .ThenByDescending(s => !s.HasPawn)
                .ThenBy(s => s.displayName ?? s.username ?? "")
                .ToList();

            var claims  = sessions.Where(s => vm.GetPendingClaim(s.username) != null).ToList();
            var waiting = sessions.Where(s => s.isConnected && !s.HasPawn && vm.GetPendingClaim(s.username) == null).ToList();
            var active  = sessions.Where(s => s.isConnected && s.HasPawn).ToList();
            var offline = sessions.Where(s => !s.isConnected).ToList();

            int sectionCount = (claims.Count > 0 ? 1 : 0) + (waiting.Count > 0 ? 1 : 0) + (active.Count > 0 ? 1 : 0) + (offline.Count > 0 ? 1 : 0);
            float contentH = sessions.Count * 68f + sectionCount * 26f + 8f;
            var contentRect = new Rect(0f, 0f, rect.width - 34f, Mathf.Max(rect.height - 42f, contentH));
            var viewRect    = new Rect(rect.x + 10f, rect.y + 30f, rect.width - 20f, rect.height - 40f);
            Widgets.BeginScrollView(viewRect, ref viewerScroll, contentRect);

            float y = 0f;
            y = DrawSessionGroup(contentRect.width, y, "Claims to approve", claims,  comp, vm);
            y = DrawSessionGroup(contentRect.width, y, "Waiting for pawn",  waiting, comp, vm);
            y = DrawSessionGroup(contentRect.width, y, "Active control",    active,  comp, vm);
            y = DrawSessionGroup(contentRect.width, y, "Offline",           offline, comp, vm);

            if (sessions.Count == 0)
            {
                GUI.color = MutedColor;
                Widgets.Label(new Rect(0f, 0f, contentRect.width, 22f), "No viewer sessions yet");
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
        }

        private float DrawSessionGroup(float width, float y, string label, List<ViewerSession> sessions, OverlordGameComponent comp, ViewerManager vm)
        {
            if (sessions == null || sessions.Count == 0)
                return y;

            y = DrawSectionLabel(width, y, $"{label} ({sessions.Count})");
            foreach (var session in sessions)
            {
                var row = new Rect(0f, y, width, 64f);
                DrawSessionRow(row, comp, vm, session);
                y += 68f;
            }

            return y + 4f;
        }

        private void DrawSessionRow(Rect row, OverlordGameComponent comp, ViewerManager vm, ViewerSession session)
        {
            bool isSelected = session.username == selectedViewer;
            DrawRow(row, isSelected);

            var clickable = new Rect(row.x, row.y, row.width - 74f, row.height);
            if (Widgets.ButtonInvisible(clickable))
            {
                selectedViewer = session.username;
                if (session.HasPawn)
                    selectedPawnId = session.assignedPawn.thingIDNumber;
            }

            var pendingClaim = vm.GetPendingClaim(session.username);

            // Right column: ticket count + [+] on top, action button on bottom — 58px wide
            const float rightW = 60f;
            float rightX = row.xMax - rightW;

            var ticketRect = new Rect(rightX, row.y + 6f, 26f, 18f);
            GUI.color = MutedColor;
            Widgets.Label(ticketRect, $"{session.tickets}t");
            GUI.color = Color.white;
            TooltipHandler.TipRegion(ticketRect, $"{session.tickets} ticket{(session.tickets != 1 ? "s" : "")}");

            int maxTickets = OverlordMod.Settings?.maxTickets ?? 5;
            var plusRect = new Rect(rightX + 28f, row.y + 4f, 24f, 22f);
            if (session.tickets < maxTickets && BrassButton(plusRect, "+"))
                vm.GrantTicket(session.username);
            TooltipHandler.TipRegion(plusRect, $"Grant ticket ({session.tickets}/{maxTickets})");

            if (!session.HasPawn)
            {
                var actionRect = new Rect(rightX, row.y + 34f, rightW, 22f);
                string actionLabel = pendingClaim != null ? "Approve" : "Assign";
                if (BrassButton(actionRect, actionLabel))
                {
                    selectedViewer = session.username;
                    if (pendingClaim != null)
                        ApprovePendingClaim(comp, vm, pendingClaim);
                    else
                        Find.WindowStack.Add(new AssignmentDialog());
                }
            }
            else
            {
                var actionRect = new Rect(rightX, row.y + 34f, rightW, 22f);
                if (BrassButton(actionRect, "Jump"))
                    JumpToPawn(session.assignedPawn);
                TooltipHandler.TipRegion(actionRect, "Jump camera to this viewer's pawn");
            }

            // Left column: dot + name + status — all remaining width
            float textX = row.x + 8f;
            float textW = rightX - textX - 6f;

            var dotRect = new Rect(textX, row.y + 8f, 8f, 8f);
            Color dotColor = session.isConnected ? (session.HasPawn ? OnlineColor : WaitingColor) : OfflineColor;
            Widgets.DrawBoxSolid(dotRect, dotColor);

            var nameRect = new Rect(dotRect.xMax + 6f, row.y + 4f, textW - 14f, 20f);
            Widgets.Label(nameRect, session.displayName ?? session.username);

            string status = pendingClaim != null ? $"Claiming {pendingClaim.pawnName}" : BuildSessionStatus(session);
            var statusRect = new Rect(dotRect.xMax + 6f, row.y + 24f, textW - 14f, 18f);
            GUI.color = session.isConnected ? (pendingClaim != null ? WaitingColor : Color.white) : MutedColor;
            Widgets.Label(statusRect, status);
            GUI.color = Color.white;

            // Third line: last command issued — gives streamer live visibility
            if (!string.IsNullOrEmpty(session.lastCommandLabel))
            {
                var cmdRect = new Rect(dotRect.xMax + 6f, row.y + 43f, textW - 14f, 16f);
                GUI.color = BrassDimColor;
                Text.Font = GameFont.Tiny;
                Widgets.Label(cmdRect, $"↳ {session.lastCommandLabel}");
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }

            TooltipHandler.TipRegion(new Rect(row.x, row.y, textW + 14f, row.height), $"{session.displayName ?? session.username}\n{status}");
        }

        private void DrawColonistPane(Rect rect, OverlordGameComponent comp, ViewerManager vm, List<Pawn> colonists)
        {
            DrawPanel(rect, "Assignment Board");

            var open     = colonists.Where(p => vm.GetSessionForPawn(p) == null).ToList();
            var assigned = colonists.Where(p => vm.GetSessionForPawn(p) != null).ToList();
            var dead     = ReviveManager.GetDeadColonists();
            int sectionCount = (open.Count > 0 ? 1 : 0) + (assigned.Count > 0 ? 1 : 0) + (dead.Count > 0 ? 1 : 0);
            float contentH = colonists.Count * 52f + dead.Count * 52f + sectionCount * 26f + 8f;
            var contentRect = new Rect(0f, 0f, rect.width - 34f, Mathf.Max(rect.height - 42f, contentH));
            var viewRect    = new Rect(rect.x + 10f, rect.y + 30f, rect.width - 20f, rect.height - 40f);
            Widgets.BeginScrollView(viewRect, ref colonistScroll, contentRect);

            float y = 0f;
            y = DrawColonistGroup(contentRect.width, y, "Open", open,     comp, vm);
            y = DrawColonistGroup(contentRect.width, y, "Assigned", assigned, comp, vm);
            y = DrawDeadColonistGroup(contentRect.width, y, dead, vm);

            if (colonists.Count == 0 && dead.Count == 0)
            {
                GUI.color = MutedColor;
                Widgets.Label(new Rect(0f, 0f, contentRect.width, 22f), "No free colonists on the current map");
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
        }

        private float DrawDeadColonistGroup(float width, float y, List<ReviveManager.DeadColonistEntry> dead, ViewerManager vm)
        {
            if (dead == null || dead.Count == 0)
                return y;

            y = DrawSectionLabel(width, y, $"Dead ({dead.Count})");
            foreach (var entry in dead)
            {
                var row = new Rect(0f, y, width, 48f);
                DrawDeadColonistRow(row, entry, vm);
                y += 52f;
            }

            return y + 4f;
        }

        private void DrawDeadColonistRow(Rect row, ReviveManager.DeadColonistEntry entry, ViewerManager vm)
        {
            var pawn = entry.pawn;
            if (pawn == null) return;

            bool isSelected = pawn.thingIDNumber == selectedPawnId;
            DrawRow(row, isSelected);

            var clickable = new Rect(row.x, row.y, row.width - 70f, row.height);
            if (Widgets.ButtonInvisible(clickable))
                selectedPawnId = pawn.thingIDNumber;

            var nameRect = new Rect(row.x + 8f, row.y + 4f, row.width - 100f, 20f);
            GUI.color = OfflineColor;
            Widgets.Label(nameRect, pawn.LabelShort);
            GUI.color = Color.white;

            string ownerLine = !string.IsNullOrEmpty(entry.lastOwnerDisplayName)
                ? $"Was {entry.lastOwnerDisplayName}"
                : "Unassigned";
            var statusRect = new Rect(row.x + 8f, row.y + 25f, row.width - 100f, 18f);
            GUI.color = MutedColor;
            Widgets.Label(statusRect, ownerLine);
            GUI.color = Color.white;

            var actionRect = new Rect(row.xMax - 64f, row.y + 12f, 56f, 24f);
            if (BrassButton(actionRect, "Revive"))
                ReviveManager.TryReviveCorpse(entry.corpse);
        }

        private float DrawColonistGroup(float width, float y, string label, List<Pawn> pawns, OverlordGameComponent comp, ViewerManager vm)
        {
            if (pawns == null || pawns.Count == 0)
                return y;

            y = DrawSectionLabel(width, y, $"{label} ({pawns.Count})");
            foreach (var pawn in pawns)
            {
                var row = new Rect(0f, y, width, 48f);
                DrawColonistRow(row, comp, vm, pawn);
                y += 52f;
            }

            return y + 4f;
        }

        private void DrawColonistRow(Rect row, OverlordGameComponent comp, ViewerManager vm, Pawn pawn)
        {
            bool isSelected = pawn.thingIDNumber == selectedPawnId;
            DrawRow(row, isSelected);

            var owner = vm.GetSessionForPawn(pawn);
            var clickable = new Rect(row.x, row.y, row.width - 70f, row.height);
            if (Widgets.ButtonInvisible(clickable))
            {
                selectedPawnId = pawn.thingIDNumber;
                if (string.IsNullOrEmpty(selectedViewer) && owner != null)
                    selectedViewer = owner.username;
            }

            var nameRect = new Rect(row.x + 8f, row.y + 4f, row.width - 100f, 20f);
            Widgets.Label(nameRect, pawn.LabelShort);

            // Status line: HP · job on EVERY row — the pawns viewers control are
            // exactly the ones the streamer most needs to health-check at a glance.
            // Assigned rows prefix the owner name.
            string statusLine;
            Color statusColor;
            bool stuck = owner != null && owner.stuckAlertRaised;
            {
                int hp   = pawn.health?.summaryHealth != null ? Mathf.RoundToInt(pawn.health.summaryHealth.SummaryHealthPercent * 100f) : 100;
                string job = pawn.jobs?.curJob?.def?.reportString ?? pawn.CurJobDef?.label ?? "idle";
                // Trim "GotoLocation" and similar internal names
                if (job.StartsWith("Goto") || job == "wait" || job == "Wait") job = "idle";
                string ownerPrefix = owner != null ? (owner.displayName ?? owner.username) + "  ·  " : "";
                if (stuck)
                {
                    // A viewer-controlled pawn that has gone inert — the streamer's
                    // signal to intervene so no one sits on a dead pawn.
                    statusLine  = $"{ownerPrefix}STUCK — no job";
                    statusColor = OfflineColor;
                }
                else
                {
                    statusLine  = $"{ownerPrefix}{hp}%  ·  {job}";
                    statusColor = hp < 50 ? OfflineColor : hp < 75 ? WaitingColor : MutedColor;
                }
            }

            var statusRect = new Rect(row.x + 8f, row.y + 25f, row.width - 100f, 18f);
            GUI.color = statusColor;
            Widgets.Label(statusRect, statusLine);
            GUI.color = Color.white;

            var actionRect = new Rect(row.xMax - 64f, row.y + 12f, 56f, 24f);
            if (!string.IsNullOrEmpty(selectedViewer))
            {
                var selectedSession = vm.GetSession(selectedViewer);
                string label = selectedSession != null && owner != null && owner.username == selectedViewer ? "Jump" : "Assign";
                if (BrassButton(actionRect, label))
                {
                    if (label == "Jump")
                        JumpToPawn(pawn);
                    else
                        AssignViewerToPawn(comp, vm, selectedViewer, pawn);
                }
            }
            else if (owner != null && BrassButton(actionRect, "Owner"))
            {
                selectedViewer = owner.username;
            }
            else if (owner == null && BrassButton(actionRect, "Jump"))
            {
                JumpToPawn(pawn);
            }
        }

        private void DrawInspectorPane(Rect rect, OverlordGameComponent comp, ViewerManager vm, List<Pawn> colonists)
        {
            DrawPanel(rect, "Inspector");

            var innerRect = new Rect(rect.x + 10f, rect.y + 30f, rect.width - 20f, rect.height - 40f);
            var selectedSession = !string.IsNullOrEmpty(selectedViewer) ? vm.GetSession(selectedViewer) : null;
            var selectedPawn    = colonists.FirstOrDefault(p => p.thingIDNumber == selectedPawnId);
            ReviveManager.DeadColonistEntry? selectedDead = null;
            if (selectedPawn == null && selectedPawnId >= 0)
            {
                var dead = ReviveManager.GetDeadColonists();
                for (int i = 0; i < dead.Count; i++)
                {
                    if (dead[i].pawn != null && dead[i].pawn.thingIDNumber == selectedPawnId)
                    {
                        selectedDead = dead[i];
                        selectedPawn = dead[i].pawn;
                        break;
                    }
                }
            }

            float contentHeight = selectedSession != null ? 720f : 280f;
            var scrollRect = new Rect(0f, 0f, innerRect.width - 18f, Mathf.Max(contentHeight, innerRect.height));
            Widgets.BeginScrollView(innerRect, ref inspectorScroll, scrollRect);

            if (selectedSession != null)
                DrawViewerInspector(scrollRect, comp, vm, selectedSession, selectedPawn);
            else if (selectedDead.HasValue)
                DrawDeadPawnInspector(scrollRect, selectedDead.Value);
            else if (selectedPawn != null)
                DrawPawnInspector(scrollRect, vm, selectedPawn);
            else
                DrawEmptyInspector(scrollRect, vm);

            Widgets.EndScrollView();
        }

        private void DrawViewerInspector(Rect rect, OverlordGameComponent comp, ViewerManager vm, ViewerSession session, Pawn selectedPawn)
        {
            float y = 0f;

            // Header
            Widgets.Label(new Rect(0f, y, rect.width, 24f), session.displayName ?? session.username);
            y += 24f;

            GUI.color = session.isConnected ? OnlineColor : OfflineColor;
            Widgets.Label(new Rect(0f, y, rect.width, 20f), session.isConnected ? "Connected" : "Offline");
            GUI.color = Color.white;
            y += 20f;

            string pawnLabel = session.HasPawn ? session.assignedPawn.LabelShort : "No pawn assigned";
            Widgets.Label(new Rect(0f, y, rect.width, 22f), $"Pawn: {pawnLabel}");
            y += 22f;

            GUI.color = MutedColor;
            Widgets.Label(new Rect(0f, y, rect.width, 22f), $"Tickets: {session.tickets}");
            GUI.color = Color.white;
            y += 26f;

            // Pending claim
            var pendingClaim = vm.GetPendingClaim(session.username);
            if (pendingClaim != null)
            {
                Widgets.Label(new Rect(0f, y, rect.width, 22f), $"Pending claim: {pendingClaim.pawnName}");
                y += 24f;

                float claimW = (rect.width - 8f) / 2f;
                if (BrassButton(new Rect(0f,           y, claimW, 28f), "Approve Claim"))
                    ApprovePendingClaim(comp, vm, pendingClaim);
                if (BrassButton(new Rect(claimW + 8f,  y, claimW, 28f), "Reject Claim"))
                    vm.RejectClaim(session.username);
                y += 34f;
            }

            // Primary actions
            float btnW = (rect.width - 8f) / 2f;
            if (session.HasPawn)
            {
                if (BrassButton(new Rect(0f,        y, btnW, 28f), "Jump to Pawn"))
                    JumpToPawn(session.assignedPawn);
                if (BrassButton(new Rect(btnW + 8f, y, btnW, 28f), "Unassign"))
                {
                    vm.UnassignPawn(session.username);
                    vm.SendColonistList();
                }
                y += 34f;

                if (BrassButton(new Rect(0f, y, rect.width, 28f), "Refresh Viewer"))
                {
                    comp.HandleRequestStatePublic(session.username);
                }
                TooltipHandler.TipRegion(new Rect(0f, y, rect.width, 28f), "Force a full state update to this viewer's browser");
                y += 34f;
            }
            else
            {
                if (BrassButton(new Rect(0f, y, btnW, 28f), "Spawn for Viewer"))
                    SpawnAndAssign(session.username, vm);

                string assignLabel = selectedPawn != null ? $"Assign {selectedPawn.LabelShort}" : "Pick Colonist";
                if (BrassButton(new Rect(btnW + 8f, y, btnW, 28f), assignLabel))
                {
                    if (selectedPawn != null)
                        AssignViewerToPawn(comp, vm, session.username, selectedPawn);
                    else
                        Find.WindowStack.Add(new AssignmentDialog());
                }
                y += 34f;
            }

            y += 4f;
            Widgets.DrawLineHorizontal(0f, y, rect.width);
            y += 8f;

            // Moderation
            bool banned    = vm.IsBanned(session.username);
            bool inTimeout = vm.IsInTimeout(session.username, out int timeoutSecondsLeft);

            if (banned)
            {
                GUI.color = OfflineColor;
                Widgets.Label(new Rect(0f, y, rect.width, 20f), "BANNED");
                GUI.color = Color.white;
                y += 22f;
                if (BrassButton(new Rect(0f, y, rect.width, 28f), "Unban"))
                    vm.UnbanViewer(session.username);
                y += 34f;
            }
            else
            {
                if (inTimeout)
                {
                    GUI.color = WaitingColor;
                    Widgets.Label(new Rect(0f, y, rect.width, 20f), $"Timed out — {timeoutSecondsLeft}s remaining");
                    GUI.color = Color.white;
                    y += 22f;
                    if (BrassButton(new Rect(0f, y, rect.width, 28f), "Clear Timeout"))
                        vm.ClearTimeout(session.username);
                    y += 34f;
                }
                else
                {
                    float modW = (rect.width - 16f) / 3f;
                    if (BrassButton(new Rect(0f,                  y, modW, 28f), "Timeout 1m"))  vm.TimeoutViewer(session.username, 60);
                    if (BrassButton(new Rect(modW + 8f,           y, modW, 28f), "Timeout 5m"))  vm.TimeoutViewer(session.username, 300);
                    if (BrassButton(new Rect((modW + 8f) * 2f,    y, modW, 28f), "Timeout 15m")) vm.TimeoutViewer(session.username, 900);
                    y += 34f;
                }

                float kickW = (rect.width - 8f) / 2f;
                if (DangerButton(new Rect(0f,         y, kickW, 28f), "Kick"))
                    vm.KickViewer(session.username);
                TooltipHandler.TipRegion(new Rect(0f, y, kickW, 28f), "Disconnect. They can rejoin.");
                if (DangerButton(new Rect(kickW + 8f, y, kickW, 28f), "Ban"))
                    vm.BanViewer(session.username);
                TooltipHandler.TipRegion(new Rect(kickW + 8f, y, kickW, 28f), "Permanent ban — blocks reconnect and re-claim.");
                y += 34f;
            }

            y += 4f;
            Widgets.DrawLineHorizontal(0f, y, rect.width);
            y += 8f;

            // Permissions
            Widgets.Label(new Rect(0f, y, rect.width - 80f, 22f), "Allowed Commands");
            y += 26f;

            y = DrawPermissionGrid(rect.width, y, session.permissions, out bool permissionsChanged);
            if (permissionsChanged)
                vm.SendPermissions(session.username);

            y += 8f;
            Widgets.DrawLineHorizontal(0f, y, rect.width);
            y += 8f;

            // Advanced section — collapsible
            bool advancedWas = advancedInspectorExpanded;
            Widgets.CheckboxLabeled(new Rect(0f, y, rect.width, 22f), "Advanced", ref advancedInspectorExpanded);
            y += 26f;

            if (advancedInspectorExpanded)
            {
                float advW = (rect.width - 8f) / 2f;
                if (BrassButton(new Rect(0f,        y, advW, 28f), "Grant Ticket"))
                    vm.GrantTicket(session.username);
                if (BrassButton(new Rect(advW + 8f, y, advW, 28f), "Reset Permissions"))
                    session.permissions.ApplyDefaults();
                y += 34f;
            }
        }

        private void DrawPawnInspector(Rect rect, ViewerManager vm, Pawn pawn)
        {
            float y = 0f;
            Widgets.Label(new Rect(0f, y, rect.width, 24f), pawn.LabelShort);
            y += 26f;

            var owner = vm.GetSessionForPawn(pawn);
            string ownerLabel = owner != null ? $"Assigned to {owner.displayName ?? owner.username}" : "Open";
            GUI.color = owner != null ? Color.white : MutedColor;
            Widgets.Label(new Rect(0f, y, rect.width, 22f), ownerLabel);
            GUI.color = Color.white;
            y += 22f;

            Widgets.Label(new Rect(0f, y, rect.width, 22f), $"Position: {pawn.Position.x}, {pawn.Position.z}");
            y += 22f;

            if (BrassButton(new Rect(0f, y, rect.width, 28f), "Jump to Pawn"))
                JumpToPawn(pawn);
            y += 36f;

            if (!string.IsNullOrEmpty(selectedViewer) && vm.GetSession(selectedViewer) != null)
            {
                if (owner == null || owner.username != selectedViewer)
                {
                    if (BrassButton(new Rect(0f, y, rect.width, 28f), $"Assign {selectedViewer} → {pawn.LabelShort}"))
                        AssignViewerToPawn(OverlordGameComponent.Instance, vm, selectedViewer, pawn);
                    y += 36f;
                }
            }

            if (owner != null && BrassButton(new Rect(0f, y, rect.width, 28f), "Select Owner"))
                selectedViewer = owner.username;

            if (string.IsNullOrEmpty(selectedViewer))
            {
                y += owner != null ? 36f : 0f;
                GUI.color = MutedColor;
                Widgets.Label(new Rect(0f, y, rect.width, 60f), "Select a viewer on the left to assign this colonist.");
                GUI.color = Color.white;
            }
        }

        private void DrawDeadPawnInspector(Rect rect, ReviveManager.DeadColonistEntry entry)
        {
            float y = 0f;
            var pawn = entry.pawn;
            GUI.color = OfflineColor;
            Widgets.Label(new Rect(0f, y, rect.width, 24f), pawn?.LabelShort ?? "Dead colonist");
            GUI.color = Color.white;
            y += 26f;

            Widgets.Label(new Rect(0f, y, rect.width, 22f), "Status: Dead");
            y += 22f;

            string ownerLabel = !string.IsNullOrEmpty(entry.lastOwnerDisplayName)
                ? $"Previous owner: {entry.lastOwnerDisplayName}"
                : "Previous owner: none";
            GUI.color = MutedColor;
            Widgets.Label(new Rect(0f, y, rect.width, 22f), ownerLabel);
            GUI.color = Color.white;
            y += 28f;

            if (BrassButton(new Rect(0f, y, rect.width, 28f), "Revive"))
                ReviveManager.TryReviveCorpse(entry.corpse);
            y += 36f;

            GUI.color = MutedColor;
            Widgets.Label(new Rect(0f, y, rect.width, 60f), "Revive restores this colonist. Waiting previous owners are reassigned automatically.");
            GUI.color = Color.white;
        }

        private void DrawEmptyInspector(Rect rect, ViewerManager vm)
        {
            GUI.color = MutedColor;
            Widgets.Label(new Rect(0f, 0f, rect.width, 60f), "Select a viewer or colonist to inspect.");
            GUI.color = Color.white;

            float y = 72f;
            Widgets.Label(new Rect(0f, y, rect.width, 22f), "Default permissions");
            y += 26f;

            DrawPermissionGrid(rect.width, y, new ViewerPermissions(), out _);
        }

        private float DrawPermissionGrid(float width, float y, ViewerPermissions permissions, out bool changed)
        {
            changed = false;
            float colGap = 12f;
            float colW = (width - colGap) / 2f;

            float yL = y, yR = y;
            yL = DrawPermissionToggle(new Rect(0f,          yL, colW, 24f), "Draft / Undraft",   ref permissions.draft,      ref changed);
            yL = DrawPermissionToggle(new Rect(0f,          yL, colW, 24f), "Move",               ref permissions.move,       ref changed);
            yL = DrawPermissionToggle(new Rect(0f,          yL, colW, 24f), "Attack",             ref permissions.attack,     ref changed);
            yL = DrawPermissionToggle(new Rect(0f,          yL, colW, 24f), "Work",               ref permissions.work,       ref changed);
            yL = DrawPermissionToggle(new Rect(0f,          yL, colW, 24f), "Schedule",           ref permissions.schedule,   ref changed);
            yL = DrawPermissionToggle(new Rect(0f,          yL, colW, 24f), "Outfit",             ref permissions.outfit,     ref changed);

            yR = DrawPermissionToggle(new Rect(colW+colGap, yR, colW, 24f), "Drug Policy",        ref permissions.drugPolicy, ref changed);
            yR = DrawPermissionToggle(new Rect(colW+colGap, yR, colW, 24f), "Food Policy",        ref permissions.foodPolicy, ref changed);
            yR = DrawPermissionToggle(new Rect(colW+colGap, yR, colW, 24f), "Area Restriction",   ref permissions.area,       ref changed);
            yR = DrawPermissionToggle(new Rect(colW+colGap, yR, colW, 24f), "Equip / Drop",       ref permissions.equip,      ref changed);
            yR = DrawPermissionToggle(new Rect(colW+colGap, yR, colW, 24f), "Appearance",         ref permissions.appearance, ref changed);

            return Mathf.Max(yL, yR);
        }

        private float DrawPermissionToggle(Rect rect, string label, ref bool value, ref bool changed)
        {
            bool before = value;
            Widgets.CheckboxLabeled(rect, label, ref value);
            if (before != value) changed = true;
            return rect.yMax + 2f;
        }

        private void DrawActionStrip(Rect rect, OverlordGameComponent comp, ViewerManager vm)
        {
            DrawPanel(rect, "Broadcast and Vote");

            float y      = rect.y + 30f;
            float innerX = rect.x + 12f;
            float innerW = rect.width - 24f;

            // Broadcast row
            var msgRect = new Rect(innerX, y, innerW - 70f, 28f);
            broadcastText = Widgets.TextField(msgRect, broadcastText);
            if (string.IsNullOrEmpty(broadcastText))
            {
                GUI.color = MutedColor;
                Widgets.Label(new Rect(msgRect.x + 4f, msgRect.y + 6f, msgRect.width - 8f, 18f), "Message to all viewers…");
                GUI.color = Color.white;
            }
            if (BrassButton(new Rect(msgRect.xMax + 6f, y, 64f, 28f), "Send") && !string.IsNullOrEmpty(broadcastText))
            {
                var msg = new Dictionary<string, object>
                {
                    ["type"]    = "admin_message",
                    ["message"] = broadcastText
                };
                comp.Relay?.Broadcast(msg);
                comp.EmbeddedServer?.Broadcast(JsonHelper.ToJson(msg));
                Messages.Message($"[Overlord] Broadcast: {broadcastText}", MessageTypeDefOf.NeutralEvent, historical: false);
                broadcastText = "";
            }

            y += 38f;

            // Vote row
            var vote = comp.VoteManager;
            if (vote.active)
            {
                if (BrassButton(new Rect(innerX, y, 120f, 28f), "End Vote"))
                    vote.EndVote();

                var counts = vote.GetCounts();
                float x = innerX + 128f;
                for (int i = 0; i < vote.options.Count; i++)
                {
                    Widgets.Label(new Rect(x, y + 4f, 160f, 22f), $"{vote.options[i]}: {counts[i]}");
                    x += 160f;
                }
            }
            else
            {
                float buttonW    = 128f;
                float questionW  = Mathf.Max(170f, (innerW - buttonW - 24f) * 0.5f);
                float optionsW   = innerW - questionW - buttonW - 16f;

                var qRect = new Rect(innerX, y, questionW, 28f);
                var oRect = new Rect(innerX + questionW + 8f, y, optionsW, 28f);

                voteQuestion = Widgets.TextField(qRect, voteQuestion);
                voteOptions  = Widgets.TextField(oRect, voteOptions);

                if (string.IsNullOrEmpty(voteQuestion))
                {
                    GUI.color = MutedColor;
                    Widgets.Label(new Rect(qRect.x + 4f, qRect.y + 6f, qRect.width - 8f, 18f), "Vote question…");
                    GUI.color = Color.white;
                }
                if (string.IsNullOrEmpty(voteOptions))
                {
                    GUI.color = MutedColor;
                    Widgets.Label(new Rect(oRect.x + 4f, oRect.y + 6f, oRect.width - 8f, 18f), "Options, comma-separated…");
                    GUI.color = Color.white;
                }

                if (BrassButton(new Rect(innerX + questionW + optionsW + 16f, y, buttonW, 28f), "Start Vote"))
                    TryStartVote(vote);
            }
        }

        private void TryStartVote(VoteManager vote)
        {
            if (vote == null || string.IsNullOrEmpty(voteQuestion) || string.IsNullOrEmpty(voteOptions))
                return;

            var opts = voteOptions.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (opts.Count == 0) return;

            vote.StartVote(voteQuestion, opts);
            voteQuestion = "";
            voteOptions  = "";
        }

        private void AssignViewerToPawn(OverlordGameComponent comp, ViewerManager vm, string viewerUsername, Pawn pawn)
        {
            if (string.IsNullOrEmpty(viewerUsername) || pawn == null) return;

            if (vm.AssignPawn(viewerUsername, pawn))
            {
                selectedViewer = viewerUsername;
                selectedPawnId = pawn.thingIDNumber;
                vm.SendColonistList();
                comp.HandleRequestStatePublic(viewerUsername);
            }
        }

        private void ApprovePendingClaim(OverlordGameComponent comp, ViewerManager vm, PendingClaim claim)
        {
            if (claim == null) return;

            var pawn = vm.FindPawnById(claim.pawnId);
            if (pawn == null) { vm.RejectClaim(claim.username); return; }

            AssignViewerToPawn(comp, vm, claim.username, pawn);
        }

        private void SyncSelection(ViewerManager vm, List<Pawn> colonists)
        {
            if (!string.IsNullOrEmpty(selectedViewer) && vm.GetSession(selectedViewer) == null)
                selectedViewer = null;

            if (selectedPawnId >= 0
                && colonists.All(p => p.thingIDNumber != selectedPawnId)
                && ReviveManager.GetDeadColonists().All(e => e.pawn == null || e.pawn.thingIDNumber != selectedPawnId))
            {
                selectedPawnId = -1;
            }
        }

        private List<Pawn> GetColonists()
        {
            return Find.CurrentMap?.mapPawns?.FreeColonists?.OrderBy(p => p.LabelShort).ToList()
                ?? new List<Pawn>();
        }

        private string BuildSessionStatus(ViewerSession session)
        {
            if (session.HasPawn)
                return session.isConnected
                    ? $"Controlling {session.assignedPawn.LabelShort}"
                    : $"Offline with {session.assignedPawn.LabelShort}";

            return session.isConnected ? "Waiting for assignment" : "Offline session";
        }

        private string BuildCapabilityWarnings()
        {
            var warnings = new List<string>();
            if (!RimWorldCompat.SupportsContextMenus)        warnings.Add("No context actions");
            if (!RimWorldCompat.SupportsPortraitRendering)   warnings.Add("No portraits");
            if (!RimWorldCompat.SupportsColonistBarOverlay)  warnings.Add("No colonist bar overlay");
            return warnings.Count == 0 ? "" : "Capability limits: " + string.Join(" | ", warnings);
        }

        private string BuildLiveRiskSummary()
        {
            var s = OverlordMod.Settings;
            if (s == null) return "";

            var risks = new List<string>();
            if (s.allowViewerEvents)     risks.Add("viewer events on");
            if (s.allowViewerTacticalMap) risks.Add("tactical map exposed");
            if (s.allowViewerResourceReadout) risks.Add("resource readout exposed");
            if (!s.enforceAreaRestrictions) risks.Add("area limits off");
            if (s.mapUpdateInterval < 0.09f || s.mapImageQuality > 84 || s.mapImageSize > 1280)
                risks.Add("camera load high");

            return risks.Count == 0 ? "" : "Live risks: " + string.Join(", ", risks);
        }

        private float DrawSectionLabel(float width, float y, string label)
        {
            GUI.color = MutedColor;
            Widgets.Label(new Rect(0f, y, width, 20f), label);
            GUI.color = Color.white;
            return y + 24f;
        }

        private void DrawButtonRow(Rect rect, List<ConsoleButton> buttons)
        {
            if (buttons == null || buttons.Count == 0) return;
            float gap = 8f;
            float buttonW = (rect.width - gap * (buttons.Count - 1)) / buttons.Count;
            float x = rect.x;
            foreach (var button in buttons)
            {
                if (BrassButton(new Rect(x, rect.y, buttonW, rect.height), button.Label))
                    button.OnClick?.Invoke();
                x += buttonW + gap;
            }
        }

        private void JumpToPawn(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return;
            CameraJumper.TryJumpAndSelect(pawn);
        }

        private class ConsoleButton
        {
            public string Label;
            public System.Action OnClick;
        }

        private bool BrassButton(Rect rect, string label)
        {
            bool hover = Mouse.IsOver(rect);
            Widgets.DrawBoxSolid(rect, hover ? new Color(0.34f, 0.24f, 0.12f, 0.92f) : new Color(0.22f, 0.16f, 0.09f, 0.90f));
            DrawFineBorder(rect, hover ? BrassColor : BrassDimColor);
            DrawFineBorder(rect.ContractedBy(2f), BrassSoftColor);

            var oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color   = TextColor;
            Widgets.Label(rect, label);
            Text.Anchor = oldAnchor;
            GUI.color   = Color.white;

            return Widgets.ButtonInvisible(rect);
        }

        private bool DangerButton(Rect rect, string label)
        {
            bool hover = Mouse.IsOver(rect);
            Widgets.DrawBoxSolid(rect, hover ? new Color(0.55f, 0.10f, 0.10f, 0.94f) : DangerFill);
            DrawFineBorder(rect, hover ? new Color(0.88f, 0.38f, 0.38f) : DangerBorder);
            DrawFineBorder(rect.ContractedBy(2f), new Color(0.72f, 0.28f, 0.28f, 0.30f));

            var oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color   = new Color(1f, 0.78f, 0.78f);
            Widgets.Label(rect, label);
            Text.Anchor = oldAnchor;
            GUI.color   = Color.white;

            return Widgets.ButtonInvisible(rect);
        }

        private void DrawFineBorder(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            Widgets.DrawLineHorizontal(rect.x, rect.y,         rect.width);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
            Widgets.DrawLineVertical(rect.x,         rect.y, rect.height);
            Widgets.DrawLineVertical(rect.xMax - 1f, rect.y, rect.height);
            GUI.color = old;
        }

        private void DrawCornerTicks(Rect rect)
        {
            Color old = GUI.color;
            GUI.color = BrassColor;
            float len = 10f;
            Widgets.DrawLineHorizontal(rect.x + 4f,        rect.y + 4f,    len);
            Widgets.DrawLineVertical(  rect.x + 4f,        rect.y + 4f,    len);
            Widgets.DrawLineHorizontal(rect.xMax - len - 4f, rect.y + 4f,  len);
            Widgets.DrawLineVertical(  rect.xMax - 5f,     rect.y + 4f,    len);
            Widgets.DrawLineHorizontal(rect.x + 4f,        rect.yMax - 5f, len);
            Widgets.DrawLineVertical(  rect.x + 4f,        rect.yMax - len - 4f, len);
            Widgets.DrawLineHorizontal(rect.xMax - len - 4f, rect.yMax - 5f, len);
            Widgets.DrawLineVertical(  rect.xMax - 5f,     rect.yMax - len - 4f, len);
            GUI.color = old;
        }

        private void DrawPanel(Rect rect, string title)
        {
            Widgets.DrawBoxSolid(rect, PanelFill);
            DrawFineBorder(rect, BrassDimColor);
            DrawFineBorder(rect.ContractedBy(2f), BrassSoftColor);

            var headerRect = new Rect(rect.x, rect.y, rect.width, 24f);
            Widgets.DrawBoxSolid(headerRect, HeaderFill);
            Widgets.DrawLineHorizontal(headerRect.x + 8f, headerRect.yMax - 1f, headerRect.width - 16f);
            GUI.color   = BrassColor;
            Text.Font   = GameFont.Small;
            Widgets.Label(new Rect(headerRect.x + 10f, headerRect.y + 3f, headerRect.width - 20f, 18f), title);
            GUI.color   = Color.white;
            DrawCornerTicks(rect);
        }

        private void DrawRow(Rect rect, bool selected)
        {
            Widgets.DrawBoxSolid(rect, selected ? SelectedFill : RowFill);
            DrawFineBorder(rect, selected ? BrassColor : BrassSoftColor);
            if (selected)
                Widgets.DrawLineHorizontal(rect.x + 8f, rect.y + 1f, rect.width - 16f);
        }

        private void SpawnNewColonist(ViewerManager vm)
        {
            var map = Find.CurrentMap;
            if (map == null) return;

            try
            {
                var request = new PawnGenerationRequest(
                    PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                    developmentalStages: DevelopmentalStage.Adult);
                Pawn newPawn = PawnGenerator.GeneratePawn(request);
                IntVec3 spawnCell = CellFinder.RandomClosewalkCellNear(map.Center, map, 10);
                GenSpawn.Spawn(newPawn, spawnCell, map, Rot4.South, WipeMode.Vanish);
                Messages.Message($"[Overlord] Spawned new colonist: {newPawn.LabelShort}", newPawn, MessageTypeDefOf.PositiveEvent, historical: false);
                vm?.SendColonistList();
            }
            catch (System.Exception ex) { LogUtil.Error($"Spawn failed: {ex.Message}"); }
        }

        private void SpawnAndAssign(string username, ViewerManager vm)
        {
            if (string.IsNullOrEmpty(username) || vm == null) return;

            if (vm.TryAssignExistingPawnForViewer(username, out Pawn existingPawn))
            {
                selectedViewer = username;
                selectedPawnId = existingPawn.thingIDNumber;
                vm.SendColonistList();
                OverlordGameComponent.Instance?.HandleRequestStatePublic(username);
                Messages.Message($"[Overlord] {username} is already assigned to {existingPawn.LabelShort}", existingPawn, MessageTypeDefOf.NeutralEvent, historical: false);
                return;
            }

            var map = Find.CurrentMap;
            if (map == null) return;

            try
            {
                var request = new PawnGenerationRequest(
                    PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                    developmentalStages: DevelopmentalStage.Adult);
                Pawn newPawn = PawnGenerator.GeneratePawn(request);
                IntVec3 spawnCell = CellFinder.RandomClosewalkCellNear(map.Center, map, 10);
                GenSpawn.Spawn(newPawn, spawnCell, map, Rot4.South, WipeMode.Vanish);

                vm.AssignPawn(username, newPawn);
                selectedViewer = username;
                selectedPawnId = newPawn.thingIDNumber;
                vm.SendColonistList();
                OverlordGameComponent.Instance?.HandleRequestStatePublic(username);

                Messages.Message($"[Overlord] Spawned {newPawn.LabelShort} for {username}", newPawn, MessageTypeDefOf.PositiveEvent, historical: false);
            }
            catch (System.Exception ex) { LogUtil.Error($"Spawn+assign failed: {ex.Message}"); }
        }
    }
}
