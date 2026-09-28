# Changelog

## 1.3.4

Rooms in move_gas.

- **`move_gas` takes a room as `from` or `to`:** `{"room_id": "<id>"}` (as `rooms` reports it) or
  `{"room_of": "<reference id>"}` (the room that thing is in). The room is every cell of it with air of its own. From a
  room, each named gas is taken from every cell in proportion to what the cell holds (`amount_mol` caps the room's
  total), with its share of heat, in one atmospherics tick; `gases` is required, so a room's air is never emptied by
  omission. Into a room, each gas is spread over the cells by volume, so no single cell spikes. The side reports
  `room {room_id, room_type, cell_count, cells_with_air, volume_l}` and the room's pressure before and after in
  `total`; room sides list no per-cell `members`. Every side now carries `room` (null for an atmosphere). The tank or
  network on the other side keeps every burst and matter check. New errors `room_not_found`, `not_in_room`.

## 1.3.3

Planet gas removal.

- **`move_gas from: "planet"`** with `delete: true` and named `gases` takes those gases out of the planet's own
  air, `PlanetaryAtmosphereSimulation._globalGasMix` (what Terraforming Reloaded reads through `GetGlobalGasMix`), and
  out of its liquid clouds, ice clouds and ice caps, under the game's `GlobalInteraction` lock on the atmospherics
  thread. Without `amount_mol` it repeats for 30 ticks, since outdoor cells hand a residual back. Needs Terraforming
  Reloaded (`terraforming_mod_required`): the stock game keeps the planet read-only. Refused without `gases`.

## 1.3.2

Landing pad atmospheres in the gas tools, and move_gas checks the receiving side by the game's matter rules.

- **Landing pads.** Every piece of a landing pad shares one atmosphere, the pad network's (the pad's gas storage
  tanks give it 500 L each). `atmosphere_contents` now reports it for any pad piece or the pad network's id
  (`source: landing_pad_network`), `find_things has_atmosphere` counts pad pieces, and `move_gas` takes a pad piece,
  the pad network or its atmosphere id as `from` or `to` (owner `kind: landing_pad_network`). Before, they were
  refused `no_atmosphere`. The pad's burst rating is the gas pipe rating, at every piece; its gas storage is damaged
  already at the rating. The pad holds liquids as liquids: its atmosphere never changes state.
- **The receiving side's matter rules.** `move_gas` is refused `would_burst` when the move would make the target
  worse by one of the game's own rules: liquid filling over 2% of a gas pipe network's volume (spread over the members
  joined for liquids), gas or liquid freezing in a network at the settled temperature, or the pressure once arriving
  liquid has boiled where the atmosphere changes state (each liquid turned into its gas, paying its latent heat).
  `force` still skips every check.
- **`total.after_boiling`** in `move_gas` replies: the pooled state once every liquid that would boil has, null when
  none would or the atmosphere never changes state.

## 1.3.1

Fixes from the first live run of 1.3.0 on a dedicated server.

- **`place_structure` places ordinary devices again.** Batteries, small transformers, passive vents, pipes, small
  tanks, radiators, gas tank storage, valves and portables connectors were refused as "a rocket part", because they
  may also be fitted in a rocket. Now only what the game places solely in a rocket (strictly internal pieces), the
  fuselage and the launch mount are refused, plus any placement into a rocket's cells. `remove_structure` had the same
  mistake: its `rocket` refusal now means a piece that is part of a rocket.
- **Walls back to back in one request.** Two plates on one face, one facing into each cell, were refused
  `overlaps_placement`. A face holds one wall per side, as in the game.
- **Devices aimed at a cell's centre stand on its floor.** A 0.5 m-grid device (a battery, a valve) given at a
  point inside a cell stayed in the air there and was refused "requires a Frame below". When the point as given cannot
  be built, the device is now set down on the surface behind it, as the placement cursor's ray lands on a surface: the
  floor plane below a standing device, the face at the back of a mounted one (a transformer facing up sits on the
  floor). A point as given that can be built is kept.
