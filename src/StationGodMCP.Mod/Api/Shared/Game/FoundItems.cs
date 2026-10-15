#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Found items: every item in the world a filter keeps, wherever it is (WorldItems: the world model's items, organs
/// left out), material machines hold as reagent stock (MachineStock, location machine_stock) and what SDB
/// Silos store (SiloStock, location silo), sortable together; grouped per outermost holder and prefab, or per prefab
/// (Pure/ItemGroups). Leaving out parts in use leaves out parts a device is using (PartsInUse) and what is inside them.
/// </summary>
internal static class FoundItems
{
    /// <summary>The rows grouped per holder and prefab, or per prefab, in the rows' order.</summary>
    internal static List<ItemGroup<FoundRow>> Groups(List<FoundRow> rows, ItemGrouping grouping) =>
        ItemGroups.Of(rows, grouping, static row => row.HolderId, static row => row.GroupName, static row => row.Quantity);

    /// <summary>A group's entry: per holder and prefab, or per prefab with its holder count.</summary>
    internal static IFoundItemView GroupView(ItemGroup<FoundRow> group, ItemGrouping grouping) =>
        grouping == ItemGrouping.Holder
            ? new HolderGroupView(group.First.Facts(), group.Entries, group.Quantity)
            : new PrefabGroupView(group.First.Facts(), group.Entries, group.Quantity, group.Holders);
}

/// <summary>
/// FoundItems.Rows spread over frames (a ScanBuild body): the items one candidate per step (WorldItems.Scan), the parts
/// in use left out, the machines' stock and the silos' stores, then each row keyed by its index in Rows, one per step.
/// Order sorts the keys by their rows' keys, so the rows come out as a one-shot sort of Rows gives them.
/// </summary>
internal sealed class FoundScan
{
    private readonly ItemFilter _filter;
    private readonly PlayerOrigin _origin;
    private readonly bool _excludeInUse;

    internal FoundScan(ItemFilter filter, PlayerOrigin origin, bool excludeInUse)
    {
        _filter = filter;
        _origin = origin;
        _excludeInUse = excludeInUse;
    }

    /// <summary>The rows the filter keeps, in the scan's order; a key's id is its row's index.</summary>
    internal List<FoundRow> Rows { get; } = new List<FoundRow>();

    /// <summary>The items left out as parts in use (0 when kept).</summary>
    internal int InUse { get; private set; }

    internal IEnumerable<object?> Scan(ScanKeys keys)
    {
        List<ItemRecord> records = new List<ItemRecord>();
        foreach (object? step in WorldItems.Scan(_filter, _origin, records.Add))
        {
            yield return step;
        }

        InUse = _excludeInUse ? WorldItems.LeaveOutPartsInUse(records) : 0;
        yield return null;
        List<StockRecord> stock = MachineStock.Collect(_filter, _origin);
        yield return null;
        List<SiloRecord> silos = SiloStock.Collect(_filter, _origin);
        yield return null;
        foreach (ItemRecord record in records)
        {
            Rows.Add(new FoundRow.ItemRow(record));
        }

        foreach (StockRecord record in stock)
        {
            Rows.Add(new FoundRow.StockRow(record));
        }

        foreach (SiloRecord record in silos)
        {
            Rows.Add(new FoundRow.SiloRow(record));
        }

        for (int index = 0; index < Rows.Count; index++)
        {
            keys.Candidates++;
            keys.Add(new ListKey(null, null, 0, index));
            yield return null;
        }
    }

    /// <summary>The list's order on the scan's keys: their rows' keys in order.</summary>
    internal Comparison<ListKey> Order(ListOrder order) =>
        (a, b) => ListKey.Compare(order, Rows[(int)a.Id].Key, Rows[(int)b.Id].Key);
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
