#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>A method's permission class (protocol.md, Sign-in and permissions).</summary>
internal enum MethodClass
{
    Read,
    Write,
    Cheat
}

/// <summary>The cost class the scheduler starts from (catalogue.md, What each part means).</summary>
internal enum CostClass
{
    Instant,
    Bounded,
    World,
    Plan,
    Job,
    Stream
}

/// <summary>How one argument must look for a rule to match: equals, in, present or absent.</summary>
internal abstract class ArgumentMatcher
{
    private ArgumentMatcher()
    {
    }

    /// <summary>value is the argument as given, null when missing or JSON null (the mod reads null as omitted).</summary>
    internal abstract bool Matches(JToken? value);

    internal static ArgumentMatcher Compile(JToken token, string at)
    {
        if (!(token is JObject matcher) || matcher.Count != 1)
        {
            throw new CatalogueException($"{at}: a matcher is an object with exactly one of equals, in, present, absent.");
        }

        JProperty only = matcher.First as JProperty ?? throw new CatalogueException($"{at}: empty matcher.");
        switch (only.Name)
        {
            case "equals":
                return new EqualTo(only.Value);
            case "in":
                if (!(only.Value is JArray values) || values.Count == 0)
                {
                    throw new CatalogueException($"{at}.in: must be a non-empty array.");
                }

                return new OneOf(new List<JToken>(values));
            case "present" when only.Value.Type == JTokenType.Boolean && (bool)only.Value:
                return new Present();
            case "absent" when only.Value.Type == JTokenType.Boolean && (bool)only.Value:
                return new Absent();
            default:
                throw new CatalogueException($"{at}: unknown matcher '{only.Name}' (equals, in, present: true, absent: true).");
        }
    }

    /// <summary>JSON equality after the mod's trimming and case folding of words.</summary>
    internal sealed class EqualTo : ArgumentMatcher
    {
        private readonly JToken _expected;

        internal EqualTo(JToken expected)
        {
            _expected = expected;
        }

        internal override bool Matches(JToken? value) => value != null && SchemaNode.SameValue(_expected, value);
    }

    internal sealed class OneOf : ArgumentMatcher
    {
        private readonly List<JToken> _values;

        internal OneOf(List<JToken> values)
        {
            _values = values;
        }

