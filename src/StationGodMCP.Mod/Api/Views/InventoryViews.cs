#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Views;

/// <summary>find_items: one page of the items that match, in the order asked.</summary>
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

    /// <summary>Items (ItemView), machine stock (StockItemView, location machine_stock), silo stock (SiloItemView).</summary>
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
    internal ItemTotalsView(List<PrefabTotalView> totals, int prefabCount, int itemCount, int machineStockEntries,
        int siloEntries)
    {
        Totals = totals;
        PrefabCount = prefabCount;
        ItemCount = itemCount;
        MachineStockEntries = machineStockEntries;
        SiloEntries = siloEntries;
    }

    public List<PrefabTotalView> Totals { get; }

    public int PrefabCount { get; }

    public int ItemCount { get; }

    /// <summary>Machine and reagent pairs counted (not in item_count).</summary>
    public int MachineStockEntries { get; }

    /// <summary>Things stored in silos counted (entries and what they hold; not in item_count).</summary>
    public int SiloEntries { get; }
}

internal sealed class PrefabTotalView
{
    internal PrefabTotalView(string? prefabName, string? displayName, string? reagent, int items,
        PlaceAmounts amounts, List<HolderTotalView>? topHolders)
    {
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Reagent = reagent;
        Items = items;
        Quantity = amounts.Quantity;
        OnGround = amounts.OnGround;
        Carried = amounts.Carried;
        Stored = amounts.Stored;
        MachineStock = amounts.MachineStock;
        Silo = amounts.Silo;
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

    /// <summary>Stored in SDB Silos (SiloStock): entries and what they hold.</summary>
    public double Silo { get; }

    /// <summary>The reagent's type name for a working-load row (prefab_name null), else null.</summary>
    public string? Reagent { get; }

    /// <summary>
    /// The holders_limit (default five) outermost holders with the most of it, machines holding it as stock included;
    /// left out with holders_limit 0.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<HolderTotalView>? TopHolders { get; }
}

/// <summary>A quantity in all, and split by where it is.</summary>
internal sealed class PlaceAmounts
{
    internal PlaceAmounts(double quantity, double onGround, double carried, double stored, double machineStock,
        double silo)
    {
        Quantity = quantity;
        OnGround = onGround;
        Carried = carried;
        Stored = stored;
        MachineStock = machineStock;
        Silo = silo;
    }

    internal double Quantity { get; }

    internal double OnGround { get; }

    internal double Carried { get; }

    internal double Stored { get; }

    internal double MachineStock { get; }

    internal double Silo { get; }
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

    /// <summary>player (carried), stored (a container), machine_stock (reagents inside a machine) or silo.</summary>
    public string Kind { get; }

    public PositionView Position { get; }
}

/// <summary>list_containers: one page of the holders with at least one item in them, in the order asked.</summary>
internal sealed class ListContainersView
{
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
internal sealed class ContainerContentsView : ITruncatingView
{
    internal ContainerContentsView(ThingView thing, PositionView position, double? distanceM, List<SlotView> slots,
        SiloContentsView? silo = null)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Position = position;
        DistanceM = distanceM;
        Slots = slots;
        Silo = silo;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    public List<SlotView> Slots { get; }

    /// <summary>An SDB Silo's store; left out for anything else.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public SiloContentsView? Silo { get; }

    public void NoteTruncations(string path)
    {
        int shown = 0;
        int hidden = 0;
        Count(Slots, ref shown, ref hidden);
        if (hidden > 0)
        {
            Truncations.Note(path + "slots (nested past depth)", shown, shown + hidden,
                "pass depth (max 6), or container_contents of the occupant", atLeast: true);
        }
    }

    // Slots shown at every depth, and the slots of occupants past the depth limit (their own nested slots unknown).
    private static void Count(List<SlotView>? slots, ref int shown, ref int hidden)
    {
        if (slots == null)
        {
            return;
        }

        foreach (SlotView slot in slots)
        {
            shown++;
            if (slot.Occupant != null)
            {
                hidden += slot.Occupant.SlotsNotShown ?? 0;
                Count(slot.Occupant.Slots, ref shown, ref hidden);
            }
        }
    }
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

    /// <summary>The same occupant with only these of its slots.</summary>
    internal OccupantView WithSlots(List<SlotView> slots) =>
        new OccupantView(new ThingView(ReferenceId, PrefabName, DisplayName), Quantity, MaxQuantity, slots,
            SlotsNotShown);
}

/// <summary>
/// container_contents' prefab_contains and name_contains: the slots whose occupant matches (any case, part of the
/// prefab name or the shown name), or holds a match at any depth, with only those inner slots. A matching occupant
/// keeps all it holds; empty slots and slots holding no match are left out.
/// </summary>
internal sealed class SlotFilter
{
    private readonly string? _prefab;
    private readonly string? _name;

    internal SlotFilter(string? prefabContains, string? nameContains)
    {
        _prefab = string.IsNullOrWhiteSpace(prefabContains) ? null : prefabContains!.Trim();
        _name = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains!.Trim();
    }

    internal bool IsActive => _prefab != null || _name != null;

    internal List<SlotView> Apply(List<SlotView> slots)
    {
        if (!IsActive)
        {
            return slots;
        }

        List<SlotView> kept = new List<SlotView>();
        foreach (SlotView slot in slots)
        {
            OccupantView? occupant = slot.Occupant;
            if (occupant == null)
            {
                continue;
            }

            if (Matches(occupant))
            {
                kept.Add(slot);
                continue;
            }

            List<SlotView> inner = occupant.Slots != null ? Apply(occupant.Slots) : new List<SlotView>();
            if (inner.Count > 0)
            {
                kept.Add(new SlotView(slot.Index, slot.Name, slot.SlotClass, occupant.WithSlots(inner)));
            }
        }

        return kept;
    }

    /// <summary>A silo entry matches by its own prefab or shown name, or by a prefab stored inside it.</summary>
    internal bool Keeps(SiloEntryView entry)
    {
        if (!IsActive || ((_prefab == null || Contains(entry.PrefabName, _prefab)) &&
                          (_name == null || Contains(entry.DisplayName, _name))))
        {
            return true;
        }

        if (_prefab == null)
        {
            return false;
        }

        foreach (SiloContentView content in entry.Contents)
        {
            if (Contains(content.PrefabName, _prefab))
            {
                return true;
            }
        }

        return false;
    }

    private bool Matches(OccupantView occupant) =>
        (_prefab == null || Contains(occupant.PrefabName, _prefab)) &&
        (_name == null || Contains(occupant.DisplayName, _name));

    private static bool Contains(string? text, string part) =>
        text != null && text.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;
}
