using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>One thing wrong with a verb call: where (an option name or path), what, and the nearest valid names.</summary>
internal sealed class OptionProblem
{
    internal OptionProblem(string path, string problem, List<string>? nearest = null)
    {
        Path = path;
        Problem = problem;
        Nearest = nearest is { Count: > 0 } ? nearest : null;
    }

    internal string Path { get; }

    internal string Problem { get; }

    internal List<string>? Nearest { get; }

    internal JsonObject ToJson()
    {
        JsonObject problem = new() { ["path"] = Path, ["problem"] = Problem };
        if (Nearest != null)
        {
            JsonArray names = [];
            foreach (string name in Nearest)
            {
                names.Add(name);
            }

            problem["nearest"] = names;
        }

        return problem;
    }

    /// <summary>
    /// Every problem at once as one invalid_argument: data {problems, valid?}, valid being the what's own options with
    /// the nearest three to an unknown name first.
    /// </summary>
    internal static VerbReply.Failed Refusal(string label, List<OptionProblem> problems, List<string>? valid = null)
    {
        StringBuilder message = new();
        message.Append(label).Append(": ");
        JsonArray list = [];
        for (int index = 0; index < problems.Count; index++)
        {
            OptionProblem problem = problems[index];
            if (index > 0)
            {
                message.Append("; ");
            }

            message.Append(problem.Path).Append(": ").Append(problem.Problem);
            if (problem.Nearest != null)
            {
                message.Append(" (nearest: ").Append(string.Join(", ", problem.Nearest)).Append(')');
            }

            list.Add(problem.ToJson());
        }

        message.Append('.');
        JsonObject data = new() { ["problems"] = list };
        if (valid is { Count: > 0 })
        {
            JsonArray names = [];
            foreach (string name in valid)
            {
                names.Add(name);
            }

            data["valid"] = names;
        }

        return new VerbReply.Failed(VerbFailure.InvalidArgument, message.ToString(), data: data);
    }
}

/// <summary>
/// Checks a verb call before anything is sent: the verb, the what and every option against the what's schema (types,
/// enum, bounds, list lengths, list items, required options), and the face's rules (hub.md, Faces): on the script face
/// options marked x-script drop are refused, confirm with "confirm belongs to the run"; the program face takes those
/// options' script set plus confirm. Every problem comes back at once, in verb names only.
/// </summary>
internal static class OptionCheck
{
    internal const string ConfirmBelongsToTheRun = "confirm belongs to the run";

    /// <summary>The verb and what the call names, or the invalid_argument that lists every problem with it.</summary>
    internal static VerbReply.Failed? Check(VerbCatalogue catalogue, VerbCall call, out VerbEntry? verb, out WhatEntry? what)
    {
        what = null;
        verb = catalogue.Verb(call.Verb);
        List<OptionProblem> problems = [];
        if (verb == null)
        {
            problems.Add(new OptionProblem("verb", $"'{call.Verb}' is not a verb", Nearest.Of(call.Verb, catalogue.VerbNames)));
            return OptionProblem.Refusal(call.Verb, problems);
        }

        if (!verb.HasWhats && call.What != null)
        {
            problems.Add(new OptionProblem("what", $"{verb.Name} takes no what"));
            return OptionProblem.Refusal(verb.Name, problems);
        }

        what = verb.What(call.What);
        if (what == null)
        {
            problems.Add(call.What == null
                ? new OptionProblem("what", "required: one of " + string.Join(", ", verb.WhatNames))
                : new OptionProblem("what", $"'{call.What}' is not a what of {verb.Name}", Nearest.Of(call.What, verb.WhatNames)));
            return OptionProblem.Refusal(verb.Name, problems);
        }

        List<string> valid = ValidNames(verb, what);
        string? unknownName = null;
        foreach ((string name, JsonNode? value) in call.Args)
        {
            if (value == null)
            {
                // JSON null is an omitted option, as the mod reads it.
                continue;
            }

            OptionEntry? option = what.Options.TryGetValue(name, out OptionEntry? own)
                ? own
                : verb.TakesShaping ? catalogue.ShapingOption(name) : null;
            if (option == null)
            {
                problems.Add(new OptionProblem(name, $"not an option of {what.Label}", Nearest.Of(name, valid)));
                unknownName ??= name;
                continue;
            }

            if (FaceProblem(verb, option, call.Face) is { } refused)
            {
                problems.Add(new OptionProblem(name, refused));
                continue;
            }

            if (name == "id" && value is JsonArray && !what.IdTakesList)
            {
                problems.Add(new OptionProblem(name, OneId(what)));
                continue;
            }

            Value(option.Schema, value, name, problems);
        }

        foreach (string name in what.OptionNames)
        {
            OptionEntry option = what.Options[name];
            if (option.Required && call.Args[name] == null)
            {
                problems.Add(new OptionProblem(name, "required"));
            }
        }

        return problems.Count == 0
            ? null
            : OptionProblem.Refusal(what.Label, problems, unknownName == null ? null : Nearest.NearestFirst(unknownName, valid));
    }

