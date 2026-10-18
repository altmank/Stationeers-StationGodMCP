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

    /// <summary>How many more fit under its MaxQuantity.</summary>
    internal int Room => System.Math.Max(MaxQuantity - Quantity, 0);
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

/// <summary>One slot an auto move fills: its index, how many go there, and whether they join the stack it holds.</summary>
internal readonly struct AutoFillPart
{
    internal AutoFillPart(int index, int quantity, bool merges)
    {
        Index = index;
        Quantity = quantity;
        Merges = merges;
    }

    internal int Index { get; }

    internal int Quantity { get; }

    internal bool Merges { get; }
}

/// <summary>
/// Where auto puts a move's items, in order: the stacks it tops up, then the empty slot that takes the rest. Staging is
/// the empty slot part of a stack is split into first, so the stacks are topped up from the part and not from the
/// whole stack (the game's merge takes all that fits); null when the moved items join the stacks from their own stack.
/// </summary>
internal sealed class AutoFill
{
    internal AutoFill(List<AutoFillPart> parts, int? staging)
    {
        Parts = parts;
        Staging = staging;
    }

    internal static AutoFill None { get; } = new AutoFill(new List<AutoFillPart>(), null);

    internal IReadOnlyList<AutoFillPart> Parts { get; }

    internal int? Staging { get; }

    internal bool Found => Parts.Count > 0;
}

/// <summary>
/// The one rule every move obeys: what a slot already holds is never replaced, dropped or destroyed. A move goes into
/// an empty slot, or joins the stack there only when the game would merge the two (Slot.CanMerge: the same prefab).
/// Into a slot given by index, the whole stack moves (the game's merge takes the whole stack) and all of it fits under
/// the stack's MaxQuantity. "auto", when merging is allowed, tops up each joinable stack with room in slot order until
/// the items run out, then puts the rest into the first empty slot that takes the item; any other occupied slot is
/// passed over.
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

    /// <summary>
    /// Where auto puts quantity items: joinable stacks topped up to their MaxQuantity in slot order, the rest into the
    /// first empty slot that takes the item; None when the empty slot the rest needs is missing. Part of a stack is
    /// staged in that empty slot first; with no empty slot it tops up only stacks it fills (the game's merge of the
    /// whole stack takes all that fits, so a stack with more room than is left would take more than asked).
    /// </summary>
    internal static AutoFill FillAuto(IReadOnlyList<AutoSlotCandidate> slots, int quantity, bool wholeStack,
        bool mergeAllowed)
    {
        int? empty = FirstEmpty(slots);
        bool staged = !wholeStack && empty.HasValue;
        List<AutoFillPart> parts = new List<AutoFillPart>();
        int left = quantity;
        for (int index = 0; index < slots.Count && mergeAllowed && left > 0; index++)
        {
            AutoSlotCandidate slot = slots[index];
            if (!slot.Occupant.HasValue || !slot.MergesHere || !slot.Occupant.Value.Joinable)
            {
                continue;
            }

            int room = slot.Occupant.Value.Room;
            if (room == 0 || (!wholeStack && !staged && room > left))
            {
                continue;
            }

            int added = System.Math.Min(room, left);
            parts.Add(new AutoFillPart(slot.Index, added, true));
            left -= added;
        }

        if (left > 0)
        {
            if (!empty.HasValue)
            {
                return AutoFill.None;
            }

            parts.Add(new AutoFillPart(empty.Value, left, false));
        }

        return new AutoFill(parts, staged && parts[0].Merges ? empty : null);
    }

    private static int? FirstEmpty(IReadOnlyList<AutoSlotCandidate> slots)
    {
        foreach (AutoSlotCandidate slot in slots)
        {
            if (!slot.Occupant.HasValue && slot.TakesNewHere)
            {
                return slot.Index;
            }
        }

        return null;
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
