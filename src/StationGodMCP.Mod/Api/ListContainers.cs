#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// list_containers: every outermost holder with at least one item stored in it, nested items included (lockers,
/// crates, machines, a tablet on the floor), nearest first. Found from the stored items (WorldItems), so an empty
/// container is not listed. Nearest first or by reference id (order). Read only.
/// </summary>
internal static class ListContainersApi
{
    private const int DefaultLimit = ReplyDefaults.Containers;
    private const int MaximumLimit = 500;

    internal static ListContainersView Handle(Args args)
    {
        string? prefabContains = args.OptionalString("prefab_contains");
        string? nameContains = args.OptionalString("name_contains");
        double? near = args.OptionalPositiveDouble("near_player_m");
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        ListOrder order = ListOrderArg.From(args);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(near.HasValue);

        List<ContainerTally> containers = new List<ContainerTally>();
        foreach (ContainerTally container in ContainerTally.Group(WorldItems.Collect(ItemFilter.StoredOnly(), origin)))
        {
            Thing root = container.Root;
            if (ItemFilter.Contains(root.PrefabName, prefabContains) &&
                ItemFilter.Contains(root.DisplayName, nameContains) &&
                (!near.HasValue || (container.Distance.HasValue && container.Distance.Value <= near.Value)))
            {
                containers.Add(container);
            }
        }

        containers.Sort((a, b) => ListKey.Compare(order, a.Key, b.Key));
        Slice<ContainerTally> slice = Slice<ContainerTally>.Of(containers, page);
        List<ContainerView> views = new List<ContainerView>(slice.Items.Count);
        foreach (ContainerTally container in slice.Items)
        {
            views.Add(container.ToView());
        }

        page.Note("containers", views.Count, containers.Count, ListOrders.PagingAdvice(order, origin.IsPresent));
        return new ListContainersView(Slice<ContainerView>.Page(views, page, containers.Count), origin.View);
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
