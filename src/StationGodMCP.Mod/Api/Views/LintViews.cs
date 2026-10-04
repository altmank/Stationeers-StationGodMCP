#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Views;

/// <summary>Which rule files a lint used: the mod's, the save's (null when none), what was turned off, what failed.</summary>
internal sealed class LintRuleSourceView
{
    internal LintRuleSourceView(LintRuleSet set)
    {
        ModFile = set.Files.Count > 0 ? set.Files[0] : null;
        SaveFile = set.Files.Count > 1 ? set.Files[1] : null;
        Rules = set.Rules.Count;
        FromSave = new List<string>();
        foreach (LintRule rule in set.Rules)
        {
            if (rule.Origin == LintRuleOrigin.Save)
            {
                FromSave.Add(rule.Id);
            }
        }

        Disabled = new List<string>(set.Disabled);
        Errors = new List<LintRuleErrorView>();
        foreach (LintRuleError error in set.Errors)
        {
            Errors.Add(new LintRuleErrorView(error));
        }
    }

    public string? ModFile { get; }

    /// <summary>The save's lint-rules.json, when one was found; it takes priority.</summary>
    public string? SaveFile { get; }

    /// <summary>Rules in effect.</summary>
    public int Rules { get; }

    /// <summary>Rules the save's file adds or replaces.</summary>
    public List<string> FromSave { get; }

    /// <summary>Rules turned off ("enabled": false).</summary>
    public List<string> Disabled { get; }

    /// <summary>Rules or files that did not load (left out; the rest ran).</summary>
    public List<LintRuleErrorView> Errors { get; }
}

/// <summary>A rule file error: file, rule, field, line and column in the file, character in the expression, why.</summary>
internal sealed class LintRuleErrorView
{
    internal LintRuleErrorView(LintRuleError error)
    {
        File = error.File;
        RuleId = error.RuleId;
        Field = error.Field;
        Line = error.Line;
        Column = error.Column;
        Position = error.Position;
        Message = error.Message;
    }

    public string File { get; }

    public string? RuleId { get; }

    public string? Field { get; }

    public int Line { get; }

    public int Column { get; }

    /// <summary>1-based character within the expression.</summary>
    public int? Position { get; }

    public string Message { get; }
}

/// <summary>One rule as it is in effect, where it came from, and its examples' count.</summary>
internal sealed class LintRuleView
{
    internal LintRuleView(LintRule rule, bool full)
    {
        Id = rule.Id;
        Level = LintRule.NameOf(rule.Level);
        LevelWhen = (string?)rule.Source["level_when"];
        On = new List<string>(rule.On);
        Source = rule.Origin == LintRuleOrigin.Save ? "save" : "mod";
        File = full ? rule.File : null;
        Description = rule.Description;
        Select = full ? rule.SelectText : null;
        Let = full && rule.Lets.Count > 0 ? new Dictionary<string, string>() : null;
        foreach ((string name, string source) in rule.Lets)
        {
            Let?.Add(name, source);
        }

        Assert = full ? rule.AssertText : null;
        Message = full ? rule.MessageText : null;
        Other = full ? (string?)rule.Source["other"] : null;
        JArray? pass = rule.Source["examples"]?["pass"] as JArray;
        JArray? fail = rule.Source["examples"]?["fail"] as JArray;
        Examples = (pass?.Count ?? 0) + (fail?.Count ?? 0);
    }

    public string Id { get; }

