#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>One part of a delivery: onto a stack already there, as a new stack in an empty slot, or on the ground.</summary>
internal abstract class PlacementStep
{
    private PlacementStep(double quantity)
    {
        Quantity = quantity;
    }

    internal double Quantity { get; }

    /// <summary>Added to the matching stack at Stack (an index into the rooms offered).</summary>
    internal sealed class TopUp : PlacementStep
    {
        internal TopUp(int stack, double quantity)
            : base(quantity)
        {
            Stack = stack;
        }

        internal int Stack { get; }
    }

    /// <summary>A new stack of Quantity made straight into the empty slot at Slot (an index into the slots offered).</summary>
    internal sealed class NewStack : PlacementStep
    {
        internal NewStack(int slot, double quantity)
            : base(quantity)
        {
            Slot = slot;
        }

        internal int Slot { get; }
    }

    /// <summary>A new stack of Quantity put down on the ground beside the holder.</summary>
    internal sealed class Ground : PlacementStep
    {
        internal Ground(double quantity)
            : base(quantity)
        {
        }
    }
}

/// <summary>Where a delivery goes, and how much of it nothing takes (only when the ground was not allowed).</summary>
internal sealed class StackPlan
{
    internal StackPlan(List<PlacementStep> steps, double unplaced)
    {
        Steps = steps;
        Unplaced = unplaced;
    }

    internal List<PlacementStep> Steps { get; }

    /// <summary>What no stack, slot or (allowed) ground takes; 0 when it all fits.</summary>
    internal double Unplaced { get; }

    internal bool Fits => Unplaced <= 0.0;

    internal double Placed
    {
        get
        {
            double placed = 0.0;
            foreach (PlacementStep step in Steps)
            {
                placed += step.Quantity;
            }

            return placed;
        }
    }
}

/// <summary>
/// Where an amount of one item goes, in the player's order: onto matching stacks already there (each up to its full
/// stack, in the order offered), then as new stacks into empty slots, then, only when allowed, on the ground; never
/// more per new stack than a full stack. Quantities are doubles because ingots hold fractional grams; for items that
/// count whole, callers pass whole numbers and get whole numbers back.
/// </summary>
internal static class StackPlacement
{
    /// <summary>Below this an amount is rounding, not material.</summary>
    internal const double Trace = 1e-9;

    /// <param name="quantity">How much to deliver.</param>
    /// <param name="fullStack">The item's full stack (1 for an item that does not stack).</param>
    /// <param name="rooms">The room left on each matching stack, in the order to fill them.</param>
    /// <param name="emptySlots">How many empty slots take the item.</param>
    /// <param name="allowGround">Whether what is left may go on the ground.</param>
    internal static StackPlan Plan(double quantity, double fullStack, IReadOnlyList<double> rooms, int emptySlots,
        bool allowGround)
    {
        List<PlacementStep> steps = new List<PlacementStep>();
        double left = quantity;
        double full = fullStack > Trace ? fullStack : 1.0;
        for (int stack = 0; stack < rooms.Count && left > Trace; stack++)
        {
            double part = Math.Min(left, rooms[stack]);
            if (part > Trace)
            {
                steps.Add(new PlacementStep.TopUp(stack, part));
                left -= part;
            }
        }

        for (int slot = 0; slot < emptySlots && left > Trace; slot++)
        {
            double part = Math.Min(left, full);
            steps.Add(new PlacementStep.NewStack(slot, part));
            left -= part;
        }

        while (allowGround && left > Trace)
        {
            double part = Math.Min(left, full);
            steps.Add(new PlacementStep.Ground(part));
            left -= part;
        }

        return new StackPlan(steps, left > Trace ? left : 0.0);
    }
}
