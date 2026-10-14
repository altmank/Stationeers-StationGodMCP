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
- **Items, rooms and planet:** find, count and move items (straight into and out of an SDB Silo too), label and
  paint things, move gas; rooms and their air, the planet's atmosphere and weather, plants and their genes, damage,
  fire risk, food and water.
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

The mod and the sidecar must be the same version: when the mod updates, extract the new sidecar too. A sidecar from
another version cannot talk to the mod.

Players who join a game or a dedicated server do not need the mod. With it, their game shares where they look, so
the agent's camera verbs (`inspect {what: "looking_at"}`, placing at the crosshair, `show {what: "highlight"}`,
`show {what: "preview"}`) work for them on a dedicated server too, and highlights are drawn on their screen.

Full steps, Codex, dedicated servers and multiplayer: **[docs/install.md](docs/install.md)**.

## First steps

Ask the agent to call these, in order, to see that everything works:

1. `help {}`: the verbs and the shared topics (targets, paging, writes, shaping, refs, errors, cheats...).
2. `find {what: "device"}`: every device in your world, with the ids other calls take.
3. `inspect {what: "looking_at"}`: what your crosshair is on. Point at something and ask the agent about it.

From there, ask in plain words: "which rooms are losing pressure", "what does this chip do", "run heavy cable from
the APC to the new room along the frames". `help {verb: "build"}` lists what a verb can act on;
`help {verb: "build", what: "cable"}` describes one in full, with an example; and a refusal's `fix` names the help
topic that explains it.

## Tools

17 tools: 15 verbs, plus `help` and `run_script`. A call names the verb and, for most, the kind of thing it acts on:

```json
{"verb": "find", "what": "device", "prefab": "*GasSensor*"}
```

| Verb | What it does |
| --- | --- |
| `find` | List things in the world or an area, filtered, nearest first. |
| `inspect` | Describe one thing, or a few by id, in detail. |
| `survey` | Map an area or a network as a whole: the grid's cells, a wall face, a network's members, feed paths. |
| `read` | Read live values: logic, memory, a snapshot, samples over time, the clock, flight logs. |
| `write` | Set values and states. |
| `plan` | Work out how, changing nothing: routes, removal prices, free spots, rocket flights, mining yields, aim angles. |
| `check` | Check against rules and risks: the layout lint of a room or box, the lint rule set, whether things are replaceable, the player's ignition risk. |
| `build` | Build new things as a player would, paying materials. |
| `swap` | Swap things already built, in place: walls or frames, upgrade or tidy cable, pipe and chute networks. |
| `remove` | Take things away as a player would, with refunds and guards. |
| `move` | Move items, gas and players. |
| `trade` | Trade with traders: contacts in the sky, what each buys and sells, and buying or selling with the landed trader. |
| `show` | Draw on the player's screen only, for a while: a build preview, or highlights through walls. |
| `undo` | Undo a finished build or removal job, a bulk write (write-N), a script run (script-14: the whole run, newest first), or the newest blueprint paste. |
| `server` | StationGod and the game server: mod version, health and costs, profiling, the in-game console. |
| `help` | StationGod's help: `help {}` lists verbs and topics; `{verb}` a verb's whats and options; `{verb, what}` one what in full; `{topic}` a shared topic. |
| `run_script` | Run Python 3.10 against the game: one call made of verb calls. |

Each verb's help names its whats, their options and their reply keys. The shared topics cover what every verb
takes: `targets` (id, a list of ids, a ref, near + radius, box, room, network, prefab), `paging` (limit and offset,
next page), `writes` (what acts at once, what reports first, `confirm`), `shaping` (fields, omit, list_limits,
output_file), `refs` (rN: an earlier reply, repeat, from_plan), `errors`, `cheats` and more.

