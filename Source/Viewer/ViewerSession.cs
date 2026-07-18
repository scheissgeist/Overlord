using System.Collections.Generic;
using Verse;
using RimWorld;

namespace Overlord
{
    /// <summary>
    /// Per-viewer state: who they are, which pawn they control, permission overrides.
    /// </summary>
    public class ViewerSession : IExposable
    {
        public string username;
        public string displayName;
        public Pawn assignedPawn;
        public ViewerPermissions permissions;
        public int tickets;
        [System.NonSerialized]
        public bool isConnected;

        // Delta tracking: hash of last-sent pawn state per viewer
        public int lastStateHash;

        // Transient tactical-map stream envelope. Resets on host reload/save load.
        [System.NonSerialized]
        public int tacticalMapEpoch;
        [System.NonSerialized]
        public int tacticalMapSeq;
        [System.NonSerialized]
        public string mapTransportPreference;
        [System.NonSerialized]
        public int tacticalEntityEpoch;
        [System.NonSerialized]
        public int tacticalEntitySeq;
        [System.NonSerialized]
        public HashSet<int> tacticalMapEntityIds;
        [System.NonSerialized]
        public Dictionary<int, int> tacticalEntityHashes;
        [System.NonSerialized]
        public int tacticalMapChunkSeq;
        [System.NonSerialized]
        public Dictionary<string, int> tacticalMapChunkHashes;

        // Action log tracking
        public string lastJobLabel;
        public float  lastHealthPct;

        // Watchdog progress-tracking (all transient, recomputed live, never saved).
        // The detector flags a pawn whose JOB IS NOT ADVANCING — same job id + same
        // cell, not moving — because the founding incident (corrupted equipment
        // tracker) freezes a non-null job rather than nulling it. Presence of a job
        // is not health; progress is.
        [System.NonSerialized]
        public int watchdogLastJobId = -1;
        [System.NonSerialized]
        public IntVec3 watchdogLastCell = IntVec3.Invalid;
        // ticksLeftThisToil last sample — a working driver (crafting at a bench,
        // researching) decrements this every tick while stationary; a tick-frozen
        // driver holds it constant. This distinguishes "busy but not moving" from
        // "frozen", so a legitimate long stationary job never false-trips.
        [System.NonSerialized]
        public int watchdogLastToilTicks = int.MinValue;
        // Tick when the pawn first stopped making progress, or -1 when healthy.
        [System.NonSerialized]
        public int stuckSinceTick = -1;
        // True once the streamer has been alerted about the current stuck episode,
        // so the message fires once per episode, not every cycle.
        [System.NonSerialized]
        public bool stuckAlertRaised = false;

        // Rate limiting: game tick of last accepted command
        public int lastCommandTick = -999;

        // Last command label for streamer UI visibility (transient, not saved)
        [System.NonSerialized]
        public string lastCommandLabel = "";

        // Independent live camera zoom for this viewer. 1 = default, higher = closer.
        public float cameraZoom = 1f;

        // Optional independent live camera center. When disabled, the camera follows the pawn.
        public bool cameraHasCenter = false;
        public float cameraCenterX = 0f;
        public float cameraCenterZ = 0f;

        // Viewer-reported viewport aspect (width/height). 0 = use host default (16:9).
        // Updated when the browser sends camera_zoom with an aspect field.
        [System.NonSerialized]
        public float viewportAspect = 0f;

        // Viewer-reported canvas pixel height. 0 = use mod setting.
        [System.NonSerialized]
        public int viewportHeight = 0;

        // Ticket earn tracking: game tick when this viewer last earned a ticket
        public int lastTicketEarnTick = -1;

        // Pending log entries to send next tick
        public readonly List<string> pendingLogEntries = new List<string>();

        // Context menu: cached options from last context_menu request
        [System.NonSerialized]
        public List<FloatMenuOption> lastContextOptions;

        // Original pawn name before viewer rename
        public string originalPawnNickname;

        // One no-cost self-service hairstyle/gender change per viewer.
        public bool freeAppearanceUsed = false;

        // Standing order: preferred weapon defName. When set, the per-pawn sweep
        // auto-equips a matching unclaimed weapon that exists in the colony if the
        // pawn isn't already holding one. Empty/null = no preference. Persisted.
        public string preferredWeaponDef;
        // Throttle: don't re-attempt the auto-equip every sweep while the weapon is
        // unreachable/claimed — remember the last tick we tried.
        public int lastPreferredWeaponTick = -999;

