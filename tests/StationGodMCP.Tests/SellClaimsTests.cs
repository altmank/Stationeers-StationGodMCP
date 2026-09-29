#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// trader_sell's bookkeeping across the lines of one call: no unit counted twice, and a replay of the game's own walk
/// that finds a sale which would be paid for goods it does not remove.
/// </summary>
public sealed class SellClaimsTests
{
    private static SellGood Ore(long id, int units = 1, bool accepted = true, bool destroyed = false) =>
        new SellGood(id, units, accepted, destroyed);

    [Fact]
    public void LinesOfOneItemUseDifferentUnits()
    {
        // serial-2: four ore of one unit each, five lines of one: the fifth finds none left.
        List<SellGood> goods = new List<SellGood> { Ore(2823), Ore(2824), Ore(2825), Ore(2826) };
        SellClaims claims = new SellClaims();
        for (int line = 0; line < 4; line++)
        {
            Assert.Equal(4 - line, claims.Available(goods));
            claims.Claim(goods, 1);
        }

        Assert.Equal(0, claims.Available(goods));
    }

    [Fact]
    public void AStackIsClaimedInPart()
    {
        List<SellGood> goods = new List<SellGood> { Ore(1, units: 50), Ore(2, units: 10) };
        SellClaims claims = new SellClaims();
        claims.Claim(goods, 45);
        Assert.Equal(15, claims.Available(goods));
        claims.Claim(goods, 10);
        Assert.Equal(5, claims.Available(goods));
    }

    [Fact]
    public void AGoodListedTwiceCountsOnce()
    {
        // A card in a vending machine on the pad's network: the game lists that machine's goods twice.
        List<SellGood> goods = new List<SellGood> { Ore(1), Ore(2), Ore(1), Ore(2) };
        Assert.Equal(2, new SellClaims().Available(goods));
    }

    [Fact]
    public void RefusedDestroyedAndFractionalGoodsDoNotCount()
    {
        List<SellGood> goods = new List<SellGood>
        {
            Ore(1, accepted: false), Ore(2, destroyed: true), Ore(3, units: 0), Ore(4, units: 3),
        };
        Assert.Equal(3, new SellClaims().Available(goods));
    }

    [Fact]
    public void GasUnitsLeaveOutClaimedMoles()
    {
        SellClaims claims = new SellClaims();
        Assert.Equal(10, claims.GasUnits(101.0, 10.0));
        claims.ClaimMoles(60.0);
        Assert.Equal(4, claims.GasUnits(101.0, 10.0));
        claims.ClaimMoles(50.0);
        Assert.Equal(0, claims.GasUnits(101.0, 10.0));
        Assert.Equal(0, new SellClaims().GasUnits(101.0, 0.0));
    }

    [Fact]
    public void TheGameWalkTakingADestroyedGoodRepeats()
    {
        // Sold earlier in the call: marked, but still first in its slot until the end of the frame.
        List<SellGood> goods = new List<SellGood> { Ore(1, destroyed: true), Ore(2), Ore(3) };
        Assert.True(SellPick.Repeats(goods, 1));
    }

    [Fact]
    public void TheGameWalkTakingAGoodTwiceRepeats()
    {
        List<SellGood> goods = new List<SellGood> { Ore(1), Ore(2), Ore(1), Ore(2) };
        Assert.False(SellPick.Repeats(goods, 2));
        Assert.True(SellPick.Repeats(goods, 3));
    }

    [Fact]
    public void TheGameWalkStopsWhenEnoughIsTaken()
    {
        List<SellGood> goods = new List<SellGood>
        {
            Ore(1, accepted: false, destroyed: true), Ore(2, units: 5), Ore(3, destroyed: true),
        };
        Assert.False(SellPick.Repeats(goods, 5));
        Assert.False(SellPick.Repeats(goods, 3));
        Assert.True(SellPick.Repeats(goods, 6));
    }
}
