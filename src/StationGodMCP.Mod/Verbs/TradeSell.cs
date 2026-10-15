#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game.Trading;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// trade sell (catalogue trade.json): sell to a landed trader exactly as the trade window's Sell button does on the
/// host (TraderCanvas.SellItem, TradeDataHelper.SellItem). The mirror of trade buy: the trader must still want the
/// item (BuyDataInstance.Required); the goods are taken from the pad network's vending machines and then the card
/// holder's inventory (TradeDataHelper.HandleSellItem destroys whole stacks and trims the last); gas is taken from the
/// pad network's atmosphere. The price is BuyData's GetCost per unit, times the respawn stress penalty. The card must
/// be held by a player or a vending machine (SellCard), and lines of one entry are sold together (SellRun). The
/// per-id flow and the replies are TradeRuns'; one id answers the trade itself, a list one row per id.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class TradeSell
{
    internal static VerbOutcome Run(TradeSellOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        TradeOrder? order = TradeOrder.Of(options, problems);
        return order != null
            ? TradeRuns.Run(TradeSide.Selling, order)
            : new VerbOutcome.Refused(OptionProblem.Refusal(TradeSellOptions.Spec.Label, problems));
    }
}
