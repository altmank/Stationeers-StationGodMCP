#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>A catalogue that cannot be used: a parse error, a missing part, or a schema keyword the mod does not check.</summary>
internal sealed class CatalogueException : Exception
{
    internal CatalogueException(string message) : base(message)
    {
    }
}

/// <summary>One thing wrong with a value: where (args.items[3].logic) and what, in a sentence.</summary>
internal sealed class SchemaProblem
{
    internal SchemaProblem(string path, string problem)
    {
        Path = path;
        Problem = problem;
    }

    internal string Path { get; }

    internal string Problem { get; }

    public override string ToString() => Problem;
}

/// <summary>
/// One compiled schema of the catalogue's subset of JSON Schema (catalogue.md, The schema subset): type, properties,
/// required, additionalProperties, items, minItems, maxItems, enum, const, minimum, maximum, exclusiveMinimum,
/// exclusiveMaximum, minLength, maxLength, pattern (anchored), oneOf, anyOf, minProperties, maxProperties, and the
/// annotations default, description and deprecated. Compiling refuses any other keyword, so the catalogue cannot say
/// more than the mod checks. Validation follows the sidecar's rules: JSON null is an omitted property; an integer
/// may be written with a zero fraction or an exponent (3.0, 1e2); strings in an enum or const compare trimmed and
/// ignoring case, as the mod compares words.
/// </summary>
internal sealed class SchemaNode
{
    private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "enum", "const",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "pattern", "oneOf",
        "anyOf", "minProperties", "maxProperties", "default", "description", "deprecated"
    };

    private JsonTypes _types = JsonTypes.Any;
    private readonly Dictionary<string, SchemaNode> _properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
    private readonly List<string> _propertyNames = new List<string>();
    private readonly List<string> _required = new List<string>();
    private bool _closed;
    private SchemaNode? _additional;
    private SchemaNode? _items;
    private int? _minItems;
    private int? _maxItems;
    private List<JToken>? _enum;
    private JToken? _const;
    private double? _minimum;
    private double? _maximum;
    private double? _exclusiveMinimum;
    private double? _exclusiveMaximum;
    private int? _minLength;
    private int? _maxLength;
    private Regex? _pattern;
    private List<SchemaNode>? _oneOf;
    private List<SchemaNode>? _anyOf;
    private int? _minProperties;
    private int? _maxProperties;

    private SchemaNode()
    {
    }

    /// <summary>The names of properties, in the schema's order; empty for a schema that is not an object's.</summary>
    internal IReadOnlyList<string> PropertyNames => _propertyNames;

    internal bool HasProperty(string name) => _properties.ContainsKey(name);

    /// <summary>Compiles a schema; at names the place in the catalogue for the message.</summary>
    internal static SchemaNode Compile(JToken token, string at)
    {
        if (!(token is JObject schema))
        {
            throw new CatalogueException($"{at}: a schema must be an object.");
        }

        SchemaNode node = new SchemaNode();
        foreach (JProperty keyword in schema.Properties())
        {
            if (!Keywords.Contains(keyword.Name))
            {
                throw new CatalogueException(
                    $"{at}: the schema keyword '{keyword.Name}' is not in the subset the mod checks (catalogue.md, The schema subset).");
            }
        }

        node._types = TypesOf(schema["type"], at);
        if (schema["properties"] is JToken properties)
        {
            if (!(properties is JObject declared))
            {
                throw new CatalogueException($"{at}.properties: must be an object.");
            }

            foreach (JProperty property in declared.Properties())
            {
                node._properties[property.Name] = Compile(property.Value, $"{at}.properties.{property.Name}");
                node._propertyNames.Add(property.Name);
            }
        }

        if (schema["required"] is JArray required)
        {
            foreach (JToken name in required)
            {
                node._required.Add(name.Type == JTokenType.String
                    ? (string)name!
                    : throw new CatalogueException($"{at}.required: names must be strings."));
            }
        }

        switch (schema["additionalProperties"])
        {
            case null:
                break;
            case JValue { Type: JTokenType.Boolean } closed:
                node._closed = !(bool)closed;
                break;
            case JObject additional:
                node._additional = Compile(additional, $"{at}.additionalProperties");
                break;
            default:
                throw new CatalogueException($"{at}.additionalProperties: must be false, true or a schema.");
        }

        if (schema["items"] is JToken items)
        {
            node._items = Compile(items, $"{at}.items");
        }

        node._minItems = Count(schema, "minItems", at);
        node._maxItems = Count(schema, "maxItems", at);
        node._minLength = Count(schema, "minLength", at);
        node._maxLength = Count(schema, "maxLength", at);
        node._minProperties = Count(schema, "minProperties", at);
        node._maxProperties = Count(schema, "maxProperties", at);
        node._minimum = Number(schema, "minimum", at);
        node._maximum = Number(schema, "maximum", at);
        node._exclusiveMinimum = Number(schema, "exclusiveMinimum", at);
        node._exclusiveMaximum = Number(schema, "exclusiveMaximum", at);
        if (schema["enum"] is JToken listed)
        {
            if (!(listed is JArray values) || values.Count == 0)
            {
                throw new CatalogueException($"{at}.enum: must be a non-empty array.");
            }

            node._enum = new List<JToken>(values);
        }

        node._const = schema["const"];
        if (schema["pattern"] is JToken pattern)
        {
            node._pattern = Pattern(pattern, at);
        }

        node._oneOf = Branches(schema, "oneOf", at);
        node._anyOf = Branches(schema, "anyOf", at);
        return node;
    }

    /// <summary>Every problem with the value; a null or absent value is checked only for what it lacks (required).</summary>
    internal void Validate(JToken? value, string path, List<SchemaProblem> problems)
    {
        if (value == null || value.Type == JTokenType.Null)
        {
            return;
        }

        if (_oneOf != null || _anyOf != null)
        {
            ValidateBranches(value, path, problems);
        }

        JsonTypes actual = TypeOf(value);
        if (_types != JsonTypes.Any && (_types & actual) == 0)
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must be {Words(_types)}."));
            return;
        }

        if (_enum != null && !Listed(_enum, value))
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must be one of: {Join(_enum)}."));
        }

        if (_const != null && !SameValue(_const, value))
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must be {_const.ToString(Formatting.None)}."));
        }

        switch (value)
        {
            case JObject obj:
                ValidateObject(obj, path, problems);
                break;
            case JArray array:
                ValidateArray(array, path, problems);
                break;
            case JValue { Type: JTokenType.String } text:
                ValidateString((string)text!, path, problems);
                break;
            case JValue { Type: JTokenType.Integer or JTokenType.Float } number:
                ValidateNumber(number.Value<double>(), path, problems);
                break;
        }
    }

    private void ValidateObject(JObject obj, string path, List<SchemaProblem> problems)
    {
        int given = 0;
        List<string>? unknown = null;
        foreach (JProperty property in obj.Properties())
        {
            if (property.Value.Type == JTokenType.Null)
            {
                continue;
            }

            given++;
            string at = Join(path, property.Name);
            if (_properties.TryGetValue(property.Name, out SchemaNode schema))
            {
                schema.Validate(property.Value, at, problems);
            }
            else if (_additional != null)
            {
                _additional.Validate(property.Value, at, problems);
            }
            else if (_closed)
            {
                unknown ??= new List<string>();
                unknown.Add(property.Name);
            }
        }

        if (unknown != null)
        {
            foreach (string name in unknown)
            {
                string? nearest = NearestName.Of(name, _propertyNames);
                problems.Add(new SchemaProblem(Join(path, name), nearest != null
                    ? $"Unknown argument '{Join(path, name)}'; did you mean '{Join(path, nearest)}'?"
                    : $"Unknown argument '{Join(path, name)}'."));
            }
        }

        foreach (string name in _required)
        {
            JToken? value = obj[name];
            if (value == null || value.Type == JTokenType.Null)
            {
                problems.Add(new SchemaProblem(Join(path, name), $"Argument '{Join(path, name)}' is required."));
            }
        }

        if (_minProperties.HasValue && given < _minProperties.Value ||
            _maxProperties.HasValue && given > _maxProperties.Value)
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must have {Range(_minProperties, _maxProperties)} keys."));
        }
    }

    private void ValidateArray(JArray array, string path, List<SchemaProblem> problems)
    {
        if (_minItems.HasValue && array.Count < _minItems.Value || _maxItems.HasValue && array.Count > _maxItems.Value)
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must be an array of {Range(_minItems, _maxItems)} entries."));
        }

        if (_items == null)
        {
            return;
        }

        for (int index = 0; index < array.Count; index++)
        {
            _items.Validate(array[index], $"{path}[{index}]", problems);
        }
    }

    private void ValidateString(string text, string path, List<SchemaProblem> problems)
    {
        if (_minLength.HasValue && text.Length < _minLength.Value || _maxLength.HasValue && text.Length > _maxLength.Value)
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must be {Range(_minLength, _maxLength)} characters long."));
        }

        if (_pattern != null && !_pattern.IsMatch(text))
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must match {_pattern}."));
        }
    }

    private void ValidateNumber(double number, string path, List<SchemaProblem> problems)
    {
        if (_minimum.HasValue && number < _minimum.Value || _maximum.HasValue && number > _maximum.Value ||
            _exclusiveMinimum.HasValue && number <= _exclusiveMinimum.Value ||
            _exclusiveMaximum.HasValue && number >= _exclusiveMaximum.Value)
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must be {Bounds()}."));
        }
    }

    // oneOf: exactly one branch takes the value; anyOf: at least one. Otherwise the branch whose JSON type fits
    // explains why (the sidecar's rule), else every branch's type is named.
    private void ValidateBranches(JToken value, string path, List<SchemaProblem> problems)
    {
        List<SchemaNode> branches = _oneOf ?? _anyOf!;
        int passed = 0;
        List<SchemaProblem>? fitting = null;
        JsonTypes offered = JsonTypes.None;
        foreach (SchemaNode branch in branches)
        {
            List<SchemaProblem> found = new List<SchemaProblem>();
            branch.Validate(value, path, found);
            offered |= branch._types;
            if (found.Count == 0)
            {
                passed++;
            }
            else if (fitting == null && (branch._types & TypeOf(value)) != 0)
            {
                fitting = found;
            }
        }

        if (_oneOf != null && passed == 1 || _oneOf == null && passed >= 1)
        {
            return;
        }

        if (passed > 1)
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' matches more than one of its forms."));
        }
        else if (fitting != null)
        {
            problems.AddRange(fitting);
        }
        else
        {
            problems.Add(new SchemaProblem(path, $"Argument '{path}' must be {Words(offered)}."));
        }
    }

    // ---- compile helpers ----

    private static JsonTypes TypesOf(JToken? token, string at)
    {
        switch (token)
        {
            case null:
                return JsonTypes.Any;
            case JValue { Type: JTokenType.String } single:
                return TypeNamed((string)single!, at);
            case JArray list:
                JsonTypes types = JsonTypes.None;
                foreach (JToken name in list)
                {
                    types |= name.Type == JTokenType.String
                        ? TypeNamed((string)name!, at)
                        : throw new CatalogueException($"{at}.type: names must be strings.");
                }

                return types;
            default:
                throw new CatalogueException($"{at}.type: must be a name or a list of names.");
        }
    }

    private static JsonTypes TypeNamed(string name, string at) => name switch
    {
        "object" => JsonTypes.Object,
        "array" => JsonTypes.Array,
        "string" => JsonTypes.String,
        "integer" => JsonTypes.Integer,
        "number" => JsonTypes.Number,
        "boolean" => JsonTypes.Boolean,
        "null" => JsonTypes.Null,
        _ => throw new CatalogueException($"{at}.type: unknown type '{name}'.")
    };

    private static int? Count(JObject schema, string keyword, string at)
    {
        JToken? token = schema[keyword];
        if (token == null)
        {
            return null;
        }

        if (token.Type != JTokenType.Integer || token.Value<long>() < 0 || token.Value<long>() > int.MaxValue)
        {
            throw new CatalogueException($"{at}.{keyword}: must be a whole number of at least 0.");
        }

        return token.Value<int>();
    }

    private static double? Number(JObject schema, string keyword, string at)
    {
        JToken? token = schema[keyword];
        if (token == null)
        {
            return null;
        }

        return token.Type == JTokenType.Integer || token.Type == JTokenType.Float
            ? token.Value<double>()
            : throw new CatalogueException($"{at}.{keyword}: must be a number.");
    }

    private static List<SchemaNode>? Branches(JObject schema, string keyword, string at)
    {
        JToken? token = schema[keyword];
        if (token == null)
        {
            return null;
        }

        if (!(token is JArray list) || list.Count == 0)
        {
            throw new CatalogueException($"{at}.{keyword}: must be a non-empty array of schemas.");
        }

        List<SchemaNode> branches = new List<SchemaNode>(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            branches.Add(Compile(list[index], $"{at}.{keyword}[{index}]"));
        }

        return branches;
    }

    // Anchored and plain: ^, $, character classes, escapes and quantifiers only, so .NET and Python agree.
    private static Regex Pattern(JToken token, string at)
    {
        string text = token.Type == JTokenType.String
            ? (string)token!
            : throw new CatalogueException($"{at}.pattern: must be a string.");
        if (text.Length < 2 || text[0] != '^' || text[text.Length - 1] != '$' || text.IndexOf('(') >= 0 ||
            text.IndexOf('|') >= 0 || text.IndexOf("\\b", StringComparison.Ordinal) >= 0)
        {
            throw new CatalogueException(
                $"{at}.pattern: '{text}' must be anchored (^...$) and use only character classes, escapes and quantifiers.");
        }

        return new Regex(text, RegexOptions.CultureInvariant);
    }

    // ---- validation helpers ----

    [Flags]
    private enum JsonTypes
    {
        None = 0,
        Object = 1,
        Array = 2,
        String = 4,
        Integer = 8,
        Number = 16,
        Boolean = 32,
        Null = 64,
        Any = Object | Array | String | Integer | Number | Boolean | Null
    }

    // A number with no fraction counts as an integer (3.0, 1e2), as JSON Schema's integer allows; every integer is a number.
    private static JsonTypes TypeOf(JToken value)
    {
        switch (value.Type)
        {
            case JTokenType.Object:
                return JsonTypes.Object;
            case JTokenType.Array:
                return JsonTypes.Array;
            case JTokenType.String:
                return JsonTypes.String;
            case JTokenType.Boolean:
                return JsonTypes.Boolean;
            case JTokenType.Null:
                return JsonTypes.Null;
            case JTokenType.Integer:
                return JsonTypes.Integer | JsonTypes.Number;
            case JTokenType.Float:
                double number = value.Value<double>();
                return !double.IsNaN(number) && !double.IsInfinity(number) && Math.Floor(number) == number
                    ? JsonTypes.Integer | JsonTypes.Number
                    : JsonTypes.Number;
            default:
                return JsonTypes.None;
        }
    }

    private static bool Listed(List<JToken> values, JToken value)
    {
        foreach (JToken listed in values)
        {
            if (SameValue(listed, value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// JSON equality, with strings compared trimmed and ignoring case (the mod's words) and numbers by value (3 is 3.0).
    /// </summary>
    internal static bool SameValue(JToken expected, JToken value)
    {
        if (expected.Type == JTokenType.String && value.Type == JTokenType.String)
        {
            return string.Equals(((string)expected!).Trim(), ((string)value!).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        bool expectedNumber = expected.Type == JTokenType.Integer || expected.Type == JTokenType.Float;
        bool valueNumber = value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
        if (expectedNumber && valueNumber)
        {
            return expected.Value<double>() == value.Value<double>();
        }

        return JToken.DeepEquals(expected, value);
    }

    private string Bounds()
    {
        StringBuilder text = new StringBuilder();
        Append(text, _minimum, "at least ");
        Append(text, _exclusiveMinimum, "greater than ");
        Append(text, _maximum, "at most ");
        Append(text, _exclusiveMaximum, "less than ");
        return text.ToString();
    }

    private static void Append(StringBuilder text, double? bound, string words)
    {
        if (!bound.HasValue)
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append(" and ");
        }

        text.Append(words).Append(bound.Value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static string Range(int? minimum, int? maximum) =>
        minimum.HasValue && maximum.HasValue ? $"{minimum.Value} to {maximum.Value}"
        : minimum.HasValue ? $"at least {minimum.Value}"
        : $"at most {maximum!.Value}";

    private static string Words(JsonTypes types)
    {
        List<string> words = new List<string>();
        if ((types & JsonTypes.Object) != 0) words.Add("an object");
        if ((types & JsonTypes.Array) != 0) words.Add("an array");
        if ((types & JsonTypes.String) != 0) words.Add("a string");
        if ((types & JsonTypes.Number) != 0) words.Add("a number");
        else if ((types & JsonTypes.Integer) != 0) words.Add("an integer");
        if ((types & JsonTypes.Boolean) != 0) words.Add("true or false");
        return words.Count == 0 ? "absent" : string.Join(" or ", words);
    }

    private static string Join(List<JToken> values)
    {
        List<string> texts = new List<string>(values.Count);
        foreach (JToken value in values)
        {
            texts.Add(value.Type == JTokenType.String ? (string)value! : value.ToString(Formatting.None));
        }

        return string.Join(", ", texts);
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
