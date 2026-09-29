using System.Text.Json;

namespace StationGodMCP.Server;

/// <summary>
/// Checks a tool call's arguments against the input schema tools/list publishes for the tool, before the call reaches
/// the game: a property an object schema with additionalProperties false does not declare is refused (with the
/// nearest declared name when one is close), and so is a value whose JSON type the schema does not allow. Ranges,
/// enums and required arguments stay with the mod, which words them per tool. JSON null is an omitted property, as the
/// mod reads it. The mod itself stays lenient for pipe clients (e.g. ids as JSON integers).
/// </summary>
internal static class ArgumentCheck
{
    /// <summary>What is wrong with the arguments, one sentence each; empty when nothing is.</summary>
    internal static IReadOnlyList<string> Problems(JsonElement schema, JsonElement arguments) =>
        arguments.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            JsonValueKind.Object => Check(schema, arguments, string.Empty),
            _ => ["The arguments must be a JSON object."]
        };

    private static List<string> Check(JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (Alternatives(schema) is { } branches)
        {
            return CheckAlternatives(branches, value, path);
        }

        if (!Allows(schema, value))
        {
            return [$"Argument '{path}' must be {TypeWords(schema)}{QuoteHint(schema, value)}."];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => CheckObject(schema, value, path),
            JsonValueKind.Array when schema.TryGetProperty("items", out JsonElement items) =>
                value.EnumerateArray().SelectMany((item, index) => Check(items, item, $"{path}[{index}]")).ToList(),
            _ => []
        };
    }

    // A value passes a oneOf/anyOf when some branch takes it. Otherwise the branch its JSON type fits explains why
    // (an unknown key in the object form); with none fitting, the message names every type the branches allow.
    private static List<string> CheckAlternatives(JsonElement[] branches, JsonElement value, string path)
    {
        List<string>[] outcomes = branches.Select(branch => Check(branch, value, path)).ToArray();
        if (outcomes.Any(problems => problems.Count == 0))
        {
            return [];
        }

        List<string>? fitting = branches.Zip(outcomes)
            .Where(pair => Alternatives(pair.First) == null && Allows(pair.First, value))
            .Select(pair => pair.Second)
            .FirstOrDefault();
        if (fitting != null)
        {
            return fitting;
        }

        string words = string.Join(" or ", branches.Select(TypeWords).Distinct());
        JsonElement stringBranch = branches.FirstOrDefault(branch => TypeNames(branch).Contains("string"));
        return [$"Argument '{path}' must be {words}{QuoteHint(stringBranch, value)}."];
    }

    private static List<string> CheckObject(JsonElement schema, JsonElement value, string path)
    {
        JsonElement properties = schema.TryGetProperty("properties", out JsonElement declared) &&
                                 declared.ValueKind == JsonValueKind.Object
            ? declared
            : default;
        schema.TryGetProperty("additionalProperties", out JsonElement additional);
        List<string> problems = [];
        foreach (JsonProperty property in value.EnumerateObject().Where(property => property.Value.ValueKind != JsonValueKind.Null))
        {
            string name = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            if (properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(property.Name, out JsonElement propertySchema))
            {
                problems.AddRange(Check(propertySchema, property.Value, name));
            }
            else if (additional.ValueKind == JsonValueKind.False)
            {
                problems.Add(Unknown(name, property.Name, properties));
            }
            else if (additional.ValueKind == JsonValueKind.Object)
            {
                problems.AddRange(Check(additional, property.Value, name));
            }
        }

        return problems;
    }

    private static string Unknown(string name, string given, JsonElement properties)
    {
        string[] known = properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
        string? nearest = Nearest(given, known);
        string suggestion = nearest == null ? string.Empty : $"; did you mean '{nearest}'?";
        string list = known.Length == 0 ? "none" : string.Join(", ", known);
        return $"Unknown argument '{name}'{suggestion} (known here: {list}).";
    }

    /// <summary>The declared name closest to a misspelt one, when the difference is a slip rather than another word.</summary>
    internal static string? Nearest(string given, IEnumerable<string> known) =>
        known.Select(name => (name, distance: Distance(given.ToLowerInvariant(), name.ToLowerInvariant())))
            .Where(candidate => candidate.distance <= Math.Max(2, candidate.name.Length / 4))
            .OrderBy(candidate => candidate.distance)
            .Select(candidate => candidate.name)
            .FirstOrDefault();

    private static int Distance(string first, string second)
    {
        int[] previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (int row = 1; row <= first.Length; row++)
        {
            int[] current = new int[second.Length + 1];
            current[0] = row;
            for (int column = 1; column <= second.Length; column++)
            {
                int substitution = previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1);
                current[column] = Math.Min(substitution, Math.Min(previous[column], current[column - 1]) + 1);
            }

            previous = current;
        }

        return previous[second.Length];
    }

    private static JsonElement[]? Alternatives(JsonElement schema) =>
        schema.TryGetProperty("oneOf", out JsonElement oneOf) && oneOf.ValueKind == JsonValueKind.Array
            ? oneOf.EnumerateArray().ToArray()
            : schema.TryGetProperty("anyOf", out JsonElement anyOf) && anyOf.ValueKind == JsonValueKind.Array
                ? anyOf.EnumerateArray().ToArray()
                : null;

    private static string[] TypeNames(JsonElement schema) =>
        schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out JsonElement type)
            ? []
            : type.ValueKind switch
            {
                JsonValueKind.String => [type.GetString()!],
                JsonValueKind.Array => type.EnumerateArray().Select(name => name.GetString() ?? string.Empty).ToArray(),
                _ => []
            };

    private static bool Allows(JsonElement schema, JsonElement value)
    {
        string[] names = TypeNames(schema);
        return names.Length == 0 || names.Any(name => IsOfType(name, value));
    }

    private static bool IsOfType(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string TypeWords(JsonElement schema) =>
        string.Join(" or ", TypeNames(schema).Select(name => name switch
        {
            "string" => "a string",
            "integer" => "an integer",
            "number" => "a number",
            "boolean" => "true or false",
            "object" => "an object",
            "array" => "an array",
            _ => name
        }));

    // Reference ids are decimal strings on the wire (they pass 2^53): a bare number where a string belongs is almost
    // always an id sent unquoted.
    private static string QuoteHint(JsonElement schema, JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && TypeNames(schema).Contains("string")
            ? $", e.g. \"{value.GetRawText()}\" in quotes"
            : string.Empty;
}
