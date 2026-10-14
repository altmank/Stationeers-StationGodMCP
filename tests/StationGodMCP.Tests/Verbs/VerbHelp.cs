#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The direct help pages, generated from catalogue/verbs: help {} (the index), help {verb} (its whats), help {verb,
/// what} (options, notes, reply keys, an example) and help {topic}. The reply line is written from the what's
/// declared reply shape, so the two cannot drift.
/// </summary>
internal static class VerbHelp
{
    internal const string IdListLine =
        "Any what that takes an id takes a list: one row per id, {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the one-id reply; a bad id is a row with error.";

    internal const string PlacedLine =
        "placed: on a job reply the new ids (a run's log.placed, a structure job's result.placed; a brief poll counts " +
        "them in log.placed_count), on a run report a count of the cells it would build.";

    internal static string Index()
    {
        StringBuilder text = new("Verbs (each a tool; most take what):\n");
        foreach (JsonObject verb in VerbCatalogue.Verbs)
        {
            string description = (string)verb["description"]!;
            int stop = description.IndexOf(". ", System.StringComparison.Ordinal);
            text.Append("- ").Append(VerbCatalogue.NameOf(verb)).Append(": ")
                .Append(stop < 0 ? description.Split('\n')[0] : description.Substring(0, stop + 1)).Append('\n');
        }

        text.Append('\n').Append("Topics: ").Append(string.Join(", ", TopicNames())).Append('\n');
        text.Append("help {verb} lists a verb's whats; help {verb, what} gives one in full; help {topic} gives a topic.\n");
        return text.ToString();
    }

    internal const string CheatsTopic = "cheats";

    /// <summary>The topic names help {} lists: the shared topics, and cheats (generated from the write rules).</summary>
    internal static IEnumerable<string> TopicNames() =>
        VerbCatalogue.Shared["topics"]!.AsObject().Select(pair => pair.Key).Where(key => !key.Contains('.'))
            .Append(CheatsTopic);

    /// <summary>Every topic key help {topic[, subtopic]} answers.</summary>
    internal static IEnumerable<string> TopicKeys() =>
        VerbCatalogue.Shared["topics"]!.AsObject().Select(pair => pair.Key).Append(CheatsTopic);

    internal static string Topic(string key) => key == CheatsTopic ? Cheats() : (string)VerbCatalogue.Shared["topics"]![key]!;

    /// <summary>The line every page of a writing verb carries: what a report answers (shared report_top).</summary>
    internal static string ReportLine() =>
        "A report carries " + string.Join(", ", VerbCatalogue.Shared["report_top"]!.AsObject().Select(pair => pair.Key + ": true")) +
        " and lists every target with its current and new value.";

    /// <summary>The cheats: every what whose write rules declare cheat, always or under an option.</summary>
    internal static string Cheats()
    {
        StringBuilder text = new("A cheat changes the world in a way a player could not. Ask the user first. A cheat reports until confirm: true.\n");
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            if (VerbCatalogue.WriteOf(source)?["cheat"] is JsonValue cheat)
            {
                string call = what == VerbCatalogue.WhatlessKey ? verb : verb + " " + what;
                text.Append("- ").Append(call).Append((string)cheat! == "always" ? "" : " with " + (string)cheat!)
                    .Append(": ").Append((string)source["summary"]!).Append('\n');
            }
        }

