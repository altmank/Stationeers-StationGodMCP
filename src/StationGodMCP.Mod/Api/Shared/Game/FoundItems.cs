#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Found items: every item in the world a filter keeps, wherever it is (WorldItems: the world model's items, organs
/// left out), material machines hold as reagent stock (MachineStock, location machine_stock) and what SDB
/// Silos store (SiloStock, location silo), sortable together; grouped per outermost holder and prefab, or per prefab
/// (Pure/ItemGroups). Leaving out parts in use leaves out parts a device is using (PartsInUse) and what is inside them.
/// </summary>
internal static class FoundItems
{
    /// <summary>The rows the filter keeps, unsorted; inUse the items left out as parts in use (0 when kept).</summary>
    internal static List<FoundRow> Rows(ItemFilter filter, PlayerOrigin origin, bool excludeInUse, out int inUse)
    {
        List<ItemRecord> records = WorldItems.Collect(filter, origin);
        inUse = excludeInUse ? WorldItems.LeaveOutPartsInUse(records) : 0;
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

        return rows;
    }

    /// <summary>The rows grouped per holder and prefab, or per prefab, in the rows' order.</summary>
    internal static List<ItemGroup<FoundRow>> Groups(List<FoundRow> rows, ItemGrouping grouping) =>
        ItemGroups.Of(rows, grouping, static row => row.HolderId, static row => row.GroupName, static row => row.Quantity);

    /// <summary>A group's entry: per holder and prefab, or per prefab with its holder count.</summary>
    internal static IFoundItemView GroupView(ItemGroup<FoundRow> group, ItemGrouping grouping) =>
        grouping == ItemGrouping.Holder
            ? new HolderGroupView(group.First.Facts(), group.Entries, group.Quantity)
            : new PrefabGroupView(group.First.Facts(), group.Entries, group.Quantity, group.Holders);
}

/// <summary>An item, a machine's stock or a silo's stored thing, sortable together.</summary>
internal abstract class FoundRow
{
    // A working load's reagent, apart from every prefab name (as ItemTotals keys it).
    private const string ReagentKeyPrefix = "reagent:";

    private FoundRow()
    {
    }

    /// <summary>Sorted by distance or id, with prefab or reagent name, then items, stock and silo entries apart.</summary>
    internal abstract ListKey Key { get; }

    internal abstract IFoundItemView ToView();

    /// <summary>
    /// The entry as a later page lists it: an item as it is now, a machine's stock or a silo's store as the scan read
    /// it while its holder stands; null when the item or its holder is gone.
    /// </summary>
    internal abstract IFoundItemView? Now(PlayerOrigin origin);

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

        internal override IFoundItemView? Now(PlayerOrigin origin) =>
            _record.Item != null && !_record.Item.IsBeingDestroyed
                ? WorldItems.Describe(_record.Item, origin).ToView()
                : null;

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

        internal override IFoundItemView? Now(PlayerOrigin origin) =>
            _record.Machine != null && !_record.Machine.IsBeingDestroyed ? _record.ToView() : null;

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

        internal override IFoundItemView? Now(PlayerOrigin origin) =>
            _record.Silo != null && !_record.Silo.IsBeingDestroyed ? _record.ToView() : null;

        internal override long? HolderId => _record.Silo.ReferenceId;

        internal override string GroupName => _record.PrefabName ?? string.Empty;

        internal override double Quantity => _record.Quantity;

        internal override FoundGroupFacts Facts() =>
            new FoundGroupFacts(_record.PrefabName, _record.DisplayName, null, SiloStock.Location,
                GameLookup.ViewOf(_record.Silo), GameLookup.ViewOf(_record.Silo.Position), Rounded(_record.Distance));
    }
}
