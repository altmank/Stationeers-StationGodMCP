using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Today's handler methods as the built-in method catalogue (catalogue.json) declares them: each one's params schema and
/// file arguments.
/// </summary>
internal sealed class HandlerSchemas
{
    private static readonly Lazy<HandlerSchemas> BuiltInSchemas = new(() => new HandlerSchemas(GameCatalogue.BuiltIn.Document));

    private readonly Dictionary<string, JsonObject> _params = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<FileArgument>> _files = new(StringComparer.Ordinal);

    internal HandlerSchemas(System.Text.Json.JsonElement catalogue)
    {
        JsonObject document = JsonObject.Create(catalogue)!;
        foreach (JsonNode? node in document["methods"]!.AsArray())
        {
            JsonObject method = node!.AsObject();
            string name = (string)method["name"]!;
            _params[name] = method["params"]?.DeepClone() as JsonObject ?? new JsonObject();
            _files[name] = FileArguments.Of(method);
        }
    }

    internal static HandlerSchemas BuiltIn => BuiltInSchemas.Value;

    internal bool Has(string handler) => _params.ContainsKey(handler);

    /// <summary>The handler's params schema; an empty object for a handler the catalogue does not hold.</summary>
    internal JsonObject Params(string handler) =>
        _params.TryGetValue(handler, out JsonObject? schema) ? schema : new JsonObject();

    /// <summary>One argument's schema, or null when the handler does not declare it.</summary>
    internal JsonObject? Arg(string handler, string arg) => Params(handler)["properties"]?[arg] as JsonObject;

    internal IReadOnlyList<FileArgument> Files(string handler) =>
        _files.TryGetValue(handler, out IReadOnlyList<FileArgument>? files) ? files : [];

    /// <summary>True when the schema (or one of its oneOf forms) is a list.</summary>
    internal static bool IsList(JsonObject? schema) => HasType(schema, "array");

    internal static bool HasType(JsonObject? schema, string type)
    {
        if (schema == null)
        {
            return false;
        }

        foreach (string declared in OptionCheck.TypesOf(schema))
        {
            if (declared == type)
            {
                return true;
            }
        }

        foreach (JsonNode? branch in schema["oneOf"] as JsonArray ?? [])
        {
            if (branch is JsonObject form && HasType(form, type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The declared keys of an object schema (or of its object form); null when it declares none.</summary>
    internal static JsonObject? Properties(JsonObject? schema)
    {
        if (schema == null)
        {
            return null;
        }

        if (schema["properties"] is JsonObject properties)
        {
            return properties;
        }

        foreach (JsonNode? branch in schema["oneOf"] as JsonArray ?? [])
        {
            if (branch is JsonObject form && form["properties"] is JsonObject formProperties)
            {
                return formProperties;
            }
        }

        return null;
    }

    /// <summary>Every key name declared anywhere in the handler's params (nested objects and list items too).</summary>
    internal HashSet<string> AllNames(string handler)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        Walk(Params(handler), names, 0);
        return names;
    }

    private static void Walk(JsonObject? schema, HashSet<string> names, int depth)
    {
        if (schema == null || depth > 8)
        {
            return;
        }

        if (schema["properties"] is JsonObject properties)
        {
            foreach ((string name, JsonNode? child) in properties)
            {
                names.Add(name);
                Walk(child as JsonObject, names, depth + 1);
            }
        }

        Walk(schema["items"] as JsonObject, names, depth + 1);
        foreach (JsonNode? branch in schema["oneOf"] as JsonArray ?? [])
        {
            Walk(branch as JsonObject, names, depth + 1);
        }
    }
}
