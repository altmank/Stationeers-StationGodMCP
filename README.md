# StationGod MCP

A Stationeers mod that lets an AI agent, such as Claude Code or Codex, read and run your base while you play. It
connects the agent to the game through the Model Context Protocol (MCP), the standard way these agents call outside
tools. The agent can look up anything you could look up yourself, and act on it: write logic, program chips, move
items and gas, and build or rebuild cables, pipes, chutes, walls, frames and structures with the checks the game
itself applies.

## What the agent can do

- **Devices and logic:** find any device in the world, read and write its logic values, slots and memory, many at
  once, and record how values change over time.
- **Chips:** read, write, compile, pause, step and restart IC10 programs, and set IC Housing pins. With
  StationeersLua, also Lua chips in IC Housings, consoles, computers, tablets and visors.
- **Items, rooms and planet:** find, count and move items, label and paint things, move gas; rooms and their air, the
  planet's atmosphere and weather, plants and their genes, damage, fire risk, food and water.
- **Solar, dishes and traders:** aim solar panels and satellite dishes, check landing pads, buy and sell with a
  landed trader.
- **Building:** plan, lay, remove and reroute cable, pipe and chute runs; upgrade and tidy whole networks; swap walls,
  windows and frames without opening a room; place and remove any structure a kit builds; paste BlueprintMod
  blueprints.
- **Console:** run any console command and read the console.

## Requirements

