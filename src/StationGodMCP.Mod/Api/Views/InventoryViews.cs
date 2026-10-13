#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Views;

/// <summary>
/// What names a find_items group: its first entry's prefab (or working-load reagent), place and outermost holder (none
/// for a loose item), and the holder's position and distance from the player.
/// </summary>
internal sealed class FoundGroupFacts
{
    internal FoundGroupFacts(string? prefabName, string? displayName, string? reagent, string location,
        ThingView? holder, PositionView? position, double? distanceM)
    {
        PrefabName = prefabName;
        DisplayName = displayName;
        Reagent = reagent;
        Location = location;
        Holder = holder;
        Position = position;
        DistanceM = distanceM;
    }

    internal string? PrefabName { get; }

    internal string? DisplayName { get; }

    internal string? Reagent { get; }

    internal string Location { get; }

    internal ThingView? Holder { get; }

    internal PositionView? Position { get; }

    internal double? DistanceM { get; }
}

/// <summary>
/// find_items group_by holder: one prefab's entries (stacks, a machine's stock, silo entries) in one outermost holder,
/// summed. Loose items of a prefab make one group with no holder and no position.
/// </summary>
internal sealed class HolderGroupView : IFoundItemView
{
    internal HolderGroupView(FoundGroupFacts first, int entries, double quantity)
    {
        PrefabName = first.PrefabName;
        DisplayName = ThingName.Displayed(first.DisplayName, first.PrefabName);
        Reagent = first.Reagent;
        Quantity = quantity;
        Entries = entries;
        Location = first.Location;
        Holder = first.Holder;
        Position = first.Holder != null ? first.Position : null;
        DistanceM = first.Holder != null ? first.DistanceM : null;
    }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>A machine's working load with no ingot to name it: the reagent; else left out.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Reagent { get; }

    public double Quantity { get; }

    public int Entries { get; }

    /// <summary>ground, player, stored, machine_stock or silo.</summary>
    public string Location { get; }

