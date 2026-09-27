# StationGod MCP

A [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) mod that lets an AI agent
such as Claude Code or Codex read and operate a running Stationeers game through the Model Context Protocol (MCP):
devices and logic, IC10 and Lua chips, rooms, air, the planet, plants, items, gas, traders, and building and
rebuilding cables, pipes, chutes, walls, frames and structures with the game's own checks.

It has two parts:

- `StationGodMCP.dll`, the mod, runs inside the game. It adds the StationGod Gateway and runs every request on the
  game's main thread, on the host.
- `StationGodMCP.Server`, the sidecar, is the MCP server the agent starts. It talks to a local game through the named
  pipe `\\.\pipe\StationGodMCP`, or to a dedicated server over authenticated TCP.

The gateway's model and kit thumbnail are runtime copies of the game's Logic Memory; no game assets are distributed.

## Requirements

- Stationeers with BepInEx 5.4 and StationeersLaunchPad.
- Windows x64 for the self-contained sidecar, or the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  for the portable one.
- An AI agent that speaks MCP over stdio (Claude Code, Codex and others).

## Install

1. Subscribe on the Steam Workshop and enable StationGod MCP in StationeersLaunchPad. Or, from source, run
   `.\build.ps1 -Deploy` with the game closed (see *Build*).
2. Open the mod's `Sidecar` folder (Workshop: `steamapps\workshop\content\544550\<item id>\Sidecar`) and extract one
   archive to a folder outside every Stationeers mod and Workshop folder, for example
   `%LOCALAPPDATA%\StationGodMCP\server`:
   - `StationGodMCP.Server-win-x64.zip`: a self-contained executable, no runtime needed. It is unsigned, so Windows
     SmartScreen may ask before the first run.
   - `StationGodMCP.Server-portable.zip`: smaller, runs through Microsoft's signed `dotnet` host, needs .NET 8.

   The sidecars ship zipped because StationeersLaunchPad scans enabled mod folders for DLLs: extracted inside a mod
   folder, the sidecar's assemblies would be taken for game plugins.
3. Register the sidecar with your agent (below).
4. Turn on *Start Local Host* in the game's settings (`StartLocalHost` in `setting.xml`) and load a save. The mod
   answers only in a game that hosts; the BepInEx log then shows
   `[StationGodMCP] Authoritative MCP bridge listening on \\.\pipe\StationGodMCP`.

### Claude Code

```powershell
claude mcp add --scope user --transport stdio stationeers -- "C:\Users\YourName\AppData\Local\StationGodMCP\server\StationGodMCP.Server.exe"
```

Portable sidecar:

```powershell
claude mcp add --scope user --transport stdio stationeers -- dotnet "C:\Users\YourName\AppData\Local\StationGodMCP\server\StationGodMCP.Server.dll"
```

Or in a project's `.mcp.json`:

```json
{ "mcpServers": { "stationeers": { "command": "dotnet", "args": ["C:/Users/YourName/AppData/Local/StationGodMCP/server/StationGodMCP.Server.dll"] } } }
```

`claude mcp list` confirms it. Restart Claude Code after changing the sidecar.

### Codex

In `%USERPROFILE%\.codex\config.toml`:

```toml
[mcp_servers.stationeers]
command = "C:\\Users\\YourName\\AppData\\Local\\StationGodMCP\\server\\StationGodMCP.Server.exe"
startup_timeout_sec = 10
tool_timeout_sec = 40
```

Portable: `command = "dotnet"` and `args = ["C:\\Users\\YourName\\AppData\\Local\\StationGodMCP\\server\\StationGodMCP.Server.dll"]`.

## The StationGod Gateway

Optional. Every device tool reaches the whole world without one (omit `gateway_id` or pass `world`). A gateway id
only narrows a call to the devices on that gateway's data networks.

Print *Kit (StationGod Gateway)* at the Electronics Printer (1 g copper, 1 g gold) or spawn
`ItemKitStationGodGateway`, build it and connect either data port. Like the Logic Memory it copies it is passive and
always on; attached to two data networks it covers both, each device once.

## Dedicated server over TCP

The host can also accept several authenticated sidecars over plain TCP. In
`BepInEx\config\net.xceled.stationeers.stationgodmcp.cfg`:

```ini
[Remote MCP]
Enabled = true
BindAddress = 0.0.0.0
Port = 8765
Secret = replace-with-a-long-random-secret
```

| Setting | Default | What it does |
| --- | --- | --- |
| `Enabled` | `false` | Accept sidecars over TCP. Stays off without a secret. |
| `BindAddress` | `0.0.0.0` | Address to listen on. |
| `Port` | `8765` | TCP port (1 to 65535). |
| `Secret` | empty | Shared secret every sidecar must send, compared in fixed time. |

Read once when the game starts. Environment variables override the file, handy for Docker:
`STATIONGODMCP_REMOTE_ENABLED`, `STATIONGODMCP_REMOTE_BIND_ADDRESS`, `STATIONGODMCP_REMOTE_PORT`,
`STATIONGODMCP_REMOTE_SECRET`. Open the port in the firewall or router. The secret keeps out anyone without it but
travels unencrypted: use a long random value and change it if it leaks.

