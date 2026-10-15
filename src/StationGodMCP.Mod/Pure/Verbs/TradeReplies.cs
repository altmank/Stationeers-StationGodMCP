#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// One id's trade outcome: the trade it made (or, until confirm, would make), the card that pays and the contact it
/// trades with, or the refusal that stops that id alone.
/// </summary>
internal abstract class TradeResult
{
    private TradeResult()
    {
    }

    internal sealed class Traded : TradeResult
    {
        internal Traded(string id, string? cardName, TradeView view)
        {
            Id = id;
            CardName = cardName;
            View = view;
        }

        /// <summary>The contact's reference id, as its row and its report targets name it.</summary>
        public string Id { get; }

        /// <summary>The card's displayed name; null when the card is unnamed.</summary>
        public string? CardName { get; }

        /// <summary>The trade, whose own keys are the what's reply.</summary>
        public TradeView View { get; }
    }

    /// <summary>Refused: the id as the call gave it, and the error the acting call would raise for it.</summary>
    internal sealed class Failed : TradeResult
    {
        internal Failed(int index, string id, VerbError error)
        {
            Index = index;
            Id = id;
            Error = error;
        }

        public int Index { get; }

        public string Id { get; }

        public VerbError Error { get; }
    }
}

/// <summary>
/// trade buy and trade sell's replies: confirmed, one row per id in the order given (the shared Rows writer), each row
/// that id's trade as its payload and a refused id its error - one id answers the single shape, a list the rows shape.
/// Until confirm, the one report (Report.Of): every traded id's contact and card a target [id, name, current -> new]
/// with the what's undo mark, the trade as the body (one list's trades as the trades list) and every refused id a
/// problem about its index. Both read the same per-id trades, so a confirmed reply and its report cannot drift.
/// </summary>
internal static class TradeReplies
{
    /// <summary>The call trades: the rows, or one id's own trade as the what's single shape.</summary>
    internal static VerbOutcome Acted(PerIdList ids, IReadOnlyList<TradeResult> results)
    {
        Rows rows = new Rows();
        foreach (TradeResult result in results)
        {
            switch (result)
            {
                case TradeResult.Traded traded:
                    rows.Succeeded(traded.Id, traded.View);
                    break;
                case TradeResult.Failed failed:
                    rows.Failed(failed.Id, failed.Error);
                    break;
            }
        }

        return rows.For(ids);
    }

    /// <summary>
    /// The report: the targets, the trade as the body, and the problems. asList is true when the call gave a list of
    /// ids: the body is then every trade in trades, one id's own trade otherwise.
    /// </summary>
    internal static ReportReply Reported(IReadOnlyList<TradeResult> results, bool asList, string mark)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        List<TradeView> trades = new List<TradeView>();
        TradeView? single = null;
        foreach (TradeResult result in results)
        {
            switch (result)
            {
                case TradeResult.Traded traded:
                    TradeView view = traded.View;
                    targets.Add(new ReportTarget(traded.Id, null, view.Name, null, null, mark));
                    targets.Add(new ReportTarget(view.CreditCardId.ToString(), null, traded.CardName,
                        view.CreditsBefore, view.CreditsAfter, mark));
                    if (asList)
                    {
                        trades.Add(view);
                    }
                    else
                    {
                        single = view;
                    }

                    break;
                case TradeResult.Failed failed:
                    problems.Add(new ReportProblem(failed.Index, failed.Id, failed.Error));
                    break;
            }
        }

        object? body = asList ? new ReplyObject(1).Add("trades", trades) : single;
        return Report.Of(targets, body, problems);
    }
}
