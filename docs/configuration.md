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
| `Performance` | `RequestBudgetMs` | `4` | none | Main-thread milliseconds one frame may spend answering requests; the rest wait for the next frame, in order. The first request of a frame always runs. `0` is unlimited. While a job holds the game tick the budget is at most 2 ms. A negative value is logged and the default used. |
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
  {"name": "dashboard", "key": "<base64 of 32 random bytes>", "level": "write", "grants": ["write_memory"], "cheat": "standing", "transports": ["pipe"]},
  {"name": "agents", "key": "...", "level": "cheat", "cheat": "armed", "transports": ["pipe", "tcp"]}
]}
```

`level` is read, write or cheat; `grants` names methods allowed beyond it; `cheat` is `armed` (default: cheat calls
need `stationgod allow` in the game) or `standing`; `transports` is `pipe` (default), `tcp` or both. A wrong entry (a
short key, an unknown level, a name given twice, an unknown transport) is left out and logged; the rest load. The
file is read again within seconds of a change.

## Sidecar options

Given as arguments after the sidecar's path in the agent's MCP registration, or as environment variables. An argument
wins over its variable.

| Argument | Environment variable | Default | What it does |
| --- | --- | --- | --- |
| `--pipe <name>` | `STATIONGODMCP_PIPE_NAME` | `StationGodMCP` | The local pipe to connect to. Must match the game's `[Pipe] Name`. |
| `--host <address>` | `STATIONGODMCP_HOST` | none | Connect to a server over TCP instead of the local pipe. |
| `--port <port>` | `STATIONGODMCP_PORT` | `8765` | The server's TCP port. |
| `--secret-env <NAME>` | | `STATIONGODMCP_SECRET` | The environment variable that holds the shared secret. With `--host`, the sidecar refuses to start without it. |
| `--output-dir <folder>` | `STATIONGODMCP_OUTPUT_DIR` | `%LOCALAPPDATA%\StationGodMCP\output` | Where `output_file` writes replies (README, *Large replies*). The sidecar deletes files there older than 7 days and keeps the newest 200 `.json` files. |

The secret is never an argument, so it does not show in process lists. Set it in the agent's MCP registration
(`claude mcp add --env "STATIONGODMCP_SECRET=..."`, or `[mcp_servers.stationeers.env]` in Codex).

The sidecar waits 3 seconds to connect and up to 35 seconds for the game to answer one call.

Setup walkthroughs for remote access and two games on one machine: [install.md](install.md#dedicated-servers-and-remote-access).
