#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// move item never replaces, drops or destroys what a slot holds: it fills an empty slot, or joins a stack of the
/// same prefab. Into a slot by index the whole moved stack must fit; "auto" tops up matching stacks to their maximum
/// and puts the rest into an empty slot, and passes over every other occupied slot.
/// </summary>
public sealed class SlotOccupancyTests
{
    private const long SpaceOre = 1550220;
    private const long OtherSpaceOre = 1553515;

    private static SlotOccupant Stack(long id, int quantity, bool joinable, int max = 100) =>
        new SlotOccupant(id, quantity, max, joinable);

    private static AutoSlotCandidate Held(int index, SlotOccupant occupant) =>
        new AutoSlotCandidate(index, occupant, mergesHere: true, takesNewHere: false);

    private static AutoSlotCandidate Empty(int index) =>
        new AutoSlotCandidate(index, null, mergesHere: false, takesNewHere: true);

    [Fact]
    public void AutoPassesOverAFullStackOfTheSamePrefab()
    {
        // A locker holding full Space Ore stacks, then an empty slot: 12 Space Ore go into the empty slot.
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(12, Stack(SpaceOre, 100, joinable: true)),
            Held(13, Stack(OtherSpaceOre, 100, joinable: true)),
            Empty(14),
        };

        AutoFillPart pick = Assert.Single(SlotOccupancy.FillAuto(slots, quantity: 12, wholeStack: true,
            mergeAllowed: true).Parts);

