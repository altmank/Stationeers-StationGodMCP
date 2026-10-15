#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// Checks a verb call before anything runs: the what, and every option against the what's schema (types, enum, bounds,
/// list lengths, list items, required options). Every problem comes back at once, in verb names, with the nearest
/// valid names (PLAN 2.0, shared forms). JSON null is an omitted option. The hub's own options (output_file, whole,
/// repeat, max_wait and file options) never reach the game: the hub takes them out, so one given here is a problem.
/// </summary>
internal static class OptionCheck
{
    /// <summary>The key params name the what with.</summary>
    internal const string WhatKey = "what";

    private static readonly string[] HubOptions = { "output_file", "whole", "repeat", "max_wait" };

    /// <summary>The call's what, or the invalid_argument that lists every problem with it.</summary>
    internal static VerbError? Check(VerbSpec verb, JObject? parameters, out WhatSpec? what)
    {
        what = null;
        JObject given = parameters ?? new JObject();
        List<OptionProblem> problems = new List<OptionProblem>();
        string? whatName = Text(given[WhatKey]);
        if (verb.HasWhats)
        {
            what = verb.What(whatName);
            if (what == null)
            {
                problems.Add(whatName == null
                    ? new OptionProblem(WhatKey, "required: one of " + string.Join(", ", verb.WhatNames))
                    : new OptionProblem(WhatKey, $"'{whatName}' is not a what of {verb.Name}",
                        NearestNames.Of(whatName, verb.WhatNames)));
                return OptionProblem.Refusal(verb.Name, problems);
            }
        }
        else
        {
            what = verb.What(null)!;
            if (given[WhatKey] is { Type: not JTokenType.Null } && !what.Takes(WhatKey))
            {
                problems.Add(new OptionProblem(WhatKey, $"{verb.Name} takes no what"));
                return OptionProblem.Refusal(verb.Name, problems);
            }
        }

        // Every name the what takes, for the nearest names: built only when an unknown name is reported.
        IReadOnlyList<string>? valid = null;
        string? unknownName = null;
        foreach (JProperty property in given.Properties())
        {
            string name = property.Name;
            JToken value = property.Value;
            if (value.Type == JTokenType.Null || (name == WhatKey && verb.HasWhats))
            {
                continue;
            }

            if (IsHubOption(what, name))
            {
                problems.Add(new OptionProblem(name, "taken by the MCP server (or sg), not by the game"));
                continue;
            }

            OptionSpec? option = what.Option(name) ?? (verb.TakesShaping ? VerbSurface.ShapingOption(name) : null);
            if (option == null)
            {
                problems.Add(new OptionProblem(name, $"not an option of {what.Label}", NearestNames.Of(name, valid ??= ValidNames(verb, what))));
                unknownName ??= name;
                continue;
            }

            if (name == "id" && value is JArray && !what.IdTakesList)
            {
                problems.Add(new OptionProblem(name, OneId(what)));
                continue;
            }

            Value(option.Schema, value, name, problems);
        }

        foreach (string name in what.OptionNames)
        {
            OptionSpec option = what.Option(name)!;
            if (option.Required && (given[name] == null || given[name]!.Type == JTokenType.Null))
            {
                problems.Add(new OptionProblem(name, "required"));
            }
        }

        return problems.Count == 0
            ? null
            : OptionProblem.Refusal(what.Label, problems,
                unknownName == null ? null : NearestNames.NearestFirst(unknownName, valid!));
    }

    /// <summary>An id that takes no list: a write that cannot be undone takes one id; several are a script's loop.</summary>
    internal static string OneId(WhatSpec what) =>
        $"takes one id: for several, use run_script (a loop of sg.{what.Verb} calls; this write cannot be undone)";

    private static bool IsHubOption(WhatSpec what, string name)
    {
        if (Array.IndexOf(HubOptions, name) >= 0)
        {
            return true;
        }

        const string FileSuffix = "_file";
        return name.EndsWith(FileSuffix, StringComparison.Ordinal) &&
               what.Takes(name.Substring(0, name.Length - FileSuffix.Length));
    }

    // The what's own options, then the shaping ones the verb takes; the hub's own are not the game's.
    private static IReadOnlyList<string> ValidNames(VerbSpec verb, WhatSpec what)
    {
        List<string> names = new List<string>();
        foreach (string name in what.OptionNames)
        {
            if (!IsHubOption(what, name))
            {
                names.Add(name);
            }
        }

        if (verb.TakesShaping)
        {
            foreach (OptionSpec shaping in VerbSurface.Shaping)
            {
                if (!names.Contains(shaping.Name))
                {
                    names.Add(shaping.Name);
                }
            }
        }

        return names;
    }

