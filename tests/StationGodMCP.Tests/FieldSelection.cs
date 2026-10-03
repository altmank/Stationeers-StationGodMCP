using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests;

/// <summary>
/// The reference the mod's single-name selectors are tested against (protocol.md, Field selectors): the keys to keep
/// in every object entry of the reply's top-level lists; the other top-level keys stay as they are. A name no entry has
/// is listed in fields_unmatched, so a misspelt field is seen rather than silently giving empty entries.
/// </summary>
internal sealed record FieldSelection(IReadOnlyList<string> Names)
{
    internal const string UnmatchedKey = "fields_unmatched";

    internal static FieldSelection Of(JsonElement value) =>
        new(value.EnumerateArray().Select(name => name.GetString()!.Trim()).Distinct(StringComparer.Ordinal).ToArray());

    internal JsonElement Apply(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object)
        {
            return reply;
        }

        JsonObject shaped = JsonObject.Create(reply)!;
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool anyEntry = false;
        foreach (JsonArray list in shaped.Select(property => property.Value).OfType<JsonArray>())
        {
            foreach (JsonObject entry in list.OfType<JsonObject>())
            {
                anyEntry = true;
                seen.UnionWith(entry.Select(property => property.Key));
                foreach (string key in entry.Select(property => property.Key).Where(key => !Names.Contains(key)).ToArray())
                {
                    entry.Remove(key);
                }
            }
        }

        string[] unmatched = Names.Where(name => !seen.Contains(name)).ToArray();
        if (anyEntry && unmatched.Length > 0)
        {
            shaped[UnmatchedKey] = new JsonArray(unmatched.Select(name => (JsonNode)JsonValue.Create(name)).ToArray());
        }

        return JsonSerializer.SerializeToElement(shaped);
    }
}
