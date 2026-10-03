#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The catalogue's files: the sources under catalogue/ and the assembled catalogue.json at the repository root.
/// Assembly reads every method file in name order, inlines every $ref (defs/*.json) and every description include
/// (defs/text/*.txt), adds the shared reply keys that apply to each method's reply, and writes indented JSON with LF
/// line ends, so the file's bytes are the same on every machine (its SHA-256 is the catalogue's identity).
/// </summary>
internal static class CatalogueFiles
{
    internal const int CatalogueVersion = 1;

    internal static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The names whose presence anywhere in a method's params means it takes network handles.</summary>
    internal static readonly string[] NetworkHandleArguments = { "network_id", "network_ids", "join_to", "allow_bridge" };

    /// <summary>The argument that means a method may touch pipe networks (and so may answer gas_hold).</summary>
    internal const string GasHoldArgument = "acknowledge_gas_lost";

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "StationGodMCP.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("No StationGodMCP.sln above the test folder.");
    }

    internal static string SourceRoot => Path.Combine(RepositoryRoot(), "catalogue");

    internal static string AssembledPath => Path.Combine(RepositoryRoot(), "catalogue.json");

    private const int LineWidth = 120;

    private static readonly JsonSerializerOptions ScalarOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The catalogue's one JSON layout: two-space indents, LF line ends, and any object or array that fits in 120
    /// columns on one line ({"type": ["string", "null"]}), so a method file reads one key per line.
    /// </summary>
    internal static string Serialize(JsonNode node)
    {
        StringBuilder text = new StringBuilder();
        Format(node, 0, text);
        return text.Append('\n').ToString();
    }

    private static void Format(JsonNode? node, int indent, StringBuilder text)
    {
        string compact = Compact(node);
        if (indent + compact.Length <= LineWidth || !(node is JsonObject || node is JsonArray))
        {
            text.Append(compact);
            return;
        }

        string inner = new string(' ', indent + 2);
        if (node is JsonObject obj)
        {
            text.Append("{\n");
            int index = 0;
            foreach ((string key, JsonNode? value) in obj)
            {
                text.Append(inner).Append(Scalar(JsonValue.Create(key))).Append(": ");
                Format(value, indent + 2, text);
                text.Append(++index < obj.Count ? ",\n" : "\n");
            }

            text.Append(' ', indent).Append('}');
            return;
        }

        JsonArray array = (JsonArray)node!;
        text.Append("[\n");
        for (int index = 0; index < array.Count; index++)
        {
            text.Append(inner);
            Format(array[index], indent + 2, text);
            text.Append(index + 1 < array.Count ? ",\n" : "\n");
        }

        text.Append(' ', indent).Append(']');
    }

    private static string Compact(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Count == 0
            ? "{}"
            : "{" + string.Join(", ", obj.Select(pair => Scalar(JsonValue.Create(pair.Key)) + ": " + Compact(pair.Value))) + "}",
        JsonArray array => array.Count == 0 ? "[]" : "[" + string.Join(", ", array.Select(Compact)) + "]",
        _ => Scalar(node)
    };

    private static string Scalar(JsonNode? node) => node == null ? "null" : node.ToJsonString(ScalarOptions);

    internal static void WriteJson(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(node), new UTF8Encoding(false));
    }

    internal static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    internal static JsonNode ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow })!;

    /// <summary>The mod's version, as the csproj gives it (build.ps1 holds the other four places to it).</summary>
    internal static string ModVersion()
    {
        string csproj = File.ReadAllText(Path.Combine(RepositoryRoot(), "StationGodMCP.csproj"));
        return Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
    }

    /// <summary>The method source files, by method name.</summary>
    internal static SortedDictionary<string, JsonObject> MethodSources()
    {
        SortedDictionary<string, JsonObject> methods = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(Path.Combine(SourceRoot, "methods"), "*.json"))
        {
            methods.Add(Path.GetFileNameWithoutExtension(path), ReadJson(path).AsObject());
        }

        return methods;
    }

    /// <summary>The protocol's own method source files (protocol/), by method name.</summary>
    internal static SortedDictionary<string, JsonObject> ProtocolMethodSources()
    {
        SortedDictionary<string, JsonObject> methods = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(Path.Combine(SourceRoot, "protocol"), "*.json"))
        {
            methods.Add(Path.GetFileNameWithoutExtension(path), ReadJson(path).AsObject());
        }

        return methods;
    }

    /// <summary>catalogue.json as the sources give it, as text.</summary>
    internal static string Assemble() => Serialize(AssembleNode());

    internal static JsonObject AssembleNode()
    {
        string root = SourceRoot;
        JsonObject server = ReadJson(Path.Combine(root, "server.json")).AsObject();
        JsonObject errors = ReadJson(Path.Combine(root, "errors.json")).AsObject();
        JsonArray shared = ReadJson(Path.Combine(root, "shared.json"))["shared_reply_keys"]!.AsArray();

        JsonArray methods = new JsonArray();
        foreach ((string file, JsonObject source) in MethodSources())
        {
            JsonObject method = Resolve(source.DeepClone(), root).AsObject();
            if ((string?)method["name"] != file)
            {
                throw new InvalidOperationException($"methods/{file}.json names the method '{method["name"]}'.");
            }

            AddSharedReplyKeys(method, shared);
            methods.Add(method);
        }

        return new JsonObject
        {
            ["catalogue_version"] = CatalogueVersion,
            ["mod_version"] = ModVersion(),
            ["server"] = Resolve(server.DeepClone(), root),
            ["errors"] = errors.DeepClone(),
            ["shared_reply_keys"] = Resolve(shared.DeepClone(), root),
            ["protocol_methods"] = ProtocolMethods(root),
            ["methods"] = methods
        };
    }

    private static JsonArray ProtocolMethods(string root)
    {
        JsonArray methods = new JsonArray();
        foreach ((string file, JsonObject source) in ProtocolMethodSources())
        {
            JsonObject method = Resolve(source.DeepClone(), root).AsObject();
            if ((string?)method["name"] != file)
            {
                throw new InvalidOperationException($"protocol/{file}.json names the method '{method["name"]}'.");
            }

            methods.Add(method);
        }

        return methods;
    }

    /// <summary>The shared reply keys that apply to a method, by the rules applies_when names.</summary>
    internal static IEnumerable<JsonObject> SharedKeysFor(JsonObject method, JsonArray shared)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        CollectPropertyNames(method["params"]!, names);
        bool topLevelGasHold = method["params"]?["properties"]?.AsObject().ContainsKey(GasHoldArgument) == true;
        foreach (JsonNode? entry in shared)
        {
            string when = (string)entry!["applies_when"]!;
            bool applies = when switch
            {
                "takes_network_handles" => NetworkHandleArguments.Any(names.Contains),
                "may_touch_pipe_networks" => topLevelGasHold,
                _ => throw new InvalidOperationException($"Unknown applies_when '{when}'.")
            };
            if (applies)
            {
                yield return entry.AsObject();
            }
        }
    }

    internal static void CollectPropertyNames(JsonNode node, HashSet<string> names)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (key == "properties" && value is JsonObject properties)
                {
                    foreach ((string name, JsonNode? schema) in properties)
                    {
                        names.Add(name);
                        if (schema != null)
                        {
                            CollectPropertyNames(schema, names);
                        }
                    }
                }
                else if (value != null && key != "description")
                {
                    CollectPropertyNames(value, names);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item != null)
                {
                    CollectPropertyNames(item, names);
                }
            }
        }
    }

    private static void AddSharedReplyKeys(JsonObject method, JsonArray shared)
    {
        JsonObject properties = method["reply"]!["properties"] as JsonObject ?? new JsonObject();
        method["reply"]!["properties"] ??= properties;
        foreach (JsonObject entry in SharedKeysFor(method, shared).ToList())
        {
            string key = (string)entry["key"]!;
            if (!properties.ContainsKey(key))
            {
                properties[key] = entry["schema"]!.DeepClone();
            }
        }
    }

    // $ref inlined (a file under catalogue/), description arrays joined with their includes read.
    private static JsonNode Resolve(JsonNode node, string root)
    {
        if (node is JsonObject obj)
        {
            if (obj.Count == 1 && obj["$ref"] is JsonValue reference)
            {
                string path = Path.Combine(root, (string)reference!);
                return Resolve(ReadJson(path), root);
            }

            JsonObject resolved = new JsonObject();
            foreach ((string key, JsonNode? value) in obj)
            {
                resolved[key] = key == "description" && value is JsonArray parts
                    ? JsonValue.Create(Joined(parts, root))
                    : value == null ? null : Resolve(value.DeepClone(), root);
            }

            return resolved;
        }

        if (node is JsonArray array)
        {
            JsonArray resolved = new JsonArray();
            foreach (JsonNode? item in array)
            {
                resolved.Add(item == null ? null : Resolve(item.DeepClone(), root));
            }

            return resolved;
        }

        return node.DeepClone();
    }

    private static string Joined(JsonArray parts, string root)
    {
        StringBuilder text = new StringBuilder();
        foreach (JsonNode? part in parts)
        {
            if (part is JsonObject include && include["$include"] is JsonValue file)
            {
                text.Append(ReadText(Path.Combine(root, (string)file!)));
            }
            else
            {
                text.Append((string)part!);
            }
        }

        return text.ToString();
    }

    // An include's text: LF line ends, one trailing line end (an editor's) dropped.
    private static string ReadText(string path)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.EndsWith('\n') ? text.Substring(0, text.Length - 1) : text;
    }
}