Sidecar side: `--host <address> --port 8765` (or `STATIONGODMCP_HOST`, `STATIONGODMCP_PORT`) and the secret in
`STATIONGODMCP_SECRET` (or another variable named by `--secret-env NAME`). Claude Code:

```powershell
claude mcp add --scope user --env "STATIONGODMCP_SECRET=<secret>" --transport stdio stationeers -- "C:\Path\To\StationGodMCP.Server.exe" --host station.example.com --port 8765
```

Codex: the same `command` and `args`, plus `[mcp_servers.stationeers.env]` with `STATIONGODMCP_SECRET`.

## Pipe name

A game or dedicated server listens on the local named pipe `\\.\pipe\StationGodMCP`. Two of them on one machine
(your game and a test server, say) need different names, or a sidecar reaches whichever started first. In the same
cfg file:

```ini
[Pipe]
Name = StationGodMCP-Test
```

| Setting | Default | What it does |
| --- | --- | --- |
| `Name` | `StationGodMCP` | The pipe's name, the part after `\\.\pipe\`. Not empty, no `\`, `/` or `:`; an invalid name is logged and the default used. Restart the game to apply. |

The environment variable `STATIONGODMCP_PIPE_NAME` overrides the file. The BepInEx log shows the name in use
(`Authoritative MCP bridge listening on \\.\pipe\<name>`) and `mod_info` reports it as `pipe_name`, so a script can
check which game it reached. Sidecar side: `--pipe <name>`, or `STATIONGODMCP_PIPE_NAME` when `--pipe` is absent.

## MCP tools

72 tools:

| Area | Tools |
| --- | --- |
| Devices and logic | `list_gateways`, `list_devices`, `describe_device`, `read_logic`, `write_logic`, `read_logic_many`, `write_logic_many`, `read_memory`, `write_memory`, `inspect_slots`, `network_snapshot`, `sample_logic`, `connections` |
| Chips | `get_ic_source`, `set_ic_source`, `get_ic_status`, `control_ic_execution`, `resolve_ic_selectors`, `set_ic_pins` |
| Console and game | `run_console_command`, `read_console`, `game_clock`, `mod_info`, `looking_at` |
| Items and storage | `find_items`, `find_things`, `item_totals`, `list_containers`, `container_contents`, `move_item`, `label`, `paint` |
| Survival | `player_vitals`, `consumables`, `water_sources`, `thing_health`, `ignition_risk` |
| Air, rooms and planet | `atmosphere_contents`, `rooms`, `outer_frames`, `planet`, `weather`, `move_gas` |
| Plants | `plants`, `plant_genes`, `reagents` |
| Solar, dishes and traders | `solar_aim`, `dish_aim`, `landing_pads`, `trader_contacts`, `trader_inventory`, `trader_buy`, `trader_sell` |
| Network upgrades and clean-up | `upgrade_cables`, `upgrade_pipes`, `clean_cables`, `clean_pipes` |
| Walls and frames | `replace_walls`, `replace_frames` |
| Cable, pipe and chute runs | `grid_survey`, `plan_cable_route`, `plan_pipe_route`, `plan_chute_route`, `place_cables`, `remove_cables`, `place_pipes`, `remove_pipes`, `place_chutes`, `remove_chutes` |
| Structures | `place_structure`, `remove_structure` |
| Blueprints (with BlueprintMod) | `paste_blueprint` |

Each tool's own description, which the agent reads, gives its arguments and reply fields in full.

Logic types can be supplied by enum name, such as `Color` or `Setting`, or by their numeric unsigned 16-bit ID. Gateway and device IDs are Stationeers reference IDs encoded as strings.

Bulk logic calls accept up to 256 operations and return an individual success or error for each operation. Memory calls read or write contiguous ranges of up to 512 addresses on devices implementing Stationeers' generic memory interfaces.

`inspect_slots` reports logical slots, occupants, constraints, and readable `LogicSlotType` values without a device-specific catalogue. `network_snapshot` filters by reference ID, prefab hash, or display-name substring and captures selected logic values in one Unity-thread request. `sample_logic` records bounded timestamped changes for up to 32 device/type pairs.

`get_ic_status` returns source, the current instruction, registers, a selectable stack window, aliases, defines, jump tags, power state, pause state, and compiler/runtime errors. `control_ic_execution` pauses, single-steps, or resumes the vanilla IC interpreter. `resolve_ic_selectors` resolves `db`, connected pins, compiled aliases, and unique prefab/name-hash selectors without changing housing pins.

### Lua chips