        public ViewerSession()
        {
            permissions = new ViewerPermissions();
            tickets = OverlordMod.Settings?.startTickets ?? 3;
        }

        public ViewerSession(string username, string displayName) : this()
        {
            this.username = username;
            this.displayName = displayName ?? username;
            isConnected = true;
        }

        // OwnsPawn = the viewer is still assigned a living colonist, even if it's
        // temporarily OFF-MAP (carried while downed, in a caravan, in a drop pod /
        // transport, captured, in a cryptosleep casket). Ownership is NOT lost —
        // only the ability to act right now is. Governs whether the reassignment /
        // "needs a colonist" claim pop-up should fire (it must NOT while merely off-map).
        public bool OwnsPawn => assignedPawn != null && !assignedPawn.Dead && !assignedPawn.Destroyed;

        // HasPawn = OwnsPawn AND the pawn is on the map right now, i.e. the viewer can
        // actually be shown a live map and issue commands. Live-action gate.
        public bool HasPawn => OwnsPawn && assignedPawn.Spawned;

        // True when the owned pawn exists but is temporarily off-map — control resumes
        // automatically when it respawns. Used to show "away" instead of "lost".
        public bool PawnAwayTemporarily => OwnsPawn && !assignedPawn.Spawned;

        public bool TacticalMapTransportAvailable =>
            OverlordMod.Settings?.allowViewerTacticalMap == true &&
            OverlordMod.Settings?.mirrorHostCameraToViewers != true;

        public string RequestedMapTransport =>
            mapTransportPreference == "jpeg" || mapTransportPreference == "tile"
                ? mapTransportPreference
                : "auto";

        public string SelectedMapTransport =>
            RequestedMapTransport == "jpeg" || !TacticalMapTransportAvailable
                ? "jpeg"
                : "tile";

        public bool UsesTacticalMapTransport => SelectedMapTransport == "tile";
        public bool UsesJpegMapTransport => SelectedMapTransport == "jpeg";

        public void SetMapTransportPreference(string requested)
        {
            string normalized = string.IsNullOrEmpty(requested) ? "auto" : requested.Trim().ToLowerInvariant();
            if (normalized != "tile" && normalized != "jpeg")
                normalized = "auto";
            if (mapTransportPreference == normalized)
                return;
            mapTransportPreference = normalized;
            ResetTacticalMapStream();
        }

        public void ResetTacticalMapStream()
        {
            tacticalMapEpoch = 0;
            tacticalMapSeq = 0;
            tacticalEntityEpoch = 0;
            tacticalEntitySeq = 0;
            tacticalMapChunkSeq = 0;
            ResetTacticalMapEntities();
            ResetTacticalMapChunks();
        }

        public void ResetTacticalMapEntities()
        {
            tacticalMapEntityIds = null;
            tacticalEntityHashes = null;
        }

        public void ResetTacticalMapChunks()
        {
            tacticalMapChunkHashes = null;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref username, "username");
            Scribe_Values.Look(ref displayName, "displayName");
            Scribe_References.Look(ref assignedPawn, "assignedPawn");
            Scribe_Deep.Look(ref permissions, "permissions");
            Scribe_Values.Look(ref tickets, "tickets", 3);
            Scribe_Values.Look(ref cameraZoom, "cameraZoom", 1f);
            Scribe_Values.Look(ref cameraHasCenter, "cameraHasCenter", false);
            Scribe_Values.Look(ref cameraCenterX, "cameraCenterX", 0f);
            Scribe_Values.Look(ref cameraCenterZ, "cameraCenterZ", 0f);
            Scribe_Values.Look(ref freeAppearanceUsed, "freeAppearanceUsed", false);
            Scribe_Values.Look(ref preferredWeaponDef, "preferredWeaponDef");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (permissions == null)
                    permissions = new ViewerPermissions();
                if (cameraZoom <= 0f)
                    cameraZoom = 1f;
                if (!cameraHasCenter)
                {
                    cameraCenterX = 0f;
                    cameraCenterZ = 0f;
                }
            }
        }
    }
}
