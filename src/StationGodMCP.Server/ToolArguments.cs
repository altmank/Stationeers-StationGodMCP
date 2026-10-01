using System.Text.Json;

namespace StationGodMCP.Server;

/// <summary>
/// The top-level argument names each tool's input schema declares, without the sidecar's own (ReplyShaping): what
/// the mod holds pipe clients to. The mod reads them from tool-arguments.json (the repository root, embedded in the
/// mod's DLL); ToolArgumentsFileTests keeps that file equal to this.
/// </summary>
internal static class ToolArguments
{
    internal static SortedDictionary<string, string[]> Declared() =>
        new(Program.InputSchemas.ToDictionary(
                tool => tool.Key,
                tool => tool.Value.GetProperty("properties").EnumerateObject()
                    .Select(property => property.Name)
                    .Where(name => !ReplyShaping.Arguments.Contains(name))
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
            StringComparer.Ordinal);

    /// <summary>The file's content: one line per tool, so a change reads as a one-line diff.</summary>
    internal static string FileText()
    {
        IEnumerable<string> lines = Declared().Select(tool =>
            $"  {JsonSerializer.Serialize(tool.Key)}: {JsonSerializer.Serialize(tool.Value)}");
        return "{\n" + string.Join(",\n", lines) + "\n}\n";
    }
}
