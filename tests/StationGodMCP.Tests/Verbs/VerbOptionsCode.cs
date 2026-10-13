#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The mod's typed options, generated from catalogue/verbs into src/StationGodMCP.Mod/Pure/Verbs/Generated: one class per
/// (verb, what) in verb option names, made of the shared option kinds (Selector, Fields, Paging, ReportConfirm,
/// MaterialsSource; the per-id list form inside Selector) and the what's own options typed by their schema, with the
/// what's spec (options and schemas, main list, id_list, write rules) the option check reads; and
/// VerbSurface.Generated.cs, the verbs in catalogue order. run_script is the hub's own and gets none. The hub's own
/// options (max_wait, file options) are in the spec, so the check can name them, and in no class.
/// </summary>
internal static class VerbOptionsCode
{
    private const string Namespace = "StationGodMCP.Pure.Verbs";

    /// <summary>The shaping options the game takes as the call's shape; the rest of _shared.json's shaping is the hub's.</summary>
    private static readonly string[] GameShaping = { "fields", "omit", "list_limits" };

    private static readonly (string Kind, string Type, string Empty, string[] Options)[] Kinds =
    {
        ("Selector", "Selector", "None", new[] { "id", "near", "radius", "within", "box", "room", "network", "prefab", "name" }),
        ("Fields", "Fields", "None", new[] { "include", "verbose" }),
        ("Paging", "Paging", "All", new[] { "limit", "offset", "order", "cursor" }),
        ("Report", "ReportConfirm", "Acting", new[] { "confirm", "rows" }),
        ("Materials", "MaterialsSource", "Player", new[] { "materials", "refund_to" })
    };

    /// <summary>The generated files, by file name under Generated.</summary>
    internal static SortedDictionary<string, string> Files()
    {
        SortedDictionary<string, string> files = new(StringComparer.Ordinal);
        StringBuilder table = new();
        foreach (JsonObject verb in VerbCatalogue.Verbs)
        {
            string name = VerbCatalogue.NameOf(verb);
            if (name == "run_script")
            {
                continue;
            }

            List<string> specs = new();
            foreach ((string what, JsonObject source) in VerbCatalogue.WhatsOf(verb))
            {
                string type = ClassName(name, what);
                files[type + ".cs"] = OptionsClass(verb, name, what, source, type);
                specs.Add(type + ".Spec");
            }

            table.Append("        new VerbSpec(").Append(Literal(name)).Append(", ")
                .Append((bool?)verb["read_only"] ?? true ? "true" : "false");
            foreach (string spec in specs)
            {
                table.Append(",\n            ").Append(spec);
            }

            table.Append("),\n");
        }

        files["VerbSurface.Generated.cs"] = Surface(table.ToString());
        return files;
    }

    internal static string ClassName(string verb, string what) =>
        Pascal(verb) + (what == VerbCatalogue.WhatlessKey ? string.Empty : Pascal(what)) + "Options";

    private static string Surface(string table)
    {
        StringBuilder code = Header();
        code.Append("using System.Collections.Generic;\n\n");
        code.Append("namespace ").Append(Namespace).Append(";\n\n");
        code.Append("internal static partial class VerbSurface\n{\n");
        code.Append("    /// <summary>The shaping options the game takes (the call's shape); the hub takes the rest.</summary>\n");
        code.Append("    internal static IReadOnlyList<OptionSpec> Shaping { get; } = new[]\n    {\n");
        foreach (string name in GameShaping)
        {
            code.Append("        ").Append(OptionSpec(name, VerbCatalogue.SharedOption(name))).Append(",\n");
        }

        code.Append("    };\n\n");
        code.Append("    /// <summary>A shaping option by name; null for any other name.</summary>\n");
        code.Append("    internal static OptionSpec? ShapingOption(string name) => name switch\n    {\n");
        for (int index = 0; index < GameShaping.Length; index++)
        {
            code.Append("        ").Append(Literal(GameShaping[index])).Append(" => Shaping[").Append(index)
                .Append("],\n");
        }

        code.Append("        _ => null\n    };\n\n");
        code.Append("    private static IReadOnlyList<VerbSpec> Generated() => new[]\n    {\n");
        code.Append(table);
        code.Append("    };\n}\n");
        return code.ToString();
    }

