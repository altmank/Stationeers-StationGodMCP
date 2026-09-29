#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Which slots a move may fill, by the rules of the game's own inventory clicks, and how a refusal is worded.
///
/// A player reaches a slot only when it is interactable (CODE): Thing.HandleSwitch refuses a slot that is not
/// Slot.IsInteractable unless forced, and InventoryWindow draws only interactable slots. A hidden slot is the game's
/// own bookkeeping, not storage: a cable or pipe coil's internal slot (whatever is put there is destroyed with the
/// coil when it is used up), a vending machine's store (filled only by its import), a machine's output while it
/// works. The player's own body slots (hands, suit, back) are filled by InventoryManager from the hotbar without that
/// check, so on a player's body only the other slot rules apply. The game's quick moves (Slot.TryMoveAll,
/// TryMoveAllOfType) also skip a slot that is not Slot.IsSwappable, so "auto" does too. A stack's own slot (a cable
/// coil is a stack) is never filled either, interactable or not: the game uses stacks up and merges them away, and
/// whatever sits in the slot goes with the stack.
/// </summary>
internal static class SlotReach
{
    /// <summary>Whether a move may put anything into this slot: a new stack, a whole item, or onto its stack.</summary>
    internal static bool Reaches(bool isInteractable, bool onPlayerBody, bool heldByStack) =>
        !heldByStack && (isInteractable || onPlayerBody);

    /// <summary>Whether "auto" may pick this slot: reachable, and one the game's quick moves use.</summary>
    internal static bool AutoPicks(bool isInteractable, bool isSwappable, bool onPlayerBody, bool heldByStack) =>
        Reaches(isInteractable, onPlayerBody, heldByStack) && isSwappable;

    /// <summary>A slot as refusals name it, by index and holder: its own name is often its class ("None").</summary>
    internal static string Label(int index, string holder) => $"Slot {index} of {holder}";

    /// <summary>The refusal for a slot a move may not fill.</summary>
    internal static string HiddenReason(int index, string holder, bool heldByStack) =>
        heldByStack
            ? $"{Label(index, holder)} belongs to a stack: the game uses stacks up and merges them away, and whatever "
              + "is in the slot goes with it."
            : $"{Label(index, holder)} is hidden (not interactable): the game keeps it for its own use and a player "
              + "cannot put anything there.";

    /// <summary>
    /// Why Slot.AllowMove refused an empty slot, in the order it checks: the item's Thing.CanEnter (with the game's
    /// own reason), the slot class, then a draggable (a crate, a portable tank), which the game never moves into a
    /// slot: it only drags it there.
    /// </summary>
    internal static string WhyRefused(bool canEnter, string? canEnterReason, bool classFits, string slotClass,
        string itemClass, bool draggable)
    {
        if (!canEnter && !string.IsNullOrEmpty(canEnterReason))
        {
            return canEnterReason!;
        }

        if (!classFits)
        {
            return $"it takes {slotClass} items and this is {itemClass}";
        }

        if (draggable)
        {
            return "it is a draggable thing, which the game puts into a slot only by dragging it, never by a move";
        }

        return canEnter ? "the game's slot rules refuse it" : "the item refuses this slot (Thing.CanEnter)";
    }
}
