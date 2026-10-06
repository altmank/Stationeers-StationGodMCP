#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The game's slots through Pure/SlotReach: which ones a move, a refund or a withdrawal may fill (never a hidden slot,
/// nor a stack's own slot such as a cable coil's, whose contents the game destroys with the coil), and why
/// Slot.AllowMove refused one.
/// </summary>
internal static class SlotAccess
{
    /// <summary>Whether anything may be put into this slot, or onto the stack in it.</summary>
    internal static bool Reaches(Slot slot) =>
        SlotReach.Reaches(slot.IsInteractable, OnPlayerBody(slot), HeldByStack(slot));

    /// <summary>Whether an automatic pick ("auto", a refund, a withdrawal) may use this slot.</summary>
    internal static bool AutoPicks(Slot slot) =>
        SlotReach.AutoPicks(slot.IsInteractable, slot.IsSwappable, OnPlayerBody(slot), HeldByStack(slot));

    /// <summary>Slot.AllowMove, on a slot a player can reach.</summary>
    internal static bool TakesNew(DynamicThing thing, Slot slot) => Reaches(slot) && Slot.AllowMove(thing, slot);

    /// <summary>Whether an automatic pick may put a new stack of this thing into this empty slot.</summary>
    internal static bool AutoTakesNew(DynamicThing thing, Slot slot) => AutoPicks(slot) && Slot.AllowMove(thing, slot);

    internal static string Label(Slot slot) => SlotReach.Label(slot.SlotIndex, slot.Parent.DisplayName);

    internal static string HiddenReason(Slot slot) =>
        SlotReach.HiddenReason(slot.SlotIndex, slot.Parent.DisplayName, HeldByStack(slot));

    /// <summary>Why Slot.AllowMove refuses this thing in this empty slot.</summary>
    internal static string WhyRefused(DynamicThing thing, Slot slot)
    {
        CanEnterResult enter = thing.CanEnter(slot);
        return SlotReach.WhyRefused(enter.Result, enter.Reason,
            slot.Type == Slot.Class.None || slot.Type == thing.SlotType, slot.Type.ToString(),
            thing.SlotType.ToString(), thing is DraggableThing);
    }

    /// <summary>Pure/ForcedSlotRule for move_item force: Slot.AllowMove less its reach and draggable rules.</summary>
    internal static ForcedRefusal ForcedInto(DynamicThing thing, Slot slot, bool wholeItem) =>
        ForcedSlotRule.Into(HeldByStack(slot), slot.Get() != null, wholeItem, thing.CanEnter(slot).Result,
            ClassFits(thing, slot));

    /// <summary>Why a forced move refuses this thing in this empty slot: Thing.CanEnter or the slot class.</summary>
    internal static string WhyForcedRefused(DynamicThing thing, Slot slot)
    {
        CanEnterResult enter = thing.CanEnter(slot);
        return SlotReach.WhyRefused(enter.Result, enter.Reason, ClassFits(thing, slot), slot.Type.ToString(),
            thing.SlotType.ToString(), draggable: false);
    }

    internal static bool HeldByStack(Slot slot) => slot.Parent is Stackable;

    private static bool ClassFits(DynamicThing thing, Slot slot) =>
        slot.Type == Slot.Class.None || slot.Type == thing.SlotType;

    private static bool OnPlayerBody(Slot slot) => slot.Parent is Human;
}
