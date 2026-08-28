using System.Reflection;
using HarmonyLib;
using RimWorld;
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

            // RelayJoin copies the viewer link the moment a join succeeds; it reaches
            // the clipboard through here so it keeps compiling without UnityEngine.
            RelayJoin.CopyLink = value => GUIUtility.systemCopyBuffer = value;

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
            bool relayMode = Settings.HasRelayUrl;
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
                        listing.Label("    They open that in a browser, type any name, and claim a colonist. No Twitch, no signup.");
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
                listing.Label("Online play — not started yet.");
                GUI.color = prev;
                listing.Label("    The host connects when you load or begin a save.");
            }
            else if (comp.Relay.IsConnected)
            {
                GUI.color = Color.green;
                listing.Label("Online play — connected. Anyone with your link can join.");
                GUI.color = prev;
            }
            else
            {
                GUI.color = Color.red;
                listing.Label("Online play — NOT connected.");
                GUI.color = prev;
                listing.Label(string.IsNullOrEmpty(Settings.hostKey)
                    ? "    Check: the address below is right, your host secret matches the one on your server, and your server is running."
                    : "    Press Test connection below — it will say what is wrong. Usually the server is asleep or has been reset.");
            }

            // Only the person running their OWN relay needs a secret. Someone who joined
            // a relay holds a key the relay issued and never sees a secret at all - but
            // this warned them anyway, in yellow, the instant they picked Twitch mode,
            // and told them to press a Generate button that is collapsed out of sight on
            // that path. That is what sent a tester hunting for a token to generate.
            if (string.IsNullOrEmpty(Settings.hostSecret) && string.IsNullOrEmpty(Settings.hostKey) && showRunMyOwn)
            {
                GUI.color = Color.yellow;
                listing.Label("    No host secret set — your own server will reject this game. Use Generate below.");
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
            if (!Settings.HasRelayUrl) return;

            listing.Gap(4f);
            var row = listing.GetRect(28f);
            var buttonRect = new Rect(row.x, row.y, 160f, row.height);

            if (RelayProbe.IsRunning)
            {
                Widgets.ButtonText(buttonRect, "Testing...", true, true, false);
            }
            else if (Widgets.ButtonText(buttonRect, "Test connection"))
            {
                RelayProbe.Start(Settings.relayUrl, Settings.hostSecret, Settings.hostKey);
            }

            DrawProbeResult(listing, "    Checks it without loading a save: is the address right, is it running, and can people get in.");
        }

        /// <summary>
        /// One renderer for the probe result. There were two, and the copy used on the
        /// joined-relay screen silently dropped Detail — which is the half that says
        /// what to DO ("press Join again", "that is for whoever runs it to fix").
        /// </summary>
        private static void DrawProbeResult(Listing_Standard listing, string idleHint)
        {
            Color prev = GUI.color;
            switch (RelayProbe.State)
            {
                case RelayProbe.Status.Idle:
                    if (!string.IsNullOrEmpty(idleHint))
                    {
                        GUI.color = Color.gray;
                        listing.Label(idleHint);
                        GUI.color = prev;
                    }
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

        // UI-only. The runtime mode is still decided by whether relayUrl is empty
        // (OverlordGameComponent), so this only chooses which panel is on screen —
        // and the relay panel says so out loud while the URL is still blank.
        private static bool uiTwitchMode;
        private static bool uiModeInitialized;
        private static string stashedRelayUrl = "";

        /// <summary>
        /// Opens the beginner online path with the project relay already filled in.
        /// The field stays editable, so replacing it with a private/self-hosted relay
        /// changes only this player's destination and keeps the same registration path.
        /// </summary>
        internal static void OpenOnlineSetup()
        {
            if (!Settings.HasRelayUrl)
                Settings.relayUrl = OverlordSettings.OfficialRelayUrl;
            uiTwitchMode = true;
            uiModeInitialized = true;
            RelayProbe.Reset();
            RelayJoin.Reset();
            Settings.Write();
            Find.WindowStack.Add(new Dialog_ModSettings(Instance));
        }

        /// <summary>
        /// The first thing in the settings window, because it is the first thing a new
        /// streamer needs. This window used to open on four camera presets and a
        /// capture-bisect debug ladder; "how does anyone connect to me" was answered by
        /// an empty text box further down whose *blankness* was itself the mode switch.
        /// Nobody discovers a mode encoded as an empty field.
        /// </summary>
        private static void DrawSetupSection(Listing_Standard listing)
        {
            if (!uiModeInitialized)
            {
                uiTwitchMode = Settings.HasRelayUrl;
                uiModeInitialized = true;
            }

            Text.Font = GameFont.Medium;
            listing.Label("Setup");
            Text.Font = GameFont.Small;
            listing.Gap(2f);

            var modeRect = listing.GetRect(34f);
            float half = (modeRect.width - 8f) / 2f;
            var friendsRect = new Rect(modeRect.x, modeRect.y, half, modeRect.height);
            var twitchRect = new Rect(friendsRect.xMax + 8f, modeRect.y, half, modeRect.height);

            Color prev = GUI.color;
            Color off = new Color(0.6f, 0.6f, 0.6f);

            GUI.color = uiTwitchMode ? off : Color.green;
            if (Widgets.ButtonText(friendsRect, "People in my house"))
            {
                if (Settings.HasRelayUrl) stashedRelayUrl = Settings.relayUrl;
                Settings.relayUrl = "";
                Settings.setupChoiceMade = true;
                uiTwitchMode = false;
                RelayProbe.Reset();
            }
            GUI.color = uiTwitchMode ? Color.green : off;
            if (Widgets.ButtonText(twitchRect, "People anywhere / my stream"))
            {
                if (!Settings.HasRelayUrl)
                    Settings.relayUrl = string.IsNullOrWhiteSpace(stashedRelayUrl)
                        ? OverlordSettings.OfficialRelayUrl
                        : stashedRelayUrl;
                uiTwitchMode = true;
                RelayProbe.Reset();
            }
            GUI.color = prev;

            listing.Gap(2f);
            // "Free tier, ~20 minutes, once" meant SETUP takes twenty minutes. Sean read
            // it as a twenty-minute usage limit and asked what was being restricted -
            // which is the only reading that matters. Never put a bare duration next to
            // the words "free tier"; say what the number measures, or drop it.
            listing.Label(uiTwitchMode
                ? "    For people outside your home network. The Overlord relay is filled in for you; replace it only if you want to use another relay."
                : "    Your game hands out a link that works on your home network. Nothing to sign up for, nothing to install, no accounts.");
            listing.Gap(6f);

            DrawConnectionStatus(listing);
            listing.Gap(8f);

            // RimWorld stops drawing the rest of a settings window when the mod throws,
            // so a single bad line renders as an EMPTY panel with no clue why - and an
            // empty panel here means nobody can find the Join button at all. Show the
            // failure instead of swallowing it.
            try
            {
                if (uiTwitchMode) DrawRelaySetup(listing);
                else DrawFriendsSetup(listing);
            }
            catch (System.Exception e)
            {
                Color err = GUI.color;
                GUI.color = Color.red;
                listing.Label("Overlord could not draw the setup panel: " + e.GetType().Name + " - " + e.Message);
                GUI.color = err;
                listing.Label("    This is a bug in the mod. The rest of the settings below still work.");
                LogUtil.Error("Settings draw failed: " + e);
            }

            DrawCopiedNote(listing);
        }

        private static void StepLabel(Listing_Standard listing, bool done, string text)
        {
            Color prev = GUI.color;
            GUI.color = done ? Color.green : new Color(0.78f, 0.78f, 0.78f);
            listing.Label(text);
            GUI.color = prev;
        }

        private static void DrawFriendsSetup(Listing_Standard listing)
        {
            var server = EmbeddedWebServer.Instance;
            bool running = server != null && server.IsRunning;
            string lan = running ? EmbeddedWebServer.GetLanAddress() : null;
            bool haveLink = running && server.BoundAllInterfaces && !string.IsNullOrEmpty(lan);
            string link = haveLink ? $"http://{lan}:{Settings.localPort}" : null;

            StepLabel(listing, true, "1.  Nothing to deploy and no accounts to make. Your game is the server.");
            if (!running)
            {
                Color c = GUI.color;
                GUI.color = Color.yellow;
                listing.Label("NEXT: load or start a save. The link below does not exist until a game is open — that is normal, not a fault.");
                GUI.color = c;
            }
            StepLabel(listing, running, "2.  Load or start a save." + (running ? "" : "   The page only exists while a game is open."));

            StepLabel(listing, haveLink, "3.  Send your friends this link:");
            if (haveLink)
            {
                var row = listing.GetRect(28f);
                var copyRect = new Rect(row.xMax - 70f, row.y, 70f, row.height);
                var linkRect = new Rect(row.x, row.y, row.width - 78f, row.height);
                Widgets.TextField(linkRect, link);
                if (Widgets.ButtonText(copyRect, "Copy")) CopyToClipboard(link, "the link");
            }
            else if (!running)
            {
                listing.Label("        The link appears here once a save is loaded — come back then.");
            }
            else
            {
                Color prev = GUI.color;
                GUI.color = Color.yellow;
                listing.Label($"        Only this machine can connect (http://localhost:{Settings.localPort}). RimWorld could not listen on your network — close it, right-click, Run as administrator.");
                GUI.color = prev;
            }

            StepLabel(listing, false, "4.  They open it, type any name, and claim a colonist. No login.");

            listing.Gap(6f);
            listing.Label("Port:");
            string portStr = listing.TextEntry(Settings.localPort.ToString());
            if (int.TryParse(portStr, out int parsedPort) && parsedPort >= 1024 && parsedPort <= 65535)
                Settings.localPort = parsedPort;

            listing.Gap(4f);
            listing.Label("Friends outside your house cannot use that link — it is a local address. Put you both on a tunnel (Tailscale is free) and the same link works, or forward the port on your router.");
            if (listing.ButtonText("Open the hosting guide"))
                Application.OpenURL("https://github.com/scheissgeist/Overlord/blob/master/docs/HOSTING_GUIDE.md");
        }

        // Two ways to be in relay mode, and they are wildly different amounts of work:
        // use the project relay (already filled in, press a button), or RUN one yourself
        // (an account, env vars, a Twitch app). The first is what almost everyone wants,
        // so it goes first and the second is folded away behind a toggle.
        private static bool showRunMyOwn;
        private static string inviteCode = "";
        private static string joinLabel = "";

        private static void DrawRelaySetup(Listing_Standard listing)
        {
            if (!string.IsNullOrEmpty(Settings.hostKey))
            {
                DrawJoinedRelay(listing);
                return;
            }

            StepLabel(listing, false, "Use the Overlord relay below, or replace it with your own address, then press Set me up.");
            // Spelled out because a tester went off and tried to generate a Twitch oauth
            // token to host with. Nothing in Overlord ever asks a host for one - that is
            // Twitch Toolkit's setup, a different and optional mod - but with nothing on
            // screen saying so, Toolkit's demand for a token looks like Overlord's.
            listing.Label("        Hosting needs NO Twitch account, NO token, and NO server of your own. If something is asking you for an oauth token, that is Twitch Toolkit (a separate, optional mod) - not this.");
            listing.Label("        Only your VIEWERS sign in with Twitch, and they just click a button on the link you send them.");

            string urlBefore = Settings.relayUrl;
            // Trimmed on the way in: a URL pasted with padding used to pass the probe
            // (which trims) and fail the real connection (which did not).
            Settings.relayUrl = listing.TextEntry(Settings.relayUrl).Trim();
            if (Settings.relayUrl != urlBefore) { RelayProbe.Reset(); RelayJoin.Reset(); }

            listing.Gap(4f);
            var row = listing.GetRect(30f);
            var joinRect = new Rect(row.x, row.y, 150f, row.height);
            // Clamped: on a narrow settings window row.width - 320f goes NEGATIVE, which
            // produces inside-out Rects that swallow their own content.
            float nameW = Mathf.Clamp(row.width - 320f, 60f, 220f);
            var nameRect = new Rect(joinRect.xMax + 8f, row.y, nameW, row.height);
            float inviteW = Mathf.Max(80f, row.xMax - nameRect.xMax - 8f);
            var inviteRect = new Rect(nameRect.xMax + 8f, row.y, inviteW, row.height);

            if (RelayJoin.IsRunning)
            {
                Widgets.ButtonText(joinRect, "Setting up...", true, true, false);
            }
            else if (Widgets.ButtonText(joinRect, "Set me up"))
            {
                RelayJoin.Start(Settings.relayUrl, joinLabel, inviteCode);
            }
            joinLabel = Widgets.TextField(nameRect, joinLabel);
            inviteCode = Widgets.TextField(inviteRect, inviteCode);
            // Both boxes are optional and were indistinguishable from the required one.
            // Say which is which where they are, not in a sentence underneath.
            Color hint = GUI.color;
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(nameRect.x + 2f, nameRect.yMax + 1f, nameRect.width, 18f), "name (optional)");
            Widgets.Label(new Rect(inviteRect.x + 2f, inviteRect.yMax + 1f, inviteRect.width, 18f), "invite code (only if given one)");
            GUI.color = hint;
            listing.Gap(18f);
            listing.Label("        The Overlord address is already filled in. Change it only for a private/self-hosted relay; leave the optional boxes empty unless told otherwise.");

            Color prev = GUI.color;
            switch (RelayJoin.State)
            {
                case RelayJoin.Status.Running:
                    GUI.color = Color.gray;
                    listing.Label("    " + RelayJoin.Headline);
                    GUI.color = prev;
                    break;
                case RelayJoin.Status.Ok:
                case RelayJoin.Status.Fail:
                    GUI.color = RelayJoin.State == RelayJoin.Status.Ok ? Color.green : Color.red;
                    listing.Label("    " + RelayJoin.Headline);
                    GUI.color = prev;
                    if (!string.IsNullOrEmpty(RelayJoin.Detail)) listing.Label("        " + RelayJoin.Detail);
                    break;
            }

            listing.Gap(8f);
            if (listing.ButtonText(showRunMyOwn ? "Hide advanced setup" : "Advanced: I want to run the server myself"))
                showRunMyOwn = !showRunMyOwn;
            if (showRunMyOwn) DrawRunMyOwnRelay(listing);
        }

        /// <summary>
        /// After joining: the only thing that matters is the link to hand out, so it is
        /// the only thing shown, with a Copy button. The host key is deliberately never
        /// displayed — nobody has any reason to see it, and showing a credential invites
        /// someone to paste it somewhere.
        /// </summary>
        private static void DrawJoinedRelay(Listing_Standard listing)
        {
            var comp = OverlordGameComponent.Instance;
            bool live = comp?.Relay != null && comp.Relay.IsConnected;
            bool inGame = comp != null;

            StepLabel(listing, true, "You are set up on " + Settings.relayUrl);

            // The single most common stall: everything is configured, nothing is
            // happening, and no screen says the missing step is "load a save". The mod
            // does not connect until a game exists, so from the main menu this is the
            // only instruction that matters.
            Color prev = GUI.color;
            if (live)
            {
                GUI.color = Color.green;
                listing.Label("NEXT: nothing — you are live. Send the link below to anyone who wants to play.");
            }
            else if (!inGame)
            {
                GUI.color = Color.yellow;
                listing.Label("NEXT: load or start a save. Nothing connects from the main menu — that is normal, not a fault.");
            }
            else if (comp.Relay == null)
            {
                GUI.color = Color.yellow;
                listing.Label("NEXT: return to the main menu and load this save again. The current save started in friends mode before online setup finished.");
            }
            else
            {
                GUI.color = Color.yellow;
                listing.Label("NEXT: connecting... if this does not turn green in a few seconds, press Test connection below.");
            }
            GUI.color = prev;

            listing.Gap(4f);
            listing.Label("Send people this link:");

            var row = listing.GetRect(28f);
            var copyRect = new Rect(row.xMax - 70f, row.y, 70f, row.height);
            var linkRect = new Rect(row.x, row.y, row.width - 78f, row.height);
            Widgets.TextField(linkRect, Settings.viewerUrl);
            if (Widgets.ButtonText(copyRect, "Copy") && !string.IsNullOrEmpty(Settings.viewerUrl))
                CopyToClipboard(Settings.viewerUrl, "your viewer link");

            listing.Label("        This link is YOURS — it opens your colony, not your friend's. Paste it in your chat, your panels, wherever.");

            listing.Gap(6f);
            var btnRow = listing.GetRect(28f);
            var testRect = new Rect(btnRow.x, btnRow.y, 150f, btnRow.height);
            var openRect = new Rect(testRect.xMax + 8f, btnRow.y, 150f, btnRow.height);
            var leaveRect = new Rect(openRect.xMax + 8f, btnRow.y, 150f, btnRow.height);

            if (RelayProbe.IsRunning) Widgets.ButtonText(testRect, "Testing...", true, true, false);
            else if (Widgets.ButtonText(testRect, "Test connection")) RelayProbe.Start(Settings.relayUrl, Settings.hostSecret, Settings.hostKey);
            if (Widgets.ButtonText(openRect, "Open the link") && !string.IsNullOrEmpty(Settings.viewerUrl))
                Application.OpenURL(Settings.viewerUrl);
            if (Widgets.ButtonText(leaveRect, "Start over"))
            {
                Settings.hostKey = "";
                Settings.roomId = "";
                Settings.viewerUrl = "";
                Settings.Write();
                RelayJoin.Reset();
                RelayProbe.Reset();
            }

            DrawProbeResult(listing, "");
        }

        /// <summary>The operator path: you deployed the relay, so you hold HOST_SECRET.</summary>
        private static void DrawRunMyOwnRelay(Listing_Standard listing)
        {
            bool haveSecret = !string.IsNullOrEmpty(Settings.hostSecret);

            listing.Gap(4f);
            StepLabel(listing, haveSecret, "1.  Make a host secret. This is the password your game uses to claim your relay.");
            var secretRect = listing.GetRect(28f);
            const float showW = 60f, genW = 80f, copyW = 70f;
            var fieldRect = new Rect(secretRect.x, secretRect.y, Mathf.Max(80f, secretRect.width - showW - genW - copyW - 12f), secretRect.height);
            var toggleRect = new Rect(fieldRect.xMax + 4f, secretRect.y, showW, secretRect.height);
            var genRect = new Rect(toggleRect.xMax + 4f, secretRect.y, genW, secretRect.height);
            var copyRect = new Rect(genRect.xMax + 4f, secretRect.y, copyW, secretRect.height);

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
            if (Widgets.ButtonText(toggleRect, showHostSecret ? "Hide" : "Show")) showHostSecret = !showHostSecret;
            if (Widgets.ButtonText(genRect, "Generate"))
            {
                Settings.hostSecret = GenerateHostSecret();
                showHostSecret = true;
                RelayProbe.Reset();
            }
            if (Widgets.ButtonText(copyRect, "Copy") && haveSecret)
                CopyToClipboard(Settings.hostSecret, "the host secret");
            listing.Label("        Generate, then Copy. Hand-typing this is where setup usually breaks - it has to match on both sides exactly.");

            listing.Gap(4f);
            StepLabel(listing, Settings.HasRelayUrl, "2.  Deploy a relay under your own account. It asks for these:");
            listing.Label("        HOST_SECRET - the value you just copied.");
            listing.Label("        TWITCH_CLIENT_ID - from the Twitch developer console. It goes on the relay, not here.");
            listing.Label("        ALLOW_GUEST_LOGIN=1 instead, with TWITCH_CLIENT_ID empty, lets viewers join by typing a name - no Twitch app at all. Anyone with the link can then join as anyone, so it is for a test or a private group.");
            listing.Label("        OPEN_HOSTING=1 lets OTHER streamers press Join and host on your relay. Their viewers cost your bandwidth, so MAX_ROOMS caps how many games at once.");
            listing.Label("        After deployment, replace the official relay address above with your server's address.");
            var btnRow = listing.GetRect(30f);
            float third = (btnRow.width - 16f) / 3f;
            if (Widgets.ButtonText(new Rect(btnRow.x, btnRow.y, third, btnRow.height), "Hosting guide"))
                Application.OpenURL("https://github.com/scheissgeist/Overlord/blob/master/docs/HOSTING_GUIDE.md");
            if (Widgets.ButtonText(new Rect(btnRow.x + third + 8f, btnRow.y, third, btnRow.height), "Deploy a relay"))
                Application.OpenURL("https://render.com/deploy?repo=https://github.com/scheissgeist/Overlord");
            if (Widgets.ButtonText(new Rect(btnRow.x + 2f * (third + 8f), btnRow.y, third, btnRow.height), "Twitch console"))
                Application.OpenURL("https://dev.twitch.tv/console/apps");

            listing.Gap(6f);
            StepLabel(listing, RelayProbe.State == RelayProbe.Status.Ok, "3.  Check it before you go live:");
            DrawRelayTest(listing);
        }

        public override string SettingsCategory()
        {
            return "Overlord";
        }

        private Vector2 settingsScroll;
        private static bool showHostSecret;
        private float settingsContentHeight = 1600f;

        // Inline "Copied." confirmation — Messages.Message is not safe to fire from the
        // main menu, where most of setup happens.
        private static string copiedLabel;
        private static float copiedAt;

        private static void CopyToClipboard(string value, string what)
        {
            GUIUtility.systemCopyBuffer = value;
            copiedLabel = what;
            copiedAt = Time.realtimeSinceStartup;
        }

        private static void DrawCopiedNote(Listing_Standard listing)
        {
            if (copiedLabel == null || Time.realtimeSinceStartup - copiedAt > 4f) return;
            Color prev = GUI.color;
            GUI.color = Color.green;
            listing.Label("    Copied " + copiedLabel + " to the clipboard.");
            GUI.color = prev;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.mapImageSize = Mathf.Clamp(Settings.mapImageSize, 360, 1440);
            Settings.mapImageQuality = Mathf.Clamp(Settings.mapImageQuality, 45, 88);
            Settings.mapUpdateInterval = Mathf.Clamp(Settings.mapUpdateInterval, 0.08f, 1f);

            // Scrollable area for all settings
            var viewRect = new Rect(0f, 0f, inRect.width - 20f, settingsContentHeight);
            Widgets.BeginScrollView(inRect, ref settingsScroll, viewRect);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            DrawSetupSection(listing);
            listing.GapLine();

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

            // Troubleshooting lives at the BOTTOM. It used to be the third thing a
            // first-time streamer saw, above the connection status — a capture-bisect
            // ladder before they had connected anyone.
            listing.GapLine();
            listing.Label("Troubleshooting");
            listing.Gap(4f);
            listing.CheckboxLabeled("Pause live map capture", ref Settings.disableMapCapture,
                "Stops all off-screen map rendering for viewers. Viewers keep full pawn control; they just lose the live map picture. Use to isolate graphics problems: if an issue disappears with this on, it's in the capture pipeline.");
            listing.Label($"Capture bisect level: {Settings.captureBisectLevel}  (0 = normal. Ladder: 1 = camera only, 2 = +terrain, 3 = +pawns, 4 = +weather. Raise until the bug appears — that level is the culprit.)");
            Settings.captureBisectLevel = (int)listing.Slider(Settings.captureBisectLevel, 0f, 4f);

            listing.End();
            Widgets.EndScrollView();

            // Measure what was actually drawn so the scroll view stops clipping its own
            // bottom. The height was a hardcoded 1180f, shorter than this content.
            settingsContentHeight = listing.CurHeight + 24f;
        }
    }
}
