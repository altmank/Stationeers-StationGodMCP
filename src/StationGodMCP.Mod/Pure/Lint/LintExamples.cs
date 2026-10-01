#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Lint;

/// <summary>One example's result: expected pass or fail, and what the rule said.</summary>
internal sealed class LintExampleResult
{
    internal LintExampleResult(string expected, int index, string actual, bool ok, string? message, string? error)
    {
        Expected = expected;
        Index = index;
        Actual = actual;
        Ok = ok;
        Message = message;
        Error = error;
    }

    /// <summary>pass or fail.</summary>
    internal string Expected { get; }

    internal int Index { get; }

    /// <summary>pass, skipped (the where left it out: counts as a pass), fail or error.</summary>
    internal string Actual { get; }

    internal bool Ok { get; }

    internal string? Message { get; }

    internal string? Error { get; }
}

/// <summary>
/// A rule's examples: {"pass": [...], "fail": [...]}, each a subject written as its fields ({"prefab": ..., "cells":
/// [...]}), or {"a": {...}, "b": {...}} for a pair rule. A key with parentheses fixes a function's result on that
/// object: "sun_blocked()": true, "logic(On)": 1, "replaceable()": {"replaceable": false, ...}. A "world" key gives
/// the world object. Run by lint_rules' self-test without the game.
/// </summary>
internal static class LintExamples
{
    internal static void CheckShape(JToken examples, string file, string id, bool pairs, List<LintRuleError> errors)
    {
        if (!(examples is JObject cases))
        {
            errors.Add(LintRuleLoader.ErrorAt(file, id, "examples", examples, "examples is {\"pass\": [...], \"fail\": [...]}"));
            return;
        }

        foreach (JProperty property in cases.Properties())
        {
            if ((property.Name != "pass" && property.Name != "fail") || !(property.Value is JArray list))
            {
                errors.Add(LintRuleLoader.ErrorAt(file, id, "examples", property, "examples has pass and fail lists"));
                continue;
            }

            foreach (JToken item in list)
            {
                if (!(item is JObject example) || (pairs && (!(example["a"] is JObject) || !(example["b"] is JObject))))
                {
                    errors.Add(LintRuleLoader.ErrorAt(file, id, "examples", item,
                        pairs ? "a pair rule's example is {\"a\": {...}, \"b\": {...}}" : "an example is an object of fields"));
                }
            }
        }
    }

    internal static List<LintExampleResult> Run(LintRule rule, LintLibrary library)
    {
        List<LintExampleResult> results = new List<LintExampleResult>();
        if (!(rule.Source["examples"] is JObject cases))
        {
            return results;
        }

        foreach (string expected in new[] { "pass", "fail" })
        {
            if (!(cases[expected] is JArray list))
            {
                continue;
            }

            for (int index = 0; index < list.Count; index++)
            {
                results.Add(RunOne(rule, library, expected, index, list[index]));
            }
        }

        return results;
    }

    private static LintExampleResult RunOne(LintRule rule, LintLibrary library, string expected, int index,
        JToken token)
    {
        try
        {
            JObject example = (JObject)token;
            ObjectType element = LintModel.ObjectNamed(LintSets.ElementTypeOf(rule.Select.Set))!;
            ExampleWorld world = new ExampleWorld(example["world"] is JObject worldJson
                ? Record(LintModel.World, worldJson, library, "world")
                : new LintRecord(LintModel.World, "world", new Dictionary<string, LintValue>(StringComparer.Ordinal)));
            LintSubjectRef subject = rule.Select.IsPairs
                ? new LintSubjectRef(Record(element, (JObject)example["a"]!, library, "a"),
                    Record(element, (JObject)example["b"]!, library, "b"))
                : new LintSubjectRef(Record(element, Without(example, "world"), library, "x"));
            LintVerdict verdict = LintEngine.Judge(rule, subject, new LintContext(world), false, true);
            string actual = verdict.Outcome == LintOutcome.Failed ? "fail"
                : verdict.Outcome == LintOutcome.Error ? "error"
                : verdict.Outcome == LintOutcome.Skipped ? "skipped"
                : "pass";
            bool ok = expected == "fail" ? actual == "fail" : actual == "pass" || actual == "skipped";
            return new LintExampleResult(expected, index, actual, ok, verdict.Message, verdict.Error);
        }
        catch (Exception error) when (error is LintEvaluationException || error is InvalidCastException ||
                                      error is ArgumentException || error is JsonException)
        {
            return new LintExampleResult(expected, index, "error", false, null, error.Message);
        }
    }

    private static JObject Without(JObject example, string key)
    {
        if (example[key] == null)
        {
            return example;
        }

        JObject copy = (JObject)example.DeepClone();
        copy.Remove(key);
        return copy;
    }

