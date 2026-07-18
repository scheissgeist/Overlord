namespace Overlord
{
    /// <summary>
    /// Message type constants for the WebSocket protocol.
    /// </summary>
    public static class StateProtocol
    {
        // --- Game -> Browser (outgoing) ---
        public const string PawnState = "pawn_state";
        public const string PawnPortrait = "pawn_portrait";
        public const string MapFrame = "map_frame";
        public const string MapTransport = "map_transport";
        public const string MapFull = "map_full";
        public const string MapDelta = "map_delta";
        public const string MapChunk = "map_chunk";
        public const string EntityKeyframe = "entity_keyframe";
        public const string EntityDelta = "entity_delta";
        public const string ActionResult = "action_result";
        public const string Permissions = "permissions";
        public const string ColonistList = "colonist_list";
        public const string GameInfo = "game_info";
        public const string ResourceReadout = "resource_readout";
        public const string PawnDied = "pawn_died";
        public const string ActionLog = "action_log";
        public const string HostCapabilities = "host_capabilities";
        public const string ToolkitState = "toolkit_state";
        public const string ArmoryState = "armory_state";
        public const string ItemIcons = "item_icons";

        // --- Browser -> Game (incoming) ---
        public const string Command = "command";
        public const string MapClick = "map_click";
        public const string RequestState = "request_state";
        public const string StateResyncRequest = "state_resync_request";
        public const string RequestArmory = "request_armory";
        public const string RequestIcons = "request_icons";
        public const string CmdSetPreferredWeapon = "set_preferred_weapon";

        // --- Relay coordination ---
        public const string HostConnected = "host_connected";
        public const string ViewerJoined = "viewer_joined";
        public const string ViewerLeft = "viewer_left";
        public const string ViewerAuth = "viewer_auth";
        public const string ViewersList = "viewers_list";
        public const string Assign = "assign";
        public const string Unassign = "unassign";
        public const string ClaimResponse = "claim_response";

        // --- Commands (values for "action" field in Command messages) ---
        public const string CmdDraft = "draft";
        public const string CmdUndraft = "undraft";
        public const string CmdMove = "move";
        public const string CmdAttack = "attack";
        public const string CmdSetWork = "set_work";
        public const string CmdSetSchedule = "set_schedule";
        public const string CmdSetOutfit = "set_outfit";
        public const string CmdSetDrugPolicy = "set_drug_policy";
        public const string CmdSetFoodPolicy = "set_food_policy";
        public const string CmdSetArea = "set_area";
        public const string CmdSetAppearance = "set_appearance";
        public const string CmdPreviewAppearance = "preview_appearance";
        public const string CmdEquip = "equip";
        public const string CmdDrop = "drop";
        public const string CmdDyeApparel = "dye_apparel";
        public const string CmdRespawn = "respawn";
        public const string CmdHostileResponse = "set_hostile_response";
        public const string CmdClaimColonist = "claim_colonist";
        public const string CmdVote = "vote";
        public const string CmdTriggerEvent = "trigger_event";
        public const string CmdSpawnColonist = "spawn_colonist";

        // Vote + events (outgoing)
        public const string VoteUpdate = "vote_update";
        public const string GameEvent = "game_event";
        public const string ClaimRequest = "claim_request";
        public const string CmdConsume = "consume";
        public const string CmdDropInventory = "drop_inventory";
        public const string CmdContextMenu = "context_menu";
        public const string CmdContextAction = "context_action";
        public const string CmdChat = "chat";
        public const string CmdCameraZoom = "camera_zoom";
        public const string CmdToolkitRefresh = "toolkit_refresh";
        public const string CmdToolkitPurchase = "toolkit_purchase";
        public const string CmdSocialInteract = "social_interact";

        // Moderation (host -> relay -> viewer)
        public const string ViewerKick = "viewer_kick";
        public const string Banned = "banned";
        public const string Timeout = "timeout";
    }
}