        return text.ToString();
    }

    internal static string VerbPage(JsonObject verb)
    {
        string name = VerbCatalogue.NameOf(verb);
        if (!VerbCatalogue.HasWhats(verb))
        {
            return WhatPage(verb, VerbCatalogue.WhatlessKey);
        }

        StringBuilder text = new();
        text.Append((string)verb["description"]!).Append('\n');
        foreach (string line in VerbText.Lines(verb["text"]))
        {
            text.Append(line).Append('\n');
        }

        if (VerbCatalogue.WhatsOf(verb).Any(what => VerbCatalogue.WriteOf(what.What) != null))
        {
            text.Append(ReportLine()).Append('\n');
        }

        text.Append("\nwhats:\n");
        List<HashSet<string>> optionSets = new();
        foreach ((string what, JsonObject source) in VerbCatalogue.WhatsOf(verb))
        {
            string? cheat = (string?)VerbCatalogue.WriteOf(source)?["cheat"];
            text.Append("- ").Append(what).Append(": ").Append((string)source["summary"]!)
                .Append(cheat == "always" ? " (cheat)" : "").Append('\n');
            optionSets.Add(new HashSet<string>(VerbCatalogue.OptionsOf(source).Select(option => option.Name)));
        }

        List<string> common = optionSets.Skip(1).Aggregate(new HashSet<string>(optionSets[0]),
            (all, next) => { all.IntersectWith(next); return all; }).OrderBy(option => option, System.StringComparer.Ordinal).ToList();
        text.Append('\n');
        if (common.Count > 0)
        {
            text.Append("Every what here takes: ").Append(string.Join(", ", common)).Append(".\n");
        }

        if (name != "help" && !VerbCatalogue.IsHubOnly(verb))
        {
            text.Append("Shaping on every call: ").Append(string.Join(", ", VerbCatalogue.Shaping))
                .Append(" (help {topic: \"shaping\"}).\n");
        }

        text.Append("help {verb: \"").Append(name).Append("\", what} gives a what's options, reply keys and an example.\n");
        return text.ToString();
    }

    internal static string WhatPage(JsonObject verb, string what)
    {
        string name = VerbCatalogue.NameOf(verb);
        JsonObject source = verb["whats"]![what]!.AsObject();
        JsonObject? write = VerbCatalogue.WriteOf(source);
        StringBuilder text = new();
        if (source["summary"] is JsonValue summary)
        {
            text.Append(VerbText.Sentence((string)summary!)).Append('\n');
        }

        if ((string?)write?["route"] == "confirm")
        {
            text.Append("Reports first; confirm: true does it.\n");
        }

        if (write?["cheat"] is JsonValue cheat)
        {
            text.Append((string)cheat! == "always" ? "Cheat.\n" : "Cheat when " + (string)cheat! + ".\n");
        }

        List<(string Name, JsonObject Option)> options = VerbCatalogue.OptionsOf(source).ToList();
        if (options.Count > 0)
        {
            text.Append("Options:\n");
            foreach ((string option, JsonObject resolved) in options)
            {
                text.Append("- ").Append(option).Append(" (").Append(VerbText.DirectType(resolved["schema"]!.AsObject()));
                if (VerbText.DefaultText(resolved) is { } fallback)
                {
                    text.Append(", default ").Append(fallback);
                }

                if ((bool?)resolved["required"] == true)
                {
                    text.Append(", required");
                }

                text.Append("): ").Append((string?)resolved["description"] ?? "").Append('\n');
            }
        }

        foreach (string line in VerbText.Lines(source["text"]))
        {
            text.Append(line).Append('\n');
        }

        if (VerbCatalogue.TakesIdList(source))
        {
            text.Append(IdListLine).Append('\n');
        }

        if (verb["reply_top"]?["placed"] != null)
        {
            text.Append(PlacedLine).Append('\n');
        }

        if (source["reply"] is JsonObject reply && reply["variants"] is JsonArray { Count: > 0 })
        {
            text.Append("Reply: ").Append(ReplyLine(reply)).Append('\n');
        }

        if (source["example"] is JsonObject example)
        {
            JsonObject args = new();
            if (VerbCatalogue.HasWhats(verb))
            {
                args["what"] = what;
            }

            foreach ((string key, JsonNode? value) in example)
            {
                args[key] = value?.DeepClone();
            }

            text.Append("Example: ").Append(name).Append(' ').Append(VerbText.DirectLiteral(args)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The reply keys, per variant, with one level of nested keys for objects and lists of objects.</summary>
    internal static string ReplyLine(JsonObject reply)
    {
        JsonObject smallTypes = VerbCatalogue.Shared["types"]!.AsObject();
        JsonArray variants = reply["variants"]!.AsArray();
        List<string> parts = new();
        foreach (JsonNode? node in variants)
        {
            JsonObject variant = node!.AsObject();
            string label = variants.Count > 1 ? "(" + (string)(variant["label"] ?? variant["view"])! + ") " : "";
            if (variant["job_view"] is JsonValue job)
            {
                parts.Add(label + "a " + (string)job! + " (help {topic: \"types\", subtopic: \"" + (string)job! + "\"})");
                continue;
            }

            List<string> keys = new();
            foreach ((string key, JsonNode? type) in variant["keys"]!.AsObject())
            {
                string? nested = type is JsonValue t ? VerbText.NestedObject((string)t!, smallTypes) : null;
                List<string> inner = nested == null ? new List<string>() : VerbText.ObjectKeys(nested);
                bool list = ((string?)type ?? "").StartsWith("list[");
                keys.Add(inner.Count == 0
                    ? key
                    : key + (list ? " [{" : " {") + string.Join(", ", inner) + (list ? "}]" : "}"));
            }

            parts.Add(label + string.Join(", ", keys));
        }

        return string.Join(" | ", parts);
    }
}
