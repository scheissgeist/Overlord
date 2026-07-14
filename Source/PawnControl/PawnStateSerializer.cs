using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace Overlord
{
    /// <summary>
    /// Serializes pawn state to a JSON string for sending to viewers.
    /// Returns a raw JSON string (not wrapped in a message envelope).
    /// </summary>
    public static class PawnStateSerializer
    {
        // ── Tiered change detection ─────────────────────────────────────────────
        // The signature is split into a FAST tier (position, drafted, health, needs,
        // gear, inventory, job — things that move constantly and are cheap to hash)
        // recomputed every sync cycle, and a SLOW tier (skills, thoughts, work
        // priorities, schedule, traits, policies, capacities, relations/opinions,
        // nearby ground equipment, backstory/appearance — things that change on the
        // order of seconds-to-never but are expensive to walk: def-database loops,
        // O(colonists) OpinionOf, ThingsInGroup scans, reflection label lookups).
        // The slow tier is sampled at most every SlowSampleIntervalSeconds per pawn
        // (realtime, immune to game-speed scaling) and INVALIDATED IMMEDIATELY when a
        // viewer command executes, so command feedback is never delayed by the cache.
        private const float SlowSampleIntervalSeconds = 2f;
        private static readonly Dictionary<int, int> slowHashCache = new Dictionary<int, int>();
        private static readonly Dictionary<int, float> slowSampleTimeCache = new Dictionary<int, float>();

        /// <summary>
        /// Drops the cached slow-tier sub-hash for a pawn so the next sync cycle
        /// recomputes it. Call after any successful viewer/admin command targeting the
        /// pawn — commands are the only way slow-tier fields change instantly.
        /// </summary>
        public static void InvalidateSignatureCache(Pawn pawn)
        {
            if (pawn == null)
                return;
            slowHashCache.Remove(pawn.thingIDNumber);
            slowSampleTimeCache.Remove(pawn.thingIDNumber);
        }

        /// <summary>
        /// Full reset — call on game shutdown. thingIDNumbers are reused across save
        /// loads, so a stale cross-save entry could mask a real change for up to 2s.
        /// </summary>
        public static void ClearSignatureCaches()
        {
            slowHashCache.Clear();
            slowSampleTimeCache.Clear();
        }

        private static int GetSlowSubHash(Pawn pawn)
        {
            int id = pawn.thingIDNumber;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (slowSampleTimeCache.TryGetValue(id, out float lastTime) &&
                now - lastTime < SlowSampleIntervalSeconds &&
                slowHashCache.TryGetValue(id, out int cached))
            {
                return cached;
            }

            int sub = ComputeSlowSubHash(pawn);

            // Keep the sample caches from growing unbounded across a long session with
            // many pawn turnovers. Only assigned colonists reach here, so this realistically
            // never trips; the clear just rebuilds lazily on next access.
            if (slowHashCache.Count > 256)
            {
                slowHashCache.Clear();
                slowSampleTimeCache.Clear();
            }

            slowHashCache[id] = sub;
            slowSampleTimeCache[id] = now;
            return sub;
        }

        private static int ComputeSlowSubHash(Pawn pawn)
        {
            unchecked
            {
                int hash = 23;

                if (pawn.skills != null)
                {
                    foreach (var skill in pawn.skills.skills)
                    {
                        if (skill?.def == null) continue;
                        AddStringHash(ref hash, skill.def.defName);
                        hash = hash * 31 + skill.Level;
                        hash = hash * 31 + (int)skill.passion;
                        hash = hash * 31 + (skill.TotallyDisabled ? 1 : 0);
                    }
                }

                if (pawn.needs?.mood?.thoughts?.memories?.Memories != null)
                {
                    foreach (var thought in pawn.needs.mood.thoughts.memories.Memories)
                    {
                        if (thought?.def == null) continue;
                        AddStringHash(ref hash, thought.def.defName);
                        hash = hash * 31 + (int)(thought.MoodOffset() * 100f);
                    }
                }

                if (pawn.workSettings != null && pawn.workSettings.EverWork)
                {
                    foreach (var wt in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                    {
                        if (wt == null) continue;
                        try
                        {
                            hash = hash * 31 + (pawn.WorkTypeIsDisabled(wt) ? -1 : pawn.workSettings.GetPriority(wt));
                        }
                        catch { }
                    }
                }

                if (pawn.timetable != null)
                {
                    for (int hour = 0; hour < 24; hour++)
                    {
                        AddStringHash(ref hash, pawn.timetable.GetAssignment(hour)?.defName);
                    }
                }

                if (pawn.story?.traits != null)
                {
                    foreach (var trait in pawn.story.traits.allTraits)
                    {
                        if (trait?.def == null) continue;
                        AddStringHash(ref hash, trait.def.defName);
                        hash = hash * 31 + trait.Degree;
                    }
                }

                AddStringHash(ref hash, RimWorldCompat.GetCurrentOutfitLabel(pawn));
                AddStringHash(ref hash, RimWorldCompat.GetCurrentDrugPolicyLabel(pawn));
                AddStringHash(ref hash, RimWorldCompat.GetCurrentFoodPolicyLabel(pawn));
                AddStringHash(ref hash, RimWorldCompat.GetCurrentAreaRestrictionLabel(pawn) ?? "Unrestricted");

                if (pawn.health?.capacities != null)
                {
                    foreach (var capDef in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
                    {
                        if (capDef == null) continue;
                        try
                        {
                            hash = hash * 31 + (int)(pawn.health.capacities.GetLevel(capDef) * 100f);
                        }
                        catch { }
                    }
                }

                if (pawn.relations != null)
                {
                    var directRelations = pawn.relations.DirectRelations;
                    if (directRelations != null)
                    {
                        foreach (var rel in directRelations)
                        {
                            if (rel?.def == null || rel.otherPawn == null) continue;
                            AddStringHash(ref hash, rel.def.defName);
                            hash = hash * 31 + rel.otherPawn.thingIDNumber;
                        }
                    }

                    if (pawn.Map != null)
                    {
                        foreach (var other in pawn.Map.mapPawns.FreeColonists)
                        {
                            if (other == pawn || other.Dead) continue;
                            try
                            {
                                hash = hash * 31 + other.thingIDNumber;
                                hash = hash * 31 + pawn.relations.OpinionOf(other);
                            }
                            catch { }
                        }
                    }
                }

                // Nearby loose weapons/apparel (viewer gear-pickup list). Hash the
                // SAME filtered list Serialize() sends (IsForbidden + reachability +
                // Take(24)) so forbid/reachability flips change the signature —
                // bounded by the 2s slow-tier cadence instead of running per-cycle.
                if (pawn.Map != null)
                {
                    foreach (var item in GetNearbyEquipment(pawn))
                    {
                        if (!item.TryGetValue("id", out object id) || !item.TryGetValue("defName", out object defName))
                            continue;
                        hash = hash * 31 + Convert.ToInt32(id);
                        AddStringHash(ref hash, defName?.ToString());
                    }
                }

                if (pawn.story != null)
                {
                    AddStringHash(ref hash, pawn.story.Title);
                    AddStringHash(ref hash, pawn.story.Childhood?.defName);
                    AddStringHash(ref hash, pawn.story.Adulthood?.defName);
                    AddStringHash(ref hash, pawn.story.hairDef?.defName);
                    AddStringHash(ref hash, pawn.story.bodyType?.defName);
                    hash = hash * 31 + (int)pawn.gender;
                }

                return hash;
            }
        }

        public static string Serialize(Pawn pawn)
        {
            if (pawn == null)
                return "{}";

            var dict = new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = pawn.LabelShort ?? "unknown",
                ["fullName"] = pawn.Name?.ToStringFull ?? pawn.LabelShort ?? "unknown",
                ["drafted"] = pawn.Drafted,
                ["dead"] = pawn.Dead,
                ["downed"] = pawn.Downed,
                ["posX"] = pawn.Position.x,
                ["posZ"] = pawn.Position.z,
            };

            // Needs
            if (pawn.needs != null)
            {
                var needs = new Dictionary<string, object>();
                foreach (var need in pawn.needs.AllNeeds)
                {
                    if (need?.def == null) continue;
                    needs[need.def.defName] = (int)(need.CurLevelPercentage * 100);
                }
                dict["needs"] = needs;
            }

            // Skills
            if (pawn.skills != null)
            {
                var skills = new List<Dictionary<string, object>>();
                foreach (var skill in pawn.skills.skills)
                {
                    if (skill?.def == null) continue;
                    skills.Add(new Dictionary<string, object>
                    {
                        ["name"] = skill.def.defName,
                        ["label"] = skill.def.skillLabel ?? skill.def.defName,
                        ["level"] = skill.Level,
                        ["passion"] = (int)skill.passion,
                        ["disabled"] = skill.TotallyDisabled
                    });
                }
                dict["skills"] = skills;
            }

            // Health
            if (pawn.health != null)
            {
                var health = new Dictionary<string, object>
                {
                    ["summaryHp"] = (int)(pawn.health.summaryHealth?.SummaryHealthPercent * 100 ?? 0),
                    ["painLevel"] = (int)((pawn.health.hediffSet?.PainTotal ?? 0f) * 100)
                };

                // Hediffs (injuries, diseases, etc.)
                var hediffs = new List<Dictionary<string, object>>();
                if (pawn.health.hediffSet != null)
                {
                    foreach (var hediff in pawn.health.hediffSet.hediffs)
                    {
                        if (hediff?.def == null) continue;
                        var h = new Dictionary<string, object>
                        {
                            ["label"] = hediff.Label ?? hediff.def.defName,
                            ["severity"] = (int)(hediff.Severity * 100)
                        };
                        if (hediff.Part != null)
                            h["part"] = hediff.Part.Label ?? hediff.Part.def.defName;
                        hediffs.Add(h);
                    }
                }
                health["hediffs"] = hediffs;
                dict["health"] = health;
            }

            // Mood/thoughts
            if (pawn.needs?.mood?.thoughts != null)
            {
                var thoughts = new List<Dictionary<string, object>>();
                var memories = pawn.needs.mood.thoughts.memories?.Memories;
                if (memories != null)
                {
                    foreach (var thought in memories)
                    {
                        if (thought?.def == null) continue;
                        thoughts.Add(new Dictionary<string, object>
                        {
                            ["label"] = thought.LabelCap ?? thought.def.defName,
                            ["mood"] = thought.MoodOffset()
                        });
                    }
                }
                dict["thoughts"] = thoughts;
            }

            // Work priorities
            if (pawn.workSettings != null && pawn.workSettings.EverWork)
            {
                var work = new Dictionary<string, object>();
                var workList = new List<Dictionary<string, object>>();
                var workTypes = GetWorkTypesInGameOrder();
                int order = 0;
                foreach (var wt in workTypes)
                {
                    if (wt == null) continue;
                    try
                    {
                        int priority;
                        bool disabled = pawn.WorkTypeIsDisabled(wt);
                        if (disabled)
                            priority = -1;
                        else
                            priority = pawn.workSettings.GetPriority(wt);

                        work[wt.defName] = priority;
                        workList.Add(new Dictionary<string, object>
                        {
                            ["defName"] = wt.defName,
                            ["label"] = wt.label ?? wt.defName,
                            ["priority"] = priority,
                            ["disabled"] = disabled,
                            ["order"] = order,
                            ["relevantSkills"] = RimWorldCompat.GetRelevantSkillSummaries(pawn, wt)
                        });
                        order++;
                    }
                    catch { /* Skip work types that error */ }
                }
                dict["work"] = work;
                dict["workPriorities"] = workList;
            }

            // Schedule (24-hour timetable)
            if (pawn.timetable != null)
            {
                var schedule = new List<object>();
                for (int hour = 0; hour < 24; hour++)
                {
                    var assignment = pawn.timetable.GetAssignment(hour);
                    schedule.Add(assignment?.defName ?? "Anything");
                }
                dict["schedule"] = schedule;

                var assignmentOptions = new List<Dictionary<string, object>>();
                foreach (var assignment in DefDatabase<TimeAssignmentDef>.AllDefsListForReading)
                {
                    if (assignment == null) continue;
                    assignmentOptions.Add(new Dictionary<string, object>
                    {
                        ["defName"] = assignment.defName,
                        ["label"] = assignment.label ?? assignment.defName
                    });
                }
                dict["scheduleAssignments"] = assignmentOptions;
            }

            // Equipment (primary weapon)
            if (pawn.equipment?.Primary != null)
            {
                var eq = pawn.equipment.Primary;
                dict["weapon"] = new Dictionary<string, object>
                {
                    ["label"] = eq.LabelCap ?? eq.def.defName,
                    ["defName"] = eq.def.defName,
                    ["slotKey"] = "weapon",
                    ["hp"] = (int)((float)eq.HitPoints / (eq.MaxHitPoints > 0 ? eq.MaxHitPoints : 1) * 100)
                };
            }

            // Apparel
            if (pawn.apparel != null)
            {
                var apparel = new List<Dictionary<string, object>>();
                foreach (var a in pawn.apparel.WornApparel)
                {
                    if (a?.def == null) continue;
                    var colorable = a.TryGetComp<CompColorable>();
                    var entry = new Dictionary<string, object>
                    {
                        ["id"] = a.thingIDNumber,
                        ["label"] = a.LabelCap ?? a.def.defName,
                        ["defName"] = a.def.defName,
                        ["slotKey"] = GearSlotKeyForDef(a.def),
                        ["hp"] = (int)((float)a.HitPoints / a.MaxHitPoints * 100),
                        ["dyeable"] = colorable != null
                    };
                    // Current draw color as hex so the UI can show a swatch.
                    var c = a.DrawColor;
                    entry["color"] = $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";
                    apparel.Add(entry);
                }
                dict["apparel"] = apparel;
            }

            // Traits
            if (pawn.story?.traits != null)
            {
                var traits = new List<Dictionary<string, object>>();
                foreach (var trait in pawn.story.traits.allTraits)
                {
                    if (trait?.def == null) continue;
                    traits.Add(new Dictionary<string, object>
                    {
                        ["label"] = trait.LabelCap ?? trait.def.defName,
                        ["defName"] = trait.def.defName,
                        ["degree"] = trait.Degree
                    });
                }
                dict["traits"] = traits;
                dict["traitOptions"] = GetTraitOptions();
            }

            if (pawn.story != null)
            {
                var story = new Dictionary<string, object>
                {
                    ["title"] = pawn.story.TitleCap ?? pawn.story.Title ?? ""
                };
                if (pawn.story.Childhood != null)
                {
                    story["childhood"] = pawn.story.Childhood.TitleFor(pawn.gender);
                    story["childhoodDef"] = pawn.story.Childhood.defName;
                }
                if (pawn.story.Adulthood != null)
                {
                    story["adulthood"] = pawn.story.Adulthood.TitleFor(pawn.gender);
                    story["adulthoodDef"] = pawn.story.Adulthood.defName;
                }
                dict["story"] = story;
            }

            // Current outfit/drug/food policies
            var outfitPolicy = RimWorldCompat.GetCurrentOutfitLabel(pawn);
            if (!string.IsNullOrEmpty(outfitPolicy))
                dict["outfitPolicy"] = outfitPolicy;
            dict["outfitPolicyOptions"] = GetOutfitPolicyOptions();

            var drugPolicy = RimWorldCompat.GetCurrentDrugPolicyLabel(pawn);
            if (!string.IsNullOrEmpty(drugPolicy))
                dict["drugPolicy"] = drugPolicy;
            dict["drugPolicyOptions"] = GetDrugPolicyOptions();

            var foodPolicy = RimWorldCompat.GetCurrentFoodPolicyLabel(pawn);
            if (!string.IsNullOrEmpty(foodPolicy))
                dict["foodPolicy"] = foodPolicy;
            dict["foodPolicyOptions"] = GetFoodPolicyOptions();

            // Area restriction
            dict["areaRestriction"] = RimWorldCompat.GetCurrentAreaRestrictionLabel(pawn) ?? "Unrestricted";
            dict["areaOptions"] = GetAreaOptions(pawn);

            // Hostile response
            if (pawn.playerSettings != null)
                dict["hostileResponse"] = (int)pawn.playerSettings.hostilityResponse;

            // Capacities (movement, sight, manipulation, etc.)
            if (pawn.health?.capacities != null)
            {
                var caps = new List<Dictionary<string, object>>();
                foreach (var capDef in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
                {
                    if (capDef == null) continue;
                    try
                    {
                        float val = pawn.health.capacities.GetLevel(capDef);
                        caps.Add(new Dictionary<string, object>
                        {
                            ["label"] = capDef.label ?? capDef.defName,
                            ["def"] = capDef.defName,
                            ["level"] = (int)(val * 100)
                        });
                    }
                    catch { }
                }
                dict["capacities"] = caps;
            }

            // Social relations
            if (pawn.relations != null)
            {
                var relations = new List<Dictionary<string, object>>();
                var directRelations = pawn.relations.DirectRelations;
                if (directRelations != null)
                {
                    foreach (var rel in directRelations)
                    {
                        if (rel?.def == null || rel.otherPawn == null) continue;
                        relations.Add(new Dictionary<string, object>
                        {
                            ["id"] = rel.otherPawn.thingIDNumber,
                            ["pawn"] = rel.otherPawn.LabelShort ?? "unknown",
                            ["relation"] = rel.def.label ?? rel.def.defName,
                        });
                    }
                }
                // Also add opinion scores for colonists on same map
                var opinions = new List<Dictionary<string, object>>();
                if (pawn.Map != null)
                {
                    foreach (var other in pawn.Map.mapPawns.FreeColonists)
                    {
                        if (other == pawn || other.Dead) continue;
                        try
                        {
                            int opinion = pawn.relations.OpinionOf(other);
                            opinions.Add(new Dictionary<string, object>
                            {
                                ["id"] = other.thingIDNumber,
                                ["pawn"] = other.LabelShort ?? "unknown",
                                ["opinion"] = opinion,
                                ["distance"] = (int)other.Position.DistanceTo(pawn.Position)
                            });
                        }
                        catch { }
                    }
                }
                dict["relations"] = relations;
                dict["opinions"] = opinions;
            }

            // Inventory (carried items, not worn apparel)
            if (pawn.inventory?.innerContainer != null)
            {
                var inv = new List<Dictionary<string, object>>();
                foreach (var thing in pawn.inventory.innerContainer)
                {
                    if (thing?.def == null) continue;
                    inv.Add(new Dictionary<string, object>
                    {
                        ["id"] = thing.thingIDNumber,
                        ["label"] = thing.LabelCap ?? thing.def.defName,
                        ["defName"] = thing.def.defName,
                        ["count"] = thing.stackCount,
                        ["hp"] = (int)((float)thing.HitPoints / (thing.MaxHitPoints > 0 ? thing.MaxHitPoints : 1) * 100),
                        ["ingestible"] = thing.def.IsIngestible
                    });
                }
                dict["inventory"] = inv;
            }

            var nearbyEquipment = GetNearbyEquipment(pawn);
            if (nearbyEquipment.Count > 0)
                dict["nearbyEquipment"] = nearbyEquipment;

            // Appearance
            if (pawn.story != null)
            {
                var appearance = new Dictionary<string, object>();
                if (pawn.story.hairDef != null)
                {
                    appearance["hair"] = pawn.story.hairDef.label ?? pawn.story.hairDef.defName;
                    appearance["hairDef"] = pawn.story.hairDef.defName;
                }
                if (pawn.story.bodyType != null)
                    appearance["bodyType"] = pawn.story.bodyType.defName;
                appearance["gender"] = pawn.gender.ToString();
                appearance["hairOptions"] = GetHairOptions();
                appearance["genderOptions"] = GetGenderOptions();
                dict["appearance"] = appearance;
            }

            // Current job label
            var jobLabel = pawn.jobs?.curDriver?.GetReport() ?? "";
            if (!string.IsNullOrEmpty(jobLabel))
                dict["currentJob"] = jobLabel;

            return JsonHelper.ToJson(dict);
        }

        private static List<object> GetOutfitPolicyOptions()
        {
            var options = new List<object>();
            var policies = Current.Game?.outfitDatabase?.AllOutfits;
            if (policies == null)
                return options;

            foreach (var policy in policies)
            {
                if (!string.IsNullOrEmpty(policy?.label))
                    options.Add(policy.label);
            }
            return options;
        }

        private static List<object> GetDrugPolicyOptions()
        {
            var options = new List<object>();
            var policies = Current.Game?.drugPolicyDatabase?.AllPolicies;
            if (policies == null)
                return options;

            foreach (var policy in policies)
            {
                if (!string.IsNullOrEmpty(policy?.label))
                    options.Add(policy.label);
            }
            return options;
        }

        private static List<object> GetFoodPolicyOptions()
        {
            var options = new List<object>();
            var policies = Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions;
            if (policies == null)
                return options;

            foreach (var policy in policies)
            {
                if (!string.IsNullOrEmpty(policy?.label))
                    options.Add(policy.label);
            }
            return options;
        }

        private static List<object> GetAreaOptions(Pawn pawn)
        {
            var options = new List<object> { "Unrestricted" };
            var areas = pawn?.Map?.areaManager?.AllAreas;
            if (areas == null)
                return options;

            foreach (var area in areas)
            {
                if (!string.IsNullOrEmpty(area?.Label))
                    options.Add(area.Label);
            }
            return options;
        }

        private static List<Dictionary<string, object>> GetHairOptions()
        {
            var options = new List<Dictionary<string, object>>();
            foreach (var hair in DefDatabase<HairDef>.AllDefsListForReading.OrderBy(h => h.label ?? h.defName))
            {
                if (hair == null || hair.noGraphic)
                    continue;
                if (hair.requiredGene != null || hair.requiredMutant != null)
                    continue;

                options.Add(new Dictionary<string, object>
                {
                    ["defName"] = hair.defName,
                    ["label"] = hair.label ?? hair.defName
                });
            }
            return options;
        }

        private static List<object> GetGenderOptions()
        {
            return new List<object> { "Male", "Female" };
        }

        private static List<Dictionary<string, object>> GetTraitOptions()
        {
            var options = new List<Dictionary<string, object>>();
            foreach (TraitDef trait in DefDatabase<TraitDef>.AllDefsListForReading.OrderBy(t => t.label ?? t.defName))
            {
                if (trait == null || trait.degreeDatas == null || trait.degreeDatas.Count == 0)
                    continue;

                foreach (TraitDegreeData degree in trait.degreeDatas)
                {
                    if (degree == null)
                        continue;
                    string label = degree.label;
                    if (string.IsNullOrEmpty(label))
                        label = trait.label ?? trait.defName;
                    options.Add(new Dictionary<string, object>
                    {
                        ["defName"] = trait.defName,
                        ["label"] = label,
                        ["degree"] = degree.degree
                    });
                }
            }
            return options;
        }

        private const float NearbyEquipmentRadius = 24f;

        /// <summary>
        /// Enumerates spawned weapons/apparel within NearbyEquipmentRadius of the pawn.
        /// Uses the (small) Weapon and Apparel ThingRequestGroup lists rather than a
        /// whole-map listerThings.AllThings scan, so cost is O(loose weapons+apparel),
        /// independent of total colony thing count. Does NOT apply reachability/forbidden
        /// filtering — callers that need it add it (Serialize does; the fingerprint doesn't).
        /// </summary>
        private static IEnumerable<Thing> EnumerateNearbyEquipmentCandidates(Pawn pawn, Map map)
        {
            float radiusSq = NearbyEquipmentRadius * NearbyEquipmentRadius;
            IntVec3 origin = pawn.Position;

            var weapons = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon);
            for (int i = 0; i < weapons.Count; i++)
            {
                var thing = weapons[i];
                if (thing?.def == null || !thing.Spawned || thing.Destroyed)
                    continue;
                if ((thing.Position - origin).LengthHorizontalSquared > radiusSq)
                    continue;
                yield return thing;
            }

            var apparel = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel);
            for (int i = 0; i < apparel.Count; i++)
            {
                var thing = apparel[i];
                if (thing?.def == null || !thing.Spawned || thing.Destroyed)
                    continue;
                // A def in BOTH groups (wearable weapons) was already yielded above.
                if (thing.def.IsWeapon)
                    continue;
                if ((thing.Position - origin).LengthHorizontalSquared > radiusSq)
                    continue;
                yield return thing;
            }
        }

        private static List<Dictionary<string, object>> GetNearbyEquipment(Pawn pawn)
        {
            var items = new List<Dictionary<string, object>>();
            Map map = pawn?.Map;
            if (map == null)
                return items;

            foreach (Thing thing in EnumerateNearbyEquipmentCandidates(pawn, map))
            {
                try
                {
                    if (thing.IsForbidden(pawn))
                        continue;
                    if (!pawn.CanReserveAndReach(thing, PathEndMode.Touch, Danger.Deadly))
                        continue;
                }
                catch
                {
                    continue;
                }

                int hp = thing.MaxHitPoints > 0
                    ? (int)((float)thing.HitPoints / thing.MaxHitPoints * 100f)
                    : 100;
                var quality = thing.TryGetComp<CompQuality>()?.Quality;
                items.Add(new Dictionary<string, object>
                {
                    ["id"] = thing.thingIDNumber,
                    ["label"] = thing.LabelCap ?? thing.def.defName,
                    ["defName"] = thing.def.defName,
                    ["type"] = thing.def.IsWeapon ? "weapon" : "apparel",
                    ["slotKey"] = GearSlotKeyForDef(thing.def),
                    ["hp"] = hp,
                    ["distance"] = (int)thing.Position.DistanceTo(pawn.Position),
                    ["marketValue"] = (int)thing.MarketValue,
                    ["quality"] = quality.HasValue ? quality.Value.GetLabel() : "",
                    ["qualityRank"] = quality.HasValue ? (int)quality.Value : -1
                });
            }

            return items
                .OrderBy(item => item.ContainsKey("distance") ? (int)item["distance"] : 999)
                .ThenBy(item => item.ContainsKey("label") ? item["label"].ToString() : "")
                .Take(24)
                .ToList();
        }

        private static string GearSlotKeyForDef(ThingDef def)
        {
            if (def == null)
                return "other";
            if (def.IsWeapon)
                return "weapon";

            string text = ((def.defName ?? "") + " " + (def.label ?? "")).ToLowerInvariant();
            var apparel = def.apparel;
            if (apparel?.bodyPartGroups != null)
            {
                foreach (BodyPartGroupDef group in apparel.bodyPartGroups)
                    text += " " + (group?.defName ?? "") + " " + (group?.label ?? "");
            }
            if (apparel?.layers != null)
            {
                foreach (ApparelLayerDef layer in apparel.layers)
                    text += " " + (layer?.defName ?? "") + " " + (layer?.label ?? "");
            }

            if (ContainsAny(text, "head", "upperhead", "fullhead", "eyes", "helmet", "hat", "hood", "cowboy"))
                return "head";
            if (ContainsAny(text, "hand", "hands", "glove", "gauntlet"))
                return "hands";
            if (ContainsAny(text, "leg", "legs", "pant", "trouser", "skirt"))
                return "legs";
            if (ContainsAny(text, "shell", "belt", "parka", "duster", "jacket", "coat", "outer", "armor", "armour", "vest"))
                return "outer";
            if (ContainsAny(text, "torso", "shirt", "t-shirt", "skin", "middle"))
                return "torso";
            return "other";
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (string needle in needles)
            {
                if (!string.IsNullOrEmpty(needle) && text.Contains(needle))
                    return true;
            }
            return false;
        }

        private static IEnumerable<WorkTypeDef> GetWorkTypesInGameOrder()
        {
            try
            {
                var utility = typeof(WorkTypeDef).Assembly.GetType("RimWorld.WorkTypeDefsUtility");
                var prop = utility?.GetProperty("WorkTypeDefsInPriorityOrder", BindingFlags.Public | BindingFlags.Static);
                var propValue = prop?.GetValue(null, null) as IEnumerable;
                if (propValue != null)
                    return propValue.Cast<WorkTypeDef>().Where(wt => wt != null).ToList();

                var field = utility?.GetField("WorkTypeDefsInPriorityOrder", BindingFlags.Public | BindingFlags.Static);
                var fieldValue = field?.GetValue(null) as IEnumerable;
                if (fieldValue != null)
                    return fieldValue.Cast<WorkTypeDef>().Where(wt => wt != null).ToList();
            }
            catch { }

            return DefDatabase<WorkTypeDef>.AllDefsListForReading;
        }

        /// <summary>
        /// Fingerprint of the pawn state rendered by the viewer. If the
        /// fingerprint matches the last one, the full Serialize() can be skipped.
        /// Keep this in step with Serialize(); omitting a visible field can leave
        /// the viewer stuck with stale work, gear, health, or policy details.
        /// </summary>
        public static int ComputeStateSignature(Pawn pawn)
        {
            if (pawn == null) return 0;

            unchecked
            {
                int hash = 17;
                hash = hash * 31 + pawn.thingIDNumber;
                AddStringHash(ref hash, pawn.LabelShort);
                AddStringHash(ref hash, pawn.Name?.ToStringFull);
                hash = hash * 31 + pawn.Position.x;
                hash = hash * 31 + pawn.Position.z;
                hash = hash * 31 + (pawn.Drafted ? 1 : 0);
                hash = hash * 31 + (pawn.Dead ? 2 : 0);
                hash = hash * 31 + (pawn.Downed ? 4 : 0);

                if (pawn.health != null)
                {
                    int hp = (int)((pawn.health.summaryHealth?.SummaryHealthPercent ?? 1f) * 100f);
                    int pain = (int)((pawn.health.hediffSet?.PainTotal ?? 0f) * 100f);
                    int hediffCount = pawn.health.hediffSet?.hediffs?.Count ?? 0;
                    hash = hash * 31 + hp;
                    hash = hash * 31 + pain;
                    hash = hash * 31 + hediffCount;

                    if (pawn.health.hediffSet?.hediffs != null)
                    {
                        foreach (var hediff in pawn.health.hediffSet.hediffs)
                        {
                            if (hediff?.def == null) continue;
                            AddStringHash(ref hash, hediff.def.defName);
                            AddStringHash(ref hash, hediff.Part?.def?.defName);
                            hash = hash * 31 + (int)(hediff.Severity * 100f);
                        }
                    }
                }

                if (pawn.needs != null)
                {
                    foreach (var need in pawn.needs.AllNeeds)
                    {
                        if (need?.def == null) continue;
                        AddStringHash(ref hash, need.def.defName);
                        hash = hash * 31 + (int)(need.CurLevelPercentage * 100f);
                    }
                }

                if (pawn.equipment?.Primary != null)
                {
                    hash = hash * 31 + pawn.equipment.Primary.thingIDNumber;
                    AddStringHash(ref hash, pawn.equipment.Primary.def?.defName);
                }
                else
                {
                    hash = hash * 31 + 0;
                }

                if (pawn.apparel?.WornApparel != null)
                {
                    hash = hash * 31 + pawn.apparel.WornApparel.Count;
                    foreach (var apparel in pawn.apparel.WornApparel)
                    {
                        if (apparel?.def == null) continue;
                        hash = hash * 31 + apparel.thingIDNumber;
                        AddStringHash(ref hash, apparel.def.defName);
                        hash = hash * 31 + (int)((float)apparel.HitPoints / apparel.MaxHitPoints * 100f);
                    }
                }

                if (pawn.playerSettings != null)
                    hash = hash * 31 + (int)pawn.playerSettings.hostilityResponse;

                if (pawn.inventory?.innerContainer != null)
                {
                    foreach (var thing in pawn.inventory.innerContainer)
                    {
                        if (thing?.def == null) continue;
                        hash = hash * 31 + thing.thingIDNumber;
                        AddStringHash(ref hash, thing.def.defName);
                        hash = hash * 31 + thing.stackCount;
                    }
                }

                var jobLabel = pawn.jobs?.curDriver?.GetReport();
                if (!string.IsNullOrEmpty(jobLabel))
                    AddStringHash(ref hash, jobLabel);

                // Slow-moving, expensive-to-walk state (skills, thoughts, work, schedule,
                // traits, policies, capacities, relations/opinions, nearby ground gear,
                // backstory/appearance) — sampled every ~2s per pawn and invalidated
                // immediately on viewer commands. See GetSlowSubHash.
                hash = hash * 31 + GetSlowSubHash(pawn);

                return hash;
            }
        }

        private static void AddStringHash(ref int hash, string value)
        {
            if (!string.IsNullOrEmpty(value))
                hash = hash * 31 + value.GetHashCode();
        }
    }
}