    /// <summary>An id that takes no list: a write that cannot be undone takes one id; several are a script's loop.</summary>
    internal static string OneId(WhatEntry what) =>
        $"takes one id: for several, use run_script (a loop of sg.{what.Verb} calls; this write cannot be undone)";

    // The what's own options, then the shaping ones the verb takes.
    private static List<string> ValidNames(VerbEntry verb, WhatEntry what)
    {
        List<string> names = new(what.OptionNames);
        if (verb.TakesShaping)
        {
            foreach (string shaping in VerbCatalogue.Shaping)
            {
                if (!names.Contains(shaping))
                {
                    names.Add(shaping);
                }
            }
        }

        return names;
    }

    // help is the hub's own page server: a script asks it by the same names (sg.help('read', 'logic')).
    private static string? FaceProblem(VerbEntry verb, OptionEntry option, Face face)
    {
        if (face == Face.Direct || verb.Name == "help" || !option.DroppedInScripts)
        {
            return null;
        }

        if (option.Name == "confirm")
        {
            return face == Face.Script ? ConfirmBelongsToTheRun : null;
        }

        return face == Face.Script ? "not taken in a script" : "not taken by a program";
    }

    /// <summary>The value's problems against a schema (type, enum, bounds, list length, items), added to problems.</summary>
    internal static void Value(JsonObject schema, JsonNode value, string path, List<OptionProblem> problems)
    {
        string kind = KindOf(value);
        List<string> types = TypesOf(schema);
        if (types.Count > 0 && !Allows(types, kind, value))
        {
            problems.Add(new OptionProblem(path, "must be " + TypeWords(types)));
            return;
        }

        if (schema["enum"] is JsonArray listed && listed.Count > 0 && !Listed(listed, value))
        {
            List<string> words = Words(listed);
            string given = JsonNodes.Text(value) ?? value.ToJsonString();
            problems.Add(new OptionProblem(path, $"'{given}' is not one of {string.Join(", ", words)}", Nearest.Of(given, words)));
            return;
        }

        if (kind is "integer" or "number")
        {
            Bounds(schema, JsonNodes.Number(value)!.Value, path, problems);
        }

        if (value is JsonArray items)
        {
            if (Count(schema, "minItems") is { } least && items.Count < least)
            {
                problems.Add(new OptionProblem(path, $"needs at least {least} entries"));
            }

            if (Count(schema, "maxItems") is { } most && items.Count > most)
            {
                problems.Add(new OptionProblem(path, $"takes at most {most} entries; {items.Count} given"));
            }

            if (schema["items"] is JsonObject itemSchema && itemSchema.Count > 0)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    if (items[index] is { } item)
                    {
                        Value(itemSchema, item, $"{path}[{index}]", problems);
                    }
                }
            }
        }
    }

    private static void Bounds(JsonObject schema, double given, string path, List<OptionProblem> problems)
    {
        if (Number(schema, "minimum") is { } minimum && given < minimum)
        {
            problems.Add(new OptionProblem(path, $"must be at least {Text(minimum)}"));
        }

        if (Number(schema, "exclusiveMinimum") is { } above && given <= above)
        {
            problems.Add(new OptionProblem(path, $"must be more than {Text(above)}"));
        }

        if (Number(schema, "maximum") is { } maximum && given > maximum)
        {
            problems.Add(new OptionProblem(path, $"must be at most {Text(maximum)}"));
        }
    }

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static double? Number(JsonObject schema, string key) => JsonNodes.Number(schema[key]);

    private static int? Count(JsonObject schema, string key) => JsonNodes.Number(schema[key]) is { } count ? (int)count : null;

    // An enum's strings match as the mod matches them: trimmed, any case.
    private static bool Listed(JsonArray listed, JsonNode value)
    {
        string? text = JsonNodes.Text(value)?.Trim();
        foreach (JsonNode? entry in listed)
        {
            if (text != null && JsonNodes.Text(entry) is { } listedText)
            {
                if (string.Equals(listedText, text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (JsonNode.DeepEquals(entry, value))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Words(JsonArray listed)
    {
        List<string> words = [];
        foreach (JsonNode? entry in listed)
        {
            words.Add(JsonNodes.Text(entry) ?? entry?.ToJsonString() ?? "null");
        }

        return words;
    }

    internal static List<string> TypesOf(JsonObject schema)
    {
        List<string> types = [];
        switch (schema["type"])
        {
            case JsonValue single when JsonNodes.Text(single) is { } name:
                types.Add(name);
                break;
            case JsonArray several:
                foreach (JsonNode? type in several)
                {
                    types.Add((string)type!);
                }

                break;
        }

        return types;
    }

    /// <summary>The JSON kind of a value: string, integer (a number with no fraction), number, boolean, array, object.</summary>
    internal static string KindOf(JsonNode value)
    {
        switch (value)
        {
            case JsonObject:
                return "object";
            case JsonArray:
                return "array";
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => IsWhole(JsonNodes.Number(value)!.Value) ? "integer" : "number",
            _ => "null"
        };
    }

    private static bool IsWhole(double value) => double.IsFinite(value) && Math.Floor(value) == value;

    private static bool Allows(List<string> types, string kind, JsonNode value)
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

    private static string TypeWords(List<string> types)
    {
        StringBuilder words = new();
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
