#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The MCP tools array plus instructions, generated from catalogue/verbs. One tool per verb: its description, a what
/// enum whose description lists each what's brief (or summary), and every option any of its whats takes, once, with
/// its schemas merged across those whats and the verb's short text for it (tool.options) as the only description;
/// the per-what detail is help {verb, what}. Every verb but help and run_script also takes the shaping options.
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

        if (name != "help" && name != "run_script")
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
            JsonObject property = merged[option];
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