        internal override bool Matches(JToken? value)
        {
            if (value == null)
            {
                return false;
            }

            foreach (JToken expected in _values)
            {
                if (SchemaNode.SameValue(expected, value))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class Present : ArgumentMatcher
    {
        internal override bool Matches(JToken? value) => value != null;
    }

    internal sealed class Absent : ArgumentMatcher
    {
        internal override bool Matches(JToken? value) => value == null;
    }
}

/// <summary>A when: every argument it names matches its matcher.</summary>
internal sealed class ArgumentCondition
{
    private readonly List<KeyValuePair<string, ArgumentMatcher>> _matchers;

    private ArgumentCondition(List<KeyValuePair<string, ArgumentMatcher>> matchers)
    {
        _matchers = matchers;
    }

    internal static ArgumentCondition Compile(JToken token, string at)
    {
        if (!(token is JObject when) || when.Count == 0)
        {
            throw new CatalogueException($"{at}: a when is an object naming at least one argument.");
        }

        List<KeyValuePair<string, ArgumentMatcher>> matchers = new List<KeyValuePair<string, ArgumentMatcher>>(when.Count);
        foreach (JProperty argument in when.Properties())
        {
            matchers.Add(new KeyValuePair<string, ArgumentMatcher>(argument.Name,
                ArgumentMatcher.Compile(argument.Value, $"{at}.{argument.Name}")));
        }

        return new ArgumentCondition(matchers);
    }

    internal bool Matches(JObject? arguments)
    {
        foreach (KeyValuePair<string, ArgumentMatcher> matcher in _matchers)
        {
            JToken? value = arguments?[matcher.Key];
            if (!matcher.Value.Matches(value == null || value.Type == JTokenType.Null ? null : value))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One rule of x-class-when or x-cost-when: it matches when its when matches and, with any_of, at least one of those
/// also matches. Rules are tried in order and the first that matches decides.
/// </summary>
internal sealed class CatalogueRule
{
    private readonly ArgumentCondition _when;
    private readonly List<ArgumentCondition>? _anyOf;

    private CatalogueRule(ArgumentCondition when, List<ArgumentCondition>? anyOf, MethodClass? methodClass,
        CostClass? cost, string? perItem)
    {
        _when = when;
        _anyOf = anyOf;
        Class = methodClass;
        Cost = cost;
        PerItem = perItem;
    }

    internal MethodClass? Class { get; }

    internal CostClass? Cost { get; }

    /// <summary>The array argument whose length scales the cost, or null.</summary>
    internal string? PerItem { get; }

    internal bool Matches(JObject? arguments)
    {
        if (!_when.Matches(arguments))
        {
            return false;
        }

        if (_anyOf == null)
        {
            return true;
        }

        foreach (ArgumentCondition condition in _anyOf)
        {
            if (condition.Matches(arguments))
            {
                return true;
            }
        }

        return false;
    }

    internal static List<CatalogueRule> CompileAll(JToken? token, string at)
    {
        List<CatalogueRule> rules = new List<CatalogueRule>();
        if (token == null)
        {
            return rules;
        }

        if (!(token is JArray list))
        {
            throw new CatalogueException($"{at}: must be an array of rules.");
        }

        for (int index = 0; index < list.Count; index++)
        {
            rules.Add(Compile(list[index], $"{at}[{index}]"));
        }

        return rules;
    }

    private static CatalogueRule Compile(JToken token, string at)
    {
        if (!(token is JObject rule) || rule["when"] == null)
        {
            throw new CatalogueException($"{at}: a rule is an object with a when.");
        }

        List<ArgumentCondition>? anyOf = null;
        if (rule["any_of"] is JToken alternatives)
        {
            if (!(alternatives is JArray list))
            {
                throw new CatalogueException($"{at}.any_of: must be an array of whens.");
            }

            anyOf = new List<ArgumentCondition>(list.Count);
            for (int index = 0; index < list.Count; index++)
            {
                anyOf.Add(ArgumentCondition.Compile(list[index], $"{at}.any_of[{index}]"));
            }
        }

        return new CatalogueRule(ArgumentCondition.Compile(rule["when"]!, $"{at}.when"), anyOf,
            rule["class"] is JToken cls ? CatalogueWords.ClassOf(cls, $"{at}.class") : (MethodClass?)null,
            rule["cost"] is JToken cost ? CatalogueWords.CostOf(cost, $"{at}.cost") : (CostClass?)null,
            rule["per_item"]?.Type == JTokenType.String ? (string)rule["per_item"]! : null);
    }
}

/// <summary>The catalogue's words for classes and costs.</summary>
internal static class CatalogueWords
{
    internal static MethodClass ClassOf(JToken token, string at) => (token.Type == JTokenType.String ? (string)token! : null) switch
    {
        "read" => MethodClass.Read,
        "write" => MethodClass.Write,
        "cheat" => MethodClass.Cheat,
        _ => throw new CatalogueException($"{at}: must be read, write or cheat.")
    };

    internal static CostClass CostOf(JToken token, string at) => (token.Type == JTokenType.String ? (string)token! : null) switch
    {
        "instant" => CostClass.Instant,
        "bounded" => CostClass.Bounded,
        "world" => CostClass.World,
        "plan" => CostClass.Plan,
        "job" => CostClass.Job,
        "stream" => CostClass.Stream,
        _ => throw new CatalogueException($"{at}: must be instant, bounded, world, plan, job or stream.")
    };
}

/// <summary>A cost at the arguments given: the class, and the item count a per_item rule scales it by (1 otherwise).</summary>
internal readonly struct CallCost
{
    internal CallCost(CostClass cost, int items)
    {
        Cost = cost;
        Items = items;
    }

    internal CostClass Cost { get; }

    internal int Items { get; }
}
