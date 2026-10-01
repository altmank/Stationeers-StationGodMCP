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
| `Remote MCP` | `Enabled` | `false` | `STATIONGODMCP_REMOTE_ENABLED` | Accept sidecars over TCP. Stays off, with a warning in the log, while `Secret` is empty. |
| `Remote MCP` | `BindAddress` | `0.0.0.0` | `STATIONGODMCP_REMOTE_BIND_ADDRESS` | Address to listen on. `0.0.0.0` is every network interface. |
| `Remote MCP` | `Port` | `8765` | `STATIONGODMCP_REMOTE_PORT` | TCP port, 1 to 65535. |
| `Remote MCP` | `Secret` | empty | `STATIONGODMCP_REMOTE_SECRET` | Shared secret every sidecar must send. Sent unencrypted: use a long random value. |
| `Layout` | `DoorKeepOutBand` | `0.5` | none | Metres either side of a door's face, inside the door's rectangle, that the route planners keep free and the place tools refuse (`in_door_keepout`); 0 to 2 in 0.5 steps, 0 keeps only the face itself. Unlike the others it applies to the next request. |

Environment values: `true` or `false` for `Enabled`, a number for `Port`. An invalid value is logged and the file's
value used.

The log confirms what is in use: `Pipe name: <name>.`, then
`Authoritative MCP bridge listening on \\.\pipe\<name>` once a hosted save is loaded, and, with remote access on,
`Authenticated remote MCP bridge listening on <address>:<port> (plain TCP).`

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
