using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// The MCP tools, from the method catalogue the sidecar was built with (catalogue.json, embedded): every method whose
/// x-mcp is not hidden becomes a tool with its name and description, its params as inputSchema (plus the sidecar's
/// own output_file and fields when its x-shaping is lists), and annotations that call it read only exactly when its
/// class is read and no x-class-when gives it another. The server's name and instructions come from the catalogue too.
/// </summary>
internal static class ToolCatalogue
{
    internal const string Resource = "StationGodMCP.catalogue.json";

    private const string OutputFileDescription = "Reply to a JSON file, answer a pointer (server instructions).";

    private const string FieldsDescription = "Keys kept per top-level list entry; a dotted name is a path into one list (things.position.x).";

    private static readonly Lazy<Loaded> State = new(() => Load(ReadResource()));

    /// <summary>The tools/list array.</summary>
    internal static JsonElement Tools => State.Value.Tools;

    /// <summary>Every tool's input schema as tools/list publishes it, by name: what ArgumentCheck holds calls to.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> InputSchemas => State.Value.InputSchemas;

    internal static IReadOnlySet<string> Names => State.Value.Names;

    /// <summary>Tools whose replies stay small (x-shaping none): they take no output_file or fields.</summary>
    internal static IReadOnlySet<string> SmallReplies => State.Value.SmallReplies;

    internal static string ServerName => State.Value.ServerName;

    internal static string Instructions => State.Value.Instructions;

    /// <summary>The embedded catalogue's exact bytes, as text.</summary>
    internal static string ReadResource()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource) ??
                              throw new InvalidOperationException($"The sidecar was built without {Resource}.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
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
                properties[ReplyShaping.OutputFileArgument] = new JsonObject
                {
                    ["type"] = new JsonArray("boolean", "string"),
                    ["description"] = OutputFileDescription
                };
                properties[ReplyShaping.FieldsArgument] = new JsonObject
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

    private static Loaded Load(string json)
    {
        JsonObject catalogue = JsonNode.Parse(json)!.AsObject();
        JsonElement tools = JsonSerializer.SerializeToElement(ToolsOf(catalogue));
        Dictionary<string, JsonElement> schemas = new(StringComparer.Ordinal);
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            schemas.Add(tool.GetProperty("name").GetString()!, tool.GetProperty("inputSchema"));
        }

        HashSet<string> small = new(StringComparer.Ordinal);
        foreach (JsonNode? method in catalogue["methods"]!.AsArray())
        {
            if ((string?)method!["x-shaping"] == "none")
            {
                small.Add((string)method["name"]!);
            }
        }

        JsonObject server = catalogue["server"]!.AsObject();
        return new Loaded(tools, schemas, new HashSet<string>(schemas.Keys, StringComparer.Ordinal), small,
            (string)server["name"]!, (string)server["instructions"]!);
    }

    private sealed record Loaded(
        JsonElement Tools,
        Dictionary<string, JsonElement> InputSchemas,
        HashSet<string> Names,
        HashSet<string> SmallReplies,
        string ServerName,
        string Instructions);
}
