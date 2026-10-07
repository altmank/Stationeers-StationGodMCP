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
/// Silos store (SiloStock, location silo), nearest first or by reference id (order). Read only.
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
        List<ItemRecord> records = WorldItems.Collect(filter, origin);
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
        Slice<FoundRow> slice = Slice<FoundRow>.Of(rows, page);
        List<IFoundItemView> items = new List<IFoundItemView>(slice.Items.Count);
        foreach (FoundRow row in slice.Items)
        {
            items.Add(row.ToView());
        }

        page.Note("items", items.Count, rows.Count, ListOrders.PagingAdvice(order, origin.IsPresent));
        return new FindItemsView(Slice<IFoundItemView>.Page(items, page, rows.Count), origin.View);
    }
}

/// <summary>An item, a machine's stock or a silo's stored thing, sortable together.</summary>
internal abstract class FoundRow
{
    private FoundRow()
    {
    }

    /// <summary>Sorted by distance or id, with prefab or reagent name, then items, stock and silo entries apart.</summary>
    internal abstract ListKey Key { get; }

    internal abstract IFoundItemView ToView();

    internal sealed class ItemRow : FoundRow
    {
        private readonly ItemRecord _record;

        internal ItemRow(ItemRecord record)
        {
            _record = record;
        }

        internal override ListKey Key => new ListKey(_record.Distance, _record.Item.PrefabName, 0, _record.Item.ReferenceId);

        internal override IFoundItemView ToView() => _record.ToView();
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
    }
}
