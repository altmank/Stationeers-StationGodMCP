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
    internal ItemSpawnView(List<ThingId> placed, string prefabName, string? displayName, double quantity, ThingView? to,
        PositionView? at, List<PlacedView> items)
    {
        Placed = placed;
        PrefabName = prefabName;
        DisplayName = displayName;
        Quantity = VaultAmount.Of(quantity);
        To = to;
        At = at;
        Items = items;
    }

    public List<ThingId> Placed { get; }

    public string PrefabName { get; }

    public string? DisplayName { get; }

    public double Quantity { get; }

    public ThingView? To { get; }

    public PositionView? At { get; }

    public List<PlacedView> Items { get; }

    /// <summary>The ids of the items each stack made or topped up, once each, in order.</summary>
    internal static List<ThingId> PlacedOf(List<PlacedView> items)
    {
        List<ThingId> placed = new List<ThingId>(items.Count);
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