With [StationeersLua](https://steamcommunity.com/sharedfiles/filedetails/?id=3659911735) loaded, the IC tools also work on its Integrated Circuit (Lua), in an IC Housing or in a [ScriptedScreens](https://steamcommunity.com/sharedfiles/filedetails/?id=3666779631) holder: a Console or Computer board, a tablet cartridge or a Programmable Visor. StationeersLua is optional: the mod finds it by name at run time and works the same without it.

- `reference_id` may be the holder, the Console, Computer or held tablet it sits in, or the chip. Replies name all three: `reference_id` (what the scope reaches), `holder` and `chip`, plus `language` (`ic10` or `lua`) and `source_length`.
- `set_ic_source` writes Lua the way the IC editor's export does. There is no IC10 line or byte limit; the tool takes up to 262144 characters (`source_too_large` above that). StationeersLua compiles on a worker thread, so the reply usually says `lua.compiling`; read `get_ic_status` until it is false.
- `lua` in the replies: `compiling`, `has_runtime`, `init_complete`, `running`, `library`, `source_version`, `last_error` (kind, line, message, traceback) and in `get_ic_status` the last `log_lines` lines of the chip's `print()` log.
- `control_ic_execution` `restart` compiles a Lua chip's source again and runs it from the start. Lua chips cannot be paused or stepped (`lua_chip_unsupported`).
- `housing.kind` in `get_ic_status` is `computer_board` for a board (runs while its computer is on, powered and built) and `cartridge` for a tablet cartridge (while the tablet is on and powered).
- A holder in a tablet or visor is reached only while a player wears or holds it, as with suits.

### Upgrading cable and pipe networks

`upgrade_cables` replaces normal cable with heavy (or super heavy) cable in place; `upgrade_pipes` replaces normal gas and liquid pipe with insulated pipe of the same content. The game itself will not place heavy cable over normal cable or insulated pipe over normal pipe, so the tools rebuild each piece the way the coil's or kit's own merge placement does.

- Name a whole network with `network_id` (from `connections`) or pieces with `reference_ids`. Pieces already at the target stay, and so do pipe members that are not pipe pieces (vents, drains, radiators).
- Each piece's replacement is the first piece of the target coil or kit whose cells and connection ends match the old piece exactly, at the old rotation or turned. Straight, corner, T, cross, 5- and 6-way pieces and the 3-, 5- and 10-long straights all map this way. A piece without such a replacement is refused, or left as it is with `skip_unmatched`.
- **Dry run by default.** The report lists every piece and its replacement, the coils or kits needed and where they are, what is given back, each network's state before and after (the weakest cable after; a pipe network's contents, volume and pressure after) and every problem at once.
- **Connections are checked before anything changes.** The tool records the game's own connections around every piece, predicts them with every replacement in place, and refuses if any connection would appear or disappear, so no two networks can merge and no device can be cut off. It first proves its prediction method against the game on those same pieces. Fuses, analysers and pipe meters mounted on a piece must stay attached.
- **A real run** needs `dry_run: false` and `confirm: true`, and starts only when the dry-run checks find nothing. It returns a `job_id`. The mod holds the game tick as a save does, runs every check again once the tick has stopped, and swaps every piece in that one frame, so no power, atmospherics or logic tick ever sees a half-built network. Each replacement is built before its old piece is removed. The next frame it checks the result against what it recorded, then lets the tick go. Poll the job with `job_id`.
- Pipe contents stay in the network's own atmosphere the whole time. A pipe network is never split, vented or divided; only its volume changes by the difference between the old and new pieces, and the report shows the resulting pressure. A run is refused if that pressure would exceed the weakest pipe's rating.
- Coils or kits come from the local player's inventory at any depth, or from `from_id`. With `refund` (the default), whatever deconstructing the old pieces would give back is made at the source: worn belts and backpacks collect what fits, and the rest drops at the source.
- If a piece fails part way, the run stops there. The job lists which pieces were swapped and whether the piece it stopped at is intact. Running the same call again resumes, because pieces already swapped then count as done.
- Devices, APCs, batteries and transformers connect to any cable type. Cable networks keep their ids.
- Multiplayer: host only. The swap uses the same calls as a player's own placement and deconstruction, so clients, saves and ownership follow as they do for normal building. Clients see a brief pause while the tick is held.
- `connectivity` is null when no piece has a replacement: the links are surveyed only around pieces to swap.
- Tested in game: `upgrade_cables` dry run and real run on a 21-piece cable network, verification ok; on a larger network the 3-, 5- and 10-long straights were left as special pieces, since fixed.
- Not yet tested in game: the long straights swapped; `upgrade_pipes`, dry run and real run.

### Cleaning up cable and pipe networks

`clean_cables` and `clean_pipes` tidy a network in place. `operations` picks what they do; each works alone or with the others, and they always run in this order, each on what the earlier ones leave:

- `remove_dead_ends`: removes stubs (a piece with one connected end) and isolated pieces (none), round after round, since removing a stub can turn its neighbour into one. It stops at a stub whose only connection is a device, and never removes a piece with a fuse, analyser, pipe meter or other device mounted on it (the game itself will not deconstruct a pipe with an attached device), or an indestructible or rocket piece. Those are listed in `dead_end_pieces` with `stopped_by`. Removed pieces give back what deconstructing them would.
- `remove_loops` (never by default, since a loop may be redundancy kept on purpose against a burnt cable): finds loops, groups of pieces joined to the rest in more than one way, and breaks each by removing, one at a time, the shortest run of plain two-ended pieces whose two ends stay joined without it. A run holding a piece linked to a device, a piece with something mounted, or an indestructible, rocket or kit-less piece never goes, so no device loses a link and nothing is cut off. The junctions a cut leaves with an open end are fitted with only their connected ends. `keep_ids` spares every loop holding one of those pieces. The report lists `loops` with their pieces, what is cut, `spared`, and `unbroken` where a cycle has to stay.
- `split_long_straights`: each 3-, 5- or 10-long straight becomes one single straight per cell it covers, in the same line, so later connections can be made anywhere along it.
- `merge_straights`: runs of single straights of one grade, colour and owner in one line become the fewest long straights that cover them exactly, longest first (10, then 5, then 3); leftovers stay single. A junction, a device or a mounted device breaks a run. Cannot be asked for together with `split_long_straights`.
- `simplify_junctions` (the default when `operations` is not given): a piece with open ends (ends nothing is connected to) becomes the piece of the same coil or kit with only the connected ends. A 3-way junction joining two neighbours becomes a straight when they are on opposite sides and a corner when they are on adjacent sides; a 4-, 5- or 6-way junction or a corner variant joining fewer neighbours becomes the smallest piece with exactly those ends.

