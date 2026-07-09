# Puppeteer Mod — Complete Feature Inventory

Reverse-engineered from OPuppeteer.dll (v0.7.0, package bleusquid.puppeteer)
Workshop ID: 2594873651 | Supported: RimWorld 1.3, 1.4 only

## Feature Categories

### 1. Viewer-Pawn Assignment
- Streamer right-clicks colonist bar to assign viewer
- [+] button to create new colonist and auto-assign
- `AssignViewerToPawn` / `CreatePuppeteerForViewer` / `Unassign`
- `AllPuppets` / `AllPuppeteers` / `AvailablePuppets` / `ConnectedPuppeteers`
- `PuppetForPawn` / `PuppeteerForViewerName` mapping

### 2. Browser Control Panel (per viewer)
**Data displayed:**
- ColonistBaseInfo / ColonistInfo — name, faction, position
- SkillInfo — all skills with levels, passions, XP
- NeedInfo — food, rest, mood, etc. with levels
- PawnHealthState + CapacityInfo — injuries, capacities
- ThoughtInfo — mood thoughts with offsets
- ScheduleInfo — 24-hour timetable
- PrioritiyInfo — work priorities per type
- ApparelJSON — worn clothing/armor
- Equipment — primary weapon
- RelationJSON — social relationships
- TraitJSON — character traits

**Actions available:**
- Draft/Undraft (FakeDraft system)
- Move (click map)
- Attack (FightEnemy, GetMeleeAttackAction)
- Set work priorities (per WorkTypeDef)
- Set schedule (24-hour TimeAssignmentDef)
- Select outfit (ApparelPolicy)
- Select drug policy
- Set food restriction
- Set area restriction
- Equip/drop weapons
- Wear/remove apparel
- Customize appearance (hair, body type, style)

### 3. Map View
- Camera-clone rendering to JPEG
- Configurable: mapImageSize, mapImageCompression, mapUpdateFrequency
- Right-click context menu in browser
- Mobile tap-and-hold for context actions
- Cell-based interaction (CellJSON with coordinates)

### 4. Respawn System
- ResurrectionPortal (ThingDef, buildable)
- PlaceWorker_ResurrectionPortal — placement validation
- Ticket system (StartTickets setting, consumed per respawn)
- Death cause tracking:
  - Viewer-caused death → respawn (ticket consumed)
  - Streamer/player-caused death → permanent
  - PlayerActionCooldownTicks — cooldown after streamer touches pawn
  - Blue bar shows cooldown period
- Portal cooldown (prevent rapid portal relocation)
- 0 or 1 portal per map

### 5. Off-Limits Areas
- Dialog_EditOffLimitsArea — area editor
- Dialog_EditRestriction — restriction editor
- Designator_OffLimits — zone painting tool
- OffLimitsComponent — tracks off-limits zones
- PawnColumnWorker_PuppetOffLimits — settings table column
- showOffLimitZones — visibility toggle
- Restricts where viewer-controlled pawns can go

### 6. Colonist Bar Integration
- ColonistBarColonistDrawer_DrawColonist_Patch — adds connection indicator
- ColonistBarColonistDrawer_HandleClicks_Patch — right-click assignment menu
- ColonistBarDrawLocsFinder_GetDrawLoc_Patch — position adjustment
- ColonistBar_MarkColonistsDirty_Patch — refresh on state change
- ColonistBar_Reorder_Patch — reorder handling
- Visual: connection status icons (Connected0/1/2.png), puppet icon, new indicator

### 7. Twitch Integration
- TwitchLib for chat connection
- TwitchToolkit compatibility
- TwitchWrapper_SendChatMessage_Patch
- IncomingChat / OutgoingChat
- sendChatResponsesToTwitch setting
- ViewerCommands / TTCommand
- Chat announcements for events

### 8. State Sync Protocol
**JSON data structures (17 types):**
ApparelJSON, BedJSON, BillJSON, CellJSON, ContainerJSON, DataJSON,
JobJSON, MindJSON, NeedJSON, PawnJSON, RelationJSON, RoomJSON,
SkillJSON, ThingJSON, TraitJSON, VerbJSON, WorkJSON

