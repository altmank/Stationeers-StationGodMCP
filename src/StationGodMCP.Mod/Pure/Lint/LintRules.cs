#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Lint;

/// <summary>Where a rule came from: the rule file shipped with the mod, or the one next to the save.</summary>
internal enum LintRuleOrigin
{
    Mod,
    Save
}

/// <summary>A rule file that does not load, or a rule in it that does not compile: file, rule, field, place, why.</summary>
internal sealed class LintRuleError
{
    internal LintRuleError(string file, string? ruleId, string? field, int line, int column, string message,
        int? position = null)
    {
        File = file;
        RuleId = ruleId;
        Field = field;
        Line = line;
        Column = column;
        Message = message;
        Position = position;
    }

    internal string File { get; }

    internal string? RuleId { get; }

    /// <summary>The rule's field the error is in: select, assert, let.name, message, other, level_when, examples.</summary>
    internal string? Field { get; }

    /// <summary>1-based line in the file (0 when unknown).</summary>
    internal int Line { get; }

    /// <summary>1-based column in the file (0 when unknown).</summary>
    internal int Column { get; }

    /// <summary>1-based character within the expression, for an expression error.</summary>
    internal int? Position { get; }

    internal string Message { get; }

    public override string ToString() =>
        $"{File}:{Line}:{Column}: {(RuleId != null ? $"rule {RuleId}" : "file")}{(Field != null ? $" {Field}" : string.Empty)}" +
        $"{(Position.HasValue ? $" at character {Position}" : string.Empty)}: {Message}";
}

/// <summary>A compiled evaluation unit: its frame size and lazy slots, and the slots its subjects go in.</summary>
internal sealed class LintUnit
{
    internal LintUnit(LintCompiler compiler, int first, int second)
    {
        Compiler = compiler;
        First = first;
        Second = second;
    }

    internal LintCompiler Compiler { get; }

    /// <summary>The slot of x (or a for a pair rule).</summary>
    internal int First { get; }

    /// <summary>The slot of b for a pair rule; -1 otherwise.</summary>
    internal int Second { get; }

    private LintEval?[]? _lazy;

    internal LintFrame Frame(LintContext context, LintSubjectRef subject, LintTrace? trace = null)
    {
        _lazy ??= Compiler.LazySlots;
        LintFrame frame = new LintFrame(context, _lazy.Length, _lazy, trace);
        frame.Set(First, LintValue.Of(subject.First));
        if (Second >= 0 && subject.Second != null)
        {
            frame.Set(Second, LintValue.Of(subject.Second));
        }

        return frame;
    }
}

/// <summary>One subject a rule is asked about: a thing, port, cell, network or room, or a pair of them.</summary>
internal readonly struct LintSubjectRef
{
    internal LintSubjectRef(ILintObject first, ILintObject? second = null)
    {
        First = first;
        Second = second;
    }

    internal ILintObject First { get; }

    internal ILintObject? Second { get; }

    internal string Key => Second == null ? First.Key : First.Key + "+" + Second.Key;

    internal string Describe => Second == null ? First.Describe : $"{First.Describe} and {Second.Describe}";
}

/// <summary>A message with {expression} parts filled from the failing subject; {{ and }} are braces.</summary>
internal sealed class LintTemplate
{
    private readonly List<(string? Text, LintEval? Part)> _parts;

    private LintTemplate(List<(string?, LintEval?)> parts)
    {
        _parts = parts;
    }

