#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api;

/// <summary>
/// find_items: every item in the world that matches, wherever it is (WorldItems: OcclusionManager.AllDynamicThings,
/// organs left out), material machines hold as reagent stock (MachineStock, location machine_stock) and what SDB
/// Silos store (SiloStock, location silo), nearest first or by reference id (order). group_by sums them per outermost
/// holder and prefab, or per prefab (Pure/ItemGroups); exclude_in_use leaves out parts a device is using
/// (PartsInUse) and what is inside them. Read only.
/// </summary>
internal static class FindItemsApi
{
    private const int DefaultLimit = ReplyDefaults.FindItems;
    private const int MaximumLimit = 500;

    [Profiled]
    internal static FindItemsView Handle(Args args)
    {
        ItemFilter filter = ItemFilter.ParseWithArea(args);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(filter.NearPlayerM.HasValue);
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        ListOrder order = ListOrderArg.From(args);
        ItemGrouping grouping = Grouping(args);
        bool excludeInUse = args.OptionalBool("exclude_in_use") ?? false;
        List<ItemRecord> records = WorldItems.Collect(filter, origin);
        int inUse = excludeInUse ? WorldItems.LeaveOutPartsInUse(records) : 0;
        List<StockRecord> stock = MachineStock.Collect(filter, origin);
        List<SiloRecord> silos = SiloStock.Collect(filter, origin);
        List<FoundRow> rows = new List<FoundRow>(records.Count + stock.Count + silos.Count);
        foreach (ItemRecord record in records)
        {
            rows.Add(new FoundRow.ItemRow(record));
        }

        foreach (StockRecord record in stock)
        {
            rows.Add(new FoundRow.StockRow(record));
        }

        foreach (SiloRecord record in silos)
        {
            rows.Add(new FoundRow.SiloRow(record));
        }

        rows.Sort((a, b) => ListKey.Compare(order, a.Key, b.Key));
        List<IFoundItemView> entries;
        int total;
        if (grouping == ItemGrouping.None)
        {
            entries = Page(rows, page, static row => row.ToView());
            total = rows.Count;
        }
        else
        {
            List<ItemGroup<FoundRow>> groups = ItemGroups.Of(rows, grouping, static row => row.HolderId,
                static row => row.GroupName, static row => row.Quantity);
            entries = Page(groups, page, group => GroupView(group, grouping));
            total = groups.Count;
        }

        page.Note("items", entries.Count, total, ListOrders.PagingAdvice(order, origin.IsPresent));
        return new FindItemsView(Slice<IFoundItemView>.Page(entries, page, total), origin.View,
            excludeInUse ? inUse : null);
    }

    private static ItemGrouping Grouping(Args args)
    {
        string? given = args.OptionalString("group_by");
        return ItemGroupings.TryParse(given, out ItemGrouping grouping)
            ? grouping
            : throw ApiErrors.InvalidArgument(
                $"Argument 'group_by' must be {ItemGroupings.Holder} or {ItemGroupings.Prefab}; '{given}' is neither.");
    }

    // Only the page's entries become views.
    private static List<IFoundItemView> Page<T>(List<T> all, PageRequest page, System.Func<T, IFoundItemView> view)
    {
        Slice<T> slice = Slice<T>.Of(all, page);
        List<IFoundItemView> views = new List<IFoundItemView>(slice.Items.Count);
        foreach (T entry in slice.Items)
        {
            views.Add(view(entry));
        }

        return views;
    }

    private static IFoundItemView GroupView(ItemGroup<FoundRow> group, ItemGrouping grouping) =>
        grouping == ItemGrouping.Holder
            ? new HolderGroupView(group.First.Facts(), group.Entries, group.Quantity)
            : new PrefabGroupView(group.First.Facts(), group.Entries, group.Quantity, group.Holders);
}

