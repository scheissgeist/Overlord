using Verse;

namespace Overlord
{
    public class OverlordSettings : ModSettings
    {
        // Relay server URL (Fly.io or custom)
        public string relayUrl = "";

        // Pre-shared secret sent by the game host to authenticate with the relay
        public string hostSecret = "";

        // Local embedded server port (fallback when no relay)
        public int localPort = 8421;

        // Map rendering
        public int mapImageSize = 720;
        public int mapImageQuality = 68;
        public float mapUpdateInterval = 0.10f;
        public bool mirrorHostCameraToViewers = false;
        public int liveCameraModeVersion = 6;
        public bool allowViewerTacticalMap = false;
        public bool allowViewerResourceReadout = false;

        // Viewer frames get an adaptive gamma lift when the frame itself measures
        // dark (night, eclipse, toxic fallout). The streamer's own view is
        // untouched — this only affects the JPEG sent to viewers, who have no
        // surrounding UI for context and otherwise cannot see their pawn at all.
        // Day frames measure bright and pass through unchanged.
        public bool brightenDarkFrames = true;

        // Render viewer frames WITHOUT RimWorld's lighting-overlay mesh, so a
        // night map arrives lit instead of black. This is the real fix for the
        // "can't see anything at night" report: a real night frame measured mean
        // luma 0.0023 (99% of pixels under 0.05), which no amount of gamma can
        // recover — the JPEG encode had already destroyed the shadow detail.
        // The streamer's own view is never affected; the flag is restored
        // immediately after our draw.
        public bool unlitViewerFrames = true;

        // Troubleshooting: pause ALL live map capture (viewer frames + spectator).
        // Viewers keep pawn control; they just lose the live map. Lets the streamer
        // isolate capture-pipeline issues in seconds without disabling the mod.
        public bool disableMapCapture = false;

        // Capture bisect (troubleshooting): limits how much of the capture runs so a
        // live graphics bug can be cornered mechanically, one level at a time.
        // 0 = full normal capture. 1 = borrow camera + render EMPTY (no map draws).
        // 2 = + terrain mesh draw. 3 = + dynamic things (pawns). 4 = + game conditions.
        public int captureBisectLevel = 0;

        // Permissions defaults
        public bool allowDraft = true;
        public bool allowMove = true;
        public bool allowAttack = true;
        public bool allowWork = true;
        public bool allowSchedule = true;
        public bool allowOutfit = true;
        public bool allowDrugPolicy = true;
        public bool allowFoodPolicy = true;
        public bool allowArea = true;
        public bool allowEquip = true;
        public bool allowAppearance = false;
        public bool allowViewerEvents = false;

        // Commands
        public int commandCooldownTicks = 20; // ~0.33 seconds between commands

        // Purchases get their own, far longer cooldown. Each accepted purchase
        // spends coins, queues a real game event, and makes Toolkit answer in the
        // streamer's chat — so the 0.33s command cooldown is nowhere near enough.
        // 180 ticks = 3 seconds.
        public int purchaseCooldownTicks = 180;

        // Diagnostic: log every message Toolkit tries to send to Twitch chat, with
        // the calling stack. ToolkitCore.SendChatMessage logs nothing itself, so
        // this is the only way to see what is posting as the streamer's account.
        // Default ON while the "viewers spamming buy requests" report is open.
        public bool logToolkitChatSends = true;
        public bool enforceAreaRestrictions = true;

        // Reconnect: hand a returning viewer back the colonist they already
        // owned, without re-prompting the streamer. Only restores a pairing the
        // streamer previously approved — never assigns anyone a new pawn.
        public bool autoReconnectPreviousPawn = true;

        // Respawn
        public int startTickets = 3;
        public int respawnCooldownTicks = 2500;  // ~42 seconds
        public int maxTickets = 5;
        public int ticketEarnIntervalTicks = 180000; // ~50 minutes; 0 = disabled

