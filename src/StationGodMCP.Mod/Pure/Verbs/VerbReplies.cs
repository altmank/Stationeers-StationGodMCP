#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// What a verb handler answers: a reply object, a refusal in verb words, or work that goes on over frames and answers
/// once it is ready (Pending: its Result is the call's Answered or Refused outcome).
/// </summary>
internal abstract class VerbOutcome
{
    private VerbOutcome()
    {
    }

    internal sealed class Answered : VerbOutcome
    {
        internal Answered(JObject reply)
        {
            Reply = reply;
        }

        public JObject Reply { get; }
    }

    internal sealed class Refused : VerbOutcome
    {
        internal Refused(VerbError error)
        {
            Error = error;
        }

        public VerbError Error { get; }
    }

    /// <summary>
    /// The call goes on over later frames: Work is stepped in the scheduler's slice lane, and its Result is the call's
    /// answer (an Answered or Refused outcome, or the reply object itself).
    /// </summary>
    internal sealed class Pending : VerbOutcome
    {
        internal Pending(IPendingResult work)
        {
            Work = work;
        }

        public IPendingResult Work { get; }
    }

    /// <summary>
    /// An outcome taken to its end within this call: a pending one's work stepped until it ends, for a caller that runs
    /// a call itself and needs its answer now (undo's replay).
    /// </summary>
    internal static VerbOutcome Settled(VerbOutcome outcome)
    {
        if (!(outcome is Pending pending))
        {
            return outcome;
        }

        while (!pending.Work.Step(double.PositiveInfinity))
        {
        }

        return Of(pending.Work.Result());
    }

    /// <summary>A pending result's outcome: Answered or Refused as given, a reply object as Answered.</summary>
    internal static VerbOutcome Of(object? result) => result switch
    {
        Answered answered => answered,
        Refused refused => refused,
        JObject reply => new Answered(reply),
        _ => throw new System.InvalidOperationException("A pending verb result is an answer, a refusal or a reply object.")
    };
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

    /// <summary>
    /// One id's row from its read: the payload read gives, or, when read refuses (an ApiException), that refusal as the
    /// row's error.
    /// </summary>
    internal void Add(string referenceId, System.Func<JObject> read)
    {
        JObject payload;
        try
        {
            payload = read();
        }
        catch (ApiException refused)
        {
            Failed(referenceId, new VerbError(refused.Code, refused.Message));
            return;
        }

        Succeeded(referenceId, payload);
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
        JObject? rest = null) =>
        OfPage(list, paging.Of(entries), entry, rest);

    /// <summary>A page already cut (taken over frames, or from a walk's cursor), in the same shape.</summary>
    internal static JObject OfPage<T>(string list, Page<T> page, System.Func<T, JToken> entry, JObject? rest = null)
    {
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
/// The declared-shape writer (D-A2-4): a reply the catalogue declares key by key that is no ListReply page (a whole
/// list with its count, or a what's own keys), written in the order given, each value as the reply views serialise.
/// </summary>
internal static class ReplyFields
{
    internal static JObject Of(params (string Key, object? Value)[] fields)
    {
        Newtonsoft.Json.JsonSerializer serializer = ApiJson.Fresh();
        JObject reply = new JObject();
        foreach ((string key, object? value) in fields)
        {
            reply[key] = Value(value, serializer);
        }

        return reply;
    }

    /// <summary>One value as a reply holds it: null as JSON null, a token as given, anything else serialised.</summary>
    internal static JToken Value(object? value, Newtonsoft.Json.JsonSerializer serializer) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token.DeepClone(),
        _ => JToken.FromObject(value, serializer)
    };
}

/// <summary>
/// One report target (SPEC-NOTES 18): [id or cell, name, current -> new], with its undo mark, the guards lifted for it
/// (PASSED) and what drives it (driven_by: a chip or writer that may write it back).
/// </summary>
internal sealed class ReportTarget
{
    internal ReportTarget(string? id, JToken? cell, string? name, JToken? current, JToken? @new, string mark,
        IReadOnlyList<string>? passed = null, JToken? drivenBy = null)
    {
        Id = id;
        Cell = cell;
        Name = name;
        Current = current;
        New = @new;
        Mark = mark;
        Passed = passed;
        DrivenBy = drivenBy;
    }

    public string? Id { get; }

    public JToken? Cell { get; }

    public string? Name { get; }

    public JToken? Current { get; }

    public JToken? New { get; }

    public string Mark { get; }

    public IReadOnlyList<string>? Passed { get; }

    public JToken? DrivenBy { get; }

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
        { Type: JTokenType.Float } => ((double)value).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(Newtonsoft.Json.Formatting.None)
    };
}