    /// <summary>The value's problems against a schema (type, enum, bounds, list length, items), added to problems.</summary>
    internal static void Value(JObject schema, JToken value, string path, List<OptionProblem> problems)
    {
        string kind = KindOf(value);
        IReadOnlyList<string> types = SchemaTypes.Of(schema);
        if (types.Count > 0 && !Allows(types, kind))
        {
            problems.Add(new OptionProblem(path, "must be " + TypeWords(types)));
            return;
        }

        if (schema["enum"] is JArray listed && listed.Count > 0 && !Listed(listed, value))
        {
            List<string> words = Words(listed);
            string shown = Text(value) ?? value.ToString(Newtonsoft.Json.Formatting.None);
            problems.Add(new OptionProblem(path, $"'{shown}' is not one of {string.Join(", ", words)}",
                NearestNames.Of(shown, words)));
            return;
        }

        if (kind is "integer" or "number")
        {
            Bounds(schema, (double)value, path, problems);
        }

        if (value is JArray items)
        {
            if (Count(schema, "minItems") is { } least && items.Count < least)
            {
                problems.Add(new OptionProblem(path, $"needs at least {least} entries"));
            }

            if (Count(schema, "maxItems") is { } most && items.Count > most)
            {
                problems.Add(new OptionProblem(path, $"takes at most {most} entries; {items.Count} given"));
            }

            if (schema["items"] is JObject itemSchema && itemSchema.Count > 0)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    if (items[index].Type != JTokenType.Null)
                    {
                        Value(itemSchema, items[index], $"{path}[{index}]", problems);
                    }
                }
            }
        }
    }

    /// <summary>The JSON kind of a value: string, integer (a number with no fraction), number, boolean, array, object.</summary>
    internal static string KindOf(JToken value) => value.Type switch
    {
        JTokenType.Object => "object",
        JTokenType.Array => "array",
        JTokenType.String => "string",
        JTokenType.Boolean => "boolean",
        JTokenType.Integer => "integer",
        JTokenType.Float => IsWhole((double)value) ? "integer" : "number",
        _ => "null"
    };

    private static bool IsWhole(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && Math.Floor(value) == value;

    private static bool Allows(IReadOnlyList<string> types, string kind)
    {
        foreach (string type in types)
        {
            if (type == kind || (type == "number" && kind == "integer"))
            {
                return true;
            }
        }

        return false;
    }

    private static void Bounds(JObject schema, double given, string path, List<OptionProblem> problems)
    {
        if (Number(schema["minimum"]) is { } minimum && given < minimum)
        {
            problems.Add(new OptionProblem(path, $"must be at least {Shown(minimum)}"));
        }

        if (Number(schema["exclusiveMinimum"]) is { } above && given <= above)
        {
            problems.Add(new OptionProblem(path, $"must be more than {Shown(above)}"));
        }

        if (Number(schema["maximum"]) is { } maximum && given > maximum)
        {
            problems.Add(new OptionProblem(path, $"must be at most {Shown(maximum)}"));
        }
    }

    private static string Shown(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static double? Number(JToken? token) =>
        token is { Type: JTokenType.Integer or JTokenType.Float } ? (double)token : null;

    private static int? Count(JObject schema, string key) => Number(schema[key]) is { } count ? (int)count : null;

    // An enum's strings match as the game reads them: trimmed, any case.
    private static bool Listed(JArray listed, JToken value)
    {
        string? text = Text(value)?.Trim();
        foreach (JToken entry in listed)
        {
            if (text != null && Text(entry) is { } listedText)
            {
                if (string.Equals(listedText, text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (JToken.DeepEquals(entry, value))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Words(JArray listed)
    {
        List<string> words = new List<string>(listed.Count);
        foreach (JToken entry in listed)
        {
            words.Add(Text(entry) ?? entry.ToString(Newtonsoft.Json.Formatting.None));
        }

        return words;
    }

    private static string? Text(JToken? token) => token?.Type == JTokenType.String ? (string)token! : null;

    private static string TypeWords(IReadOnlyList<string> types)
    {
        StringBuilder words = new StringBuilder();
        for (int index = 0; index < types.Count; index++)
        {
            if (index > 0)
            {
                words.Append(index == types.Count - 1 ? " or " : ", ");
            }

            words.Append(types[index] switch
            {
                "string" => "a string",
                "integer" => "an integer",
                "number" => "a number",
                "boolean" => "true or false",
                "array" => "a list",
                "object" => "an object",
                string other => other
            });
        }

        return words.ToString();
    }
}
