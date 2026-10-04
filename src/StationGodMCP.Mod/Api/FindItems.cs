#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// find_items: every item in the world that matches, wherever it is (WorldItems: OcclusionManager.AllDynamicThings,
/// organs left out), and material machines hold as reagent stock (MachineStock, location machine_stock), nearest
/// first. Read only.
/// </summary>
internal static class FindItemsApi
{
    private const int DefaultLimit = ReplyDefaults.FindItems;
    private const int MaximumLimit = 500;

    internal static FindItemsView Handle(Args args)
    {
        ItemFilter filter = ItemFilter.Parse(args);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(filter.NearPlayerM.HasValue);
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
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

        rows.Sort(static (a, b) => FoundRow.NearestFirst(a, b));
        Slice<FoundRow> slice = Slice<FoundRow>.Of(rows, page);
        List<IFoundItemView> items = new List<IFoundItemView>(slice.Items.Count);
        foreach (FoundRow row in slice.Items)
        {
            items.Add(row.ToView());
        }

        page.Note("items", items.Count, rows.Count);
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

    /// <summary>Nearest first (no player: all last), then prefab or reagent name, items before stock, id.</summary>
    internal static int NearestFirst(FoundRow a, FoundRow b)
    {
        int byDistance = (a.Distance ?? double.MaxValue).CompareTo(b.Distance ?? double.MaxValue);
        if (byDistance != 0)
        {
            return byDistance;
        }

        int byName = string.CompareOrdinal(a.Name, b.Name);
        if (byName != 0)
        {
            return byName;
        }

        int byKind = a.IsStock.CompareTo(b.IsStock);
        return byKind != 0 ? byKind : a.Id.CompareTo(b.Id);
    }

    internal IFoundItemView ToView() => _item != null ? _item.ToView() : _stock!.ToView();
}