    public string Level { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? LevelWhen { get; }

    public List<string> On { get; }

    /// <summary>mod or save: rule_source names both files once.</summary>
    public string Source { get; }

    /// <summary>The file the rule comes from; with full or rule_id only.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? File { get; }

    public string? Description { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Select { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string>? Let { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Assert { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Message { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Other { get; }

    public int Examples { get; }
}

/// <summary>One field of an object type.</summary>
internal sealed class LintFieldView
{
    internal LintFieldView(LintField field)
    {
        Name = field.Name;
        Type = field.Type.Name;
        Doc = field.Doc;
    }

    public string Name { get; }

    public string Type { get; }

    public string Doc { get; }
}

/// <summary>An object type and its fields.</summary>
internal sealed class LintTypeView
{
    internal LintTypeView(ObjectType type)
    {
        Name = type.Name;
        Doc = type.Doc;
        Fields = new List<LintFieldView>();
        foreach (LintField field in type.Fields)
        {
            Fields.Add(new LintFieldView(field));
        }
    }

    public string Name { get; }

    public string Doc { get; }

    public List<LintFieldView> Fields { get; }
}

/// <summary>A library function or language form with its signature.</summary>
internal sealed class LintFunctionView
{
    internal LintFunctionView(string name, string signature, string doc, bool form, bool cached)
    {
        Name = name;
        Signature = signature;
        Doc = doc;
        Form = form;
        Cached = cached;
    }

    public string Name { get; }

    public string Signature { get; }

    public string Doc { get; }

    /// <summary>Part of the language (a collection form, if, has) rather than a registered function.</summary>
    public bool Form { get; }

    /// <summary>Its result is kept per lint call for the same arguments.</summary>
    public bool Cached { get; }
}

/// <summary>A rule's example self-test.</summary>
internal sealed class LintExampleView
{
    internal LintExampleView(LintExampleResult result)
    {
        Expected = result.Expected;
        Index = result.Index;
        Actual = result.Actual;
        Ok = result.Ok;
        Message = result.Message;
        Error = result.Error;
    }

    public string Expected { get; }

    public int Index { get; }

    public string Actual { get; }

    public bool Ok { get; }

    public string? Message { get; }

    public string? Error { get; }
}

/// <summary>One rule's self-test.</summary>
internal sealed class LintRuleTestView
{
    internal LintRuleTestView(string id, List<LintExampleResult> results)
    {
        Id = id;
        Examples = results.ConvertAll(result => new LintExampleView(result));
        Passed = Examples.TrueForAll(example => example.Ok);
    }

    public string Id { get; }

    public bool Passed { get; }

    public List<LintExampleView> Examples { get; }
}

/// <summary>One value explain recorded: the expression and the values it had (several inside a lambda).</summary>
internal sealed class LintTracedView
{
    internal LintTracedView(string expression, IReadOnlyList<LintValue> values)
    {
        Expression = expression;
        Values = new List<string>(values.Count);
        foreach (LintValue value in values)
        {
            Values.Add(value.ToText());
        }
    }

    public string Expression { get; }

    public List<string> Values { get; }
}

/// <summary>Why a rule said what it said about a subject: its outcome and the values it read.</summary>
internal sealed class LintExplainView
{
    internal LintExplainView(string rule, string subject, LintVerdict verdict)
    {
        Rule = rule;
        Subject = subject;
        Outcome = verdict.Outcome.ToString().ToLowerInvariant();
        Level = verdict.Outcome == LintOutcome.Failed ? LintRule.NameOf(verdict.Level) : null;
        Message = verdict.Message;
        Error = verdict.Error;
        Values = new List<LintTracedView>();
        if (verdict.Trace != null)
        {
            foreach (string expression in verdict.Trace.Order)
            {
                Values.Add(new LintTracedView(expression, verdict.Trace.ValuesOf(expression)));
            }
        }
    }

    public string Rule { get; }

    public string Subject { get; }

    /// <summary>skipped (the select's where left it out), passed, failed or error.</summary>
    public string Outcome { get; }

    public string? Level { get; }

    public string? Message { get; }

    public string? Error { get; }

    public List<LintTracedView> Values { get; }
}

/// <summary>lint_rules' reply: what was asked for, the rule source, and the part the action fills.</summary>
internal sealed class LintRulesView
{
    internal LintRulesView(string action, LintRuleSourceView source)
    {
        Action = action;
        RuleSource = source;
    }

    public string Action { get; }

    public LintRuleSourceView RuleSource { get; }

    public List<LintRuleView>? Rules { get; set; }

    public List<LintTypeView>? Types { get; set; }

    public Dictionary<string, string>? Sets { get; set; }

    public List<LintFunctionView>? Functions { get; set; }

    public List<LintRuleTestView>? Tests { get; set; }

    public bool? AllPassed { get; set; }

    public List<LintExplainView>? Explained { get; set; }

    public bool? Valid { get; set; }
}