        Assert.Equal(14, pick.Index);
        Assert.False(pick.Merges);
    }

    [Fact]
    public void AutoPassesOverAStackOfAnotherPrefab()
    {
        // Dirty Ore never joins Space Ore: Slot.CanMerge says no, so the slot is passed over whatever its room.
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(12, Stack(SpaceOre, 12, joinable: false)),
            Empty(13),
        };

        AutoFillPart pick = Assert.Single(SlotOccupancy.FillAuto(slots, quantity: 10, wholeStack: true,
            mergeAllowed: true).Parts);

        Assert.Equal(13, pick.Index);
        Assert.False(pick.Merges);
    }

    [Fact]
    public void AutoJoinsAMatchingStackWithRoomBeforeAnEmptySlot()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Empty(11),
            Held(12, Stack(SpaceOre, 9, joinable: true)),
        };

        AutoFill fill = SlotOccupancy.FillAuto(slots, quantity: 10, wholeStack: true, mergeAllowed: true);

        AutoFillPart pick = Assert.Single(fill.Parts);
        Assert.Equal((12, 10, true), (pick.Index, pick.Quantity, pick.Merges));
        Assert.Null(fill.Staging);
    }

    [Fact]
    public void AutoFillsAStackToItsMaximumAndPutsTheRestInAnEmptySlot()
    {
        // 3 into a locker holding 3 of at most 5: 2 join the stack, 1 takes the empty slot.
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(0, Stack(SpaceOre, 3, joinable: true, max: 5)),
            Empty(1),
        };

        AutoFill fill = SlotOccupancy.FillAuto(slots, quantity: 3, wholeStack: true, mergeAllowed: true);

        Assert.Equal(new[] { (0, 2, true), (1, 1, false) }, Shares(fill));
        Assert.Null(fill.Staging);
    }

    [Fact]
    public void AutoTopsUpEveryStackWithRoomInSlotOrder()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Empty(0),
            Held(1, Stack(SpaceOre, 4, joinable: true, max: 5)),
            Held(2, Stack(SpaceOre, 5, joinable: true, max: 5)),
            Held(3, Stack(SpaceOre, 2, joinable: true, max: 5)),
        };

        Assert.Equal(new[] { (1, 1, true), (3, 3, true) },
            Shares(SlotOccupancy.FillAuto(slots, quantity: 4, wholeStack: true, mergeAllowed: true)));
    }

    [Fact]
    public void WithNoEmptySlotTheStacksMustTakeItAll()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(0, Stack(SpaceOre, 3, joinable: true, max: 5)),
        };

        Assert.False(SlotOccupancy.FillAuto(slots, quantity: 3, wholeStack: true, mergeAllowed: true).Found);
        Assert.Equal(new[] { (0, 2, true) },
            Shares(SlotOccupancy.FillAuto(slots, quantity: 2, wholeStack: true, mergeAllowed: true)));
    }

    private static (int, int, bool)[] Shares(AutoFill fill)
    {
        (int, int, bool)[] shares = new (int, int, bool)[fill.Parts.Count];
        for (int index = 0; index < shares.Length; index++)
        {
            AutoFillPart part = fill.Parts[index];
            shares[index] = (part.Index, part.Quantity, part.Merges);
        }

        return shares;
    }

    [Fact]
    public void AStackFilledExactlyToItsMaximumIsJoined() =>
        Assert.Null(SlotOccupancy.JoinRefusal(Stack(SpaceOre, 94, joinable: true), 6, wholeStack: true,
            mergeAllowed: true));

    [Fact]
    public void PartOfAStackIsStagedInTheEmptySlotAndJoinsFromThere()
    {
        // The game's merge takes all of a stack that fits: 4 of a larger stack are split into slot 13 first, and
        // the stack in 12 is topped up from those 4.
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(12, Stack(SpaceOre, 9, joinable: true)),
            Empty(13),
        };

        AutoFill fill = SlotOccupancy.FillAuto(slots, quantity: 4, wholeStack: false, mergeAllowed: true);

        Assert.Equal(new[] { (12, 4, true) }, Shares(fill));
        Assert.Equal(13, fill.Staging);
    }

    [Fact]
    public void PartOfAStackWithNoEmptySlotFillsOnlyStacksItFills()
    {
        // No slot to stage in: a merge from the whole stack would take all that fits, so only a stack whose room is
        // at most what is left is topped up.
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(0, Stack(SpaceOre, 90, joinable: true)),
            Held(1, Stack(SpaceOre, 97, joinable: true)),
        };

        AutoFill fill = SlotOccupancy.FillAuto(slots, quantity: 4, wholeStack: false, mergeAllowed: true);

        Assert.False(fill.Found);
        Assert.Equal(new[] { (1, 3, true) },
            Shares(SlotOccupancy.FillAuto(slots, quantity: 3, wholeStack: false, mergeAllowed: true)));
    }

    [Fact]
    public void PartOfAStackThatNoStackJoinsIsSplitIntoTheEmptySlot()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(12, Stack(SpaceOre, 100, joinable: true)),
            Empty(13),
        };

        AutoFill fill = SlotOccupancy.FillAuto(slots, quantity: 4, wholeStack: false, mergeAllowed: true);

        Assert.Equal(new[] { (13, 4, false) }, Shares(fill));
        Assert.Null(fill.Staging);
    }

    [Fact]
    public void MergeOffUsesOnlyAnEmptySlot()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(12, Stack(SpaceOre, 9, joinable: true)),
            Empty(13),
        };

        Assert.Equal(new[] { (13, 10, false) },
            Shares(SlotOccupancy.FillAuto(slots, 10, wholeStack: true, mergeAllowed: false)));
    }

    [Fact]
    public void ALockerWithNoRoomAnywhereIsRefusedNotOverwritten()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(0, Stack(SpaceOre, 100, joinable: true)),
            Held(1, Stack(OtherSpaceOre, 3, joinable: false)),
            Held(2, new SlotOccupant(307658, 1, 1, false)),
        };

        Assert.False(SlotOccupancy.FillAuto(slots, 10, wholeStack: true, mergeAllowed: true).Found);
    }

    [Fact]
    public void AnOccupiedSlotAutoMayNotMergeIntoIsPassedOver()
    {
        // A hidden, unswappable or grower slot holding a matching stack: auto neither joins nor replaces it.
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            new AutoSlotCandidate(0, Stack(SpaceOre, 1, joinable: true), mergesHere: false, takesNewHere: true),
            Empty(1),
        };

        Assert.Equal(new[] { (1, 10, false) },
            Shares(SlotOccupancy.FillAuto(slots, 10, wholeStack: true, mergeAllowed: true)));
    }

    [Fact]
    public void AnExactSlotIsNotJoinedWithMergeOff() =>
        Assert.Equal(OccupantRefusal.MergeOff,
            SlotOccupancy.JoinRefusal(Stack(SpaceOre, 50, joinable: true), 10, wholeStack: true, mergeAllowed: false));

    [Fact]
    public void AnExactSlotOfAnotherPrefabIsNotJoined() =>
        Assert.Equal(OccupantRefusal.CannotJoin,
            SlotOccupancy.JoinRefusal(Stack(SpaceOre, 50, joinable: false), 10, wholeStack: true, mergeAllowed: true));

    [Fact]
    public void AnExactSlotIsNotJoinedByPartOfAStack() =>
        Assert.Equal(OccupantRefusal.PartialMerge,
            SlotOccupancy.JoinRefusal(Stack(SpaceOre, 50, joinable: true), 10, wholeStack: false, mergeAllowed: true));

    [Fact]
    public void AnExactSlotWithoutRoomIsRefused() =>
        Assert.Equal(OccupantRefusal.StackFull,
            SlotOccupancy.JoinRefusal(Stack(SpaceOre, 100, joinable: true), 12, wholeStack: true, mergeAllowed: true));

    [Fact]
    public void AMovePlannedIntoAnEmptySlotStopsWhenTheSlotIsNowTaken() =>
        Assert.False(SlotOccupancy.StillSafe(Stack(SpaceOre, 100, joinable: true), plannedMergeInto: null, 12));

    [Fact]
    public void AMergeStopsWhenItsStackIsNoLongerTheOccupant() =>
        Assert.False(SlotOccupancy.StillSafe(Stack(OtherSpaceOre, 10, joinable: true), SpaceOre, 12));

    [Fact]
    public void AMergeStopsWhenItsStackFilledMeanwhile() =>
        Assert.False(SlotOccupancy.StillSafe(Stack(SpaceOre, 95, joinable: true), SpaceOre, 12));

    [Fact]
    public void AMergeIntoItsPlannedStackWithRoomGoesAhead() =>
        Assert.True(SlotOccupancy.StillSafe(Stack(SpaceOre, 88, joinable: true), SpaceOre, 12));

    [Fact]
    public void AMoveIntoAStillEmptySlotGoesAhead() =>
        Assert.True(SlotOccupancy.StillSafe(null, plannedMergeInto: null, 12));
}
