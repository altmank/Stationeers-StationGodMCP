#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The Python stub per what (help {verb, what, lang: "python"}; sg.help(verb, what)), generated from catalogue/verbs:
/// the options a script takes (x-script not drop), typed, with the script text where x-script gives one; the
/// what's script text; its reply shape with the small types written out in place and job views named; an example.
/// </summary>
internal static class VerbStubs
{
    internal const int Width = 150;

    // SPEC-NOTES note 28 (D13): in a script a per-id what answers the list wrapper whether given one id or a list.
    internal const string IdListLine =
        "In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.";

    internal const string PlacedLine =
        "placed: list[str] on a job reply, the new ids (a run's log.placed, a structure job's result.placed; a brief " +
        "poll counts them in log.placed_count); int on a run report, the cells it would build.";

    private static readonly Regex SimpleWord = new(@"^[A-Za-z_][\w+\-]*$");

    internal static string Stub(JsonObject verb, string what)
    {
        string name = VerbCatalogue.NameOf(verb);
        JsonObject source = verb["whats"]![what]!.AsObject();
        StringBuilder text = new();
        bool hasWhats = VerbCatalogue.HasWhats(verb);
        text.Append("def ").Append(name).Append(hasWhats ? "(what: Literal['" + what + "'], *,\n" : "(*,\n");
        List<(string Name, JsonObject Option)> options = VerbCatalogue.OptionsOf(source).ToList();
        if (name != "help")
        {
            options.Insert(0, ("fields", VerbCatalogue.SharedOption("fields")));
        }

        foreach ((string option, JsonObject resolved) in options)
        {
            string script = (string)resolved["x-script"]!;
            if (script == "drop")
            {
                continue;
            }

            string comment = script == "same" ? (string?)resolved["description"] ?? "" : script;
            text.Append("    ").Append(option).Append(": ").Append(VerbText.PythonType(resolved["schema"]!.AsObject()));
            if ((bool?)resolved["required"] != true)
            {
                string? fallback = VerbText.DefaultText(resolved);
                string literal = Default(fallback);
                if (literal == "None" && fallback != null)
                {
                    comment += " (default " + fallback + ")";
                }

                text.Append(" = ").Append(literal);
            }

            text.Append(",");
            if (comment.Length > 0)
            {
                text.Append("  # ").Append(comment);
            }

            text.Append('\n');
        }

        JsonObject? reply = source["reply"] as JsonObject;
        string? mainList = (string?)reply?["list"];
        text.Append(") -> dict:");
        if (mainList != null)
        {
            text.Append("  # main list: r['").Append(mainList).Append("']; iterating or indexing it walks every page, in order");
        }

        text.Append('\n');
        string summary = (string?)source["x-script-summary"] ?? VerbText.Sentence((string?)source["summary"] ?? name);
        text.Append("    \"\"\"").Append(summary).Append('\n');
        foreach (string line in VerbText.Lines(source["x-script-text"] ?? source["text"]))
        {
            text.Append("    ").Append(line).Append('\n');
        }

        if ((string?)VerbCatalogue.WriteOf(source)?["route"] == "confirm")
        {
            text.Append("    Reports in a report run; acts in a confirmed run.\n");
        }

        if (VerbCatalogue.WriteOf(source)?["cheat"] is JsonValue cheat)
        {
            text.Append((string)cheat! == "always"
                ? "    Cheat: ask the user first.\n"
                : "    Cheat when " + (string)cheat! + ": ask the user first.\n");
        }

        if (verb["reply_top"]?["placed"] != null)
        {
            text.Append("    ").Append(PlacedLine).Append('\n');
        }

        if (reply != null && reply["variants"] is JsonArray { Count: > 0 } variants)
        {
            AppendReply(text, reply, variants);
        }

        if (source["example"] is JsonObject example)
        {
            List<string> args = new();
            if (hasWhats)
            {
                args.Add("'" + what + "'");
            }

            Dictionary<string, JsonObject> byName = options.ToDictionary(option => option.Name, option => option.Option);
            foreach ((string key, JsonNode? value) in example)
            {
                if (byName.TryGetValue(key, out JsonObject? option) && (string)option["x-script"]! == "drop")
                {
                    continue;
                }

                args.Add(key + "=" + VerbText.PythonLiteral(value));
            }

            text.Append("    Example: sg.").Append(name).Append('(').Append(string.Join(", ", args)).Append(")\n");
        }

        if (VerbCatalogue.TakesIdList(source))
        {
            text.Append("    ").Append(IdListLine).Append('\n');
        }

        text.Append("    \"\"\"\n");
        return text.ToString();
    }

    private static void AppendReply(StringBuilder text, JsonObject reply, JsonArray variants)
    {
        JsonObject smallTypes = VerbCatalogue.Shared["types"]!.AsObject();
        text.Append(variants.Count > 1 ? "    Reply, one of:\n" : "    Reply:\n");
        List<string> jobViews = new();
        foreach (JsonNode? node in variants)
        {
            JsonObject variant = node!.AsObject();
            if (variants.Count > 1)
            {
                text.Append("    (").Append((string)(variant["view"] ?? variant["label"])!).Append(")\n");
            }

            if (variant["job_view"] is JsonValue job)
            {
                text.Append("      ").Append((string)job!).Append('\n');
                jobViews.Add((string)job!);
                continue;
            }

            foreach ((string key, JsonNode? type) in variant["keys"]!.AsObject())
            {
                foreach (string line in VerbText.Wrap("      " + key + ": ", (string)type!, "          ", Width))
                {
                    text.Append(line).Append('\n');
                }
            }
        }

        List<string> used = VerbText.Lines(reply["types"]).ToList();
        List<string> small = used.Where(type => smallTypes[type] != null).ToList();
        if (small.Count > 0)
        {
            string types = string.Join("; ", small.Select(type => type + " = " + (string)smallTypes[type]!));
            foreach (string line in VerbText.Wrap("    Types: ", types, "        ", Width))
            {
                text.Append(line).Append('\n');
            }
        }

        jobViews.AddRange(used.Where(type => smallTypes[type] == null));
        if (jobViews.Count > 0)
        {
            string names = string.Join(", ", jobViews.Distinct().OrderBy(view => view, System.StringComparer.Ordinal));
            foreach (string line in VerbText.Wrap(
                         "    Job views, one per help call (help {topic: \"types\", subtopic: \"<name>\"} or sg.help('types', '<name>')): ",
                         names, "        ", Width))
            {
                text.Append(line).Append('\n');
            }
        }
    }

    private static string Default(string? text)
    {
        if (text == null)
        {
            return "None";
        }

        if (text == "true" || text == "false")
        {
            return text == "true" ? "True" : "False";
        }

        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return text;
        }

        return SimpleWord.IsMatch(text) ? "'" + text + "'" : "None";
    }
}
