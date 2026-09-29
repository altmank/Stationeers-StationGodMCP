#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The items fixes from the headless live test of 1.4.4 (2026-09-29): move_item, refunds and vault withdrawals never
/// fill a hidden slot or a stack's own slot (a cable coil's destroyed the frame put there, and took refunded coils),
/// refusals name the real reason and the slot by index, and a burst pipe counts as broken.
/// </summary>
public sealed class ItemsLiveTestTests
{
    [Fact]
    public void AHiddenSlotIsNeverFilled()
    {
        Assert.False(SlotReach.Reaches(isInteractable: false, onPlayerBody: false, heldByStack: false));
        Assert.False(SlotReach.AutoPicks(false, true, false, false));
        Assert.True(SlotReach.Reaches(isInteractable: true, onPlayerBody: false, heldByStack: false));
    }

    [Fact]
    public void AStacksOwnSlotIsNeverFilledEvenWhenInteractable()
    {
        // A cable coil is a stack: using it up or merging it destroys whatever sits in its slot.
        Assert.False(SlotReach.Reaches(isInteractable: true, onPlayerBody: false, heldByStack: true));
        Assert.False(SlotReach.AutoPicks(true, true, false, true));
        Assert.Contains("belongs to a stack", SlotReach.HiddenReason(0, "Cable Coil", heldByStack: true));
    }

    [Fact]
    public void ThePlayersOwnBodySlotsFollowTheOtherRulesOnly()
    {
        // InventoryManager fills hands, suit and back from the hotbar without asking Slot.IsInteractable.
        Assert.True(SlotReach.Reaches(isInteractable: false, onPlayerBody: true, heldByStack: false));
    }

    [Fact]
    public void AutoSkipsASlotTheGamesQuickMovesSkip()
    {
        Assert.False(SlotReach.AutoPicks(true, false, false, false));
        Assert.True(SlotReach.AutoPicks(true, true, false, false));
    }

    [Fact]
    public void ADraggableIsRefusedForBeingDraggableNotForItsClass()
    {
        string why = SlotReach.WhyRefused(true, null, classFits: true, "Crate", "Crate", draggable: true);
        Assert.Contains("draggable", why);
        Assert.DoesNotContain("it takes Crate items", why);
    }

    [Fact]
    public void TheGamesOwnReasonComesFirst()
    {
        Assert.Equal("Slot does not allow dragging",
            SlotReach.WhyRefused(false, "Slot does not allow dragging", true, "None", "Crate", true));
    }

    [Fact]
    public void AClassMismatchNamesBothClasses()
    {
        Assert.Equal("it takes Battery items and this is Ore",
            SlotReach.WhyRefused(true, null, classFits: false, "Battery", "Ore", draggable: false));
    }

    [Fact]
    public void ASlotIsNamedByIndexAndHolderNotByItsClassName()
    {
        Assert.Equal("Slot 0 of ItemsB", SlotReach.Label(0, "ItemsB"));
        Assert.StartsWith("Slot 3 of Vending Machine is hidden", SlotReach.HiddenReason(3, "Vending Machine", false));
    }

    [Fact]
    public void ABurstPipeIsAWreckThoughItsDamageIsZero()
    {
        bool broken = HealthCondition.IsWreck(gameBroken: false, pipeBurst: true);
        Assert.True(broken);
        Assert.Equal(HealthCondition.Broken, HealthCondition.Of(broken, true, false, 0.0));
        Assert.True(HealthCondition.ScanKeeps(broken, true, 0.0, 0.0, brokenOnly: true));
        Assert.False(HealthCondition.IsWreck(gameBroken: false, pipeBurst: false));
    }
}
