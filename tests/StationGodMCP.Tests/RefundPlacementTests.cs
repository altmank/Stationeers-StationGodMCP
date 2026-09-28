#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Where a refund goes (RefundPlacement), in the player's order: matching stacks held first, then empty slots that take the
/// item, the ground beside the holder only for what is left. The 2026-09-27 incident: 25 wall kits made at the
/// player's position hit the suit.
/// </summary>
public sealed class RefundPlacementTests
{
    private static string Describe(List<RefundStep> steps) =>
        string.Join(" ", steps.ConvertAll(step => step switch
        {
            RefundStep.Merge merge => $"merge{merge.Stack}:{step.Quantity}",
            RefundStep.IntoSlot slot => $"slot{slot.Slot}:{step.Quantity}",
            _ => $"ground:{step.Quantity}"
        }));

    [Fact]
    public void StacksHeldAreToppedUpBeforeAnyNewItemIsMade()
    {
        // 25 kits, a stack of 3 in the jetpack (room 7), two empty slots: nothing goes to the ground.
        List<RefundStep> steps = RefundPlacement.Plan(25, 10, new List<int> { 7 }, 2);
        Assert.Equal("merge0:7 slot0:10 slot1:8", Describe(steps));
    }

    [Fact]
    public void EveryMatchingStackIsFilledInInventoryOrder()
    {
        Assert.Equal("merge0:2 merge1:5 merge2:1", Describe(RefundPlacement.Plan(8, 10, new List<int> { 2, 5, 9 }, 3)));
    }

    [Fact]
    public void OnlyWhatNoStackOrSlotTakesGoesToTheGroundInFullStacks()
    {
        Assert.Equal("slot0:10 ground:10 ground:5", Describe(RefundPlacement.Plan(25, 10, new List<int>(), 1)));
        Assert.Equal("ground:10 ground:10 ground:5", Describe(RefundPlacement.Plan(25, 10, new List<int>(), 0)));
    }

    [Fact]
    public void AnItemThatDoesNotStackTakesOneSlotEach()
    {
        Assert.Equal("slot0:1 slot1:1 ground:1", Describe(RefundPlacement.Plan(3, 1, new List<int> { 4 }, 2)));
    }

    [Fact]
    public void NothingToGiveBackMakesNothing()
    {
        Assert.Empty(RefundPlacement.Plan(0, 10, new List<int> { 5 }, 2));
        Assert.Empty(RefundPlacement.Plan(-2, 10, new List<int>(), 0));
    }

    [Fact]
    public void EveryItemIsAccountedForOnce()
    {
        for (int quantity = 1; quantity <= 60; quantity += 7)
        {
            int total = 0;
            foreach (RefundStep step in RefundPlacement.Plan(quantity, 20, new List<int> { 3, 0, 11 }, 1))
            {
                Assert.InRange(step.Quantity, 1, 20);
                total += step.Quantity;
            }

            Assert.Equal(quantity, total);
        }
    }
}
