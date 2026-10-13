#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Containers: every outermost holder with at least one item stored in it, nested items included (lockers, crates,
/// machines, a tablet on the floor). Found from the stored items (WorldItems), so an empty container is not one.
/// </summary>
internal static class Containers
{
    /// <summary>Every container whose prefab and shown name the filters keep, within metres of the player when given.</summary>
    internal static List<ContainerTally> Collect(PrefabFilter? prefab, string? name, double? aroundPlayerM,
        PlayerOrigin origin)
    {
        List<ContainerTally> containers = new List<ContainerTally>();
        foreach (ContainerTally container in ContainerTally.Group(WorldItems.Collect(ItemFilter.StoredOnly(), origin)))
        {
            Thing root = container.Root;
            if ((prefab == null || prefab.Keeps(root.PrefabName, root.PrefabHash)) &&
                ItemFilter.Contains(root.DisplayName, name) &&
                (!aroundPlayerM.HasValue || (container.Distance.HasValue && container.Distance.Value <= aroundPlayerM.Value)))
            {
                containers.Add(container);
            }
        }

        return containers;
    }

    /// <summary>
    /// One container as it holds now, its items found from its own slots: null when it is gone, holds nothing, or is
    /// itself held (no longer outermost).
    /// </summary>
    internal static ContainerTally? Live(long id, PlayerOrigin origin)
    {
        Thing? root = Thing.Find(id);
        if (root == null || root.IsBeingDestroyed || (root is DynamicThing { ParentSlot: not null }))
        {
            return null;
        }

        List<ItemRecord> records = new List<ItemRecord>();
        AddStored(root, origin, records, 0);
        return records.Count > 0 ? ContainerTally.Group(records)[0] : null;
    }

    // The items in a holder's slots and, inside them, theirs: as deep as a holder chain goes.
    private static void AddStored(Thing holder, PlayerOrigin origin, List<ItemRecord> records, int depth)
    {
        if (holder.Slots == null || depth >= HolderChain.MaximumDepth)
        {
            return;
        }

        foreach (Slot slot in holder.Slots)
        {
            if (slot?.Get() is Item item && item != null && !(item is Organ) && !item.IsBeingDestroyed)
            {
                records.Add(WorldItems.Describe(item, origin));
                AddStored(item, origin, records, depth + 1);
            }
        }
    }
}

/// <summary>The stored items of one outermost holder.</summary>
internal sealed class ContainerTally
{
    private readonly List<ItemRecord> _records = new List<ItemRecord>();

    private ContainerTally(ItemRecord first)
    {
        Root = first.Root;
        Distance = first.Distance;
    }

    internal Thing Root { get; }

    /// <summary>From the player, unrounded; null without a player.</summary>
    internal double? Distance { get; }

    internal static List<ContainerTally> Group(List<ItemRecord> records)
    {
        List<ContainerTally> containers = new List<ContainerTally>();
        Dictionary<long, ContainerTally> byRoot = new Dictionary<long, ContainerTally>();
        foreach (ItemRecord record in records)
        {
            if (!byRoot.TryGetValue(record.Root.ReferenceId, out ContainerTally container))
            {
                container = new ContainerTally(record);
                byRoot[record.Root.ReferenceId] = container;
                containers.Add(container);
            }

            container._records.Add(record);
        }

        return containers;
    }

    /// <summary>Sorted by distance, then prefab name and the holder's reference id; or by reference id.</summary>
    internal ListKey Key => new ListKey(Distance, Root.PrefabName, 0, Root.ReferenceId);

    internal ContainerView ToView()
    {
        List<Slot>? slots = Root.Slots;
        int used = 0;
        if (slots != null)
        {
            foreach (Slot slot in slots)
            {
                used += slot != null && slot.Get() != null ? 1 : 0;
            }
        }

        SlotUse use = new SlotUse(slots != null ? slots.Count : 0, used, _records.Count);
        double? distance = Distance.HasValue ? Math.Round(Distance.Value, PositionView.Decimals) : null;
        return new ContainerView(GameLookup.ViewOf(Root), use, Items(), GameLookup.ViewOf(Root.Position), distance);
    }

    // The items inside summed per prefab, most first, then as first seen.
    private List<PrefabQuantityView> Items()
    {
        List<PrefabSum> sums = new List<PrefabSum>();
        Dictionary<string, PrefabSum> byPrefab = new Dictionary<string, PrefabSum>(StringComparer.Ordinal);
        foreach (ItemRecord record in _records)
        {
            string prefab = record.Item.PrefabName ?? string.Empty;
            if (!byPrefab.TryGetValue(prefab, out PrefabSum sum))
            {
                sum = new PrefabSum(prefab, sums.Count);
                byPrefab[prefab] = sum;
                sums.Add(sum);
            }

            sum.Quantity += record.Quantity;
        }

        sums.Sort(static (a, b) => PrefabSum.LargestFirst(a, b));
        List<PrefabQuantityView> items = new List<PrefabQuantityView>(sums.Count);
        foreach (PrefabSum sum in sums)
        {
            items.Add(new PrefabQuantityView(sum.Prefab, sum.Quantity));
        }

        return items;
    }

    private sealed class PrefabSum
    {
        internal PrefabSum(string prefab, int seen)
        {
            Prefab = prefab;
            Seen = seen;
        }

        internal string Prefab { get; }

        internal int Seen { get; }

        internal double Quantity { get; set; }

        internal static int LargestFirst(PrefabSum a, PrefabSum b)
        {
            int byQuantity = b.Quantity.CompareTo(a.Quantity);
            return byQuantity != 0 ? byQuantity : a.Seen.CompareTo(b.Seen);
        }
    }
}
