# Changelog

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