Details:

- Name a whole network with `network_id` (from `connections`) or pieces with `reference_ids`; only the named pieces change. The grade stays: normal cable stays normal, heavy stays heavy, super heavy stays super heavy; gas, liquid and insulated pipe keep their type and content. Colour and owner carry over as in the upgrade tools.
- Every replacement is read from the loaded coil or kit rather than from a table, found the same way as in `upgrade_cables`. A piece with no such replacement is refused, or left with `skip_unmatched`.
- An end counts as connected when a piece or device next to it links through it, as the game links them. Each listed piece shows its `operation`, its `ends` and `connected_ends` as world axes (`+x`, `-x`, `+y` up, `-y`, `+z`, `-z`), `replacement_count` (0 for a removal), `round` for removals, `merged_reference_ids` for merges, and its `cost` and `refund_count`.
- Without `remove_dead_ends`, dead ends are only listed in `dead_end_pieces`.
- Coils and kits: new pieces cost what the coil's or kit's own placement charges for pieces placed over others, the new pieces' cost less the old ones'. A simplified junction usually costs nothing and a merge gives coils back. A split costs more than it gives back (5 singles for a 5-long piece): the difference is taken from the local player's inventory, or from `from_id`, and the run is refused with `not_enough_coils` if there is not enough. Nothing is ever made for free. With `refund`, surplus coils and removed pieces' materials come back at the source.
- Pipe contents stay in the network's own atmosphere the whole time. A removed pipe leaves the network before it goes, so only its volume leaves: the moles stay and the pressure rises. A run is refused if the pressure after would exceed the weakest remaining pipe's rating. The game's own removal of a network's last pipe deletes whatever it held, so the last pipes of a network that still holds gas or liquid are never removed (`stopped_by` `holds_contents`, with the moles); an empty isolated pipe is removed.
- Every connection stays, less exactly those of removed pieces; no device loses a link. The run is refused otherwise. After a run the verification checks the links, mounts, device networks, each network's member count (which changes by exactly the planned additions and removals) and, for pipes, the contents.
- Multiplayer: host only, as `upgrade_cables`.
- Not yet tested in game: `clean_cables` and `clean_pipes`, every operation, dry run and real run.

### Replacing walls and frames

`replace_walls` replaces walls and windows in place with another wall or window prefab; `replace_frames` replaces frames with another frame prefab, or finishes unfinished frames in place. Neither ever opens a face or a cell to the air: nothing of the game runs between taking the old piece away and the new one standing in its place.

How to use:

