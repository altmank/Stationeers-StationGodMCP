#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests.Verbs;

/// <summary>Small text helpers the verb generators share: type texts, literals, reply shapes and wrapping.</summary>
internal static class VerbText
{
    internal static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    /// <summary>A node built in code, as parsed JSON (the catalogue's layout writer takes parsed values).</summary>
    internal static JsonNode Parsed(JsonNode node) => JsonNode.Parse(node.ToJsonString(Compact))!;

    internal static string Capitalized(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    internal static string Sentence(string text) => text.EndsWith('.') ? Capitalized(text) : Capitalized(text) + ".";

    internal static IEnumerable<string> Lines(JsonNode? lines) =>
        lines is JsonArray array ? array.Select(line => (string)line!) : Array.Empty<string>();

    internal static List<string> TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue single => new List<string> { (string)single! },
        JsonArray many => many.Select(type => (string)type!).ToList(),
        _ => new List<string>()
    };

    private static string Raw(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue(out string? text) ? text : value?.ToJsonString(Compact) ?? "null";

    /// <summary>
    /// An option's default as help and the stubs state it: the default, then each x-default-when form. A boolean default
    /// reads "true (false with compact)"; any other "30; box 25; summarize 10" (a form's condition reads as the option's
    /// name when it is present or true, else as the option and its value). Null when the option gives no default.
    /// </summary>
    internal static string? DefaultText(JsonObject option)
    {
        if (option["default"] is not JsonValue given)
        {
            return null;
        }

        string text = (string)given!;
        if (option["x-default-when"] is not JsonArray forms || forms.Count == 0)
        {
            return text;
        }

        List<string> others = forms.Select(form => (Condition: ConditionText(form!["when"]!.AsObject()),
            Default: (string)form["default"]!)).Select(form => text is "true" or "false"
            ? form.Default + " with " + form.Condition
            : form.Condition + " " + form.Default).ToList();
        return text is "true" or "false"
            ? text + " (" + string.Join("; ", others) + ")"
            : text + "; " + string.Join("; ", others);
    }

    private static string ConditionText(JsonObject when) => string.Join(" and ", when.Select(condition =>
        condition.Value switch
        {
            JsonObject test when test["present"] is JsonValue present && (bool)present! => condition.Key,
            JsonValue flag when flag.TryGetValue(out bool on) && on => condition.Key,
            JsonNode value => condition.Key + " " + Raw(value),
            null => condition.Key
        }));

    /// <summary>The direct page's type text: an enum's values, "list of a | b", or the JSON types joined with "or".</summary>
    internal static string DirectType(JsonObject schema)
    {
        if (schema["enum"] is JsonArray values)
        {
            return string.Join(" | ", values.Select(Raw));
        }

        List<string> types = TypesOf(schema);
        if (types.Count == 1 && types[0] == "array")
        {
            return schema["items"]?["enum"] is JsonArray items ? "list of " + string.Join(" | ", items.Select(Raw)) : "list";
        }

        return types.Count == 0 ? "any" : string.Join(" or ", types);
    }

    /// <summary>The Python type a stub gives an option.</summary>
    internal static string PythonType(JsonObject schema)
    {
        if (schema["enum"] is JsonArray values)
        {
            return "Literal[" + string.Join(", ", values.Select(PythonLiteral)) + "]";
        }

        List<string> parts = new();
        foreach (string type in TypesOf(schema))
        {
            parts.Add(type switch
            {
                "string" => "str",
                "integer" => "int",
                "number" => "float",
                "boolean" => "bool",
                "object" => "dict",
                "array" => schema["items"] is JsonObject items && items["enum"] is JsonArray choices
                    ? "list[Literal[" + string.Join(", ", choices.Select(PythonLiteral)) + "]]"
                    : schema["items"] is JsonObject plain && TypesOf(plain) is { Count: 1 } one && one[0] == "string"
                        ? "list[str]"
                        : "list",
                _ => "Any"
            });
        }

        return parts.Count == 0 ? "Any" : string.Join(" | ", parts.Distinct());
    }

    /// <summary>A JSON value written as a Python literal.</summary>
    internal static string PythonLiteral(JsonNode? value) => value switch
    {
        null => "None",
        JsonArray array => "[" + string.Join(", ", array.Select(PythonLiteral)) + "]",
        JsonObject obj => "{" + string.Join(", ", obj.Select(pair => "'" + pair.Key + "': " + PythonLiteral(pair.Value))) + "}",
        JsonValue v when v.TryGetValue(out bool flag) => flag ? "True" : "False",
        JsonValue v when v.TryGetValue(out string? text) => "'" + text!.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        _ => value.ToJsonString(Compact)
    };

    /// <summary>A JSON value written as the direct pages write arguments: bare keys, JSON values.</summary>
    internal static string DirectLiteral(JsonNode? value) => value switch
    {
        null => "null",
        JsonArray array => "[" + string.Join(", ", array.Select(DirectLiteral)) + "]",
        JsonObject obj => "{" + string.Join(", ", obj.Select(pair => pair.Key + ": " + DirectLiteral(pair.Value))) + "}",
        _ => value.ToJsonString(Compact)
    };

    /// <summary>The top-level keys of an object type text "{a: str, b: list[{c: int}]}".</summary>
    internal static List<string> ObjectKeys(string type)
    {
        List<string> keys = new();
        string body = type.Trim();
        if (!body.StartsWith('{') || !body.EndsWith('}'))
        {
            return keys;
        }

        foreach (string part in SplitTop(body.Substring(1, body.Length - 2)))
        {
            int colon = part.IndexOf(':');
            keys.Add((colon < 0 ? part : part.Substring(0, colon)).Trim());
        }

        return keys;
    }

    /// <summary>Splits on ", " outside brackets.</summary>
    internal static List<string> SplitTop(string text)
    {
        List<string> parts = new();
        int depth = 0;
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (c == '{' || c == '[' || c == '(')
            {
                depth++;
            }
            else if (c == '}' || c == ']' || c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text.Substring(start, index - start).Trim());
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            parts.Add(text.Substring(start).Trim());
        }

        return parts;
    }

