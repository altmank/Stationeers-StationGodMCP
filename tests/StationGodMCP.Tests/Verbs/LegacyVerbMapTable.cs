#nullable enable

using System.Text.Json.Nodes;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// LegacyVerbMap's table, generated from catalogue/verbs: per verb and what, each handler it calls (today's method),
/// when that handler is the one, and how each option maps onto the handler's arguments (renamed, merged, moved, kept,
/// a default changed). The hub's VerbPipeline (1b item 2) routes verb calls onto today's handlers with it until the
/// mod's verb layer replaces it (3a).
/// </summary>
internal static class LegacyVerbMapTable
{
    internal static JsonObject Build()
    {
        JsonArray entries = new();
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            foreach (JsonNode? node in source["handlers"]?.AsArray() ?? new JsonArray())
            {
                JsonObject handler = node!.AsObject();
                JsonObject entry = new()
                {
                    ["verb"] = verb,
                    ["what"] = what,
                    ["handler"] = handler["name"]!.DeepClone()
                };
                foreach (string key in new[] { "when", "note", "pending" })
                {
                    if (handler[key] != null)
                    {
                        entry[key] = handler[key]!.DeepClone();
                    }
                }

                entry["args"] = handler["args"]?.DeepClone() ?? new JsonArray();
                entries.Add(entry);
            }
        }

        return new JsonObject { ["entries"] = entries };
    }
}
