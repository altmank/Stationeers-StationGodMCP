#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A build state entry (ToolEntry or ToolEntry2): an item key, its quantity, whether it is a tool.</summary>
internal readonly struct BuildEntry
{
    internal BuildEntry(int item, int quantity, bool isTool)
    {
        Item = item;
        Quantity = quantity;
        IsTool = isTool;
    }

    /// <summary>The item's key (its PrefabHash in the game).</summary>
    internal int Item { get; }

    internal int Quantity { get; }

    /// <summary>A welder, wrench, grinder...: used, never consumed or given back.</summary>
    internal bool IsTool { get; }
}

/// <summary>An item key and a count.</summary>
internal readonly struct ItemCount
{
    internal ItemCount(int item, int quantity)
    {
        Item = item;
        Quantity = quantity;
    }

    internal int Item { get; }

    internal int Quantity { get; }
}

/// <summary>
/// One item of one swap: what building the new piece up to its final state costs, what deconstructing the old piece
/// from its current state gives back, and the difference either way.
/// </summary>
internal sealed class MaterialLine
{
    internal MaterialLine(int item, int cost, int refund)
    {
        Item = item;
        Cost = cost;
        Refund = refund;
    }

    internal int Item { get; }

    internal int Cost { get; }

    internal int Refund { get; }

    /// <summary>Cost less refund: positive is taken from the source, negative is given back.</summary>
    internal int Net => Cost - Refund;

    internal int Charge => Math.Max(0, Net);

    internal int GiveBack => Math.Max(0, -Net);
}

/// <summary>
/// The game's material rule (ToolUse.SpawnItem, ToolUse.Deconstruct): build state i needs its ToolEntry times
/// EntryQuantity and its ToolEntry2 times EntryQuantity2, skipping tools and zero quantities; deconstructing gives
/// each state back the same way down to state 0, whose entry is the kit. A swap is netted per item as a merge
/// placement charges only the difference: take what the new piece needs beyond what the old one returns, give back
/// the rest.
/// </summary>
internal static class MaterialRule
{
    /// <summary>What states 0 to lastState take (or give back), summed per item in order of first appearance.</summary>
    internal static List<ItemCount> Totals(IReadOnlyList<IReadOnlyList<BuildEntry>> states, int lastState)
    {
        List<int> items = new List<int>();
        List<int> counts = new List<int>();
        for (int state = 0; state <= lastState && state < states.Count; state++)
        {
            foreach (BuildEntry entry in states[state])
            {
                if (entry.IsTool || entry.Quantity <= 0)
                {
                    continue;
                }

                int at = items.IndexOf(entry.Item);
                if (at < 0)
                {
                    items.Add(entry.Item);
                    counts.Add(entry.Quantity);
                }
                else
                {
                    counts[at] += entry.Quantity;
                }
            }
        }

        List<ItemCount> totals = new List<ItemCount>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            totals.Add(new ItemCount(items[index], counts[index]));
        }

        return totals;
    }

    /// <summary>Cost and refund lined up per item: the cost's items first, then items only the refund has.</summary>
    internal static List<MaterialLine> Net(IReadOnlyList<ItemCount> cost, IReadOnlyList<ItemCount> refund)
    {
        List<MaterialLine> lines = new List<MaterialLine>(cost.Count + refund.Count);
        foreach (ItemCount item in cost)
        {
            lines.Add(new MaterialLine(item.Item, item.Quantity, QuantityOf(refund, item.Item)));
        }

        foreach (ItemCount item in refund)
        {
            if (!Has(cost, item.Item))
            {
                lines.Add(new MaterialLine(item.Item, 0, item.Quantity));
            }
        }

        return lines;
    }

    /// <summary>
    /// Several swaps' lines summed per item. Charge and give back are summed per swap, not netted across swaps: each
    /// swap takes its own charge when it is made, and everything given back is delivered after the last one.
    /// </summary>
    internal static List<MaterialTotal> Sum(IEnumerable<IReadOnlyList<MaterialLine>> swaps)
    {
        List<MaterialTotal> totals = new List<MaterialTotal>();
        foreach (IReadOnlyList<MaterialLine> lines in swaps)
        {
            foreach (MaterialLine line in lines)
            {
                MaterialTotal? total = totals.Find(existing => existing.Item == line.Item);
                if (total == null)
                {
                    total = new MaterialTotal(line.Item);
                    totals.Add(total);
                }

                total.Add(line);
            }
        }

        return totals;
    }

    private static int QuantityOf(IReadOnlyList<ItemCount> counts, int item)
    {
        foreach (ItemCount count in counts)
        {
            if (count.Item == item)
            {
                return count.Quantity;
            }
        }

        return 0;
    }

    private static bool Has(IReadOnlyList<ItemCount> counts, int item)
    {
        foreach (ItemCount count in counts)
        {
            if (count.Item == item)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One item over a whole run: gross cost and refund, and the per-swap charges and give-backs summed.</summary>
internal sealed class MaterialTotal
{
    internal MaterialTotal(int item)
    {
        Item = item;
    }

    internal int Item { get; }

    internal int Cost { get; private set; }

    internal int Refund { get; private set; }

    internal int Charge { get; private set; }

    internal int GiveBack { get; private set; }

    internal void Add(MaterialLine line)
    {
        Cost += line.Cost;
        Refund += line.Refund;
        Charge += line.Charge;
        GiveBack += line.GiveBack;
    }
}
