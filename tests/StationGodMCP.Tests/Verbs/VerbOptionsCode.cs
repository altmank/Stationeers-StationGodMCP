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
/// (verb, what) in verb option names, made of the shared option kinds (Selector, Fields, Paging, ReportConfirm on a what
/// that takes confirm, MaterialsSource; the per-id list form inside Selector) and the what's own options typed by their
/// schema, with the what's spec (options and schemas, main list, id_list, write rules) the option check reads; and
/// VerbSurface.Generated.cs, the verbs in catalogue order. A what the hub answers itself ("hub": true) gets none: the
/// mod never dispatches it, and a verb every what of which is hub-answered (run_script) is left out of the surface. The
/// hub's own options (max_wait, file options) are in the spec, so the check can name them, and in no class.
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
            List<string> specs = new();
            foreach ((string what, JsonObject source) in VerbCatalogue.WhatsOf(verb))
            {
                if (VerbCatalogue.IsHub(source))
                {
                    continue;
                }

                string type = ClassName(name, what);
                files[type + ".cs"] = OptionsClass(verb, name, what, source, type);
                specs.Add(type + ".Spec");
            }

            if (specs.Count == 0)
            {
                continue;
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
        code.Append("using System.Collections.Generic;\nusing Newtonsoft.Json.Linq;\nusing StationGodMCP.Pure.Catalogue;\n\n");
        code.Append("namespace ").Append(Namespace).Append(";\n\n");
        code.Append("/// <summary>The options of ").Append(label).Append(", in verb names (catalogue/verbs/")
            .Append(verbName).Append(".json).</summary>\n");
        code.Append("internal sealed class ").Append(type).Append("\n{\n");
        code.Append("    internal static WhatSpec Spec { get; } = new WhatSpec(").Append(Literal(verbName)).Append(", ")
            .Append(Literal(what)).Append(", ").Append(Literal((string?)source["reply"]?["list"])).Append(", ")
            .Append(Literal((string?)source["id_list"])).Append(", ").Append(WriteSpec(source)).Append(", ")
            .Append(CostSpec(source)).Append(", ").Append(ReportCostSpec(source)).Append(", ")
            .Append(DefaultLimitsSpec(source));
        foreach ((string name, JsonObject option) in options)
        {
            code.Append(",\n        ").Append(OptionSpec(name, option));
        }

        code.Append(");\n");

        List<(string Property, string Type, string Bind, string? Empty)> members = new();
        HashSet<string> inKinds = new(StringComparer.Ordinal);
        foreach ((string kind, string kindType, string empty, string[] names) in Kinds)
        {
            if (Binds(kind, names, taken, verbName))
            {
                members.Add((kind, kindType, kindType + ".Bind(options)", kindType + "." + empty));
                inKinds.UnionWith(names);
            }
        }

        foreach ((string name, JsonObject option) in options)
        {
            if (inKinds.Contains(name) || hubOnly.Contains(name))
            {
                continue;
            }

            JsonObject schema = option["schema"] as JsonObject ?? new JsonObject();
            (string propertyType, string read) = Typed(schema);
            string bind = "options." + read + "(" + Literal(name) + ")";
            if (DefaultRead(read, schema, option) is { } fallback)
            {
                bind += " ?? options." + fallback + "(" + Literal(name) + ")";
            }

            members.Add((Pascal(name), propertyType, bind, null));
        }

        foreach ((string property, string propertyType, string _, string? empty) in members)
        {
            code.Append("\n    internal ").Append(propertyType).Append(' ').Append(property)
                .Append(" { get; private set; }").Append(empty == null ? "\n" : " = " + empty + ";\n");
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

    // Whether a what's options take a shared kind: Fields always (the call's shape) but on help; ReportConfirm only on a
    // what that takes confirm, so rows on a read-only what is its own option; every other kind on any of its options.
    private static bool Binds(string kind, string[] names, HashSet<string> taken, string verbName) => kind switch
    {
        "Fields" => verbName != "help" || names.Any(taken.Contains),
        "Report" => taken.Contains("confirm"),
        _ => names.Any(taken.Contains)
    };

    // max_wait and a file option (code_file: a path the hub reads into code) are the hub's: the game never gets them.
    private static bool IsHubOption(string name, HashSet<string> taken) =>
        name == "max_wait" || (name.EndsWith("_file", StringComparison.Ordinal) && taken.Contains(name[..^"_file".Length]));

    // The reader's method for an own option's catalogue default, when the default is a value of the option's type (a
    // flag, a number, a word its enum takes, or a list "[a, b]" of words its items' enum takes): the default of the form the call takes (x-default-when) fills the
    // option left out. Prose defaults ("every supplier") bind nothing.
    private static string? DefaultRead(string read, JsonObject schema, JsonObject option)
    {
        if (option["default"] is not JsonValue given || !given.TryGetValue(out string? text))
        {
            return null;
        }

        return read switch
        {
            "Flag" when text is "true" or "false" => "DefaultFlag",
            "Integer" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "DefaultInteger",
            "Number" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => "DefaultNumber",
            "Text" when schema["enum"] is JsonArray words && words.Any(word => (string?)word == text) => "DefaultText",
            "Words" when text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal) &&
                         schema["items"]?["enum"] is JsonArray allowed &&
                         StationGodMCP.Pure.Verbs.OptionReader.WordsOf(text).All(word => allowed.Any(entry => (string?)entry == word)) =>
                "DefaultWords",
            _ => null
        };
    }

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

        if (option["x-default-when"] is JsonArray forms)
        {
            spec.Append(", ").Append(Literal(forms.ToJsonString(VerbText.Compact)));
        }

        return spec.Append(')').ToString();
    }

    // x-cost: new WhatCost(CostClass.World), with the per options and the duration where it states them.
    private static string CostSpec(JsonObject source) =>
        source["x-cost"] is JsonObject cost ? CostCode(cost) : "null";

    // x-cost-report: what a report call of the what counts as; null where the what states none (a report counts as
    // x-cost then).
    private static string ReportCostSpec(JsonObject source) =>
        source["x-cost-report"] is JsonObject report ? CostCode(report) : "null";

    // x-default-limits: the most entries the writer keeps of each named top-level list when the call does not say;
    // null where the what states none.
    private static string DefaultLimitsSpec(JsonObject source) =>
        source["x-default-limits"] is JsonObject limits
            ? "new Dictionary<string, int> { " + string.Join(", ", limits.Select(limit =>
                "[" + Literal(limit.Key) + "] = " + ((int)limit.Value!).ToString(CultureInfo.InvariantCulture))) + " }"
            : "null";

    private static string CostCode(JsonObject cost)
    {
        StringBuilder spec = new StringBuilder("new WhatCost(CostClass.").Append(Pascal((string)cost["class"]!));
        List<string> per = cost["per"] switch
        {
            JsonArray many => many.Select(option => (string)option!).ToList(),
            JsonValue one => new List<string> { (string)one! },
            _ => new List<string>()
        };
        if (per.Count > 0)
        {
            spec.Append(", new[] { ").Append(string.Join(", ", per.Select(option => Literal(option)))).Append(" }");
        }

        if (cost["duration"] is JsonObject duration)
        {
            spec.Append(per.Count > 0 ? ", " : ", null, ").Append("new CostDuration(")
                .Append(Literal((string)duration["option"]!)).Append(", ")
                .Append(((double)duration["max_s"]!).ToString("R", CultureInfo.InvariantCulture)).Append(')');
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
