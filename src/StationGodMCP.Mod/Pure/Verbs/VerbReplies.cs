#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;

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

    /// <summary>An answer: a view or a reply object (ReplyObject), plain data the reply's writer writes once.</summary>
    internal sealed class Answered : VerbOutcome
    {
        internal Answered(object reply)
        {
            Reply = reply;
        }

        public object Reply { get; }
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
    /// answer (an Answered or Refused outcome, or the reply itself).
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
    /// A call a caller runs itself, as a body of the caller's own work (undo's replay; A3): run starts the call under a
    /// frame cap that lets its handler take one step, and each later step of its pending work is one step of this body,
    /// so the caller's Work spreads it over frames. reply gets the answer; a refusal is thrown, as any call's is.
    /// </summary>
    internal static IEnumerable<object?> Replied(Func<VerbOutcome> run, Answer<object> reply)
    {
        VerbOutcome outcome;
        using (CurrentFrame.Capped(double.NegativeInfinity))
        {
            outcome = run();
        }

        while (outcome is Pending pending)
        {
            while (!pending.Work.Step(double.NegativeInfinity))
            {
                yield return null;
            }

            outcome = Of(pending.Work.Result());
        }

        reply.Value = outcome switch
        {
            Answered answered => answered.Reply,
            Refused refused => throw ApiErrors.Refused(refused.Error.Code, refused.Error.Message),
            _ => throw new InvalidOperationException("A call's outcome is answered or refused.")
        };
    }

    /// <summary>
    /// A result's outcome: Answered, Refused or Pending as given (a pending result may go on with more work: a report
    /// described over frames), any other reply as Answered.
    /// </summary>
    internal static VerbOutcome Of(object? result) => result switch
    {
        VerbOutcome outcome => outcome,
        null => throw new System.InvalidOperationException("A pending verb result is an answer, a refusal or a reply."),
        _ => new Answered(result)
    };
}

/// <summary>
/// The per-id rows (PLAN 2.0; SPEC-NOTES 14): one row per id, in the order given, {index, ok, reference_id, payload}
/// or {index, ok: false, reference_id, error}, and the counts count, success_count and error_count. A payload's keys go
/// into its row (a spread: nothing is copied).
/// </summary>
internal sealed class Rows
{
    private static readonly string[] RowKeys = { "index", "ok", "reference_id" };
    private static readonly string[] Counted = { "index", "ok" };

    private readonly List<ReplyObject> _rows = new List<ReplyObject>();
    private readonly List<VerbError?> _errors = new List<VerbError?>();
    private int _succeeded;

    internal int Count => _rows.Count;

    internal void Succeeded(string? referenceId, object payload)
    {
        _rows.Add(Start(referenceId, true).Spread(payload, RowKeys));
        _errors.Add(null);
        _succeeded++;
    }

    /// <summary>
    /// One id's row from its read: the payload read gives, or, when read refuses (an ApiException it answers, built and
    /// not thrown, or one it throws), that refusal as the row's error.
    /// </summary>
    internal void Add(string referenceId, System.Func<object> read)
    {
        object payload;
        try
        {
            payload = read();
        }
        catch (ApiException refused)
        {
            payload = refused;
        }

        if (payload is ApiException refusal)
        {
            Failed(referenceId, new VerbError(refusal.Code, refusal.Message));
        }
        else
        {
            Succeeded(referenceId, payload);
        }
    }

    internal void Failed(string? referenceId, VerbError error)
    {
        _rows.Add(Start(referenceId, false).Add("error", error.ToJson()));
        _errors.Add(error);
    }

    /// <summary>{...top, results, count, success_count, error_count}: top's keys (gateway_id) first.</summary>
    internal ReplyObject Reply(object? top = null) => new ReplyObject()
        .Spread(top)
        .Add("results", _rows)
        .Add("count", _rows.Count)
        .Add("success_count", _succeeded)
        .Add("error_count", _rows.Count - _succeeded);

