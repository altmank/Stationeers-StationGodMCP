#nullable enable

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
    internal static JObject Routed(string route, object view)
    {
        JObject reply = new JObject { ["route"] = route };
        foreach (JProperty key in JObject.FromObject(view, ApiJson.Serializer).Properties())
        {
            reply[key.Name] = key.Value;
        }

        return reply;
    }

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
        Report.Of(targets, new JObject { ["route"] = ItemMoveRoutes.Slot }, problems);

    /// <summary>
    /// A deposit's report (into a vault or a silo): every thing it would store, [id, name, held -> left in its source],
    /// and every refused one a problem; the body is the reply without items.
    /// </summary>
    internal static ReportReply DepositReport(JObject reply)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (JObject row in Rows(reply))
        {
            if (Problem(row) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            double quantity = (double?)row["quantity"] ?? 0.0;
            double left = (double?)row["left_in_source"] ?? 0.0;
            targets.Add(new ReportTarget((string?)row["reference_id"], null, (string?)row["display_name"],
                new JValue(quantity + left), new JValue(left), StoreMoveMark));
        }

        return Report.Of(targets, Without(reply, ItemsKey), problems);
    }

    /// <summary>
    /// A move from vault to vault's report: every line it would move, on the source vault [vault, "vault (line)",
    /// before -> after], and every refused line a problem; the body is the reply without items.
    /// </summary>
    internal static ReportReply TransferReport(JObject reply)
    {
        string? vault = (string?)reply["from_vault"]?["reference_id"];
        string? vaultName = (string?)reply["from_vault"]?["display_name"];
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (JObject row in Rows(reply))
        {
            if (Problem(row) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            string line = (string?)row["display_name"] ?? (string?)row["prefab_name"] ?? (string?)row["reagent"] ?? "?";
            targets.Add(new ReportTarget(vault, null, $"{vaultName} ({line})", row["from"]?["before"],
                row["from"]?["after"], StoreMoveMark));
        }

        return Report.Of(targets, Without(reply, ItemsKey), problems);
    }

    /// <summary>
    /// A move out of a store's report: the store it takes from, [store, "store (thing)", stock before -> after]; the
    /// body is the whole reply (where the items would go, and the stock).
    /// </summary>
    internal static ReportReply TakeReport(JObject reply, string storeKey)
    {
        JToken? stock = reply["stock"] is JArray lines && lines.Count > 0 ? lines[0] : reply["stock"];
        string thing = (string?)stock?["display_name"] ?? (string?)stock?["prefab_name"] ?? (string?)stock?["reagent"] ??
                       "?";
        ReportTarget target = new ReportTarget((string?)reply[storeKey]?["reference_id"], null,
            $"{(string?)reply[storeKey]?["display_name"]} ({thing})", stock?["before"], stock?["after"],
            StoreMoveMark);
        return Report.Of(new[] { target }, reply, new List<ReportProblem>());
    }

    // "<holder id> slot <n>", or "world" for an item lying loose.
    private static JToken Where(SlotRefView? slot) =>
        new JValue(slot == null ? "world" : $"{slot.Id} slot {slot.Slot}");

    private static List<JObject> Rows(JObject reply)
    {
        List<JObject> rows = new List<JObject>();
        if (reply[ItemsKey]?[ResultsKey] is JArray results)
        {
            foreach (JToken row in results)
            {
                if (row is JObject entry)
                {
                    rows.Add(entry);
                }
            }
        }

        return rows;
    }

    private static ReportProblem? Problem(JObject row) =>
        row["error"] is JObject error
            ? new ReportProblem((int?)row["index"], (string?)row["reference_id"],
                new VerbError((string?)error["code"] ?? VerbError.InvalidArgument, (string?)error["message"] ?? "",
                    error["data"]))
            : null;

    private static JObject Without(JObject reply, string key)
    {
        JObject body = (JObject)reply.DeepClone();
        body.Remove(key);
        return body;
    }
}
