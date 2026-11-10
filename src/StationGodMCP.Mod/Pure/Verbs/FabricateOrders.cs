#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>One ingot prefab to load into a fabricator's stock, and how many ingot units.</summary>
internal sealed class LoadEntry
{
    internal LoadEntry(PrefabPattern prefab, double quantity)
    {
        Prefab = prefab;
        Quantity = quantity;
    }

    public PrefabPattern Prefab { get; }

    public double Quantity { get; }
}

/// <summary>
/// write fabricator's call, checked without the game: the one item to make (a whole prefab name or a hash; left out
/// only when make is false), the ingots to load first (at most FabricateRule.MaximumLoads entries, each more than 0 and
/// at most FabricateRule.MaximumLoadQuantity; entries of one ingot are merged by summing), and make (default true).
/// </summary>
internal sealed class FabricateOrder
{
    private FabricateOrder(PrefabPattern? prefab, List<LoadEntry> loads, bool make)
    {
        Prefab = prefab;
        Loads = loads;
        Make = make;
    }

    /// <summary>The item to make; null when make is false and none was given.</summary>
    public PrefabPattern? Prefab { get; }

    public List<LoadEntry> Loads { get; }

    public bool Make { get; }

    internal static FabricateOrder? Of(PrefabFilter? prefab, JToken? load, bool? make, List<OptionProblem> problems)
    {
        int before = problems.Count;
        bool making = make ?? true;

        PrefabPattern? pattern = null;
        if (prefab != null)
        {
            if (prefab.Patterns.Count == 1 && (prefab.Patterns[0] is PrefabPattern.Whole ||
                                               prefab.Patterns[0] is PrefabPattern.Hash))
            {
                pattern = prefab.Patterns[0];
            }
            else
            {
                problems.Add(new OptionProblem("prefab",
                    "is one item's whole name or its hash, not a * pattern or a list"));
            }
        }
        else if (making)
        {
            problems.Add(new OptionProblem("prefab", "is needed: the item to make (a prefab name or hash)"));
        }

        List<LoadEntry> loads = ReadLoads(load, problems);
        if (!making && loads.Count == 0 && problems.Count == before)
        {
            problems.Add(new OptionProblem("load",
                "is needed when make is false: with no load and no make there is nothing to do"));
        }

        return problems.Count == before ? new FabricateOrder(making ? pattern : null, loads, making) : null;
    }

    private static List<LoadEntry> ReadLoads(JToken? load, List<OptionProblem> problems)
    {
        List<LoadEntry> merged = new List<LoadEntry>();
        if (load == null)
        {
            return merged;
        }

        if (!(load is JArray entries))
        {
            problems.Add(new OptionProblem("load", "is a list of {prefab, quantity}"));
            return merged;
        }

        if (entries.Count > FabricateRule.MaximumLoads)
        {
            problems.Add(new OptionProblem("load",
                $"has {entries.Count} entries; at most {FabricateRule.MaximumLoads} are loaded in one call"));
            return merged;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"load[{index}]";
            if (!(entries[index] is JObject entry))
            {
                problems.Add(new OptionProblem(path, "is {prefab, quantity}"));
                continue;
            }

            PrefabPattern? pattern = entry["prefab"] is { } given ? PrefabPattern.Of(given) : null;
            if (pattern == null || pattern is PrefabPattern.Parts)
            {
                problems.Add(new OptionProblem(path + ".prefab", "is needed: one ingot's whole prefab name or hash"));
            }

            double quantity = OptionReader.NumberOf(entry["quantity"]) ?? double.NaN;
            if (!(quantity > 0.0) || double.IsInfinity(quantity) || quantity > FabricateRule.MaximumLoadQuantity)
            {
                problems.Add(new OptionProblem(path + ".quantity",
                    $"must be more than 0 and at most {FabricateRule.MaximumLoadQuantity:0}"));
            }

            if (pattern == null || pattern is PrefabPattern.Parts || !(quantity > 0.0) ||
                double.IsInfinity(quantity) || quantity > FabricateRule.MaximumLoadQuantity)
            {
                continue;
            }

            if (!Merge(merged, pattern, quantity))
            {
                problems.Add(new OptionProblem(path + ".quantity",
                    $"adds up with the other entries of that ingot to more than {FabricateRule.MaximumLoadQuantity:0}"));
            }
        }

        return merged;
    }

    // Entries of one ingot (the same name ignoring case, or the same hash) become one, quantities summed, first seen first.
    // False when the merged total passes the cap.
    private static bool Merge(List<LoadEntry> merged, PrefabPattern pattern, double quantity)
    {
        for (int index = 0; index < merged.Count; index++)
        {
            if (Same(merged[index].Prefab, pattern))
            {
                double total = merged[index].Quantity + quantity;
                if (total > FabricateRule.MaximumLoadQuantity)
                {
                    return false;
                }

                merged[index] = new LoadEntry(merged[index].Prefab, total);
                return true;
            }
        }

        merged.Add(new LoadEntry(pattern, quantity));
        return true;
    }

    private static bool Same(PrefabPattern a, PrefabPattern b) =>
        a is PrefabPattern.Hash ha && b is PrefabPattern.Hash hb
            ? ha.Value == hb.Value
            : a is PrefabPattern.Whole wa && b is PrefabPattern.Whole wb &&
              string.Equals(wa.Name, wb.Name, StringComparison.OrdinalIgnoreCase);
}