**Message flow:**
- IncomingChat / IncomingJob / IncomingState (browser → game)
- OutgoingChat / OutgoingJobResult / OutgoingState / OutgoingRequests (game → browser)
- SendAllState / SendAllColonists / SendGameInfo / SendTimeInfo
- SendPortrait / SendGear / SendInventory / SendPriorities / SendSchedules
- SendChangedPriorities / SendChangedSchedules (delta)
- SendSocialRelations / SendNextSocial
- SendCoins / SendCoinsToAll / SendToolkitCommands

### 9. Settings & Configuration
- PuppeteerSettingsWindow — main tab (MainButtonDef)
- PuppeteerSettingsTable — per-colonist settings
- PawnColumnWorker_PuppetEnabled — toggle control per colonist
- CopyPastePuppeteerSettings — copy settings between colonists
- Game token system (file-based auth in Config folder)
- Stream Information configuration

### 10. Remote Action Log
- RemoteLog — action history
- RemoteAction / RemoteActionExtension
- All viewer actions logged to colonist log tab
- Shows who, what, when

### 11. Harmony Patches (55 total)
**Low risk (game lifecycle):**
Game_LoadGame_Patch, Game_UpdatePlay_Patch, Root_Play_Start_Patch,
Current_Notify_LoadedSceneChanged_Patch, MainMenuDrawer_Init_Patch,
Prefs_DevMode_Patch

**Medium risk (pawn state):**
Pawn_Kill_Patch, Pawn_DeSpawn_Patch, Pawn_SpawnSetup_Patch,
Pawn_set_Name_Patch, Pawn_SetFaction_Patch, Thing_SetFactionDirect_Patch,
Pawn_DraftController_Drafted_Patch, Pawn_WorkSettings_SetPriority_Patch,
Pawn_WorkSettings_Notify_UseWorkPrioritiesChanged_Patch,
Pawn_TimetableTracker_Constructor_Patch, Pawn_TimetableTracker_SetPriority_Patch,
GenSpawn_Spawn_Patch, WorldPawns_PassToWorld_Patch, Thing_SplitOff_Patch,
GameDataSaveLoader_SaveGame_Patch

**High risk (UI/rendering — BROKEN in 1.6):**
PawnRenderer_RenderPawnInternal_Patch — GONE
ColonistBarColonistDrawer_DrawColonist_Patch — GONE
ColonistBarColonistDrawer_HandleClicks_Patch — GONE
ColonistBarDrawLocsFinder_GetDrawLoc_Patch — changed
FloatMenuOption_Constructor_Patch — GONE
GizmoGridDrawer_DrawGizmoGrid_Patch — internal
LearningReadout_WindowOnGUI_Patch — GONE

**Graphics system (6 patches):**
Graphics_DrawMeshInstanced_Patch, Graphics_Internal_DrawMesh_Patch,
Graphics_Internal_DrawMeshInstancedIndirect_Patch,
Graphics_Internal_DrawMeshNow1_Patch, Graphics_Internal_DrawMeshNow2_Patch,
CameraDriver patches

**Other:**
Area_Patches, DebugToolsPawns_TryJobGiver_Patch,
BuildCopyCommandUtility_BuildCommand_Patch, PlayerIssuedOrders_Patch,
PlaySettings_DoPlaySettingsGlobalControls_Patch, PortraitsCache_SetDirty_Patch,
Root_OnGUI_Patch, Widgets_WidgetsOnGUI_Patch, WindowStack_WindowStackOnGUI_Patch,
ColonistBar_MarkColonistsDirty_Patch, ColonistBar_Reorder_Patch,
Map_MapUpdate_Patch, GlobalControls_GlobalControlsOnGUI_Patch,
CellBoolDrawer_ActuallyDraw_Patch, CellBoolDrawer_CreateMaterialIfNeeded_Patch

### 12. Compatibility
- Prepare Carefully — new colonist creation
- Twitch Toolkit — channel points, commands
- DUbs Mini Map — layer ordering
- Camera+ — rendering compatibility
- WorkTab — extended work priorities
- Royalty DLC — psycast abilities
