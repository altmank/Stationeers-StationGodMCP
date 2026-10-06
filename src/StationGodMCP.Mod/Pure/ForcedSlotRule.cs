#nullable enable

namespace StationGodMCP.Pure;

/// <summary>Why move_item force refuses a slot; None when the item goes in.</summary>
internal enum ForcedRefusal
{
    None,
    StackSlot,
    Occupied,
    PartOfStack,
    Refuses
}

/// <summary>
/// move_item force (a cheat): an item into a slot a player cannot reach, as the game's own code fills one. A wrench on
/// a rocket payload beside a payload bay calls OnServer.MoveToSlot(payload, payloadMount.PayloadSlot) (CODE,
/// RocketPayload.AttackWith), though the bay's slot is hidden and a payload is a DraggableThing, which Slot.AllowMove
/// refuses. So force skips Slot.IsInteractable and the draggable rule, and keeps the rest of Slot.AllowMove: the slot
/// is empty (a forced move never replaces or joins what a slot holds), its class is None or the item's SlotType, and
/// the item's Thing.CanEnter allows it. A locked slot is refused before this rule. A stack's own slot (a cable coil's)
/// stays refused: whatever is put there goes with the stack. The item moves whole with OnServer.MoveToSlot.
/// </summary>
internal static class ForcedSlotRule
{
    internal static ForcedRefusal Into(bool heldByStack, bool occupied, bool wholeItem, bool canEnter, bool classFits)
    {
        if (heldByStack)
        {
            return ForcedRefusal.StackSlot;
        }

        if (occupied)
        {
            return ForcedRefusal.Occupied;
        }

        if (!wholeItem)
        {
            return ForcedRefusal.PartOfStack;
        }

        return canEnter && classFits ? ForcedRefusal.None : ForcedRefusal.Refuses;
    }

    /// <summary>The hint a hidden-slot refusal of move_item adds: force is the way in.</summary>
    internal const string HiddenSlotHint =
        " force true (a cheat; ask the user) puts a whole item into an empty hidden slot whose class takes it, as the "
        + "game's own code does (a wrench mounting a rocket payload in a payload bay).";
}