/// <summary>
/// One refusal a report lists (PLAN 2.0, direct write rule): the acting call would be refused so, for the entry at
/// index (left out for the whole call) on the thing reference_id names (left out when none is known). A report that
/// lists a problem still answers.
/// </summary>
internal sealed class ReportProblem
{
    internal ReportProblem(int? index, string? referenceId, VerbError error)
    {
        Index = index;
        ReferenceId = referenceId;
        Error = error;
    }

    public int? Index { get; }

    public string? ReferenceId { get; }

    public VerbError Error { get; }

    internal JObject ToJson()
    {
        JObject problem = new JObject();
        if (Index is { } index)
        {
            problem["index"] = index;
        }

        if (ReferenceId != null)
        {
            problem["reference_id"] = ReferenceId;
        }

        foreach (JProperty key in Error.ToJson().Properties())
        {
            problem[key.Name] = key.Value.DeepClone();
        }

        return problem;
    }
}

/// <summary>
/// The one report shape (PLAN 2.0, direct write rule; SPEC-NOTES 18): what a write would do, nothing changed. The
/// what's own report keys, then report_only: true and targets, every target listed whole. rows (ReportConfirm.Rows)
/// cuts the what's own lists only, through Shown. A report over SummaryBytes answers its summary form (DESIGN 3.3):
/// the what's own keys that are no list, every protected key in full, and each target as an [id, name] pair but those
/// with a lifted guard or a driver, which stay whole. A summary form still over MostCharacters is too large for the
/// client: report_too_large, with the target count and how to narrow it.
/// </summary>
internal static class Report
{
    /// <summary>The direct face's reply cap: a report larger than this answers its summary form.</summary>
    internal const int SummaryBytes = 12 * 1024;

    /// <summary>The client's limit: Claude Code's default 25,000 tokens, as 100,000 characters of JSON.</summary>
    internal const int MostCharacters = 100000;

    internal const int ClientTokens = 25000;

    internal const string TooLarge = "report_too_large";

    // The keys a report keeps whole, whatever its size (DESIGN 3.3, 3.4 rule 2).
    private static readonly string[] Protected = { "problems", "warnings", "will_burst", "gas_check", "passed" };

    /// <summary>
    /// The report: body's keys, then problems (what the acting call would refuse, each with its entry; left out when
    /// null), report_only: true and every target; its summary form when it is very large. Throws report_too_large
    /// when even the summary form is too large for the client.
    /// </summary>
    [Profiled]
    internal static JObject Of(IReadOnlyList<ReportTarget> targets, JObject? body = null,
        IReadOnlyList<ReportProblem>? problems = null)
    {
        JObject whole = Whole(targets, body, problems);
        if (Encoding.UTF8.GetByteCount(whole.ToString(Formatting.None)) <= SummaryBytes)
        {
            return whole;
        }

        JObject summary = Summary(whole, targets);
        return summary.ToString(Formatting.None).Length <= MostCharacters
            ? summary
            : throw new ApiException(TooLarge,
                $"The report lists {targets.Count} targets, more than the client takes even in its summary form; " +
                "nothing changed. Narrow the target: a filter on the find, a smaller ref, fewer spread lines.",
                new JObject
                {
                    ["targets"] = targets.Count,
                    ["limit_tokens"] = ClientTokens,
                    ["fix"] = "narrow the target: a filter on the find, a smaller ref, fewer spread lines"
                });
    }

    // The summary form: the body's keys that hold no list, the protected keys whole, report_only, and the targets as
    // [id, name] pairs, a target with a lifted guard or a driver kept whole.
    private static JObject Summary(JObject whole, IReadOnlyList<ReportTarget> targets)
    {
        JObject summary = new JObject();
        foreach (JProperty key in whole.Properties())
        {
            if (key.Name == "targets" || (key.Value is JArray && Array.IndexOf(Protected, key.Name) < 0))
            {
                continue;
            }

            summary[key.Name] = key.Value.DeepClone();
        }

        JArray pairs = new JArray();
        foreach (ReportTarget target in targets)
        {
            pairs.Add(target.Passed is { Count: > 0 } || target.DrivenBy != null
                ? target.ToJson()
                : new JArray(target.Id != null ? new JValue(target.Id) : target.Cell?.DeepClone(), target.Name));
        }

        summary["targets"] = pairs;
        return summary;
    }

    private static JObject Whole(IReadOnlyList<ReportTarget> targets, JObject? body,
        IReadOnlyList<ReportProblem>? problems)
    {
        JObject report = body != null ? (JObject)body.DeepClone() : new JObject();
        if (problems != null)
        {
            JArray listed = new JArray();
            foreach (ReportProblem problem in problems)
            {
                listed.Add(problem.ToJson());
            }

            report["problems"] = listed;
        }

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
