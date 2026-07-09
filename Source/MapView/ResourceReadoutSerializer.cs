using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Aggregated colony stock totals for the viewer resource readout
    /// (RimWorld left-panel style), sourced from Map.resourceCounter.
    /// </summary>
    public static class ResourceReadoutSerializer
    {
        public const int MaxRows = 80;

        private static readonly string[] CategoryOrder =
        {
            "food", "medicine", "materials", "weapons", "apparel", "other"
        };

        public static Dictionary<string, object> Serialize(Map map, out int hash)
        {
            hash = 0;
            if (map?.resourceCounter == null)
                return null;

            try
            {
                var amounts = map.resourceCounter.AllCountedAmounts;
                if (amounts == null)
                    return null;

                var rows = new List<Dictionary<string, object>>();
                unchecked
                {
                    hash = 17;
                    foreach (var kv in amounts)
                    {
                        var def = kv.Key;
                        int count = kv.Value;
                        if (def == null || count <= 0)
                            continue;
                        if (def.resourceReadoutPriority == ResourceCountPriority.Uncounted &&
                            !def.resourceReadoutAlwaysShow)
                            continue;

                        string category = Classify(def);
                        string defName = def.defName ?? "";
                        string label = def.LabelCap.ToString();
                        if (string.IsNullOrEmpty(label))
                            label = defName;
                        hash = hash * 31 + defName.GetHashCode();
                        hash = hash * 31 + count;
                        hash = hash * 31 + category.GetHashCode();

                        rows.Add(new Dictionary<string, object>
                        {
                            ["def"] = defName,
                            ["label"] = label,
                            ["count"] = count,
                            ["category"] = category,
                            ["priority"] = (int)def.resourceReadoutPriority
                        });
                    }
                }

                rows = rows
                    .OrderBy(r => CategorySortKey((string)r["category"]))
                    .ThenByDescending(r => (int)r["priority"])
                    .ThenBy(r => (string)r["label"], StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRows)
                    .Select(r =>
                    {
                        r.Remove("priority");
                        return r;
                    })
                    .ToList();

                return new Dictionary<string, object>
                {
                    ["type"] = StateProtocol.ResourceReadout,
                    ["resources"] = rows,
                    ["hash"] = hash
                };
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Resource readout serialize failed: {ex.Message}");
                hash = 0;
                return null;
            }
        }

        private static int CategorySortKey(string category)
        {
            int idx = Array.IndexOf(CategoryOrder, category ?? "other");
            return idx >= 0 ? idx : CategoryOrder.Length;
        }

        private static string Classify(ThingDef def)
        {
            try
            {
                if (def.IsNutritionGivingIngestible)
                    return "food";
                if (def.IsMedicine)
                    return "medicine";
                if (def.IsWeapon)
                    return "weapons";
                if (def.IsApparel)
                    return "apparel";
                if (def.CountAsResource)
                    return "materials";

                if (def.thingCategories != null)
                {
                    foreach (var cat in def.thingCategories)
                    {
                        string name = cat?.defName ?? "";
                        if (name.IndexOf("Food", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "food";
                        if (name.IndexOf("Medicine", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "medicine";
                        if (name.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "weapons";
                        if (name.IndexOf("Apparel", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "apparel";
                        if (name.IndexOf("Resource", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Manufactured", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "materials";
                    }
                }
            }
            catch { }

            return "other";
        }
    }
}
