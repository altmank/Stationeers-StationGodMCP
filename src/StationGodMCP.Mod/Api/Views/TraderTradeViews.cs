#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>trader_buy and trader_sell: the trade as a whole, and each line of it.</summary>
internal sealed class TradeView
{
    internal TradeView(ThingId referenceId, string? name, bool dryRun, ThingId creditCardId, float creditsBefore,
        float creditsAfter, BatchResultView items, List<DeliveredView> delivered, ThingId? gasAtmosphereId)
    {
        ReferenceId = referenceId;
        Name = name;
        DryRun = dryRun;
        CreditCardId = creditCardId;
        CreditsBefore = Credits.Round(creditsBefore);
        CreditsAfter = Credits.Round(creditsAfter);
        Results = items.Results;
        Count = items.Count;
        SuccessCount = items.SuccessCount;
        ErrorCount = items.ErrorCount;
        Delivered = delivered;
        GasAtmosphereId = gasAtmosphereId;
    }

    /// <summary>The trader contact.</summary>
    public ThingId ReferenceId { get; }

    public string? Name { get; }

    public bool DryRun { get; }

    /// <summary>The card paid from or into (CreditCard.Currency).</summary>
    public ThingId CreditCardId { get; }

    public float CreditsBefore { get; }

    public float CreditsAfter { get; }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }

    /// <summary>Bought goods, each where the game put it; empty for a sale or a dry run.</summary>
    public List<DeliveredView> Delivered { get; }

    /// <summary>The pad network's atmosphere, where bought gas goes and sold gas comes from; null without.</summary>
    public ThingId? GasAtmosphereId { get; }

    /// <summary>
    /// A dry run's credits_after: the balance once every line that would go through has traded, less what they would
    /// spend and plus what they would earn; a line that would go through only in part (a purchase that runs out of
    /// room) counts for its part, a refused line for nothing.
    /// </summary>
    internal static float PredictedCredits(float creditsBefore, List<BatchItemView> results, bool buying)
    {
        float credits = creditsBefore;
        foreach (BatchItemView result in results)
        {
            credits += result switch
            {
                TradedView traded => (traded.CreditsEarned ?? 0f) - (traded.CreditsSpent ?? 0f),
                NotTradedView partial => buying ? -partial.Credits : partial.Credits,
                _ => 0f,
            };
        }

        return credits;
    }
}

/// <summary>One line of a trade that went through (or, for a dry run, would).</summary>
internal sealed class TradedView : BatchItemView
{
    internal TradedView(int index, TradeLine line, bool buying, int quantity, float credits, int limitAfter)
        : base(index, ok: true)
    {
        Name = line.Name;
        PrefabName = line.PrefabName;
        Gas = line.Gas;
        Quantity = quantity;
        CreditsEach = line.CreditsEach;
        CreditsSpent = buying ? Credits.Round(credits) : (float?)null;
        CreditsEarned = buying ? (float?)null : Credits.Round(credits);
        StockAfter = buying ? limitAfter : (int?)null;
        WantedAfter = buying ? (int?)null : limitAfter;
    }

    public string? Name { get; }

    public string? PrefabName { get; }

    public bool Gas { get; }

    public int Quantity { get; }

    public float CreditsEach { get; }

    /// <summary>Credits paid; trader_buy only.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? CreditsSpent { get; }

    /// <summary>Credits received; trader_sell only.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public float? CreditsEarned { get; }

    /// <summary>The trader's stock after the purchase; trader_buy only.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? StockAfter { get; }

    /// <summary>How many more the trader wants after the sale; trader_sell only.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? WantedAfter { get; }
}

/// <summary>A trade line that was refused, or went through only in part (quantity and credits say how far).</summary>
internal sealed class NotTradedView : BatchItemView
{
    internal NotTradedView(int index, TradeLine? line, int quantity, float credits, ApiException error)
        : base(index, ok: false)
    {
        Name = line?.Name;
        PrefabName = line?.PrefabName;
        Quantity = quantity;
        Credits = StationGodMCP.Api.Views.Credits.Round(credits);
        Error = new ErrorView(error.Code, error.Message);
    }

    public string? Name { get; }

    public string? PrefabName { get; }

    /// <summary>How many went through before the game stopped (0 when refused up front).</summary>
    public int Quantity { get; }

    /// <summary>Credits paid or received for those.</summary>
    public float Credits { get; }

    public ErrorView Error { get; }
}

/// <summary>
/// Credits as the reply gives them: to the cent. The game keeps CreditCard.Currency in a float, so a difference of
/// two balances carries noise (0.999999762 for a 1-credit line).
/// </summary>
internal static class Credits
{
    internal static float Round(float credits) => (float)Math.Round(credits, 2, MidpointRounding.AwayFromZero);
}

/// <summary>What one trade line is: the trader's entry, its unit price to this player, and whether it is gas.</summary>
internal sealed class TradeLine
{
    internal TradeLine(string? name, string? prefabName, bool gas, float creditsEach)
    {
        Name = name;
        PrefabName = prefabName;
        Gas = gas;
        CreditsEach = creditsEach;
    }

    internal string? Name { get; }

    internal string? PrefabName { get; }

    internal bool Gas { get; }

    internal float CreditsEach { get; }
}

/// <summary>A bought item where it landed: the holder, the slot, and the item now in it.</summary>
internal sealed class DeliveredView
{
    internal DeliveredView(ThingId referenceId, int slot, ThingView item, float quantity)
    {
        ReferenceId = referenceId;
        Slot = slot;
        Item = item;
        Quantity = quantity;
    }

    /// <summary>The vending machine, or the player, whose slot took it.</summary>
    public ThingId ReferenceId { get; }

    public int Slot { get; }

    public ThingView Item { get; }

    public float Quantity { get; }
}
