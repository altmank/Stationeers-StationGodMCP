using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>Small readings of JsonNode values, whether parsed (backed by JsonElement) or made in code.</summary>
internal static class JsonNodes
{
    /// <summary>The text of a string value; null for anything else.</summary>
    internal static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>The number of a number value; null for anything else.</summary>
    internal static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetValue(out JsonElement element))
        {
            return element.GetDouble();
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        if (value.TryGetValue(out long whole))
        {
            return whole;
        }

        if (value.TryGetValue(out int small))
        {
            return small;
        }

        return value.TryGetValue(out decimal exact) ? (double)exact : double.Parse(value.ToJsonString(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>True for a true boolean.</summary>
    internal static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    /// <summary>True when the list holds the word (any case).</summary>
    internal static bool Holds(JsonNode? list, string word)
    {
        if (list is not JsonArray entries)
        {
            return false;
        }

        foreach (JsonNode? entry in entries)
        {
            if (Text(entry) is { } text && string.Equals(text.Trim(), word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static JsonElement ToElement(JsonNode? node) => JsonSerializer.SerializeToElement(node);

    internal static JsonNode? FromElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
}
