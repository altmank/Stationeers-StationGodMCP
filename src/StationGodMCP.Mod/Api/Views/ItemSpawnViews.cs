#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// build item's reply: placed (the ids of the items made, in the order made; empty in a report), the prefab, the
/// quantity, the holder (to) or the point (at) they went to, and each stack: where it went (a new stack in a slot, onto
/// a matching stack, or loose), its slot, its quantity and its id (null in a report for a stack not made yet).
/// </summary>
internal sealed class ItemSpawnView
{
    internal ItemSpawnView(List<ThingId> placed, string? prefabName, string? displayName, double? quantity,
        ThingView? to, PositionView? at, List<SpawnedView> items)
    {
        Placed = placed;
        PrefabName = prefabName;
        DisplayName = displayName;
        Quantity = quantity is { } amount ? VaultAmount.Of(amount) : (double?)null;
        To = to;
        At = at;
        Items = items;
    }

    public List<ThingId> Placed { get; }

    /// <summary>The one item's prefab; null for an items list, whose rows each name their own.</summary>
    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The one item's quantity; null for an items list.</summary>
    public double? Quantity { get; }

    public ThingView? To { get; }

    public PositionView? At { get; }

    public List<SpawnedView> Items { get; }

    /// <summary>The ids of the items each stack made or topped up, once each, in order.</summary>
    internal static List<ThingId> PlacedOf(IEnumerable<PlacedView> items)
    {
        List<ThingId> placed = new List<ThingId>();
        foreach (PlacedView item in items)
        {
            if (item.Where != PlacedView.Merged && item.ReferenceId is { } id && !placed.Contains(id))
            {
                placed.Add(id);
            }
        }

        return placed;
    }
}

/// <summary>One stack build item made or topped up: the items line it came from, then where it went.</summary>
internal sealed class SpawnedView : PlacedView
{
    internal SpawnedView(int line, string prefabName, PlacedView placed)
        : base(placed.Where, placed.Slot, placed.Quantity, placed.ReferenceId)
    {
        Line = line;
        PrefabName = prefabName;
    }

    /// <summary>The place of the line in items (0 for a call without items).</summary>
    [Newtonsoft.Json.JsonProperty(Order = -1)]
    public int Line { get; }

    /// <summary>The line's prefab, for the report; not in the reply.</summary>
    internal string PrefabName { get; }
}
