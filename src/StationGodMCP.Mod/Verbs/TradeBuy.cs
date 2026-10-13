#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game.Trading;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// trade buy (catalogue trade.json): buy from a landed trader exactly as the trade window's Buy button does on the
/// host (TraderCanvas.BuyItem, which calls TradeDataHelper.BuyItem). Writes; host only. The per-id flow and the
/// replies are TradeRuns'; one id answers the trade itself, a list one row per id.
///
/// The game's buy (CODE, TraderUI.TradeDataHelper): the window opens only while the pad's trader is ready and trading
/// with this contact (ITraderDestination.IsTraderReady, CurrentTradingContact) on a pad that is on and without error.
/// TradeDataHelper.BuyItem refuses more than SellDataInstance.Stock, no card, or a cost above CreditCard.Currency.
/// Goods go into the first empty tradable slot (ITradableInventory.IsSlotTradable) of the vending machines on the
/// pad's data network (LandingPadNetwork.GetVendorsOnNetwork), then of the card holder's inventory, made one by one
/// straight into the slot (TradeDataHelper.Make, OnServer.MoveToSlot) and stacked up to their maximum; it charges
/// only for what it delivered and says the trade was incomplete when it ran out of room. Gas goes into the pad
/// network's atmosphere (AtmosphericEventInstance.CreateAdd). The price is TransactionData.GetCost per unit, divided
/// by DifficultySetting.RespawnStressTradePenalty while the player has respawn stress (TradeItem.SetTradeAmount).
/// The game keeps no trader currency. The unused RequestTradeMessage path is not involved.
/// </summary>
internal static class TradeBuy
{
    internal static VerbOutcome Run(TradeBuyOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        TradeOrder? order = TradeOrder.Of(options, problems);
        return order != null
            ? TradeRuns.Run(TradeSide.Buying, order)
            : new VerbOutcome.Refused(OptionProblem.Refusal(TradeBuyOptions.Spec.Label, problems));
    }
}
