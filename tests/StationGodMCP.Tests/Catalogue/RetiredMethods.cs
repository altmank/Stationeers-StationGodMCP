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
        "check_replaceable",
        "clean_cables",
        "clean_chutes",
        "clean_pipes",
        "connections",
        "consumables",
        "container_contents",
        "control_ic_execution",
        "deep_miner_spots",
        "describe_device",
        "describe_prefab",
        "dish_aim",
        "feed_paths",
        "find_items",
        "find_spot",
        "find_things",
        "game_clock",
        "get_ic_source",
        "get_ic_status",
        "grid_survey",
        "ignition_risk",
        "inspect_slots",
        "item_totals",
        "label",
        "landing_pads",
        "lint_layout",
        "lint_rules",
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
        "remove_structure",
        "replace_frames",
        "replace_walls",
        "resolve_ic_selectors",
        "rocket_flight_log",
        "rocket_forecast",
        "rocket_mining_options",
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
        "solar_aim",
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
        "write_memory",
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
