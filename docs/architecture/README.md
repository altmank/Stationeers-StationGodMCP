# StationGod MCP: the built system

StationGod gives an agent, a script or a program read and write access to a running Stationeers world. Three pieces
do the work. The **mod** runs inside the game on the host, owns every verb, and answers one newline-delimited JSON
protocol on a named pipe (`StationGodMCP`, `src/StationGodMCP.Mod/Pure/PipeName.cs:11`) or TCP
(`src/StationGodMCP.Mod/StationGodMod.cs:34`, `:230`). The **sidecar** (`StationGodMCP.Server`) is one binary in two
roles: an MCP server for an agent, and a **hub** that holds one game connection and gives every program its own stream
on it (`src/StationGodMCP.Server/Program.cs:12-24`, `src/StationGodMCP.Server/Hub/HubMode.cs:11`). The **Python
library `sg`** is the client
for programs and scripts; it never talks to the mod, only to a hub (`clients/python/sg/__init__.py:1-11`).

The layers, from the game out:

- **The game ⇄ the mod.** The mod's verbs: a call is `{"method":"<verb>","params":{"what", ...options}}`, answered
  by id; hello/welcome, streams, shaping, errors, subscriptions and resuming are [protocol.md](protocol.md).
- **The mod ⇄ the sidecar.** The same wire, through the C# client `src/StationGodMCP.Client` (`GameLink.cs:29-90`).
- **The agent ⇄ the sidecar.** MCP: `initialize`, `ping`, `tools/list` (17 tools) and `tools/call`
  (`src/StationGodMCP.Server/McpAdapter.cs:11-17`, `:180-184`).
- **A run, a program or the dashboard ⇄ a hub.** The hub protocol, which is close to the mod's wire
  ([hub.md](hub.md)): `run_script` workers, `sg.connect()` programs and the dashboard all speak it
  (`src/StationGodMCP.Server/Hub/HubSession.cs:8-17`).

Every verb, its options and its reply are described once in `catalogue/verbs/`; the mod, the sidecar and `sg` all read
that model, or files generated from it ([catalogue.md](catalogue.md)).

| Page | What it holds |
| --- | --- |
| [protocol.md](protocol.md) | The mod's wire: framing, hello/welcome, calls, streams, order, shaping, errors, subscriptions, resuming, holds. |
| [hub.md](hub.md) | The hub: modes, streams, the hub protocol and faces, runs, the seen set, the journal, recording, the C# seams. |
| [catalogue.md](catalogue.md) | `catalogue/verbs`: its files, the generated files, its readers, its hash, and what a change means. |
| [clients.md](clients.md) | The C# client, `sg`, the hub `sg` talks to, and the dashboard's child hub. |
| [scheduling.md](scheduling.md) | The frame: budget, lanes, cost classes, streams, subscriptions, settings. |
| [reply-budget.md](reply-budget.md) | The 12 KB reply cap, the run's own 4 KB cut, and the size budgets around them. |

Claims cite the code as `file:line`; paths without a repository name are in this repository.

## The consumers

**An agent** starts one sidecar and sees its 17 tools; the sidecar's `tools/list` is the verb list, and `tools/call`
runs the direct face (`McpAdapter.cs:183-184`). **A script** is one `run_script` call: the hub runs the code in a
Python worker (Python 3.10 or later, `clients/python/pyproject.toml` *requires-python*), the worker calls verbs
through `sg`, and writes act only on targets the session has seen ([hub.md](hub.md), *Runs*). **A Python program**
calls `sg.connect()` and gets a child hub of its own (`clients/python/sg/_transport.py:275`). **The dashboard**
vendors `sg` and keeps one child hub for the process (StationeersScriptDashboard `stationscript/transport.py:1-14`).
**The test server** runs the same mod under its own pipe name (StationeersTestServer `testserver.json`), and the
test-side tools choose the pipe they speak to.

## Where the code lives

| Path | What it is |
| --- | --- |
| `src/StationGodMCP.Mod/` | The mod: verbs (`Verbs/`), the game API (`Api/`), the wire (`Protocol/`), pure logic with tests (`Pure/`). |
| `src/StationGodMCP.Server/` | The sidecar and hub: MCP (`McpAdapter.cs`), the verb pipeline (`Verbs/`), the hub (`Hub/`), runs (`Runs/`). |
| `src/StationGodMCP.Client/` | The C# client to the mod: `GameLink`, `GameConnection`, `GameCatalogue`, `OutputFiles`. |
| `catalogue/verbs/` | The one verb model; `generated/verbs/` holds what is generated from it. |
| `clients/python/sg/` | The Python library, its generated verb layer and the `run_script` worker. |
| `tests/StationGodMCP.Tests/` | The C# tests: verbs, hub, scheduling, catalogue; `clients/python/tests/` the Python ones. |
