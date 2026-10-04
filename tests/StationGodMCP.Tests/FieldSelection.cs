using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests;

/// <summary>
/// The reference the mod's single-name selectors are tested against (protocol.md, Field selectors). At the reply's
/// top: a key a name names is kept whole; numbers, strings, booleans and nulls are kept; every other list keeps its
/// object entries, each with only the named keys, and every other object keeps only the named keys; a list or object
/// that had something and kept none of it is left out. A name that matched nothing is listed in fields_unmatched, with fields_valid
/// naming, sorted, the keys the reply had (its top-level keys and the keys of the entries it shaped), so a misspelt field is
/// seen rather than silently giving an empty reply. A reply with an empty top-level list and no list entry reports
/// nothing: the name may be missing only because there were no entries.
/// </summary>
internal sealed record FieldSelection(IReadOnlyList<string> Names)
{
    internal const string UnmatchedKey = "fields_unmatched";
    internal const string ValidKey = "fields_valid";
    private const int MaximumValidKeys = 100;

    internal static FieldSelection Of(JsonElement value) =>
        new(value.EnumerateArray().Select(name => name.GetString()!.Trim()).Distinct(StringComparer.Ordinal).ToArray());

    internal JsonElement Apply(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object)
        {
            return reply;
        }

        JsonObject shaped = [];
        SortedSet<string> valid = new(StringComparer.Ordinal);
        HashSet<string> matched = new(StringComparer.Ordinal);
        bool anyEntry = false;
        bool emptyList = false;
        void Note(string key)
        {
            valid.Add(key);
        }

        JsonObject Shape(JsonElement entry)
        {
            JsonObject kept = [];
            foreach (JsonProperty property in entry.EnumerateObject())
            {
                Note(property.Name);
                if (Names.Contains(property.Name, StringComparer.Ordinal))
                {
                    matched.Add(property.Name);
                    kept[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }

            return kept;
        }

        foreach (JsonProperty property in reply.EnumerateObject())
        {
            Note(property.Name);
            JsonElement value = property.Value;
            if (value.ValueKind == JsonValueKind.Array)
            {
                emptyList |= value.GetArrayLength() == 0;
                anyEntry |= value.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.Object);
            }

            if (Names.Contains(property.Name, StringComparer.Ordinal))
            {
                matched.Add(property.Name);
                shaped[property.Name] = JsonNode.Parse(value.GetRawText());
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                {
                    List<JsonObject> entries = value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object)
                        .Select(Shape).ToList();
                    if (entries.Any(entry => entry.Count > 0) || value.GetArrayLength() == 0)
                    {
                        shaped[property.Name] = new JsonArray(entries.Select(entry => (JsonNode)entry).ToArray());
                    }

                    break;
                }
                case JsonValueKind.Object:
                {
                    JsonObject kept = Shape(value);
                    if (kept.Count > 0 || !value.EnumerateObject().Any())
                    {
                        shaped[property.Name] = kept;
                    }

                    break;
                }
                default:
                    shaped[property.Name] = JsonNode.Parse(value.GetRawText());
                    break;
            }
        }

        string[] unmatched = Names.Where(name => !matched.Contains(name)).ToArray();
        if ((anyEntry || !emptyList) && unmatched.Length > 0)
        {
            shaped[UnmatchedKey] = new JsonArray(unmatched.Select(name => (JsonNode)JsonValue.Create(name)).ToArray());
            shaped[ValidKey] = new JsonArray(valid.Take(MaximumValidKeys).Select(name => (JsonNode)JsonValue.Create(name)).ToArray());
        }

        return JsonSerializer.SerializeToElement(shaped);
    }
}
