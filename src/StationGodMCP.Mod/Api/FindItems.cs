#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// find_items: every item in the world that matches, wherever it is (WorldItems: OcclusionManager.AllDynamicThings,
/// organs left out), and material machines hold as reagent stock (MachineStock, location machine_stock), nearest
/// first or by reference id (order). Read only.
/// </summary>
internal static class FindItemsApi
{
    private const int DefaultLimit = ReplyDefaults.FindItems;
    private const int MaximumLimit = 500;

    internal static FindItemsView Handle(Args args)
    {
        ItemFilter filter = ItemFilter.ParseWithArea(args);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(filter.NearPlayerM.HasValue);
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        ListOrder order = ListOrderArg.From(args);
        List<ItemRecord> records = WorldItems.Collect(filter, origin);
        List<StockRecord> stock = MachineStock.Collect(filter, origin);
        List<FoundRow> rows = new List<FoundRow>(records.Count + stock.Count);
        foreach (ItemRecord record in records)
        {
            rows.Add(new FoundRow(record));
        }

        foreach (StockRecord record in stock)
        {
            rows.Add(new FoundRow(record));
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

/// <summary>An item or a machine's stock, sortable together.</summary>
internal sealed class FoundRow
{
    private readonly ItemRecord? _item;
    private readonly StockRecord? _stock;

    internal FoundRow(ItemRecord item)
    {
        _item = item;
    }

    internal FoundRow(StockRecord stock)
    {
        _stock = stock;
    }

    private double? Distance => _item != null ? _item.Distance : _stock!.Distance;

    private string? Name => _item != null ? _item.Item.PrefabName : _stock!.IngotPrefab ?? _stock.Reagent;

    private bool IsStock => _item == null;

    // An item's own reference id; stock has none, so its machine's.
    private long Id => _item != null ? _item.Item.ReferenceId : _stock!.Machine.ReferenceId;

    /// <summary>Sorted by distance or id, with prefab or reagent name and items before stock telling rows apart.</summary>
    internal ListKey Key => new ListKey(Distance, Name, IsStock ? 1 : 0, Id);

    internal IFoundItemView ToView() => _item != null ? _item.ToView() : _stock!.ToView();
}