- Stationeers with BepInEx 5.4 and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).
- Windows x64 for the self-contained sidecar, or the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  for the portable one.
- An AI agent that runs MCP servers over stdio: Claude Code, Codex, or another MCP client.
- Optional: [StationeersLua](https://steamcommunity.com/sharedfiles/filedetails/?id=3659911735) for Lua chips,
  [BlueprintMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3672138641) for blueprints.

## Install in short

The mod has two parts: the mod, which runs inside the game, and the **sidecar**, a small program your agent starts
that passes its requests to the game.

1. Subscribe on the Steam Workshop and enable StationGod MCP in StationeersLaunchPad.
2. In the mod's `Sidecar` folder, extract `StationGodMCP.Server-win-x64.zip` (no .NET needed) or
   `StationGodMCP.Server-portable.zip` (needs .NET 8) to a folder outside every mod folder, for example
   `%LOCALAPPDATA%\StationGodMCP\server`. The same archives, with `SHA256SUMS.txt`, and the mod package
   `StationGodMCP.zip` for installs without the Workshop (extract it into `Documents\My Games\Stationeers\mods`)
   are on the [GitHub Releases](https://github.com/altmank/Stationeers-StationGodMCP/releases) page.
3. Register it with your agent. Claude Code, in PowerShell:

   ```powershell
   claude mcp add --scope user --transport stdio stationeers -- "$env:LOCALAPPDATA\StationGodMCP\server\StationGodMCP.Server.exe"
   ```

4. Turn on *Start Local Host* in the game's settings and load a save. The mod only answers in a game that hosts.

Full steps, Codex, dedicated servers and multiplayer: **[docs/install.md](docs/install.md)**.

## First steps

Ask the agent to call these, in order, to see that everything works:

1. `mod_info`: the mod's version and the pipe it listens on.
2. `list_devices`: every device in your world, with the reference ids other tools take.
3. `looking_at`: what your crosshair is on. Point at something and ask the agent about it.

From there, ask in plain words: "which rooms are losing pressure", "what does this chip do", "run heavy cable from
the APC to the new room along the frames". The agent reads each tool's own description, which lists every argument.

## Tools

85 tools, in these areas. Each page lists its tools with what they take and give back.

| Area | Tools | Page |
| --- | --- | --- |
| Devices, logic and console | `list_devices`, `describe_device`, `read_logic`, `write_logic`, `read_logic_many`, `write_logic_many`, `read_memory`, `write_memory`, `inspect_slots`, `network_snapshot`, `sample_logic`, `connections`, `list_gateways`, `run_console_command`, `read_console`, `game_clock`, `looking_at`, `mod_info` | [devices-and-logic.md](docs/devices-and-logic.md) |
| Chips | `get_ic_source`, `set_ic_source`, `get_ic_status`, `control_ic_execution`, `resolve_ic_selectors`, `set_ic_pins` | [chips.md](docs/chips.md) |
| Items | `find_items`, `find_things`, `item_totals`, `list_containers`, `container_contents`, `move_item`, `label`, `paint`, `consumables` | [items.md](docs/items.md) |
| Air, planet, plants and survival | `rooms`, `atmosphere_contents`, `water_sources`, `move_gas`, `outer_frames`, `planet`, `deep_miner_spots`, `weather`, `plants`, `plant_genes`, `reagents`, `player_vitals`, `ignition_risk`, `thing_health` | [air-planet-and-plants.md](docs/air-planet-and-plants.md) |
| Solar, dishes and traders | `solar_aim`, `dish_aim`, `landing_pads`, `trader_contacts`, `trader_inventory`, `trader_buy`, `trader_sell` | [solar-and-traders.md](docs/solar-and-traders.md) |
| Cables, pipes and chutes | `grid_survey`, `plan_cable_route`, `plan_pipe_route`, `plan_chute_route`, `place_cables`, `place_pipes`, `place_chutes`, `remove_cables`, `remove_pipes`, `remove_chutes`, `upgrade_cables`, `upgrade_pipes` | [building.md](docs/building.md) |
| Clean-up and refactoring | `clean_cables`, `clean_pipes`, `plan_removal`, `feed_paths` | [cleanup-and-refactor.md](docs/cleanup-and-refactor.md) |
| Walls, frames and structures | `replace_walls`, `replace_frames`, `describe_prefab`, `wall_map`, `find_spot`, `lint_layout`, `check_replaceable`, `show_preview`, `place_structure`, `remove_structure`, `undo_job` | [walls-frames-structures.md](docs/walls-frames-structures.md) |
| Blueprints | `paste_blueprint` | [blueprints.md](docs/blueprints.md) |
| Ingot Vault (mod) | `vault_contents`, `vault_deposit`, `vault_withdraw` | [ingot-vault.md](docs/ingot-vault.md) |

Every building tool works the same way: a **dry run** by default that changes nothing and lists every problem; a real
run only with `dry_run: false` and `confirm: true`; the change made in one held game tick and checked afterwards;
materials taken from your inventory as the game would charge, and refunds put back into it. See
[building.md](docs/building.md#how-every-building-tool-works).

Every tool error has one shape, `{code, message}`, as the result's text and its structured content alike. Arguments
are checked against the tool's schema before the call reaches the game: an argument the tool does not take (the
message names the nearest one it does take), a value of the wrong JSON type, a word the argument's list does not hold
(case is ignored), a key given twice and a number past a double's range (`1e309`) are `invalid_argument`. An integer
may be written `3.0` or `1e2`. Reference ids are decimal strings (`"364"`). `game_unavailable` means the game could not be reached, and says whether no pipe
answered or the game took the request but did not reply in time (it may still have run).

Things are named by `display_name`, the game's own name (the label, else the localised name). Where the game has no
English name for a prefab it shows a placeholder such as `<N:EN:StructureCrewUmbilicalDoor>`; `display_name` and every
message then carry the prefab name instead (`StructureCrewUmbilicalDoor`), the same in every tool.

## Configuration

A single local game needs none. The settings cover the local pipe's name and remote access over TCP:
[docs/configuration.md](docs/configuration.md).

## Safety

- **This is a cheat-level tool.** The agent gets real write access to your world, console commands and device
  memory included. There are no per-user permissions and no general undo. Use an agent you trust, and save first.
- **Host only.** The mod runs on the game that hosts; its changes reach other players through the game's own sync.
  Every player who joins needs the same version, because the StationGod Gateway is a new structure.
- **The building tools refuse rather than guess.** They never make materials, never delete a pipe network's
  contents, never remove a chute with an item in it, never touch indestructible pieces or a launching or landing rocket, and
  never swap a
  wall or frame in a way that opens a room. Every job that changes pipe networks checks their contents before and
  after (`gas_check`) and stops further pipe jobs if anything went missing, until the world is reloaded or the user
  agrees to accept the loss (`acknowledge_gas_lost`).
- **Remote access is not encrypted.** The TCP transport checks a shared secret but sends everything in plain text.
- **After a game update** the mod checks at load every game member it relies on. A missing one turns off only the
  tools that need it, which answer `game_changed`; `mod_info` lists them.

## Status (1.5.0)

**Proven in real play on a live base:** the device, logic, IC10 and Lua chip tools, `set_ic_source` on Lua chips
included; the item and container tools, `find_things`, `move_item`, `label` and `paint`; `planet`, `plants`,
`player_vitals`, `thing_health`, `outer_frames`; the solar, dish and trader tools; `upgrade_cables`; `place_cables`
and `remove_cables`; `clean_cables` (dead ends, junctions, merge and split, loops); `plan_cable_route` with
`frames_first`, `assume_removed`, `prefer: hidden`, several starts and trunk taps; `grid_survey`, `feed_paths` and
`plan_removal`; `replace_walls`; `place_structure` and `remove_structure` for walls, floors and ceilings, batches
included.

**Proven on a dedicated server:** a twelve-round live test (2026-09-29, headless, no player) ran every tool area with
real runs and fixed what it found; its last round found nothing. Covered: cables, pipes and chutes (place, remove,
upgrade, clean with all its operations, the route planners, `undo_job`, `refund_to`), the gas check of pipe jobs
(merges, splits, long straights swapped, in-line tanks removed, `allow_burst` outdoors and into a room), `move_gas`
with rooms and its `dry_run`, the placement and layout tools that need no camera (door keep-out, the layout preview,
`orient`, `wall_map`, `find_spot`, `lint_layout`, `check_replaceable`), `replace_walls` and `replace_frames`,
broken structures, the power overload guard, the trader tools, the chip tools and the sidecar's argument checks.
Earlier: `paste_blueprint` (paste, status and undo), the pipe name setting, every 1.3.0 addition and the 1.3.1 fixes.

**Not yet tested in game:** what needs a player or a camera (`looking_at`, `at` at the crosshair or relative to you,
`show_preview`, hidden and body slots with a real inventory, the kit leaving your inventory, suit damage from
refunds), a real `acknowledge_gas_lost` lift (no gas loss could be provoked), the 1.3.2 landing pad atmospheres in
`move_gas`, the 1.3.3 planet gas removal (needs Terraforming Reloaded) and the 1.4.0 Ingot Vault tools (need the
Ingot Vault mod).

## Documentation

- [Install](docs/install.md): Workshop, sidecar, Claude Code, Codex, dedicated servers, multiplayer, removing it.
- [Configuration](docs/configuration.md): mod settings, sidecar options, environment variables.
- [Devices, logic and console](docs/devices-and-logic.md)
- [Chips: IC10 and Lua](docs/chips.md)
- [Items](docs/items.md)
- [Air, planet, plants and survival](docs/air-planet-and-plants.md)
- [Solar, dishes and traders](docs/solar-and-traders.md)
- [Building cables, pipes and chutes](docs/building.md)
- [Clean-up and refactoring networks](docs/cleanup-and-refactor.md)
- [Walls, frames and structures](docs/walls-frames-structures.md)
- [Blueprints](docs/blueprints.md)
- [Building from source](docs/building-from-source.md) (maintainers)
- [Changes per version](CHANGELOG.md)

## License and links

[MIT](LICENSE). By XCeled.

- Steam Workshop: search for *StationGod MCP* in the Stationeers Workshop.
- Source and issues: [github.com/altmank/Stationeers-StationGodMCP](https://github.com/altmank/Stationeers-StationGodMCP/issues)
