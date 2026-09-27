# Changelog

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
