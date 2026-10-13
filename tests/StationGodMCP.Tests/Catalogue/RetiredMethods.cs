#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The method catalogue's entries (catalogue/methods) whose game handler is gone because the game answers that work as
/// a verb. The MCP server and the client still read the method catalogue, so it keeps them; the checks that hold the
/// method catalogue to the game's handlers and reply views pass over them.
/// </summary>
internal static class RetiredMethods
{
    internal static IReadOnlyCollection<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "consumables",
        "deep_miner_spots",
        "find_items",
        "feed_paths",
        "find_things",
        "item_totals",
        "grid_survey",
        "landing_pads",
        "list_containers",
        "list_devices",
        "list_gateways",
        "outer_frames",
        "plants",
        "rooms",
        "sample_logic",
        "wall_map",
        "water_sources"
    };

    internal static bool Has(string? name) => name != null && ((HashSet<string>)Names).Contains(name);

    /// <summary>
    /// The arguments of a method catalogue entry whose form the game answers as a verb while the entry's other form
    /// keeps its per-tool handler: connections' network and box forms (survey network). The checks that hold an
    /// entry's arguments to its handler pass over them.
    /// </summary>
    internal static IReadOnlyDictionary<string, HashSet<string>> Forms { get; } =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["connections"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "network_id", "kind", "limit", "offset", "prefab_contains", "open_ends_only", "min", "max", "near",
                "radius_m", "summarize"
            }
        };

    internal static bool HasArgument(string method, string argument) =>
        Forms.TryGetValue(method, out HashSet<string>? arguments) && arguments.Contains(argument);
}