    /// <summary>The object an item or value type names, for a reply line's nested keys: "{...}" of list[{...}] or {...}.</summary>
    internal static string? NestedObject(string type, JsonObject smallTypes)
    {
        string first = SplitAlternatives(type)[0];
        if (first.StartsWith("list[") && first.EndsWith(']'))
        {
            string inner = first.Substring(5, first.Length - 6);
            return inner.StartsWith('{') ? inner : smallTypes[inner] is JsonValue named ? (string)named! : null;
        }

        if (first.StartsWith('{'))
        {
            return first;
        }

        return smallTypes[first] is JsonValue small ? (string)small! : null;
    }

    private static List<string> SplitAlternatives(string type)
    {
        List<string> parts = new();
        int depth = 0;
        int start = 0;
        for (int index = 0; index < type.Length; index++)
        {
            char c = type[index];
            if (c == '{' || c == '[')
            {
                depth++;
            }
            else if (c == '}' || c == ']')
            {
                depth--;
            }
            else if (c == '|' && depth == 0)
            {
                parts.Add(type.Substring(start, index - start).Trim());
                start = index + 1;
            }
        }

        parts.Add(type.Substring(start).Trim());
        return parts;
    }

    /// <summary>Words of text laid out at most width columns, the first line after prefix and the rest after indent.</summary>
    internal static IEnumerable<string> Wrap(string prefix, string text, string indent, int width)
    {
        StringBuilder line = new(prefix);
        bool empty = true;
        foreach (string word in text.Split(' '))
        {
            if (!empty && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear().Append(indent).Append(word);
                continue;
            }

            if (!empty)
            {
                line.Append(' ');
            }

            line.Append(word);
            empty = false;
        }

        yield return line.ToString();
    }
}
