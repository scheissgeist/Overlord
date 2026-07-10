using System.Linq;
using UnityEngine;
using Verse;
using Verse.Sound;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Host-facing claim / waiting-viewer alert shown as a compact safe-area toast.
    /// </summary>
    public static class ClaimAlertOverlay
    {
        private static readonly Color PanelFill = new Color(0.050f, 0.054f, 0.052f, 0.96f);
        private static readonly Color HeaderFill = new Color(0.52f, 0.34f, 0.10f, 0.96f);
        private static readonly Color BrassColor = new Color(0.82f, 0.61f, 0.32f);
        private static readonly Color BrassDimColor = new Color(0.45f, 0.32f, 0.16f);
        private static readonly Color BrassSoftColor = new Color(0.82f, 0.61f, 0.32f, 0.28f);
        private static readonly Color TextColor = new Color(0.92f, 0.88f, 0.78f);
        private static readonly Color MutedColor = new Color(0.62f, 0.58f, 0.50f);
        private static readonly Color AccentFill = new Color(0.28f, 0.18f, 0.06f, 0.97f);

        private const float Width = 540f;
        private const float Height = 132f;
        private const float MinPad = 12f;
        private const float TopPad = 76f;
        private const int HighlightTicks = 420;

        private static string lastClaimKey;
        private static int highlightUntilTick;
        private static string lastWaitingUsername;
        private static int waitingHighlightUntilTick;
        private static int waitingSnoozedUntilTick;
        private static int lastBellTick;
        private const int WaitingSnoozeTicks = 7200; // ~2 min at 1x
        private const int BellCooldownTicks = 600;   // ~10s at 1x

        public static void NotifyClaimRequest(PendingClaim claim)
        {
            if (claim == null)
                return;

            string key = claim.username + ":" + claim.pawnId + ":" + claim.requestedTick;
            lastClaimKey = key;
            highlightUntilTick = (Find.TickManager?.TicksGame ?? 0) + HighlightTicks;
            PlayAlertSound();
        }

        public static void NotifyWaitingViewer(ViewerSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.username))
                return;
            if (!session.isConnected || session.HasPawn)
                return;

            lastWaitingUsername = session.username;
            waitingHighlightUntilTick = (Find.TickManager?.TicksGame ?? 0) + HighlightTicks;
            PlayAlertSound();
        }

        public static void Draw(OverlordGameComponent comp, ViewerManager viewers)
        {
            if (comp == null || viewers == null)
                return;

            var eventType = Event.current?.type ?? EventType.Repaint;
            bool drawVisuals = eventType == EventType.Repaint;
            bool handleInput =
                drawVisuals ||
                eventType == EventType.Layout ||
                eventType == EventType.MouseDown ||
                eventType == EventType.MouseUp;
            if (!handleInput)
                return;

            // FIFO: the viewer who asked first gets decided first.
            var claims = viewers.PendingClaims
                .Where(c => c != null)
                .OrderBy(c => c.requestedTick)
                .ToList();
            if (claims.Count > 0)
            {
                var claim = claims[0];
                var claimRect = GetToastRect();

                if (drawVisuals)
                    DrawPanel(claimRect, IsHighlighted(claim));
                DrawClaimContents(claimRect.ContractedBy(14f), comp, viewers, claim, claims.Count, drawVisuals);
                return;
            }

            // Streamer can snooze the (non-actionable) waiting toast; claim toasts
            // above are actionable and always show.
            if ((Find.TickManager?.TicksGame ?? 0) < waitingSnoozedUntilTick)
                return;

            var waiting = viewers.AllSessions
                .Where(s => s != null && s.isConnected && !s.HasPawn && viewers.GetPendingClaim(s.username) == null)
                .OrderByDescending(s => string.Equals(s.username, lastWaitingUsername, System.StringComparison.OrdinalIgnoreCase))
                .ThenBy(s => s.displayName ?? s.username ?? "")
                .ToList();
            if (waiting.Count == 0)
                return;

            var session = waiting[0];
            var rect = GetToastRect();

            if (drawVisuals)
                DrawPanel(rect, IsWaitingHighlighted(session));
            DrawWaitingContents(rect.ContractedBy(14f), session, waiting.Count, drawVisuals);
        }

        private static Rect GetToastRect()
        {
            float width = Mathf.Min(Width, UI.screenWidth - MinPad * 2f);
            float x = Mathf.Max(MinPad, (UI.screenWidth - width) * 0.5f);
            float y = Mathf.Clamp(TopPad, MinPad, Mathf.Max(MinPad, UI.screenHeight - Height - 140f));
            return new Rect(x, y, width, Height);
        }

        private static bool IsHighlighted(PendingClaim claim)
        {
            if (claim == null)
                return false;

            int now = Find.TickManager?.TicksGame ?? 0;
            string key = claim.username + ":" + claim.pawnId + ":" + claim.requestedTick;
            return key == lastClaimKey && now < highlightUntilTick;
        }

        private static bool IsWaitingHighlighted(ViewerSession session)
        {
            if (session == null || string.IsNullOrEmpty(lastWaitingUsername))
                return false;

            int now = Find.TickManager?.TicksGame ?? 0;
            return string.Equals(session.username, lastWaitingUsername, System.StringComparison.OrdinalIgnoreCase)
                && now < waitingHighlightUntilTick;
        }

        private static void DrawWaitingContents(Rect rect, ViewerSession session, int count, bool drawVisuals)
        {
            float openW = 118f;
            float buttonH = 30f;
            float buttonY = rect.yMax - buttonH;
            float buttonX = rect.xMax - openW;

            if (drawVisuals)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = BrassColor;
                Widgets.Label(new Rect(rect.x, rect.y, rect.width - 110f, 16f), "Waiting for assignment");
                if (count > 1)
                {
                    var oldAnchor = Text.Anchor;
                    Text.Anchor = TextAnchor.UpperRight;
                    Widgets.Label(new Rect(rect.xMax - 110f, rect.y, 110f, 16f), $"{count} waiting");
                    Text.Anchor = oldAnchor;
                }

                Text.Font = GameFont.Small;
                GUI.color = TextColor;
                string viewer = session.displayName ?? session.username ?? "viewer";
                Widgets.Label(new Rect(rect.x, rect.y + 26f, rect.width, 24f), $"{viewer} needs a colonist");

                Color oldColor = GUI.color;
                GUI.color = BrassSoftColor;
                Widgets.DrawLineHorizontal(rect.x, buttonY - 10f, rect.width);
                GUI.color = oldColor;
            }

            const float hideW = 78f;
            if (BrassButton(new Rect(buttonX, buttonY, openW, buttonH), "Open", drawVisuals, primary: true))
                OpenOverlordAssignments();

            if (BrassButton(new Rect(buttonX - hideW - 8f, buttonY, hideW, buttonH), "Hide", drawVisuals))
                waitingSnoozedUntilTick = (Find.TickManager?.TicksGame ?? 0) + WaitingSnoozeTicks;

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void DrawClaimContents(Rect rect, OverlordGameComponent comp, ViewerManager viewers, PendingClaim claim, int count, bool drawVisuals)
        {
            var pawnObj = viewers.FindPawnById(claim.pawnId);
            float gap = 8f;
            float approveW = 100f;
            float rejectW = 78f;
            float openW = 66f;
            float jumpW = pawnObj != null ? 62f : 0f;
            float totalW = approveW + gap + rejectW + gap + openW + (pawnObj != null ? gap + jumpW : 0f);
            float buttonH = 30f;
            float buttonY = rect.yMax - buttonH;
            float buttonX = rect.xMax - totalW;

            if (drawVisuals)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = BrassColor;
                Widgets.Label(new Rect(rect.x, rect.y, rect.width - 110f, 16f), "Claim request");
                if (count > 1)
                {
                    var oldAnchor = Text.Anchor;
                    Text.Anchor = TextAnchor.UpperRight;
                    Widgets.Label(new Rect(rect.xMax - 110f, rect.y, 110f, 16f), $"{count} pending");
                    Text.Anchor = oldAnchor;
                }

                Text.Font = GameFont.Small;
                GUI.color = TextColor;
                string viewer = claim.displayName ?? claim.username ?? "viewer";
                string pawn = claim.pawnName ?? "colonist";
                Widgets.Label(new Rect(rect.x, rect.y + 26f, rect.width, 24f), $"{viewer} wants {pawn}");

                Color oldColor = GUI.color;
                GUI.color = BrassSoftColor;
                Widgets.DrawLineHorizontal(rect.x, buttonY - 10f, rect.width);
                GUI.color = oldColor;
            }

            if (BrassButton(new Rect(buttonX, buttonY, approveW, buttonH), "Approve", drawVisuals, primary: true))
                ApproveClaim(comp, viewers, claim);

            if (BrassButton(new Rect(buttonX + approveW + gap, buttonY, rejectW, buttonH), "Reject", drawVisuals))
            {
                viewers.RejectClaim(claim.username);
                viewers.SendColonistList();
            }

            if (BrassButton(new Rect(buttonX + approveW + gap + rejectW + gap, buttonY, openW, buttonH), "Open", drawVisuals))
                OpenOverlordAssignments();

            if (pawnObj != null && BrassButton(new Rect(buttonX + approveW + gap + rejectW + gap + openW + gap, buttonY, jumpW, buttonH), "Jump", drawVisuals))
                CameraJumper.TryJumpAndSelect(pawnObj);

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void ApproveClaim(OverlordGameComponent comp, ViewerManager viewers, PendingClaim claim)
        {
            if (comp == null || viewers == null || claim == null)
                return;

            var pawn = viewers.FindPawnById(claim.pawnId);
            if (pawn == null)
            {
                viewers.RejectClaim(claim.username);
                viewers.SendColonistList();
                return;
            }

            if (viewers.AssignPawn(claim.username, pawn))
            {
                viewers.SendColonistList();
                comp.HandleRequestStatePublic(claim.username);
            }
        }

        private static void OpenOverlordAssignments()
        {
            var def = DefDatabase<MainButtonDef>.GetNamedSilentFail("Overlord_MainButton");
            if (def != null)
                Find.MainTabsRoot.SetCurrentTab(def);
            else
                Find.WindowStack.Add(new AssignmentDialog());
        }

        private static void PlayAlertSound()
        {
            // Cooldown: a claim burst (raid of new viewers) must not bell-storm
            // the stream audio.
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now - lastBellTick < BellCooldownTicks)
                return;
            lastBellTick = now;

            try
            {
                SoundDef.Named("TinyBell").PlayOneShotOnCamera();
            }
            catch
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
        }

        private static void DrawPanel(Rect rect, bool highlighted)
        {
            Widgets.DrawShadowAround(rect);
            Widgets.DrawBoxSolid(rect, highlighted ? AccentFill : PanelFill);
            DrawFineBorder(rect, highlighted ? BrassColor : BrassDimColor);
            DrawFineBorder(rect.ContractedBy(2f), BrassSoftColor);

            var headerRect = new Rect(rect.x, rect.y, 5f, rect.height);
            Widgets.DrawBoxSolid(headerRect, HeaderFill);
        }

        private static bool BrassButton(Rect rect, string label, bool drawVisuals, bool primary = false)
        {
            if (drawVisuals)
            {
                bool hover = Mouse.IsOver(rect);
                Color fill = primary
                    ? (hover ? new Color(0.55f, 0.39f, 0.15f, 0.98f) : new Color(0.40f, 0.27f, 0.09f, 0.97f))
                    : (hover ? new Color(0.34f, 0.24f, 0.12f, 0.96f) : new Color(0.19f, 0.14f, 0.08f, 0.94f));
                Widgets.DrawBoxSolid(rect, fill);
                DrawFineBorder(rect, hover ? BrassColor : BrassDimColor);

                var oldAnchor = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = TextColor;
                Widgets.Label(rect, label);
                Text.Anchor = oldAnchor;
                GUI.color = Color.white;
            }

            return Widgets.ButtonInvisible(rect);
        }

        private static void DrawFineBorder(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            Widgets.DrawLineHorizontal(rect.x, rect.y, rect.width);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
            Widgets.DrawLineVertical(rect.x, rect.y, rect.height);
            Widgets.DrawLineVertical(rect.xMax - 1f, rect.y, rect.height);
            GUI.color = old;
        }
    }
}
