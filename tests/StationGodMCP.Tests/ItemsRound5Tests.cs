#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The items fixes from round 5 of the headless live test (2026-09-29): a grower's plant slot takes only a seed or
/// plant (fertiliser went in, the whole stack, and auto preferred it over the empty fertiliser slot), a fertiliser
/// slot's refusal is worded by the item, and a burnt cable is a wreck.
/// </summary>
public sealed class ItemsRound5Tests
{
    [Theory]
    [InlineData("Fertiliser")]
    [InlineData("Other")]
    public void APlantSlotRefusesAnythingButASeedOrPlant(string item)
    {
        Assert.Equal(GrowerRefusal.NotPlant, GrowerSlotRule.Into(
            GrowerSlotKind.Plant, System.Enum.Parse<GrowerItem>(item), occupied: false, quantity: 1));
    }

    [Theory]
    [InlineData("Plant", "Plant", true)]
    [InlineData("Plant", "Fertiliser", false)]
    [InlineData("Plant", "Other", false)]
    [InlineData("Fertiliser", "Fertiliser", true)]
    [InlineData("Fertiliser", "Plant", false)]
    [InlineData("Fertiliser", "Other", false)]
    [InlineData("Other", "Other", true)]
    public void AutoPutsEachKindOnlyIntoItsOwnGrowerSlot(string kind, string item, bool takes)
    {
        Assert.Equal(takes, GrowerSlotRule.AutoTakes(
            System.Enum.Parse<GrowerSlotKind>(kind), System.Enum.Parse<GrowerItem>(item)));
    }

    [Fact]
    public void AutoSendsFertiliserPastAnEmptyPlantSlotToTheFertiliserSlot()
    {
        // A tray: slot 0 plant, slot 1 fertiliser, both empty; auto walks them in order.
        List<GrowerSlotKind> tray = new List<GrowerSlotKind> { GrowerSlotKind.Plant, GrowerSlotKind.Fertiliser };
        Assert.Equal(1, tray.FindIndex(kind => GrowerSlotRule.AutoTakes(kind, GrowerItem.Fertiliser)));
        Assert.Equal(0, tray.FindIndex(kind => GrowerSlotRule.AutoTakes(kind, GrowerItem.Plant)));
    }

    [Fact]
    public void FertiliserAtAPlantSlotIsToldWhereTheHandSendsIt()
    {
        Assert.Equal(
            "Slot 0 of TrayA is a plant slot, which a player fills only with a seed or plant; fertiliser goes into "
            + "the grower's fertiliser slot 1, as a player's hand sends it: pass to_slot 1 or auto.",
            GrowerRefusalText.NotPlant("Slot 0 of TrayA", "Fertilizer", GrowerItem.Fertiliser, 1));
        Assert.Equal(
            "Slot 0 of TrayA is a plant slot, which a player fills only with a seed or plant; Cable Coil does not go "
            + "into a grower.",
            GrowerRefusalText.NotPlant("Slot 0 of TrayA", "Cable Coil", GrowerItem.Other, 1));
    }

    [Fact]
    public void OnlyASeedOrPlantInAFertiliserSlotGetsTheSeedReason()
    {
        Assert.Equal("Slot 1 of PlanterA is a fertiliser slot, which a player fills only with fertiliser.",
            GrowerRefusalText.NotFertiliser("Slot 1 of PlanterA", GrowerItem.Other));
        Assert.EndsWith("the game would take a seed or plant there for the grower's plant.",
            GrowerRefusalText.NotFertiliser("Slot 1 of PlanterA", GrowerItem.Plant));
    }

    [Fact]
    public void ABurntCableIsAWreck()
    {
        bool broken = HealthCondition.IsWreck(gameBroken: false, pipeBurst: false, cableBurnt: true);
        Assert.True(broken);
        Assert.Equal(HealthCondition.Broken, HealthCondition.Of(broken, true, false, 0.0));
        Assert.True(HealthCondition.ScanKeeps(broken, true, 0.0, 0.0, brokenOnly: true));
    }
}
