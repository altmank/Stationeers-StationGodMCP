#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Tests.CatalogueChecks;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// One list a what can cut: the reply's truncated.list path (the one-id form; a call with an id list writes it under
/// results[].), the default cap (null where the cut has no fixed number) and the argument that lifts it
/// (list_limits.&lt;list&gt;, limit, or the handler's own argument; null where none does).
/// </summary>
internal sealed record CutRow(string Verb, string What, string Path, int? Cap, string? LiftedBy);

/// <summary>
/// generated/verbs/cuts.json, from the catalogue alone. A cut is stated by exactly one source: x-default-limits (the
/// writer's cap, lifted by list_limits), a limit option (the what's main list, lifted by limit) or x-handler-cuts (a
/// list a handler cuts on its own).
/// </summary>
internal static class VerbCuts
{
    internal const string File = "cuts.json";

    /// <summary>Every cut row, in the order the source states them.</summary>
    internal static List<CutRow> Collect()
    {
        List<CutRow> rows = new();
        foreach ((string verb, string what, JsonObject source) in VerbCatalogue.Whats())
        {
            if (VerbCatalogue.IsHub(source))
            {
                continue;
            }

            foreach ((string list, JsonNode? cap) in source["x-default-limits"] as JsonObject ?? new JsonObject())
            {
                rows.Add(new CutRow(verb, what, list, (int)cap!, "list_limits." + list));
            }

            if (source["reply"]?["list"] is JsonValue main
                && VerbCatalogue.OptionsOf(source).Any(option => option.Name == "limit"))
            {
                JsonObject limit = VerbCatalogue.OptionsOf(source).First(option => option.Name == "limit").Option;
                rows.Add(new CutRow(verb, what, (string)main!, DefaultOf(limit), "limit"));
            }

            foreach (JsonNode? cut in source["x-handler-cuts"] as JsonArray ?? new JsonArray())
            {
                rows.Add(new CutRow(verb, what, (string)cut!["path"]!, (int?)cut["cap"], (string?)cut["lifted_by"]));
            }
        }

        return rows;
    }

    /// <summary>The rows stated by more than one source, as "verb what path".</summary>
    internal static List<string> StatedTwice(IEnumerable<CutRow> rows) =>
        rows.GroupBy(row => (row.Verb, row.What, row.Path))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.Verb + " " + group.Key.What + " " + group.Key.Path)
            .ToList();

    /// <summary>Sorted by verb, what and path, so the file is stable.</summary>
    internal static string Build()
    {
        JsonArray array = new();
        foreach (CutRow row in Collect()
                     .OrderBy(row => row.Verb, StringComparer.Ordinal)
                     .ThenBy(row => row.What, StringComparer.Ordinal)
                     .ThenBy(row => row.Path, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["verb"] = row.Verb,
                ["what"] = row.What,
                ["path"] = row.Path,
                ["cap"] = row.Cap,
                ["lifted_by"] = row.LiftedBy
            });
        }

        return CatalogueFiles.Serialize(array);
    }

    // The limit option's plain default; null where it has none or depends on the form of the call.
    private static int? DefaultOf(JsonObject limit) =>
        int.TryParse((string?)limit["default"], NumberStyles.None, CultureInfo.InvariantCulture, out int cap)
            ? cap
            : null;
}
