#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A list filter on a thing's prefab name: prefab or prefabs, whole names as place_structure and describe_prefab take
/// them (StructureFrame, not StructureFrameCorner), and prefab_contains, a part of one. All ignore case; exact names and
/// a part given together, a name must be one of the names and hold the part. None: every name.
/// </summary>
internal sealed class PrefabMatch
{
    internal static readonly PrefabMatch Any = new PrefabMatch((string?)null, null);

    private readonly HashSet<string>? _exact;

    /// <summary>One exact name (empty or null: none) and a part.</summary>
    internal PrefabMatch(string? exact, string? contains)
        : this(string.IsNullOrEmpty(exact) ? null : NamesOf(new[] { exact! }), contains)
    {
    }

    private PrefabMatch(HashSet<string>? exact, string? contains)
    {
        _exact = exact;
        Contains = string.IsNullOrEmpty(contains) ? null : contains;
    }

    /// <summary>Several exact names (a name repeated in another case counts once) and a part.</summary>
    internal static PrefabMatch OfNames(IReadOnlyCollection<string> names, string? contains) =>
        new PrefabMatch(NamesOf(names), contains);

    private static HashSet<string> NamesOf(IReadOnlyCollection<string> names)
    {
        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            set.Add(name);
        }

        return set;
    }

    /// <summary>The exact names, distinct ignoring case; null when any name will do.</summary>
    internal IReadOnlyCollection<string>? Exact => _exact;

    internal string? Contains { get; }

    /// <summary>Whether the filter names prefabs at all (else it keeps every name).</summary>
    internal bool IsActive => _exact != null || Contains != null;

    internal bool Keeps(string? prefabName) =>
        (_exact == null || (prefabName != null && _exact.Contains(prefabName))) &&
        (Contains == null ||
         (!string.IsNullOrEmpty(prefabName) && prefabName!.IndexOf(Contains, StringComparison.OrdinalIgnoreCase) >= 0));
}
