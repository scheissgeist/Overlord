using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Overlord
{
    /// <summary>
    /// Dispatches incoming viewer commands to the correct pawn API.
    /// Checks permissions before executing. Returns result message.
    /// </summary>
    public static class PawnCommandRouter
    {
        // ── Auto-undraft ────────────────────────────────────────────────────────
        // Move/attack commands auto-draft the pawn so the ordered job can't be
        // interrupted, but nothing ever undrafted them — a drafted pawn finishes the
        // order and then stands at attention forever (drafted pawns take no work
        // jobs). Track pawns WE drafted; once such a pawn has been idle in
        // Wait_Combat with an empty job queue for a few sync cycles, undraft it so
        // it resumes normal life. An explicit viewer Draft/Undraft command takes
        // manual control and clears the flag.
        private static readonly HashSet<int> autoDraftedPawns = new HashSet<int>();
        private static readonly Dictionary<int, int> autoDraftIdleCycles = new Dictionary<int, int>();
        private const int AutoUndraftIdleCycles = 12; // ~2s at the ~6/s sync cadence

        private static void MarkAutoDrafted(Pawn pawn)
        {
            autoDraftedPawns.Add(pawn.thingIDNumber);
            autoDraftIdleCycles.Remove(pawn.thingIDNumber);
        }

        private static void ClearAutoDraft(Pawn pawn)
        {
            autoDraftedPawns.Remove(pawn.thingIDNumber);
            autoDraftIdleCycles.Remove(pawn.thingIDNumber);
        }

        public static void ClearAutoDraftState()
        {
            autoDraftedPawns.Clear();
            autoDraftIdleCycles.Clear();
        }

        /// <summary>
        /// Called once per sync cycle per assigned pawn (ViewerManager.Tick). Undrafts
        /// a pawn that a viewer command auto-drafted once it has clearly finished the
        /// ordered action (standing in Wait_Combat, nothing queued) for ~2 seconds.
        /// The delay keeps a pawn drafted through combat chains (attack → next target).
        /// </summary>
        public static void MaybeAutoUndraft(Pawn pawn)
        {
            if (pawn == null)
                return;

            int id = pawn.thingIDNumber;
            if (!autoDraftedPawns.Contains(id))
                return;

            if (pawn.Dead || pawn.Destroyed || pawn.drafter == null || !pawn.Drafted)
            {
                ClearAutoDraft(pawn);
                return;
            }

            bool idle = pawn.jobs?.curJob?.def == JobDefOf.Wait_Combat &&
                        (pawn.jobs.jobQueue == null || pawn.jobs.jobQueue.Count == 0);
            if (!idle)
            {
                autoDraftIdleCycles.Remove(id);
                return;
            }

            autoDraftIdleCycles.TryGetValue(id, out int cycles);
            cycles++;
            if (cycles < AutoUndraftIdleCycles)
            {
                autoDraftIdleCycles[id] = cycles;
                return;
            }

            pawn.drafter.Drafted = false;
            ClearAutoDraft(pawn);
            LogUtil.Log($"Auto-undrafted {pawn.LabelShort} (command order finished)");
        }

        /// <summary>
        /// Route a command JSON to the assigned pawn.
        /// Returns a result dict to send back to the viewer.
        /// </summary>
        public static Dictionary<string, object> Execute(string json, ViewerManager viewerManager)
        {
            string username = JsonHelper.ExtractLastString(json, "username");
            string action = JsonHelper.ExtractLastString(json, "action");
            bool isAdminCommand =
                JsonHelper.ExtractLastBool(json, "adminCommand", false) &&
                JsonHelper.ExtractLastString(json, "source") == "admin";

            if (action == null || (!isAdminCommand && username == null))
                return ErrorResult("Missing username or action");

            if (action == StateProtocol.CmdSpawnColonist)
            {
                if (!isAdminCommand)
                    return ErrorResult("Only the streamer can spawn colonists");
                return ExecuteSpawnColonist(username, json, viewerManager);
            }

            if (action == "start_vote")
            {
                if (!isAdminCommand) return ErrorResult("Only the streamer can start votes");
                return ExecuteStartVote(json);
            }
            if (action == "end_vote")
            {
                if (!isAdminCommand) return ErrorResult("Only the streamer can end votes");
                return ExecuteEndVote();
            }
            if (action == "ban")
            {
                if (!isAdminCommand) return ErrorResult("Admin only");
                return ExecuteBan(username, json, viewerManager);
            }
            if (action == "timeout")
            {
                if (!isAdminCommand) return ErrorResult("Admin only");
                return ExecuteTimeout(username, json, viewerManager);
            }
            if (action == "grant_ticket")
            {
                if (!isAdminCommand) return ErrorResult("Admin only");
                return ExecuteGrantTicket(username, viewerManager);
            }

            // Banned viewers see an error on every command
            if (viewerManager.IsBanned(username))
                return ErrorResult("You are banned from this stream");

            var session = viewerManager.GetSession(username);
            if (session == null)
                return ErrorResult("Not connected");

            if (action == StateProtocol.CmdCameraZoom)
                return ExecuteCameraZoom(session, json);

            // Timeout: reject all commands until expiry
            if (viewerManager.IsInTimeout(username, out int timeoutSecondsLeft))
                return ErrorResult($"Timed out — {timeoutSecondsLeft}s remaining");

            // Rate limiting. State toggles like Draft should not consume the
            // movement cooldown; viewers naturally hit Draft -> Move quickly.
            if (ShouldThrottleCommand(action))
            {
                int cooldownTicks = OverlordMod.Settings?.commandCooldownTicks ?? 20;
                int now = Find.TickManager.TicksGame;
                if (now - session.lastCommandTick < cooldownTicks)
                {
                    float secondsLeft = (cooldownTicks - (now - session.lastCommandTick)) / 60f;
                    return ErrorResult($"Too fast — wait {secondsLeft:F1}s");
                }
                session.lastCommandTick = now;
                session.lastCommandLabel = action;
            }

            // Commands allowed without a pawn
            if (action == StateProtocol.CmdRespawn)
                return ExecuteRespawn(username, json);
            if (action == StateProtocol.CmdToolkitRefresh)
                return ExecuteToolkitRefresh(username);
            if (action == StateProtocol.CmdToolkitPurchase)
                return ExecuteToolkitPurchase(username, json);
            if (action == StateProtocol.CmdClaimColonist)
                return ExecuteClaimColonist(username, json, viewerManager);
            if (action == StateProtocol.CmdVote)
                return ExecuteVote(username, json);
            if (action == StateProtocol.CmdTriggerEvent)
                return ExecuteTriggerEvent(username, json);

            if (!session.HasPawn)
                return ErrorResult("No pawn assigned");

            if (action == StateProtocol.CmdPreviewAppearance)
                return ExecutePreviewAppearance(session, json);
            if (action == StateProtocol.CmdSetAppearance)
                return ExecuteSetAppearance(session, json);

            // Permission check
            if (!session.permissions.IsAllowed(action))
                return ErrorResult($"Action '{action}' not permitted");

            var pawn = session.assignedPawn;

            switch (action)
            {
                case StateProtocol.CmdDraft:
                    return ExecuteDraft(pawn, true);

                case StateProtocol.CmdUndraft:
                    return ExecuteDraft(pawn, false);

                case StateProtocol.CmdMove:
                    return ExecuteMove(pawn, json);

                case StateProtocol.CmdAttack:
                    return ExecuteAttack(pawn, json);

                case StateProtocol.CmdSetWork:
                    return ExecuteSetWork(pawn, json);

                case StateProtocol.CmdSetSchedule:
                    return ExecuteSetSchedule(pawn, json);

                case StateProtocol.CmdSetOutfit:
                    return ExecuteSetPolicy(pawn, json, "outfit");

                case StateProtocol.CmdSetDrugPolicy:
                    return ExecuteSetPolicy(pawn, json, "drugPolicy");

                case StateProtocol.CmdSetFoodPolicy:
                    return ExecuteSetPolicy(pawn, json, "foodPolicy");

                case StateProtocol.CmdSetArea:
                    return ExecuteSetArea(pawn, json);

                case StateProtocol.CmdEquip:
                    return ExecuteEquip(pawn, json);

                case StateProtocol.CmdDrop:
                    return ExecuteDrop(pawn, json);

                case StateProtocol.CmdDyeApparel:
                    return ExecuteDyeApparel(session, pawn, json);

                case StateProtocol.CmdHostileResponse:
                    return ExecuteHostileResponse(pawn, json);

                case StateProtocol.CmdConsume:
                    return ExecuteConsume(pawn, json);

                case StateProtocol.CmdDropInventory:
                    return ExecuteDropInventory(pawn, json);

                case StateProtocol.CmdContextMenu:
                    return ExecuteContextMenu(pawn, json, viewerManager);

                case StateProtocol.CmdContextAction:
                    return ExecuteContextAction(pawn, json);

                case StateProtocol.CmdSocialInteract:
                    return ExecuteSocialInteract(pawn, json);

                default:
                    return ErrorResult($"Unknown action: {action}");
            }
        }

        private static Dictionary<string, object> ExecuteCameraZoom(ViewerSession session, string json)
        {
            float zoom = JsonHelper.ExtractFloat(json, "zoom", session.cameraZoom > 0f ? session.cameraZoom : 1f);
            session.cameraZoom = Mathf.Clamp(zoom, 0.45f, 5f);

            // Optional viewer-reported viewport aspect (width/height). Lets the
            // host render to the viewer's actual aspect instead of a fixed 16:9,
            // killing the black side gutters on portrait/ultrawide windows.
            float aspect = JsonHelper.ExtractFloat(json, "aspect", 0f);
            if (aspect > 0f)
                session.viewportAspect = Mathf.Clamp(aspect, 0.5f, 3f);

            int pixelHeight = JsonHelper.ExtractInt(json, "pixelHeight", 0);
            if (pixelHeight > 0)
                session.viewportHeight = Mathf.Clamp(pixelHeight, 360, 1440);

            bool followPawn = JsonHelper.ExtractBool(json, "followPawn", false);
            if (followPawn)
            {
                session.cameraHasCenter = false;
                session.cameraCenterX = 0f;
                session.cameraCenterZ = 0f;
            }
            else
            {
                float centerX = JsonHelper.ExtractFloat(json, "centerX", -1f);
                float centerZ = JsonHelper.ExtractFloat(json, "centerZ", -1f);
                if (centerX >= 0f && centerZ >= 0f)
                {
                    session.cameraCenterX = centerX;
                    session.cameraCenterZ = centerZ;
                    session.cameraHasCenter = true;
                }
            }

            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = true,
                ["silent"] = true,
                ["zoom"] = session.cameraZoom,
                ["followPawn"] = !session.cameraHasCenter,
                ["centerX"] = session.cameraCenterX,
                ["centerZ"] = session.cameraCenterZ,
                ["pixelHeight"] = session.viewportHeight,
                ["message"] = "Camera zoom updated"
            };
        }

        private static Dictionary<string, object> ExecuteRespawn(string username, string json)
        {
            int portalId = JsonHelper.ExtractInt(json, "portalId", -1);

            Building portal = null;
            if (portalId >= 0)
            {
                var map = Find.CurrentMap;
                if (map != null)
                {
                    var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == portalId);
                    portal = thing as Building;
                }
            }

            // Fall back to any portal on the current map
            if (portal == null)
            {
                var map = Find.CurrentMap;
                if (map != null)
                {
                    portal = map.listerBuildings.AllBuildingsColonistOfDef(
                        DefDatabase<ThingDef>.GetNamed("Overlord_RespawnPortal", errorOnFail: false)
                    )?.FirstOrDefault() as Building;
                }
            }

            string result = RespawnManager.TryRespawn(username, portal);
            bool success = result.StartsWith("Respawned");
            return new Dictionary<string, object>
            {
                ["type"]    = StateProtocol.ActionResult,
                ["ok"]      = success,
                ["message"] = result
            };
        }

        private static Dictionary<string, object> ExecuteToolkitRefresh(string username)
        {
            OverlordGameComponent.Instance?.SendToolkitStatePublic(username);
            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = true,
                ["silent"] = true,
                ["message"] = "Toolkit state refreshed"
            };
        }

        private static Dictionary<string, object> ExecuteToolkitPurchase(string username, string json)
        {
            string purchase = JsonHelper.ExtractString(json, "purchase");
            int quantity = JsonHelper.ExtractInt(json, "quantity", 1);
            string argument = JsonHelper.ExtractString(json, "argument");
            bool equipToPawn = JsonHelper.ExtractBool(json, "equipToPawn", false);
            var session = OverlordGameComponent.Instance?.Viewers?.GetSession(username);
            Pawn targetPawn = session != null && session.HasPawn ? session.assignedPawn : null;
            var result = TwitchToolkitBridge.ExecutePurchase(username, purchase, quantity, argument, targetPawn, equipToPawn);
            // Always push a fresh wallet/store snapshot so Buy UI coins/affordability update.
            OverlordGameComponent.Instance?.SendToolkitStatePublic(username);
            if (result != null && !result.ContainsKey("action"))
                result["action"] = StateProtocol.CmdToolkitPurchase;
            return result;
        }

        private static Dictionary<string, object> ExecuteSetAppearance(ViewerSession session, string json)
        {
            var pawn = session?.assignedPawn;
            if (!CanUseAppearance(session, pawn, out string error))
                return ErrorResult(error);
            if (!TryResolveAppearanceChoice(pawn, json, out HairDef hair, out Gender gender, out error))
                return ErrorResult(error);

            bool changed = false;
            if (hair != null && pawn.story.hairDef != hair)
            {
                pawn.story.hairDef = hair;
                changed = true;
            }

            if (gender != Gender.None && pawn.gender != gender)
            {
                pawn.gender = gender;
                changed = true;
            }

            if (!changed)
                return SuccessResult("Appearance already matches");

            session.freeAppearanceUsed = true;
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();
            PortraitsCache.SetDirty(pawn);
            Find.ColonistBar?.MarkColonistsDirty();

            var comp = OverlordGameComponent.Instance;
            comp?.InvalidatePawnPortrait(pawn);
            if (!string.IsNullOrEmpty(session.username))
            {
                comp?.Viewers?.SendPermissions(session.username);
                comp?.HandleRequestStatePublic(session.username);
            }

            return SuccessResult("Appearance updated");
        }

        private static Dictionary<string, object> ExecutePreviewAppearance(ViewerSession session, string json)
        {
            var pawn = session?.assignedPawn;
            if (!CanUseAppearance(session, pawn, out string error))
                return ErrorResult(error);
            if (!TryResolveAppearanceChoice(pawn, json, out HairDef hair, out Gender gender, out error))
                return ErrorResult(error);

            HairDef oldHair = pawn.story.hairDef;
            Gender oldGender = pawn.gender;

            try
            {
                if (hair != null)
                    pawn.story.hairDef = hair;
                if (gender != Gender.None)
                    pawn.gender = gender;

                pawn.Drawer?.renderer?.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(pawn);

                string portrait = PortraitRenderer.GetPortraitBase64(pawn);
                if (string.IsNullOrEmpty(portrait))
                    return ErrorResult("Appearance preview unavailable");

                string hairLabel = pawn.story.hairDef?.label ?? pawn.story.hairDef?.defName ?? "";
                return new Dictionary<string, object>
                {
                    ["type"] = StateProtocol.ActionResult,
                    ["ok"] = true,
                    ["message"] = "Preview ready",
                    ["appearancePreview"] = portrait,
                    ["previewLabel"] = "Preview",
                    ["previewHair"] = hairLabel,
                    ["previewGender"] = pawn.gender.ToString()
                };
            }
            finally
            {
                pawn.story.hairDef = oldHair;
                pawn.gender = oldGender;
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(pawn);
                Find.ColonistBar?.MarkColonistsDirty();
            }
        }

        private static bool CanUseAppearance(ViewerSession session, Pawn pawn, out string error)
        {
            error = null;
            if (pawn == null || pawn.story == null)
            {
                error = "Pawn appearance is not available";
                return false;
            }
            if (pawn.RaceProps?.Humanlike != true)
            {
                error = "Only humanlike pawns can change hairstyle and gender";
                return false;
            }
            if (session.freeAppearanceUsed && session.permissions?.appearance != true)
            {
                error = "Free appearance change already used";
                return false;
            }
            return true;
        }

        private static bool TryResolveAppearanceChoice(Pawn pawn, string json, out HairDef hair, out Gender gender, out string error)
        {
            hair = pawn?.story?.hairDef;
            gender = pawn?.gender ?? Gender.None;
            error = null;

            string hairDefName = JsonHelper.ExtractString(json, "hairDef");
            if (!string.IsNullOrEmpty(hairDefName))
            {
                hair = DefDatabase<HairDef>.GetNamed(hairDefName, errorOnFail: false);
                if (hair == null || hair.noGraphic || hair.requiredGene != null || hair.requiredMutant != null)
                {
                    error = "That hairstyle is not available";
                    return false;
                }
            }

            string genderName = JsonHelper.ExtractString(json, "gender");
            if (!string.IsNullOrEmpty(genderName))
            {
                if (!Enum.TryParse(genderName, ignoreCase: true, out gender) || gender == Gender.None)
                {
                    error = "That gender option is not available";
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, object> ExecuteDraft(Pawn pawn, bool drafted)
        {
            if (pawn.drafter == null)
                return ErrorResult("Pawn cannot be drafted");

            pawn.drafter.Drafted = drafted;
            // Explicit draft/undraft = viewer took manual control; stop auto-undrafting.
            ClearAutoDraft(pawn);
            return SuccessResult(drafted ? "Drafted" : "Undrafted");
        }

        private static Dictionary<string, object> ExecuteMove(Pawn pawn, string json)
        {
            var map = pawn.Map;
            if (map == null)
                return ErrorResult("Pawn not on a map");

            int x = JsonHelper.ExtractInt(json, "x", -1);
            int z = JsonHelper.ExtractInt(json, "z", -1);

            // Browser sends normalized 0-1 fractions as targetX/targetY
            // Convert to map cell coords using map size
            if (x < 0 || z < 0)
            {
                float tx = JsonHelper.ExtractFloat(json, "targetX", -1f);
                float ty = JsonHelper.ExtractFloat(json, "targetY", -1f);
                if (tx >= 0f && ty >= 0f)
                {
                    x = (int)(tx * map.Size.x);
                    z = (int)((1f - ty) * map.Size.z); // Y is inverted: top of canvas = high Z
                }
            }

            if (x < 0 || z < 0)
                return ErrorResult("Invalid coordinates");

            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map))
                return ErrorResult("Coordinates out of bounds");

            if (!cell.Standable(map))
                return ErrorResult("Cannot stand there");

            if (!TargetCellAllowed(pawn, cell, out string areaMessage))
                return ErrorResult(areaMessage);

            // Auto-draft if not drafted — and remember WE did it, so the pawn is
            // undrafted again once the order finishes (see MaybeAutoUndraft).
            if (pawn.drafter != null && !pawn.Drafted)
            {
                pawn.drafter.Drafted = true;
                MarkAutoDrafted(pawn);
            }

            var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.playerForced = true;
            job.locomotionUrgency = pawn.Drafted ? LocomotionUrgency.Jog : LocomotionUrgency.Walk;

            JobTag tag = pawn.Drafted ? JobTag.DraftedOrder : JobTag.Misc;
            if (!pawn.jobs.TryTakeOrderedJob(job, tag, false))
                return ErrorResult($"Could not start moving to ({x},{z})");

            return SuccessResult($"Moving to ({x},{z})");
        }

        private static Dictionary<string, object> ExecuteAttack(Pawn pawn, string json)
        {
            var map = pawn.Map;
            if (map == null)
                return ErrorResult("Pawn not on a map");

            int targetId = JsonHelper.ExtractInt(json, "targetId", -1);
            Thing target = null;

            if (targetId >= 0)
            {
                target = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == targetId);
            }
            else
            {
                // Try coordinate-based: find nearest hostile pawn at cell
                int x = JsonHelper.ExtractInt(json, "x", -1);
                int z = JsonHelper.ExtractInt(json, "z", -1);
                if (x < 0 || z < 0)
                {
                    float tx = JsonHelper.ExtractFloat(json, "targetX", -1f);
                    float ty = JsonHelper.ExtractFloat(json, "targetY", -1f);
                    if (tx >= 0f && ty >= 0f)
                    {
                        x = (int)(tx * map.Size.x);
                        z = (int)((1f - ty) * map.Size.z);
                    }
                }
                if (x >= 0 && z >= 0)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (cell.InBounds(map))
                    {
                        if (!TargetCellAllowed(pawn, cell, out string areaMessage))
                            return ErrorResult(areaMessage);

                        // Find closest attackable thing at or near the cell
                        target = map.thingGrid.ThingsAt(cell)
                            .FirstOrDefault(t => t is Pawn p && !p.Dead && p.Faction != null &&
                                FactionUtility.HostileTo(p.Faction, pawn.Faction));
                        if (target == null)
                        {
                            // Check adjacent cells
                            foreach (var adj in GenAdj.CellsAdjacent8Way(cell, Rot4.North, IntVec2.One))
                            {
                                if (!adj.InBounds(map)) continue;
                                target = map.thingGrid.ThingsAt(adj)
                                    .FirstOrDefault(t => t is Pawn p && !p.Dead && p.Faction != null &&
                                        FactionUtility.HostileTo(p.Faction, pawn.Faction));
                                if (target != null) break;
                            }
                        }
                    }
                }
            }

            if (target == null)
                return ErrorResult("No target found");

            if (IsOtherViewerColonist(target, pawn, out string ownerLabel))
                return ErrorResult($"Cannot attack {target.LabelShort ?? "colonist"} — controlled by {ownerLabel}");

            if (target.Spawned && !TargetCellAllowed(pawn, target.Position, out string targetAreaMessage))
                return ErrorResult(targetAreaMessage);

            // Auto-draft — tracked for auto-undraft once combat/orders end.
            if (pawn.drafter != null && !pawn.Drafted)
            {
                pawn.drafter.Drafted = true;
                MarkAutoDrafted(pawn);
            }

            // Choose melee or ranged based on equipment
            Job job;
            if (pawn.equipment?.Primary != null && pawn.equipment.Primary.def.IsRangedWeapon)
            {
                job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
            }
            else
            {
                job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            }

            pawn.jobs.TryTakeOrderedJob(job);
            return SuccessResult($"Attacking {target.LabelShort ?? "target"}");
        }

        private static Dictionary<string, object> ExecuteSetWork(Pawn pawn, string json)
        {
            string workDef = JsonHelper.ExtractString(json, "workDef");
            int priority = JsonHelper.ExtractInt(json, "priority", -1);

            if (workDef == null || priority < 0)
                return ErrorResult("Missing workDef or priority");

            if (PawnPolicyController.SetWorkPriority(pawn, workDef, priority))
                return SuccessResult($"Set {workDef} to {priority}");
            return ErrorResult($"Cannot set {workDef}");
        }

        private static Dictionary<string, object> ExecuteSetSchedule(Pawn pawn, string json)
        {
            int hour = JsonHelper.ExtractInt(json, "hour", -1);
            string assignment = JsonHelper.ExtractString(json, "assignment");

            if (hour < 0 || assignment == null)
                return ErrorResult("Missing hour or assignment");

            if (PawnPolicyController.SetSchedule(pawn, hour, assignment))
                return SuccessResult($"Set hour {hour} to {assignment}");
            return ErrorResult($"Cannot set schedule");
        }

        private static Dictionary<string, object> ExecuteSetPolicy(Pawn pawn, string json, string policyType)
        {
            string label = JsonHelper.ExtractString(json, "policy");
            if (label == null)
                return ErrorResult("Missing policy name");

            bool ok;
            switch (policyType)
            {
                case "outfit":
                    ok = PawnPolicyController.SetOutfit(pawn, label);
                    break;
                case "drugPolicy":
                    ok = PawnPolicyController.SetDrugPolicy(pawn, label);
                    break;
                case "foodPolicy":
                    ok = PawnPolicyController.SetFoodPolicy(pawn, label);
                    break;
                default:
                    return ErrorResult("Unknown policy type");
            }

            if (ok)
                return SuccessResult($"Set {policyType} to {label}");
            return ErrorResult($"Policy '{label}' not found");
        }

        private static Dictionary<string, object> ExecuteSetArea(Pawn pawn, string json)
        {
            string areaLabel = JsonHelper.ExtractString(json, "area");
            if (areaLabel == null)
                areaLabel = "Unrestricted";

            if (PawnPolicyController.SetArea(pawn, areaLabel))
                return SuccessResult($"Set area to {areaLabel}");
            return ErrorResult($"Area '{areaLabel}' not found");
        }

        private static Dictionary<string, object> ExecuteEquip(Pawn pawn, string json)
        {
            int thingId = JsonHelper.ExtractInt(json, "thingId", -1);
            if (thingId < 0)
                return ErrorResult("No item specified");

            var map = pawn.Map;
            if (map == null)
                return ErrorResult("Pawn not on a map");

            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing == null)
                return ErrorResult("Item not found");

            Pawn holder = GetCarryingPawn(thing);
            if (holder != null && holder != pawn)
            {
                if (IsOtherViewerColonist(holder, pawn, out string ownerLabel))
                    return ErrorResult($"Cannot take gear from {holder.LabelShort ?? "colonist"} — controlled by {ownerLabel}");
                return ErrorResult($"Cannot take gear from {holder.LabelShort ?? "another colonist"}");
            }

            if (!ArmoryCatalog.ValidateEquipTarget(pawn, thing, out string validationError))
                return ErrorResult(validationError);
            if (!ArmoryCatalog.TryClaimEquipTarget(pawn, thing, out string claimError))
                return ErrorResult(claimError);

            // JobDefOf.Equip is the WEAPON job — it puts the item into the pawn's
            // equipment tracker. Using it for apparel stuffed hats into the weapon
            // tracker: they piled up unworn ("collecting hats") and corrupted the
            // tracker (EquipmentTrackerTick NREs -> uncontrollable pawns, PostLoadInit
            // failures on save load). Apparel takes the Wear job.
            Job job;
            if (thing is Apparel)
                job = JobMaker.MakeJob(JobDefOf.Wear, thing);
            else if (thing.def.IsWeapon && thing is ThingWithComps)
                job = JobMaker.MakeJob(JobDefOf.Equip, thing);
            else
                return ErrorResult($"{thing.LabelShort ?? "Item"} cannot be equipped");

            if (!pawn.jobs.TryTakeOrderedJob(job))
            {
                ArmoryCatalog.ReleaseEquipClaim(pawn, thing);
                return ErrorResult($"Could not order {thing.LabelShort ?? "item"}");
            }
            return SuccessResult($"{(thing is Apparel ? "Wearing" : "Equipping")} {thing.LabelShort ?? "item"}");
        }

        /// <summary>
        /// Repairs a pawn whose equipment tracker holds invalid entries — null slots
        /// (save corruption; NREs every EquipmentTrackerTick and suppresses the pawn's
        /// tick, making it uncontrollable) and Apparel stuffed in by the old equip
        /// command. Nulls are removed; apparel is dropped at the pawn's feet so it can
        /// be worn properly. Safe to call repeatedly; never throws.
        /// </summary>
        public static bool RepairEquipmentTracker(Pawn pawn)
        {
            bool repaired = false;
            try
            {
                var owner = pawn?.equipment?.GetDirectlyHeldThings();
                if (owner == null)
                    return false;

                int removed = owner.RemoveAll(t => t == null);
                if (removed > 0)
                {
                    repaired = true;
                    LogUtil.Warn($"Repaired {pawn.LabelShort}: removed {removed} null equipment entr{(removed == 1 ? "y" : "ies")}");
                }

                if (pawn.Spawned && pawn.Map != null)
                {
                    var stuckApparel = new List<Thing>();
                    for (int i = 0; i < owner.Count; i++)
                    {
                        if (owner[i] is Apparel)
                            stuckApparel.Add(owner[i]);
                    }
                    foreach (var item in stuckApparel)
                    {
                        if (owner.TryDrop(item, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _))
                        {
                            repaired = true;
                            LogUtil.Warn($"Repaired {pawn.LabelShort}: ejected {item.LabelShort} from equipment tracker");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Equipment repair failed for {pawn?.LabelShort}: {ex.Message}");
            }
            return repaired;
        }

        private static Dictionary<string, object> ExecuteDrop(Pawn pawn, string json)
        {
            string slot = JsonHelper.ExtractString(json, "slot") ?? "weapon";

            if (slot == "weapon")
            {
                if (pawn.equipment?.Primary == null)
                    return ErrorResult("No weapon equipped");

                ThingWithComps dropped;
                pawn.equipment.TryDropEquipment(pawn.equipment.Primary, out dropped, pawn.Position, true);
                return SuccessResult("Dropped weapon");
            }

            // Drop apparel by defName
            if (pawn.apparel == null)
                return ErrorResult("No apparel");

            var apparel = pawn.apparel.WornApparel.FirstOrDefault(a => a.def.defName == slot);
            if (apparel == null)
                return ErrorResult($"Not wearing {slot}");

            Apparel droppedApparel;
            pawn.apparel.TryDrop(apparel, out droppedApparel, pawn.Position, true);
            return SuccessResult($"Dropped {apparel.LabelShort ?? slot}");
        }

        // Curated dye palette sent to viewers. NOT RimWorld's ColorDefs (vanilla has
        // effectively none for dyeing — the game uses a free color picker); this is a
        // deliberate bounded set of canon-feeling apparel colors (earthy tones, muted
        // primaries, neutrals) so viewer dyeing stays tasteful and not neon slop.
        // Keyed by a stable id the web UI sends back.
        public static readonly (string id, string label, float r, float g, float b)[] DyePalette =
        {
            ("crimson",   "Crimson",    0.55f, 0.12f, 0.12f),
            ("rust",      "Rust",       0.62f, 0.31f, 0.16f),
            ("amber",     "Amber",      0.82f, 0.55f, 0.18f),
            ("gold",      "Gold",       0.82f, 0.66f, 0.36f),
            ("olive",     "Olive",      0.42f, 0.44f, 0.20f),
            ("forest",    "Forest",     0.20f, 0.38f, 0.22f),
            ("teal",      "Teal",       0.16f, 0.44f, 0.44f),
            ("navy",      "Navy",       0.16f, 0.22f, 0.42f),
            ("royal",     "Royal Blue", 0.24f, 0.34f, 0.66f),
            ("violet",    "Violet",     0.40f, 0.24f, 0.50f),
            ("plum",      "Plum",       0.42f, 0.20f, 0.34f),
            ("rose",      "Rose",       0.78f, 0.42f, 0.48f),
            ("sand",      "Sand",       0.80f, 0.72f, 0.52f),
            ("brown",     "Brown",      0.36f, 0.25f, 0.16f),
            ("charcoal",  "Charcoal",   0.16f, 0.16f, 0.17f),
            ("slate",     "Slate",      0.40f, 0.43f, 0.47f),
            ("bone",      "Bone",       0.86f, 0.83f, 0.74f),
            ("white",     "White",      0.92f, 0.92f, 0.90f),
            ("black",     "Black",      0.08f, 0.08f, 0.09f),
        };

        public static List<object> BuildDyePaletteMessage()
        {
            var list = new List<object>();
            foreach (var c in DyePalette)
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = c.id,
                    ["label"] = c.label,
                    ["hex"] = $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}"
                });
            return list;
        }

        private static Dictionary<string, object> ExecuteDyeApparel(ViewerSession session, Pawn pawn, string json)
        {
            if (session?.permissions?.appearance != true)
                return ErrorResult("Dyeing is disabled by the streamer");
            if (pawn?.apparel == null)
                return ErrorResult("No apparel to dye");

            int itemId = JsonHelper.ExtractInt(json, "itemId", -1);
            string colorId = JsonHelper.ExtractString(json, "colorId");
            if (itemId < 0 || string.IsNullOrEmpty(colorId))
                return ErrorResult("Pick an item and a color");

            var swatch = DyePalette.FirstOrDefault(c => c.id == colorId);
            if (swatch.id == null)
                return ErrorResult("Unknown color");

            var apparel = pawn.apparel.WornApparel.FirstOrDefault(a => a.thingIDNumber == itemId);
            if (apparel == null)
                return ErrorResult("That item isn't worn");

            var colorable = apparel.TryGetComp<CompColorable>();
            if (colorable == null)
                return ErrorResult($"{apparel.LabelShort} can't be dyed");

            colorable.SetColor(new Color(swatch.r, swatch.g, swatch.b));
            // Refresh the pawn's drawn graphics + the viewer's portrait/state.
            pawn.Drawer?.renderer?.SetAllGraphicsDirty();
            PortraitsCache.SetDirty(pawn);
            Find.ColonistBar?.MarkColonistsDirty();
            var comp = OverlordGameComponent.Instance;
            comp?.InvalidatePawnPortrait(pawn);
            if (!string.IsNullOrEmpty(session.username))
                comp?.HandleRequestStatePublic(session.username);

            return SuccessResult($"Dyed {apparel.LabelShort} {swatch.label}");
        }

        private static Dictionary<string, object> ExecuteSpawnColonist(string username, string json, ViewerManager vm)
        {
            if (!string.IsNullOrEmpty(username) && vm != null)
            {
                var session = vm.GetSession(username) ?? vm.GetOrCreateSession(username, username);
                if (session == null)
                    return ErrorResult("Viewer not available");

                if (vm.TryAssignExistingPawnForViewer(username, out Pawn existingPawn))
                {
                    vm.SendColonistList();
                    OverlordGameComponent.Instance?.HandleRequestStatePublic(username);
                    return SuccessResult($"Already assigned to {existingPawn.LabelShort}");
                }
            }

            var map = Find.CurrentMap;
            if (map == null) return ErrorResult("No map loaded");

            try
            {
                var request = new PawnGenerationRequest(
                    PawnKindDefOf.Colonist,
                    Faction.OfPlayer,
                    PawnGenerationContext.NonPlayer,
                    developmentalStages: DevelopmentalStage.Adult
                );
                Pawn newPawn = PawnGenerator.GeneratePawn(request);

                // Spawn near map center
                IntVec3 spawnCell = CellFinder.RandomClosewalkCellNear(map.Center, map, 10);
                GenSpawn.Spawn(newPawn, spawnCell, map, Rot4.South, WipeMode.Vanish);

                // Assign to viewer if specified
                if (!string.IsNullOrEmpty(username) && vm != null)
                {
                    var session = vm.GetSession(username);
                    if (session != null)
                    {
                        vm.AssignPawn(username, newPawn);
                        OverlordGameComponent.Instance?.HandleRequestStatePublic(username);
                    }
                }
                vm?.SendColonistList();

                Messages.Message(
                    $"[Overlord] New colonist {newPawn.LabelShort} spawned{(string.IsNullOrEmpty(username) ? "" : $" for {username}")}.",
                    newPawn, MessageTypeDefOf.PositiveEvent, historical: false
                );

                return SuccessResult($"Spawned {newPawn.LabelShort}");
            }
            catch (System.Exception ex)
            {
                LogUtil.Error($"Spawn failed: {ex.Message}");
                return ErrorResult("Spawn failed");
            }
        }

        private static Dictionary<string, object> ExecuteHostileResponse(Pawn pawn, string json)
        {
            int mode = JsonHelper.ExtractInt(json, "mode", -1);
            if (mode < 0 || mode > 2)
                return ErrorResult("Invalid mode (0=flee, 1=attack, 2=ignore)");

            if (pawn.playerSettings == null)
                return ErrorResult("No player settings");

            pawn.playerSettings.hostilityResponse = (HostilityResponseMode)mode;
            return SuccessResult($"Hostile response: {(HostilityResponseMode)mode}");
        }

        private static Dictionary<string, object> ExecuteConsume(Pawn pawn, string json)
        {
            int thingId = JsonHelper.ExtractInt(json, "thingId", -1);
            if (thingId < 0)
                return ErrorResult("No item specified");

            if (pawn.inventory?.innerContainer == null)
                return ErrorResult("No inventory");

            var thing = pawn.inventory.innerContainer.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing == null)
                return ErrorResult("Item not in inventory");

            if (thing.def.IsIngestible)
            {
                var job = JobMaker.MakeJob(JobDefOf.Ingest, thing);
                job.count = 1;
                pawn.jobs.TryTakeOrderedJob(job);
                return SuccessResult($"Consuming {thing.LabelShort ?? "item"}");
            }

            return ErrorResult("Item is not consumable");
        }

        private static Dictionary<string, object> ExecuteDropInventory(Pawn pawn, string json)
        {
            int thingId = JsonHelper.ExtractInt(json, "thingId", -1);
            if (thingId < 0)
                return ErrorResult("No item specified");

            if (pawn.inventory?.innerContainer == null)
                return ErrorResult("No inventory");

            var thing = pawn.inventory.innerContainer.FirstOrDefault(t => t.thingIDNumber == thingId);
            if (thing == null)
                return ErrorResult("Item not in inventory");

            pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _);
            return SuccessResult($"Dropped {thing.LabelShort ?? "item"}");
        }

        private static Dictionary<string, object> ExecuteSocialInteract(Pawn pawn, string json)
        {
            if (pawn?.interactions == null)
                return ErrorResult("Pawn cannot socially interact");

            int targetId = JsonHelper.ExtractInt(json, "targetId", -1);
            string interactionName = JsonHelper.ExtractString(json, "interaction") ?? "KindWords";
            if (targetId < 0)
                return ErrorResult("No social target specified");

            var map = pawn.Map;
            if (map == null)
                return ErrorResult("Pawn not on a map");

            Pawn target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);
            if (target == null || target.Dead || target == pawn)
                return ErrorResult("Social target unavailable");
            if (target.RaceProps?.Humanlike != true)
                return ErrorResult("Social target is not a colonist");
            // Social is allowed across colonists; it does not grant control of the target.

            InteractionDef interaction = DefDatabase<InteractionDef>.GetNamedSilentFail(interactionName);
            if (interaction == null)
                return ErrorResult("Unknown interaction");

            try
            {
                if (!pawn.interactions.CanInteractNowWith(target, interaction))
                    return ErrorResult("Cannot interact with that pawn right now");
                if (!pawn.interactions.TryInteractWith(target, interaction))
                    return ErrorResult("Interaction failed");
            }
            catch (Exception ex)
            {
                return ErrorResult("Interaction failed: " + ex.Message);
            }

            return SuccessResult($"{interaction.label ?? interaction.defName} with {target.LabelShort ?? "colonist"}");
        }

        private static Dictionary<string, object> ExecuteContextMenu(Pawn pawn, string json, ViewerManager vm)
        {
            var map = pawn.Map;
            if (map == null)
                return ErrorResult("Pawn not on a map");

            int x = JsonHelper.ExtractInt(json, "x", -1);
            int z = JsonHelper.ExtractInt(json, "z", -1);
            int targetId = JsonHelper.ExtractInt(json, "targetId", -1);
            if (targetId < 0)
                targetId = JsonHelper.ExtractInt(json, "targetEntityId", -1);

            Thing requestedTarget = null;
            if (targetId >= 0)
            {
                requestedTarget = map.listerThings.AllThings.FirstOrDefault(t =>
                    t != null &&
                    !t.Destroyed &&
                    t.Spawned &&
                    t.Map == map &&
                    t.thingIDNumber == targetId);
                if (requestedTarget != null)
                {
                    if (IsOtherViewerColonist(requestedTarget, pawn, out string ownerLabel))
                        return ErrorResult($"Cannot command {requestedTarget.LabelShort ?? "colonist"} — controlled by {ownerLabel}");

                    var targetCell = requestedTarget.Position;
                    if (targetCell.InBounds(map))
                    {
                        x = targetCell.x;
                        z = targetCell.z;
                    }
                }
            }

            if (x < 0 || z < 0)
            {
                float tx = JsonHelper.ExtractFloat(json, "targetX", -1f);
                float ty = JsonHelper.ExtractFloat(json, "targetY", -1f);
                if (tx >= 0f && ty >= 0f)
                {
                    x = (int)(tx * map.Size.x);
                    z = (int)((1f - ty) * map.Size.z);
                }
            }

            if (x < 0 || z < 0)
                return ErrorResult("Invalid coordinates");

            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map))
                return ErrorResult("Out of bounds");

            if (!TargetCellAllowed(pawn, cell, out string areaMessage))
                return ErrorResult(areaMessage);

            if (!RimWorldCompat.SupportsContextMenus)
            {
                return ErrorResult("Context menu unavailable on this RimWorld build");
            }

            List<FloatMenuOption> options = null;
            IntVec3 resolvedCell = cell;
            bool foundOptions = requestedTarget != null &&
                TryFindContextOptionsNear(pawn, map, requestedTarget.Position, out options, out resolvedCell);
            if (!foundOptions)
                foundOptions = TryFindContextOptionsNear(pawn, map, cell, out options, out resolvedCell);

            if (!foundOptions)
                return ErrorResult("No actions here");

            if (options == null || !options.Any())
                return ErrorResult("No actions here");

            var sortedOptions = options
                .OrderByDescending(o =>
                {
                    try { return RimWorldCompat.SafeReadOptionPriority(o); }
                    catch { return 4; }
                })
                .ThenByDescending(o => o?.orderInPriority ?? 0)
                .Take(20)
                .ToList();

            var menuItems = new List<Dictionary<string, object>>();
            for (int i = 0; i < sortedOptions.Count; i++)
                menuItems.Add(RimWorldCompat.BuildContextOptionMetadata(sortedOptions[i], i));

            // Store options on session for later action execution
            var session = vm.GetSessionForPawn(pawn);
            if (session != null)
                session.lastContextOptions = sortedOptions;

            return new Dictionary<string, object>
            {
                ["type"] = "context_menu",
                ["ok"] = true,
                ["x"] = resolvedCell.x,
                ["z"] = resolvedCell.z,
                ["targetId"] = requestedTarget?.thingIDNumber ?? -1,
                ["targetLabel"] = requestedTarget?.LabelShort ?? "",
                ["options"] = menuItems
            };
        }

        private static bool TryFindContextOptionsNear(Pawn pawn, Map map, IntVec3 origin, out List<FloatMenuOption> options, out IntVec3 resolvedCell)
        {
            options = null;
            resolvedCell = origin;

            foreach (var cell in ContextProbeCells(origin, map, 3))
            {
                if (!TargetCellAllowed(pawn, cell, out _))
                    continue;

                var clickPos = cell.ToVector3Shifted();
                if (!RimWorldCompat.TryGetContextOptions(pawn, clickPos, out var found))
                    continue;

                if (found == null || !found.Any())
                    continue;

                options = found;
                resolvedCell = cell;
                return true;
            }

            return false;
        }

        private static IEnumerable<IntVec3> ContextProbeCells(IntVec3 origin, Map map, int radius)
        {
            if (origin.InBounds(map))
                yield return origin;

            for (int r = 1; r <= radius; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r)
                            continue;

                        var cell = new IntVec3(origin.x + dx, 0, origin.z + dz);
                        if (cell.InBounds(map))
                            yield return cell;
                    }
                }
            }
        }

        private static Dictionary<string, object> ExecuteContextAction(Pawn pawn, string json)
        {
            int optionId = JsonHelper.ExtractInt(json, "optionId", -1);
            if (optionId < 0)
                return ErrorResult("No option specified");

            string username = JsonHelper.ExtractLastString(json, "username");
            var vm = OverlordGameComponent.Instance?.Viewers;
            var session = vm?.GetSession(username);
            if (session == null || session.assignedPawn != pawn)
                return ErrorResult("No pawn assigned");
            if (session.lastContextOptions == null || optionId >= session.lastContextOptions.Count)
                return ErrorResult("Invalid option or no context menu open");

            var opt = session.lastContextOptions[optionId];
            if (opt.Disabled)
                return ErrorResult($"Action disabled: {opt.Label}");

            opt.action?.Invoke();
            session.lastContextOptions = null;
            return SuccessResult($"Executing: {opt.Label}");
        }

        private static Dictionary<string, object> ExecuteClaimColonist(string username, string json, ViewerManager vm)
        {
            int pawnId = JsonHelper.ExtractInt(json, "pawnId", -1);
            if (pawnId < 0) return ErrorResult("No colonist specified");

            var pawn = vm.FindPawnById(pawnId);
            if (pawn == null) return ErrorResult("Colonist not found");

            // Check if already taken
            var existing = vm.GetSessionForPawn(pawn);
            if (existing != null)
            {
                if (string.Equals(existing.username, username, System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.displayName, username, System.StringComparison.OrdinalIgnoreCase))
                {
                    vm.SendColonistList(username);
                    OverlordGameComponent.Instance?.HandleRequestStatePublic(username);
                    return SuccessResult($"Already assigned to {pawn.LabelShort}");
                }

                return ErrorResult($"Already assigned to {existing.username}");
            }

            // Send claim request to streamer for approval
            var comp = OverlordGameComponent.Instance;
            var session = vm.GetSession(username);
            string displayName = session?.displayName ?? username;
            var claim = vm.AddClaimRequest(username, displayName, pawn);
            comp?.NotifyClaimRequest(claim);

            Messages.Message(
                $"[Overlord] {displayName} wants to claim {pawn.LabelShort}. Use the Overlord tab to approve.",
                pawn, MessageTypeDefOf.NeutralEvent, historical: false
            );

            // Also broadcast to admin/OBS
            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ClaimRequest,
                ["username"] = username,
                ["displayName"] = displayName,
                ["pawnId"] = claim?.pawnId ?? pawnId,
                ["pawnName"] = claim?.pawnName ?? pawn.LabelShort ?? "unknown",
                ["adminOnly"] = true
            };
            comp?.Relay?.Broadcast(msg);

            return SuccessResult($"Claim request sent for {pawn.LabelShort}");
        }

        private static Dictionary<string, object> ExecuteVote(string username, string json)
        {
            int optionIndex = JsonHelper.ExtractInt(json, "option", -1);
            var vote = OverlordGameComponent.Instance?.VoteManager;
            if (vote == null || !vote.active) return ErrorResult("No active vote");
            if (optionIndex < 0) return ErrorResult("No option specified");

            vote.CastVote(username, optionIndex);
            return SuccessResult($"Vote recorded");
        }

        private static Dictionary<string, object> ExecuteTriggerEvent(string username, string json)
        {
            if (OverlordMod.Settings?.allowViewerEvents != true)
                return ErrorResult("Viewer-triggered events are disabled");

            string eventId = JsonHelper.ExtractString(json, "eventId");
            if (string.IsNullOrEmpty(eventId)) return ErrorResult("No event specified");

            string result = EventTriggerManager.TryTrigger(username, eventId);
            bool success = result.StartsWith("Triggered");
            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = success,
                ["message"] = result
            };
        }

        private static Dictionary<string, object> ExecuteStartVote(string json)
        {
            string question = JsonHelper.ExtractString(json, "question");
            string optionsStr = JsonHelper.ExtractString(json, "options");
            if (string.IsNullOrEmpty(question) || string.IsNullOrEmpty(optionsStr))
                return ErrorResult("Vote needs a question and options");

            var opts = optionsStr.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (opts.Count < 2) return ErrorResult("Vote needs at least 2 options");

            var vote = OverlordGameComponent.Instance?.VoteManager;
            if (vote == null) return ErrorResult("VoteManager not available");

            vote.StartVote(question, opts);
            return SuccessResult($"Vote started: {question}");
        }

        private static Dictionary<string, object> ExecuteEndVote()
        {
            var vote = OverlordGameComponent.Instance?.VoteManager;
            if (vote == null || !vote.active) return ErrorResult("No active vote");
            vote.EndVote();
            return SuccessResult("Vote ended");
        }

        private static Dictionary<string, object> ExecuteBan(string username, string json, ViewerManager vm)
        {
            string target = JsonHelper.ExtractLastString(json, "username");
            if (string.IsNullOrEmpty(target)) return ErrorResult("No username");
            vm.BanViewer(target, "Banned by admin");
            return SuccessResult($"Banned {target}");
        }

        private static Dictionary<string, object> ExecuteTimeout(string username, string json, ViewerManager vm)
        {
            string target = JsonHelper.ExtractLastString(json, "username");
            int seconds = JsonHelper.ExtractInt(json, "seconds", 300);
            if (string.IsNullOrEmpty(target)) return ErrorResult("No username");
            vm.TimeoutViewer(target, seconds);
            return SuccessResult($"Timed out {target} for {seconds}s");
        }

        private static Dictionary<string, object> ExecuteGrantTicket(string username, ViewerManager vm)
        {
            if (string.IsNullOrEmpty(username)) return ErrorResult("No username");
            if (!vm.GrantTicket(username)) return ErrorResult($"{username} is at max tickets");
            return SuccessResult($"Granted ticket to {username}");
        }

        private static Dictionary<string, object> SuccessResult(string message)
        {
            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = true,
                ["message"] = message
            };
        }

        private static Dictionary<string, object> ErrorResult(string message)
        {
            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = false,
                ["message"] = message
            };
        }

        private static bool ShouldThrottleCommand(string action)
        {
            switch (action)
            {
                case StateProtocol.CmdDraft:
                case StateProtocol.CmdUndraft:
                case StateProtocol.CmdCameraZoom:
                case StateProtocol.CmdToolkitRefresh:
                case StateProtocol.CmdPreviewAppearance:
                case StateProtocol.CmdSetSchedule:
                // The relay already wall-clock paces and coalesces menu requests.
                // Game-tick throttling here made menus appear broken while paused.
                case StateProtocol.CmdContextMenu:
                    return false;
                default:
                    return true;
            }
        }

        private static bool TargetCellAllowed(Pawn pawn, IntVec3 cell, out string message)
        {
            message = null;
            if (RimWorldCompat.IsCellAllowedByCurrentArea(pawn, cell, out string areaLabel))
                return true;

            message = string.IsNullOrEmpty(areaLabel)
                ? "That target is outside the allowed area"
                : $"That target is outside allowed area: {areaLabel}";
            return false;
        }

        /// <summary>
        /// True when <paramref name="target"/> is a colonist assigned to a different Overlord viewer.
        /// Used to block cross-viewer control / grief paths (attack-by-id, strip gear, context on their pawn).
        /// </summary>
        private static bool IsOtherViewerColonist(Thing target, Pawn actor, out string ownerLabel)
        {
            ownerLabel = null;
            if (!(target is Pawn other) || other.Dead || other.Destroyed)
                return false;
            if (actor != null && other == actor)
                return false;

            var vm = OverlordGameComponent.Instance?.Viewers;
            if (vm == null)
                return false;

            var owner = vm.GetSessionForPawn(other);
            if (owner == null || !owner.HasPawn || owner.assignedPawn != other)
                return false;

            var actorSession = actor != null ? vm.GetSessionForPawn(actor) : null;
            if (actorSession != null &&
                string.Equals(actorSession.username, owner.username, StringComparison.OrdinalIgnoreCase))
                return false;

            ownerLabel = owner.displayName ?? owner.username ?? "another viewer";
            return true;
        }

        private static Pawn GetCarryingPawn(Thing thing)
        {
            for (IThingHolder holder = thing?.ParentHolder; holder != null; holder = holder.ParentHolder)
            {
                if (holder is Pawn pawn)
                    return pawn;
            }
            return null;
        }
    }
}
