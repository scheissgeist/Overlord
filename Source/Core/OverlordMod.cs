using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Overlord
{
    public class OverlordMod : Mod
    {
        public static OverlordMod Instance { get; private set; }
        public static OverlordSettings Settings { get; private set; }

        private Harmony harmony;

        public OverlordMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<OverlordSettings>();

            harmony = new Harmony("BroTeamPill.Overlord");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            LogUtil.Log("Mod loaded, Harmony patches applied");
        }

        public override string SettingsCategory()
        {
            return "Overlord";
        }

        private Vector2 settingsScroll;
        private bool showHostSecret;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.mapImageSize = Mathf.Clamp(Settings.mapImageSize, 360, 1440);
            Settings.mapImageQuality = Mathf.Clamp(Settings.mapImageQuality, 45, 88);
            Settings.mapUpdateInterval = Mathf.Clamp(Settings.mapUpdateInterval, 0.08f, 1f);

            // Scrollable area for all settings
            var viewRect = new Rect(0f, 0f, inRect.width - 20f, 1180f);
            Widgets.BeginScrollView(inRect, ref settingsScroll, viewRect);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("Live setup");
            listing.Gap(4f);
            if (listing.ButtonText("Use live-safe defaults"))
            {
                Settings.allowViewerEvents = false;
                Settings.allowViewerTacticalMap = false;
                Settings.enforceAreaRestrictions = true;
                Settings.mapImageSize = 720;
                Settings.mapImageQuality = 68;
                Settings.mapUpdateInterval = 0.10f;
                Settings.mirrorHostCameraToViewers = false;
                Settings.commandCooldownTicks = 20;
            }
            if (listing.ButtonText("Use sharp binary camera"))
            {
                Settings.mapImageSize = 1280;
                Settings.mapImageQuality = 84;
                Settings.mapUpdateInterval = 0.10f;
                Settings.mirrorHostCameraToViewers = false;
            }
            if (listing.ButtonText("Use low-lag camera"))
            {
                Settings.mapImageSize = 540;
                Settings.mapImageQuality = 58;
                Settings.mapUpdateInterval = 0.08f;
                Settings.allowViewerTacticalMap = false;
                Settings.mirrorHostCameraToViewers = false;
            }
            if (listing.ButtonText("Use smooth tactical map"))
            {
                Settings.allowViewerTacticalMap = true;
                Settings.mirrorHostCameraToViewers = false;
                Settings.mapImageSize = 720;
                Settings.mapImageQuality = 68;
                Settings.mapUpdateInterval = 0.10f;
            }
            listing.Gap(6f);
            listing.Label("Live-safe keeps viewer events off, tactical map data private, area limits on, and camera traffic moderate. Sharp binary camera is for one or a few viewers. Smooth tactical map is the recommended low-lag control path (browser tilemap).");
            listing.GapLine();

            listing.CheckboxLabeled("Pause live map capture (troubleshooting)", ref Settings.disableMapCapture,
                "Stops all off-screen map rendering for viewers. Viewers keep full pawn control; they just lose the live map picture. Use to isolate graphics problems: if an issue disappears with this on, it's in the capture pipeline.");
            listing.GapLine();

            listing.Label("Relay Server URL (leave blank for local-only mode):");
            Settings.relayUrl = listing.TextEntry(Settings.relayUrl);

            listing.Gap(4f);
            listing.Label("Host secret (must match HOST_SECRET on relay server):");
            var secretRect = listing.GetRect(28f);
            float btnWidth = 60f;
            var fieldRect = new Rect(secretRect.x, secretRect.y, secretRect.width - btnWidth - 4f, secretRect.height);
            var toggleRect = new Rect(fieldRect.xMax + 4f, secretRect.y, btnWidth, secretRect.height);
            if (showHostSecret)
            {
                Settings.hostSecret = Widgets.TextField(fieldRect, Settings.hostSecret);
            }
            else
            {
                string masked = Settings.hostSecret.Length > 0 ? new string('*', System.Math.Min(Settings.hostSecret.Length, 32)) : "";
                Widgets.Label(fieldRect, masked);
            }
            if (Widgets.ButtonText(toggleRect, showHostSecret ? "Hide" : "Show"))
                showHostSecret = !showHostSecret;

            listing.Gap(6f);
            listing.Label("Local server port:");
            string portStr = listing.TextEntry(Settings.localPort.ToString());
            if (int.TryParse(portStr, out int parsedPort) && parsedPort >= 1024 && parsedPort <= 65535)
                Settings.localPort = parsedPort;

            listing.GapLine();
            listing.Label("Live Camera");
            listing.Gap(4f);

            listing.Label($"Image size: {Settings.mapImageSize}px (higher is sharper, heavier)");
            Settings.mapImageSize = (int)listing.Slider(Settings.mapImageSize, 360, 1440);

            listing.Label($"JPEG quality: {Settings.mapImageQuality}% (higher sends more data)");
            Settings.mapImageQuality = (int)listing.Slider(Settings.mapImageQuality, 45, 88);

            listing.Label($"Frame interval: {Settings.mapUpdateInterval:F2}s (~{(1f / Mathf.Max(Settings.mapUpdateInterval, 0.01f)):F0} FPS target; lower is smoother, heavier)");
            Settings.mapUpdateInterval = listing.Slider(Settings.mapUpdateInterval, 0.08f, 1f);
            listing.CheckboxLabeled("Fallback: mirror host world camera to viewers", ref Settings.mirrorHostCameraToViewers);
            listing.CheckboxLabeled("Expose full tactical map data to viewers (recommended smooth / low-lag path)", ref Settings.allowViewerTacticalMap);
            listing.CheckboxLabeled("Expose colony resource readout to assigned viewers (stock totals)", ref Settings.allowViewerResourceReadout);

            listing.GapLine();
            listing.Label("Default Viewer Permissions");
            listing.Gap(4f);

            listing.CheckboxLabeled("Allow draft/undraft", ref Settings.allowDraft);
            listing.CheckboxLabeled("Allow move", ref Settings.allowMove);
            listing.CheckboxLabeled("Allow attack", ref Settings.allowAttack);
            listing.CheckboxLabeled("Allow work priorities", ref Settings.allowWork);
            listing.CheckboxLabeled("Allow schedule", ref Settings.allowSchedule);
            listing.CheckboxLabeled("Allow outfit changes", ref Settings.allowOutfit);
            listing.CheckboxLabeled("Allow drug policy", ref Settings.allowDrugPolicy);
            listing.CheckboxLabeled("Allow food policy", ref Settings.allowFoodPolicy);
            listing.CheckboxLabeled("Allow area restriction", ref Settings.allowArea);
            listing.CheckboxLabeled("Allow equip/drop", ref Settings.allowEquip);
            listing.CheckboxLabeled("Allow appearance changes", ref Settings.allowAppearance);
            listing.CheckboxLabeled("Allow viewer-triggered events", ref Settings.allowViewerEvents);

            listing.GapLine();
            listing.Label("Command Safety");
            listing.Gap(4f);

            listing.Label($"Command cooldown: {Settings.commandCooldownTicks / 60f:F2}s per command");
            Settings.commandCooldownTicks = (int)listing.Slider(Settings.commandCooldownTicks, 0, 300);
            listing.CheckboxLabeled("Enforce pawn area restrictions for viewer commands", ref Settings.enforceAreaRestrictions);

            listing.GapLine();
            listing.Label("Respawn");
            listing.Gap(4f);

            listing.Label($"Starting tickets: {Settings.startTickets}");
            Settings.startTickets = (int)listing.Slider(Settings.startTickets, 0, 10);

            listing.Label($"Max tickets: {Settings.maxTickets}");
            Settings.maxTickets = (int)listing.Slider(Settings.maxTickets, 1, 20);

            string earnLabel = Settings.ticketEarnIntervalTicks <= 0
                ? "Ticket earn interval: disabled"
                : $"Ticket earn interval: {Settings.ticketEarnIntervalTicks / 60f / 60f:F1} min";
            listing.Label(earnLabel);
            Settings.ticketEarnIntervalTicks = (int)listing.Slider(Settings.ticketEarnIntervalTicks, 0, 720000);

            listing.Label($"Respawn cooldown: {Settings.respawnCooldownTicks / 60f:F0}s");
            Settings.respawnCooldownTicks = (int)listing.Slider(Settings.respawnCooldownTicks, 0, 15000);

            listing.End();
            Widgets.EndScrollView();
        }
    }
}
