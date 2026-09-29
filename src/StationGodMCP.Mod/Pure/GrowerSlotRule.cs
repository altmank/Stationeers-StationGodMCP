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
/// class, which lets far more in.
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
    /// <summary>Why a move into this grower slot is refused; null when the other slot rules decide.</summary>
    internal static GrowerRefusal? Into(GrowerSlotKind kind, bool isFertiliser, bool occupied, int quantity)
    {
        if (kind != GrowerSlotKind.Fertiliser)
        {
            return null;
        }

        if (!isFertiliser)
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
    /// slot-class rules: a fertiliser slot (FertiliserInHand fills it, anything else is refused by <see cref="Into"/>)
    /// and a plant slot given a seed or plant (PlantInHand plants it). HandlePlantInteraction never asks
    /// Slot.IsInteractable or the slot class, and a planter's slots and a station's fertiliser slots are hidden in the
    /// inventory window though a player fills them by hand. Anything else into a plant slot follows the generic rules.
    /// </summary>
    internal static bool HandDecides(GrowerSlotKind kind, bool isPlant) =>
        kind == GrowerSlotKind.Fertiliser || (kind == GrowerSlotKind.Plant && isPlant);

    /// <summary>Whether "auto" may put a new stack of this item into this empty grower slot.</summary>
    internal static bool AutoTakes(GrowerSlotKind kind, bool isFertiliser) =>
        kind != GrowerSlotKind.Fertiliser || isFertiliser;

    /// <summary>Whether "auto" may merge into the stack in this slot: never into a plant or fertiliser.</summary>
    internal static bool AutoMerges(GrowerSlotKind kind) => kind == GrowerSlotKind.Other;

    /// <summary>Whether an item may be taken out of this slot: not a plant growing in a plant slot.</summary>
    internal static bool TakesOut(GrowerSlotKind kind, bool isPlant, bool isSeed) =>
        kind != GrowerSlotKind.Plant || !isPlant || isSeed;
}

/// <summary>Why a grower slot refuses a move.</summary>
internal enum GrowerRefusal
{
    /// <summary>A fertiliser slot takes only fertiliser: slot_refuses.</summary>
    NotFertiliser,

    /// <summary>A fertiliser slot holds one fertiliser, added to an empty slot only: slot_occupied.</summary>
    Occupied,

    /// <summary>A player adds one unit of fertiliser: invalid_argument.</summary>
    OneUnit
}
