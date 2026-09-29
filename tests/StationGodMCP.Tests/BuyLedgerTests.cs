using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>trader_buy's dry run adds its lines up: credits, stock and room carry from line to line.</summary>
public sealed class BuyLedgerTests
{
    private const string Coil = "coil";
    private const string Canister = "canister";

    // c8cf4f7: each line was checked on its own, so the predicted balance went below 0.
    [Fact]
    public void CreditsCarryAcrossLines()
    {
        BuyLedger<string> ledger = new BuyLedger<string>(credits: 10f, freeSlots: 10);

        BuyVerdict first = ledger.Take(Coil, 100, 2, 6f, BuyRoom.OnePerSlot);
        BuyVerdict second = ledger.Take(Canister, 100, 1, 6f, BuyRoom.OnePerSlot);
        BuyVerdict third = ledger.Take(Canister, 100, 1, 4f, BuyRoom.OnePerSlot);

        Assert.IsType<BuyVerdict.Bought>(first);
        BuyVerdict.Refused refused = Assert.IsType<BuyVerdict.Refused>(second);
        Assert.Equal("insufficient_credits", refused.Code);
        Assert.Contains("after the earlier lines", refused.Message);
        Assert.IsType<BuyVerdict.Bought>(third);
        Assert.Equal(0f, ledger.Credits, 3);
    }

    [Fact]
    public void StockCarriesPerEntry()
    {
        BuyLedger<string> ledger = new BuyLedger<string>(100f, 10);

        BuyVerdict.Bought first = Assert.IsType<BuyVerdict.Bought>(ledger.Take(Coil, 5, 3, 1f, BuyRoom.OnePerSlot));
        BuyVerdict.Refused second = Assert.IsType<BuyVerdict.Refused>(
            ledger.Take(Coil, 5, 3, 1f, BuyRoom.OnePerSlot));
        BuyVerdict.Bought other = Assert.IsType<BuyVerdict.Bought>(
            ledger.Take(Canister, 5, 3, 1f, BuyRoom.OnePerSlot));

        Assert.Equal(2, first.StockAfter);
        Assert.Equal("insufficient_stock", second.Code);
        Assert.Contains("has 2 in stock", second.Message);
        Assert.Equal(2, other.StockAfter);
    }

    // The game's order: stock before credits before room.
    [Fact]
    public void ChecksInTheGamesOrder()
    {
        BuyLedger<string> ledger = new BuyLedger<string>(1f, 0);

        Assert.Equal("insufficient_stock",
            Assert.IsType<BuyVerdict.Refused>(ledger.Take(Coil, 1, 2, 5f, BuyRoom.OnePerSlot)).Code);
        Assert.Equal("insufficient_credits",
            Assert.IsType<BuyVerdict.Refused>(ledger.Take(Coil, 5, 2, 5f, BuyRoom.OnePerSlot)).Code);
        Assert.Equal("no_room",
            Assert.IsType<BuyVerdict.Refused>(ledger.Take(Coil, 5, 1, 1f, BuyRoom.OnePerSlot)).Code);
        Assert.IsType<BuyVerdict.Bought>(ledger.Take(Coil, 5, 1, 1f, BuyRoom.Gas));
    }

    [Fact]
    public void RoomCarriesAndARunOutOfRoomBuysWhatFits()
    {
        BuyLedger<string> ledger = new BuyLedger<string>(100f, 3);

        Assert.IsType<BuyVerdict.Bought>(ledger.Take(Canister, 10, 2, 2f, BuyRoom.OnePerSlot));
        BuyVerdict.Partial partial = Assert.IsType<BuyVerdict.Partial>(
            ledger.Take(Canister, 10, 4, 8f, BuyRoom.OnePerSlot));
        BuyVerdict.Refused full = Assert.IsType<BuyVerdict.Refused>(
            ledger.Take(Coil, 10, 1, 1f, BuyRoom.OnePerSlot));

        Assert.Equal(1, partial.Quantity);
        Assert.Equal(2f, partial.Credits, 3);
        Assert.Equal("no_room", full.Code);
        Assert.Equal(96f, ledger.Credits, 3);
    }

    [Fact]
    public void StacksShareSlots()
    {
        BuyRoom coils = BuyRoom.Stacked(quantityPerUnit: 10, maxQuantity: 50);

        Assert.Equal(1, coils.SlotsFor(5));
        Assert.Equal(2, coils.SlotsFor(6));
        Assert.Equal(10, coils.UnitsFitting(2));
        Assert.Equal(2, BuyRoom.Stacked(80, 50).SlotsFor(2));
        Assert.Equal(0, BuyRoom.Gas.SlotsFor(100));

        BuyLedger<string> ledger = new BuyLedger<string>(100f, 2);
        Assert.IsType<BuyVerdict.Bought>(ledger.Take(Coil, 100, 6, 6f, coils));
        Assert.Equal(0, ledger.FreeSlots);
    }

    [Fact]
    public void APartLineCountsInThePredictedBalance()
    {
        TradeLine coil = new TradeLine("Cable Coil", "ItemCableCoil", gas: false, creditsEach: 1f);
        BatchBuilder buy = new BatchBuilder(2);
        buy.Succeeded(new TradedView(0, coil, buying: true, 2, 2f, 8));
        buy.Failed(new NotTradedView(1, coil, 1, 1f, ApiErrors.Refused("trade_failed", "room")));

        Assert.Equal(7f, TradeView.PredictedCredits(10f, buy.Build().Results, buying: true), 3);
    }
}
