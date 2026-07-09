# RimWorld 1.6 API Surface for Overlord

Installed version: **1.6.4633 rev1260**
DLL: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`

## Verified Public APIs (via reflection)

### Pawn_DraftController
```
Drafted { get; set; }              // Draft/undraft pawn
FireAtWill { get; set; }           // Fire at will toggle
ShowDraftGizmo { get; }            // Gizmo visibility
DraftControllerTickInterval()      // Tick update
```

### Pawn_JobTracker (27 methods)
```
StartJob(Job, JobCondition)        // Issue job
TryTakeOrderedJob(Job)             // Queue ordered job
TryStartJob(Job)                   // Try variant
EndCurrentJob(JobCondition)        // Cancel current job
EndCurrentOrQueuedJob()            // Cancel all
ClearQueuedJobs()                  // Clear queue
StopAll()                          // Stop everything
SuspendCurrentJob(JobCondition)    // Suspend
CheckForJobOverride()              // Override check
AllJobs()                          // Get all jobs
ReleaseReservations()              // Cleanup
```

### Pawn_WorkSettings (11 methods)
```
SetPriority(WorkTypeDef, int)      // Set work priority (0-4)
GetPriority(WorkTypeDef)           // Get priority
WorkIsActive(WorkTypeDef)          // Check if active
EnableAndInitialize()              // Init
DisableAll()                       // Disable all
Disable()                          // Disable work
```

### Pawn_TimetableTracker
```
SetAssignment(int hour, RestCategory)  // Set schedule slot
GetAssignment(int hour)                // Get schedule slot
CurrentAssignment { get; }             // Current activity
times: RestCategory[]                  // 24-hour array
```

### Pawn_OutfitTracker
```
CurrentApparelPolicy { get; set; }     // GET/SET outfit (was CurrentOutfit in 1.4)
curApparelPolicy                       // Backing field
forcedHandler: OutfitForcedHandler     // Forced items
```

### Pawn_ApparelTracker (39 methods)
```
Wear(Apparel)                      // Wear item
TryDrop(Apparel)                   // Remove item
Wearing(Apparel)                   // Check if wearing
Lock() / Unlock()                  // Lock/unlock apparel
LockAll() / UnlockAll()            // Batch lock
IsLocked(Apparel)                  // Check locked
WornApparel { get; }               // All worn items
WornApparelCount { get; }          // Count
```

### Pawn_DrugPolicyTracker
```
CurrentPolicy { get; set; }        // GET/SET drug policy
AllowedToTakeScheduledNow()        // Auto-take check
ShouldTryToTakeScheduledNow()      // Decision
HasEverTaken()                     // History
```

### Pawn_FoodRestrictionTracker
```
CurrentFoodPolicy { get; set; }    // GET/SET food policy (was CurrentFoodRestriction in 1.4)
Configurable { get; }              // Can configure
GetCurrentRespectedRestriction()   // Active restriction
BabyFoodAllowed() / SetBabyFoodAllowed(bool)
```

### Pawn_PlayerSettings
```
AreaRestrictionInPawnCurrentMap { get; set; }  // Area restriction
EffectiveAreaRestrictionInPawnCurrentMap { get; }
Master { get; set; }               // Master pawn
medCare: MedicalCareCategory       // Medical care level
hostilityResponse: HostilityResponseMode
selfTend { get; set; }             // Self-tend
followDrafted { get; set; }        // Follow when drafted
followFieldwork { get; set; }      // Follow fieldwork
```

### Pawn_AbilityTracker
```
GainAbility(Ability)               // Learn
RemoveAbility(Ability)             // Forget
GetAbility(AbilityDef)             // Get specific
AICastableAbilities()              // Castable list
AllAbilitiesForReading { get; }    // Read all
```

### PortraitsCache (replaces PawnRenderer)
```
static Get(Pawn, Vector2, Rot4, Color[], bool)  // Get portrait render
static SetDirty(Pawn)              // Invalidate
static Clear()                     // Clear all
static PortraitsCacheUpdate()      // Frame update
```

### FloatMenuMakerMap
```
static GetOptions(Pawn, Thing)     // Get context menu options
static Init(FloatMenuSizeMode)     // Initialize
static ShouldGenerateFloatMenuForPawn(Pawn)
```

### FloatMenuOptionProvider (NEW in 1.6)
Base class for context menu options. Subclass and override:
```
Applies()                          // Does this provider apply?
GetOptions()                       // Get all options
GetOptionsFor(Pawn, Thing)         // Options for target
GetOptionsFor(Pawn, Pawn)          // Options for pawn target
```

### Game (15 methods)
```
UpdatePlay()                       // Per-frame update
InitNewGame()                      // New game init
LoadGame()                         // Load game
AddMap(IntVec3, WorldObject)       // Add map
FindMap(int)                       // Find map
DeinitAndRemoveMap(Map)            // Remove map
GetComponent<T>(Type)              // Get game component
CurrentMap { get; set; }           // Active map
Maps { get; }                      // All maps
PlayerHasControl { get; }          // Player control status
```

## Classes REMOVED in 1.6

| 1.4 Class | Status | Replacement |
|-----------|--------|-------------|
| `Verse.PawnRenderer` | GONE | `PawnRenderTree` (node-based system) |
| `RimWorld.ColonistBarColonistDrawer` | GONE | Use `Find.ColonistBar` public API |
| `Verse.FloatMenuOption` | GONE | `FloatMenuOptionProvider` system |
| `Verse.GizmoGridDrawer` | Internal only | Still exists but changed |
| `RimWorld.LearningReadout` | GONE from Assembly-CSharp | Moved/restructured |

## Classes RENAMED in 1.6

| 1.4 Name | 1.6 Name |
|----------|----------|
| `CurrentOutfit` | `CurrentApparelPolicy` |
| `CurrentFoodRestriction` | `CurrentFoodPolicy` |
| `Pawn_OutfitTracker.curOutfit` | `Pawn_OutfitTracker.curApparelPolicy` |

## Safe Patch Targets (stable since 1.0)

- `Game.InitNewGame` — postfix
- `Game.LoadGame` — postfix
- `Game.DeinitAndRemoveMap` — prefix
- `Pawn.Kill` — postfix (check `DamageInfo.Instigator`)

## RimWorld DLL Locations

```
RimWorld: C:\Program Files (x86)\Steam\steamapps\common\RimWorld
Managed:  ...\RimWorldWin64_Data\Managed\Assembly-CSharp.dll
Unity:    ...\RimWorldWin64_Data\Managed\UnityEngine*.dll
Harmony:  ...\Mods\HarmonyRimWorld\Current\Assemblies\0Harmony.dll
```
