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

        /// <summary>
        /// Cryptographically-random host secret. Hand-invented secrets are the common
        /// failure here — people pick something short, or mistype it into one of the two
        /// places it must match.
        /// </summary>
        private static string GenerateHostSecret()
        {
            const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[32];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            var sb = new System.Text.StringBuilder(bytes.Length);
            foreach (byte b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        /// <summary>
        /// Live setup status at the top of the settings window. This exists because the
        /// relay URL and host secret were previously bare text fields that reported
        /// NOTHING — a typo produced silence, and the streamer concluded the mod was
        /// broken. Reads state that already existed (RelayClient.IsConnected,
        /// EmbeddedWebServer.Instance) rather than probing anything.
        /// </summary>
        private static void DrawConnectionStatus(Listing_Standard listing)
        {
            bool relayMode = !string.IsNullOrEmpty(Settings.relayUrl);
            var comp = OverlordGameComponent.Instance;

            Color prev = GUI.color;

            if (!relayMode)
            {
                // ── Friends / local mode ──────────────────────────────────────────
                var server = EmbeddedWebServer.Instance;
                bool running = server != null && server.IsRunning;

                if (running)
                {
                    string lan = EmbeddedWebServer.GetLanAddress();
                    if (server.BoundAllInterfaces && !string.IsNullOrEmpty(lan))
                    {
                        GUI.color = Color.green;
                        listing.Label($"Friends mode — ready. Send friends:  http://{lan}:{Settings.localPort}");
                        GUI.color = prev;
                        listing.Label("    They open that in a browser, type any name, and claim a colonist. No Twitch, no relay.");
                    }
                    else if (server.BoundAllInterfaces)
                    {
                        GUI.color = Color.green;
                        listing.Label($"Friends mode — running on port {Settings.localPort} (LAN address unknown).");
                        GUI.color = prev;
                        listing.Label("    Find your local IP (Windows: run 'ipconfig') and send friends http://THAT-IP:" + Settings.localPort);
                    }
                    else
                    {
                        GUI.color = Color.yellow;
                        listing.Label($"Friends mode — THIS MACHINE ONLY (http://localhost:{Settings.localPort}).");
                        GUI.color = prev;
                        listing.Label("    RimWorld could not bind all network interfaces, so friends cannot connect. Run RimWorld as administrator to fix.");
                    }
                }
                else
                {
                    GUI.color = Color.gray;
                    listing.Label("Friends mode — not started yet.");
                    GUI.color = prev;
                    listing.Label("    The local server starts when you load or begin a save. Come back here after loading to get the link for friends.");
                }
                return;
            }

            // ── Relay / Twitch mode ───────────────────────────────────────────────
            if (comp?.Relay == null)
            {
                GUI.color = Color.gray;
                listing.Label("Relay mode — not started yet.");
                GUI.color = prev;
                listing.Label("    The host connects when you load or begin a save.");
            }
            else if (comp.Relay.IsConnected)
            {
                GUI.color = Color.green;
                listing.Label("Relay mode — connected. Send viewers your relay's public URL.");
                GUI.color = prev;
            }
            else
            {
                GUI.color = Color.red;
                listing.Label("Relay mode — NOT connected.");
                GUI.color = prev;
                listing.Label("    Check: the URL below is right, the host secret matches HOST_SECRET on the relay, and the relay is running.");
            }

            if (string.IsNullOrEmpty(Settings.hostSecret))
            {
                GUI.color = Color.yellow;
                listing.Label("    No host secret set — the relay will reject this host. Use Generate below.");
                GUI.color = prev;
            }
        }

        /// <summary>
        /// "Test connection" — asks the relay directly, from the main menu, before any
        /// save exists. DrawConnectionStatus can only report the live host socket, so
        /// during first-time setup it says "not started yet" no matter how wrong the
        /// URL or secret are. This is the check that separates the three setup
        /// failures — unreachable, wrong secret, no Twitch client id — which otherwise
        /// all present as the same silence.
        /// </summary>
        private static void DrawRelayTest(Listing_Standard listing)
        {
            if (string.IsNullOrEmpty(Settings.relayUrl)) return;

            listing.Gap(4f);
            var row = listing.GetRect(28f);
            var buttonRect = new Rect(row.x, row.y, 160f, row.height);

            if (RelayProbe.IsRunning)
            {
                Widgets.ButtonText(buttonRect, "Testing...", true, true, false);
            }
            else if (Widgets.ButtonText(buttonRect, "Test connection"))
            {
                RelayProbe.Start(Settings.relayUrl, Settings.hostSecret);
            }

            Color prev = GUI.color;
            switch (RelayProbe.State)
            {
                case RelayProbe.Status.Idle:
                    GUI.color = Color.gray;
                    listing.Label("    Checks the relay without loading a save: is the address right, is the relay up, does it accept this host secret, can viewers log in with Twitch.");
                    GUI.color = prev;
                    break;

                case RelayProbe.Status.Running:
                    GUI.color = Color.gray;
                    listing.Label("    " + RelayProbe.Headline);
                    GUI.color = prev;
                    break;

                default:
                    GUI.color = RelayProbe.State == RelayProbe.Status.Ok ? Color.green
                              : RelayProbe.State == RelayProbe.Status.Warn ? Color.yellow
                              : Color.red;
                    listing.Label("    " + RelayProbe.Headline);
                    GUI.color = prev;
                    if (!string.IsNullOrEmpty(RelayProbe.Detail))
                        listing.Label("        " + RelayProbe.Detail);
                    break;
            }
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
            listing.Label($"Capture bisect level: {Settings.captureBisectLevel}  (0 = normal. Troubleshooting ladder: 1 = camera only, 2 = +terrain, 3 = +pawns, 4 = +weather. Raise until the bug appears — that level is the culprit.)");
            Settings.captureBisectLevel = (int)listing.Slider(Settings.captureBisectLevel, 0f, 4f);
            listing.GapLine();

            DrawConnectionStatus(listing);
            listing.GapLine();

            listing.Label("Relay Server URL (leave blank to play with friends — no Twitch, no stream):");
            listing.Label($"    Blank = the mod serves the viewer UI itself on port {Settings.localPort}. Friends open http://<your-LAN-IP>:{Settings.localPort} in a browser, type any name, and claim a colonist. No Twitch app, no relay, no host secret. Set a URL below only for Twitch/stream mode.");
            string urlBefore = Settings.relayUrl;
            Settings.relayUrl = listing.TextEntry(Settings.relayUrl);
            if (Settings.relayUrl != urlBefore) RelayProbe.Reset();

            listing.Gap(4f);
            listing.Label("Host secret (must match HOST_SECRET on relay server):");
            var secretRect = listing.GetRect(28f);
            float btnWidth = 60f;
            float genWidth = 80f;
            var fieldRect = new Rect(secretRect.x, secretRect.y, secretRect.width - btnWidth - genWidth - 8f, secretRect.height);
            var toggleRect = new Rect(fieldRect.xMax + 4f, secretRect.y, btnWidth, secretRect.height);
            var genRect = new Rect(toggleRect.xMax + 4f, secretRect.y, genWidth, secretRect.height);
            if (showHostSecret)
            {
                string secretBefore = Settings.hostSecret;
                Settings.hostSecret = Widgets.TextField(fieldRect, Settings.hostSecret);
                if (Settings.hostSecret != secretBefore) RelayProbe.Reset();
            }
            else
            {
                string masked = Settings.hostSecret.Length > 0 ? new string('*', System.Math.Min(Settings.hostSecret.Length, 32)) : "";
                Widgets.Label(fieldRect, masked);
            }
            if (Widgets.ButtonText(toggleRect, showHostSecret ? "Hide" : "Show"))
                showHostSecret = !showHostSecret;
            if (Widgets.ButtonText(genRect, "Generate"))
            {
                Settings.hostSecret = GenerateHostSecret();
                showHostSecret = true;
                RelayProbe.Reset();
            }
            listing.Label("    Must match HOST_SECRET on your relay exactly. Generate makes a strong one — copy it to the relay.");

            DrawRelayTest(listing);

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
            listing.CheckboxLabeled("Render viewer frames fully lit (ignore night darkness)", ref Settings.unlitViewerFrames,
                "Viewers see the map without RimWorld's darkness overlay, so a night raid is actually visible in the browser. Your own game view keeps normal lighting.");
            listing.CheckboxLabeled("Also brighten dark viewer frames (eclipse, fallout)", ref Settings.brightenDarkFrames,
                "Secondary adaptive lift for frames that are still dark after the lighting fix. Bright frames are left alone. Your own game view is never affected.");
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
            listing.CheckboxLabeled("Auto-reconnect returning viewers to their own colonist",
                ref Settings.autoReconnectPreviousPawn,
                "When a viewer rejoins, hand them back the colonist they already had — no approval prompt. Only restores pairings you previously approved; never gives anyone a new colonist, and never takes one that someone else holds.");

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
