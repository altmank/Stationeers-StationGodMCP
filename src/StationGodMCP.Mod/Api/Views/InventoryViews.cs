#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>find_items: one page of the items that match, nearest first.</summary>
internal sealed class FindItemsView
{
    internal FindItemsView(Slice<IFoundItemView> page, LocalPlayerView? localPlayer)
    {
        Items = page.Items;
        Count = page.Items.Count;
        Total = page.Total;
        Offset = page.Offset;
        Limit = page.Limit;
        HasMore = page.HasMore;
        LocalPlayer = localPlayer;
    }

    /// <summary>Items (ItemView) and machine stock (StockItemView, location machine_stock).</summary>
    public List<IFoundItemView> Items { get; }

    public int Count { get; }

    public int Total { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }

    public LocalPlayerView? LocalPlayer { get; }
}

/// <summary>item_totals: the matching items summed per prefab, largest first.</summary>
internal sealed class ItemTotalsView
{
    private const string NoteText =
        "quantity is the stack size for stackable items and 1 for anything else; machine_stock is material held as " +
        "reagents inside machines (fabricator stock counts as the ingots it ejects; a furnace's or centrifuge's " +
        "working load counts under its reagent, with prefab_name null), included in quantity";

    internal ItemTotalsView(List<PrefabTotalView> totals, int prefabCount, int itemCount, int machineStockEntries)
    {
        Totals = totals;
        PrefabCount = prefabCount;
        ItemCount = itemCount;
        MachineStockEntries = machineStockEntries;
    }

    public List<PrefabTotalView> Totals { get; }

    public int PrefabCount { get; }

    public int ItemCount { get; }

    /// <summary>Machine and reagent pairs counted (not in item_count).</summary>
    public int MachineStockEntries { get; }

    public string Note => NoteText;
}

internal sealed class PrefabTotalView
{
    internal PrefabTotalView(string? prefabName, string? displayName, string? reagent, int items,
        PlaceAmounts amounts, List<HolderTotalView> topHolders)
    {
        PrefabName = prefabName;
        DisplayName = displayName;
        Reagent = reagent;
        Items = items;
        Quantity = amounts.Quantity;
        OnGround = amounts.OnGround;
        Carried = amounts.Carried;
        Stored = amounts.Stored;
        MachineStock = amounts.MachineStock;
        TopHolders = topHolders;
    }

    /// <summary>Null for a reagent that only exists as a machine's working load (see reagent).</summary>
    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public int Items { get; }

    public double Quantity { get; }

    public double OnGround { get; }

    public double Carried { get; }

    public double Stored { get; }

    /// <summary>Held as reagents inside machines (MachineStock).</summary>
    public double MachineStock { get; }

    /// <summary>The reagent's type name for a working-load row (prefab_name null), else null.</summary>
    public string? Reagent { get; }

    /// <summary>The five outermost holders with the most of it, machines holding it as stock included.</summary>
    public List<HolderTotalView> TopHolders { get; }
}

/// <summary>A quantity in all, and split by where it is.</summary>
internal sealed class PlaceAmounts
{
    internal PlaceAmounts(double quantity, double onGround, double carried, double stored, double machineStock)
    {
        Quantity = quantity;
        OnGround = onGround;
        Carried = carried;
        Stored = stored;
        MachineStock = machineStock;
    }

    internal double Quantity { get; }

    internal double OnGround { get; }

    internal double Carried { get; }

    internal double Stored { get; }

    internal double MachineStock { get; }
}

internal sealed class HolderTotalView
{
    internal HolderTotalView(ThingView holder, double quantity, string kind, PositionView position)
    {
        ReferenceId = holder.ReferenceId;
        PrefabName = holder.PrefabName;
        DisplayName = holder.DisplayName;
        Quantity = quantity;
        Kind = kind;
        Position = position;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public double Quantity { get; }

    /// <summary>player (carried), stored (a container) or machine_stock (reagents inside a machine).</summary>
    public string Kind { get; }

    public PositionView Position { get; }
}

/// <summary>list_containers: one page of the holders with at least one item in them, nearest first.</summary>
internal sealed class ListContainersView
{
    private const string NoteText =
        "Lists every holder with at least one item in it, nested items included: lockers, crates, machines, a " +
        "tablet on the floor. Empty containers are not listed.";

    internal ListContainersView(Slice<ContainerView> page, LocalPlayerView? localPlayer)
    {
        Containers = page.Items;
        Count = page.Items.Count;
        Total = page.Total;
        Offset = page.Offset;
        Limit = page.Limit;
        HasMore = page.HasMore;
        LocalPlayer = localPlayer;
    }

    public List<ContainerView> Containers { get; }

    public int Count { get; }

    public int Total { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }

    public LocalPlayerView? LocalPlayer { get; }

    public string Note => NoteText;
}

internal sealed class ContainerView
{
    internal ContainerView(ThingView holder, SlotUse slots, List<PrefabQuantityView> items, PositionView position,
        double? distanceM)
    {
        ReferenceId = holder.ReferenceId;
        PrefabName = holder.PrefabName;
        DisplayName = holder.DisplayName;
        SlotsTotal = slots.Total;
        SlotsUsed = slots.Used;
        ItemCount = slots.ItemCount;
        Items = items;
        Position = position;
        DistanceM = distanceM;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public int SlotsTotal { get; }

    public int SlotsUsed { get; }

    /// <summary>Items anywhere inside, nested ones included.</summary>
    public int ItemCount { get; }

    public List<PrefabQuantityView> Items { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }
}

/// <summary>A holder's slots in all and in use, and the items inside it.</summary>
internal sealed class SlotUse
{
    internal SlotUse(int total, int used, int itemCount)
    {
        Total = total;
        Used = used;
        ItemCount = itemCount;
    }

    internal int Total { get; }

    internal int Used { get; }

    internal int ItemCount { get; }
}

internal sealed class PrefabQuantityView
{
    internal PrefabQuantityView(string prefabName, double quantity)
    {
        PrefabName = prefabName;
        Quantity = quantity;
    }

    public string PrefabName { get; }

    public double Quantity { get; }
}

/// <summary>container_contents: a thing's slots, and what is in each, a few levels deep.</summary>
internal sealed class ContainerContentsView
{
    internal ContainerContentsView(ThingView thing, PositionView position, double? distanceM, List<SlotView> slots)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Position = position;
        DistanceM = distanceM;
        Slots = slots;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    public List<SlotView> Slots { get; }
}

internal sealed class SlotView
{
    internal SlotView(int index, string? name, string slotClass, OccupantView? occupant)
    {
        Index = index;
        Name = name;
        SlotClass = slotClass;
        Occupant = occupant;
    }

    public int Index { get; }

    public string? Name { get; }

    public string SlotClass { get; }

    public bool Empty => Occupant == null;

    public OccupantView? Occupant { get; }
}

internal sealed class OccupantView
{
    internal OccupantView(ThingView thing, double quantity, double? maxQuantity, List<SlotView>? slots,
        int? slotsNotShown)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Quantity = quantity;
        MaxQuantity = maxQuantity;
        Slots = slots;
        SlotsNotShown = slotsNotShown;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public double Quantity { get; }

    public double? MaxQuantity { get; }

    /// <summary>Its own slots, while depth lasts; null past it or when it has none.</summary>
    public List<SlotView>? Slots { get; }

    /// <summary>How many slots it has past the depth limit; null otherwise.</summary>
    public int? SlotsNotShown { get; }
}
