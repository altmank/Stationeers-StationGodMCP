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
        "clean_cables",
        "clean_chutes",
        "clean_pipes",
        "connections",
        "consumables",
        "control_ic_execution",
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
        "label",
        "landing_pads",
        "list_containers",
        "list_devices",
        "list_gateways",
        "looking_at",
        "move_gas",
        "move_item",
        "move_player",
        "network_snapshot",
        "outer_frames",
        "paint",
        "planet",
        "plant_genes",
        "plants",
        "player_vitals",
        "read_devices",
        "read_logic",
        "read_logic_many",
        "read_memory",
        "reagents",
        "remove_cables",
        "remove_chutes",
        "remove_pipes",
        "replace_frames",
        "replace_walls",
        "resolve_ic_selectors",
        "rocket_flight_log",
        "rocket_status",
        "rooms",
        "sample_logic",
        "screen_press",
        "screen_read",
        "set_battery_charge",
        "set_ic_pins",
        "set_ic_source",
        "set_uplink",
        "silo_deposit",
        "silo_withdraw",
        "thing_health",
        "upgrade_cables",
        "upgrade_pipes",
        "vault_contents",
        "vault_deposit",
        "vault_transfer",
        "vault_withdraw",
        "wall_map",
        "water_sources",
        "weather",
        "write_logic",
        "write_logic_many",
        "write_memory"
    };

    internal static bool Has(string? name) => name != null && ((HashSet<string>)Names).Contains(name);

    /// <summary>
    /// The arguments of a method catalogue entry whose form the game answers as a verb while the entry's other form
    /// keeps its per-tool handler: paste_blueprint's status (inspect job). The checks that hold an entry's arguments to its
    /// handler pass over them.
    /// </summary>
    internal static IReadOnlyDictionary<string, HashSet<string>> Forms { get; } =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["paste_blueprint"] = new HashSet<string>(StringComparer.Ordinal) { "status" }
        };

    internal static bool HasArgument(string method, string argument) =>
        Forms.TryGetValue(method, out HashSet<string>? arguments) && arguments.Contains(argument);
}
