#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// move_item never replaces, drops or destroys what a slot holds: it fills an empty slot, or joins a stack of the
/// same prefab that has room for the whole moved stack. "auto" passes over every other occupied slot.
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

        AutoSlotPick pick = SlotOccupancy.PickAuto(slots, quantity: 12, wholeStack: true, mergeAllowed: true);

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

        AutoSlotPick pick = SlotOccupancy.PickAuto(slots, quantity: 10, wholeStack: true, mergeAllowed: true);

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

        AutoSlotPick pick = SlotOccupancy.PickAuto(slots, quantity: 10, wholeStack: true, mergeAllowed: true);

        Assert.Equal(12, pick.Index);
        Assert.True(pick.Merges);
    }

    [Fact]
    public void AStackFilledExactlyToItsMaximumIsJoined() =>
        Assert.Null(SlotOccupancy.JoinRefusal(Stack(SpaceOre, 94, joinable: true), 6, wholeStack: true,
            mergeAllowed: true));

    [Fact]
    public void PartOfAStackNeverJoinsOne()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(12, Stack(SpaceOre, 9, joinable: true)),
            Empty(13),
        };

        AutoSlotPick pick = SlotOccupancy.PickAuto(slots, quantity: 4, wholeStack: false, mergeAllowed: true);

        Assert.Equal(13, pick.Index);
        Assert.False(pick.Merges);
    }

    [Fact]
    public void MergeOffUsesOnlyAnEmptySlot()
    {
        List<AutoSlotCandidate> slots = new List<AutoSlotCandidate>
        {
            Held(12, Stack(SpaceOre, 9, joinable: true)),
            Empty(13),
        };

        Assert.Equal(13, SlotOccupancy.PickAuto(slots, 10, wholeStack: true, mergeAllowed: false).Index);
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

        Assert.False(SlotOccupancy.PickAuto(slots, 10, wholeStack: true, mergeAllowed: true).Found);
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

        AutoSlotPick pick = SlotOccupancy.PickAuto(slots, 10, wholeStack: true, mergeAllowed: true);

        Assert.Equal(1, pick.Index);
        Assert.False(pick.Merges);
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
