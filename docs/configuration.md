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
| `Remote MCP` | `Enabled` | `false` | `STATIONGODMCP_REMOTE_ENABLED` | Accept clients over TCP. Listens once there is a `Secret` (the old sign-in) or a key whose transports include `tcp`. |
| `Remote MCP` | `BindAddress` | `0.0.0.0` | `STATIONGODMCP_REMOTE_BIND_ADDRESS` | Address to listen on. `0.0.0.0` is every network interface. |
| `Remote MCP` | `Port` | `8765` | `STATIONGODMCP_REMOTE_PORT` | TCP port, 1 to 65535. |
| `Remote MCP` | `Secret` | empty | `STATIONGODMCP_REMOTE_SECRET` | Shared secret every sidecar must send. Sent unencrypted: use a long random value. |
| `Server` | `MaxPipeConnections` | `32` | none | The most local pipe connections at once, 1 to 254 (each agent session, the dashboard and each script keeps one). A client past it waits until one closes. |
| `Server` | `OverlappedPipes` | `true` | none | Serve the pipe so each connection can read and write at once. `false` goes back to the synchronous pipe of 1.10 and earlier (four connections); off Windows that one is always used. |
| `Server` | `Protocol2` | `true` | none | Let pipe clients speak protocol version 2 (a first line of type `hello`: several calls in flight per connection, `cancel`, events). `false` answers every connection with version 1 only. |
| `Server` | `StrictArguments` | `true` | none | Check version-2 calls against the method catalogue in full before they run: argument names at every depth, types, ranges, enums, patterns, required arguments and the `shape`; a refusal lists each problem with its path. `false` checks them as version 1 is checked (top-level names only). Version 1 is never checked in full. |
| `Server` | `MaxTcpConnections` | `8` | none | The most TCP connections at once, 1 to 64, counted apart from the pipe's. |
| `Access` | `AnonymousPipeLevel` | `write` | none | What a pipe client of the current protocol without a key may do: `none` (refused), `read`, `write` or `cheat`. |
| `Access` | `AnonymousCheat` | `armed` | none | With `AnonymousPipeLevel = cheat`: `armed` (each cheat call needs `stationgod allow` in the game) or `standing`. |
| `Access` | `LegacyPipeLevel` | `cheat` | none | What a pipe client of the old protocol (no `hello`) may do: `none` refuses it; at cheat it needs no approval, as before. |
| `Access` | `LegacyTcpLevel` | `cheat` | none | What a TCP client signing in with the shared `Secret` may do: `none` refuses it. The secret travels in plain text; while this is on, the log warns at load and records each such sign-in with its address. |
| `Access` | `AllowArmingFromToolConsole` | `false` | none | Test servers only: lets `run_console_command` run `stationgod allow`. Never set it on a game you play. |
| `Performance` | `RequestBudgetMs` | `4` | none | Main-thread milliseconds one frame may spend answering requests; the rest wait for the next frame. Clients take turns, one call each per round, and a call that changes the world still waits for its own client's earlier calls. The first request of a frame always runs. `0` is unlimited. While a job holds the game tick the budget is at most 2 ms. A negative value is logged and the default used. |
| `Performance` | `SubscriptionBudgetMs` | `1.5` | none | Milliseconds of each frame for subscription and `sample_logic` samples, taken out of the same frame and at most half of `RequestBudgetMs`. The first due sample of a frame always runs. `0` turns subscriptions off (`subscribe` is refused `subscription_limit`, and clients poll instead). |
| `Performance` | `HeavyThresholdMs` | `1.0` | none | A call the mod expects to take longer than this (from the method's recent calls, per item) waits in the heavy lane. Surveys, plans and building jobs are always heavy. At most one heavy call runs per frame, after the light ones. |
| `Performance` | `HeavyMaxWaitFrames` | `10` | none | A heavy call passed over this many frames runs even when the frame is over budget, so heavy calls always finish. |
| `Layout` | `DoorKeepOutBand` | `0.5` | none | Metres either side of a door's face, inside the door's rectangle, that the route planners keep free and the place tools refuse (`in_door_keepout`); 0 to 2 in 0.5 steps, 0 keeps only the face itself. Unlike the others it applies to the next request. |

Environment values: `true` or `false` for `Enabled`, a number for `Port`. An invalid value is logged and the file's
value used.

The log confirms what is in use: `Pipe name: <name>.`, then
`Authoritative MCP bridge listening on \\.\pipe\<name> (overlapped, up to 32 connections).` once a
hosted save is loaded, and, with remote access on,
`Authenticated remote MCP bridge listening on <address>:<port> (plain TCP).`

## Keys: the clients file

`BepInEx\config\net.xceled.stationeers.stationgodmcp.clients.json`, beside the mod's config file, holds the keys
clients of the current protocol sign in with. `StationGodMCP.Server key new <name> <level>` writes entries
([install.md](install.md), *Keys and levels*); the mod never writes it and logs a key only by name and fingerprint.

```json
{"clients": [
  {"name": "dashboard", "key": "<base64 of 32 random bytes>", "level": "write", "grants": ["write_memory"], "transports": ["pipe"]},
  {"name": "agents", "key": "...", "level": "cheat", "cheat": "armed", "transports": ["pipe", "tcp"]}
]}
```

`level` is read, write or cheat; `grants` names methods allowed beyond it; `cheat` is `armed` (default: cheat calls
need `stationgod allow` in the game) or `standing`; `transports` is `pipe` (default), `tcp` or both. A wrong entry (a
short key, an unknown level, a name given twice, an unknown transport) is left out and logged; the rest load. The
file is read again within seconds of a change.

A grant lets a key call one method above its level; it does not lift the approval a cheat method needs. The
dashboard's key above is write with a `write_memory` grant and no standing cheat: its smelter watchdog's
`write_memory` runs only while you have approved the dashboard with `stationgod allow` in the game, and is refused
`cheat_not_armed` otherwise. Do not give the dashboard `--cheat standing`.

## Sidecar options

Given as arguments after the sidecar's path in the agent's MCP registration, or as environment variables. An argument
wins over its variable.

| Argument | Environment variable | Default | What it does |
| --- | --- | --- | --- |
| `--pipe <name>` | `STATIONGODMCP_PIPE_NAME` | `StationGodMCP` | The local pipe to connect to. Must match the game's `[Pipe] Name`. |
| `--host <address>` | `STATIONGODMCP_HOST` | none | Connect to a server over TCP instead of the local pipe. |
| `--port <port>` | `STATIONGODMCP_PORT` | `8765` | The server's TCP port. |
| `--secret-env <NAME>` | | `STATIONGODMCP_SECRET` | The environment variable that holds the old shared secret, asked for only when a mod over TCP speaks the old protocol. |
| `--client <name>` | | none | Sign in as this key's name (protocol 2). Without it the sidecar connects without a key, at `[Access] AnonymousPipeLevel` on the pipe; over TCP a key is required. |
| `--key-env <NAME>` | | `STATIONGOD_KEY_<PIPE NAME>` | The environment variable that holds the key for `--client` (for the default pipe `STATIONGOD_KEY_STATIONGODMCP`). |
| `--inline-limit-kb <n>` | | `200` | A reply larger than this, without `output_file`, is written to a file on its own and answered with the pointer, marked `auto_output_file: true`. `0` never does. |
| `--output-dir <folder>` | `STATIONGODMCP_OUTPUT_DIR` | `%LOCALAPPDATA%\StationGodMCP\output` | Where `output_file` writes replies (README, *Large replies*). The sidecar deletes files there older than 7 days and keeps the newest 200 `.json` files. |

The secret and the key are never arguments, so they do not show in process lists. Set them in the agent's MCP
registration (`claude mcp add --env "STATIONGODMCP_SECRET=..."`, or `[mcp_servers.stationeers.env]` in Codex).

The sidecar keeps one connection to the game and sends the agent's calls over it as they come, several at once. It
waits 1 second to connect to the pipe (3 over TCP), and for each call its deadline (30 seconds) plus the call's own
duration (`sample_logic`'s `duration_seconds`) plus 5 seconds. A call that never reached the game, or a read whose
connection broke, is sent again once on a new connection, never into another world; anything else that may have run is
answered `game_unavailable` saying so. When the game's tool list changes (a newer mod), the sidecar tells the agent
(`notifications/tools/list_changed`).

Setup walkthroughs for remote access and two games on one machine: [install.md](install.md#dedicated-servers-and-remote-access).
