#nullable enable

using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// move item's replies (catalogue move.json, item): route first, then the route's own keys. One slot move by id answers
/// ItemMoved (index 0, ok true); a list or moves answers BatchResult rows; a store move answers its store's reply. A
/// report (Report.Of) has the reply's keys without its rows, then problems, report_only and targets: each slot move,
/// each thing deposited, each line transferred, or the store a move out of it takes from.
/// </summary>
internal static class ItemMoveReplies
{
    internal const string StoreMoveMark = "[cannot undo: store move]";
    internal const string UndoMark = "[undo]";
    internal const string ForceMark = "[cannot undo: force]";
    private const string ItemsKey = "items";
    private const string ResultsKey = "results";

    /// <summary>{route, ...the view's keys}.</summary>
    internal static ReplyObject Routed(string route, object view) => new ReplyObject(1).Add("route", route).Spread(view);

    /// <summary>
    /// A slot move's report target: [item, name, where it is -> where it goes]. A merge cannot be undone; force is a
    /// cheat that cannot be either.
    /// </summary>
    internal static ReportTarget SlotTarget(ThingId item, string name, SlotRefView? from, SlotRefView to,
        ThingId? mergedInto, bool force) =>
        new ReportTarget(item.ToString(), null, name, Where(from), Where(to),
            force ? ForceMark : mergedInto is { } into ? $"[cannot undo: merged into {into}]" : UndoMark);

    /// <summary>A slot move's report: {route}, then its targets, and a refused move as a problem with its index.</summary>
    internal static ReportReply SlotReport(IReadOnlyList<ReportTarget> targets, IReadOnlyList<ReportProblem> problems) =>
        Report.Of(targets, new ReplyObject(1).Add("route", ItemMoveRoutes.Slot), problems);

    /// <summary>
    /// A deposit's report (into a vault or a silo): every thing it would store, [id, name, held -> left in its source],
    /// and every refused one a problem; the body is the reply without items.
    /// </summary>
    internal static ReportReply DepositReport(ReplyObject reply)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (object row in Rows(reply))
        {
            if (Problem(row) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            double quantity = Number(row, "quantity");
            double left = Number(row, "left_in_source");
            targets.Add(new ReportTarget(Text(row, "reference_id"), null, Text(row, "display_name"), quantity + left,
                left, StoreMoveMark));
        }

        return Report.Of(targets, Without(reply, ItemsKey), problems);
    }

    /// <summary>
    /// A move from vault to vault's report: every line it would move, on the source vault [vault, "vault (line)",
    /// before -> after], and every refused line a problem; the body is the reply without items.
    /// </summary>
    internal static ReportReply TransferReport(ReplyObject reply)
    {
        object? from = Get(reply, "from_vault");
        string? vault = Text(from, "reference_id");
        string? vaultName = Text(from, "display_name");
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (object row in Rows(reply))
        {
            if (Problem(row) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            string line = Text(row, "display_name") ?? Text(row, "prefab_name") ?? Text(row, "reagent") ?? "?";
            object? side = Get(row, "from");
            targets.Add(new ReportTarget(vault, null, $"{vaultName} ({line})", Get(side, "before"), Get(side, "after"),
                StoreMoveMark));
        }

        return Report.Of(targets, Without(reply, ItemsKey), problems);
    }

    /// <summary>
    /// A move out of a store's report: the store it takes from, [store, "store (thing)", stock before -> after]; the
    /// body is the whole reply (where the items would go, and the stock).
    /// </summary>
    internal static ReportReply TakeReport(ReplyObject reply, string storeKey)
    {
        object? stock = Get(reply, "stock");
        if (stock is IList { Count: > 0 } lines)
        {
            stock = lines[0];
        }

        string thing = Text(stock, "display_name") ?? Text(stock, "prefab_name") ?? Text(stock, "reagent") ?? "?";
        object? store = Get(reply, storeKey);
        ReportTarget target = new ReportTarget(Text(store, "reference_id"), null,
            $"{Text(store, "display_name")} ({thing})", Get(stock, "before"), Get(stock, "after"), StoreMoveMark);
        return Report.Of(new[] { target }, reply, new List<ReportProblem>());
    }

    // "<holder id> slot <n>", or "world" for an item lying loose.
    private static string Where(SlotRefView? slot) => slot == null ? "world" : $"{slot.Id} slot {slot.Slot}";

    // The rows of the reply's items (its results), as the views they are.
    private static List<object> Rows(ReplyObject reply)
    {
        List<object> rows = new List<object>();
        if (Get(Get(reply, ItemsKey), ResultsKey) is IEnumerable results)
        {
            foreach (object? row in results)
            {
                if (row != null)
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    private static ReportProblem? Problem(object row) =>
        Get(row, "error") is ErrorView error
            ? new ReportProblem(Get(row, "index") as int?, Text(row, "reference_id"),
                new VerbError(error.Code, error.Message,
                    error.Data == null ? null : JToken.FromObject(error.Data, ApiJson.Serializer)))
            : null;

    private static ReplyObject Without(ReplyObject reply, string key) => new ReplyObject(1).Spread(reply, key);

    private static object? Get(object? view, string key) => ViewKeys.Get(ApiJson.Serializer, view, key);

    private static string? Text(object? view, string key) => Get(view, key)?.ToString();

    private static double Number(object? view, string key) => Get(view, key) switch
    {
        double number => number,
        float number => number,
        int number => number,
        long number => number,
        _ => 0.0
    };
}
