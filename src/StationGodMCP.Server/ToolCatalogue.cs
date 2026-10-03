using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The MCP tools a method catalogue gives: every method whose x-mcp is not hidden becomes a tool with its name and
/// description, its params as inputSchema (plus the sidecar's own output_file and fields when its x-shaping is lists),
/// and annotations that call it read only exactly when its class is read and no x-class-when gives it another. The
/// server's name and instructions come from the catalogue too. The sidecar starts with the catalogue it was built with
/// and switches to the mod's when the mod's hash differs.
/// </summary>
internal sealed record ToolSet(
    JsonElement Tools,
    IReadOnlyDictionary<string, JsonElement> InputSchemas,
    IReadOnlySet<string> Names,
    IReadOnlySet<string> SmallReplies,
    IReadOnlySet<string> RunInSidecar,
    string ServerName,
    string Instructions)
{
    private const string OutputFileDescription = "Reply to a JSON file, answer a pointer (server instructions).";

    private const string FieldsDescription = "Keys kept per top-level list entry; a dotted name is a path into one list (things.position.x).";

    private static readonly Lazy<ToolSet> Embedded = new(() => From(GameCatalogue.BuiltIn.Document, fallback: null));

    /// <summary>The tools of the catalogue the sidecar was built with.</summary>
    internal static ToolSet BuiltIn => Embedded.Value;

    /// <summary>The tools a catalogue gives; a catalogue without a server section keeps the built-in name and instructions.</summary>
    internal static ToolSet From(JsonElement catalogue) => From(catalogue, BuiltIn);

    private static ToolSet From(JsonElement catalogue, ToolSet? fallback)
    {
        JsonObject document = JsonObject.Create(catalogue)!;
        JsonElement tools = JsonSerializer.SerializeToElement(ToolsOf(document));
        Dictionary<string, JsonElement> schemas = new(StringComparer.Ordinal);
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            schemas.Add(tool.GetProperty("name").GetString()!, tool.GetProperty("inputSchema"));
        }

        HashSet<string> small = new(StringComparer.Ordinal);
        HashSet<string> sidecar = new(StringComparer.Ordinal);
        foreach (JsonNode? method in document["methods"]!.AsArray())
        {
            string name = (string)method!["name"]!;
            if ((string?)method["x-shaping"] == "none")
            {
                small.Add(name);
            }

            if ((string?)method["x-runs-in"] == "sidecar")
            {
                sidecar.Add(name);
            }
        }

        JsonObject? server = document["server"] as JsonObject;
        return new ToolSet(tools, schemas, new HashSet<string>(schemas.Keys, StringComparer.Ordinal), small, sidecar,
            (string?)server?["name"] ?? fallback?.ServerName ?? "StationGodMCP",
            (string?)server?["instructions"] ?? fallback?.Instructions ?? string.Empty);
    }

    /// <summary>The tools a catalogue gives, as tools/list publishes them.</summary>
    internal static JsonArray ToolsOf(JsonObject catalogue)
    {
        JsonArray tools = [];
        foreach (JsonNode? node in catalogue["methods"]!.AsArray())
        {
            JsonObject method = node!.AsObject();
            if ((string?)method["x-mcp"] == "hidden")
            {
                continue;
            }

            JsonObject inputSchema = method["params"]!.DeepClone().AsObject();
            if ((string?)method["x-shaping"] == "lists")
            {
                JsonObject properties = inputSchema["properties"]!.AsObject();
                properties[SidecarArguments.OutputFileArgument] = new JsonObject
                {
                    ["type"] = new JsonArray("boolean", "string"),
                    ["description"] = OutputFileDescription
                };
                properties[SidecarArguments.FieldsArgument] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = FieldsDescription
                };
            }

            bool readOnly = (string?)method["class"] == "read" && method["x-class-when"] == null;
            tools.Add(new JsonObject
            {
                ["name"] = method["name"]!.DeepClone(),
                ["description"] = method["description"]!.DeepClone(),
                ["inputSchema"] = inputSchema,
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = readOnly,
                    ["destructiveHint"] = !readOnly,
                    ["idempotentHint"] = readOnly,
                    ["openWorldHint"] = false
                }
            });
        }

        return tools;
    }
}

/// <summary>The built-in tool set, by the names the tests and the first tools/list use.</summary>
internal static class ToolCatalogue
{
    /// <summary>The tools/list array.</summary>
    internal static JsonElement Tools => ToolSet.BuiltIn.Tools;

    /// <summary>Every tool's input schema as tools/list publishes it, by name: what ArgumentCheck holds calls to.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> InputSchemas => ToolSet.BuiltIn.InputSchemas;

    internal static IReadOnlySet<string> Names => ToolSet.BuiltIn.Names;

    /// <summary>Tools whose replies stay small (x-shaping none): they take no output_file or fields.</summary>
    internal static IReadOnlySet<string> SmallReplies => ToolSet.BuiltIn.SmallReplies;

    internal static string ServerName => ToolSet.BuiltIn.ServerName;

    internal static string Instructions => ToolSet.BuiltIn.Instructions;

    /// <summary>The tools a catalogue gives, as tools/list publishes them.</summary>
    internal static JsonArray ToolsOf(JsonObject catalogue) => ToolSet.ToolsOf(catalogue);
}
