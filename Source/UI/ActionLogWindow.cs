using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Live action log window. Filterable by username and kind. Click to jump
    /// the camera to the pawn referenced by the entry.
    /// </summary>
    public class ActionLogWindow : Window
    {
        private static readonly Color FailColor = new Color(0.85f, 0.45f, 0.45f);
        private static readonly Color ModColor = new Color(0.95f, 0.7f, 0.3f);
        private static readonly Color DeathColor = new Color(0.9f, 0.4f, 0.4f);
        private static readonly Color ClaimColor = new Color(0.5f, 0.85f, 1f);
        private static readonly Color AssignColor = new Color(0.4f, 0.85f, 0.5f);
        private static readonly Color MutedColor = new Color(0.65f, 0.65f, 0.7f);

        public override Vector2 InitialSize => new Vector2(640f, 520f);

        private Vector2 scroll;
        private string usernameFilter = "";
        private bool autoScroll = true;
        private bool showCommands = true;
        private bool showFailed = true;
        private bool showClaims = true;
        private bool showAssign = true;
        private bool showDeaths = true;
        private bool showMod = true;

        public ActionLogWindow()
        {
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            draggable = true;
            resizeable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;

            var titleRect = new Rect(inRect.x, inRect.y, inRect.width, 26f);
            Text.Font = GameFont.Medium;
            Widgets.Label(titleRect, "Overlord — Action Log");
            Text.Font = GameFont.Small;

            float y = titleRect.yMax + 4f;

            // Filter row
            var filterLabelRect = new Rect(inRect.x, y, 60f, 24f);
            Widgets.Label(filterLabelRect, "User:");
            var filterRect = new Rect(filterLabelRect.xMax + 4f, y, 180f, 24f);
            usernameFilter = Widgets.TextField(filterRect, usernameFilter ?? "");

            var clearRect = new Rect(filterRect.xMax + 6f, y, 60f, 24f);
            if (Widgets.ButtonText(clearRect, "Clear"))
                ActionLog.Clear();
            TooltipHandler.TipRegion(clearRect, "Clear all entries");

            var autoRect = new Rect(clearRect.xMax + 8f, y, 110f, 24f);
            Widgets.CheckboxLabeled(autoRect, "Auto-scroll", ref autoScroll);

            var countRect = new Rect(autoRect.xMax + 8f, y, inRect.xMax - autoRect.xMax - 12f, 24f);
            GUI.color = MutedColor;
            Widgets.Label(countRect, $"{ActionLog.Count} entries");
            GUI.color = Color.white;
            y += 28f;

            // Kind toggles
            float toggleW = (inRect.width - 12f) / 6f;
            Widgets.CheckboxLabeled(new Rect(inRect.x, y, toggleW, 22f), "Commands", ref showCommands);
            Widgets.CheckboxLabeled(new Rect(inRect.x + toggleW, y, toggleW, 22f), "Failed", ref showFailed);
            Widgets.CheckboxLabeled(new Rect(inRect.x + toggleW * 2f, y, toggleW, 22f), "Claims", ref showClaims);
            Widgets.CheckboxLabeled(new Rect(inRect.x + toggleW * 3f, y, toggleW, 22f), "Assign", ref showAssign);
            Widgets.CheckboxLabeled(new Rect(inRect.x + toggleW * 4f, y, toggleW, 22f), "Deaths", ref showDeaths);
            Widgets.CheckboxLabeled(new Rect(inRect.x + toggleW * 5f, y, toggleW, 22f), "Mod", ref showMod);
            y += 26f;

            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 4f;

            var snapshot = ActionLog.Snapshot();
            var filtered = FilterEntries(snapshot);

            float listH = inRect.yMax - y;
            var outRect = new Rect(inRect.x, y, inRect.width, listH);
            float rowH = 22f;
            float contentH = filtered.Count * rowH + 4f;
            var viewRect = new Rect(0f, 0f, outRect.width - 18f, Mathf.Max(contentH, outRect.height));

            if (autoScroll)
                scroll.y = Mathf.Max(0f, contentH - outRect.height);

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float ry = 0f;
            foreach (var entry in filtered)
            {
                var row = new Rect(0f, ry, viewRect.width, rowH);
                if (Mouse.IsOver(row))
                    Widgets.DrawHighlight(row);

                string time = entry.when.ToString("HH:mm:ss");
                var timeRect = new Rect(2f, ry, 70f, rowH);
                GUI.color = MutedColor;
                Widgets.Label(timeRect, time);
                GUI.color = Color.white;

                var userRect = new Rect(timeRect.xMax + 4f, ry, 130f, rowH);
                Widgets.Label(userRect, entry.username ?? "");

                var kindRect = new Rect(userRect.xMax + 4f, ry, 70f, rowH);
                GUI.color = ColorForKind(entry.kind);
                Widgets.Label(kindRect, KindLabel(entry.kind));
                GUI.color = Color.white;

                var msgRect = new Rect(kindRect.xMax + 4f, ry, viewRect.width - kindRect.xMax - 8f, rowH);
                string text = string.IsNullOrEmpty(entry.action)
                    ? entry.message ?? ""
                    : (string.IsNullOrEmpty(entry.message) ? entry.action : $"{entry.action} — {entry.message}");
                Widgets.Label(msgRect, text);

                if (entry.pawnId.HasValue && Widgets.ButtonInvisible(row))
                {
                    var pawn = FindPawn(entry.pawnId.Value);
                    if (pawn != null && pawn.Spawned)
                    {
                        CameraJumper.TryJumpAndSelect(pawn);
                        autoScroll = false;
                    }
                }
                ry += rowH;
            }

            if (filtered.Count == 0)
            {
                GUI.color = MutedColor;
                Widgets.Label(new Rect(0f, 0f, viewRect.width, 22f), "No entries match the filter");
                GUI.color = Color.white;
            }

            Widgets.EndScrollView();
        }

        private List<ActionLogEntry> FilterEntries(List<ActionLogEntry> entries)
        {
            string user = (usernameFilter ?? "").Trim();
            return entries.Where(e =>
            {
                if (e == null) return false;
                if (!string.IsNullOrEmpty(user) && (e.username ?? "").IndexOf(user, System.StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
                switch (e.kind)
                {
                    case ActionLogKind.Command: return showCommands;
                    case ActionLogKind.CommandFailed: return showFailed;
                    case ActionLogKind.Claim: return showClaims;
                    case ActionLogKind.Assignment:
                    case ActionLogKind.Unassignment:
                        return showAssign;
                    case ActionLogKind.Death: return showDeaths;
                    case ActionLogKind.Moderation: return showMod;
                    default: return true;
                }
            }).ToList();
        }

        private static Color ColorForKind(ActionLogKind kind)
        {
            switch (kind)
            {
                case ActionLogKind.CommandFailed: return FailColor;
                case ActionLogKind.Moderation: return ModColor;
                case ActionLogKind.Death: return DeathColor;
                case ActionLogKind.Claim: return ClaimColor;
                case ActionLogKind.Assignment:
                case ActionLogKind.Unassignment:
                    return AssignColor;
                default: return Color.white;
            }
        }

        private static string KindLabel(ActionLogKind kind)
        {
            switch (kind)
            {
                case ActionLogKind.Command: return "cmd";
                case ActionLogKind.CommandFailed: return "fail";
                case ActionLogKind.Claim: return "claim";
                case ActionLogKind.Assignment: return "assign";
                case ActionLogKind.Unassignment: return "unassign";
                case ActionLogKind.Death: return "death";
                case ActionLogKind.Moderation: return "mod";
                default: return "system";
            }
        }

        private static Pawn FindPawn(int thingId)
        {
            var map = Find.CurrentMap;
            if (map == null) return null;
            foreach (var p in map.mapPawns.AllPawnsSpawned)
                if (p.thingIDNumber == thingId) return p;
            return null;
        }
    }
}
