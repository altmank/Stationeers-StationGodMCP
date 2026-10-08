# Configuration

[Back to the README](../README.md)

A single local game needs no configuration. The settings cover two cases: a second game on the same machine (the pipe
name) and an agent on another machine (remote access over TCP).

## Mod settings

Edit them in StationeersLaunchPad's config editor, or in `BepInEx\config\net.xceled.stationeers.stationgodmcp.cfg`.
They are read once when the game starts: restart the game to apply a change. Each setting also reads an environment
variable, which wins over the file (handy for Docker and dedicated servers).

| Section | Setting | Default | Environment variable | What it does |
| --- | --- | --- | --- | --- |
| `Pipe` | `Name` | `StationGodMCP` | `STATIONGODMCP_PIPE_NAME` | Name of the local pipe the mod listens on, the part after `\\.\pipe\`. Not empty, no `\`, `/` or `:`; an invalid name is logged and the default used. |
| `Remote MCP` | `Enabled` | `false` | `STATIONGODMCP_REMOTE_ENABLED` | Accept clients over TCP. Listens once there is a `Secret`. |
| `Remote MCP` | `BindAddress` | `0.0.0.0` | `STATIONGODMCP_REMOTE_BIND_ADDRESS` | Address to listen on. `0.0.0.0` is every network interface. |
| `Remote MCP` | `Port` | `8765` | `STATIONGODMCP_REMOTE_PORT` | TCP port, 1 to 65535. |
| `Remote MCP` | `Secret` | empty | `STATIONGODMCP_REMOTE_SECRET` | Shared secret every TCP client must send first. Sent unencrypted: use a long random value. |
| `Server` | `MaxPipeConnections` | `32` | none | The most local pipe connections at once, 1 to 254 (each agent session, the dashboard and each script keeps one). A client past it waits until one closes. |
| `Server` | `StrictArguments` | `true` | none | Check every call against the method catalogue in full before it runs: argument names at every depth, types, ranges, enums, patterns, required arguments and the `shape`; a refusal lists each problem with its path. `false` checks top-level argument names only. |
| `Server` | `MaxTcpConnections` | `8` | none | The most TCP connections at once, 1 to 64, counted apart from the pipe's. |
| `Performance` | `RequestBudgetMs` | `4` | none | Main-thread milliseconds one frame may spend answering requests; the rest wait for the next frame. Clients take turns, one call each per round, and a call that changes the world still waits for its own client's earlier calls. The first request of a frame always runs. `0` is unlimited. While a job holds the game tick the budget is at most 2 ms. A negative value is logged and the default used. |
| `Performance` | `SubscriptionBudgetMs` | `1.5` | none | Milliseconds of each frame for subscription and `sample_logic` samples, taken out of the same frame and at most half of `RequestBudgetMs`. The first due sample of a frame always runs. `0` turns subscriptions off (`subscribe` is refused `subscription_limit`, and clients poll instead). |
| `Performance` | `HeavyThresholdMs` | `1.0` | none | A call the mod expects to take longer than this (from the method's recent calls, per item) waits in the heavy lane. Surveys, plans and building jobs are always heavy. At most one heavy call runs per frame, after the light ones. |
| `Performance` | `HeavyMaxWaitFrames` | `10` | none | A heavy call passed over this many frames runs even when the frame is over budget, so heavy calls always finish. |
| `Performance` | `Profiling` | `false` | none | Start the runtime profiler when the mod loads, as the `profiling` tool's `action: on` does (`slow_frame_ms` 8, no CSV). It times StationGod's main-thread work per frame, per call and per job tick hold; the `profiling` tool switches it on and off at any time. Off, each timed piece costs one check. It records on the machine it runs on and sends nothing to other players. |
| `Performance` | `VerifyPrefabIndex` | `false` | none | A check for the prefab index: every `find_things`, `find_items` or `item_totals` call filtered by `prefab`, `prefabs` or `prefab_contains` also walks every thing in the world and logs a warning when the two answers differ; `mod_info` `runtime.prefab_index` counts the checks (`verified`) and differences. Each such call costs a full walk again, so leave it off in play. |
| `Multiplayer` | `ShareViews` | `true` | none | Share views between games: a client tells a server that runs StationGod where its player looks, so the server's camera tools work for that player, and the server's `highlight` and `show_preview` show on that player's screen. StationGod stays optional on every player's game. It uses StationeersLaunchPad's networking, which makes a game expect that networking on the other side of a join: on a server where no other mod uses it, `false` lets in players whose game runs no such mod. |
| `Layout` | `DoorKeepOutBand` | `0.5` | none | Metres either side of a door's face, inside the door's rectangle, that the route planners keep free and the place tools refuse (`in_door_keepout`); 0 to 2 in 0.5 steps, 0 keeps only the face itself. Unlike the others it applies to the next request. |

Environment values: `true` or `false` for `Enabled`, a number for `Port`. An invalid value is logged and the file's
value used.

The pipe needs Windows. On any other system (a Linux dedicated server) the log says the pipe is not available, and
only remote access over TCP serves.

The log confirms what is in use: `Pipe name: <name>.`, then
`Authoritative MCP bridge listening on \\.\pipe\<name> (overlapped, up to 32 connections).` once a
hosted save is loaded, and, with remote access on,
`Authenticated remote MCP bridge listening on <address>:<port> (plain TCP).`

## Sidecar options

Given as arguments after the sidecar's path in the agent's MCP registration, or as environment variables. An argument
wins over its variable.

| Argument | Environment variable | Default | What it does |
| --- | --- | --- | --- |
| `--pipe <name>` | `STATIONGODMCP_PIPE_NAME` | `StationGodMCP` | The local pipe to connect to. Must match the game's `[Pipe] Name`. |
| `--host <address>` | `STATIONGODMCP_HOST` | none | Connect to a server over TCP instead of the local pipe. |
| `--port <port>` | `STATIONGODMCP_PORT` | `8765` | The server's TCP port. |
| `--secret-env <NAME>` | | `STATIONGODMCP_SECRET` | The environment variable that holds the shared secret, sent first over TCP (the server's `[Remote MCP] Secret`). |
| `--client <name>` | | none | The name the sidecar gives itself in `hello` (protocol 2), shown in `mod_info`'s connections. |
| `--inline-limit-kb <n>` | | `200` | A reply larger than this, without `output_file`, is written to a file on its own and answered with the pointer, marked `auto_output_file: true`. `0` never does. |
| `--output-dir <folder>` | `STATIONGODMCP_OUTPUT_DIR` | `%LOCALAPPDATA%\StationGodMCP\output` | Where `output_file` writes replies (README, *Large replies*). The sidecar deletes files there older than 7 days and keeps the newest 200 `.json` files. |

The secret is never an argument, so it does not show in process lists. Set it in the agent's MCP
registration (`claude mcp add --env "STATIONGODMCP_SECRET=..."`, or `[mcp_servers.stationeers.env]` in Codex).

The sidecar keeps one connection to the game and sends the agent's calls over it as they come, several at once. It
waits 1 second to connect to the pipe (3 over TCP), and for each call its deadline (30 seconds) plus the call's own
duration (`sample_logic`'s `duration_seconds`) plus 5 seconds. A call that never reached the game, or a read whose
connection broke, is sent again once on a new connection, never into another world; anything else that may have run is
answered `game_unavailable` saying so. When the game's tool list changes (a newer mod), the sidecar tells the agent
(`notifications/tools/list_changed`).

Setup walkthroughs for remote access and two games on one machine: [install.md](install.md#dedicated-servers-and-remote-access).
