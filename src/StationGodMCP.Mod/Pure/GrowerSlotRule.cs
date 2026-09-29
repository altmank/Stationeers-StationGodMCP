#nullable enable

namespace StationGodMCP.Pure;

/// <summary>What a slot of a grower (a hydroponics tray, station or device) is for.</summary>
internal enum GrowerSlotKind
{
    /// <summary>Not a grower's, or a grower slot the other slot rules cover (an import, export or battery slot).</summary>
    Other,

    /// <summary>A plant slot: IGrower.PlantToFertiliserSlotMapping(...).PlantSlot.</summary>
    Plant,

    /// <summary>A fertiliser slot: IGrower.PlantToFertiliserSlotMapping(...).FertiliserSlot.</summary>
    Fertiliser
}

/// <summary>
/// A grower's slots by the game's hand interactions (CODE, HydroponicsUtils.HandlePlantInteraction), not by their slot
/// class, which lets far more in. The hand interaction never asks Slot.IsInteractable or the slot class, and it decides
/// every item offered to a plant or fertiliser slot.
///
/// A plant slot is filled only by PlantInHand: a seed or plant is planted there. Fertiliser in the hand at a plant
/// slot goes to the mapped fertiliser slot (FertiliserInHand runs before PlantInHand), and anything else is refused
/// (InvalidObjectInHand), so a fertiliser or any other item in a plant slot is a state no player makes: the grower is
/// then unplantable until it is moved out.
///
/// A fertiliser slot is filled only by FertiliserInHand: one unit of a Fertiliser off the hand's stack, into an empty
/// slot (the robotic arm's TryFertilize does the same). Anything else there is wrong, and a seed or plant is harmful:
/// HydroponicTray, HydroponicsTrayDevice, HydroponicsStation and DynamicHydroponics take ANY plant entering ANY slot
/// (OnChildEnterInventory) for their plant, so the tray's plant pointer leaves the real plant and the seed bag is
/// registered as a growing plant. A whole fertiliser stack there would be used up by the cycles of one.
///
/// A planted plant never leaves its plant slot whole: an empty hand harvests a mature or seeding plant (its fruit or
/// seeds, the plant stays) and fails otherwise (NothingInHand), alt clears it by destroying it (HandleClearPlant). A
/// seed bag in a plant slot is the broken state the game's own OnChildEnterInventory leaves, never a growing plant,
/// so it may be taken out.
/// </summary>
internal static class GrowerSlotRule
{
    /// <summary>
    /// Why a move into this grower slot is refused; null when the item may go there (a plant into a plant slot is
    /// then planted, with its own rules) or when the slot is not a grower's plant or fertiliser slot.
    /// </summary>
    internal static GrowerRefusal? Into(GrowerSlotKind kind, GrowerItem item, bool occupied, int quantity)
    {
        switch (kind)
        {
            case GrowerSlotKind.Plant:
                return item == GrowerItem.Plant ? (GrowerRefusal?)null : GrowerRefusal.NotPlant;
            case GrowerSlotKind.Fertiliser:
                return IntoFertiliserSlot(item, occupied, quantity);
            default:
                return null;
        }
    }

    private static GrowerRefusal? IntoFertiliserSlot(GrowerItem item, bool occupied, int quantity)
    {
        if (item != GrowerItem.Fertiliser)
        {
            return GrowerRefusal.NotFertiliser;
        }

        if (occupied)
        {
            return GrowerRefusal.Occupied;
        }

        return quantity != 1 ? GrowerRefusal.OneUnit : null;
    }

    /// <summary>
    /// Whether the grower's hand interaction alone decides a move into this slot, before the generic hidden-slot and
    /// slot-class rules: every item into a plant or fertiliser slot (<see cref="Into"/> judges it).
    /// HandlePlantInteraction never asks Slot.IsInteractable or the slot class, and a planter's slots and a station's
    /// fertiliser slots are hidden in the inventory window though a player fills them by hand.
    /// </summary>
    internal static bool HandDecides(GrowerSlotKind kind) => kind != GrowerSlotKind.Other;

    /// <summary>
    /// Whether "auto" may put a new stack of this item into this empty grower slot: only a plant into a plant slot and
    /// only fertiliser into a fertiliser slot, so each kind lands where the hand would send it.
    /// </summary>
    internal static bool AutoTakes(GrowerSlotKind kind, GrowerItem item)
    {
        switch (kind)
        {
            case GrowerSlotKind.Plant:
                return item == GrowerItem.Plant;
            case GrowerSlotKind.Fertiliser:
                return item == GrowerItem.Fertiliser;
            default:
                return true;
        }
    }

    /// <summary>Whether "auto" may merge into the stack in this slot: never into a plant or fertiliser.</summary>
    internal static bool AutoMerges(GrowerSlotKind kind) => kind == GrowerSlotKind.Other;

    /// <summary>Whether an item may be taken out of this slot: not a plant growing in a plant slot.</summary>
    internal static bool TakesOut(GrowerSlotKind kind, bool isPlant, bool isSeed) =>
        kind != GrowerSlotKind.Plant || !isPlant || isSeed;
}

/// <summary>Why a grower slot refuses an item, worded by what the item is.</summary>
internal static class GrowerRefusalText
{
    /// <summary>
    /// A plant slot given something else: fertiliser in the hand goes to the mapped fertiliser slot
    /// (FertiliserInHand), anything else the hand refuses (InvalidObjectInHand).
    /// </summary>
    internal static string NotPlant(string slotLabel, string itemName, GrowerItem item, int? fertiliserSlot)
    {
        string plantSlot = $"{slotLabel} is a plant slot, which a player fills only with a seed or plant";
        if (item != GrowerItem.Fertiliser)
        {
            return $"{plantSlot}; {itemName} does not go into a grower.";
        }

        return fertiliserSlot.HasValue
            ? $"{plantSlot}; fertiliser goes into the grower's fertiliser slot {fertiliserSlot.Value}, as a player's "
                + $"hand sends it: pass to_slot {fertiliserSlot.Value} or auto."
            : $"{plantSlot}; fertiliser goes into the grower's fertiliser slot.";
    }

    /// <summary>A fertiliser slot given something else; a seed or plant there is taken for the grower's plant.</summary>
    internal static string NotFertiliser(string slotLabel, GrowerItem item) =>
        $"{slotLabel} is a fertiliser slot, which a player fills only with fertiliser"
        + (item == GrowerItem.Plant ? "; the game would take a seed or plant there for the grower's plant." : ".");
}

/// <summary>What an item is to a grower's hand interaction.</summary>
internal enum GrowerItem
{
    /// <summary>Neither: the hand refuses it at a grower (InvalidObjectInHand).</summary>
    Other,

    /// <summary>A Plant, a seed bag included: PlantInHand plants it.</summary>
    Plant,

    /// <summary>A Fertiliser: FertiliserInHand adds one unit to the fertiliser slot.</summary>
    Fertiliser
}

/// <summary>Why a grower slot refuses a move.</summary>
internal enum GrowerRefusal
{
    /// <summary>A plant slot takes only a seed or plant: slot_refuses.</summary>
    NotPlant,

    /// <summary>A fertiliser slot takes only fertiliser: slot_refuses.</summary>
    NotFertiliser,

    /// <summary>A fertiliser slot holds one fertiliser, added to an empty slot only: slot_occupied.</summary>
    Occupied,

    /// <summary>A player adds one unit of fertiliser: invalid_argument.</summary>
    OneUnit
}
