#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Trading;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// trade buy and trade sell (catalogue trade.json): one trade per id, in the order given, each id on its own. The
/// trading call trades as the trade window does (TradeSession: the contact found, landed at a pad on and without
/// error, the card, then each line in order); a report runs the same checks and prices the same lines, changing
/// nothing, and lists each refusal beside the id it is about. One id answers the what's single shape, a list one row
/// per id; a contact that is not landed is that id's error and the others still trade.
/// </summary>
internal static class TradeRuns
{
    internal static VerbOutcome Run(TradeSide side, TradeOrder order)
    {
        List<TradeResult> results = new List<TradeResult>(order.Ids.Ids.Count);
        for (int index = 0; index < order.Ids.Ids.Count; index++)
        {
            results.Add(Trade(side, order, index));
        }

        return order.Acts
            ? TradeReplies.Acted(order.Ids, results)
            : new VerbOutcome.Answered(TradeReplies.Reported(results, order.Ids.AnswersRows, order.Mark));
    }

    // One id's trade: the checks the trading call makes, then each line of it (a report only prices them), or the
    // refusal that stops this id alone.
    private static TradeResult Trade(TradeSide side, TradeOrder order, int index)
    {
        string id = order.Ids.Ids[index];
        try
        {
            TradeSession.RequireHost();
            TraderContact contact = TradeSession.RequireTradingContact(Contacts.IdOf(id));
            CreditCard card = TradeSession.RequireCard(TradeSession.CardId(order.Card));
            TradeView view = TradeSession.Run(Request(order, contact, card), side, contact, card);
            return new TradeResult.Traded(view.ReferenceId.ToString(), Text.Plain(card.DisplayName), view);
        }
        catch (ApiException refused)
        {
            return new TradeResult.Failed(index, id, VerbError.Of(refused));
        }
    }

    // The call's lines as the session reads them: the entry each line names, and how many units of it.
    private static TradeRequest Request(TradeOrder order, TraderContact contact, CreditCard card)
    {
        List<TradeItemRequest> items = new List<TradeItemRequest>(order.Items.Count);
        foreach (TradeItemLine line in order.Items)
        {
            items.Add(new TradeItemRequest(line.Prefab, line.Name, line.Quantity));
        }

        return new TradeRequest(new ThingId(contact.ReferenceId), new ThingId(card.ReferenceId), items, !order.Acts);
    }
}
