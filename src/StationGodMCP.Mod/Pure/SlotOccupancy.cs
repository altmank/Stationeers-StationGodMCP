#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>What a slot holds, as a move sees it: nothing, or a stack (or single item) and whether the move may join it.</summary>
internal readonly struct SlotOccupant
{
    internal SlotOccupant(long id, int quantity, int maxQuantity, bool joinable)
    {
        Id = id;
        Quantity = quantity;
        MaxQuantity = maxQuantity;
        Joinable = joinable;
    }

    internal long Id { get; }

    internal int Quantity { get; }

    internal int MaxQuantity { get; }

    /// <summary>The game would merge the moved stack into it (Slot.CanMerge: same prefab, IMergeable.CanStack).</summary>
    internal bool Joinable { get; }

    /// <summary>Whether this many more fit under its MaxQuantity.</summary>
    internal bool Fits(int quantity) => Quantity + quantity <= MaxQuantity;
}

/// <summary>One slot of the destination, as "auto" weighs it.</summary>
internal readonly struct AutoSlotCandidate
{
    internal AutoSlotCandidate(int index, SlotOccupant? occupant, bool mergesHere, bool takesNewHere)
    {
        Index = index;
        Occupant = occupant;
        MergesHere = mergesHere;
        TakesNewHere = takesNewHere;
    }

    internal int Index { get; }

    internal SlotOccupant? Occupant { get; }

    /// <summary>The slot is one auto may merge into (reachable, swappable, not a grower slot that never merges).</summary>
    internal bool MergesHere { get; }

    /// <summary>The slot, when empty, takes the item by the game's quick-move rules (or a grower's hand rules).</summary>
    internal bool TakesNewHere { get; }
}

/// <summary>The slot auto chose: its index, and whether the move joins the stack there.</summary>
internal readonly struct AutoSlotPick
{
    internal AutoSlotPick(int index, bool merges)
    {
        Index = index;
        Merges = merges;
    }

    internal static AutoSlotPick None { get; } = new AutoSlotPick(-1, false);

    internal int Index { get; }

    internal bool Merges { get; }

    internal bool Found => Index >= 0;
}

/// <summary>
/// The one rule every move obeys: what a slot already holds is never replaced, dropped or destroyed. A move goes into
/// an empty slot, or joins the stack there only when the game would merge the two (Slot.CanMerge: the same prefab),
/// the whole stack moves (the game has no partial merge) and all of it fits under the stack's MaxQuantity. "auto"
/// picks a joinable stack with room first, when merging is allowed, then the first empty slot that takes the item;
/// any other occupied slot is passed over.
/// </summary>
internal static class SlotOccupancy
{
    /// <summary>Why the move may not go into a slot holding this occupant; null when it may join it.</summary>
    internal static OccupantRefusal? JoinRefusal(SlotOccupant occupant, int quantity, bool wholeStack, bool mergeAllowed)
    {
        if (!mergeAllowed)
        {
            return OccupantRefusal.MergeOff;
        }

        if (!occupant.Joinable)
        {
            return OccupantRefusal.CannotJoin;
        }

        if (!wholeStack)
        {
            return OccupantRefusal.PartialMerge;
        }

        return occupant.Fits(quantity) ? (OccupantRefusal?)null : OccupantRefusal.StackFull;
    }

    internal static AutoSlotPick PickAuto(IReadOnlyList<AutoSlotCandidate> slots, int quantity, bool wholeStack,
        bool mergeAllowed)
    {
        for (int index = 0; index < slots.Count && mergeAllowed; index++)
        {
            AutoSlotCandidate slot = slots[index];
            if (slot.Occupant.HasValue && slot.MergesHere &&
                JoinRefusal(slot.Occupant.Value, quantity, wholeStack, mergeAllowed) == null)
            {
                return new AutoSlotPick(slot.Index, true);
            }
        }

        for (int index = 0; index < slots.Count; index++)
        {
            AutoSlotCandidate slot = slots[index];
            if (!slot.Occupant.HasValue && slot.TakesNewHere)
            {
                return new AutoSlotPick(slot.Index, false);
            }
        }

        return AutoSlotPick.None;
    }

    /// <summary>
    /// The last check before the game call, against the slot as it is now: the plan's merge target must still be the
    /// occupant and still have room, and a move planned into an empty slot must find it empty.
    /// </summary>
    internal static bool StillSafe(SlotOccupant? now, long? plannedMergeInto, int quantity) =>
        plannedMergeInto.HasValue
            ? now.HasValue && now.Value.Id == plannedMergeInto.Value && now.Value.Joinable && now.Value.Fits(quantity)
            : !now.HasValue;
}

/// <summary>Why a move may not join what a slot holds.</summary>
internal enum OccupantRefusal
{
    MergeOff,
    CannotJoin,
    PartialMerge,
    StackFull,
}
