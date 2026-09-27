#nullable enable

using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A game reference id (a thing, a network, a contact). On the wire it is always a decimal string, because the ids
/// pass 2^53 and a JSON number would lose digits.
/// </summary>
[JsonConverter(typeof(ThingIdJsonConverter))]
internal readonly struct ThingId : IEquatable<ThingId>
{
    internal ThingId(long value)
    {
        Value = value;
    }

    internal long Value { get; }

    /// <summary>Reads an id given as a decimal string or, for older clients, as a JSON integer.</summary>
    internal static bool TryRead(JToken? token, out ThingId id)
    {
        id = default;
        if (token == null || (token.Type != JTokenType.String && token.Type != JTokenType.Integer))
        {
            return false;
        }

        string text = token.Type == JTokenType.String
            ? token.Value<string>() ?? string.Empty
            : token.ToString(Formatting.None);
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return false;
        }

        id = new ThingId(value);
        return true;
    }

    public bool Equals(ThingId other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is ThingId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Writes a ThingId as its decimal string; reads a string or an integer.</summary>
internal sealed class ThingIdJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(ThingId) || objectType == typeof(ThingId?);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is ThingId id)
        {
            writer.WriteValue(id.ToString());
            return;
        }

        writer.WriteNull();
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer)
    {
        JToken token = JToken.Load(reader);
        if (token.Type == JTokenType.Null && objectType == typeof(ThingId?))
        {
            return null;
        }

        if (!ThingId.TryRead(token, out ThingId id))
        {
            throw new JsonSerializationException($"'{token}' is not a reference id.");
        }

        return id;
    }
}
