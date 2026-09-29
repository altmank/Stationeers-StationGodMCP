#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>trader_buy and trader_sell: the reply shapes the tool descriptions promise.</summary>
public sealed class TraderTradeViewTests
{
    private static readonly TradeLine Oxite = new TradeLine("Oxite", "ItemOxite", gas: false, creditsEach: 2.5f);

    [Fact]
    public void BuyLineShowsSpentAndStock()
    {
        Assert.Equal(
            "{\"index\":0,\"ok\":true,\"name\":\"Oxite\",\"prefab_name\":\"ItemOxite\",\"gas\":false,\"quantity\":10,"
            + "\"credits_each\":2.5,\"credits_spent\":25.0,\"stock_after\":90}",
            WireCheck.New(new TradedView(0, Oxite, buying: true, 10, 25f, 90)));
    }

    [Fact]
    public void SellLineShowsEarnedAndWanted()
    {
        Assert.Equal(
            "{\"index\":1,\"ok\":true,\"name\":\"Oxite\",\"prefab_name\":\"ItemOxite\",\"gas\":false,\"quantity\":4,"
            + "\"credits_each\":2.5,\"credits_earned\":10.0,\"wanted_after\":6}",
            WireCheck.New(new TradedView(1, Oxite, buying: false, 4, 10f, 6)));
    }

    [Fact]
    public void CreditsAreRoundedToTheCent()
    {
        // serial-5: the card's float balance made a 1-credit line read 0.999999762.
        Assert.Contains("\"credits_earned\":1.0,", WireCheck.New(new TradedView(0, Oxite, buying: false, 1, 0.999999762f, 5)));
        Assert.Contains("\"credits_spent\":10.0,", WireCheck.New(new TradedView(0, Oxite, buying: true, 4, 10.000001f, 5)));
        Assert.Contains("\"credits\":1.2,",
            WireCheck.New(new NotTradedView(0, Oxite, 1, 1.20000076f, ApiErrors.Refused("trade_failed", "no"))));
        TradeView trade = new TradeView(new ThingId(5), "Trader", dryRun: false, new ThingId(9), 16.2000008f,
            1.20000076f, new BatchBuilder(0).Build(), new List<DeliveredView>(), null);
        Assert.Contains("\"credits_before\":16.2,\"credits_after\":1.2,", WireCheck.New(trade));
    }

    [Fact]
    public void TradeShape()
    {
        BatchBuilder batch = new BatchBuilder(1);
        batch.Failed(new NotTradedView(0, null, 0, 0f, ApiErrors.Refused("not_sold", "no")));
        TradeView trade = new TradeView(
            new ThingId(5), "Trader", dryRun: true, new ThingId(9), 100f, 100f, batch.Build(),
            new List<DeliveredView>(), null);
        Assert.Equal(
            "{\"reference_id\":\"5\",\"name\":\"Trader\",\"dry_run\":true,\"credit_card_id\":\"9\","
            + "\"credits_before\":100.0,\"credits_after\":100.0,\"results\":[{\"index\":0,\"ok\":false,\"name\":null,"
            + "\"prefab_name\":null,\"quantity\":0,\"credits\":0.0,"
            + "\"error\":{\"code\":\"not_sold\",\"message\":\"no\"}}],"
            + "\"count\":1,\"success_count\":0,\"error_count\":1,\"delivered\":[],\"gas_atmosphere_id\":null}",
            WireCheck.New(trade));
    }
}