    /// <summary>
    /// The answer for the ids a call gave (D13, direct face): a list answers the rows; one id answers its row's
    /// payload as the what's single shape, or its error as the call's refusal.
    /// </summary>
    internal VerbOutcome For(PerIdList ids, object? top = null)
    {
        if (ids.AnswersRows || _rows.Count != 1)
        {
            return new VerbOutcome.Answered(Reply(top));
        }

        return _errors[0] is { } error
            ? new VerbOutcome.Refused(error)
            : new VerbOutcome.Answered(new ReplyObject().Spread(top).Spread(_rows[0], Counted));
    }

    private ReplyObject Start(string? referenceId, bool ok) => new ReplyObject()
        .Add("index", _rows.Count)
        .Add("ok", ok)
        .Add("reference_id", referenceId);
}

/// <summary>
/// A list reply (PLAN 2.0, shared paging): the what's main list as one page, then count (entries shown), total,
/// offset, limit and has_more; the what's other keys follow.
/// </summary>
internal static class ListReply
{
    internal static ReplyObject Of<T>(string list, IReadOnlyList<T> entries, Paging paging, System.Func<T, object?> entry,
        object? rest = null) =>
        OfPage(list, paging.Of(entries), entry, rest);

    /// <summary>A page already cut (taken over frames, or from a walk's cursor), in the same shape.</summary>
    internal static ReplyObject OfPage<T>(string list, Page<T> page, System.Func<T, object?> entry, object? rest = null)
    {
        List<object?> shown = new List<object?>(page.Entries.Count);
        foreach (T item in page.Entries)
        {
            shown.Add(entry(item));
        }

        ReplyObject reply = new ReplyObject()
            .Add(list, shown)
            .Add("count", shown.Count)
            .Add("total", page.Total)
            .Add("offset", page.Offset);
        if (page.Limit is { } limit)
        {
            reply.Add("limit", limit);
        }

        return reply.Add("has_more", page.HasMore).Spread(rest);
    }

    /// <summary>A page whose entries are the views themselves.</summary>
    internal static ReplyObject OfPage<T>(string list, Page<T> page, object? rest = null) =>
        OfPage(list, page, static view => view, rest);
}