1. `rooms` lists the rooms; take the `room_id` of the room whose walls or frames you want to change (or collect the pieces' reference ids, e.g. with `looking_at` or `find_things`).
2. Dry run: `replace_walls {room_id: "<id>", to: "StructureCompositeWall"}`, or `replace_frames {room_id: "<id>"}` to finish that room's unfinished frames. Add `from_prefabs: ["StructureWallIron"]` to touch only those prefabs. Read `ready`, `problems`, `pieces` (each piece's target, what it blocks before and after, its faces' pressures, its rooms and its materials), `materials` and `rooms`.
3. Real run: the same call with `dry_run: false, confirm: true`. It returns a `job_id`.
4. Poll with `{job_id: "<id>"}` alone until `status` is no longer `waiting` or `verifying`. `applied` means every check passed.
5. Check with `rooms` that the room kept its id, its cell count and its air.

What is supported:

- Walls: any wall or window prefab to any other wall or window prefab with the same footprint: iron wall to composite or reinforced wall, wall to window, window to wall, and so on. Only the plain wall and window classes are touched, both ways: shuttered windows and their connectors, floors, ladder platforms and crew umbilical doors are kept as `special_piece`, and such a target is `invalid_target`.
- Frames: iron to steel and steel to iron, and any frame to its own prefab, which finishes it (a frame already finished is kept as `already_at_target`). Rocket towers are never touched.
- The target is a prefab name (`to`), required for walls. It must be a loaded prefab that some kit builds.
- Scope: `reference_ids` (up to 4096) or `room_id`: for walls every wall on a face of the room's cells, for frames every frame next to the room (and any inside it that lets gravity pass). `from_prefabs` narrows either; the rest are kept as `not_selected`.
- `limit` caps the pieces listed in the report (default 200); every piece is counted and checked.

How a swap works: the mod holds the game tick as a save does and, once it has stopped, runs every check again and swaps every piece in that one frame. For each piece the old one is marked as being destroyed (which is what lets the game hand its slot to the new one), the new piece is built in the same slot with the old one's owner and colour and raised at once to its final build state, and only when it holds every slot the old one held and blocks what the old one blocked is the old one removed. If the new piece did not take every slot or does not seal, it is taken away and the old piece gets its slots back; the run stops there (`stopped_at`, with `piece_intact` and `rolled_back`), so a face is never left open. The frame after, with the tick still held, the mod checks each new piece, that the old ones are gone, that the walls beside a replaced frame are the same ones, and that every room's and cell's air around the pieces is exactly as it was (`held_check`). Then it lets the tick go (`verifying`), waits until the game has run its ticks and re-evaluated its rooms (at most 10 seconds), holds the tick once more for a consistent look, and checks that every room next to a swapped piece still exists under the same id with the same cells and the same air within 1 % (at least 0.5 mol, 1 kJ) (`room_check`). A room that appears where there was none, because a finished frame closed a space, is listed in `new_rooms`, not as a problem.

What is refused, and why:

- `footprint_mismatch` (unmatched): the new piece would not register in exactly the old one's slots: for walls the same face points in the same cell, for frames the same cells. Swapping it would leave a slot empty or collide. `skip_unmatched: true` leaves such pieces as they are.
- `would_open`: the old piece blocks air or gravity now and the target does not at its final state. A leaky old piece made airtight is allowed and shown as `seals`.
- `would_overstress` (walls): a face's current pressure difference is at or above what the face bears with the new wall (the summed `MaxPressureDelta` of its airtight structures), so the game would damage the new wall until it breaks. Above the new wall's stress mark it is flagged `stressed`, a warning only.
- `cell_occupied` (frames): finishing a frame closes its cell, so anything else in it would be sealed in: another structure, a pipe, cable, chute or device inside it, a player, creature or loose item. Walls on the frame's faces are fine and are checked to still stand after the swap.
- Kept with a reason: `already_at_target`, `special_piece`, `not_selected`, `indestructible`, `broken` (a damaged build state), `being_destroyed`.
- `not_enough_materials`, one per missing item.

Finishing a frame does what welding its last sheet does: its cell leaves its room, and whatever gas the cell held is divided among its open neighbours. The room check expects exactly those cells to leave and allows that much gas to move.

Materials follow the game's deconstruction rule. Each build state takes its `ToolEntry` and `ToolEntry2` items with their quantities; tools (welder, wrench, grinder) are not consumed. The new piece costs its states up to its final one; the old piece gives back its states up to the one it is at. Per piece and per item, the shortfall is taken from the local player's inventory at any depth (or `from_id`) and the surplus is given back with `refund` (the default), made at the source, where worn belts and backpacks collect what fits. The report lists each piece's cost, refund and net, and the run's totals. Assumption: tool wear, welder fuel and power-tool battery are not charged.

- Multiplayer: host only (`not_host` on a client). Clients get the new walls and frames through the game's own sync (a new structure with its build state, the old one's removal), so no custom messages are sent and a client's grid cannot be left inconsistent by message order. Players without the mod see normal walls and frames. Clients see a brief pause while the tick is held.
- One swap job runs at a time across `upgrade_*`, `clean_*` and `replace_*` (`busy`).
- Not yet tested in game: `replace_walls` and `replace_frames`, dry run and real run.

### Laying, removing and rerouting cable and pipe runs

`place_cables` and `place_pipes` build a run the way a coil or kit does; `remove_cables` and `remove_pipes` take pieces away as wire cutters or a wrench would; `grid_survey` shows what stands where; `plan_cable_route` and `plan_pipe_route` find a route under rules and dry-run it.

How to use:

