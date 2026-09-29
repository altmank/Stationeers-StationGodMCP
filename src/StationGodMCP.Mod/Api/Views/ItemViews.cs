#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>One find_items entry: an item, or a machine's stock.</summary>
internal interface IFoundItemView
{
}

/// <summary>
/// An item in the world and where it is, as find_items lists it. Tools that add fields to an item derive from this;
/// its fields always come first on the wire.
/// </summary>
internal abstract class ItemFieldsView
{
    protected ItemFieldsView(ItemFields fields)
    {
        ReferenceId = fields.Item.ReferenceId;
        PrefabName = fields.Item.PrefabName;
        DisplayName = fields.Item.DisplayName;
        Quantity = fields.Quantity;
        MaxQuantity = fields.MaxQuantity;
        Location = fields.Place.Location;
        CarriedBy = fields.Place.CarriedBy;
        HeldIn = fields.Place.HeldIn;
        Position = fields.Place.Position;
        DistanceM = fields.Place.DistanceM;
    }

    [JsonProperty(Order = -20)]
    public ThingId ReferenceId { get; }

    [JsonProperty(Order = -19)]
    public string? PrefabName { get; }

    [JsonProperty(Order = -18)]
    public string? DisplayName { get; }

    /// <summary>The stack size for stackable items (IQuantity), 1 for anything else.</summary>
    [JsonProperty(Order = -17)]
    public double Quantity { get; }

    [JsonProperty(Order = -16)]
    public double? MaxQuantity { get; }

    /// <summary>ground, player or stored (machine stock has its own view).</summary>
    [JsonProperty(Order = -15)]
    public string Location { get; }

    [JsonProperty(Order = -14)]
    public string? CarriedBy { get; }

    /// <summary>The item's slot first, then outwards.</summary>
    [JsonProperty(Order = -13)]
    public List<HeldInView> HeldIn { get; }

    /// <summary>The outermost holder's position (the item's own when loose).</summary>
    [JsonProperty(Order = -12)]
    public PositionView Position { get; }

    [JsonProperty(Order = -11)]
    public double? DistanceM { get; }
}

internal sealed class ItemView : ItemFieldsView, IFoundItemView
{
    internal ItemView(ItemFields fields) : base(fields)
    {
    }
}

/// <summary>
/// Material a machine holds as reagent stock, listed like an item (same field names and order) with location
/// machine_stock. It is not an item: reference_id is null, move_item cannot move it, and held_in names the machine
/// with slot_index -1. machine_stock says what it is and how to get it out.
/// </summary>
internal sealed class StockItemView : IFoundItemView
{
    internal const int NoSlot = -1;
    internal const string StockSlotName = "reagent stock";

    internal StockItemView(string? prefabName, string? displayName, double quantity, List<HeldInView> heldIn,
        PositionView position, double? distanceM, MachineStockView machineStock)
    {
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Quantity = quantity;
        HeldIn = heldIn;
        Position = position;
        DistanceM = distanceM;
        MachineStock = machineStock;
    }

    public ThingId? ReferenceId => null;

    /// <summary>The ingot a fabricator ejects it as; null for a working load (a furnace's melt) or a reagent.</summary>
    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>Reagent units: for fabricator stock, ingots.</summary>
    public double Quantity { get; }

    public double? MaxQuantity => null;

    public string Location => MachineStockView.Location;

    public string? CarriedBy => null;

    public List<HeldInView> HeldIn { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    public MachineStockView MachineStock { get; }
}

/// <summary>What a stock entry is and how to get it out.</summary>
internal sealed class MachineStockView
{
    internal const string Location = "machine_stock";

    private const string FabricatorHow =
        "Open the machine while it is on, powered and not printing: it ejects its stock as ingots into its export " +
        "slot, one stack per tick (EjectReagent on its logic stack ejects one reagent). Not movable with move_item.";

    private const string ProcessingHow =
        "A working load, not ingots yet: the machine smelts or processes it, or drops it as Reagent Mix. Not " +
        "movable with move_item.";

    internal MachineStockView(string reagent, string? reagentName, string kind)
    {
        Reagent = reagent;
        ReagentName = reagentName;
        Kind = kind;
    }

    /// <summary>Reagent.TypeName, e.g. Electrum.</summary>
    public string Reagent { get; }

    public string? ReagentName { get; }

    /// <summary>fabricator (ejectable ingots) or processing (a furnace's, centrifuge's or mixer's load).</summary>
    public string Kind { get; }

    public bool Movable => false;

    public string HowToGet => Kind == "fabricator" ? FabricatorHow : ProcessingHow;
}

/// <summary>An item's identity, amount and place, to build any item view from.</summary>
internal sealed class ItemFields
{
    internal ItemFields(ThingView item, double quantity, double? maxQuantity, ItemPlace place)
    {
        Item = item;
        Quantity = quantity;
        MaxQuantity = maxQuantity;
        Place = place;
    }

    internal ThingView Item { get; }

    internal double Quantity { get; }

    internal double? MaxQuantity { get; }

    internal ItemPlace Place { get; }
}

/// <summary>Where an item is: loose, carried or stored, its holders, and the outermost holder's position.</summary>
internal sealed class ItemPlace
{
    internal ItemPlace(string location, string? carriedBy, List<HeldInView> heldIn, PositionView position,
        double? distanceM)
    {
        Location = location;
        CarriedBy = carriedBy;
        HeldIn = heldIn;
        Position = position;
        DistanceM = distanceM;
    }

    internal string Location { get; }

    internal string? CarriedBy { get; }

    internal List<HeldInView> HeldIn { get; }

    internal PositionView Position { get; }

    internal double? DistanceM { get; }
}

/// <summary>One holder of an item and the slot the item (or the next holder in) sits in.</summary>
internal sealed class HeldInView
{
    internal HeldInView(ThingView holder, int slotIndex, string? slotName)
    {
        ReferenceId = holder.ReferenceId;
        PrefabName = holder.PrefabName;
        DisplayName = holder.DisplayName;
        SlotIndex = slotIndex;
        SlotName = slotName;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public int SlotIndex { get; }

    public string? SlotName { get; }
}