    internal static LintTemplate Compile(string template, LintCompiler compiler, LintScope scope)
    {
        List<(string?, LintEval?)> parts = new List<(string?, LintEval?)>();
        StringBuilder text = new StringBuilder();
        int at = 0;
        while (at < template.Length)
        {
            char c = template[at];
            if ((c == '{' || c == '}') && at + 1 < template.Length && template[at + 1] == c)
            {
                text.Append(c);
                at += 2;
                continue;
            }

            if (c == '}')
            {
                throw new LintSyntaxException("a } with no { before it (write }} for a brace)", at);
            }

            if (c != '{')
            {
                text.Append(c);
                at++;
                continue;
            }

            int end = template.IndexOf('}', at + 1);
            if (end < 0)
            {
                throw new LintSyntaxException("a { is not closed (write {{ for a brace)", at);
            }

            if (text.Length > 0)
            {
                parts.Add((text.ToString(), null));
                text.Clear();
            }

            string source = template.Substring(at + 1, end - at - 1);
            try
            {
                LintCompiled compiled = compiler.Compile(LintParser.Expression(source), scope);
                parts.Add((null, compiled.Eval));
            }
            catch (LintSyntaxException error)
            {
                throw new LintSyntaxException(error.Message, at + 1 + error.Position);
            }

            at = end + 1;
        }

        if (text.Length > 0)
        {
            parts.Add((text.ToString(), null));
        }

        return new LintTemplate(parts);
    }

    internal string Render(LintFrame frame)
    {
        StringBuilder message = new StringBuilder();
        foreach ((string? text, LintEval? part) in _parts)
        {
            message.Append(text ?? part!(frame).ToText());
        }

        return message.ToString();
    }
}

/// <summary>
/// One compiled rule: what it selects, the assertion each subject must hold, the message for one that does not, and
/// where it came from. Built once per rule file change.
/// </summary>
internal sealed class LintRule
{
    internal static readonly string[] Phases = { "audit", "dry_run" };

    internal LintRule(string id, ConflictLevel level, string? description, IReadOnlyList<string> on, string selectText,
        string assertText, SelectNode select, LintUnit unit, LintEval? where, LintUnit? whereUnit, LintEval assert,
        LintTemplate message, string messageText, LintEval? other, LintEval? levelWhen, JObject source,
        LintRuleOrigin origin, string file, IReadOnlyList<(string Name, string Source)> lets)
    {
        Id = id;
        Level = level;
        Description = description;
        On = on;
        SelectText = selectText;
        AssertText = assertText;
        Select = select;
        Unit = unit;
        Where = where;
        WhereUnit = whereUnit;
        Assert = assert;
        Message = message;
        MessageText = messageText;
        Other = other;
        LevelWhen = levelWhen;
        Source = source;
        Origin = origin;
        File = file;
        Lets = lets;
    }

    internal string Id { get; }

    internal ConflictLevel Level { get; }

    internal string? Description { get; }

    internal IReadOnlyList<string> On { get; }

    internal string SelectText { get; }

    internal string AssertText { get; }

    internal SelectNode Select { get; }

    internal LintUnit Unit { get; }

    /// <summary>The select's where, run in Unit (single subjects) or WhereUnit (each side of a pair).</summary>
    internal LintEval? Where { get; }

    internal LintUnit? WhereUnit { get; }

    internal LintEval Assert { get; }

    internal LintTemplate Message { get; }

    internal string MessageText { get; }

    internal LintEval? Other { get; }

    /// <summary>level_when: an expression giving the level (problem, warning, info) for one failing subject.</summary>
    internal LintEval? LevelWhen { get; }

    /// <summary>The rule as written, for lint_rules and for an override to patch.</summary>
    internal JObject Source { get; }

    internal LintRuleOrigin Origin { get; }

    internal string File { get; }

    internal IReadOnlyList<(string Name, string Source)> Lets { get; }

    internal bool RunsOn(string phase)
    {
        foreach (string each in On)
        {
            if (each == phase)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryLevel(string? text, out ConflictLevel level)
    {
        switch (text)
        {
            case "problem":
                level = ConflictLevel.Problem;
                return true;
            case "warning":
                level = ConflictLevel.Warning;
                return true;
            case "info":
                level = ConflictLevel.Info;
                return true;
            default:
                level = ConflictLevel.Info;
                return false;
        }
    }

    internal static string NameOf(ConflictLevel level) => level.ToString().ToLowerInvariant();
}
