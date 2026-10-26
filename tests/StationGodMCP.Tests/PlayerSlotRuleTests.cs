#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// move item to: "player" with slot auto (live, Mimas 2026-10-25: three Mining Charges answered no_free_slot while a
/// hand and two Mining Belt Tool slots were free): the slots of what the player wears come first, then a hand; body
/// slots and a held tool's slots are left out; hands skip the quick-move swappable rule; the refusal names each reason.
/// </summary>
public sealed class PlayerSlotRuleTests
{
    [Fact]
    public void WornSlotsComeBeforeAHand()
    {
        Assert.Equal(0, PlayerSlotRule.Order(PlayerSlotPlace.Worn));
        Assert.Equal(1, PlayerSlotRule.Order(PlayerSlotPlace.Hand));
    }

    [Fact]
    public void BodySlotsAndAHeldToolsSlotsAreLeftOut()
    {
        Assert.Null(PlayerSlotRule.Order(PlayerSlotPlace.Body));
        Assert.Null(PlayerSlotRule.Order(PlayerSlotPlace.InHand));
    }

    [Fact]
    public void AHandIsNotHeldToTheQuickMoveRule()
    {
        Assert.False(PlayerSlotRule.NeedsSwappable(PlayerSlotPlace.Hand));
        Assert.True(PlayerSlotRule.NeedsSwappable(PlayerSlotPlace.Worn));
    }

    [Fact]
    public void TheRefusalNamesEachSlotsReason()
    {
        string text = PlayerSlotRule.NoSlot("LU", "Mining Charge", false,
            new[] { "Slot 0 of Mining Belt holds Drill", "Slot 1 of LU holds Wrench" });
        Assert.Equal("LU has no slot that takes Mining Charge: Slot 0 of Mining Belt holds Drill; Slot 1 of LU holds Wrench.",
            text);
    }

    [Fact]
    public void ManyReasonsAreCounted()
    {
        string[] reasons = new string[PlayerSlotRule.ShownReasons + 2];
        for (int index = 0; index < reasons.Length; index++)
        {
            reasons[index] = "r" + index;
        }

        string text = PlayerSlotRule.NoSlot("LU", "Ore", true, reasons);
        Assert.StartsWith("LU has no slot that takes Ore (its matching stacks have no room for all of it): r0;", text);
        Assert.EndsWith("; and 2 more.", text);
        Assert.Equal("Locker has no slot that takes Ore.", PlayerSlotRule.NoSlot("Locker", "Ore", false, new string[0]));
    }
}
