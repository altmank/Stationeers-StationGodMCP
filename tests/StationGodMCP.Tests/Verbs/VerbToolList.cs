#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The MCP tools array plus instructions, generated from catalogue/verbs. One tool per verb: its description, a what
/// enum whose description lists each what's brief (or summary), and every option any of its whats takes, once, with
/// its schemas merged across those whats and the verb's short text for it (tool.options) as the only description;
/// the per-what detail is help {verb, what}. Every verb but help and the hub-only ones (run_script) also takes the
/// shaping options.
/// </summary>
internal static class VerbToolList
{
    internal static JsonObject Build()
    {
        JsonArray tools = new();
        foreach (JsonObject verb in VerbCatalogue.Verbs)
        {
            tools.Add(Tool(verb));
        }

        return new JsonObject
        {
            ["instructions"] = VerbCatalogue.Shared["instructions"]!.DeepClone(),
            ["tools"] = tools
        };
    }

    internal static JsonObject Tool(JsonObject verb)
    {
        string name = VerbCatalogue.NameOf(verb);
        JsonObject briefs = verb["tool"]?["options"] as JsonObject ?? new JsonObject();
        JsonObject properties = new();
        if (VerbCatalogue.HasWhats(verb))
        {
            JsonArray values = new();
            List<string> described = new();
            foreach ((string what, JsonObject source) in VerbCatalogue.WhatsOf(verb))
            {
                values.Add(what);
                described.Add(what + ": " + (string)(source["brief"] ?? source["summary"])!);
            }

            properties["what"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = values,
                ["description"] = string.Join("; ", described)
            };
        }

        Dictionary<string, JsonObject> merged = new();
        List<string> order = new();
        foreach ((string _, JsonObject source) in VerbCatalogue.WhatsOf(verb))
        {
            foreach ((string option, JsonObject resolved) in VerbCatalogue.OptionsOf(source))
            {
                JsonObject schema = resolved["schema"]!.AsObject();
                if (merged.TryGetValue(option, out JsonObject? seen))
                {
                    merged[option] = Merge(seen, schema);
                }
                else
                {
                    merged[option] = schema.DeepClone().AsObject();
                    order.Add(option);
                }
            }
        }

        if (name != "help" && !VerbCatalogue.IsHubOnly(verb))
        {
            foreach (string shaping in VerbCatalogue.Shaping)
            {
                if (!merged.ContainsKey(shaping))
                {
                    merged[shaping] = VerbCatalogue.SharedOption(shaping)["schema"]!.DeepClone().AsObject();
                    order.Add(shaping);
                }
            }
        }

        foreach (string option in order)
        {
            JsonObject property = Published(merged[option]);
            if (briefs[option] is JsonValue brief)
            {
                property["description"] = (string)brief!;
            }

            properties[option] = property;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = verb["description"]!.DeepClone(),
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties }
        };
    }

    /// <summary>
    /// The part of an option's schema the tool list carries: its types, enum and item schema. Bounds (minimum,
    /// maximum, minItems, maxItems) stay in the catalogue, where the hub's OptionCheck holds calls to them and the
    /// help page states them.
    /// </summary>
    internal static JsonObject Published(JsonObject schema)
    {
        JsonObject published = new();
        bool hasEnum = schema["enum"] is JsonArray;
        foreach ((string key, JsonNode? value) in schema)
        {
            if (key == "enum" || (key == "type" && !hasEnum))
            {
                // an enum's values give its type, so its "type" is left out
                published[key] = value?.DeepClone();
            }
            else if (key == "items" && value is JsonObject items && SaysMore(items))
            {
                published[key] = Published(items);
            }
        }

        return published;
    }

    // An item schema worth publishing: an enum, or a scalar type (an array's object or array items say nothing the
    // help does not).
    private static bool SaysMore(JsonObject items) =>
        items["enum"] is JsonArray ||
        VerbText.TypesOf(items).Any(type => type is "string" or "integer" or "number" or "boolean");

    /// <summary>
    /// Two whats' schemas for one option as one: the types joined, enums joined when both have one, items merged,
    /// and any other keyword kept only when both agree.
    /// </summary>
    internal static JsonObject Merge(JsonObject first, JsonObject second)
    {
        if (JsonNode.DeepEquals(first, second))
        {
            return first;
        }

        JsonObject merged = new();
        List<string> types = VerbText.TypesOf(first).Union(VerbText.TypesOf(second)).ToList();
        if (types.Count == 1)
        {
            merged["type"] = types[0];
        }
        else if (types.Count > 1)
        {
            merged["type"] = new JsonArray(types.Select(type => (JsonNode)JsonValue.Create(type)!).ToArray());
        }

        foreach ((string key, JsonNode? value) in first)
        {
            if (key == "type")
            {
                continue;
            }

            JsonNode? other = second[key];
            if (key == "enum" && value is JsonArray a && other is JsonArray b)
            {
                JsonArray union = new();
                foreach (JsonNode? item in a.Concat(b))
                {
                    if (!union.Any(seen => JsonNode.DeepEquals(seen, item)))
                    {
                        union.Add(item?.DeepClone());
                    }
                }

                merged["enum"] = union;
            }
            else if (key == "items" && value is JsonObject x && other is JsonObject y)
            {
                merged["items"] = Merge(x, y).DeepClone();
            }
            else if (other != null && JsonNode.DeepEquals(value, other))
            {
                merged[key] = value?.DeepClone();
            }
        }

        return merged;
    }
}
