#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// A list filter on a thing's prefab name: prefab, the whole name as place_structure and describe_prefab take it
/// (StructureFrame, not StructureFrameCorner), and prefab_contains, a part of it. Both ignore case; given together, a
/// name must pass both. Neither: every name.
/// </summary>
internal sealed class PrefabMatch
{
    internal static readonly PrefabMatch Any = new PrefabMatch(null, null);

    internal PrefabMatch(string? exact, string? contains)
    {
        Exact = string.IsNullOrEmpty(exact) ? null : exact;
        Contains = string.IsNullOrEmpty(contains) ? null : contains;
    }

    internal string? Exact { get; }

    internal string? Contains { get; }

    internal bool Keeps(string? prefabName) =>
        (Exact == null || string.Equals(prefabName, Exact, StringComparison.OrdinalIgnoreCase)) &&
        (Contains == null ||
         (!string.IsNullOrEmpty(prefabName) && prefabName!.IndexOf(Contains, StringComparison.OrdinalIgnoreCase) >= 0));
}
