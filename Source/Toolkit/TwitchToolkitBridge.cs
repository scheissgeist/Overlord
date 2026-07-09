using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using RimWorld;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Optional bridge into Twitch Toolkit / ToolkitUtils. This deliberately
    /// avoids hard assembly references so Overlord still loads without them.
    /// </summary>
    public static class TwitchToolkitBridge
    {
        private const int MaxStoreEntries = 700;
        private const int MaxItemEntries = 560;
        private const int MaxStuffOptions = 48;

        private static Type fakeMessageType;
        private static Type fakeMessageInterface;

        public static bool IsToolkitLoaded => FindType("TwitchToolkit.Viewers") != null;
        public static bool IsToolkitUtilsLoaded => FindType("SirRandoo.ToolkitUtils.CommandRouter") != null;
        public static bool IsBridgeAvailable =>
            FindType("TwitchToolkit.Viewers") != null &&
            FindType("TwitchToolkit.Store.Purchase_Handler") != null;

        public static bool IsChatConnected => GetToolkitChatConnected();

        public static Dictionary<string, object> BuildViewerState(string username)
        {
            var msg = new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ToolkitState,
                ["available"] = IsBridgeAvailable,
                ["toolkitLoaded"] = IsToolkitLoaded,
                ["toolkitUtilsLoaded"] = IsToolkitUtilsLoaded,
                ["chatConnected"] = IsChatConnected
            };

            if (!IsBridgeAvailable)
            {
                msg["status"] = "missing";
                msg["message"] = "Twitch Toolkit is not loaded in the RimWorld host.";
                msg["entries"] = new List<Dictionary<string, object>>();
                return msg;
            }

            try
            {
                object viewer = GetViewer(username);
                int coins = InvokeInt(viewer, "GetViewerCoins", 0);
                int karma = InvokeInt(viewer, "GetViewerKarma", 0);
                bool unlimited = ReadStaticBool("TwitchToolkit.ToolkitSettings", "UnlimitedCoins", false);
                bool earningCoins = ReadStaticBool("TwitchToolkit.ToolkitSettings", "EarningCoins", false);
                int coinAmount = ReadStaticInt("TwitchToolkit.ToolkitSettings", "CoinAmount", 0);
                int coinInterval = ReadStaticInt("TwitchToolkit.ToolkitSettings", "CoinInterval", 0);
                int minimumPurchase = ReadStaticInt("TwitchToolkit.ToolkitSettings", "MinimumPurchasePrice", 0);

                msg["status"] = IsChatConnected ? "connected" : "offline";
                msg["username"] = NormalizeUsername(username);
                msg["coins"] = coins;
                msg["karma"] = karma;
                msg["unlimitedCoins"] = unlimited;
                msg["earningCoins"] = earningCoins;
                msg["coinAmount"] = coinAmount;
                msg["coinInterval"] = coinInterval;
                msg["minimumPurchase"] = minimumPurchase;
                msg["entries"] = BuildStoreEntries(coins, unlimited);
                msg["itemCount"] = GetStoreItemCount();
            }
            catch (Exception ex)
            {
                msg["available"] = false;
                msg["status"] = "error";
                msg["message"] = "Toolkit bridge error: " + ex.Message;
                msg["entries"] = new List<Dictionary<string, object>>();
            }

            return msg;
        }

        public static Dictionary<string, object> ExecutePurchase(string username, string purchase, int quantity, string argument, Pawn targetPawn = null)
        {
            if (!IsBridgeAvailable)
                return Error("Twitch Toolkit is not loaded");

            if (!IsChatConnected)
                return Error("Twitch Toolkit chat is not connected in RimWorld");

            string sku = NormalizeSku(purchase);
            if (string.IsNullOrEmpty(sku))
                return Error("Missing purchase");

            quantity = Math.Max(1, Math.Min(quantity, 100));

            PurchaseInfo info;
            if (!TryFindPurchase(sku, out info))
                return Error("Toolkit purchase not found: " + sku);

            string purchaseArgument = NormalizePurchaseArgument(argument);
            if (info.needsInput && string.IsNullOrEmpty(purchaseArgument))
                return Error("That purchase needs a selection.");

            // Pawn-targeted Toolkit SKUs (healme, trait, etc.) resolve against Toolkit's
            // GameComponentPawns.pawnHistory — sync it to Overlord's assigned pawn first.
            if (IsPawnTargetedPurchase(sku, info))
            {
                if (targetPawn == null || targetPawn.Dead || targetPawn.Destroyed)
                    return Error("Assign a colonist in Overlord before buying pawn Toolkit items");
                if (!TrySyncViewerPawn(username, targetPawn, out string syncError))
                    return Error(syncError ?? "Could not link Toolkit to your Overlord colonist");
            }

            object viewer = GetViewer(username);
            int coins = InvokeInt(viewer, "GetViewerCoins", 0);
            bool unlimited = ReadStaticBool("TwitchToolkit.ToolkitSettings", "UnlimitedCoins", false);
            int finalCost = info.kind == "item"
                ? CalculateToolkitItemCost(info.sku, quantity, info.cost)
                : info.cost;
            if (!unlimited && finalCost > coins)
                return Error($"Not enough coins: {finalCost} required, {coins} available");

            int minimumPurchase = ReadStaticInt("TwitchToolkit.ToolkitSettings", "MinimumPurchasePrice", 0);
            if (info.kind == "item" && minimumPurchase > 0 && finalCost < minimumPurchase)
                return Error($"Minimum purchase is {minimumPurchase} coins");

            // Toolkit enforces per-viewer item/event cooldowns and maxed purchase windows.
            // Check before both the stuff path and ResolvePurchase so Overlord Buy shows the block.
            if (ToolkitItemBlockedByCooldown(username))
                return Error(DescribeToolkitCooldown(username, info));

            if (string.Equals(info.kind, "item", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(purchaseArgument))
            {
                return ExecuteItemPurchaseWithStuff(username, info, quantity, purchaseArgument, viewer, finalCost, unlimited);
            }

            try
            {
                Type purchaseHandler = FindType("TwitchToolkit.Store.Purchase_Handler");
                Type messageInterface = FindType("TwitchLib.Client.Models.Interfaces.ITwitchMessage");
                if (purchaseHandler == null || messageInterface == null)
                    return Error("Toolkit purchase API is unavailable");

                MethodInfo resolve = purchaseHandler.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ResolvePurchase" && m.GetParameters().Length >= 2);
                if (resolve == null)
                    return Error("Toolkit purchase resolver is unavailable");

                string command = BuildPurchaseCommand(sku, info, quantity, purchaseArgument);
                object twitchMessage = CreateFakeTwitchMessage(messageInterface, NormalizeUsername(username), command);
                var parameters = resolve.GetParameters().Length == 3
                    ? new[] { viewer, twitchMessage, (object)false }
                    : new[] { viewer, twitchMessage };
                resolve.Invoke(null, parameters);

                return PurchaseOk(info.label, quantity);
            }
            catch (TargetInvocationException ex)
            {
                return Error(FormatToolkitRejection(ex.InnerException ?? ex));
            }
            catch (Exception ex)
            {
                return Error(FormatToolkitRejection(ex));
            }
        }

        private static List<Dictionary<string, object>> BuildStoreEntries(int coins, bool unlimited)
        {
            var entries = new List<Dictionary<string, object>>();
            AppendIncidentEntries(entries, "TwitchToolkit.Store.Purchase_Handler", "allStoreIncidentsSimple", coins, unlimited, "event");
            AppendIncidentEntries(entries, "TwitchToolkit.Store.Purchase_Handler", "allStoreIncidentsVariables", coins, unlimited, "event");
            AppendItemEntries(entries, coins, unlimited);

            return entries
                .Where(e => e.ContainsKey("sku"))
                .OrderByDescending(e => e.ContainsKey("affordable") && e["affordable"] is bool b && b)
                .ThenBy(e => e.ContainsKey("cost") ? Convert.ToInt32(e["cost"]) : int.MaxValue)
                .ThenBy(e => e.ContainsKey("label") ? e["label"].ToString() : "")
                .Take(MaxStoreEntries)
                .ToList();
        }

        private static void AppendIncidentEntries(List<Dictionary<string, object>> entries, string ownerTypeName, string fieldName, int coins, bool unlimited, string kind)
        {
            Type owner = FindType(ownerTypeName);
            FieldInfo field = owner?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            var list = field?.GetValue(null) as IEnumerable;
            if (list == null) return;

            foreach (object incident in list)
            {
                string sku = NormalizeSku(ReadString(incident, "abbreviation"));
                if (string.IsNullOrEmpty(sku)) continue;

                int cost = Math.Max(0, ReadInt(incident, "cost", 0));
                int variables = Math.Max(0, ReadInt(incident, "variables", 0));
                string label = ReadString(incident, "label");
                if (string.IsNullOrEmpty(label)) label = sku;
                string category = CategoryForPurchase(kind, sku, label, ReadString(incident, "description"));

                entries.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["category"] = category,
                    ["sku"] = sku,
                    ["label"] = label,
                    ["description"] = Truncate(ReadString(incident, "description"), 180),
                    ["cost"] = cost,
                    ["price"] = cost,
                    ["unitCost"] = cost,
                    ["karmaType"] = ReadString(incident, "karmaType"),
                    ["affordable"] = unlimited || coins >= cost,
                    ["needsInput"] = variables > 0,
                    ["variables"] = variables,
                    ["syntax"] = ReadString(incident, "syntax"),
                    ["command"] = "!buy " + sku
                });
            }
        }

        private static void AppendItemEntries(List<Dictionary<string, object>> entries, int coins, bool unlimited)
        {
            var items = GetStoreItemsEnumerable();
            if (items == null) return;

            var itemEntries = new List<Dictionary<string, object>>();
            foreach (object item in items)
            {
                string keyText;
                object storeItem = UnwrapStoreItem(item, out keyText);
                string sku = NormalizeSku(ReadFirstString(storeItem, "abr", "Abr", "abbreviation", "Abbreviation", "name", "Name", "itemName", "ItemName"));
                if (string.IsNullOrEmpty(sku))
                    sku = NormalizeSku(keyText);

                string defName = ReadFirstString(storeItem, "defname", "defName", "DefName", "thingDef", "ThingDef", "thing", "Thing", "def", "Def");
                int price = Math.Max(0, ReadFirstInt(storeItem, 0, "price", "Price", "cost", "Cost", "basePrice", "BasePrice"));
                price = Math.Max(0, InvokeItemPrice(storeItem, 1, price));
                if (string.IsNullOrEmpty(sku) || price <= 0) continue;

                ThingDef def = ResolveThingDef(defName);
                string label = LabelForThing(def, defName, sku);
                string category = CategoryForThing(def, defName, label);
                var stuffOptions = BuildStuffOptions(def);
                bool requiresResearch = ReadStaticBool("TwitchToolkit.IncidentHelpers.IncidentHelper_Settings.BuyItemSettings", "mustResearchFirst", false);
                string researchLabel;
                bool researched = IsThingResearched(def, out researchLabel);
                itemEntries.Add(new Dictionary<string, object>
                {
                    ["kind"] = "item",
                    ["category"] = category,
                    ["sku"] = sku,
                    ["label"] = label,
                    ["defName"] = defName ?? "",
                    ["description"] = defName ?? "",
                    ["cost"] = price,
                    ["price"] = price,
                    ["unitCost"] = price,
                    ["affordable"] = unlimited || coins >= price,
                    ["researched"] = researched,
                    ["mustResearchFirst"] = requiresResearch,
                    ["researchProject"] = researchLabel ?? "",
                    ["madeFromStuff"] = stuffOptions.Count > 0,
                    ["stuffOptions"] = stuffOptions,
                    ["isWeapon"] = def?.IsWeapon == true,
                    ["isApparel"] = def?.apparel != null,
                    ["isBuildable"] = def != null && (def.building != null || def.Minifiable || def.category == ThingCategory.Building),
                    ["needsInput"] = false,
                    ["command"] = "!buy " + sku
                });
            }

            foreach (var entry in SelectBalancedItemEntries(itemEntries))
            {
                entries.Add(entry);
            }
        }

        private static IEnumerable<Dictionary<string, object>> SelectBalancedItemEntries(List<Dictionary<string, object>> itemEntries)
        {
            var selected = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] categories = { "medical", "food", "weapons", "apparel", "buildables", "items" };
            int quota = Math.Max(8, MaxItemEntries / categories.Length);

            foreach (string category in categories)
            {
                foreach (var entry in SortedStoreEntries(itemEntries.Where(e => EntryCategory(e) == category)).Take(quota))
                {
                    if (TryAddBalancedEntry(selected, seen, entry) && selected.Count >= MaxItemEntries)
                        return selected;
                }
            }

            foreach (var entry in SortedStoreEntries(itemEntries))
            {
                if (TryAddBalancedEntry(selected, seen, entry) && selected.Count >= MaxItemEntries)
                    break;
            }

            return selected;
        }

        private static IEnumerable<Dictionary<string, object>> SortedStoreEntries(IEnumerable<Dictionary<string, object>> entries)
        {
            return entries
                .OrderByDescending(e => e.ContainsKey("affordable") && e["affordable"] is bool b && b)
                .ThenBy(e => e.ContainsKey("cost") ? Convert.ToInt32(e["cost"]) : int.MaxValue)
                .ThenBy(e => e.ContainsKey("label") ? e["label"].ToString() : "");
        }

        private static bool TryAddBalancedEntry(List<Dictionary<string, object>> selected, HashSet<string> seen, Dictionary<string, object> entry)
        {
            string sku = entry.TryGetValue("sku", out object skuValue) ? skuValue?.ToString() : null;
            if (string.IsNullOrEmpty(sku) || seen.Contains(sku))
                return false;
            seen.Add(sku);
            selected.Add(entry);
            return true;
        }

        private static string EntryCategory(Dictionary<string, object> entry)
        {
            return entry.TryGetValue("category", out object value) ? value?.ToString() ?? "" : "";
        }

        private static bool TryFindPurchase(string sku, out PurchaseInfo info)
        {
            info = null;
            var state = BuildStoreEntries(int.MaxValue, true);
            foreach (var entry in state)
            {
                if (!string.Equals(entry.TryGetValue("sku", out object value) ? value?.ToString() : null, sku, StringComparison.OrdinalIgnoreCase))
                    continue;

                info = new PurchaseInfo
                {
                    sku = sku,
                    label = entry.TryGetValue("label", out object label) ? label?.ToString() ?? sku : sku,
                    kind = entry.TryGetValue("kind", out object kind) ? kind?.ToString() ?? "event" : "event",
                    defName = entry.TryGetValue("defName", out object defName) ? defName?.ToString() ?? "" : "",
                    cost = entry.TryGetValue("cost", out object cost) ? Convert.ToInt32(cost) : 0,
                    needsInput = entry.TryGetValue("needsInput", out object needs) && needs is bool needsBool && needsBool,
                    variables = entry.TryGetValue("variables", out object variables) ? Convert.ToInt32(variables) : 0,
                    syntax = entry.TryGetValue("syntax", out object syntax) ? syntax?.ToString() ?? "" : "",
                    category = entry.TryGetValue("category", out object category) ? category?.ToString() ?? "" : ""
                };
                return true;
            }
            return false;
        }

        private static string BuildPurchaseCommand(string sku, PurchaseInfo info, int quantity, string argument)
        {
            if (string.Equals(info.kind, "item", StringComparison.OrdinalIgnoreCase))
                return "!buy " + sku + (quantity > 1 ? " " + quantity : "");

            return string.IsNullOrEmpty(argument)
                ? "!buy " + sku
                : "!buy " + sku + " " + argument;
        }

        private static Dictionary<string, object> ExecuteItemPurchaseWithStuff(
            string username,
            PurchaseInfo info,
            int quantity,
            string stuffDefName,
            object viewer,
            int finalCost,
            bool unlimited)
        {
            ThingDef itemDef = ResolveThingDef(info.defName);
            if (itemDef == null)
                return Error("Toolkit item def not found: " + info.defName);
            if (!itemDef.MadeFromStuff)
                return Error("That item does not support a material choice.");

            ThingDef stuffDef = ResolveThingDef(stuffDefName);
            if (!IsAllowedStuffFor(itemDef, stuffDef))
                return Error("That material is not valid for " + (itemDef.label ?? info.label));

            if (ReadStaticBool("TwitchToolkit.IncidentHelpers.IncidentHelper_Settings.BuyItemSettings", "mustResearchFirst", false) &&
                !IsThingResearched(itemDef, out string researchLabel))
            {
                return Error($"{GenText.CapitalizeFirst(itemDef.label ?? info.label)} requires research first: {researchLabel}");
            }

            if (ToolkitItemBlockedByCooldown(username))
                return Error(DescribeToolkitCooldown(username, info));

            Map map = Find.Maps?.FirstOrDefault(m => m != null && m.IsPlayerHome) ?? Find.CurrentMap;
            if (map == null)
                return Error("No player map is available for the drop pod.");

            try
            {
                Thing thing = MakeStoreThing(itemDef, stuffDef, quantity);
                IntVec3 dropCell = DropCellFinder.TradeDropSpot(map);
                TradeUtility.SpawnDropPod(dropCell, map, thing);

                if (!unlimited)
                    InvokeInstanceMethod(viewer, "TakeViewerCoins", finalCost);
                ApplyToolkitItemKarma(viewer, finalCost);
                TryLogToolkitItemPurchase(username, info.sku);

                string materialLabel = stuffDef.label ?? stuffDef.defName;
                string label = GenText.CapitalizeFirst((materialLabel + " " + (itemDef.label ?? info.label)).Trim());
                return PurchaseOk(label, quantity);
            }
            catch (Exception ex)
            {
                return Error(FormatToolkitRejection(ex));
            }
        }

        private static Dictionary<string, object> PurchaseOk(string label, int quantity)
        {
            string message = quantity > 1
                ? $"Purchase requested: {quantity} {label}"
                : $"Purchase requested: {label}";
            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = true,
                ["action"] = StateProtocol.CmdToolkitPurchase,
                ["message"] = message
            };
        }

        private static string DescribeToolkitCooldown(string username, PurchaseInfo info)
        {
            string label = info?.label ?? info?.sku ?? "item";
            string kind = string.Equals(info?.kind, "event", StringComparison.OrdinalIgnoreCase) ? "event" : "item";
            // Toolkit chat often says "X item is maxed, wait N days" — we cannot always read N
            // without Toolkit internals, so keep a clear Overlord-facing message.
            return $"{GenText.CapitalizeFirst(label)} {kind} is on Toolkit cooldown or maxed. Wait for the limit to reset, or ask the streamer to raise store limits.";
        }

        private static string FormatToolkitRejection(Exception ex)
        {
            string raw = ex?.Message ?? "unknown error";
            // Prefer the innermost message Toolkit threw (often the chat-facing string).
            Exception walk = ex;
            while (walk?.InnerException != null)
                walk = walk.InnerException;
            if (walk != null && !string.IsNullOrEmpty(walk.Message))
                raw = walk.Message;

            raw = raw.Trim();
            if (raw.StartsWith("Exception:", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("Exception:".Length).Trim();
            if (raw.IndexOf("maxed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("cooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("wait", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return raw;
            }
            return "Toolkit rejected purchase: " + raw;
        }

        private static Thing MakeStoreThing(ThingDef itemDef, ThingDef stuffDef, int quantity)
        {
            Thing thing = ThingMaker.MakeThing(itemDef, stuffDef);
            if (thing == null)
                throw new InvalidOperationException("ThingMaker returned null");

            QualityCategory quality;
            if (QualityUtility.TryGetQuality(thing, out quality))
            {
                var comp = thing.TryGetComp<CompQuality>();
                comp?.SetQuality(QualityUtility.GenerateQualityTraderItem(), ArtGenerationContext.Outsider);
            }

            if (itemDef.Minifiable)
            {
                var minified = (MinifiedThing)ThingMaker.MakeThing(itemDef.minifiedDef);
                minified.InnerThing = thing;
                minified.stackCount = quantity;
                return minified;
            }

            thing.stackCount = quantity;
            return thing;
        }

        private static bool IsAllowedStuffFor(ThingDef itemDef, ThingDef stuffDef)
        {
            if (itemDef == null || stuffDef == null || !itemDef.MadeFromStuff)
                return false;

            try
            {
                foreach (ThingDef allowed in GenStuff.AllowedStuffsFor(itemDef, (TechLevel)0, false))
                {
                    if (allowed == stuffDef && !PawnWeaponGenerator.IsDerpWeapon(itemDef, stuffDef))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static List<Dictionary<string, object>> BuildStuffOptions(ThingDef itemDef)
        {
            var options = new List<Dictionary<string, object>>();
            if (itemDef == null || !itemDef.MadeFromStuff)
                return options;

            try
            {
                foreach (ThingDef stuff in GenStuff.AllowedStuffsFor(itemDef, (TechLevel)0, false))
                {
                    if (stuff == null || PawnWeaponGenerator.IsDerpWeapon(itemDef, stuff))
                        continue;

                    options.Add(new Dictionary<string, object>
                    {
                        ["defName"] = stuff.defName,
                        ["label"] = stuff.label ?? stuff.defName,
                        ["category"] = StuffCategoryLabel(stuff),
                        ["marketValue"] = (int)Math.Round(stuff.BaseMarketValue),
                        ["commonality"] = stuff.stuffProps?.commonality ?? 0f
                    });
                }
            }
            catch { }

            return options
                .OrderBy(o => o.TryGetValue("category", out object category) ? category?.ToString() : "")
                .ThenBy(o => o.TryGetValue("label", out object label) ? label?.ToString() : "")
                .Take(MaxStuffOptions)
                .ToList();
        }

        private static string StuffCategoryLabel(ThingDef stuff)
        {
            try
            {
                var category = stuff?.stuffProps?.categories?.FirstOrDefault();
                if (!string.IsNullOrEmpty(category?.label))
                    return category.label;
                if (!string.IsNullOrEmpty(category?.defName))
                    return category.defName;
            }
            catch { }
            return "Material";
        }

        private static bool IsThingResearched(ThingDef def, out string researchLabel)
        {
            researchLabel = null;
            if (def == null)
                return true;

            try
            {
                if (def.recipeMaker?.researchPrerequisite != null &&
                    !def.recipeMaker.researchPrerequisite.IsFinished)
                {
                    researchLabel = def.recipeMaker.researchPrerequisite.LabelCap;
                    return false;
                }

                if (!def.IsResearchFinished)
                {
                    var research = def.researchPrerequisites?.FirstOrDefault();
                    researchLabel = research?.LabelCap ?? "unfinished research";
                    return false;
                }
            }
            catch { }

            return true;
        }

        private static object InvokeInstanceMethod(object target, string methodName, params object[] args)
        {
            if (target == null || string.IsNullOrEmpty(methodName))
                return null;

            try
            {
                MethodInfo method = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
                return method?.Invoke(target, args);
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyToolkitItemKarma(object viewer, int finalCost)
        {
            try
            {
                object itemIncident = ReadStaticMember(FindType("TwitchToolkit.Incidents.StoreIncidentDefOf"), "Item");
                object karmaType = ReadMember(itemIncident, "karmaType");
                if (karmaType != null)
                    InvokeInstanceMethod(viewer, "CalculateNewKarma", karmaType, finalCost);
            }
            catch { }
        }

        private static void TryLogToolkitItemPurchase(string username, string sku)
        {
            try
            {
                object itemIncident = ReadStaticMember(FindType("TwitchToolkit.Incidents.StoreIncidentDefOf"), "Item");
                Type storeComponentType = FindType("TwitchToolkit.Store.Store_Component");
                MethodInfo getComponent = typeof(Game)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                object component = getComponent?.MakeGenericMethod(storeComponentType).Invoke(Current.Game, null);
                MethodInfo logIncident = storeComponentType?.GetMethod("LogIncident", BindingFlags.Public | BindingFlags.Instance);
                if (component != null && itemIncident != null)
                    logIncident?.Invoke(component, new[] { itemIncident });

                Type logger = FindType("TwitchToolkit.Store.Store_Logger");
                MethodInfo logPurchase = logger?.GetMethod("LogPurchase", BindingFlags.Public | BindingFlags.Static);
                logPurchase?.Invoke(null, new object[] { NormalizeUsername(username), "!buy " + sku });
            }
            catch { }
        }

        private static bool ToolkitItemBlockedByCooldown(string username)
        {
            try
            {
                Type purchaseHandler = FindType("TwitchToolkit.Store.Purchase_Handler");
                if (purchaseHandler == null)
                    return false;

                MethodInfo variableList = purchaseHandler
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CheckIfViewerIsInVariableCommandList" && m.GetParameters().Length >= 1);
                if (InvokeBool(variableList, NormalizeUsername(username), false))
                    return true;

                MethodInfo carePackage = purchaseHandler
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CheckIfCarePackageIsOnCooldown" && m.GetParameters().Length >= 1);
                if (InvokeBool(carePackage, NormalizeUsername(username), false))
                    return true;

                object itemIncident = ReadStaticMember(FindType("TwitchToolkit.Incidents.StoreIncidentDefOf"), "Item");
                MethodInfo incidentCooldown = purchaseHandler
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CheckIfIncidentIsOnCooldown" && m.GetParameters().Length >= 2);
                return itemIncident != null && InvokeBool(incidentCooldown, itemIncident, NormalizeUsername(username), false);
            }
            catch
            {
                return false;
            }
        }

        private static bool InvokeBool(MethodInfo method, params object[] args)
        {
            if (method == null)
                return false;

            try
            {
                var parameters = method.GetParameters();
                object[] callArgs = args.Take(parameters.Length).ToArray();
                object value = method.Invoke(null, callArgs);
                return value is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static object GetViewer(string username)
        {
            Type viewers = FindType("TwitchToolkit.Viewers");
            MethodInfo getViewer = viewers?.GetMethod("GetViewer", BindingFlags.Public | BindingFlags.Static);
            if (getViewer == null)
                throw new InvalidOperationException("TwitchToolkit.Viewers.GetViewer not found");
            return getViewer.Invoke(null, new object[] { NormalizeUsername(username) });
        }

        /// <summary>
        /// Force Toolkit's viewer↔pawn map to Overlord's assigned colonist.
        /// ToolkitUtils healme/trait/etc. read TwitchToolkit.PawnQueue.GameComponentPawns.pawnHistory.
        /// </summary>
        public static bool TrySyncViewerPawn(string username, Pawn pawn, out string error)
        {
            error = null;
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username))
            {
                error = "Missing username";
                return false;
            }
            if (pawn == null || pawn.Dead || pawn.Destroyed)
            {
                error = "No colonist to link";
                return false;
            }

            try
            {
                object component = GetToolkitPawnComponent();
                if (component == null)
                {
                    error = "Toolkit pawn queue is unavailable";
                    return false;
                }

                IDictionary history = ReadPawnHistory(component);
                if (history == null)
                {
                    error = "Toolkit pawn history is unavailable";
                    return false;
                }

                // Drop any other Toolkit username currently pointing at this pawn.
                var staleKeys = new List<object>();
                foreach (DictionaryEntry entry in history)
                {
                    if (ReferenceEquals(entry.Value, pawn) &&
                        !string.Equals(Convert.ToString(entry.Key), username, StringComparison.OrdinalIgnoreCase))
                    {
                        staleKeys.Add(entry.Key);
                    }
                }
                foreach (object key in staleKeys)
                    history.Remove(key);

                // AssignUserToPawn uses Dictionary.Add and cannot rebind — set the indexer.
                history[username] = pawn;

                // Keep Toolkit's name queue consistent with AssignUserToPawn.
                try
                {
                    object queueObj = component.GetType()
                        .GetProperty("ViewerNameQueue", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(component, null);
                    if (queueObj is IList queue)
                    {
                        for (int i = queue.Count - 1; i >= 0; i--)
                        {
                            if (string.Equals(Convert.ToString(queue[i]), username, StringComparison.OrdinalIgnoreCase))
                                queue.RemoveAt(i);
                        }
                    }
                }
                catch { }

                LogUtil.Log($"Synced Toolkit pawn for {username} → {pawn.LabelShort}");
                return true;
            }
            catch (Exception ex)
            {
                error = "Toolkit pawn sync failed: " + ex.Message;
                LogUtil.Warn(error);
                return false;
            }
        }

        public static void ClearViewerPawn(string username)
        {
            username = NormalizeUsername(username);
            if (string.IsNullOrEmpty(username))
                return;

            try
            {
                object component = GetToolkitPawnComponent();
                IDictionary history = ReadPawnHistory(component);
                if (history == null)
                    return;

                object matchKey = null;
                foreach (DictionaryEntry entry in history)
                {
                    if (string.Equals(Convert.ToString(entry.Key), username, StringComparison.OrdinalIgnoreCase))
                    {
                        matchKey = entry.Key;
                        break;
                    }
                }
                if (matchKey != null)
                    history.Remove(matchKey);
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Toolkit pawn clear failed for {username}: {ex.Message}");
            }
        }

        public static bool IsPawnTargetedSku(string sku)
        {
            return IsPawnTargetedPurchase(sku, null);
        }

        private static bool IsPawnTargetedPurchase(string sku, PurchaseInfo info)
        {
            string key = NormalizeSku(sku);
            if (string.IsNullOrEmpty(key))
                return false;

            // healme is categorized "medical" by keyword heuristics — allowlist is authoritative.
            if (PawnTargetedSkus.Contains(key))
                return true;

            string category = info?.category;
            if (string.IsNullOrEmpty(category) && info != null)
                category = CategoryForPurchase(info.kind, info.sku, info.label, null);
            return string.Equals(category, "pawn", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> PawnTargetedSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "healme", "fullheal", "reviveme", "rescueme", "imstuck", "fixmypawn",
            "trait", "removetrait", "settraits", "levelskill", "passionshuffle", "genderswap"
        };

        private static object GetToolkitPawnComponent()
        {
            Type componentType = FindType("TwitchToolkit.PawnQueue.GameComponentPawns");
            if (componentType == null || Current.Game == null)
                return null;

            MethodInfo getComponent = typeof(Game)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            return getComponent?.MakeGenericMethod(componentType).Invoke(Current.Game, null);
        }

        private static IDictionary ReadPawnHistory(object component)
        {
            if (component == null)
                return null;
            FieldInfo field = component.GetType().GetField("pawnHistory", BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(component) as IDictionary;
        }

        private static object CreateFakeTwitchMessage(Type messageInterface, string username, string message)
        {
            if (fakeMessageType == null || fakeMessageInterface != messageInterface)
            {
                fakeMessageType = BuildFakeMessageType(messageInterface);
                fakeMessageInterface = messageInterface;
            }
            return Activator.CreateInstance(fakeMessageType, username, message);
        }

        private static Type BuildFakeMessageType(Type messageInterface)
        {
            var assemblyName = new AssemblyName("OverlordToolkitMessageProxy" + Guid.NewGuid().ToString("N"));
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("Main");
            var type = module.DefineType(
                "Overlord.ToolkitMessageProxy",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
            type.AddInterfaceImplementation(messageInterface);

            FieldBuilder usernameField = type.DefineField("_username", typeof(string), FieldAttributes.Private);
            FieldBuilder messageField = type.DefineField("_message", typeof(string), FieldAttributes.Private);

            ConstructorBuilder ctor = type.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                new[] { typeof(string), typeof(string) });
            ILGenerator il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, usernameField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stfld, messageField);
            il.Emit(OpCodes.Ret);

            foreach (PropertyInfo prop in messageInterface.GetProperties())
            {
                MethodInfo interfaceGetter = prop.GetGetMethod();
                if (interfaceGetter == null) continue;

                MethodBuilder getter = type.DefineMethod(
                    interfaceGetter.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                    prop.PropertyType,
                    Type.EmptyTypes);
                ILGenerator getterIl = getter.GetILGenerator();
                if (prop.Name == "Username" && prop.PropertyType == typeof(string))
                {
                    getterIl.Emit(OpCodes.Ldarg_0);
                    getterIl.Emit(OpCodes.Ldfld, usernameField);
                }
                else if (prop.Name == "Message" && prop.PropertyType == typeof(string))
                {
                    getterIl.Emit(OpCodes.Ldarg_0);
                    getterIl.Emit(OpCodes.Ldfld, messageField);
                }
                else if (prop.PropertyType.IsValueType)
                {
                    LocalBuilder local = getterIl.DeclareLocal(prop.PropertyType);
                    getterIl.Emit(OpCodes.Ldloca_S, local);
                    getterIl.Emit(OpCodes.Initobj, prop.PropertyType);
                    getterIl.Emit(OpCodes.Ldloc_0);
                }
                else
                {
                    getterIl.Emit(OpCodes.Ldnull);
                }
                getterIl.Emit(OpCodes.Ret);
                type.DefineMethodOverride(getter, interfaceGetter);
            }

            return type.CreateType();
        }

        private static bool GetToolkitChatConnected()
        {
            try
            {
                Type wrapper = FindType("ToolkitCore.TwitchWrapper");
                PropertyInfo clientProp = wrapper?.GetProperty("Client", BindingFlags.Public | BindingFlags.Static);
                object client = clientProp?.GetValue(null, null);
                if (client == null) return false;
                PropertyInfo connected = client.GetType().GetProperty("IsConnected", BindingFlags.Public | BindingFlags.Instance);
                return connected != null && connected.GetValue(client, null) is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static int GetStoreItemCount()
        {
            try
            {
                var items = GetStoreItemsEnumerable();
                if (items == null) return 0;
                if (items is ICollection collection) return collection.Count;

                int count = 0;
                foreach (object _ in items)
                {
                    count++;
                    if (count > MaxStoreEntries * 4)
                        break;
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        private static IEnumerable GetStoreItemsEnumerable()
        {
            Type inventory = FindType("TwitchToolkit.Store.StoreInventory");
            if (inventory == null) return null;
            EnsureStoreItemsGenerated(inventory);

            foreach (string name in new[] { "items", "Items", "storeItems", "StoreItems", "inventory", "Inventory" })
            {
                object value = ReadStaticMember(inventory, name);
                if (IsEnumerableValue(value))
                    return value as IEnumerable;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (FieldInfo field in inventory.GetFields(flags))
            {
                if (!LooksLikeItemCollectionName(field.Name)) continue;
                object value = field.GetValue(null);
                if (IsEnumerableValue(value))
                    return value as IEnumerable;
            }

            foreach (PropertyInfo prop in inventory.GetProperties(flags))
            {
                if (!LooksLikeItemCollectionName(prop.Name)) continue;
                if (prop.GetIndexParameters().Length != 0) continue;
                object value = prop.GetValue(null, null);
                if (IsEnumerableValue(value))
                    return value as IEnumerable;
            }

            return null;
        }

        private static void EnsureStoreItemsGenerated(Type inventory)
        {
            try
            {
                object current = ReadStaticMember(inventory, "items") ?? ReadStaticMember(inventory, "Items");
                if (current is ICollection collection && collection.Count > 0)
                    return;

                Type itemType = FindType("TwitchToolkit.Store.Item");
                MethodInfo method = itemType?.GetMethod("TryMakeAllItems", BindingFlags.Public | BindingFlags.Static);
                method?.Invoke(null, null);
            }
            catch
            {
                // Store generation is best-effort; reading the current Toolkit state below remains authoritative.
            }
        }

        private static object ReadStaticMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo field = type.GetField(name, flags);
            if (field != null) return field.GetValue(null);
            PropertyInfo prop = type.GetProperty(name, flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                return prop.GetValue(null, null);
            return null;
        }

        private static bool IsEnumerableValue(object value)
        {
            return value is IEnumerable && !(value is string);
        }

        private static bool LooksLikeItemCollectionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("item") || lower.Contains("store") || lower.Contains("inventory");
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            return null;
        }

        private static int InvokeInt(object target, string methodName, int fallback)
        {
            try
            {
                MethodInfo method = target?.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                object value = method?.Invoke(target, null);
                return value == null ? fallback : Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static bool ReadStaticBool(string typeName, string name, bool fallback)
        {
            object value = ReadStatic(typeName, name);
            return value is bool b ? b : fallback;
        }

        private static int ReadStaticInt(string typeName, string name, int fallback)
        {
            object value = ReadStatic(typeName, name);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value); }
            catch { return fallback; }
        }

        private static object ReadStatic(string typeName, string name)
        {
            Type type = FindType(typeName);
            if (type == null) return null;
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field != null) return field.GetValue(null);
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null, null);
        }

        private static string ReadString(object target, string name)
        {
            object value = ReadMember(target, name);
            return StringFromValue(value);
        }

        private static int ReadInt(object target, string name, int fallback)
        {
            object value = ReadMember(target, name);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value); }
            catch { return fallback; }
        }

        private static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field.GetValue(target);
                PropertyInfo prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.GetIndexParameters().Length == 0) return prop.GetValue(target, null);
                type = type.BaseType;
            }
            type = target.GetType();
            while (type != null)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                        return field.GetValue(target);
                }
                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.GetIndexParameters().Length == 0)
                        return prop.GetValue(target, null);
                }
                type = type.BaseType;
            }
            return null;
        }

        private static object UnwrapStoreItem(object item, out string keyText)
        {
            keyText = null;
            if (item is DictionaryEntry entry)
            {
                keyText = StringFromValue(entry.Key);
                return entry.Value ?? item;
            }

            object key = ReadMember(item, "Key");
            object value = ReadMember(item, "Value");
            if (key != null && value != null)
            {
                keyText = StringFromValue(key);
                return value;
            }

            return item;
        }

        private static string ReadFirstString(object target, params string[] names)
        {
            foreach (string name in names)
            {
                string value = StringFromValue(ReadMember(target, name));
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            return null;
        }

        private static int ReadFirstInt(object target, int fallback, params string[] names)
        {
            foreach (string name in names)
            {
                object value = ReadMember(target, name);
                if (value == null) continue;
                try { return Convert.ToInt32(value); }
                catch { }
            }
            return fallback;
        }

        private static int CalculateToolkitItemCost(string sku, int quantity, int fallbackUnitCost)
        {
            try
            {
                Type inventory = FindType("TwitchToolkit.Store.StoreInventory");
                if (inventory != null)
                    EnsureStoreItemsGenerated(inventory);
                Type itemType = FindType("TwitchToolkit.Store.Item");
                MethodInfo lookup = itemType?.GetMethod("GetItemFromAbr", BindingFlags.Public | BindingFlags.Static);
                object item = lookup?.Invoke(null, new object[] { sku });
                if (item == null)
                    return fallbackUnitCost * quantity;
                return InvokeItemPrice(item, quantity, fallbackUnitCost * quantity);
            }
            catch
            {
                return fallbackUnitCost * quantity;
            }
        }

        private static int InvokeItemPrice(object item, int quantity, int fallback)
        {
            try
            {
                MethodInfo method = item?.GetType().GetMethod("CalculatePrice", BindingFlags.Public | BindingFlags.Instance);
                object value = method?.Invoke(item, new object[] { quantity });
                return value == null ? fallback : Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static string StringFromValue(object value)
        {
            if (value == null) return null;
            if (value is Def def) return def.defName;
            if (value is string text) return text;

            object defName = ReadMember(value, "defName") ?? ReadMember(value, "DefName");
            if (defName is string nestedDefName && !string.IsNullOrEmpty(nestedDefName))
                return nestedDefName;

            return value.ToString();
        }

        private static ThingDef ResolveThingDef(string defName)
        {
            if (!string.IsNullOrEmpty(defName))
            {
                try
                {
                    return DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                }
                catch { }
            }
            return null;
        }

        private static string LabelForThing(ThingDef def, string defName, string fallback)
        {
            if (!string.IsNullOrEmpty(def?.label))
                return def.label;
            if (!string.IsNullOrEmpty(defName))
            {
                try
                {
                    def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    if (!string.IsNullOrEmpty(def?.label))
                        return def.label;
                }
                catch { }
            }
            return fallback;
        }

        private static string CategoryForThing(ThingDef def, string defName, string label)
        {
            string text = ((defName ?? "") + " " + (label ?? "")).ToLowerInvariant();
            try
            {
                if (def != null)
                {
                    if (def.IsWeapon)
                        text += " weapon";
                    if (def.apparel != null)
                        text += " apparel clothes armor";
                    if (def.building != null || def.Minifiable || def.category == ThingCategory.Building)
                        text += " buildable building";
                }
                if (def?.thingCategories != null)
                {
                    foreach (ThingCategoryDef category in def.thingCategories)
                        text += " " + (category?.defName ?? "") + " " + (category?.label ?? "");
                    text = text.ToLowerInvariant();
                }
            }
            catch { }

            if (ContainsAny(text, "medicine", "medical", "drug", "heal", "herbal", "neutroamine", "penoxycyline"))
                return "medical";
            if (ContainsAny(text, "meal", "food", "meat", "vegetable", "fruit", "milk", "egg", "pemmican", "survivalpack", "nutrition"))
                return "food";
            if (ContainsAny(text, "weapon", "gun", "rifle", "pistol", "revolver", "bow", "sword", "knife", "mace", "spear", "grenade", "launcher"))
                return "weapons";
            if (ContainsAny(text, "apparel", "armor", "armour", "helmet", "hat", "shirt", "pants", "jacket", "parka", "duster", "boots", "vest", "clothes", "clothing"))
                return "apparel";
            if (ContainsAny(text, "building", "buildable", "wall", "door", "floor", "bed", "table", "chair", "lamp", "battery", "generator", "turret", "bench", "workbench", "sculpture", "plant pot", "shelf"))
                return "buildables";
            return "items";
        }

        private static string CategoryForPurchase(string kind, string sku, string label, string description)
        {
            string text = ((kind ?? "") + " " + (sku ?? "") + " " + (label ?? "") + " " + (description ?? "")).ToLowerInvariant();
            if (ContainsAny(text, "heal", "revive", "rescue", "medicine", "medical", "injury", "pain", "disease", "infection"))
                return "medical";
            if (ContainsAny(text, "trait", "passion", "skill", "story", "pawn", "colonist", "hair", "gender", "appearance"))
                return "pawn";
            if (ContainsAny(text, "raid", "event", "incident", "weather", "manhunter", "trader", "drop", "quest", "threat"))
                return "events";
            if (ContainsAny(text, "meal", "food", "meat", "pemmican"))
                return "food";
            return string.Equals(kind, "item", StringComparison.OrdinalIgnoreCase) ? "items" : "events";
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

        private static string NormalizeUsername(string username)
        {
            return (username ?? "").Trim().ToLowerInvariant();
        }

        private static string NormalizeSku(string sku)
        {
            sku = (sku ?? "").Trim().ToLowerInvariant();
            if (sku.Length > 64) return null;
            foreach (char ch in sku)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))
                    return null;
            }
            return sku;
        }

        private static string NormalizePurchaseArgument(string argument)
        {
            argument = (argument ?? "").Trim();
            if (argument.Length == 0) return "";
            argument = argument.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (argument.Contains("  "))
                argument = argument.Replace("  ", " ");
            return argument.Length <= 96 ? argument : argument.Substring(0, 96);
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "...";
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                ["type"] = StateProtocol.ActionResult,
                ["ok"] = false,
                ["action"] = StateProtocol.CmdToolkitPurchase,
                ["message"] = message
            };
        }

        private class PurchaseInfo
        {
            public string sku;
            public string label;
            public string kind;
            public string defName;
            public string category;
            public int cost;
            public bool needsInput;
            public int variables;
            public string syntax;
        }
    }
}