- **Positions in messages are in metres.** Problems and warnings printed cells ten times too large
  ("Cell (-13060, 2200, -7075)" for (-1306, 220, -707.5)).
- **`plan_removal` on a dedicated server.** With no local player and no `from_id` it reported a `no_local_player`
  problem; it only prices the refund, so that is now a warning.
- **Smaller fixes.** A tap's changed trunk piece is listed under `created_by_part` `joined`, not `run`; a plan lists
  the network `to` names once in `resolved_networks`, not as both `to` and `to.network_id`; two plates back to back
  removed together report their breach once.

## 1.3.0

Tools for refactoring a whole network, each replacing a step that had to be done by hand in a live cable refactor.

- **Splits name the devices cut off.** `would_split` (place, remove, `plan_removal`, `remove_structure`) now lists
  each network a split leaves with its devices (`components`), the devices that feed it (`root`: the request's
  `root`, else every supplier on it: an APC's, transformer's or battery's output, a generator, a solar panel) and
  `cut_off`, the devices no root reaches afterwards. The problem message names them too.
- **New clean operation `remove_redundant`** (`clean_cables`, `clean_pipes`; never by default): removes every piece
  no device needs, oldest first, keeping every remaining piece joined, so each device stays on the network with its
  root. `keep_ids` never go (for example a new run's `created_ids`); `only_ids` and `older_than_id` narrow the
  candidates; a piece joined to a device port never goes. It finds the loops `remove_loops` cannot: an old feed and
  a new drop meeting at a device's port piece. The report says why each candidate stays and which devices need it.
- **Tap check.** A run end that stops next to, or one free cell short of, a piece of another network warns
  `not_joined`. `join_to` names the network a run must end up on, `join_trunk: true` adds the missing tap
  (`tap_added`). The route planners set `join_to` from `to` and pass it on in `place_arguments`, so a saved plan that
  ends one cell short of its trunk is caught when it is built.
- **Network handles.** Every `network_id` (and `to: {network_id}`, `join_to`, `allow_bridge` entries) also takes the
  reference id of a piece or device on the network, or `{reference_id, port}` of a device port, resolved to the
  current id when the call runs. Replies list them in `resolved_networks`.
- **Busy job slot.** A real run that finds another job running answers status `busy` with `running_job_id` instead
  of a refusal; `wait: true` queues it (up to 8) and starts it once the slot is free, checked again from scratch.
- **Created ids.** A run job's log lists `created_ids` and `created_by_part` (run, each branch, joined neighbours).
- **Fix: `remove_structure`'s breach check.** It follows the game's air rule and judges the whole request at once.
  A wall plate on the face of a finished frame no longer counts as a breach (the frame still seals the face; 185
  false alarms on one base), and two plates back to back between rooms are flagged when both are removed together
  (38 missed before, each checked alone while the other still stood).

## 1.2.0

- **Plan as if old pieces were gone.** `assume_removed: [ids]` on `plan_cable_route`, `plan_pipe_route` and
  `plan_chute_route` plans through and beside pieces that are about to be removed: their cells are free and their
  links gone. The kind's own pieces among them go into the plan's `remove_ids`, so one job builds the new run and
  removes the old one, and the dry run's guards see the result. `route.assumed_removed.in_the_way` lists the pieces the
  new route needs gone. The place tools take `assume_removed` too, for a dry run of a run whose old pieces go in
  another job; a real run is refused (`assumed_present`) while any of them still stands.
- **Least visible routes.** `prefer: hidden` grades every cell by how much of a cable shows there: inside a frame
  costs 1, on a frame's surface 3, on a wall's plane 5, in air 9. Where `inside_frames` gives no route, this gives the
  least visible one. Every route now reports its new cells by class (`route.visibility`).
- **A trunk and its drops in one job.** `trunk: {waypoints}` instead of `to` lays that trunk as given and branches
  every start from it with junctions, so a bus that is not built yet can be planned, checked and built with all its
  drops at once.
- **16 starts.** A plan takes up to 16 starts (was 8), enough for a generator network's ports.
- **grid_survey:** a new support class `i` for cells inside a frame (`f` is now only a frame's face), and
  `network_visibility`: each network's cells by class with the floating ones listed. `include_refund` adds what
  removing each piece would give back.
- **New `feed_paths` tool.** From a root device such as an APC, the path to every device on its network and the rooms
  it crosses; devices fed through another room (daisy chains) and rooms fed at more than one place are flagged.
- **New `plan_removal` tool.** The dry run of a removal, refund and `would_split` included, as a read-only tool; it
  also takes a whole `network_id`. Plans report `removal_refund` for the pieces they remove. 74 tools now.
- **Fix: refunds no longer hit the player.** Every tool that gives materials back (`replace_walls`, `replace_frames`,
  `upgrade_*`, `clean_*`, `place_*`, `remove_*`, `remove_structure`) made the items at the player's position, so they
  were pushed out of the player's body and damaged the suit. They now go straight into the inventory: onto matching
  stacks anywhere in it first, then into empty slots that take them, and only what does not fit goes on the ground
  a metre in front of the player, at rest. Each part is reported as `merged`, `slot` or `ground`.

## 1.1.1

- **`inside_frames` accepts beam tops.** The route rule now judges a cell by every frame it sits in or on, the same
  way `frames_first` and `prefer: frame_edges` do, so the top of a frame beam and the outer faces of frames count as
  on the frame. It used to judge the top of a beam by the empty cell above and refuse it.
- **Reroute between an APC and its network.** `reroute: {between: [...]}` failed with `not_on_one_network` when an end
  was a device on several networks of the kind, such as an APC's input and output. It now uses the one network both
  ends share, and an end may name a device port as `{reference_id, port}`. When the ends share no network, or
  several (`ambiguous_port`), the error lists each end's ports and their networks.

## 1.1.0

- **Paste blueprints without a player.** With BlueprintMod loaded, the new `paste_blueprint` tool pastes a blueprint
  at a position and quarter turn you give, as the D.B.P.U. does, so pieces land on the grid. It works on a dedicated
  server, where the console's `bppaste` cannot (it needs a local player). It also reports how the paste went and
  undoes it. 72 tools now.
- **Choose the pipe name.** New setting `[Pipe] Name` (default `StationGodMCP`, or the environment variable
  `STATIONGODMCP_PIPE_NAME`) lets a second game or a test server on the same machine listen on its own pipe instead
  of racing the first for it. The sidecar takes the same variable when `--pipe` is not given, and `mod_info` now
  reports `pipe_name`. Restart the game after changing it.

## 1.0.0

- **First release.** An MCP server for Stationeers: an AI agent such as Claude Code or Codex reads and operates the
  running game through 71 tools.
- **Devices, logic and chips.** Find any device in the world, read and write logic values, slots and memory in bulk,
  record changes over time; read, write, compile, pause, step and restart IC10 programs (suit chips included) and,
  with StationeersLua, Lua chips on consoles, computers, tablets and visors; set IC Housing pins.
- **The base and the planet.** Rooms and their air, the planet's atmosphere and weather, plants and genes, damage,
  fire risk, water, food, vitals, the game clock and what the player is looking at.
- **Items, gas, solar and traders.** Find, count and move items, label and paint, move gas between canisters, tanks
  and pipe networks, aim solar panels and dishes, check landing pads, buy and sell with a landed trader.
- **Building.** Plan, lay, remove and reroute cable, pipe and chute runs; upgrade cable and pipe networks in place;
  clean up junctions, long straights, loops and dead ends; swap walls, windows and frames without opening a room;
  place and remove any kit-built structure. Each has a dry run, holds the game tick for the real run, charges and
  refunds as the game does, and checks the result.
- **Local and remote.** A named pipe for a local hosting game; authenticated TCP for dedicated servers.
