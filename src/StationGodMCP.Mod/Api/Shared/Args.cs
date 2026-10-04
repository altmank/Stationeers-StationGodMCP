#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A request's parameters with typed getters. JSON null means absent everywhere. A value of the wrong type is
/// invalid_argument, naming the argument; a getter never guesses. A request's own parameters know their method's
/// declared names (the catalogue), and a name read that is not one of them counts in ArgumentDrift. Shape is the
/// call's shape, which a handler asks before building a reply part it declares costly.
/// </summary>
internal sealed class Args
{
    private readonly JObject _parameters;
    private readonly string _path;
    private readonly ArgumentNames? _declared;

    internal Args(JObject? parameters)
        : this(parameters, string.Empty)
    {
    }

    /// <summary>A request's parameters with the shape its reply is written through.</summary>
    internal Args(JObject? parameters, ShapeRequest shape)
        : this(parameters, string.Empty, null, shape)
    {
    }

    /// <summary>A nested object's parameters; path (e.g. "items[3]") prefixes every name an error message gives.</summary>
    internal Args(JObject? parameters, string path)
        : this(parameters, path, null)
    {
    }

    /// <summary>A request's parameters, held to its method's declared argument names, with its reply's shape.</summary>
    internal Args(JObject? parameters, ArgumentNames declared, ShapeRequest shape)
        : this(parameters, string.Empty, declared, shape)
    {
    }

    private Args(JObject? parameters, string path, ArgumentNames? declared = null, ShapeRequest? shape = null)
    {
        _parameters = parameters ?? new JObject();
        _path = path;
        _declared = declared;
        Shape = shape ?? ShapeRequest.None;
    }

    /// <summary>The call's shape; ShapeRequest.None (every part wanted) when the call has none.</summary>
    internal ShapeRequest Shape { get; }

    internal bool Has(string name) => Token(name) != null;

    internal ThingId ThingId(string name) =>
        OptionalThingId(name) ?? throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' is required.");

    internal ThingId? OptionalThingId(string name)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        if (!Shared.ThingId.TryRead(token, out ThingId id))
        {
            throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be a reference id as a decimal string.");
        }

        return id;
    }

    internal List<ThingId> ThingIds(string name, int maximum)
    {
        JArray array = Array(name, maximum);
        List<ThingId> ids = new List<ThingId>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            if (!Shared.ThingId.TryRead(array[index], out ThingId id))
            {
                throw ApiErrors.InvalidArgument($"{Named(name)}[{index}] must be a reference id as a decimal string.");
            }

            ids.Add(id);
        }

        return ids;
    }

    internal string String(string name) =>
        OptionalString(name) ?? throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' is required.");

    internal string? OptionalString(string name)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        if (token.Type != JTokenType.String)
        {
            throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be a string.");
        }

        return token.Value<string>();
    }

    internal bool? OptionalBool(string name)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        if (token.Type != JTokenType.Boolean)
        {
            throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be true or false.");
        }

        return token.Value<bool>();
    }

    internal int? OptionalInt(string name, int minimum, int maximum)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        string text = token.ToString(Formatting.None);
        if (token.Type != JTokenType.Integer ||
            !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ||
            value < minimum || value > maximum)
        {
            throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be an integer from {minimum} to {maximum}.");
        }

        return (int)value;
    }

    internal double? OptionalDouble(string name)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        if ((token.Type != JTokenType.Integer && token.Type != JTokenType.Float) || !IsFinite(token.Value<double>()))
        {
            throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be a finite number.");
        }

        return token.Value<double>();
    }

    internal double Double(string name) =>
        OptionalDouble(name) ??
        throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' is required: a finite number.");

    internal int Int(string name, int minimum, int maximum) =>
        OptionalInt(name, minimum, maximum) ??
        throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be an integer from {minimum} to {maximum}.");

    internal double? OptionalPositiveDouble(string name)
    {
        double? value = OptionalDouble(name);
        if (value.HasValue && !(value.Value > 0.0))
        {
            throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be greater than 0.");
        }

        return value;
    }

    internal JArray Array(string name, int maximum)
    {
        JToken? token = Token(name);
        if (!(token is JArray array) || array.Count == 0 || array.Count > maximum)
        {
            throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be an array of 1 to {maximum} entries.");
        }

        return array;
    }

    internal JObject? OptionalObject(string name)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        return token as JObject ?? throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' must be an object.");
    }

    /// <summary>Whether the argument is the given word (a string, compared ignoring case).</summary>
    internal bool IsWord(string name, string word)
    {
        JToken? token = Token(name);
        return token != null && token.Type == JTokenType.String &&
               string.Equals(token.Value<string>(), word, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the method declares the argument (always, for parameters held to no catalogue entry). A reader several
    /// methods share asks first before reading an argument only some of them take, so the read is never drift.
    /// </summary>
    internal bool Declares(string name) => _declared == null || _declared.Contains(name);

    /// <summary>
    /// Refuses arguments that belong to another form of the tool. A name the method does not declare is skipped:
    /// DeclaredArguments refused it before the handler ran.
    /// </summary>
    internal void Reject(string form, params string[] names)
    {
        foreach (string name in names)
        {
            if (Declares(name) && Has(name))
            {
                throw ApiErrors.InvalidArgument($"Argument '{Named(name)}' does not go with {form}.");
            }
        }
    }

    /// <summary>
    /// The raw value of an argument that takes more than one JSON type (a logic type by name or number, a scope by id
    /// or "world"), for a parser that knows them; null when absent.
    /// </summary>
    internal JToken? Optional(string name) => Token(name);

    /// <summary>
    /// An argument that must be a list: each object item read through its own Args (a batch's items), null for an item
    /// that is not an object, so the batch can fail that item alone.
    /// </summary>
    internal List<Args?> Objects(string name, int maximum)
    {
        JArray array = Array(name, maximum);
        List<Args?> items = new List<Args?>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            items.Add(array[index] is JObject item ? new Args(item) : null);
        }

        return items;
    }

    /// <summary>These parameters with one more set (a default a tool derives, e.g. plan routes' join_to).</summary>
    internal Args With(string name, JToken value)
    {
        JObject copy = (JObject)_parameters.DeepClone();
        copy[name] = value.DeepClone();
        return new Args(copy, _path, _declared, Shape);
    }

    // The name as an error message gives it: with the nested object's path in front.
    private string Named(string name) => _path.Length == 0 ? name : $"{_path}.{name}";

    private JToken? Token(string name)
    {
        if (_declared != null && !_declared.Contains(name))
        {
            ArgumentDrift.Record(_declared.Method, name);
        }

        JToken? token = _parameters[name];
        return token == null || token.Type == JTokenType.Null ? null : token;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// The names handlers read that their method's catalogue entry does not declare (mod_info.runtime.catalogue_drift),
/// and where the first read of each is reported (the mod's log).
/// </summary>
internal static class ArgumentDrift
{
    internal static CatalogueDrift Counts { get; } = new CatalogueDrift();

    /// <summary>Told the first undeclared read of each name by each method; null reports nothing.</summary>
    internal static System.Action<string, string>? FirstMiss { get; set; }

    internal static void Record(string method, string name)
    {
        if (Counts.Miss(method, name))
        {
            FirstMiss?.Invoke(method, name);
        }
    }
}
