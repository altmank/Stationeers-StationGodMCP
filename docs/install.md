# Install

[Back to the README](../README.md)

StationGod MCP has two parts:

- **The mod** runs inside the game. It answers requests on the game's main thread, on the game that hosts.
- **The sidecar** is a small program your AI agent starts. It speaks MCP to the agent and passes each request to the
  game, through a local named pipe or, for a server on another machine, over TCP.

## Requirements

- Stationeers with BepInEx 5.4 and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).
- Windows x64 for the self-contained sidecar, or the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  for the portable one.
- An AI agent that runs MCP servers over stdio: Claude Code, Codex, or another MCP client.
- Optional mods, found by name at run time; everything else works without them:
  [StationeersLua](https://steamcommunity.com/sharedfiles/filedetails/?id=3659911735) for Lua chips and
  [BlueprintMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3672138641) for `paste_blueprint`.

## 1. Install the mod

**Steam Workshop:** subscribe to StationGod MCP and enable it in StationeersLaunchPad.

**Without the Workshop** (a dedicated server, a non-Steam setup): download `StationGodMCP.zip` from the
[latest GitHub Release](https://github.com/altmank/Stationeers-StationGodMCP/releases/latest) and extract it into the
game's local mods folder (`Documents\My Games\Stationeers\mods` for a player; a dedicated server's own `mods`
folder). The zip holds one `StationGodMCP` folder, so you end up with `mods\StationGodMCP\About\About.xml` and
`mods\StationGodMCP\StationGodMCP.dll`. Then enable it in StationeersLaunchPad.

## 2. Get the sidecar

Two sources, the same files:

- **From the Workshop item:** its folder, `steamapps\workshop\content\544550\<item id>\Sidecar`, holds both archives.
- **From GitHub:** the [latest Release](https://github.com/altmank/Stationeers-StationGodMCP/releases/latest) has both
  archives and `SHA256SUMS.txt`. Check a download before extracting it, in PowerShell:

  ```powershell
  (Get-FileHash .\StationGodMCP.Server-win-x64.zip -Algorithm SHA256).Hash
  ```

  The hash must match that file's line in `SHA256SUMS.txt` (upper or lower case does not matter).

Extract one archive to a folder outside every Stationeers mod and Workshop folder. This guide uses
`%LOCALAPPDATA%\StationGodMCP\server`.

| Archive | Needs | Run it as |
| --- | --- | --- |
| `StationGodMCP.Server-win-x64.zip` | nothing | `StationGodMCP.Server.exe` |
| `StationGodMCP.Server-portable.zip` | .NET 8 runtime | `dotnet StationGodMCP.Server.dll` |

Every Release has the same four files:

| File | What it is |
| --- | --- |
| `StationGodMCP.zip` | The mod package: one `StationGodMCP` folder for the mods folder. |
| `StationGodMCP.Server-win-x64.zip` | The self-contained sidecar. |
| `StationGodMCP.Server-portable.zip` | The portable sidecar. |
| `SHA256SUMS.txt` | The SHA-256 of each zip. |

The newest one downloads from `https://github.com/altmank/Stationeers-StationGodMCP/releases/latest/download/<file>`,
a given version from `https://github.com/altmank/Stationeers-StationGodMCP/releases/download/v<version>/<file>` (for
example `.../releases/download/v1.3.1/StationGodMCP.zip`).

The executable is unsigned, so Windows SmartScreen may ask before its first run. The portable build runs through
Microsoft's signed `dotnet` host instead.

**Why outside the mod folder:** StationeersLaunchPad loads every DLL it finds in an enabled mod's folder. Extracted
there, the sidecar's DLLs would be taken for game plugins. That is also why they ship zipped.

When the mod updates, extract the new sidecar over the old one and restart your agent. The sidecar and the mod
should be the same version: the tool list lives in the sidecar, the tools themselves in the mod.

## 3. Register the sidecar with your agent

### Claude Code

In PowerShell (`$env:LOCALAPPDATA` expands to your own folder):

```powershell
claude mcp add --scope user --transport stdio stationeers -- "$env:LOCALAPPDATA\StationGodMCP\server\StationGodMCP.Server.exe"
```

Portable sidecar:

```powershell
claude mcp add --scope user --transport stdio stationeers -- dotnet "$env:LOCALAPPDATA\StationGodMCP\server\StationGodMCP.Server.dll"
```

Or, for one project only, in its `.mcp.json`:

```json
{ "mcpServers": { "stationeers": { "command": "C:/Users/<you>/AppData/Local/StationGodMCP/server/StationGodMCP.Server.exe" } } }
```

`claude mcp list` shows it. Restart Claude Code after replacing the sidecar.

### Codex

In `%USERPROFILE%\.codex\config.toml` (replace `<you>` with your Windows user name):

```toml
[mcp_servers.stationeers]
command = "C:\\Users\\<you>\\AppData\\Local\\StationGodMCP\\server\\StationGodMCP.Server.exe"
startup_timeout_sec = 10
tool_timeout_sec = 40
```

Portable sidecar: `command = "dotnet"` and
`args = ["C:\\Users\\<you>\\AppData\\Local\\StationGodMCP\\server\\StationGodMCP.Server.dll"]`.

### Other MCP clients

Add a stdio MCP server that runs the executable (or `dotnet` with the DLL). The sidecar waits up to 35 seconds for
the game to answer one call, so set the client's tool timeout above that.

## 4. Host a save

The mod only answers in a game that hosts. Turn on *Start Local Host* in the game's settings (`StartLocalHost` in
`setting.xml`), then load a save. A single-player game with the setting off, or a client joined to someone else's
server, gets no answer.

## 5. Check it works

- The BepInEx log (`BepInEx\LogOutput.log`) shows
  `[StationGodMCP] Authoritative MCP bridge listening on \\.\pipe\StationGodMCP` once a hosted save is loaded.
- Ask the agent to call `mod_info`. It answers with `mod_version` and `pipe_name`, and `missing_count` 0 when every
  game member the mod relies on was found.
- Then `list_devices`: every device in the world.

**No answer?** Check, in this order: the game hosts and a save is loaded; the log line above is there (lines starting
`[StationGodMCP]` say what went wrong); the agent lists the server (`claude mcp list`); the sidecar is not inside a mod
folder; the pipe name matches (see *Two games on one machine*).

## Multiplayer

- **The host needs the mod; every player who joins needs the same version.** Requests run on the host, where the
  game's state lives, and every change reaches players through the game's own sync. The StationGod Gateway and its kit
  are new prefabs, which a game without the mod cannot show.
- The pipe and the TCP listener open only on the host. On a client the mod does nothing.
- Anyone who can run a program on the host's machine can open the pipe; over TCP only a key or the secret gets in. A
  key's level, and your approval in the game for cheat tools, limit what a client does (see *Keys and levels*).
- Building jobs hold the game tick for a frame or two; players see a brief pause.

## Dedicated servers and remote access

A dedicated server runs the mod like a hosted game. An agent on the same machine uses the pipe as above. An agent on
another machine connects over TCP.

On the server, in `BepInEx\config\net.xceled.stationeers.stationgodmcp.cfg`:

```ini
[Remote MCP]
Enabled = true
BindAddress = 0.0.0.0
Port = 8765
Secret = replace-with-a-long-random-secret
```

Restart the server. Its log shows `Authenticated remote MCP bridge listening on 0.0.0.0:8765 (plain TCP).` Open the
port in the firewall or router. Each setting can also come from an environment variable, handy for Docker; see
[configuration.md](configuration.md).

On the agent's machine, start the sidecar with `--host` and `--port`, and put the secret in the environment variable
`STATIONGODMCP_SECRET`. Claude Code:

```powershell
claude mcp add --scope user --env "STATIONGODMCP_SECRET=<secret>" --transport stdio stationeers -- "$env:LOCALAPPDATA\StationGodMCP\server\StationGodMCP.Server.exe" --host station.example.com --port 8765
```

Codex:

```toml
[mcp_servers.stationeers]
command = "C:\\Users\\<you>\\AppData\\Local\\StationGodMCP\\server\\StationGodMCP.Server.exe"
args = ["--host", "station.example.com", "--port", "8765"]
tool_timeout_sec = 40

[mcp_servers.stationeers.env]
STATIONGODMCP_SECRET = "<secret>"
```

The server compares the secret in fixed time and keeps out anyone without it. The connection is not encrypted:
use a long random secret, change it if it leaks, and prefer a private network or a VPN over the open internet.

Several sidecars may connect at once.

**A dedicated server with nobody connected** may hold its world paused. Gas moves and blueprint pastes wait for the
game to run; the console command `pause false` (through `run_console_command`) unpauses it.

## Keys and levels

Clients that speak the current protocol (the Python client library, and the sidecar from a later version) sign in
with a key you make on the host, outside the game:

```powershell
& "$env:LOCALAPPDATA\StationGodMCP\server\StationGodMCP.Server.exe" key new dashboard write --grants write_memory
& "$env:LOCALAPPDATA\StationGodMCP\server\StationGodMCP.Server.exe" key new agents cheat --transports pipe,tcp
```

Each command adds the key to `BepInEx\config\net.xceled.stationeers.stationgodmcp.clients.json` (`--config <folder>`
for another game or a dedicated server) and prints it once, in that terminal only; give it to the client in its key
variable (`STATIONGOD_KEY_<PIPE NAME>`, for the default pipe `STATIONGOD_KEY_STATIONGODMCP`). The mod reads the file
again within seconds of a change; a client whose key was removed or lowered is disconnected. Options: `--grants
method,...` allows single methods beyond the level (a granted cheat method still needs your OK below), `--cheat
standing` lets a cheat key work without your approval, `--replace` replaces a key of that name. The dashboard's key is
write with only `write_memory` granted, for its smelter watchdog, and never standing: each time the watchdog needs to
write memory, you approve the dashboard in the game.

A key at cheat level still needs your OK for each cheat tool, for a while, in the game's console:

- `stationgod allow c9 15` approves one connection (its id is in its welcome and in `stationgod clients`) for 15
  minutes (at most 240); `stationgod allow agents 15` approves every connection of the key `agents`, subagents
  included.
- `stationgod deny c9` takes it back at once; `stationgod clients` lists the connections.
- On a client of a remote host, type `serverrun stationgod allow c9 15`; the game runs it on the host when both have the
  same `ServerAuthSecret`. Without one, use a key with `--cheat standing`.
- `run_console_command` never runs `stationgod`, so no client can approve itself.

A local connection without a key gets `[Access] AnonymousPipeLevel` (write). Clients of the old protocol, which
today's sidecar, the script dashboard and the scripts speak, keep full access (`[Access] LegacyPipeLevel`, and
`LegacyTcpLevel` for the old TCP secret) until the old protocol is switched off. Over TCP, a client of the current
protocol always needs a key whose `--transports` include `tcp`; with such a key, TCP listens even without a `Secret`.
See [configuration.md](configuration.md), *Access*.

## Two games on one machine

A game and a dedicated server on one machine (or two servers) both listen on the pipe `StationGodMCP` by default, and
a sidecar reaches whichever started first. Give one of them its own name:

```ini
[Pipe]
Name = StationGodMCP-Test
```

and start that game's sidecar with `--pipe StationGodMCP-Test`. `mod_info` reports `pipe_name`, so the agent can check
which game it reached. Players never see the pipe name; it only matters on the machine the game runs on.

## Removing it

Everything the agent built, placed, labelled or painted is ordinary game content and stays. Gateways and gateway kits
are the mod's own and disappear when a save loads without the mod (the game logs `Can't spawn` for each). Then delete
the sidecar folder and remove the agent's registration (`claude mcp remove stationeers`, or the `config.toml`
section).