/// <summary>
/// The declared-shape writer (D-A2-4): a reply the catalogue declares key by key that is no ListReply page (a whole
/// list with its count, or a what's own keys), written in the order given, each value as the reply views serialise.
/// </summary>
internal static class ReplyFields
{
    internal static ReplyObject Of(params (string Key, object? Value)[] fields)
    {
        ReplyObject reply = new ReplyObject(fields.Length);
        foreach ((string key, object? value) in fields)
        {
            reply.Add(key, value);
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
/// (PASSED) and what drives it (driven_by: a chip or writer that may write it back). Its values are plain (a number, a
/// string, a view, a list); it is written by its converter, its text made as it is written.
/// </summary>
[JsonConverter(typeof(ReportTargetConverter))]
internal sealed class ReportTarget
{
    internal ReportTarget(string? id, object? cell, string? name, object? current, object? @new, string mark,
        IReadOnlyList<string>? passed = null, object? drivenBy = null)
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

    public object? Cell { get; }

    public string? Name { get; }

    public object? Current { get; }

    public object? New { get; }

    public string Mark { get; }

    public IReadOnlyList<string>? Passed { get; }

    public object? DrivenBy { get; }

    /// <summary>Kept whole in a report's summary form: a lifted guard or a driver.</summary>
    internal bool KeptWhole => Passed is { Count: > 0 } || DrivenBy != null;

    internal void WriteTo(JsonWriter writer, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        if (Id != null)
        {
            writer.WritePropertyName("id");
            writer.WriteValue(Id);
        }

        if (Cell != null)
        {
            writer.WritePropertyName("cell");
            serializer.Serialize(writer, Cell);
        }

        writer.WritePropertyName("name");
        writer.WriteValue(Name);
        writer.WritePropertyName("current");
        serializer.Serialize(writer, Current);
        writer.WritePropertyName("new");
        serializer.Serialize(writer, New);
        writer.WritePropertyName("undo");
        writer.WriteValue(Mark);
        if (Passed is { Count: > 0 })
        {
            writer.WritePropertyName("passed");
            serializer.Serialize(writer, Passed);
        }

        if (DrivenBy != null)
        {
            writer.WritePropertyName("driven_by");
            serializer.Serialize(writer, DrivenBy);
        }

        writer.WritePropertyName("text");
        writer.WriteValue(Text(serializer));
        writer.WriteEndObject();
    }

    /// <summary>The summary form's [id or cell, name] pair.</summary>
    internal void WritePair(JsonWriter writer, JsonSerializer serializer)
    {
        writer.WriteStartArray();
        if (Id != null)
        {
            writer.WriteValue(Id);
        }
        else
        {
            serializer.Serialize(writer, Cell);
        }

        writer.WriteValue(Name);
        writer.WriteEndArray();
    }

    /// <summary>"[id, name, current -> new] mark", then PASSED: the lifted guards.</summary>
    internal string Text(JsonSerializer serializer)
    {
        StringBuilder text = new StringBuilder();
        text.Append('[').Append(Id ?? (Cell != null ? Compact(Cell, serializer) : "?")).Append(", ")
            .Append(Name ?? "?").Append(", ").Append(Shown(Current, serializer)).Append(" -> ")
            .Append(Shown(New, serializer)).Append("] ").Append(Mark);
        if (Passed is { Count: > 0 })
        {
            text.Append(" PASSED: ").Append(string.Join(", ", Passed));
        }

        return text.ToString();
    }

    private static string Shown(object? value, JsonSerializer serializer) => value switch
    {
        null => "?",
        JToken { Type: JTokenType.Null } => "?",
        JToken { Type: JTokenType.String } token => (string)token!,
        JToken { Type: JTokenType.Float } token => ((double)token).ToString("R", CultureInfo.InvariantCulture),
        string given => given,
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => ((double)number).ToString("R", CultureInfo.InvariantCulture),
        decimal number => ((double)number).ToString("R", CultureInfo.InvariantCulture),
        _ => Compact(value, serializer)
    };

    private static string Compact(object value, JsonSerializer serializer)
    {
        StringWriter text = new StringWriter(CultureInfo.InvariantCulture);
        using (JsonTextWriter writer = new JsonTextWriter(text))
        {
            serializer.Serialize(writer, value);
        }

        return text.ToString();
    }
}

/// <summary>Writes a report target.</summary>
internal sealed class ReportTargetConverter : JsonConverter
{
    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) => objectType == typeof(ReportTarget);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        ((ReportTarget)value!).WriteTo(writer, serializer);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
        throw new NotSupportedException("A report target is only written.");
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

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Index { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ReferenceId { get; }

    [JsonIgnore]
    public VerbError Error { get; }

    public string Code => Error.Code;

    public string Message => Error.Message;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JToken? Data => Error.Data;
}

/// <summary>
/// A reply whose form is chosen when it is written (a report: whole, its summary form, or report_too_large): the
/// writer asks for the form before it writes the call's reply, and a refusal it throws answers the call.
/// </summary>
internal interface IReplyForm
{
    /// <summary>The reply to write; throws ApiException when it is to be refused.</summary>
    object Form(JsonSerializer serializer);
}

/// <summary>
/// The one report shape (PLAN 2.0, direct write rule; SPEC-NOTES 18): what a write would do, nothing changed. The
/// what's own report keys, then report_only: true and targets, every target listed whole. A report over SummaryBytes
/// answers its summary form (DESIGN 3.3): the what's own keys that are no list, every protected key in full, and each
/// target as an [id, name] pair but those with a lifted guard or a driver, which stay whole. A summary form still over
/// MostCharacters is too large for the client: report_too_large, with the target count and how to narrow it. The form
/// is chosen where the reply is written (ReportReply), off the game's thread.
/// </summary>
internal static class Report
{
    /// <summary>The direct face's reply cap: a report larger than this answers its summary form.</summary>
    internal const int SummaryBytes = 12 * 1024;

    /// <summary>The client's limit: Claude Code's default 25,000 tokens, as 100,000 characters of JSON.</summary>
    internal const int MostCharacters = 100000;

    internal const int ClientTokens = 25000;

    internal const string TooLarge = "report_too_large";

    /// <summary>
    /// The report: body's keys, then problems (what the acting call would refuse, each with its entry; left out when
    /// null), report_only: true and every target; its summary form when it is very large, chosen when it is written.
    /// </summary>
    internal static ReportReply Of(IReadOnlyList<ReportTarget> targets, object? body = null,
        IReadOnlyList<ReportProblem>? problems = null) => new ReportReply(targets, body, problems);
}

/// <summary>A report (Report.Of): its parts as given; its form chosen when it is written.</summary>
[JsonConverter(typeof(ReplyFormConverter))]
internal sealed class ReportReply : IReplyForm
{
    // The keys a report keeps whole, whatever its size (DESIGN 3.3, 3.4 rule 2).
    private static readonly string[] Protected = { "problems", "warnings", "will_burst", "gas_check", "passed" };

    internal ReportReply(IReadOnlyList<ReportTarget> targets, object? body, IReadOnlyList<ReportProblem>? problems)
    {
        Targets = targets;
        Body = body;
        Problems = problems;
    }

    internal IReadOnlyList<ReportTarget> Targets { get; }

    internal object? Body { get; }

    internal IReadOnlyList<ReportProblem>? Problems { get; }

    /// <summary>The whole report: body's keys, problems, report_only and every target.</summary>
    internal ReplyObject Whole()
    {
        ReplyObject report = new ReplyObject().Spread(Body);
        if (Problems != null)
        {
            report.Add("problems", Problems);
        }

        return report.Add("report_only", true).Add("targets", Targets);
    }

    public object Form(JsonSerializer serializer)
    {
        ReplyObject whole = Whole();
        if (ReplyLength.Utf8Bytes(whole, serializer) <= Report.SummaryBytes)
        {
            return whole;
        }

        ReplyObject summary = Summary(serializer);
        return ReplyLength.Characters(summary, serializer) <= Report.MostCharacters
            ? summary
            : throw new ApiException(Report.TooLarge,
                $"The report lists {Targets.Count} targets, more than the client takes even in its summary form; " +
                "nothing changed. Narrow the target: a filter on the find, a smaller ref, fewer spread lines.",
                new JObject
                {
                    ["targets"] = Targets.Count,
                    ["limit_tokens"] = Report.ClientTokens,
                    ["fix"] = "narrow the target: a filter on the find, a smaller ref, fewer spread lines"
                });
    }

    // The summary form: the whole report's keys that hold no list, the protected keys whole, report_only, and the
    // targets as [id, name] pairs, a target with a lifted guard or a driver kept whole.
    private ReplyObject Summary(JsonSerializer serializer)
    {
        ReplyObject summary = new ReplyObject();
        foreach (KeyValuePair<string, object?> key in ViewKeys.Pairs(serializer, Whole(), null))
        {
            if (key.Key == "targets" || (ViewKeys.IsList(key.Value) && Array.IndexOf(Protected, key.Key) < 0))
            {
                continue;
            }

            summary.Add(key.Key, key.Value);
        }

        return summary.Add("targets", new SummaryTargets(Targets));
    }

    [JsonConverter(typeof(SummaryTargetsConverter))]
    private sealed class SummaryTargets
    {
        internal SummaryTargets(IReadOnlyList<ReportTarget> targets) => Targets = targets;

        internal IReadOnlyList<ReportTarget> Targets { get; }
    }

    private sealed class SummaryTargetsConverter : JsonConverter
    {
        public override bool CanRead => false;

        public override bool CanConvert(Type objectType) => objectType == typeof(SummaryTargets);

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            foreach (ReportTarget target in ((SummaryTargets)value!).Targets)
            {
                if (target.KeptWhole)
                {
                    target.WriteTo(writer, serializer);
                }
                else
                {
                    target.WritePair(writer, serializer);
                }
            }

            writer.WriteEndArray();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue,
            JsonSerializer serializer) => throw new NotSupportedException("A summary is only written.");
    }
}

/// <summary>Writes a reply whose form is chosen as it is written (IReplyForm) in that form.</summary>
internal sealed class ReplyFormConverter : JsonConverter
{
    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) => typeof(IReplyForm).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        serializer.Serialize(writer, ((IReplyForm)value!).Form(serializer));

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
        throw new NotSupportedException("A reply is only written.");
}
