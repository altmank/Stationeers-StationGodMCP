#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The top-level argument names each tool takes (tool-arguments.json, written from the sidecar's schemas), so a pipe
/// client is held to them as the sidecar holds an MCP client: an argument the tool does not take is invalid_argument,
/// naming the nearest one it does take and every one it takes, before the tool runs. Without the check a misspelt
/// filter (prefab for prefab_contains) was dropped and the call answered the whole world. Null is an omitted argument.
/// Nested objects are left to each tool. A tool the file does not list is not checked.
/// </summary>
internal sealed class DeclaredArguments
{
    private readonly Dictionary<string, List<string>> _byTool;

    private DeclaredArguments(Dictionary<string, List<string>> byTool)
    {
        _byTool = byTool;
    }

    internal static DeclaredArguments None { get; } =
        new DeclaredArguments(new Dictionary<string, List<string>>(StringComparer.Ordinal));

    internal int ToolCount => _byTool.Count;

    internal static DeclaredArguments Parse(string json)
    {
        Dictionary<string, List<string>> byTool = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (JProperty tool in JObject.Parse(json).Properties())
        {
            List<string> names = new List<string>();
            foreach (JToken name in (JArray)tool.Value)
            {
                names.Add((string)name!);
            }

            names.Sort(StringComparer.Ordinal);
            byTool[tool.Name] = names;
        }

        return new DeclaredArguments(byTool);
    }

    /// <summary>Refuses (invalid_argument) a request that gives an argument its tool does not take.</summary>
    internal void Check(string tool, JObject? parameters)
    {
        if (parameters == null || !_byTool.TryGetValue(tool, out List<string> known))
        {
            return;
        }

        StringBuilder? problems = null;
        foreach (JProperty property in parameters.Properties())
        {
            if (property.Value.Type == JTokenType.Null || known.BinarySearch(property.Name, StringComparer.Ordinal) >= 0)
            {
                continue;
            }

            problems ??= new StringBuilder();
            string? nearest = NearestName.Of(property.Name, known);
            problems.Append(nearest != null
                ? $"Unknown argument '{property.Name}'; did you mean '{nearest}'? "
                : $"Unknown argument '{property.Name}'. ");
        }

        if (problems != null)
        {
            string list = known.Count == 0 ? "none" : string.Join(", ", known);
            throw ApiErrors.InvalidArgument($"{problems}{tool} takes: {list}. Nothing was run.");
        }
    }
}