/// <summary>An item, a machine's stock or a silo's stored thing, sortable together.</summary>
internal abstract class FoundRow
{
    // A working load's reagent, apart from every prefab name (as item_totals keys it).
    private const string ReagentKeyPrefix = "reagent:";

    private FoundRow()
    {
    }

    /// <summary>Sorted by distance or id, with prefab or reagent name, then items, stock and silo entries apart.</summary>
    internal abstract ListKey Key { get; }

    internal abstract IFoundItemView ToView();

    /// <summary>The outermost holder's id (a machine's, a silo's); null for a loose item.</summary>
    internal abstract long? HolderId { get; }

    /// <summary>What group_by sums under: the prefab, or a working load's reagent apart from every prefab name.</summary>
    internal abstract string GroupName { get; }

    internal abstract double Quantity { get; }

    /// <summary>What names a group this entry comes first in.</summary>
    internal abstract FoundGroupFacts Facts();

    private static double? Rounded(double? distance) =>
        distance.HasValue ? System.Math.Round(distance.Value, PositionView.Decimals) : null;

    internal sealed class ItemRow : FoundRow
    {
        private readonly ItemRecord _record;

        internal ItemRow(ItemRecord record)
        {
            _record = record;
        }

        internal override ListKey Key => new ListKey(_record.Distance, _record.Item.PrefabName, 0, _record.Item.ReferenceId);

        internal override IFoundItemView ToView() => _record.ToView();

        internal override long? HolderId => _record.Path.Count > 0 ? _record.Root.ReferenceId : null;

        internal override string GroupName => _record.Item.PrefabName ?? string.Empty;

        internal override double Quantity => _record.Quantity;

        internal override FoundGroupFacts Facts() =>
            new FoundGroupFacts(_record.Item.PrefabName, _record.Item.DisplayName, null, _record.Location,
                _record.Path.Count > 0 ? GameLookup.ViewOf(_record.Root) : null,
                GameLookup.ViewOf(_record.Root.Position), Rounded(_record.Distance));
    }

    // Stock has no id of its own, so its machine's.
    internal sealed class StockRow : FoundRow
    {
        private readonly StockRecord _record;

        internal StockRow(StockRecord record)
        {
            _record = record;
        }

        internal override ListKey Key =>
            new ListKey(_record.Distance, _record.IngotPrefab ?? _record.Reagent, 1, _record.Machine.ReferenceId);

        internal override IFoundItemView ToView() => _record.ToView();

        internal override long? HolderId => _record.Machine.ReferenceId;

        internal override string GroupName => _record.IngotPrefab ?? ReagentKeyPrefix + _record.Reagent;

        internal override double Quantity => _record.Quantity;

        internal override FoundGroupFacts Facts() =>
            new FoundGroupFacts(_record.IngotPrefab, _record.DisplayName,
                _record.IngotPrefab == null ? _record.Reagent : null, MachineStock.Location,
                GameLookup.ViewOf(_record.Machine), GameLookup.ViewOf(_record.Machine.Position),
                Rounded(_record.Distance));
    }

    // A stored thing has no id in the world, so its silo's.
    internal sealed class SiloRow : FoundRow
    {
        private readonly SiloRecord _record;

        internal SiloRow(SiloRecord record)
        {
            _record = record;
        }

        internal override ListKey Key => new ListKey(_record.Distance, _record.PrefabName, 2, _record.Silo.ReferenceId);

        internal override IFoundItemView ToView() => _record.ToView();

        internal override long? HolderId => _record.Silo.ReferenceId;

        internal override string GroupName => _record.PrefabName ?? string.Empty;

        internal override double Quantity => _record.Quantity;

        internal override FoundGroupFacts Facts() =>
            new FoundGroupFacts(_record.PrefabName, _record.DisplayName, null, SiloStock.Location,
                GameLookup.ViewOf(_record.Silo), GameLookup.ViewOf(_record.Silo.Position), Rounded(_record.Distance));
    }
}