    private static string OptionsClass(JsonObject verb, string verbName, string what, JsonObject source, string type)
    {
        List<(string Name, JsonObject Option)> options = VerbCatalogue.WireOptionsOf(source).ToList();
        HashSet<string> taken = new(options.Select(option => option.Name), StringComparer.Ordinal);
        HashSet<string> hubOnly = new(options.Where(option => IsHubOption(option.Name, taken)).Select(option => option.Name));
        string label = what == VerbCatalogue.WhatlessKey ? verbName : verbName + " " + what;

        StringBuilder code = Header();
        code.Append("using System.Collections.Generic;\nusing Newtonsoft.Json.Linq;\n\n");
        code.Append("namespace ").Append(Namespace).Append(";\n\n");
        code.Append("/// <summary>The options of ").Append(label).Append(", in verb names (catalogue/verbs/")
            .Append(verbName).Append(".json).</summary>\n");
        code.Append("internal sealed record ").Append(type).Append("\n{\n");
        code.Append("    internal static WhatSpec Spec { get; } = new WhatSpec(").Append(Literal(verbName)).Append(", ")
            .Append(Literal(what)).Append(", ").Append(Literal((string?)source["reply"]?["list"])).Append(", ")
            .Append(Literal((string?)source["id_list"])).Append(", ").Append(WriteSpec(source));
        foreach ((string name, JsonObject option) in options)
        {
            code.Append(",\n        ").Append(OptionSpec(name, option));
        }

        code.Append(");\n");

        List<(string Property, string Type, string Bind, string? Empty)> members = new();
        foreach ((string kind, string kindType, string empty, string[] names) in Kinds)
        {
            bool shaping = kind == "Fields" && verbName != "help";
            if (shaping || names.Any(taken.Contains))
            {
                members.Add((kind, kindType, kindType + ".Bind(options)", kindType + "." + empty));
            }
        }

        HashSet<string> inKinds = new(Kinds.SelectMany(kind => kind.Options), StringComparer.Ordinal);
        foreach ((string name, JsonObject option) in options)
        {
            if (inKinds.Contains(name) || hubOnly.Contains(name))
            {
                continue;
            }

            (string propertyType, string read) = Typed(option["schema"] as JsonObject ?? new JsonObject());
            members.Add((Pascal(name), propertyType, "options." + read + "(" + Literal(name) + ")", null));
        }

        foreach ((string property, string propertyType, string _, string? empty) in members)
        {
            code.Append("\n    internal ").Append(propertyType).Append(' ').Append(property)
                .Append(" { get; private init; }").Append(empty == null ? "\n" : " = " + empty + ";\n");
        }

        code.Append("\n    internal static ").Append(type).Append(" Bind(OptionReader options) => new ").Append(type)
            .Append("\n    {\n");
        foreach ((string property, string _, string bind, string? _) in members)
        {
            code.Append("        ").Append(property).Append(" = ").Append(bind).Append(",\n");
        }

        code.Append("    };\n}\n");
        return code.ToString();
    }

    // max_wait and a file option (code_file: a path the hub reads into code) are the hub's: the game never gets them.
    private static bool IsHubOption(string name, HashSet<string> taken) =>
        name == "max_wait" || (name.EndsWith("_file", StringComparison.Ordinal) && taken.Contains(name[..^"_file".Length]));

    // The property type and the reader's method for an own option, by its schema.
    private static (string Type, string Read) Typed(JsonObject schema)
    {
        List<string> types = VerbText.TypesOf(schema);
        List<string> items = schema["items"] is JsonObject itemSchema ? VerbText.TypesOf(itemSchema) : new List<string>();
        bool words = items.Count == 1 && items[0] == "string";
        return types switch
        {
            ["string"] => ("string?", "Text"),
            ["integer"] => ("long?", "Integer"),
            ["number"] => ("double?", "Number"),
            ["boolean"] => ("bool?", "Flag"),
            ["array"] when words => ("IReadOnlyList<string>?", "Words"),
            ["string", "array"] when words => ("IReadOnlyList<string>?", "Words"),
            _ => ("JToken?", "Raw")
        };
    }

    private static string WriteSpec(JsonObject source)
    {
        if (VerbCatalogue.WriteOf(source) is not { } write)
        {
            return "null";
        }

        string undo = (string)write["x-undo"]!;
        string mark = (string?)write["x-undo-mark"] ?? (undo == "undo" ? "[undo]" : "[cannot undo]");
        return "new WriteSpec(" + Literal((string)write["route"]!) + ", " + Literal(undo) + ", " + Literal(mark) + ")";
    }

    private static string OptionSpec(string name, JsonObject option)
    {
        JsonObject schema = option["schema"] as JsonObject ?? new JsonObject();
        StringBuilder spec = new();
        spec.Append("new OptionSpec(").Append(Literal(name)).Append(", ")
            .Append(Literal(schema.ToJsonString(VerbText.Compact)));
        bool required = (bool?)option["required"] ?? false;
        string? defaultText = option["default"] switch
        {
            null => null,
            JsonValue value when value.TryGetValue(out string? text) => text,
            JsonNode other => other.ToJsonString(VerbText.Compact)
        };
        if (required || defaultText != null)
        {
            spec.Append(", ").Append(required ? "true" : "false");
        }

        if (defaultText != null)
        {
            spec.Append(", ").Append(Literal(defaultText));
        }

        return spec.Append(')').ToString();
    }

    private static StringBuilder Header() => new StringBuilder()
        .Append("// <auto-generated>\n")
        .Append("// Generated from catalogue/verbs by tests/StationGodMCP.Tests/Verbs/VerbOptionsCode.cs; do not edit.\n")
        .Append("// STATIONGOD_WRITE_CATALOGUE=1 and VerbCatalogueTests.GeneratedFilesAreCurrent rewrite it.\n")
        .Append("// </auto-generated>\n")
        .Append("#nullable enable\n\n");

    private static string Pascal(string name)
    {
        StringBuilder pascal = new(name.Length);
        foreach (string part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            pascal.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
        }

        return pascal.ToString();
    }

    private static string Literal(string? text)
    {
        if (text == null)
        {
            return "null";
        }

        StringBuilder literal = new(text.Length + 2);
        literal.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"':
                    literal.Append("\\\"");
                    break;
                case '\\':
                    literal.Append("\\\\");
                    break;
                case '\n':
                    literal.Append("\\n");
                    break;
                case '\r':
                    literal.Append("\\r");
                    break;
                case '\t':
                    literal.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        literal.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        literal.Append(c);
                    }

                    break;
            }
        }

        return literal.Append('"').ToString();
    }
}
