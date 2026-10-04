#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A network's members counted by prefab: how many of each, and of those with a colour, how many of each colour. The
/// list runs most first, then by prefab name.
/// </summary>
internal sealed class MemberTally
{
    private readonly Dictionary<string, MemberPrefabCount> _byPrefab = new Dictionary<string, MemberPrefabCount>(StringComparer.Ordinal);

    /// <summary>One member; its shown name is asked only for the first of its prefab.</summary>
    internal void Add(string prefab, Func<string?> displayName, string member, string? color)
    {
        if (!_byPrefab.TryGetValue(prefab, out MemberPrefabCount count))
        {
            count = new MemberPrefabCount(prefab, displayName(), member);
            _byPrefab[prefab] = count;
        }

        count.Add(color);
    }

    internal List<MemberPrefabCount> MostFirst()
    {
        List<MemberPrefabCount> counts = new List<MemberPrefabCount>(_byPrefab.Values);
        counts.Sort(static (a, b) => a.Count != b.Count
            ? b.Count.CompareTo(a.Count)
            : string.CompareOrdinal(a.Prefab, b.Prefab));
        return counts;
    }
}

/// <summary>How many members of one prefab, and how many of them show each colour (by colour name).</summary>
internal sealed class MemberPrefabCount
{
    internal MemberPrefabCount(string prefab, string? displayName, string member)
    {
        Prefab = prefab;
        DisplayName = displayName;
        Member = member;
    }

    internal string Prefab { get; }

    internal string? DisplayName { get; }

    internal string Member { get; }

    internal int Count { get; private set; }

    /// <summary>Colour name to count; null while no member of the prefab has shown a colour.</summary>
    internal SortedDictionary<string, int>? Colors { get; private set; }

    internal void Add(string? color)
    {
        Count++;
        if (color == null)
        {
            return;
        }

        Colors ??= new SortedDictionary<string, int>(StringComparer.Ordinal);
        Colors[color] = Colors.TryGetValue(color, out int seen) ? seen + 1 : 1;
    }
}
