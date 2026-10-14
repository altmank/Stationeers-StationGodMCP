using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// One parameter of a saved script's main(...), as the save-time parse read it: its name, the annotation as written
/// (null without one), whether it has a default, and its kind.
/// </summary>
internal sealed record ScriptParameter(string Name, string? Type, bool HasDefault, string Kind)
{
    internal const string PosOrKw = "pos_or_kw";
    internal const string KeywordOnly = "keyword_only";
    internal const string VarPositional = "var_positional";
    internal const string VarKeyword = "var_keyword";

    /// <summary>True for a parameter run_script's args may name.</summary>
    internal bool Named => Kind is PosOrKw or KeywordOnly;
}

/// <summary>
/// run_script {name, args}: the arguments checked against the saved script's main(...) parameters before the run starts
/// (SPEC-NOTES 5b). The check is partial by necessity: a missing required argument, an unknown argument name and a
/// value whose JSON type the annotation clearly maps are refused; a list's or dict's items, a union that names no
/// mapped type, and an annotation this code does not know are left to the run (say so in the what's help).
/// </summary>
internal static class ScriptArguments
{
    /// <summary>The problems with args for main(...), or null when there are none.</summary>
    internal static List<OptionProblem>? Check(IReadOnlyList<ScriptParameter> parameters, JsonObject args)
    {
        List<OptionProblem>? problems = null;
        bool kwarg = false;
        foreach (ScriptParameter parameter in parameters)
        {
            kwarg |= parameter.Kind == ScriptParameter.VarKeyword;
        }

        foreach ((string name, JsonNode? value) in args)
        {
            ScriptParameter? parameter = Of(parameters, name);
            if (parameter == null)
            {
                if (!kwarg)
                {
                    (problems ??= []).Add(new OptionProblem(name, "not a parameter of main(...)", Named(parameters)));
                }

                continue;
            }

            // JSON null is Python's None: every annotation may take it at the call (annotations are not enforced).
            if (value != null && parameter.Type is { } type && TypeProblem(type, value) is { } expected)
            {
                (problems ??= []).Add(new OptionProblem(name, $"must be {expected} (main's annotation says {type})",
                    Named(parameters)));
            }
        }

        foreach (ScriptParameter parameter in parameters)
        {
            if (parameter.Named && !parameter.HasDefault && !args.ContainsKey(parameter.Name))
            {
                string annotation = parameter.Type is { } type ? $" ({type})" : string.Empty;
                (problems ??= []).Add(new OptionProblem(parameter.Name, $"required: main(...) takes it{annotation}"));
            }
        }

        return problems;
    }

    private static ScriptParameter? Of(IReadOnlyList<ScriptParameter> parameters, string name)
    {
        foreach (ScriptParameter parameter in parameters)
        {
            if (parameter.Name == name && parameter.Named)
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>The names args may use, in main's order: the nearest list of an unknown name.</summary>
    internal static List<string> Named(IReadOnlyList<ScriptParameter> parameters)
    {
        List<string> names = [];
        foreach (ScriptParameter parameter in parameters)
        {
            if (parameter.Named)
            {
                names.Add(parameter.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// What the value must be, when the annotation maps the JSON type clearly enough to refuse it; null when the
    /// annotation names any type (Any, a class of the script's own), joins one with an unmapped type, or is absent.
    /// </summary>
    private static string? TypeProblem(string type, JsonNode value)
    {
        List<string>? kinds = Kinds(type);
        if (kinds == null || kinds.Contains(KindOf(value)))
        {
            return null;
        }

        return kinds.Count == 1 ? "a " + Words(kinds[0]) : "a " + Join(kinds);
    }

    private static string Join(List<string> kinds)
    {
        System.Text.StringBuilder words = new();
        for (int index = 0; index < kinds.Count; index++)
        {
            if (index > 0)
            {
                words.Append(index == kinds.Count - 1 ? " or " : ", ");
            }

            words.Append(Words(kinds[index]));
        }

        return words.ToString();
    }

    // The JSON kinds an annotation allows: int, float, str, bool, list[...], dict[...], None and their unions. Null
    // for anything else: Any, an unknown name, a subscripted type that is not a list or a dict.
    private static List<string>? Kinds(string type)
    {
        string text = type.Trim();
        if (text.StartsWith("Optional[", StringComparison.Ordinal) && text.EndsWith(']'))
        {
            text = text["Optional[".Length..^1];
        }

        List<string> kinds = [];
        foreach (string part in Split(text))
        {
            string name = part.Trim();
            int bracket = name.IndexOf('[');
            string head = (bracket >= 0 ? name[..bracket] : name).Trim();
            switch (head)
            {
                case "int":
                    Add(kinds, "integer");
                    break;
                case "float":
                    Add(kinds, "integer");
                    Add(kinds, "number");
                    break;
                case "str":
                    Add(kinds, "string");
                    break;
                case "bool":
                    Add(kinds, "boolean");
                    break;
                case "list" or "List" or "Sequence" or "Iterable" or "tuple" or "Tuple" or "set" or "Set":
                    Add(kinds, "array");
                    break;
                case "dict" or "Dict" or "Mapping" or "MutableMapping":
                    Add(kinds, "object");
                    break;
                case "None":
                    Add(kinds, "null");
                    break;
                default:
                    return null;
            }
        }

        return kinds.Count == 0 ? null : kinds;
    }

    // A union's parts: "int | str", "Union[int, str]" and "int | None" split; every other text is one part.
    private static List<string> Split(string text)
    {
        string body = text;
        if (text.StartsWith("Union[", StringComparison.Ordinal) && text.EndsWith(']'))
        {
            body = text["Union[".Length..^1];
        }

        List<string> parts = [];
        int depth = 0;
        int start = 0;
        for (int index = 0; index < body.Length; index++)
        {
            char c = body[index];
            depth += c is '[' or '(' ? 1 : c is ']' or ')' ? -1 : 0;
            if (depth == 0 && (c == '|' || c == ','))
            {
                parts.Add(body[start..index]);
                start = index + 1;
            }
        }

        parts.Add(body[start..]);
        return parts;
    }

    private static void Add(List<string> kinds, string kind)
    {
        if (!kinds.Contains(kind))
        {
            kinds.Add(kind);
        }
    }

    /// <summary>The JSON kind of a value as Python reads it: 1.0 is a number, 1 an integer.</summary>
    private static string KindOf(JsonNode value) => value switch
    {
        JsonObject => "object",
        JsonArray => "array",
        _ => value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => "string",
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
            System.Text.Json.JsonValueKind.Number => Floats(value.ToJsonString()) ? "number" : "integer",
            _ => "null"
        }
    };

    private static bool Floats(string raw) => raw.Contains('.') || raw.Contains('e') || raw.Contains('E');

    private static string Words(string kind) => kind switch
    {
        "string" => "string",
        "integer" => "whole number",
        "number" => "number",
        "boolean" => "true or false",
        "array" => "list",
        "object" => "object",
        _ => kind
    };
}