    /// <summary>The outermost holder; null for loose items.</summary>
    public ThingView? Holder { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PositionView? Position { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? DistanceM { get; }
}

/// <summary>find_items group_by prefab: every entry of one prefab (or working-load reagent) summed.</summary>
internal sealed class PrefabGroupView : IFoundItemView
{
    internal PrefabGroupView(FoundGroupFacts first, int entries, double quantity, int holders)
    {
        PrefabName = first.PrefabName;
        DisplayName = ThingName.Displayed(first.DisplayName, first.PrefabName);
        Reagent = first.Reagent;
        Quantity = quantity;
        Entries = entries;
        Holders = holders;
    }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Reagent { get; }

    public double Quantity { get; }

    public int Entries { get; }

    /// <summary>Distinct outermost holders (players, containers, machines, silos); loose items count none.</summary>
    public int Holders { get; }
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

internal sealed class ContainerView
{
    internal ContainerView(ThingView holder, SlotUse slots, List<PrefabQuantityView> items, PositionView position,
        double? distanceM)
    {
        ReferenceId = holder.ReferenceId;
        PrefabName = holder.PrefabName;
        DisplayName = holder.DisplayName;
        SlotCount = slots.Total;
        UsedSlotCount = slots.Used;
        ItemCount = slots.ItemCount;
        Items = items;
        Position = position;
        DistanceM = distanceM;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public int SlotCount { get; }

    public int UsedSlotCount { get; }

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
    internal ContainerContentsView(ThingView thing, PositionView position, double? distanceM, ShownSlots slots,
        SiloContentsView? silo = null)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Position = position;
        DistanceM = distanceM;
        Slots = slots.Slots;
        EmptySlotsOmitted = slots.EmptyOmitted > 0 ? slots.EmptyOmitted : null;
        Silo = silo;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    public List<SlotView> Slots { get; }

    /// <summary>Empty slots left out at every depth (include_empty false); left out when none were.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? EmptySlotsOmitted { get; }

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
                Vocabulary.Text("pass {arg:depth} (max 6), or {tool:container_contents} of the " +
                    "occupant"), atLeast: true);
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
    internal SlotView(int index, string? displayName, string slotClass, OccupantView? occupant)
    {
        Index = index;
        DisplayName = displayName;
        SlotClass = slotClass;
        Occupant = occupant;
    }

    public int Index { get; }

    /// <summary>The slot's shown name (Hand, Back, Slot), as inspect_slots names it.</summary>
    public string? DisplayName { get; }

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

    /// <summary>
    /// How many of its slots a deeper call would show (occupied ones, or every one with include_empty) past the depth
    /// limit; null otherwise.
    /// </summary>
    public int? SlotsNotShown { get; }

    /// <summary>The same occupant with only these of its slots; none kept leaves its slots out.</summary>
    internal OccupantView WithSlots(List<SlotView> slots) =>
        new OccupantView(new ThingView(ReferenceId, PrefabName, DisplayName), Quantity, MaxQuantity,
            slots.Count > 0 ? slots : null, SlotsNotShown);
}

/// <summary>container_contents' slots as shown, and how many empty slots were left out on the way.</summary>
internal sealed class ShownSlots
{
    internal ShownSlots(List<SlotView> slots, int emptyOmitted)
    {
        Slots = slots;
        EmptyOmitted = emptyOmitted;
    }

    internal List<SlotView> Slots { get; }

    internal int EmptyOmitted { get; }

    /// <summary>Every slot as read: nothing left out.</summary>
    internal static ShownSlots All(List<SlotView> slots) => new ShownSlots(slots, 0);

    /// <summary>
    /// The slots without the empty ones, at every depth: an occupant that holds nothing at all keeps no slots list.
    /// Occupied slots keep their place and index.
    /// </summary>
    internal static ShownSlots Occupied(List<SlotView> slots)
    {
        int omitted = 0;
        List<SlotView> kept = WithoutEmpty(slots, ref omitted);
        return new ShownSlots(kept, omitted);
    }

    /// <summary>These slots run through filter (no-op when it is inactive), the count kept.</summary>
    internal ShownSlots Filtered(SlotFilter filter) => new ShownSlots(filter.Apply(Slots), EmptyOmitted);

    private static List<SlotView> WithoutEmpty(List<SlotView> slots, ref int omitted)
    {
        List<SlotView> kept = new List<SlotView>(slots.Count);
        foreach (SlotView slot in slots)
        {
            OccupantView? occupant = slot.Occupant;
            if (occupant == null)
            {
                omitted++;
                continue;
            }

            kept.Add(occupant.Slots == null
                ? slot
                : new SlotView(slot.Index, slot.DisplayName, slot.SlotClass,
                    occupant.WithSlots(WithoutEmpty(occupant.Slots, ref omitted))));
        }

        return kept;
    }
}

/// <summary>
/// inspect contents' prefab and name: the slots whose occupant matches (prefab keeps its prefab name; name is part of
/// the shown name, any case), or holds a match at any depth, with only those inner slots. A matching occupant keeps
/// all it holds; empty slots and slots holding no match are left out.
/// </summary>
internal sealed class SlotFilter
{
    private readonly System.Func<string?, bool>? _prefab;
    private readonly string? _name;

    internal SlotFilter(System.Func<string?, bool>? prefab, string? nameContains)
    {
        _prefab = prefab;
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
                kept.Add(new SlotView(slot.Index, slot.DisplayName, slot.SlotClass, occupant.WithSlots(inner)));
            }
        }

        return kept;
    }

    /// <summary>A silo entry matches by its own prefab or shown name, or by a prefab stored inside it.</summary>
    internal bool Keeps(SiloEntryView entry)
    {
        if (!IsActive || ((_prefab == null || _prefab(entry.PrefabName)) &&
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
            if (_prefab(content.PrefabName))
            {
                return true;
            }
        }

        return false;
    }

    private bool Matches(OccupantView occupant) =>
        (_prefab == null || _prefab(occupant.PrefabName)) &&
        (_name == null || Contains(occupant.DisplayName, _name));

    private static bool Contains(string? text, string part) =>
        text != null && text.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;
}
