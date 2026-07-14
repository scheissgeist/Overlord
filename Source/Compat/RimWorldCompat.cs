using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Overlord
{
    /// <summary>
    /// Centralizes version-sensitive RimWorld API access behind a narrow facade.
    /// When upstream APIs drift, this is the file that should absorb the change.
    /// </summary>
    public static class RimWorldCompat
    {
        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class CurrentMapScope : IDisposable
        {
            private readonly Game game;
            private readonly sbyte previousMapIndex;
            private bool disposed;

            public CurrentMapScope(Game game, sbyte previousMapIndex)
            {
                this.game = game;
                this.previousMapIndex = previousMapIndex;
            }

            public void Dispose()
            {
                if (disposed)
                    return;

                if (game != null)
                    game.currentMapIndex = previousMapIndex;
                disposed = true;
            }
        }

        public sealed class ReservationMetadata
        {
            public bool Reserved;
            public int ReservedById = -1;
            public string ReservedByLabel = "";
            public string ReservationJobDef = "";
            public int ReservationTargetId = -1;
            public int ReservationTargetX = -1;
            public int ReservationTargetZ = -1;
            public bool HasReservationTargetCell;
        }

        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly PropertyInfo VersionStringProperty =
            ResolveProperty(typeof(VersionControl), "CurrentVersionStringWithRev", "CurrentVersionString");

        private static readonly MethodInfo WorkPriorityMethod =
            ResolveMethod(typeof(Pawn_WorkSettings), "SetPriority", typeof(WorkTypeDef), typeof(int));

        private static readonly MethodInfo TimetableSetAssignmentMethod =
            ResolveTimetableSetAssignmentMethod();

        private static readonly PropertyInfo OutfitPolicyProperty =
            ResolveProperty(typeof(Pawn_OutfitTracker), "CurrentApparelPolicy", "CurrentOutfit");

        private static readonly PropertyInfo DrugPolicyProperty =
            ResolveProperty(typeof(Pawn_DrugPolicyTracker), "CurrentPolicy");

        private static readonly PropertyInfo FoodPolicyProperty =
            ResolveProperty(typeof(Pawn_FoodRestrictionTracker), "CurrentFoodPolicy", "CurrentFoodRestriction");

        private static readonly PropertyInfo AreaRestrictionProperty =
            ResolveProperty(typeof(Pawn_PlayerSettings), "AreaRestrictionInPawnCurrentMap");

        private static readonly FieldInfo WorkTypeRelevantSkillsField =
            ResolveField(typeof(WorkTypeDef), "relevantSkills");

        private static readonly PropertyInfo WorkTypeRelevantSkillsProperty =
            ResolveProperty(typeof(WorkTypeDef), "RelevantSkills");

        private static readonly MethodInfo PortraitGetMethod =
            ResolvePortraitGetMethod();

        private static readonly PropertyInfo ColonistBarEntriesProperty =
            ResolveProperty(typeof(ColonistBar), "Entries");

        private static readonly PropertyInfo ColonistBarDrawLocsProperty =
            ResolveProperty(typeof(ColonistBar), "DrawLocs");

        private static readonly PropertyInfo ColonistBarSizeProperty =
            ResolveProperty(typeof(ColonistBar), "Size");

        private static readonly Func<object, Pawn> ColonistBarEntryPawnAccessor =
            ResolveColonistBarEntryPawnAccessor();

        private static readonly MethodInfo FloatMenuGetOptionsMethod =
            ResolveFloatMenuGetOptionsMethod();

        private static bool loggedCapabilities;

        public static string RimWorldVersion
        {
            get
            {
                try
                {
                    return VersionStringProperty?.GetValue(null, null)?.ToString() ?? "unknown";
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        public static bool SupportsWorkPriorities => WorkPriorityMethod != null;
        public static bool SupportsScheduleAssignments => TimetableSetAssignmentMethod != null;
        public static bool SupportsOutfitPolicies => OutfitPolicyProperty != null;
        public static bool SupportsDrugPolicies => DrugPolicyProperty != null;
        public static bool SupportsFoodPolicies => FoodPolicyProperty != null;
        public static bool SupportsAreaRestrictions => AreaRestrictionProperty != null;
        public static bool SupportsPortraitRendering => PortraitGetMethod != null;
        public static bool SupportsColonistBarOverlay =>
            ColonistBarEntriesProperty != null &&
            ColonistBarDrawLocsProperty != null &&
            ColonistBarSizeProperty != null &&
            ColonistBarEntryPawnAccessor != null;
        public static bool SupportsContextMenus => FloatMenuGetOptionsMethod != null;

        public static void LogCapabilitiesOnce()
        {
            if (loggedCapabilities)
                return;

            loggedCapabilities = true;
            LogUtil.Log(DescribeCapabilities());
        }

        public static string DescribeCapabilities()
        {
            return
                $"Compat ready for RimWorld {RimWorldVersion}: " +
                $"work={(SupportsWorkPriorities ? "ok" : "missing")}, " +
                $"schedule={(SupportsScheduleAssignments ? "ok" : "missing")}, " +
                $"outfit={(SupportsOutfitPolicies ? "ok" : "missing")}, " +
                $"drug={(SupportsDrugPolicies ? "ok" : "missing")}, " +
                $"food={(SupportsFoodPolicies ? "ok" : "missing")}, " +
                $"area={(SupportsAreaRestrictions ? "ok" : "missing")}, " +
                $"portraits={(SupportsPortraitRendering ? "ok" : "missing")}, " +
                $"colonistBar={(SupportsColonistBarOverlay ? "ok" : "missing")}, " +
                $"contextMenu={(SupportsContextMenus ? "ok" : "missing")}";
        }

        public static Dictionary<string, object> BuildCapabilityMessage()
        {
            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.HostCapabilities,
                ["rimworldVersion"] = RimWorldVersion,
                ["work"] = SupportsWorkPriorities,
                ["schedule"] = SupportsScheduleAssignments,
                ["outfit"] = SupportsOutfitPolicies,
                ["drug"] = SupportsDrugPolicies,
                ["food"] = SupportsFoodPolicies,
                ["area"] = SupportsAreaRestrictions,
                ["portraits"] = SupportsPortraitRendering,
                ["colonistBar"] = SupportsColonistBarOverlay,
                ["contextMenu"] = SupportsContextMenus,
                ["events"] = OverlordMod.Settings?.allowViewerEvents == true,
                ["enforceAreaRestrictions"] = OverlordMod.Settings?.enforceAreaRestrictions == true,
                ["mirrorHostCamera"] = OverlordMod.Settings?.mirrorHostCameraToViewers == true,
                ["independentViewerCamera"] = OverlordMod.Settings?.mirrorHostCameraToViewers != true,
                ["serverCameraZoom"] = true,
                ["tacticalMap"] = OverlordMod.Settings?.allowViewerTacticalMap == true,
                ["tacticalMapEntityVisibility"] = "fog",
                ["visibilityFilteredEntities"] = true,
                ["visibilityFilteredMap"] = true,
                ["resourceReadout"] = OverlordMod.Settings?.allowViewerResourceReadout == true,
                ["toolkitBridge"] = TwitchToolkitBridge.IsBridgeAvailable,
                ["storyPurchaseArguments"] = true,
                ["toolkitLoaded"] = TwitchToolkitBridge.IsToolkitLoaded,
                ["toolkitUtilsLoaded"] = TwitchToolkitBridge.IsToolkitUtilsLoaded,
                ["toolkitChatConnected"] = TwitchToolkitBridge.IsChatConnected,
                // Fixed dye swatch palette for the viewer gear panel.
                ["dyePalette"] = PawnCommandRouter.BuildDyePaletteMessage()
            };
        }

        public static byte ClassifyMapBuilding(Thing thing)
        {
            var def = thing?.def;
            if (def == null)
                return 0;

            try
            {
                if (def == ThingDefOf.Wall)
                    return 1;
                if (def == ThingDefOf.Door)
                    return 2;
                if (def.building?.bed_humanlike == true)
                    return 3;
                if (def.hasInteractionCell)
                    return 4;
                if (thing.Faction != null && FactionUtility.HostileTo(thing.Faction, Faction.OfPlayer))
                    return 5;
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        public static string MapBuildingRole(Thing thing)
        {
            var def = thing?.def;
            if (def == null)
                return "building";

            try
            {
                if (def == ThingDefOf.Wall)
                    return "wall";
                if (def == ThingDefOf.Door)
                    return "door";
                if (def.building?.bed_humanlike == true)
                    return "bed";
                if (def.hasInteractionCell)
                    return "workbench";
            }
            catch
            {
                return "building";
            }

            return "building";
        }

        public static int MapThingFlags(Thing thing, Pawn viewerPawn)
        {
            int flags = 0;
            if (IsForbiddenForViewer(thing, viewerPawn))
                flags |= 1;
            if (IsReserved(thing))
                flags |= 2;
            return flags;
        }

        public static byte ClassifyMapItem(Thing thing)
        {
            var def = thing?.def;
            if (def == null)
                return 0;

            try
            {
                if (def.IsWeapon)
                    return 1;
                if (def.apparel != null)
                    return 2;

                string text = ((def.defName ?? "") + " " + (def.label ?? "")).ToLowerInvariant();
                if (ContainsAny(text, "meal", "food", "nutrition", "meat", "vegetable", "berry", "berries", "corn", "rice", "potato", "milk", "egg"))
                    return 3;
                if (ContainsAny(text, "medicine", "drug", "penoxy", "herbal", "healroot", "glitterworld"))
                    return 4;
                if (ContainsAny(text, "steel", "wood", "cloth", "component", "plasteel", "uranium", "silver", "gold", "jade", "stoneblock", "blocks"))
                    return 5;
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        public static string ClassifyMapWorldEntityKind(Thing thing)
        {
            if (thing == null)
                return null;

            try
            {
                if (thing is Fire)
                    return "fire";
                if (thing is Plant)
                    return "plant";
                if (thing is Blueprint || thing is Frame)
                    return "construction";
            }
            catch
            {
                return null;
            }

            return null;
        }

        public static float SafePlantGrowth(Thing thing)
        {
            try
            {
                return thing is Plant plant ? plant.Growth : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        public static float SafeConstructionProgress(Thing thing)
        {
            if (thing == null)
                return 0f;

            try
            {
                float workDone = ReadFloatMember(thing, "workDone", "WorkDone");
                float workToBuild = ReadFloatMember(thing, "workToBuild", "WorkToBuild");
                if (workToBuild <= 0f)
                    return 0f;
                return Mathf.Clamp01(workDone / workToBuild);
            }
            catch
            {
                return 0f;
            }
        }

        public static bool SafeDoorOpen(Thing thing)
        {
            try
            {
                if (MapBuildingRole(thing) != "door")
                    return false;
                return ReadBoolMember(thing, "Open", "open", "openInt");
            }
            catch
            {
                return false;
            }
        }

        public static List<string> SafeBedOwners(Thing thing)
        {
            var owners = new List<string>();
            try
            {
                if (MapBuildingRole(thing) != "bed")
                    return owners;

                object value = ReadMember(thing, "OwnersForReading", "owners", "Owners");
                if (value is IEnumerable enumerable)
                {
                    foreach (object entry in enumerable)
                    {
                        string label = GetLabel(entry);
                        if (!string.IsNullOrEmpty(label))
                            owners.Add(label);
                    }
                }
            }
            catch
            {
                return owners;
            }

            return owners;
        }

        public static int SafeBillCount(Thing thing)
        {
            try
            {
                if (MapBuildingRole(thing) != "workbench")
                    return 0;

                object billStack = ReadMember(thing, "BillStack", "billStack");
                object bills = ReadMember(billStack, "Bills", "bills");
                return CountEnumerable(bills);
            }
            catch
            {
                return 0;
            }
        }

        public static List<string> SafeBillLabels(Thing thing, int limit = 4)
        {
            var labels = new List<string>();
            try
            {
                if (MapBuildingRole(thing) != "workbench")
                    return labels;

                object billStack = ReadMember(thing, "BillStack", "billStack");
                object bills = ReadMember(billStack, "Bills", "bills");
                if (bills is IEnumerable enumerable)
                {
                    foreach (object bill in enumerable)
                    {
                        string label = GetLabel(bill);
                        if (!string.IsNullOrEmpty(label))
                            labels.Add(label);
                        if (labels.Count >= limit)
                            break;
                    }
                }
            }
            catch
            {
                return labels;
            }

            return labels;
        }

        public static List<Dictionary<string, object>> SafeBillDetails(Thing thing, int limit = 4)
        {
            var details = new List<Dictionary<string, object>>();
            try
            {
                if (MapBuildingRole(thing) != "workbench")
                    return details;

                object billStack = ReadMember(thing, "BillStack", "billStack");
                object bills = ReadMember(billStack, "Bills", "bills");
                if (bills is IEnumerable enumerable)
                {
                    foreach (object entry in enumerable)
                    {
                        if (!(entry is Bill bill))
                            continue;

                        var detail = BuildBillDetail(bill);
                        if (detail.Count > 0)
                            details.Add(detail);
                        if (details.Count >= limit)
                            break;
                    }
                }
            }
            catch
            {
                return details;
            }

            return details;
        }

        public static string SummarizeBillDetails(List<Dictionary<string, object>> details, int limit = 2)
        {
            if (details == null || details.Count == 0)
                return "";

            var parts = new List<string>();
            foreach (var detail in details)
            {
                if (detail == null)
                    continue;

                string label = DetailString(detail, "label");
                string repeat = DetailString(detail, "repeatInfo");
                string state = DetailBool(detail, "suspended")
                    ? "suspended"
                    : DetailBool(detail, "paused")
                        ? "paused"
                        : DetailBool(detail, "shouldDoNow")
                            ? "active"
                            : "waiting";
                string text = (label + " " + repeat + " " + state).Trim();
                if (!string.IsNullOrEmpty(text))
                    parts.Add(text);
                if (parts.Count >= limit)
                    break;
            }

            return string.Join(" | ", parts.ToArray());
        }

        public static Dictionary<string, object> SafeActiveWorkbenchJobMetadata(Thing thing)
        {
            var metadata = new Dictionary<string, object>();
            try
            {
                if (MapBuildingRole(thing) != "workbench" || thing?.Map?.mapPawns == null)
                    return metadata;

                foreach (Pawn pawn in thing.Map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn == null || pawn.Destroyed || pawn.Dead)
                        continue;

                    Job job = pawn.jobs?.curJob;
                    if (job == null || job.def != JobDefOf.DoBill || job.targetA.Thing != thing)
                        continue;

                    JobDriver driver = pawn.jobs?.curDriver;
                    JobDriver_DoBill billDriver = driver as JobDriver_DoBill;
                    metadata["active"] = true;
                    metadata["workerId"] = pawn.thingIDNumber;
                    metadata["workerLabel"] = pawn.LabelShort ?? SafeThingLabel(pawn);
                    metadata["jobDef"] = job.def?.defName ?? "";
                    metadata["report"] = SafeJobReport(driver);

                    if (job.bill != null)
                    {
                        string billLabel = GetLabel(job.bill);
                        if (!string.IsNullOrEmpty(billLabel))
                            metadata["billLabel"] = billLabel;
                        if (job.bill.recipe != null)
                            metadata["recipeDef"] = job.bill.recipe.defName ?? "";
                    }

                    if (billDriver != null)
                    {
                        float workLeft = Mathf.Max(0f, billDriver.workLeft);
                        float workTotal = SafeBillWorkTotal(job);
                        float progress = workTotal > 0f ? Mathf.Clamp01(1f - workLeft / workTotal) : 0f;
                        if (workTotal <= 0f && billDriver.ticksSpentDoingRecipeWork > 0 && workLeft <= 0f)
                            progress = 1f;

                        metadata["workLeft"] = workLeft;
                        metadata["workTotal"] = workTotal;
                        metadata["progress"] = progress;
                        metadata["progressPercent"] = Mathf.RoundToInt(progress * 100f);
                        metadata["billStartTick"] = billDriver.billStartTick;
                        metadata["ticksSpentDoingRecipeWork"] = billDriver.ticksSpentDoingRecipeWork;
                        metadata["toilIndex"] = billDriver.CurToilIndex;
                        metadata["toil"] = billDriver.CurToilString ?? "";
                        metadata["ticksLeftThisToil"] = billDriver.ticksLeftThisToil;
                        if (billDriver.ActiveSkill != null)
                            metadata["activeSkill"] = billDriver.ActiveSkill.defName ?? "";
                    }

                    AddLocalTargetMetadata(metadata, "targetB", job.targetB);
                    return metadata;
                }
            }
            catch
            {
                return metadata;
            }

            return metadata;
        }

        public static string SummarizeActiveWorkbenchJob(Dictionary<string, object> metadata)
        {
            if (metadata == null || metadata.Count == 0 || !DetailBool(metadata, "active"))
                return "";

            string worker = DetailString(metadata, "workerLabel");
            string bill = DetailString(metadata, "billLabel");
            if (string.IsNullOrEmpty(bill))
                bill = DetailString(metadata, "recipeDef");
            int progress = DetailInt(metadata, "progressPercent", -1);

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(worker))
                parts.Add(worker);
            if (!string.IsNullOrEmpty(bill))
                parts.Add(bill);
            if (progress >= 0)
                parts.Add(progress + "%");

            return string.Join(" ", parts.ToArray());
        }

        public static string SafeThingDefName(Thing thing)
        {
            return thing?.def?.defName ?? "";
        }

        public static string SafeThingLabel(Thing thing)
        {
            if (thing == null)
                return "";

            try
            {
                string label = thing.LabelCap.ToString();
                return !string.IsNullOrEmpty(label) ? label : SafeThingDefName(thing);
            }
            catch
            {
                return SafeThingDefName(thing);
            }
        }

        public static int SafeThingRotation(Thing thing)
        {
            try
            {
                return thing != null ? thing.Rotation.AsInt : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsTacticalMapThingVisible(Thing thing, Pawn viewerPawn)
        {
            if (thing == null)
                return false;

            if (viewerPawn != null && thing == viewerPawn)
                return true;

            try
            {
                Map map = thing.Map ?? viewerPawn?.Map;
                if (map == null)
                    return true;

                if (IsTacticalMapCellVisible(map, thing.Position, viewerPawn))
                    return true;

                if (thing.def?.size.x > 1 || thing.def?.size.z > 1)
                {
                    foreach (IntVec3 cell in thing.OccupiedRect().Cells)
                    {
                        if (IsTacticalMapCellVisible(map, cell, viewerPawn))
                            return true;
                    }
                }
            }
            catch
            {
                return true;
            }

            return false;
        }

        public static bool IsTacticalMapCellVisible(Map map, IntVec3 cell, Pawn viewerPawn)
        {
            try
            {
                if (map == null)
                    return true;

                if (!cell.IsValid || !cell.InBounds(map))
                    return false;

                if (viewerPawn != null &&
                    viewerPawn.Map == map &&
                    viewerPawn.Spawned &&
                    viewerPawn.Position == cell)
                {
                    return true;
                }

                return map.fogGrid == null || !map.fogGrid.IsFogged(cell);
            }
            catch
            {
                return true;
            }
        }

        public static bool IsForbiddenForViewer(Thing thing, Pawn viewerPawn)
        {
            if (thing == null || viewerPawn == null)
                return false;

            try
            {
                return thing.IsForbidden(viewerPawn);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsReserved(Thing thing)
        {
            if (thing?.Map?.reservationManager == null)
                return false;

            try
            {
                if (thing.Map.reservationManager.IsReserved(thing))
                    return true;

                return FindReservationForThing(thing) != null;
            }
            catch
            {
                return false;
            }
        }

        public static ReservationMetadata SafeReservationMetadata(Thing thing)
        {
            var metadata = new ReservationMetadata();
            try
            {
                var reservation = FindReservationForThing(thing);
                if (reservation == null)
                    return metadata;

                metadata.Reserved = true;
                Pawn claimant = reservation.Claimant;
                if (claimant != null)
                {
                    metadata.ReservedById = claimant.thingIDNumber;
                    metadata.ReservedByLabel = claimant.LabelShort ?? SafeThingLabel(claimant);
                }

                metadata.ReservationJobDef = reservation.Job?.def?.defName ?? "";

                LocalTargetInfo target = reservation.Target;
                if (target.Thing != null)
                {
                    metadata.ReservationTargetId = target.Thing.thingIDNumber;
                }
                else if (target.IsValid && target.Cell.IsValid)
                {
                    metadata.HasReservationTargetCell = true;
                    metadata.ReservationTargetX = target.Cell.x;
                    metadata.ReservationTargetZ = target.Cell.z;
                }
            }
            catch
            {
                return metadata;
            }

            return metadata;
        }

        public static bool TryGetInteractionCell(Thing thing, out int x, out int z)
        {
            x = 0;
            z = 0;
            if (thing?.def?.hasInteractionCell != true)
                return false;

            try
            {
                IntVec3 cell = thing.InteractionCell;
                x = cell.x;
                z = cell.z;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsCellAllowedByCurrentArea(Pawn pawn, IntVec3 cell, out string areaLabel)
        {
            areaLabel = null;
            if (pawn?.playerSettings == null || pawn.Map == null)
                return true;

            if (OverlordMod.Settings?.enforceAreaRestrictions != true)
                return true;

            if (AreaRestrictionProperty == null)
                return true;

            try
            {
                var area = AreaRestrictionProperty.GetValue(pawn.playerSettings, null) as Area;
                if (area == null)
                    return true;

                areaLabel = area.Label;
                return cell.InBounds(pawn.Map) && area[cell];
            }
            catch
            {
                return true;
            }
        }

        public static bool TrySetWorkPriority(Pawn pawn, WorkTypeDef workType, int priority)
        {
            if (pawn?.workSettings == null || workType == null || WorkPriorityMethod == null)
                return false;

            try
            {
                WorkPriorityMethod.Invoke(pawn.workSettings, new object[] { workType, priority });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static List<Dictionary<string, object>> GetRelevantSkillSummaries(Pawn pawn, WorkTypeDef workType)
        {
            var result = new List<Dictionary<string, object>>();
            if (pawn?.skills == null || workType == null)
                return result;

            foreach (SkillDef skillDef in GetRelevantSkills(workType))
            {
                if (skillDef == null)
                    continue;

                try
                {
                    SkillRecord skill = pawn.skills.GetSkill(skillDef);
                    if (skill == null)
                        continue;

                    result.Add(new Dictionary<string, object>
                    {
                        ["name"] = skillDef.defName,
                        ["label"] = skillDef.skillLabel ?? skillDef.label ?? skillDef.defName,
                        ["level"] = skill.Level,
                        ["passion"] = (int)skill.passion,
                        ["disabled"] = skill.TotallyDisabled
                    });
                }
                catch
                {
                    // Missing or modded skill defs should not break the work panel.
                }
            }

            return result;
        }

        private static List<SkillDef> GetRelevantSkills(WorkTypeDef workType)
        {
            var result = new List<SkillDef>();
            if (workType == null)
                return result;

            object value = null;
            try
            {
                if (WorkTypeRelevantSkillsField != null)
                    value = WorkTypeRelevantSkillsField.GetValue(workType);
                else if (WorkTypeRelevantSkillsProperty != null && WorkTypeRelevantSkillsProperty.CanRead)
                    value = WorkTypeRelevantSkillsProperty.GetValue(workType, null);
            }
            catch
            {
                value = null;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (object entry in enumerable)
                {
                    if (entry is SkillDef skillDef)
                        result.Add(skillDef);
                }
            }

            return result;
        }

        public static bool TrySetScheduleAssignment(Pawn pawn, int hour, TimeAssignmentDef assignment)
        {
            if (pawn?.timetable == null || assignment == null || TimetableSetAssignmentMethod == null)
                return false;

            try
            {
                var parameters = TimetableSetAssignmentMethod.GetParameters();
                if (parameters.Length != 2)
                    return false;

                if (!parameters[1].ParameterType.IsAssignableFrom(typeof(TimeAssignmentDef)) &&
                    parameters[1].ParameterType != typeof(TimeAssignmentDef))
                {
                    return false;
                }

                TimetableSetAssignmentMethod.Invoke(pawn.timetable, new object[] { hour, assignment });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TrySetOutfitPolicy(Pawn pawn, object policy)
        {
            return TrySetPropertyValue(pawn?.outfits, OutfitPolicyProperty, policy);
        }

        public static bool TrySetDrugPolicy(Pawn pawn, object policy)
        {
            return TrySetPropertyValue(pawn?.drugs, DrugPolicyProperty, policy);
        }

        public static bool TrySetFoodPolicy(Pawn pawn, object policy)
        {
            return TrySetPropertyValue(pawn?.foodRestriction, FoodPolicyProperty, policy);
        }

        public static bool TrySetAreaRestriction(Pawn pawn, Area area)
        {
            return TrySetPropertyValue(pawn?.playerSettings, AreaRestrictionProperty, area);
        }

        public static string GetCurrentOutfitLabel(Pawn pawn)
        {
            return GetLabelFromProperty(pawn?.outfits, OutfitPolicyProperty);
        }

        public static string GetCurrentDrugPolicyLabel(Pawn pawn)
        {
            return GetLabelFromProperty(pawn?.drugs, DrugPolicyProperty);
        }

        public static string GetCurrentFoodPolicyLabel(Pawn pawn)
        {
            return GetLabelFromProperty(pawn?.foodRestriction, FoodPolicyProperty);
        }

        public static string GetCurrentAreaRestrictionLabel(Pawn pawn)
        {
            if (pawn?.playerSettings == null || AreaRestrictionProperty == null)
                return null;

            try
            {
                var area = AreaRestrictionProperty.GetValue(pawn.playerSettings, null);
                return GetLabel(area);
            }
            catch
            {
                return null;
            }
        }

        public static RenderTexture GetPortraitTexture(Pawn pawn, Vector2 size, Rot4 rotation)
        {
            if (pawn == null || PortraitGetMethod == null)
                return null;

            try
            {
                var parameters = PortraitGetMethod.GetParameters();
                object[] args;

                if (parameters.Length == 5 &&
                    parameters[3].ParameterType == typeof(Vector3) &&
                    parameters[4].ParameterType == typeof(float))
                {
                    args = new object[] { pawn, size, rotation, default(Vector3), 1f };
                }
                else if (parameters.Length == 5 &&
                         parameters[3].ParameterType == typeof(Color[]) &&
                         parameters[4].ParameterType == typeof(bool))
                {
                    args = new object[] { pawn, size, rotation, null, true };
                }
                else
                {
                    return null;
                }

                return PortraitGetMethod.Invoke(null, args) as RenderTexture;
            }
            catch
            {
                return null;
            }
        }

        public static bool TryGetColonistBarDrawData(out List<Pawn> pawns, out List<Vector2> drawLocs, out float portraitSize)
        {
            pawns = null;
            drawLocs = null;
            portraitSize = 0f;

            var bar = Find.ColonistBar;
            if (bar == null || !SupportsColonistBarOverlay)
                return false;

            try
            {
                var entries = ColonistBarEntriesProperty.GetValue(bar, null) as IEnumerable;
                var locs = ColonistBarDrawLocsProperty.GetValue(bar, null) as IEnumerable;
                if (entries == null || locs == null)
                    return false;

                pawns = new List<Pawn>();
                foreach (var entry in entries)
                    pawns.Add(ColonistBarEntryPawnAccessor(entry));

                drawLocs = new List<Vector2>();
                foreach (var loc in locs)
                {
                    if (loc is Vector2 vec)
                        drawLocs.Add(vec);
                }

                var sizeValue = ColonistBarSizeProperty.GetValue(bar, null);
                if (sizeValue is Vector2 sizeVec)
                    portraitSize = sizeVec.x;
                else if (sizeValue is float sizeFloat)
                    portraitSize = sizeFloat;

                return pawns.Count > 0 && drawLocs.Count > 0 && portraitSize > 0f;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetContextOptions(Pawn pawn, Vector3 clickPos, out List<FloatMenuOption> options)
        {
            options = null;

            if (pawn == null || FloatMenuGetOptionsMethod == null)
                return false;

            try
            {
                object[] args = { new List<Pawn> { pawn }, clickPos, null };
                IEnumerable result;
                using (TemporarilySetCurrentMap(pawn.Map))
                {
                    result = FloatMenuGetOptionsMethod.Invoke(null, args) as IEnumerable;
                }
                if (result == null)
                    return false;

                options = new List<FloatMenuOption>();
                foreach (var entry in result)
                {
                    if (entry is FloatMenuOption option)
                        options.Add(option);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly FieldInfo FloatMenuOptionTooltipField =
            typeof(FloatMenuOption).GetField("tooltip", BindingFlags.Public | BindingFlags.Instance);
        private static readonly FieldInfo FloatMenuOptionIconThingField =
            typeof(FloatMenuOption).GetField("iconThing", BindingFlags.Public | BindingFlags.Instance);
        private static readonly FieldInfo FloatMenuOptionShownItemField =
            typeof(FloatMenuOption).GetField("shownItem", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FloatMenuOptionOrderInPriorityField =
            typeof(FloatMenuOption).GetField("orderInPriority", BindingFlags.Public | BindingFlags.Instance);
        private static readonly PropertyInfo FloatMenuOptionPriorityProperty =
            typeof(FloatMenuOption).GetProperty("Priority", BindingFlags.Public | BindingFlags.Instance);

        public static int SafeReadOptionPriority(FloatMenuOption option)
        {
            if (option == null) return -1;
            try
            {
                if (FloatMenuOptionPriorityProperty != null)
                {
                    var raw = FloatMenuOptionPriorityProperty.GetValue(option, null);
                    if (raw != null)
                        return Convert.ToInt32(raw);
                }
            }
            catch { }
            return option.Disabled ? 0 : 4;
        }

        public static Dictionary<string, object> BuildContextOptionMetadata(FloatMenuOption option, int id)
        {
            var entry = new Dictionary<string, object>
            {
                ["id"] = id,
                ["label"] = option?.Label ?? "?",
                ["disabled"] = option?.Disabled ?? true
            };

            if (option == null)
                return entry;

            int priorityRank = 4;
            string priorityName = "Default";
            try
            {
                if (FloatMenuOptionPriorityProperty != null)
                {
                    var raw = FloatMenuOptionPriorityProperty.GetValue(option, null);
                    if (raw != null)
                    {
                        priorityName = raw.ToString();
                        priorityRank = Convert.ToInt32(raw);
                    }
                }
            }
            catch { }
            entry["priority"] = priorityRank;
            entry["priorityName"] = priorityName;

            int orderInPriority = 0;
            try
            {
                if (FloatMenuOptionOrderInPriorityField != null)
                    orderInPriority = Convert.ToInt32(FloatMenuOptionOrderInPriorityField.GetValue(option));
            }
            catch { }
            entry["orderInPriority"] = orderInPriority;

            string tooltipText = TryReadTooltipText(option);
            if (!string.IsNullOrEmpty(tooltipText))
                entry["tooltip"] = tooltipText;

            string disabledReason = ExtractDisabledReason(option, tooltipText);
            if (option.Disabled && !string.IsNullOrEmpty(disabledReason))
                entry["disabledReason"] = disabledReason;

            ThingDef iconDef = TryReadIconDef(option);
            if (iconDef != null)
            {
                entry["iconDefName"] = iconDef.defName;
                if (!string.IsNullOrEmpty(iconDef.label))
                    entry["iconLabel"] = iconDef.label;
            }

            return entry;
        }

        private static string TryReadTooltipText(FloatMenuOption option)
        {
            try
            {
                if (FloatMenuOptionTooltipField == null)
                    return null;
                var raw = FloatMenuOptionTooltipField.GetValue(option);
                if (raw == null)
                    return null;
                if (raw is TipSignal tip)
                    return ResolveTipSignalText(tip);
                if (raw.GetType().IsGenericType && raw.GetType().GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    var hasValueProp = raw.GetType().GetProperty("HasValue");
                    var valueProp = raw.GetType().GetProperty("Value");
                    if (hasValueProp != null && valueProp != null && (bool)hasValueProp.GetValue(raw, null))
                    {
                        var tipValue = valueProp.GetValue(raw, null);
                        if (tipValue is TipSignal nested)
                            return ResolveTipSignalText(nested);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string ResolveTipSignalText(TipSignal tip)
        {
            try
            {
                if (!string.IsNullOrEmpty(tip.text))
                    return tip.text;
                if (tip.textGetter != null)
                    return tip.textGetter();
            }
            catch { }
            return null;
        }

        private static string ExtractDisabledReason(FloatMenuOption option, string tooltipText)
        {
            if (!option.Disabled)
                return null;

            // Vanilla RimWorld convention: disabled options often embed the reason in the
            // label as "Cannot do thing: reason" or "Cannot do thing (reason)".
            string label = option.Label ?? string.Empty;
            int colon = label.IndexOf(':');
            if (colon > 0 && colon < label.Length - 1)
                return label.Substring(colon + 1).Trim();
            int paren = label.IndexOf('(');
            int closeParen = label.LastIndexOf(')');
            if (paren > 0 && closeParen > paren + 1)
                return label.Substring(paren + 1, closeParen - paren - 1).Trim();
            if (!string.IsNullOrEmpty(tooltipText))
                return tooltipText.Trim();
            return null;
        }

        private static ThingDef TryReadIconDef(FloatMenuOption option)
        {
            try
            {
                if (FloatMenuOptionShownItemField != null)
                {
                    if (FloatMenuOptionShownItemField.GetValue(option) is ThingDef shown)
                        return shown;
                }
                if (FloatMenuOptionIconThingField != null)
                {
                    if (FloatMenuOptionIconThingField.GetValue(option) is Thing iconThing && iconThing.def != null)
                        return iconThing.def;
                }
            }
            catch { }
            return null;
        }

        public static IDisposable TemporarilySetCurrentMap(Map map)
        {
            var game = Current.Game;
            if (game == null || map == null || game.Maps == null)
                return new NoopScope();

            int index = game.Maps.IndexOf(map);
            if (index < 0 || index > sbyte.MaxValue)
                return new NoopScope();

            sbyte previous = game.currentMapIndex;
            game.currentMapIndex = (sbyte)index;
            return new CurrentMapScope(game, previous);
        }

        private static bool TrySetPropertyValue(object target, PropertyInfo property, object value)
        {
            if (target == null || property == null || !property.CanWrite)
                return false;

            try
            {
                property.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, object> BuildBillDetail(Bill bill)
        {
            var detail = new Dictionary<string, object>();
            if (bill == null)
                return detail;

            try
            {
                string label = GetLabel(bill);
                if (!string.IsNullOrEmpty(label))
                    detail["label"] = label;
                if (bill.recipe != null)
                {
                    detail["recipeDef"] = bill.recipe.defName ?? "";
                    if (bill.recipe.workSkill != null)
                        detail["workSkill"] = bill.recipe.workSkill.defName ?? "";
                }

                detail["suspended"] = bill.suspended;
                detail["shouldDoNow"] = SafeBillShouldDoNow(bill);
                detail["ingredientSearchRadius"] = bill.ingredientSearchRadius;
                detail["ingredientFilterSummary"] = SafeThingFilterSummary(bill.ingredientFilter);
                detail["ingredientAllowedDefCount"] = bill.ingredientFilter?.AllowedDefCount ?? 0;
                detail["skillMin"] = bill.allowedSkillRange.min;
                detail["skillMax"] = bill.allowedSkillRange.max;
                if (bill.PawnRestriction != null)
                {
                    detail["pawnRestrictionId"] = bill.PawnRestriction.thingIDNumber;
                    detail["pawnRestrictionLabel"] = bill.PawnRestriction.LabelShort ?? SafeThingLabel(bill.PawnRestriction);
                }
                if (bill.SlavesOnly)
                    detail["workerRestriction"] = "slaves";
                else if (bill.MechsOnly)
                    detail["workerRestriction"] = "mechs";
                else if (bill.NonMechsOnly)
                    detail["workerRestriction"] = "nonMechs";

                var production = bill as Bill_Production;
                if (production != null)
                {
                    detail["repeatMode"] = production.repeatMode?.defName ?? "";
                    detail["repeatInfo"] = SafeBillRepeatInfo(production);
                    detail["repeatCount"] = production.repeatCount;
                    detail["targetCount"] = production.targetCount;
                    detail["productCount"] = SafeBillProductCount(production);
                    detail["paused"] = production.paused;
                    detail["pauseWhenSatisfied"] = production.pauseWhenSatisfied;
                    detail["unpauseWhenYouHave"] = production.unpauseWhenYouHave;
                    detail["includeEquipped"] = production.includeEquipped;
                    detail["includeTainted"] = production.includeTainted;
                    detail["limitToAllowedStuff"] = production.limitToAllowedStuff;
                    detail["hpMin"] = production.hpRange.min;
                    detail["hpMax"] = production.hpRange.max;
                    detail["qualityMin"] = production.qualityRange.min.ToString();
                    detail["qualityMax"] = production.qualityRange.max.ToString();
                }
            }
            catch
            {
                return detail;
            }

            return detail;
        }

        private static bool SafeBillShouldDoNow(Bill bill)
        {
            try
            {
                return bill?.ShouldDoNow() == true;
            }
            catch
            {
                return false;
            }
        }

        private static string SafeBillRepeatInfo(Bill_Production bill)
        {
            try
            {
                return bill?.RepeatInfoText ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static int SafeBillProductCount(Bill_Production bill)
        {
            try
            {
                if (bill?.repeatMode != BillRepeatModeDefOf.TargetCount || bill.recipe?.WorkerCounter == null)
                    return -1;
                return bill.recipe.WorkerCounter.CountProducts(bill);
            }
            catch
            {
                return -1;
            }
        }

        private static float SafeBillWorkTotal(Job job)
        {
            try
            {
                Thing target = job?.targetB.Thing;
                return job?.bill?.recipe?.WorkAmountTotal(target) ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static string SafeJobReport(JobDriver driver)
        {
            try
            {
                return driver?.GetReport() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static void AddLocalTargetMetadata(Dictionary<string, object> metadata, string prefix, LocalTargetInfo target)
        {
            if (metadata == null || string.IsNullOrEmpty(prefix) || !target.IsValid)
                return;

            try
            {
                Thing thing = target.Thing;
                if (thing != null)
                {
                    metadata[prefix + "Id"] = thing.thingIDNumber;
                    metadata[prefix + "Def"] = SafeThingDefName(thing);
                    metadata[prefix + "Label"] = SafeThingLabel(thing);
                }

                IntVec3 cell = target.Cell;
                if (cell.IsValid)
                {
                    metadata[prefix + "X"] = cell.x;
                    metadata[prefix + "Z"] = cell.z;
                }
            }
            catch
            {
            }
        }

        private static string SafeThingFilterSummary(ThingFilter filter)
        {
            try
            {
                return filter?.Summary ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string DetailString(Dictionary<string, object> detail, string key)
        {
            if (detail == null || !detail.TryGetValue(key, out object value) || value == null)
                return "";
            return value.ToString();
        }

        private static bool DetailBool(Dictionary<string, object> detail, string key)
        {
            if (detail == null || !detail.TryGetValue(key, out object value) || value == null)
                return false;
            return value is bool boolValue && boolValue;
        }

        private static int DetailInt(Dictionary<string, object> detail, string key, int fallback = 0)
        {
            if (detail == null || !detail.TryGetValue(key, out object value) || value == null)
                return fallback;
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static ReservationManager.Reservation FindReservationForThing(Thing thing)
        {
            if (thing?.Map?.reservationManager?.ReservationsReadOnly == null)
                return null;

            try
            {
                LocalTargetInfo thingTarget = thing;
                foreach (var reservation in thing.Map.reservationManager.ReservationsReadOnly)
                {
                    if (reservation == null)
                        continue;
                    if (ReservationTargetsThing(reservation.Target, thing, thingTarget))
                        return reservation;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool ReservationTargetsThing(LocalTargetInfo target, Thing thing, LocalTargetInfo thingTarget)
        {
            if (thing == null || !target.IsValid)
                return false;

            try
            {
                if (target == thingTarget || target.Thing == thing)
                    return true;

                if (thing is Building building && building.def?.hasInteractionCell == true)
                {
                    IntVec3 interactionCell = building.InteractionCell;
                    if (target.Thing == null && target.Cell == interactionCell)
                        return true;

                    Building edifice = interactionCell.GetEdifice(thing.Map);
                    if (edifice != null && target.Thing == edifice)
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static float ReadFloatMember(object target, params string[] names)
        {
            if (target == null || names == null)
                return 0f;

            Type type = target.GetType();
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                try
                {
                    var field = type.GetField(name, InstanceFlags | StaticFlags);
                    if (field != null)
                        return Convert.ToSingle(field.GetValue(target));

                    var property = ResolveProperty(type, name);
                    if (property != null && property.CanRead)
                        return Convert.ToSingle(property.GetValue(target, null));
                }
                catch
                {
                    continue;
                }
            }

            return 0f;
        }

        private static bool ReadBoolMember(object target, params string[] names)
        {
            object value = ReadMember(target, names);
            if (value is bool boolValue)
                return boolValue;
            return false;
        }

        private static object ReadMember(object target, params string[] names)
        {
            if (target == null || names == null)
                return null;

            Type type = target.GetType();
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                try
                {
                    var field = type.GetField(name, InstanceFlags | StaticFlags);
                    if (field != null)
                        return field.GetValue(target);

                    var property = ResolveProperty(type, name);
                    if (property != null && property.CanRead)
                        return property.GetValue(target, null);
                }
                catch
                {
                    continue;
                }
            }

            return null;
        }

        private static int CountEnumerable(object value)
        {
            if (value == null)
                return 0;
            if (value is ICollection collection)
                return collection.Count;
            if (value is IEnumerable enumerable)
            {
                int count = 0;
                foreach (object _ in enumerable)
                    count++;
                return count;
            }
            return 0;
        }

        private static string GetLabelFromProperty(object target, PropertyInfo property)
        {
            if (target == null || property == null || !property.CanRead)
                return null;

            try
            {
                return GetLabel(property.GetValue(target, null));
            }
            catch
            {
                return null;
            }
        }

        private static string GetLabel(object value)
        {
            if (value == null)
                return null;

            var type = value.GetType();
            var labelProp = ResolveProperty(type, "label", "Label");
            try
            {
                return labelProp?.GetValue(value, null)?.ToString() ?? value.ToString();
            }
            catch
            {
                return value.ToString();
            }
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrEmpty(text) || needles == null)
                return false;

            foreach (string needle in needles)
            {
                if (!string.IsNullOrEmpty(needle) && text.Contains(needle))
                    return true;
            }

            return false;
        }

        private static PropertyInfo ResolveProperty(Type type, params string[] names)
        {
            if (type == null || names == null)
                return null;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                var prop = type.GetProperty(name, InstanceFlags | StaticFlags);
                if (prop != null)
                    return prop;
            }

            return null;
        }

        private static FieldInfo ResolveField(Type type, params string[] names)
        {
            if (type == null || names == null)
                return null;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                var field = type.GetField(name, InstanceFlags | StaticFlags);
                if (field != null)
                    return field;
            }

            return null;
        }

        private static MethodInfo ResolveMethod(Type type, string name, params Type[] parameterTypes)
        {
            return type?.GetMethod(name, InstanceFlags | StaticFlags, null, parameterTypes, null);
        }

        private static MethodInfo ResolveTimetableSetAssignmentMethod()
        {
            return typeof(Pawn_TimetableTracker)
                .GetMethods(InstanceFlags)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "SetAssignment")
                        return false;

                    var parameters = m.GetParameters();
                    return parameters.Length == 2 && parameters[0].ParameterType == typeof(int);
                });
        }

        private static MethodInfo ResolvePortraitGetMethod()
        {
            return typeof(PortraitsCache)
                .GetMethods(StaticFlags)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "Get")
                        return false;

                    if (!typeof(RenderTexture).IsAssignableFrom(m.ReturnType))
                        return false;

                    var parameters = m.GetParameters();
                    return parameters.Length >= 3 &&
                           parameters[0].ParameterType == typeof(Pawn) &&
                           parameters[1].ParameterType == typeof(Vector2) &&
                           parameters[2].ParameterType == typeof(Rot4);
                });
        }

        private static Func<object, Pawn> ResolveColonistBarEntryPawnAccessor()
        {
            var entriesProperty = ResolveProperty(typeof(ColonistBar), "Entries");
            var listType = entriesProperty?.PropertyType;
            if (listType == null || !listType.IsGenericType)
                return null;

            var entryType = listType.GetGenericArguments()[0];
            var pawnProperty = ResolveProperty(entryType, "pawn", "Pawn");
            if (pawnProperty != null)
                return entry => pawnProperty.GetValue(entry, null) as Pawn;

            var pawnField = entryType.GetField("pawn", InstanceFlags) ?? entryType.GetField("Pawn", InstanceFlags);
            if (pawnField != null)
                return entry => pawnField.GetValue(entry) as Pawn;

            return null;
        }

        private static MethodInfo ResolveFloatMenuGetOptionsMethod()
        {
            return typeof(FloatMenuMakerMap)
                .GetMethods(StaticFlags)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "GetOptions")
                        return false;

                    var parameters = m.GetParameters();
                    if (parameters.Length != 3)
                        return false;

                    if (parameters[1].ParameterType != typeof(Vector3))
                        return false;

                    return parameters[2].IsOut;
                });
        }
    }
}
