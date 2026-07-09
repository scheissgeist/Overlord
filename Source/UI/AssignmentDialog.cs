using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// In-game quick picker for assigning viewers to colonists.
    /// The main tab is the full console; this dialog is the fast live fallback.
    /// </summary>
    public class AssignmentDialog : Window
    {
        public override Vector2 InitialSize => new Vector2(720f, 560f);

        private static readonly Color WindowFill = new Color(0.025f, 0.04f, 0.055f, 0.96f);
        private static readonly Color RowFill = new Color(0.95f, 0.80f, 0.52f, 0.035f);
        private static readonly Color SelectedFill = new Color(0.34f, 0.24f, 0.10f, 0.60f);
        private static readonly Color BrassColor = new Color(0.82f, 0.61f, 0.32f);
        private static readonly Color BrassDimColor = new Color(0.45f, 0.32f, 0.16f);
        private static readonly Color BrassSoftColor = new Color(0.82f, 0.61f, 0.32f, 0.30f);
        private static readonly Color TextColor = new Color(0.88f, 0.84f, 0.75f);

        private Vector2 colonistScroll;
        private Vector2 viewerScroll;
        private int selectedPawnId = -1;
        private string selectedViewer = null;

        public AssignmentDialog()
        {
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var vm = OverlordGameComponent.Instance?.Viewers;
            if (vm == null)
            {
                Widgets.Label(inRect, "Overlord not initialized.");
                return;
            }

            Text.Font = GameFont.Small;
            Widgets.DrawBoxSolid(inRect, WindowFill);
            DrawFineBorder(inRect, BrassDimColor);

            var titleRect = inRect.TopPartPixels(30f);
            Text.Font = GameFont.Medium;
            GUI.color = BrassColor;
            Widgets.Label(titleRect, "Quick assignment");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            var helpRect = new Rect(inRect.x, titleRect.yMax + 2f, inRect.width, 34f);
            GUI.color = new Color(0.7f, 0.7f, 0.74f);
            Widgets.Label(helpRect, "Pick a colonist, pick a viewer, then assign. Use Unassign to clear the current owner.");
            GUI.color = Color.white;

            float y = helpRect.yMax + 8f;
            float halfW = (inRect.width - 12f) / 2f;

            var map = Find.CurrentMap;
            List<Pawn> colonists = map != null
                ? map.mapPawns.FreeColonists
                    .OrderBy(p => vm.GetSessionForPawn(p) != null)
                    .ThenBy(p => p.LabelShort)
                    .ToList()
                : new List<Pawn>();

            var sessions = vm.AllSessions
                .Where(s => s != null)
                .OrderByDescending(s => s.isConnected && !s.HasPawn)
                .ThenByDescending(s => s.isConnected)
                .ThenBy(s => s.displayName ?? s.username ?? "")
                .ToList();

            DrawColonistPicker(new Rect(inRect.x, y, halfW, inRect.height - y - 50f), vm, colonists);
            DrawViewerPicker(new Rect(inRect.x + halfW + 12f, y, halfW, inRect.height - y - 50f), vm, sessions);
            DrawFooter(new Rect(inRect.x, inRect.yMax - 40f, inRect.width, 34f), vm, colonists, sessions);
        }

        private void DrawColonistPicker(Rect rect, ViewerManager vm, List<Pawn> colonists)
        {
            GUI.color = BrassColor;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 20f), "Colonists");
            GUI.color = Color.white;

            var outRect = new Rect(rect.x, rect.y + 24f, rect.width, rect.height - 24f);
            var viewRect = new Rect(0f, 0f, rect.width - 18f, Mathf.Max(outRect.height, colonists.Count * 42f + 4f));
            Widgets.BeginScrollView(outRect, ref colonistScroll, viewRect);

            float y = 0f;
            foreach (var pawn in colonists)
            {
                var row = new Rect(0f, y, viewRect.width, 40f);
                bool selected = pawn.thingIDNumber == selectedPawnId;
                DrawRow(row, selected);

                var owner = vm.GetSessionForPawn(pawn);
                Widgets.Label(new Rect(row.x + 4f, row.y + 2f, row.width - 82f, 18f), pawn.LabelShort);

                GUI.color = owner != null ? new Color(0.35f, 0.9f, 0.4f) : new Color(0.62f, 0.62f, 0.66f);
                string status = owner != null ? $"Owned by {owner.displayName ?? owner.username}" : "Open colonist";
                Widgets.Label(new Rect(row.x + 4f, row.y + 20f, row.width - 82f, 18f), status);
                GUI.color = Color.white;

                var jumpRect = new Rect(row.xMax - 66f, row.y + 8f, 58f, 24f);
                if (BrassButton(jumpRect, "Jump"))
                    CameraJumper.TryJumpAndSelect(pawn);

                if (Widgets.ButtonInvisible(row.LeftPartPixels(row.width - 72f)))
                    selectedPawnId = selected ? -1 : pawn.thingIDNumber;

                y += 42f;
            }

            if (colonists.Count == 0)
            {
                GUI.color = new Color(0.62f, 0.62f, 0.66f);
                Widgets.Label(new Rect(0f, 0f, viewRect.width, 24f), "No colonists on this map.");
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
        }

        private void DrawViewerPicker(Rect rect, ViewerManager vm, List<ViewerSession> sessions)
        {
            GUI.color = BrassColor;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 20f), "Viewers");
            GUI.color = Color.white;

            var outRect = new Rect(rect.x, rect.y + 24f, rect.width, rect.height - 24f);
            var viewRect = new Rect(0f, 0f, rect.width - 18f, Mathf.Max(outRect.height, (sessions.Count + 1) * 42f + 4f));
            Widgets.BeginScrollView(outRect, ref viewerScroll, viewRect);

            float y = 0f;
            var unassignRow = new Rect(0f, y, viewRect.width, 40f);
            bool unassignSelected = selectedViewer == "";
            DrawRow(unassignRow, unassignSelected);
            GUI.color = new Color(0.9f, 0.45f, 0.45f);
            Widgets.Label(new Rect(unassignRow.x + 4f, unassignRow.y + 10f, unassignRow.width - 8f, 20f), "Unassign selected colonist");
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(unassignRow))
                selectedViewer = unassignSelected ? null : "";
            y += 42f;

            foreach (var session in sessions)
            {
                var row = new Rect(0f, y, viewRect.width, 40f);
                bool selected = session.username == selectedViewer;
                DrawRow(row, selected);

                Widgets.Label(new Rect(row.x + 4f, row.y + 2f, row.width - 36f, 18f), session.displayName ?? session.username);

                string status;
                Color statusColor;
                var claim = vm.GetPendingClaim(session.username);
                if (claim != null)
                {
                    status = $"Claiming {claim.pawnName}";
                    statusColor = new Color(0.95f, 0.8f, 0.25f);
                }
                else if (session.HasPawn)
                {
                    status = $"Controls {session.assignedPawn.LabelShort}";
                    statusColor = new Color(0.35f, 0.9f, 0.4f);
                }
                else if (session.isConnected)
                {
                    status = $"Waiting, {session.tickets} ticket{(session.tickets == 1 ? "" : "s")}";
                    statusColor = new Color(0.62f, 0.62f, 0.66f);
                }
                else
                {
                    status = "Offline";
                    statusColor = new Color(0.62f, 0.62f, 0.66f);
                }

                GUI.color = statusColor;
                Widgets.Label(new Rect(row.x + 4f, row.y + 20f, row.width - 36f, 18f), status);
                GUI.color = Color.white;

                int maxT = OverlordMod.Settings?.maxTickets ?? 5;
                var grantRect = new Rect(row.xMax - 28f, row.y + 8f, 24f, 24f);
                if (session.tickets < maxT && BrassButton(grantRect, "+"))
                    vm.GrantTicket(session.username);
                TooltipHandler.TipRegion(grantRect, $"Grant ticket ({session.tickets}/{maxT})");

                if (Widgets.ButtonInvisible(row.LeftPartPixels(row.width - 32f)))
                    selectedViewer = selected ? null : session.username;

                y += 42f;
            }

            if (sessions.Count == 0)
            {
                GUI.color = new Color(0.62f, 0.62f, 0.66f);
                Widgets.Label(new Rect(0f, y, viewRect.width, 24f), "No viewers connected yet.");
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
        }

        private void DrawFooter(Rect rect, ViewerManager vm, List<Pawn> colonists, List<ViewerSession> sessions)
        {
            bool canAssign = selectedPawnId >= 0 && selectedViewer != null;
            string btnLabel = selectedViewer == "" ? "Unassign" : "Assign";

            var btnRect = new Rect(rect.x, rect.y, 140f, rect.height);
            if (canAssign && BrassButton(btnRect, btnLabel))
            {
                var pawn = colonists.FirstOrDefault(p => p.thingIDNumber == selectedPawnId);
                if (pawn != null)
                {
                    if (selectedViewer == "")
                    {
                        var existing = vm.GetSessionForPawn(pawn);
                        if (existing != null)
                        {
                            vm.UnassignPawn(existing.username);
                            vm.SendColonistList();
                        }
                    }
                    else if (vm.AssignPawn(selectedViewer, pawn))
                    {
                        vm.SendColonistList();
                        OverlordGameComponent.Instance?.HandleRequestStatePublic(selectedViewer);
                    }
                }

                selectedPawnId = -1;
                selectedViewer = null;
            }

            if (!canAssign)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.35f);
                DrawFineBorder(btnRect, BrassDimColor);
                Widgets.Label(btnRect.ContractedBy(4f), "Assign");
                GUI.color = Color.white;
            }

            string pawnLabel = selectedPawnId >= 0
                ? colonists.FirstOrDefault(p => p.thingIDNumber == selectedPawnId)?.LabelShort ?? "selected colonist"
                : "no colonist";
            string viewerLabel = selectedViewer == ""
                ? "unassign"
                : selectedViewer != null
                    ? sessions.FirstOrDefault(s => s.username == selectedViewer)?.displayName ?? selectedViewer
                    : "no viewer";
            Widgets.Label(new Rect(btnRect.xMax + 12f, rect.y + 8f, rect.width - btnRect.width - 12f, 22f), $"Selected: {pawnLabel} -> {viewerLabel}");
        }

        private void DrawRow(Rect rect, bool selected)
        {
            Widgets.DrawBoxSolid(rect, selected ? SelectedFill : RowFill);
            DrawFineBorder(rect, selected ? BrassColor : BrassSoftColor);
        }

        private bool BrassButton(Rect rect, string label)
        {
            bool hover = Mouse.IsOver(rect);
            Widgets.DrawBoxSolid(rect, hover
                ? new Color(0.34f, 0.24f, 0.12f, 0.92f)
                : new Color(0.22f, 0.16f, 0.09f, 0.90f));
            DrawFineBorder(rect, hover ? BrassColor : BrassDimColor);
            DrawFineBorder(rect.ContractedBy(2f), BrassSoftColor);

            var oldAnchor = Text.Anchor;
            var oldColor = GUI.color;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = TextColor;
            Widgets.Label(rect, label);
            Text.Anchor = oldAnchor;
            GUI.color = oldColor;

            return Widgets.ButtonInvisible(rect);
        }

        private void DrawFineBorder(Rect rect, Color color)
        {
            Color oldColor = GUI.color;
            GUI.color = color;
            Widgets.DrawLineHorizontal(rect.x, rect.y, rect.width);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
            Widgets.DrawLineVertical(rect.x, rect.y, rect.height);
            Widgets.DrawLineVertical(rect.xMax - 1f, rect.y, rect.height);
            GUI.color = oldColor;
        }
    }
}
