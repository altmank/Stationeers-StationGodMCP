#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>One part of a refund's delivery: onto a stack already held, as a new item in an empty slot, or on the ground.</summary>
internal abstract class RefundStep
{
    private RefundStep(int quantity)
    {
        Quantity = quantity;
    }

    internal int Quantity { get; }

    /// <summary>Added to the matching stack at Stack (an index into the stacks offered).</summary>
    internal sealed class Merge : RefundStep
    {
        internal Merge(int stack, int quantity)
            : base(quantity)
        {
            Stack = stack;
        }

        internal int Stack { get; }
    }

    /// <summary>A new item of Quantity made straight into the empty slot at Slot (an index into the slots offered).</summary>
    internal sealed class IntoSlot : RefundStep
    {
        internal IntoSlot(int slot, int quantity)
            : base(quantity)
        {
            Slot = slot;
        }

        internal int Slot { get; }
    }

    /// <summary>A new item of Quantity put down on the ground beside the holder, at rest.</summary>
    internal sealed class Ground : RefundStep
    {
        internal Ground(int quantity)
            : base(quantity)
        {
        }
    }
}

/// <summary>
/// Where a refund of one item goes, in LU's order: first onto matching stacks the holder already carries anywhere in
/// its inventory (each up to its maximum, in the order offered), then as new stacks into empty slots that accept the
/// item, then, only for what is left, onto the ground beside the holder. Never more per new item than a full stack.
/// </summary>
internal static class RefundPlacement
{
    /// <param name="quantity">How many to give back.</param>
    /// <param name="maxStack">The item's full stack (1 for an item that does not stack).</param>
    /// <param name="stackRooms">Room left on each matching stack held, in inventory order.</param>
    /// <param name="emptySlots">How many empty slots that accept the item, in inventory order.</param>
    internal static List<RefundStep> Plan(int quantity, int maxStack, IReadOnlyList<int> stackRooms, int emptySlots)
    {
        int full = Math.Max(1, maxStack);
        int left = Math.Max(0, quantity);
        List<RefundStep> steps = new List<RefundStep>();
        for (int index = 0; index < stackRooms.Count && left > 0 && full > 1; index++)
        {
            int part = Math.Min(left, stackRooms[index]);
            if (part > 0)
            {
                steps.Add(new RefundStep.Merge(index, part));
                left -= part;
            }
        }

        for (int slot = 0; slot < emptySlots && left > 0; slot++)
        {
            int part = Math.Min(left, full);
            steps.Add(new RefundStep.IntoSlot(slot, part));
            left -= part;
        }

        while (left > 0)
        {
            int part = Math.Min(left, full);
            steps.Add(new RefundStep.Ground(part));
            left -= part;
        }

        return steps;
    }
}