        public override void ExposeData()
        {
            Scribe_Values.Look(ref relayUrl, "relayUrl", "");
            Scribe_Values.Look(ref hostSecret, "hostSecret", "");
            Scribe_Values.Look(ref localPort, "localPort", 8421);
            Scribe_Values.Look(ref mapImageSize, "mapImageSize", 720);
            Scribe_Values.Look(ref mapImageQuality, "mapImageQuality", 68);
            Scribe_Values.Look(ref mapUpdateInterval, "mapUpdateInterval", 0.10f);
            Scribe_Values.Look(ref mirrorHostCameraToViewers, "mirrorHostCameraToViewers", false);
            Scribe_Values.Look(ref liveCameraModeVersion, "liveCameraModeVersion", 0);
            Scribe_Values.Look(ref allowViewerTacticalMap, "allowViewerTacticalMap", false);
            Scribe_Values.Look(ref disableMapCapture, "disableMapCapture", false);
            Scribe_Values.Look(ref captureBisectLevel, "captureBisectLevel", 0);
            Scribe_Values.Look(ref allowViewerResourceReadout, "allowViewerResourceReadout", false);
            Scribe_Values.Look(ref brightenDarkFrames, "brightenDarkFrames", true);
            Scribe_Values.Look(ref unlitViewerFrames, "unlitViewerFrames", true);
            Scribe_Values.Look(ref allowDraft, "allowDraft", true);
            Scribe_Values.Look(ref allowMove, "allowMove", true);
            Scribe_Values.Look(ref allowAttack, "allowAttack", true);
            Scribe_Values.Look(ref allowWork, "allowWork", true);
            Scribe_Values.Look(ref allowSchedule, "allowSchedule", true);
            Scribe_Values.Look(ref allowOutfit, "allowOutfit", true);
            Scribe_Values.Look(ref allowDrugPolicy, "allowDrugPolicy", true);
            Scribe_Values.Look(ref allowFoodPolicy, "allowFoodPolicy", true);
            Scribe_Values.Look(ref allowArea, "allowArea", true);
            Scribe_Values.Look(ref allowEquip, "allowEquip", true);
            Scribe_Values.Look(ref allowAppearance, "allowAppearance", false);
            Scribe_Values.Look(ref allowViewerEvents, "allowViewerEvents", false);
            Scribe_Values.Look(ref commandCooldownTicks, "commandCooldownTicks", 20);
            Scribe_Values.Look(ref purchaseCooldownTicks, "purchaseCooldownTicks", 180);
            Scribe_Values.Look(ref logToolkitChatSends, "logToolkitChatSends", true);
            Scribe_Values.Look(ref enforceAreaRestrictions, "enforceAreaRestrictions", true);
            Scribe_Values.Look(ref startTickets, "startTickets", 3);
            Scribe_Values.Look(ref respawnCooldownTicks, "respawnCooldownTicks", 2500);
            Scribe_Values.Look(ref maxTickets, "maxTickets", 5);
            Scribe_Values.Look(ref ticketEarnIntervalTicks, "ticketEarnIntervalTicks", 180000);
            Scribe_Values.Look(ref autoReconnectPreviousPawn, "autoReconnectPreviousPawn", true);
            base.ExposeData();

            if (Scribe.mode == LoadSaveMode.PostLoadInit && liveCameraModeVersion < 2)
            {
                mirrorHostCameraToViewers = false;
                liveCameraModeVersion = 2;
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit && liveCameraModeVersion < 3)
            {
                if (mapImageSize <= 640)
                    mapImageSize = 720;
                if (mapImageQuality <= 68)
                    mapImageQuality = 68;
                if (mapUpdateInterval >= 0.25f)
                    mapUpdateInterval = 0.25f;
                liveCameraModeVersion = 3;
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit && liveCameraModeVersion < 4)
            {
                if (mapImageSize == 900)
                    mapImageSize = 720;
                if (mapImageQuality == 76)
                    mapImageQuality = 68;
                if (mapUpdateInterval <= 0.16f)
                    mapUpdateInterval = 0.25f;
                liveCameraModeVersion = 4;
            }

            // v5: raise default JPEG cadence from slideshow 0.25s toward ~7 FPS solo.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && liveCameraModeVersion < 5)
            {
                if (mapUpdateInterval >= 0.24f)
                    mapUpdateInterval = 0.14f;
                liveCameraModeVersion = 5;
            }

            // v6: push default toward ~10 FPS; migrate prior 0.14 defaults upward.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && liveCameraModeVersion < 6)
            {
                if (mapUpdateInterval >= 0.13f && mapUpdateInterval <= 0.15f)
                    mapUpdateInterval = 0.10f;
                liveCameraModeVersion = 6;
            }
        }
    }
}