**Every change reports first.** Build, swap, remove, trade and undo calls, and every cheat, only report until
`confirm: true`; so does any write whose targets you did not type out (a ref, a network, a spread). A write to typed
ids acts at once. A report changes nothing and lists every target with its current value under `targets`; a
confirmed job runs in one held game tick and is checked afterwards; materials are taken from your inventory as the
game would charge, and refunds are put back into it. See
[building.md](docs/building.md#how-every-building-tool-works).

A refusal is `{code, message, fix}`: the message says what went wrong, the fix what to do next. Missing or wrong
options are one `invalid_argument` listing every problem at once (`{path, problem}` each), with a short list of what
the what does take, nearest first. The mod does the checking, so scripts that talk to it directly get the same
checks. `game_unavailable` means the MCP server could not reach the game; a write may have run if the game took the
call. After a game update a missing game member turns off only the whats that need it, which answer `game_changed`.

**Large replies.** Every verb takes the shaping options `fields`, `omit` and `list_limits`. `fields: ["reference_id",
"position"]` keeps only those keys in each entry of the reply's lists, and a dotted name is a path read from each
entry, through nested objects and lists at any depth (`occupant.prefab_name`, `held_in.reference_id`). `omit:
["source", "runtime.registers"]` leaves keys out wherever a path reaches them. A name that matches nothing comes
back in `fields_unmatched` with the nearest names in `fields_closest`, and `omit` names that matched nothing in
`omit_unmatched`. The mod applies `fields` and `omit` while it writes the reply, so keys left out are never formatted
or sent; where a key costs the game real work, the mod does not even work it out when `fields` leaves it out.
`output_file: true`, or a file name such as `"ores"`, writes the whole reply as indented JSON to a file and answers
a small pointer instead: `{output_file (the full path), bytes, tool, counts, summary, in_file_only}`. Files go to
`%LOCALAPPDATA%\StationGodMCP\output` on the machine the sidecar runs on (the agent's), or the folder given with
`--output-dir`; a named file is overwritten, and files older than 7 days or beyond the newest 200 are deleted. A
reply larger than 200 KB that did not ask for `output_file` is written to a file on its own and answered with the
pointer, marked `auto_output_file: true` (`--inline-limit-kb` changes the size).

A direct reply over 12 KB, to a call that asked for no size, is cut to about 4 KB: it keeps whole entries where they
fit, else every entry in a shallow form, and adds `ref`, `cut` and `next` (repeat the call with `whole: true`, or
with an `offset` for the next page). `whole: true` lifts the cut; a report, a paging `limit`, `list_limits` and
`output_file` are never cut. Lists answer one page at a time: `limit` entries from `offset`, with `total` and
`has_more`, and `repeat: "r3"` with a new `offset` fetches the next page. Every reply ends with `truncated`, naming
each list it held back, how many it returned, the total, and how to get the rest (`[]` when it held nothing back);
`list_limits: {"devices": 100}` keeps more of one list.

Things are named by `display_name`, the game's own name (the label, else the localised name). Where the game has no
English name for a prefab it shows a placeholder such as `<N:EN:StructureCrewUmbilicalDoor>`; `display_name` and every
message then carry the prefab name instead (`StructureCrewUmbilicalDoor`), the same in every verb.

## Configuration

A single local game needs none. The settings cover the local pipe's name, remote access over TCP, and how much of
each frame the mod may spend: [docs/configuration.md](docs/configuration.md).

The game stays smooth while several clients call at once. The mod answers within a per-frame budget
(`[Performance] RequestBudgetMs`), clients take turns, quick calls go first, and at most one heavy call (a survey, a
plan, a building job) runs per frame.

To see what the mod costs a running server, `server {what: "profiling"}` switches a profiler on and off at runtime:
per frame, per call and per job tick hold, with a slow-frame warning in the log and an optional rolling CSV
([devices-and-logic.md](docs/devices-and-logic.md#profiling-a-running-server)). Off, it costs next to nothing. It
runs on the host, where requests run, and sends nothing to other players.

## Your own programs

Scripts and dashboards can talk to the game directly, without an agent, through the Python client library in
`clients/python` (`py -3.12 -m pip install -e clients/python`), which speaks the mod's current protocol: several calls
at once on one connection, reconnecting, and **subscriptions**, which push a set of device values each time one of
them changes instead of reading them every tick. The protocol itself is in
[docs/architecture/protocol.md](docs/architecture/protocol.md).

An agent or a program can also run Python inside the game's hub with `run_script`: one call made of verb calls, with
waits, polls and repeated work written in ordinary Python. A run changes nothing unless it is confirmed, and a
finished run's writes can be taken back with `undo {id: "script-14"}`.

## Safety

- **It can cheat.** Every what is a read, a write (what a player or a chip can do: logic, chips, labels, paint,
  item moves, trading, building), or a cheat (what no player can: `move {what: "gas"}` and `"player"`,
  `write {what: "memory", "battery", "genes"}`, `server {what: "command"}`, free placement, blueprint pastes,
  forced item moves). Every connection can call every verb; the class is there so an agent can tell you when a call
  is a cheat and ask first. Reports make even the risky writes show their targets first. There is no general undo:
  save first.
- **Host only.** The mod runs on the game that hosts; its changes reach other players through the game's own sync.
  Every player who joins needs the same version, because the StationGod Gateway is a new structure.
- **The building verbs refuse rather than guess.** They never make materials, never delete a pipe network's
  contents, never remove a chute with an item in it, never touch indestructible pieces or a launching or landing rocket, and
  never swap a
  wall or frame in a way that opens a room. Every job that changes pipe networks checks their contents before and
  after (`gas_check`) and stops further pipe jobs if anything went missing, until the world is reloaded or the user
  agrees to accept the loss (`acknowledge_gas_lost`).
- **Remote access is not encrypted.** Over TCP the shared secret keeps out anyone without it, but the secret and
  everything after it travel in plain text: use a private network or a VPN.
- **After a game update** the mod checks at load every game member it relies on. A missing one turns off only the
  whats that need it, which answer `game_changed`; `server {what: "info"}` lists them.

## Documentation

- [Install](docs/install.md): Workshop, sidecar, Claude Code, Codex, dedicated servers, multiplayer, removing it.
- [Configuration](docs/configuration.md): mod settings, sidecar options, environment variables.
- [Devices, logic and console](docs/devices-and-logic.md)
- [Chips: IC10 and Lua](docs/chips.md)
- [Items](docs/items.md)
- [SDB Silo](docs/silo.md)
- [Air, planet, plants and survival](docs/air-planet-and-plants.md)
- [Solar, dishes and traders](docs/solar-and-traders.md)
- [Building cables, pipes and chutes](docs/building.md)
- [Clean-up and refactoring networks](docs/cleanup-and-refactor.md)
- [Walls, frames and structures](docs/walls-frames-structures.md)
- [Lint rules](docs/lint-rules.md): the rule file, your own rules per save, the rule language, fields and functions.
- [Blueprints](docs/blueprints.md)
- [Building from source](docs/building-from-source.md) (maintainers)
- [Changes per version](CHANGELOG.md)

## License and links

[MIT](LICENSE). By XCeled.

- Steam Workshop: search for *StationGod MCP* in the Stationeers Workshop.
- Source and issues: [github.com/altmank/Stationeers-StationGodMCP](https://github.com/altmank/Stationeers-StationGodMCP/issues)