1. Survey: `grid_survey {room_id: "<id>"}` or `{min: [x, y, z], max: [x, y, z]}`. Each 2 m cell gives its frame, walls, room and a 64-character string of its 0.5 m cells (see `legend`), then the cables, pipes and devices there, each device port with the cell a piece joining it stands in, and the networks. `support` marks the same 64 cells by what would hold a piece up: `e` a frame edge or corner, `f` on or inside a frame, `w` on a wall's plane, `a` air.
2. Plan: `plan_cable_route {from: {reference_id: "<device>", port: 1}, to: {reference_id: "<cable>"}, prefer: "frame_edges"}`, or a reroute: `{reroute: {between: ["<device a>", "<device b>"]}, avoid_walkways: true}`. It returns the route's waypoints, `place_arguments` and `place_cables`' own dry run.
3. Dry run: `place_cables {waypoints: [[x, y, z], ...], grade: "heavy"}` (the default grade), or one piece: `{piece: {at: [x, y, z], ends: ["+x", "-y"]}}`. Read `ready`, `problems`, `cells` (each cell's piece, turn, shape, ends and what each end joins), `would_bridge`, `would_split`, `networks_before`, `networks_after` and `materials`.
4. Real run: the same call with `dry_run: false, confirm: true`; poll `{job_id}` until `applied`.

What it does:

- Every cell gets the one-cell piece of the grade with exactly that cell's connections, read from the coil or kit and turned to fit; long straights are never used.
- Where the run meets existing pieces: a crossed or joined piece becomes the junction with the extra end (the inverse of `simplify_junctions`), keeping its own grade; run ends join open ends and device ports pointing at them (`join: ends`, the default; `none` or `all`).
- Placement is checked the way the game's cursor checks it, because the server checks nothing when a structure is built: devices, chutes and other small things block; a pipe blocks a cable only along its own axis; frames and walls never block cables or pipes.
- `remove_ids` removes pieces in the same job before building, so a device is never seen unpowered between its old cable and its new one.
- `branches` add side runs to a run: each attaches to a cell of the run (or an earlier branch) with a junction, and its first cell joins ports and ends as a run end does. `plan_*_route` fills them in for several starts.
- A long straight the run must join in its middle, or cross, is split into single pieces of its own grade, colour and owner in the same job, then joined (`long_split` warning; `allow_split_long: false` refuses with `long_piece` instead).
- `would_loop` (a warning): the run joins something that is already joined another way, so the network gets a second path. Keep it only if that redundancy is meant.
- `plan_*_route` follows frames first (`frames_first`, on by default): a cell in air, on no frame and no wall plane, costs as much as 50 more cells over frames, so a route over frames or along walls wins whenever the search box holds one, even a much longer one. Only where none exists does the route cross air, with as few air cells as possible and a `through_air` note. `route.air_cells` counts the new cells in air (0 for a clean route) and `route.air` lists them; `frames_first: false` turns the rule off. The top of a frame beam counts as frame: it lies on the bottom plane of the empty cell above.
- The place tools' dry run counts the new pieces in air (`air_cells`) and names them in a `through_air` warning.
- `plan_*_route` takes several starts (`from: {reference_id, ports: [2, 3]}` or an array) and grows one tree: the first start routes to the target, every other start to the nearest cell of the tree so far. A device with separate power and data ports gets one run with a junction, never two parallel runs closing a loop. `to` may be `{network_id}` (the nearest piece of that network) or a long straight (any of its cells).

What is refused, and why:

- `would_bridge`: the run would join two or more networks, or put two power ports of one device on one network (both sides of an APC or transformer, a battery's input and output; for pipes a pump's or regulator's two sides). The report names the networks and the devices on each; `allow_bridge` must name every network of the merge (or the device).
- `would_split`: a removal would split a network or leave a device port joined to nothing (`allow_split`).
- `would_overload` (cables): the network after the edit would carry min(potential, required) over its weakest cable, new pieces included; the game would burn a cable every power tick.
- `would_burst`, `holds_contents`, `contents_would_move` (pipes): pooled contents over the weakest pipe; a removal that would delete or divide a network's contents.
- `cell_blocked`, `long_piece`, `cannot_change`, `content_mismatch`, `no_piece_for_ends`, `not_enough_coils`, `link_lost`, `connectivity_model_mismatch`.

- Multiplayer: host only (`not_host` on a client); clients get the pieces through the game's own sync. Players without the mod see normal cables and pipes.
- One job runs at a time across `upgrade_*`, `clean_*`, `replace_*`, `place_*` and `remove_*` (`busy`).
- Not yet tested in game: all seven tools. Piece choice and prefab ends come from the loaded coil at run time; the forecast, guards and route search are covered by unit tests only.
- Not yet tested in game: branches, several starts, `to: {network_id}`, splitting long straights, `would_loop`, `remove_loops`.
- Not yet tested in game: `frames_first`, `air_cells` and `through_air`, `grid_survey`'s `support`.

### Laying chutes

`place_chutes`, `remove_chutes` and `plan_chute_route` work as the cable tools do, with Kit (Chute) and the item flow checked.

- Pieces: straights and corners (1 kit each) and, where three ends meet in a T, a junction (2 kits). Chutes have no other shape, so a cross or a corner of three is refused (`no_piece_for_ends`). Long straights, windows, valves, overflows, splitters, bins, inlets and outlets are never placed or replaced.
- Direction: items travel along a run from its first cell to its last. Start at the source (a device's chute Output port, a chute bin, a line carrying items towards you) and end at the sink (a device's chute Input port). `grid_survey` and `connections` list each chute port's role.
- A straight or corner passes an item out of the end it did not come in by; a junction takes items in through its two inputs and lets them out of its output only, so each junction is turned to face downstream. A junction merges; it cannot split a flow (that needs a splitter, which these tools do not build).
- Refused: `flow_reversed` (what the run joins pushes items against it: reverse the waypoints), `flow_conflict` (two flows would meet head-on, or an item would have to enter a junction through its output), `flow_ambiguous` (a junction is needed and nothing says which way it must face). Warned: `drops_items` where items would leave an open end and fall to the ground, `flow_unknown` where nothing gives new pieces a direction.
- A chute with an item riding in it is never removed or replaced: the game would lose the item with it. Let it pass, or take it out with `move_item`.
- Chutes block cables, pipes, devices and other chutes in the same small cell; frames and walls never block them.
- `would_bridge` also covers a device's chute output fed back into its own input.
- Chute networks take new ids after any removal or change: the game rebuilds them.
- `grid_survey` shows each chute's network, `flow {in, out}` and the item it `carries`.
- Multiplayer: host only, as the cable tools.
- Not yet tested in game: all three chute tools.

### Placing and removing structures

`place_structure` places any structure some kit builds; `remove_structure` removes structures as deconstructing them by hand would. They are the generic builder and remover for simple edits; the specialised tools stay the way to lay cables, pipes and chutes (`place_*`, `remove_*`) and to swap walls and frames in place (`replace_*`).

How to use:

1. Find the prefab name (`looking_at`, `find_things`) and a point in the target cell (`grid_survey`, `looking_at`).
2. Dry run: `place_structure {prefab: "StructureWallLight", at: [x, y, z], facing: "-x"}`, or several at once with `placements: [{prefab, at, ...}, ...]`. Read `ready`, `problems`, `warnings`, each placement's snapped `position`, `orientation`, `face`, `build_state` and `cost`, and `materials`.
3. Real run: the same call with `dry_run: false, confirm: true`; poll `{job_id}` until `applied`. `result.verification` says per piece that it stands as planned.
4. Remove: `remove_structure {reference_ids: ["<id>"]}` dry run, then `dry_run: false, confirm: true`, poll `{job_id}`.

Placing:

- Position: `at` is a point in the cell, snapped as the placement cursor snaps it. A piece placed on a cell face (a wall, placement `face`) sits on the face of that cell opposite its facing; `face: "+x"` puts it on the cell's +x face, looking into the cell.
- Turn: at most one of `rotation` ([x, y, z] degrees, multiples of 90, Quaternion.Euler order), `facing` (+x, -x, +y up, -y, +z, -z) with optional `up` (default +y, or +z when facing is vertical), or `face`. A grid-placed piece may only be turned about the axes its cursor turns it (`invalid_rotation`).
- `build_state`: `finished` (default), `first` (as a kit leaves it) or an index. `label` names it as the Labeller does; `color` is a colour name or index (`paint` lists them).
- Checked with the game's own placement cursor for the prefab: `CanConstruct` (blocked cells and faces, small-grid collisions, rocket cells, each class's own rules), a face-mounted piece's support (`CanMountOnWall`), and nothing loose, no creature or player inside a piece that fills its cell. A small-grid piece whose slot in a cell is taken is refused: a coil would merge the two, a plain build would stack them. The check runs again just before each piece is built, so later placements see earlier ones.
- Cost: every build state's items up to the chosen state (state 0 is the kit), taken from the local player's inventory at any depth or `from_id`, as a kit's placement takes them. `free: true` places without materials and is refused unless the world is creative (`not_creative`). Tool wear, welder fuel and battery charge are not charged.
- Cables, pipes and chutes are placed as exactly the piece given, with a `network_piece` warning: the game joins whatever its ends touch, with no `would_bridge` check.
- Refused per placement: `invalid_prefab` (not loaded, not a structure, no kit builds it, a rocket part), `invalid_rotation`, `invalid_build_state`, `no_cursor`, `cannot_place` (with the game's own reason), `not_labelable`, `not_paintable`, `invalid_color`, `overlaps_placement`; for the run: `not_enough_materials`, `not_creative`.

Removing:

- Gives back what deconstructing by hand does, every build state's items down to the kit: to the source (`refund_to: "source"`, the default: `from_id` or the local player; worn items collect, the rest at its feet), on the ground where each piece stood (`ground`), or not at all (`none`).
- Refused: `not_a_structure` (items: `move_item`), `being_destroyed`, `indestructible`, `rocket`, `broken` (a damaged state; repair it first), `game_refuses` (the game's own `CanDeconstruct`), `has_mounted` (a device mounted on it).
- Refused unless allowed: `holds_items` and `holds_gas` (`allow_contents`: items drop where it stood, as in the game; a tank lets its gas out into its cell, other devices lose it), `would_breach` (the piece blocks air and removing it joins spaces whose pressures differ by 1 kPa or more, such as a pressurised room and the outside; `allow_breach`).
- Warned: `port_left_open` (a device end that joins a cable, pipe, chute or device now).
- Cable, pipe and chute pieces are removed as `remove_cables`, `remove_pipes` and `remove_chutes` remove them (a network kept whole keeps its id and contents) and their checks apply: `would_split` is only a warning here, `holds_contents` and `contents_would_move` are lifted by `allow_contents`, the rest refuse.

- The job holds the game tick, runs every check again, places or removes everything in one frame, delivers the refund and verifies: a placed piece stands with its prefab, position, turn, build state, label and colour; a removed one is gone. Status `applied`, `applied_with_differences` or `stopped` (`stopped_at`: the placement or removal it stopped at; nothing after it was done).
- Multiplayer: host only. One job at a time across all the job tools (`busy`).
- Not yet tested in game: both tools. The cursor check, snapping, turns and verification come from the game's own code; the rotation maths and guards are covered by unit tests only.

### Pasting blueprints

With [BlueprintMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3672138641) loaded, `paste_blueprint` pastes one of its blueprints at a position and turn you give, with no player needed. The console's `bppaste` takes both from the local player, so it cannot run on a dedicated server; this tool makes the call the D.B.P.U. makes instead. BlueprintMod is optional: the mod finds it by name at run time, and without it the tool answers `mod_missing`.

- Paste: `name` (a file in BlueprintMod's Blueprints folder, with or without `.blueprint`, or an absolute path), `anchor` `[x, y, z]` (the world position in metres where the blueprint's reference point lands: the large-grid point the copying player stood on, x and z odd whole metres, y even) and `rotation` 0, 90, 180 or 270 (default 0), added to the angle the blueprint was copied at so every piece stays on the grid. The reply comes at once: `started`, `file`, `entries`, `anchor`, `rotation`, `copy_y_angle` and `expected_duration_s`. BlueprintMod then places the pieces over 2 to 30 seconds (0.15 s per entry).
- `status: true`: how the last paste this tool started went, `created`, `failed`, `skipped`, `pasted`, `complete`, `cancelled`, and whether another paste is running (`other_active`). The counts stay readable after the paste ends.
- `undo: true`: BlueprintMod's `bpundo`, and its answer as `message`. It cancels a running paste and removes what it placed, or removes the last finished paste.
- Refused: `paste_refused` with BlueprintMod's own message (the same blueprint already pasted at that position and turn, not enough DeanamicMatter), `blueprint_failed` (BlueprintMod threw, with its error, such as a file that is not a blueprint), `invalid_argument` (no such file, naming the path looked at), `game_changed` (BlueprintMod no longer has something the tool calls).
- Rooms are not worked out again after a paste or an undo: run the console command `regeneraterooms` before reading `rooms`.
- Outside creative, BlueprintMod charges DeanamicMatter from the local player. A dedicated server has none, so use a creative world there.
- Multiplayer: host only (`not_host` on a client). BlueprintMod shows its paste effect to every player and the game sends them the new structures, so clients need nothing from this tool.

## Multiplayer

The mod works on the host, where the game's state lives: requests run on the host's main thread, and every change
reaches players through the game's own sync. The pipe and the TCP listener open only on the host. Every player who
joins needs the same version too, because the StationGod Gateway and its kit are new prefabs a client without the
mod cannot show. The pipe name only matters on the machine the game runs on; players never see it.

## Removing it

Everything the agent built, placed, labelled or painted is ordinary game content and stays. Gateways and gateway
kits are the mod's own and are dropped when a save loads without it (the game logs `Can't spawn` for each). Then
delete the sidecar folder and remove the agent's MCP registration.

## Limits and known gaps

- The agent gets real write access, including console commands and device memory. There are no per-user
  permissions, no undo and no backups: save first.
- The TCP transport authenticates but does not encrypt.
- After a game update the mod checks every game member it reaches by reflection at load (`mod_info` lists them);
  a missing one turns off only the tools that need it, which answer `game_changed`.
- Chute runs: the umbilical check is untested; loops made only of junctions joined directly are reported, not cut.
- Tested in game (1.0.0): the device, logic, IC10 and Lua chip, inventory, `label`, `paint`, `move_item`, `planet`,
  `plants`, `player_vitals`, `thing_health`, `outer_frames`, solar, dish and trader tools in daily use on a live
  base; `upgrade_cables` dry and real runs.
- Not yet tested in game (1.0.0): `upgrade_pipes`; `clean_cables` and `clean_pipes`; `replace_walls` and
  `replace_frames`; the cable, pipe and chute run tools (`place_*`, `remove_*`, `plan_*_route`, `grid_survey`)
  including branches, `frames_first` and `remove_loops`; `place_structure` and `remove_structure`. Their guards,
  forecasts, route search and rotation maths are covered by unit tests; the parts that call the game follow the
  game's own code.
- Not yet tested in game (1.1.0): `paste_blueprint` and the `[Pipe] Name` setting. Their replies, argument checks
  and pipe name rules are covered by unit tests.

## Build

Requirements: the installed game with BepInEx and StationeersLaunchPad, and the .NET 8 SDK.

```powershell
.\build.ps1            # build, check versions and the Workshop page length, stage .\package
.\build.ps1 -Deploy    # also install to Documents\My Games\Stationeers\mods\StationGodMCP and the
                       # sidecar to %LOCALAPPDATA%\StationGodMCP\server (game closed)
dotnet test .\tests\StationGodMCP.Tests
```

The game folder comes from `-GameDir`, else `STATIONEERS_DIR`, else the Steam default. The version lives in five
places that `build.ps1` checks agree: `StationGodMCP.csproj`, `StationGodMod.Version`, `About\About.xml`,
`StationGodMCP.Server.csproj` and `Program.ServerVersion`. `.\package` holds exactly what the Workshop gets: the
About and GameData folders, the DLL, the LICENSE and `Sidecar` with both ZIPs.

The tests need no game: they compile the mod's game-free code (`Pure`, `Api\Shared`, `Api\Views`) and check the
planners, guards and every tool's reply shape.

## How it is built

Both transports live in the mod. Pipe and TCP requests are read on background threads, queued through one
dispatcher and run in the game's update loop, so no game object is touched off the main thread. Jobs that rebuild
things hold the game tick across frames (`HeldTickJobs`), so a change and its check see one consistent world. Every
game member reached by reflection is declared in `GameMembers.cs` and checked at load.

## License

[MIT](LICENSE).
