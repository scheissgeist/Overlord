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

        public bool HasPawn => assignedPawn != null && !assignedPawn.Dead && !assignedPawn.Destroyed && assignedPawn.Spawned;

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
