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
        "atmosphere_contents",
        "connections",
        "consumables",
        "container_contents",
        "deep_miner_spots",
        "describe_device",
        "describe_prefab",
        "feed_paths",
        "find_items",
        "find_things",
        "game_clock",
        "get_ic_source",
        "get_ic_status",
        "grid_survey",
        "inspect_slots",
        "item_totals",
        "landing_pads",
        "list_containers",
        "list_devices",
        "list_gateways",
        "looking_at",
        "network_snapshot",
        "outer_frames",
        "planet",
        "plants",
        "player_vitals",
        "read_devices",
        "read_logic",
        "read_logic_many",
        "read_memory",
        "reagents",
        "resolve_ic_selectors",
        "rocket_status",
        "rooms",
        "sample_logic",
        "screen_read",
        "thing_health",
        "vault_contents",
        "wall_map",
        "water_sources",
        "weather",
        "write_logic",
        "write_logic_many"
    };

    internal static bool Has(string? name) => name != null && ((HashSet<string>)Names).Contains(name);

    /// <summary>
    /// The arguments of a method catalogue entry whose form the game answers as a verb while the entry's other form
    /// keeps its per-tool handler: plant_genes' reads (inspect genes), paste_blueprint's status (inspect job). The checks that hold an entry's arguments to its
    /// handler pass over them.
    /// </summary>
    internal static IReadOnlyDictionary<string, HashSet<string>> Forms { get; } =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["plant_genes"] = new HashSet<string>(StringComparer.Ordinal) { "reference_ids", "include_notes" },
            ["paste_blueprint"] = new HashSet<string>(StringComparer.Ordinal) { "status" }
        };

    internal static bool HasArgument(string method, string argument) =>
        Forms.TryGetValue(method, out HashSet<string>? arguments) && arguments.Contains(argument);
}
