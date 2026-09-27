#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// item_totals: the matching items summed per prefab, with where they are and the holders with the most. Quantity is
/// the stack size for IQuantity items and 1 for anything else (WorldItems.QuantityOf). Machine stock (MachineStock)
/// adds to the same rows: fabricator stock under the ingot it ejects as, a working load under its reagent in a row of
/// its own (prefab_name null, reagent set). Read only.
/// </summary>
internal static class ItemTotalsApi
{
    private const int DefaultLimit = 200;
    private const int MaximumLimit = 500;
    private const int TopHolders = 5;

    // Row key of a working-load reagent, apart from every prefab name.
    private const string ReagentKeyPrefix = "reagent:";

    internal static ItemTotalsView Handle(Args args)
    {
        ItemFilter filter = ItemFilter.Parse(args);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(filter.NearPlayerM.HasValue);
        int limit = args.OptionalInt("limit", 1, MaximumLimit) ?? DefaultLimit;
        List<ItemRecord> records = WorldItems.Collect(filter, origin);
        List<StockRecord> stock = MachineStock.Collect(filter, origin);
        Dictionary<string, PrefabTally> byKey = new Dictionary<string, PrefabTally>(StringComparer.Ordinal);
        List<PrefabTally> tallies = new List<PrefabTally>();
        foreach (ItemRecord record in records)
        {
            string prefab = record.Item.PrefabName ?? string.Empty;
            TallyFor(byKey, tallies, prefab, prefab, record.Item.DisplayName, null).Add(record);
        }

        foreach (StockRecord record in stock)
        {
            PrefabTally tally = record.IngotPrefab != null
                ? TallyFor(byKey, tallies, record.IngotPrefab, record.IngotPrefab, record.IngotName, null)
                : TallyFor(byKey, tallies, ReagentKeyPrefix + record.Reagent, null, record.DisplayName, record.Reagent);
            tally.Add(record);
        }

        tallies.Sort(static (a, b) => PrefabTally.LargestFirst(a, b));
        List<PrefabTotalView> totals = new List<PrefabTotalView>(Math.Min(limit, tallies.Count));
        for (int index = 0; index < tallies.Count && index < limit; index++)
        {
            totals.Add(tallies[index].ToView(TopHolders));
        }

        return new ItemTotalsView(totals, tallies.Count, records.Count, stock.Count);
    }

    private static PrefabTally TallyFor(Dictionary<string, PrefabTally> byKey, List<PrefabTally> tallies, string key,
        string? prefab, string? display, string? reagent)
    {
        if (!byKey.TryGetValue(key, out PrefabTally tally))
        {
            tally = new PrefabTally(key, prefab, display, reagent);
            byKey[key] = tally;
            tallies.Add(tally);
        }

        return tally;
    }
}

/// <summary>One prefab's items (or one working-load reagent) summed, overall, by place and per holder.</summary>
internal sealed class PrefabTally
{
    private readonly string _key;
    private readonly string? _prefab;
    private readonly string? _display;
    private readonly string? _reagent;
    private readonly List<HolderTally> _holders = new List<HolderTally>();
    private readonly Dictionary<long, HolderTally> _holdersById = new Dictionary<long, HolderTally>();
    private readonly Dictionary<long, HolderTally> _machinesById = new Dictionary<long, HolderTally>();
    private int _items;
    private double _onGround;
    private double _carried;
    private double _stored;
    private double _machineStock;

    internal PrefabTally(string key, string? prefab, string? display, string? reagent)
    {
        _key = key;
        _prefab = prefab;
        _display = display;
        _reagent = reagent;
    }

    internal double Quantity { get; private set; }

    internal void Add(ItemRecord record)
    {
        _items++;
        Quantity += record.Quantity;
        switch (record.Location)
        {
            case "ground":
                _onGround += record.Quantity;
                return;
            case "player":
                _carried += record.Quantity;
                break;
            default:
                _stored += record.Quantity;
                break;
        }

        HolderIn(_holdersById, record.Root, record.Location).Quantity += record.Quantity;
    }

    internal void Add(StockRecord record)
    {
        Quantity += record.Quantity;
        _machineStock += record.Quantity;
        HolderIn(_machinesById, record.Machine, MachineStock.Location).Quantity += record.Quantity;
    }

    // A machine can hold the same prefab both in its slots (stored) and as stock, so the two are tallied apart.
    private HolderTally HolderIn(Dictionary<long, HolderTally> byId, Thing root, string kind)
    {
        if (!byId.TryGetValue(root.ReferenceId, out HolderTally holder))
        {
            holder = new HolderTally(root, kind, _holders.Count);
            byId[root.ReferenceId] = holder;
            _holders.Add(holder);
        }

        return holder;
    }

    /// <summary>Most first, then by prefab name (a reagent row by its key).</summary>
    internal static int LargestFirst(PrefabTally a, PrefabTally b)
    {
        int byQuantity = b.Quantity.CompareTo(a.Quantity);
        return byQuantity != 0 ? byQuantity : string.CompareOrdinal(a._key, b._key);
    }

    internal PrefabTotalView ToView(int topHolders)
    {
        _holders.Sort(static (a, b) => HolderTally.LargestFirst(a, b));
        List<HolderTotalView> top = new List<HolderTotalView>(Math.Min(topHolders, _holders.Count));
        for (int index = 0; index < _holders.Count && index < topHolders; index++)
        {
            HolderTally holder = _holders[index];
            top.Add(new HolderTotalView(GameLookup.ViewOf(holder.Root), holder.Quantity, holder.Kind,
                GameLookup.ViewOf(holder.Root.Position)));
        }

        return new PrefabTotalView(_prefab, _display, _reagent, _items,
            new PlaceAmounts(Quantity, _onGround, _carried, _stored, _machineStock), top);
    }
}

/// <summary>One outermost holder's share of a prefab, and when it was first seen (the tie-break).</summary>
internal sealed class HolderTally
{
    internal HolderTally(Thing root, string kind, int seen)
    {
        Root = root;
        Kind = kind;
        Seen = seen;
    }

    internal Thing Root { get; }

    /// <summary>player, stored or machine_stock.</summary>
    internal string Kind { get; }

    internal int Seen { get; }

    internal double Quantity { get; set; }

    internal static int LargestFirst(HolderTally a, HolderTally b)
    {
        int byQuantity = b.Quantity.CompareTo(a.Quantity);
        return byQuantity != 0 ? byQuantity : a.Seen.CompareTo(b.Seen);
    }
}
