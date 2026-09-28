#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A request's parameters with typed getters. JSON null means absent everywhere. A value of the wrong type is
/// invalid_argument, naming the argument; a getter never guesses.
/// </summary>
internal sealed class Args
{
    private readonly JObject _parameters;

    internal Args(JObject? parameters)
    {
        _parameters = parameters ?? new JObject();
    }

    internal bool Has(string name) => Token(name) != null;

    internal ThingId ThingId(string name) =>
        OptionalThingId(name) ?? throw ApiErrors.InvalidArgument($"Argument '{name}' is required.");

    internal ThingId? OptionalThingId(string name)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        if (!Shared.ThingId.TryRead(token, out ThingId id))
        {
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be a reference id as a decimal string.");
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
                throw ApiErrors.InvalidArgument($"{name}[{index}] must be a reference id as a decimal string.");
            }

            ids.Add(id);
        }

        return ids;
    }

    internal string String(string name) =>
        OptionalString(name) ?? throw ApiErrors.InvalidArgument($"Argument '{name}' is required.");

    internal string? OptionalString(string name)
    {
        JToken? token = Token(name);
        if (token == null)
        {
            return null;
        }

        if (token.Type != JTokenType.String)
        {
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be a string.");
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
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be true or false.");
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
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be an integer from {minimum} to {maximum}.");
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
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be a finite number.");
        }

        return token.Value<double>();
    }

    internal double Double(string name) =>
        OptionalDouble(name) ?? throw ApiErrors.InvalidArgument($"Argument '{name}' must be a finite number.");

    internal int Int(string name, int minimum, int maximum) =>
        OptionalInt(name, minimum, maximum) ??
        throw ApiErrors.InvalidArgument($"Argument '{name}' must be an integer from {minimum} to {maximum}.");

    internal double? OptionalPositiveDouble(string name)
    {
        double? value = OptionalDouble(name);
        if (value.HasValue && !(value.Value > 0.0))
        {
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be greater than 0.");
        }

        return value;
    }

    internal JArray Array(string name, int maximum)
    {
        JToken? token = Token(name);
        if (!(token is JArray array) || array.Count == 0 || array.Count > maximum)
        {
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be an array of 1 to {maximum} entries.");
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

        return token as JObject ?? throw ApiErrors.InvalidArgument($"Argument '{name}' must be an object.");
    }

    /// <summary>Whether the argument is the given word (a string, compared ignoring case).</summary>
    internal bool IsWord(string name, string word)
    {
        JToken? token = Token(name);
        return token != null && token.Type == JTokenType.String &&
               string.Equals(token.Value<string>(), word, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Refuses arguments that belong to another form of the tool.</summary>
    internal void Reject(string form, params string[] names)
    {
        foreach (string name in names)
        {
            if (Has(name))
            {
                throw ApiErrors.InvalidArgument($"Argument '{name}' does not go with {form}.");
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
        return new Args(copy);
    }

    private JToken? Token(string name)
    {
        JToken? token = _parameters[name];
        return token == null || token.Type == JTokenType.Null ? null : token;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