    /// <summary>An object of the given type from its fields as JSON; keys with parentheses fix function results.</summary>
    internal static LintRecord Record(ObjectType type, JObject json, LintLibrary library, string path)
    {
        Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal);
        Dictionary<string, LintValue> stubs = new Dictionary<string, LintValue>(StringComparer.Ordinal);
        foreach (JProperty property in json.Properties())
        {
            int open = property.Name.IndexOf('(');
            if (open > 0 && property.Name.EndsWith(")", StringComparison.Ordinal))
            {
                string name = property.Name.Substring(0, open);
                LintFunction function = library.Find(name) ??
                                        throw new LintEvaluationException($"{path}: no function {name} to fix the result of");
                string inside = property.Name.Substring(open + 1, property.Name.Length - open - 2);
                stubs[inside.Length == 0 ? name : $"{name}({inside})"] =
                    Convert(property.Value, function.Returns, library, $"{path}.{property.Name}");
                continue;
            }

            LintField field = type.Find(property.Name) ??
                              throw new LintEvaluationException($"{path}: a {type.Name} has no field {property.Name}");
            values[field.Name] = Convert(property.Value, field.Type, library, $"{path}.{property.Name}");
        }

        string key = values.TryGetValue("reference_id", out LintValue id) && id.Kind == LintKind.Number
            ? $"{type.Name}:{LintValue.NumberText(id.AsNumber)}"
            : values.TryGetValue("id", out LintValue other) && other.Kind == LintKind.Number
                ? $"{type.Name}:{LintValue.NumberText(other.AsNumber)}"
                : $"{type.Name}:{json.ToString(Formatting.None)}";
        string describe = values.TryGetValue("prefab", out LintValue prefab) && prefab.Kind == LintKind.String
            ? $"{prefab.AsString} (example {path})"
            : $"{type.Name} (example {path})";
        return new LintRecord(type, key, values, stubs, describe);
    }

    private static LintValue Convert(JToken token, LintType type, LintLibrary library, string path)
    {
        if (token.Type == JTokenType.Null)
        {
            return type.IsNullable || ReferenceEquals(type, LintType.Any)
                ? LintValue.Null
                : throw new LintEvaluationException($"{path} is a {type.Name}, never null");
        }

        LintType core = type.NonNull;
        if (ReferenceEquals(core, LintType.Any))
        {
            return Infer(token, library, path);
        }

        if (ReferenceEquals(core, LintType.Bool) && token.Type == JTokenType.Boolean)
        {
            return LintValue.Of((bool)token);
        }

        if (ReferenceEquals(core, LintType.Number) && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
        {
            return LintValue.Of((double)token);
        }

        if (ReferenceEquals(core, LintType.Number) && token.Type == JTokenType.String &&
            ((string)token! == "infinity" || (string)token! == "-infinity"))
        {
            return LintValue.Of((string)token! == "infinity" ? double.PositiveInfinity : double.NegativeInfinity);
        }

        if (ReferenceEquals(core, LintType.String) && token.Type == JTokenType.String)
        {
            return LintValue.Of((string)token!);
        }

        if (ReferenceEquals(core, LintType.Vec) && token is JArray xyz && xyz.Count == 3)
        {
            return LintValue.Of(new Vec3((double)xyz[0], (double)xyz[1], (double)xyz[2]));
        }

        if (core is ListType list && token is JArray items)
        {
            LintValue[] values = new LintValue[items.Count];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = Convert(items[index], list.Element, library, $"{path}[{index}]");
            }

            return LintValue.Of(values);
        }

        if (core is MapType map && token is JObject entries)
        {
            Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal);
            foreach (JProperty entry in entries.Properties())
            {
                values[entry.Name] = Convert(entry.Value, map.Value, library, $"{path}.{entry.Name}");
            }

            return LintValue.Of(values);
        }

        if (core is ObjectType objectType && token is JObject fields)
        {
            return LintValue.Of(Record(objectType, fields, library, path));
        }

        throw new LintEvaluationException($"{path} should be a {type.Name}");
    }

    private static LintValue Infer(JToken token, LintLibrary library, string path) => token.Type switch
    {
        JTokenType.Boolean => LintValue.Of((bool)token),
        JTokenType.Integer or JTokenType.Float => LintValue.Of((double)token),
        JTokenType.String => LintValue.Of((string)token!),
        JTokenType.Array => Convert(token, LintType.ListOf(LintType.Any), library, path),
        JTokenType.Object => Convert(token, LintType.MapOf(LintType.Any), library, path),
        _ => LintValue.Null
    };

    /// <summary>The world of an example: its world object, no subjects, no cells.</summary>
    private sealed class ExampleWorld : ILintWorld
    {
        internal ExampleWorld(ILintObject world)
        {
            World = world;
        }

        public ILintObject World { get; }

        public IReadOnlyList<ILintObject> Subjects(string set) => new ILintObject[0];

        public ILintObject? CellAt(Vec3 point, double size) => null;

        public bool IsPlanned(ILintObject subject) => false;
    }
}
