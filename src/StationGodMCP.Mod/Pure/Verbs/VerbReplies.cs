#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>What a verb handler answers: a reply object, or a refusal in verb words.</summary>
internal abstract record VerbOutcome
{
    private VerbOutcome()
    {
    }

    internal sealed record Answered(JObject Reply) : VerbOutcome;

    internal sealed record Refused(VerbError Error) : VerbOutcome;
}

/// <summary>
/// The per-id rows (PLAN 2.0; SPEC-NOTES 14): one row per id, in the order given, {index, ok, reference_id, payload}
/// or {index, ok: false, reference_id, error}, and the counts count, success_count and error_count. A payload's keys go
/// into its row.
/// </summary>
internal sealed class Rows
{
    private readonly JArray _rows = new JArray();
    private int _succeeded;

    internal int Count => _rows.Count;

    internal void Succeeded(string? referenceId, JObject payload)
    {
        JObject row = Start(referenceId, true);
        foreach (JProperty key in payload.Properties())
        {
            if (key.Name is not ("index" or "ok" or "reference_id"))
            {
                row[key.Name] = key.Value.DeepClone();
            }
        }

        _rows.Add(row);
        _succeeded++;
    }

    internal void Failed(string? referenceId, VerbError error)
    {
        JObject row = Start(referenceId, false);
        row["error"] = error.ToJson();
        _rows.Add(row);
    }

    /// <summary>{...top, results, count, success_count, error_count}: top's keys (gateway_id) first.</summary>
    internal JObject Reply(JObject? top = null)
    {
        JObject reply = top != null ? (JObject)top.DeepClone() : new JObject();
        reply["results"] = _rows.DeepClone();
        reply["count"] = _rows.Count;
        reply["success_count"] = _succeeded;
        reply["error_count"] = _rows.Count - _succeeded;
        return reply;
    }

    /// <summary>
    /// The answer for the ids a call gave (D13, direct face): a list answers the rows; one id answers its row's
    /// payload as the what's single shape, or its error as the call's refusal.
    /// </summary>
    internal VerbOutcome For(PerIdList ids, JObject? top = null)
    {
        if (ids.AnswersRows || _rows.Count != 1)
        {
            return new VerbOutcome.Answered(Reply(top));
        }

        JObject row = (JObject)_rows[0];
        if (row["error"] is JObject error)
        {
            return new VerbOutcome.Refused(new VerbError((string)error["code"]!, (string)error["message"]!,
                error["data"]));
        }

        JObject single = top != null ? (JObject)top.DeepClone() : new JObject();
        foreach (JProperty key in row.Properties())
        {
            if (key.Name is not ("index" or "ok"))
            {
                single[key.Name] = key.Value.DeepClone();
            }
        }

        return new VerbOutcome.Answered(single);
    }

    private JObject Start(string? referenceId, bool ok) => new JObject
    {
        ["index"] = _rows.Count,
        ["ok"] = ok,
        ["reference_id"] = referenceId
    };
}

/// <summary>
/// A list reply (PLAN 2.0, shared paging): the what's main list as one page, then count (entries shown), total,
/// offset, limit and has_more; the what's other keys follow.
/// </summary>
internal static class ListReply
{
    internal static JObject Of<T>(string list, IReadOnlyList<T> entries, Paging paging, System.Func<T, JToken> entry,
        JObject? rest = null)
    {
        Page<T> page = paging.Of(entries);
        JArray shown = new JArray();
        foreach (T item in page.Entries)
        {
            shown.Add(entry(item));
        }

        JObject reply = new JObject
        {
            [list] = shown,
            ["count"] = shown.Count,
            ["total"] = page.Total,
            ["offset"] = page.Offset
        };
        if (page.Limit is { } limit)
        {
            reply["limit"] = limit;
        }

        reply["has_more"] = page.HasMore;
        if (rest != null)
        {
            foreach (JProperty key in rest.Properties())
            {
                reply[key.Name] = key.Value.DeepClone();
            }
        }

        return reply;
    }
}

/// <summary>
/// One report target (SPEC-NOTES 18): [id or cell, name, current -> new], with its undo mark, the guards lifted for it
/// (PASSED) and what drives it (driven_by: a chip or writer that may write it back).
/// </summary>
internal sealed record ReportTarget(
    string? Id,
    JToken? Cell,
    string? Name,
    JToken? Current,
    JToken? New,
    string Mark,
    IReadOnlyList<string>? Passed = null,
    JToken? DrivenBy = null)
{
    internal JObject ToJson()
    {
        JObject row = new JObject();
        if (Id != null)
        {
            row["id"] = Id;
        }

        if (Cell != null)
        {
            row["cell"] = Cell.DeepClone();
        }

        row["name"] = Name;
        row["current"] = Current?.DeepClone();
        row["new"] = New?.DeepClone();
        row["undo"] = Mark;
        if (Passed is { Count: > 0 })
        {
            row["passed"] = new JArray(Passed);
        }

        if (DrivenBy != null)
        {
            row["driven_by"] = DrivenBy.DeepClone();
        }

        row["text"] = Text();
        return row;
    }

    /// <summary>"[id, name, current -> new] mark", then PASSED: the lifted guards.</summary>
    internal string Text()
    {
        StringBuilder text = new StringBuilder();
        text.Append('[').Append(Id ?? Cell?.ToString(Newtonsoft.Json.Formatting.None) ?? "?").Append(", ")
            .Append(Name ?? "?").Append(", ").Append(Shown(Current)).Append(" -> ").Append(Shown(New)).Append("] ")
            .Append(Mark);
        if (Passed is { Count: > 0 })
        {
            text.Append(" PASSED: ").Append(string.Join(", ", Passed));
        }

        return text.ToString();
    }

    private static string Shown(JToken? value) => value switch
    {
        null => "?",
        { Type: JTokenType.Null } => "?",
        { Type: JTokenType.String } => (string)value!,
        _ => value.ToString(Newtonsoft.Json.Formatting.None)
    };
}

/// <summary>
/// The one report shape (PLAN 2.0, direct write rule; SPEC-NOTES 18): what a write would do, nothing changed. The
/// what's own report keys, then report_only: true and targets, every target listed whole. rows (ReportConfirm.Rows)
/// cuts the what's own lists only, through Shown.
/// </summary>
internal static class Report
{
    internal static JObject Of(IReadOnlyList<ReportTarget> targets, JObject? body = null)
    {
        JObject report = body != null ? (JObject)body.DeepClone() : new JObject();
        report["report_only"] = true;
        JArray rows = new JArray();
        foreach (ReportTarget target in targets)
        {
            rows.Add(target.ToJson());
        }

        report["targets"] = rows;
        return report;
    }

    /// <summary>The first rows entries of a report list (all when rows is left out; none for 0).</summary>
    internal static JArray Shown(IReadOnlyList<JToken> entries, ReportConfirm report)
    {
        JArray shown = new JArray();
        int most = report.Rows ?? int.MaxValue;
        for (int index = 0; index < entries.Count && index < most; index++)
        {
            shown.Add(entries[index].DeepClone());
        }

        return shown;
    }
}
