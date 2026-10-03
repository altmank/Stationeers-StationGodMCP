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

When the mod updates, extract the new sidecar over the old one and restart your agent. The sidecar and the mod must
be the same version: they speak one protocol, and a sidecar from another version cannot talk to the mod.

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

- **The host needs the mod; the players who join do not.** Requests run on the host, where the game's state lives,
  and every change reaches players through the game's own sync. A player without StationGod, or with another version,
  joins as before. One exception: a world with a StationGod Gateway in it needs the mod on every player's game, as the
  Gateway and its kit are new prefabs, which a game without the mod cannot show.
- **What StationGod on a player's game adds (1.12.0+).** On a dedicated server the agent's "the player" is the one
  connected player, but their camera is on their own machine. With StationGod there, their game shares where they look
  with the server, so the camera tools work for them as in single player: `looking_at`, placing at the `crosshair`,
  `on_face_i_look_at`, the `player` frame, `find_spot` near the crosshair, `wall_map` and `find_spot` with
  `looking: true`, and `highlight` and `show_preview`, which their game then draws on their screen (the reply's
  `drawn_on` names them). Without it those calls refuse `no_view`, and the message says why (no StationGod on their
  game, or a version that speaks another view protocol). A view the player has since walked away from (more than
  0.3 m, or older than 5 s once they moved) is refused `view_stale` rather than read; standing still keeps it current,
  alt-tabbed or not. Both games need the same view protocol, which this version calls 1; the log of each says when
  they differ.
- StationGod's messages between games go through StationeersLaunchPad's networking. As soon as any mod uses it, a game
  expects it on the other side of a join, so on a server where StationGod is the only mod that does, a player whose
  game runs no such mod cannot join. If that matters, set `[Multiplayer] ShareViews = false` on the server (see
  [configuration](configuration.md)); the camera tools then refuse `no_view` for remote players.
- The pipe and the TCP listener open only on the host. On a client the mod serves no requests: it shares its player's
  view with a server running StationGod and draws what that server sends it.
- Anyone who can run a program on the host's machine can open the pipe; over TCP only the secret gets in. Every
  connection can call every tool, cheats included; agents tell you when a tool is a cheat.
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
