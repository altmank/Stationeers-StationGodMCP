#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The catalogue's files: the verb sources under catalogue/verbs and the JSON layout everything generated from them
/// is written in. The layout is two-space indents, LF line ends, and any object or array that fits in 120 columns on
/// one line ({"type": ["string", "null"]}), so a source file reads one key per line.
/// </summary>
internal static class CatalogueFiles
{
    private const int LineWidth = 120;

    private static readonly JsonSerializerOptions ScalarOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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

    /// <summary>The catalogue's one JSON layout, as text.</summary>
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

    internal static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    internal static JsonNode ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow })!;
}
