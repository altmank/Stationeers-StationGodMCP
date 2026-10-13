#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// check rules' call, checked without the game: the action (list, validate, fields, functions, test, explain), the one
/// rule it is about, the rule file text it reads instead of the files in effect, and for explain the subject.
/// </summary>
internal sealed class RuleCheck
{
    internal const string List = "list";
    internal const string Validate = "validate";
    internal const string Fields = "fields";
    internal const string Functions = "functions";
    internal const string Test = "test";
    internal const string Explain = "explain";

    private RuleCheck(string action, string? rule, bool full, string? rulesText, ExplainSubject? subject)
    {
        Action = action;
        Rule = rule;
        Full = full;
        RulesText = rulesText;
        Subject = subject;
    }

    public string Action { get; }

    public string? Rule { get; }

    /// <summary>list: every rule's file and expressions.</summary>
    public bool Full { get; }

    public string? RulesText { get; }

    /// <summary>explain's subject; null for every other action.</summary>
    public ExplainSubject? Subject { get; }

    /// <summary>The call's check, or null with every problem added.</summary>
    internal static RuleCheck? Of(CheckRulesOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        string action = options.Action ?? List;
        string? rule = string.IsNullOrWhiteSpace(options.Rule) ? null : options.Rule!.Trim();
        ExplainSubject? subject = null;
        if (action == Explain)
        {
            if (rule == null)
            {
                problems.Add(new OptionProblem("rule", "explain needs the rule to explain"));
            }

            subject = SubjectOf(options, problems);
        }

        return problems.Count > before
            ? null
            : new RuleCheck(action, rule, options.Full ?? false, options.RulesText, subject);
    }

    private static ExplainSubject? SubjectOf(CheckRulesOptions options, List<OptionProblem> problems)
    {
        long? id = null;
        if (options.Selector.Ids is { } ids)
        {
            if (ids.Ids.Count != 1)
            {
                problems.Add(new OptionProblem("id", "explain reads one subject: one thing, network or room id"));
            }
            else if ((id = IdOf(ids.Ids[0])) == null)
            {
                problems.Add(new OptionProblem("id", "is a thing, network or room id"));
            }
        }

        Vec3? at = null;
        if (options.At != null)
        {
            at = options.At is JArray point ? Point.Of(point) : null;
            if (at == null)
            {
                problems.Add(new OptionProblem("at", "is a cell, [x, y, z]"));
            }
        }

        long? other = null;
        if (options.Other != null && (other = IdOf(options.Other)) == null)
        {
            problems.Add(new OptionProblem("other", "is the other thing's id"));
        }

        if (options.Selector.Ids == null && options.At == null)
        {
            problems.Add(new OptionProblem("id", "or at is needed: explain reads a thing, network or room id, or a cell"));
        }

        return new ExplainSubject(id, options.Port is { } port ? (int)port : null, other, at);
    }

    private static long? IdOf(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0
            ? id
            : null;
}

/// <summary>What explain judges: a thing, network or room (with one of its ports), a cell, and a pair rule's other.</summary>
internal sealed class ExplainSubject
{
    internal ExplainSubject(long? id, int? port, long? other, Vec3? at)
    {
        Id = id;
        Port = port;
        Other = other;
        At = at;
    }

    public long? Id { get; }

    public int? Port { get; }

    public long? Other { get; }

    public Vec3? At { get; }
}
