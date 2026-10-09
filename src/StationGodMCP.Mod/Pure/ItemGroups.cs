#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>find_items group_by: each entry alone (none), summed per outermost holder and prefab, or per prefab.</summary>
internal enum ItemGrouping
{
    None,
    Holder,
    Prefab
}

/// <summary>The group_by argument's words.</summary>
internal static class ItemGroupings
{
    internal const string Holder = "holder";
    internal const string Prefab = "prefab";

    /// <summary>holder or prefab, any case and trimmed; absent is none.</summary>
    internal static bool TryParse(string? text, out ItemGrouping grouping)
    {
        string? word = text?.Trim();
        grouping = word == null ? ItemGrouping.None
            : string.Equals(word, Holder, StringComparison.OrdinalIgnoreCase) ? ItemGrouping.Holder
            : string.Equals(word, Prefab, StringComparison.OrdinalIgnoreCase) ? ItemGrouping.Prefab
            : ItemGrouping.None;
        return word == null || grouping != ItemGrouping.None;
    }
}

/// <summary>One group of found entries: the first in list order (it names the group), how many, their sum and holders.</summary>
internal sealed class ItemGroup<T>
{
    private readonly HashSet<long> _holders = new HashSet<long>();

    internal ItemGroup(T first)
    {
        First = first;
    }

    internal T First { get; }

    /// <summary>Entries summed: stacks, a machine's stock of one reagent, a silo entry.</summary>
    internal int Entries { get; private set; }

    internal double Quantity { get; private set; }

    /// <summary>Distinct outermost holders; loose entries have none.</summary>
    internal int Holders => _holders.Count;

    internal void Add(long? holder, double quantity)
    {
        Entries++;
        Quantity += quantity;
        if (holder.HasValue)
        {
            _holders.Add(holder.Value);
        }
    }
}

/// <summary>
/// find_items group_by over the entries in list order: holder sums entries of one prefab (or one working-load reagent)
/// in one outermost holder, loose entries of a prefab together with no holder; prefab sums every entry of it. Groups
/// come in the order of their first entry, so the list's order still holds (nearest first, or by reference id).
/// </summary>
internal static class ItemGroups
{
    internal static List<ItemGroup<T>> Of<T>(IReadOnlyList<T> entries, ItemGrouping by, Func<T, long?> holder,
        Func<T, string> name, Func<T, double> quantity)
    {
        Dictionary<(long?, string), ItemGroup<T>> byKey = new Dictionary<(long?, string), ItemGroup<T>>();
        List<ItemGroup<T>> groups = new List<ItemGroup<T>>();
        foreach (T entry in entries)
        {
            long? holderId = holder(entry);
            (long?, string) key = (by == ItemGrouping.Holder ? holderId : null, name(entry));
            if (!byKey.TryGetValue(key, out ItemGroup<T> group))
            {
                group = new ItemGroup<T>(entry);
                byKey[key] = group;
                groups.Add(group);
            }

            group.Add(holderId, quantity(entry));
        }

        return groups;
    }
}
