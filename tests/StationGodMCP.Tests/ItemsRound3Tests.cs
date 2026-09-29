#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The items fixes from round 3 of the headless live test of 1.4.4 (2026-09-29): a grower's fertiliser slot takes
/// only fertiliser, one unit into an empty slot (a seed put there by auto was taken for the tray's plant), a growing
/// plant never leaves its plant slot whole, and a dry run with refund false shows nothing coming back.
/// </summary>
public sealed class ItemsRound3Tests
{
    [Fact]
    public void AFertiliserSlotRefusesASeed()
    {
        Assert.Equal(GrowerRefusal.NotFertiliser,
            GrowerSlotRule.Into(GrowerSlotKind.Fertiliser, isFertiliser: false, occupied: false, quantity: 1));
    }

    [Fact]
    public void AFertiliserSlotTakesOneFertiliserIntoAnEmptySlot()
    {
        Assert.Null(GrowerSlotRule.Into(GrowerSlotKind.Fertiliser, isFertiliser: true, occupied: false, quantity: 1));
        Assert.Equal(GrowerRefusal.OneUnit,
            GrowerSlotRule.Into(GrowerSlotKind.Fertiliser, isFertiliser: true, occupied: false, quantity: 5));
        Assert.Equal(GrowerRefusal.Occupied,
            GrowerSlotRule.Into(GrowerSlotKind.Fertiliser, isFertiliser: true, occupied: true, quantity: 1));
    }

    [Fact]
    public void OtherSlotsAreLeftToTheOtherRules()
    {
        // The plant slot has its own planting rule; an automated grower's import slot takes seeds from a chute.
        Assert.Null(GrowerSlotRule.Into(GrowerSlotKind.Plant, isFertiliser: false, occupied: false, quantity: 1));
        Assert.Null(GrowerSlotRule.Into(GrowerSlotKind.Other, isFertiliser: false, occupied: false, quantity: 3));
    }

    [Fact]
    public void AutoSkipsAFertiliserSlotForAnythingButFertiliser()
    {
        Assert.False(GrowerSlotRule.AutoTakes(GrowerSlotKind.Fertiliser, isFertiliser: false));
        Assert.True(GrowerSlotRule.AutoTakes(GrowerSlotKind.Fertiliser, isFertiliser: true));
        Assert.True(GrowerSlotRule.AutoTakes(GrowerSlotKind.Other, isFertiliser: false));
    }

    [Fact]
    public void AutoNeverMergesIntoAPlantOrFertiliser()
    {
        Assert.False(GrowerSlotRule.AutoMerges(GrowerSlotKind.Plant));
        Assert.False(GrowerSlotRule.AutoMerges(GrowerSlotKind.Fertiliser));
        Assert.True(GrowerSlotRule.AutoMerges(GrowerSlotKind.Other));
    }

    [Fact]
    public void AGrowingPlantStaysInItsPlantSlot()
    {
        Assert.False(GrowerSlotRule.TakesOut(GrowerSlotKind.Plant, isPlant: true, isSeed: false));
    }

    [Fact]
    public void ASeedBagInAPlantSlotOrAnythingElsewhereMayBeTakenOut()
    {
        Assert.True(GrowerSlotRule.TakesOut(GrowerSlotKind.Plant, isPlant: true, isSeed: true));
        Assert.True(GrowerSlotRule.TakesOut(GrowerSlotKind.Fertiliser, isPlant: true, isSeed: true));
        Assert.True(GrowerSlotRule.TakesOut(GrowerSlotKind.Other, isPlant: true, isSeed: false));
    }

    [Fact]
    public void RefundFalseShowsNothingComingBack()
    {
        List<int> refund = new List<int> { 1 };
        Assert.Empty(RefundShown.Items(false, refund));
        Assert.Same(refund, RefundShown.Items(true, refund));
        Assert.Equal(0, RefundShown.Count(false, 3));
        Assert.Equal(3, RefundShown.Count(true, 3));
    }

    [Fact]
    public void ASwapNetIsNeverAGiveBackWithRefundFalse()
    {
        Assert.Equal(-2, RefundShown.Net(true, 0, 2));
        Assert.Equal(0, RefundShown.Net(false, 0, 2));
        Assert.Equal(1, RefundShown.Net(false, 3, 2));
    }

    [Fact]
    public void RunMaterialsListNoRefundWithRefundFalse()
    {
        RunMaterialsView materials = new RunMaterialsView(null, new List<UpgradeCoilView>(), false,
            new List<UpgradeAmountView> { new UpgradeAmountView("ItemCableCoil", 1) });

        JObject wire = JObject.Parse(WireCheck.New(materials));

        Assert.False(wire.Value<bool>("refund_enabled"));
        Assert.Empty((JArray)wire["refund"]!);
    }

    [Fact]
    public void UpgradeReportsListNoRefundWithRefundFalse()
    {
        UpgradeResources resources = new UpgradeResources(null, new List<UpgradeCoilView>(), false,
            new List<UpgradeAmountView> { new UpgradeAmountView("ItemCableCoil", 3) }, new List<object>());

        Assert.Empty(resources.Refund);
    }

    [Fact]
    public void ASwapLineKeepsItsRefundAsTheOffsetButGivesNothingBack()
    {
        JObject wire = JObject.Parse(WireCheck.New(
            new StructureMaterialLineView("ItemSteelSheets", 0, 2, refundEnabled: false)));

        Assert.Equal(2, wire.Value<int>("refund"));
        Assert.Equal(0, wire.Value<int>("net"));
    }
}
