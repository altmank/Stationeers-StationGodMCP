#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>trader_inventory's have: what a trader would accept from what is held, counted as its sell counts.</summary>
public sealed class SellHoldingsTests
{
    private static SellGood Good(long id, int units = 1, bool accepted = true, bool destroyed = false) =>
        new SellGood(id, units, accepted, destroyed);

    [Fact]
    public void OnlyGoodsTheConditionsAcceptCount()
    {
        // Two boxes: the trader's conditions accept the one whose contents it wants.
        List<SellGood> goods = new List<SellGood> { Good(1, accepted: true), Good(2, accepted: false) };
        Assert.Equal(1, SellHoldings.Goods(goods));
    }

    [Fact]
    public void AGoodListedTwiceCountsOnce()
    {
        // A vending machine that holds the card is both on the pad network and the card holder.
        List<SellGood> goods = new List<SellGood> { Good(7, units: 50), Good(8, units: 10), Good(7, units: 50) };
        Assert.Equal(60, SellHoldings.Goods(goods));
    }

    [Fact]
    public void GoodsBeingDestroyedOrOfNoWholeUnitDoNotCount()
    {
        List<SellGood> goods = new List<SellGood> { Good(1, units: 5, destroyed: true), Good(2, units: 0), Good(3) };
        Assert.Equal(1, SellHoldings.Goods(goods));
    }

    [Fact]
    public void GasUnitsAreWholePerAtmosphere()
    {
        // 150 mol and 150 mol at 100 mol a unit: one unit each, never a pooled third.
        Assert.Equal(2, SellHoldings.Gas(new[] { 150.0, 150.0 }, 100.0));
    }

    [Fact]
    public void NoAcceptedGasOrNoUnitSizeIsNothing()
    {
        Assert.Equal(0, SellHoldings.Gas(new double[0], 100.0));
        Assert.Equal(0, SellHoldings.Gas(new[] { 500.0 }, 0.0));
    }
}
