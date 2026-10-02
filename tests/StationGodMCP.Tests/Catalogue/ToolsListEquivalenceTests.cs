#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// While the tool definitions move out of the sidecar: tools/list built from the catalogue equals the one
/// ToolDefinitions built, tool by tool as parsed JSON, apart from the range keywords the catalogue adds from the
/// handlers (the sidecar does not enforce them: ArgumentCheck leaves numeric ranges to the mod). Deleted with
/// ToolDefinitions.
/// </summary>
public sealed class ToolsListEquivalenceTests
{
    private static readonly string[] RangeKeywords =
        { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "pattern" };

    [Fact]
    public void ToolsListFromTheCatalogueEqualsToolDefinitionsApartFromRanges()
    {
        JsonSerializerOptions options = (JsonSerializerOptions)typeof(Program)
            .GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Dictionary<string, JsonNode> before = ByName(JsonSerializer.SerializeToNode(ToolDefinitions.All, options)!.AsArray());
        Dictionary<string, JsonNode> after = ByName(JsonNode.Parse(ToolCatalogue.Tools.GetRawText())!.AsArray());

        Assert.Equal(before.Keys.OrderBy(name => name), after.Keys.OrderBy(name => name));
        List<string> differ = before.Keys.Where(name => !JsonNode.DeepEquals(WithoutRanges(before[name]), WithoutRanges(after[name])))
            .ToList();
        Assert.True(differ.Count == 0, "tools/list differs for: " + string.Join(", ", differ));
    }

    [Fact]
    public void AnnotationsAndShapingArgumentsAreAsBefore()
    {
        Assert.Equal(ToolDefinitions.SmallReplies.OrderBy(name => name), ToolCatalogue.SmallReplies.OrderBy(name => name));
        Assert.Equal(ToolDefinitions.Names.OrderBy(name => name), ToolCatalogue.Names.OrderBy(name => name));
    }

    private static Dictionary<string, JsonNode> ByName(JsonArray tools) =>
        tools.ToDictionary(tool => (string)tool!["name"]!, tool => tool!.DeepClone(), StringComparer.Ordinal);

    private static JsonNode WithoutRanges(JsonNode node)
    {
        JsonNode copy = node.DeepClone();
        Strip(copy);
        return copy;
    }

    private static void Strip(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (string keyword in RangeKeywords)
            {
                // A property named like a keyword (an argument called "pattern") is kept: only schema keywords go.
                if (obj[keyword] is JsonValue)
                {
                    obj.Remove(keyword);
                }
            }

            foreach ((string _, JsonNode? value) in obj.ToList())
            {
                if (value != null)
                {
                    Strip(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item != null)
                {
                    Strip(item);
                }
            }
        }
    }
}
