using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// Checks a tool call's arguments against the input schema tools/list publishes for the tool, before the call reaches
/// the game: a property an object schema with additionalProperties false does not declare is refused (with the
/// nearest declared name when one is close), and so is a value whose JSON type the schema does not allow, a string or
/// number its enum does not list (strings compared as the mod compares them: trimmed, ignoring case), a key given
/// twice in one object (JSON parsers keep the last one silently), and a number past a double's range (1e309). An
/// integer may be written as any JSON number with no fraction (3.0, 1e2), as JSON Schema's integer allows; Normalised
/// rewrites those as integers for the mod, which reads integer tokens only. Ranges and required arguments stay with the
/// mod, which words them per tool. JSON null is an omitted property, as the mod reads it. The mod itself stays lenient
/// for pipe clients (e.g. ids as JSON integers).
/// </summary>
internal static class ArgumentCheck
{
    /// <summary>What is wrong with the arguments, one sentence each; empty when nothing is.</summary>
    internal static IReadOnlyList<string> Problems(JsonElement schema, JsonElement arguments) =>
        arguments.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => [],
            JsonValueKind.Object => Malformed(arguments, string.Empty) is { Count: > 0 } malformed
                ? malformed
                : Check(schema, arguments, string.Empty),
            _ => ["The arguments must be a JSON object."]
        };

    /// <summary>
    /// The arguments as the mod should get them: every number where the schema asks for an integer written as one
    /// (3.0 and 1e2 as 3 and 100). Call it on arguments Problems passed.
    /// </summary>
    internal static JsonElement Normalised(JsonElement schema, JsonElement arguments) =>
        JsonSerializer.SerializeToElement(Normalise(schema, arguments));

    /// <summary>The first key given twice in this object (not below it), or null.</summary>
    internal static string? RepeatedKey(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        return value.EnumerateObject().Select(property => property.Name).FirstOrDefault(name => !seen.Add(name));
    }

    // Faults the schema cannot see: a key given twice anywhere, a number that is not a finite double.
    private static List<string> Malformed(JsonElement value, string path) => value.ValueKind switch
    {
        JsonValueKind.Object when RepeatedKey(value) is { } repeated =>
            [$"Argument '{Join(path, repeated)}' is given twice; name each key once."],
        JsonValueKind.Object => value.EnumerateObject()
            .SelectMany(property => Malformed(property.Value, Join(path, property.Name))).ToList(),
        JsonValueKind.Array => value.EnumerateArray()
            .SelectMany((item, index) => Malformed(item, $"{path}[{index}]")).ToList(),
        JsonValueKind.Number when !IsFinite(value) =>
            [$"Argument '{path}' must be a finite number; {value.GetRawText()} is past a double's range."],
        _ => []
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static bool IsFinite(JsonElement number) => number.TryGetDouble(out double value) && double.IsFinite(value);

    // A JSON number with no fraction, as the integer it is; null for a fraction or past a long's range.
    private static long? WholeNumber(JsonElement number)
    {
        if (number.TryGetInt64(out long exact))
        {
            return exact;
        }

        const double LongRange = 9.2e18;
        return number.TryGetDouble(out double value) && double.IsFinite(value) && Math.Floor(value) == value &&
               Math.Abs(value) < LongRange
            ? (long)value
            : null;
    }

    private static JsonNode? Normalise(JsonElement schema, JsonElement value)
    {
        if (schema.ValueKind == JsonValueKind.Object && Alternatives(schema) is { } branches)
        {
            JsonElement taken = branches.FirstOrDefault(branch => Check(branch, value, string.Empty).Count == 0);
            return Normalise(taken, value);
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when AsksForInteger(schema) && WholeNumber(value) is { } whole => JsonValue.Create(whole),
            JsonValueKind.Object => NormaliseObject(schema, value),
            JsonValueKind.Array => new JsonArray(value.EnumerateArray()
                .Select(item => Normalise(ItemsOf(schema), item)).ToArray()),
            _ => JsonNode.Parse(value.GetRawText())
        };
    }

    private static JsonObject NormaliseObject(JsonElement schema, JsonElement value)
    {
        JsonObject normalised = new();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            normalised[property.Name] = Normalise(PropertySchema(schema, property.Name), property.Value);
        }

        return normalised;
    }

    private static bool AsksForInteger(JsonElement schema)
    {
        string[] names = TypeNames(schema);
        return names.Contains("integer") && !names.Contains("number");
    }

    private static JsonElement ItemsOf(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("items", out JsonElement items) ? items : default;

    private static JsonElement PropertySchema(JsonElement schema, string name)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (schema.TryGetProperty("properties", out JsonElement properties) &&
            properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out JsonElement declared))
        {
            return declared;
        }

        return schema.TryGetProperty("additionalProperties", out JsonElement additional) ? additional : default;
    }

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

        if (EnumProblem(schema, value, path) is { } notListed)
        {
            return [notListed];
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
        "integer" => value.ValueKind == JsonValueKind.Number && WholeNumber(value).HasValue,
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    // An enum lists every value the mod takes; the mod trims a word and ignores its case.
    private static string? EnumProblem(JsonElement schema, JsonElement value, string path)
    {
        if (!schema.TryGetProperty("enum", out JsonElement listed) || listed.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement[] values = listed.EnumerateArray().ToArray();
        if (values.Any(allowed => Matches(allowed, value)))
        {
            return null;
        }

        string words = string.Join(", ", values.Select(allowed => allowed.ValueKind == JsonValueKind.String
            ? allowed.GetString()
            : allowed.GetRawText()));
        string? nearest = value.ValueKind == JsonValueKind.String
            ? Nearest(value.GetString()!.Trim(), values.Where(allowed => allowed.ValueKind == JsonValueKind.String)
                .Select(allowed => allowed.GetString()!))
            : null;
        string suggestion = nearest == null ? "." : $"; did you mean '{nearest}'?";
        return $"Argument '{path}' must be one of {words}{suggestion}";
    }

    private static bool Matches(JsonElement allowed, JsonElement value) => (allowed.ValueKind, value.ValueKind) switch
    {
        (JsonValueKind.String, JsonValueKind.String) =>
            string.Equals(allowed.GetString(), value.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase),
        (JsonValueKind.Number, JsonValueKind.Number) =>
            allowed.TryGetDouble(out double expected) && value.TryGetDouble(out double given) && expected == given,
        _ => allowed.GetRawText() == value.GetRawText()
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

    // Reference ids are decimal strings on the wire (they pass 2^53): a bare whole number where a string belongs is
    // almost always an id sent unquoted. A fraction is no id, and quoting it would not help.
    private static string QuoteHint(JsonElement schema, JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && WholeNumber(value).HasValue && TypeNames(schema).Contains("string")
            ? $", e.g. \"{value.GetRawText()}\" in quotes"
            : string.Empty;
}
