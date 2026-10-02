# StationGod MCP, version 2 architecture

This folder is the specification for rebuilding how everything talks to StationGod MCP. It is written for the owner,
who approved the design, and for the people implementing it. The first part of this page says what changes and why
in plain words. The other pages hold the exact rules:

| Page | What it holds |
| --- | --- |
| [protocol.md](protocol.md) | The wire protocol: framing, versions, messages, errors, shaping, subscriptions, sign-in and permissions, pipe and TCP, and how today's clients keep working. |
| [catalogue.md](catalogue.md) | The method catalogue: its format, where it lives, how it is kept true to the code, and who reads it. |
| [scheduling.md](scheduling.md) | How the game's main thread is shared between clients, what requests cost today, and the limits on subscriptions. |
| [clients.md](clients.md) | The Python library, the C# client inside the sidecar, where `output_file` lives, and moving every existing client over. |
| [stages.md](stages.md) | The plan: small stages, each shippable on its own, with tests and who builds it. |

Claims about how things work today cite the code as `file:line`. Paths without a repository name are in this
repository. Anything not checked against code or a live game is marked GUESS.

## The problem

StationGod has two halves. The mod runs inside the game and answers requests on a named pipe (and, optionally,
over TCP). The sidecar is a small program each AI agent starts; it speaks MCP to the agent and passes each tool call
to the mod.

Over time the sidecar grew features the mod does not have. The two that matter most are `fields`, which keeps only
the named keys in a reply's lists, and `output_file`, which writes a large reply to disk and answers a short pointer.
Both live only in the sidecar (`src/StationGodMCP.Server/ReplyShaping.cs:22-49`). The sidecar strips them before the
request reaches the game (`ReplyShaping.cs:35-39`), so the mod still builds and serialises the whole reply, and the
sidecar throws most of it away afterwards. Argument checking is split the same way: the sidecar checks types, enums,
array sizes and required arguments (`src/StationGodMCP.Server/ArgumentCheck.cs:6-17`), while the mod checks only
that top-level argument names exist (`src/StationGodMCP.Mod/Api/Shared/DeclaredArguments.cs:16`, `:51-77`) and
otherwise stays lenient for pipe clients (`ArgumentCheck.cs:17`). One tool, `sample_logic`, exists only in the
sidecar, which builds it from repeated `read_logic_many` calls (`src/StationGodMCP.Server/Program.cs:187-189`,
`:332-455`); the mod has 90 methods (`src/StationGodMCP.Mod/Api/ApiHost.cs:26-120`), the sidecar 91 tools
(`Program.cs:583-678`).

The sidecar is also a private child process of one agent, talking MCP over its standard input and output
(`Program.cs:34-62`). Nothing else can connect to it. So everything that is not an agent went straight to the pipe:
the script dashboard, the watcher and flight-log scripts, the test-server client and the leak test. They get full
replies. Reading the health of 84 objects with `thing_health` returns every one of its 24 keys per object
(`src/StationGodMCP.Mod/Api/Views/ThingHealthViews.cs:196-249`), about 62.5 KB a call, when the caller wanted four.
Each of those clients also carries its own copy of rules that belong to the mod: the dashboard keeps a hand-written
list of read-only methods to decide what is safe to send twice (StationeersScriptDashboard
`stationscript/transport.py:23-35`), and that list already names `sample_logic`, which the mod does not have.

The owner asked for one proper interface rather than another patch on the side.

## The design in one paragraph

The mod becomes the one authority. It speaks one versioned protocol, the same on the pipe and over TCP, and every
method it offers is described in one machine-readable catalogue. The mod checks every request against that catalogue
and shapes every reply before it is written, for every caller. Each connection signs in to a permission level (read,
write or cheat) that the mod enforces. The mod shares its main thread fairly between a dashboard's small tick reads
and an agent's heavy survey, and it can push changed values to a client instead of being polled. The sidecar shrinks
to a translator between MCP and this protocol, with its tool list taken from the catalogue. Official client
libraries for Python and C#, generated from the same catalogue, handle connecting, reconnecting and the few things
that must happen on the caller's own machine, of which writing `output_file` is the main one.

## Who uses it, and what the design does for each

**Several agent sessions and subagents at once.** Today each sidecar opens a fresh pipe connection for every call
(`Program.cs:222-249`), and the mod serves at most four pipe connections at a time (`StationGodPipeServer.cs:23`).
In the new design each sidecar keeps one connection and sends several requests on it at once; the mod serves many
more connections, and its scheduler takes requests from each connection in turn so one busy agent cannot push
another to the back of the line.

**The script dashboard.** A long-running Python process that ticks its cards one after another on a single thread
(StationeersScriptDashboard `stationscript/runner.py:139`, `:240-262`), every 0.5 to 10 game seconds per card
(`scripts/*.py`, for example `print_queue.py:223`, `smelter.py:2068`), reading devices with one `read_devices` call
per gateway per tick (`runner.py:491-528`). It keeps one pipe connection (`transport.py:55-110`). In the new design
it uses the Python library, can ask the mod to push only the values that changed, gets its reads in a reserved slice
of each frame, and signs in at write level so it can never use a cheat tool by mistake.

**Scripts.** Watchers, the flight log, coolant and burn watchers and the layout solver import the dashboard's
`PipeTransport` (for example CheatEngineExpert `Cheats/Stationeers/tools/rocket/flight_log.py:10`,
`tools/coolant_watch.py:16`). The test-server client and the leak test have their own pipe client (StationeersTestServer
`tsclient.py:40-66`; TerraformingReloaded `tools/LeakTest/leaktest.py:67-68`, `:116-126`). LiveCheck is not a pipe
client: it is a test plugin inside the server that only measures, while the leak test drives StationGod
(TerraformingReloaded `tools/LiveCheck/Watch.cs:19`). In the new design all of these get the Python library, mostly
without edits, because the dashboard's `PipeTransport` and the test server's `Client` keep their names and become
thin wrappers over it.

**The headless test server beside the live game.** It already uses its own pipe name, `StationGodMCP-Test`
(StationeersTestServer `testserver.json`), and its client refuses any pipe that does not prove it is the test one
(`tsclient.py:90-101`). Nothing about that changes: the pipe name stays a setting, and the new sign-in reply names
the pipe and the world, so the same check works with less effort.

**Remote hosting.** The game runs on another machine while the owner plays on it as a client, and agents reach it
over a private network. The TCP listener exists today, with one shared secret sent in plain text
(`src/StationGodMCP.Mod/StationGodTcpServer.cs:174-229`; README "Safety"). In the new design TCP speaks exactly the
same protocol as the pipe, each client has its own key and level, and the key is never sent over the wire, only a
proof that the client holds it. The connection itself is still not encrypted, so the design expects a private network
or a VPN. One gap remains here; see *What this design does not solve* below.

**Workshop players.** Install the mod, unpack the sidecar, register it with the agent: the same three steps as
today (README "Install in short"). A local connection without a key gets the level the owner configures, and its
default keeps today's behaviour, so a fresh install works with no configuration.

**Large replies.** Shaping moves into the mod, so a caller that asks for four keys gets four keys, over the pipe or
TCP alike, and the game spends no time writing the rest. `output_file` stays on the caller's machine, because with a
remote game the file must land next to the agent, not next to the game. The sidecar also gains a safety net: a reply
larger than an agent can sensibly read goes to a file on its own.

**Cheat-level tools.** Today every client can do everything; the README calls the mod "a cheat-level tool" with "no
per-user permissions" (README "Safety"). In the new design every method has a class in the catalogue (read, write or
cheat; dry runs count as reads) and the mod refuses a method above the connection's level. Agents get cheat only when
the owner allows it.

**Multiplayer.** The mod listens only on the host (`src/StationGodMCP.Mod/StationGodMod.cs:100-104`;
`docs/install.md:141`), and that stays so. A player who is not the host reaches the mod through the host's TCP
listener, like any remote client.

## Decisions and why

These are the choices made inside the approved architecture. Each has its background and the reason.

### One protocol, two transports, negotiated at connect

Today the pipe and TCP differ only in TCP's first line, a shared-secret check (`StationGodTcpServer.cs:187-197`),
and both carry one request line and one reply line at a time (`StationGodPipeServer.cs:174-209`,
`StationGodTcpServer.cs:199-209`). The new protocol keeps newline-delimited JSON, which every client already
speaks, and adds a first message, `hello`, in which the client names the protocol versions it speaks. A connection
whose first line is an ordinary request is served exactly as today, so no existing client breaks; the mod tells the
two apart by that first line. A version-2 connection may have several requests in flight and receives pushed events,
so replies carry their request's id and may arrive out of order. Requests that change the world keep their order on
a connection. Details: [protocol.md](protocol.md).

### Shaping before serialising, and before building where it pays

The 1.10.0 performance work considered an envelope-level `shape` and cut it (the owner's notes in `CLAUDE.md`, State,
1.10.0). The approved architecture brings it back as part of the protocol. The mod applies shaping while it writes
the reply, so unwanted keys cost no serialisation and no bytes. Most replies are typed view classes whose values
are computed before serialising (`ThingHealthViews.cs:196-249` delegates to a `HealthView` built earlier), so the
game work is already done by then. Where a part of a reply is expensive to compute, such as the networks of every
structure in `thing_health` (`src/StationGodMCP.Mod/Api/ThingHealth.cs:294`), the handler asks whether the caller
wants it and skips it if not. Field selection keeps today's exact meaning for plain names and adds dotted paths for
nested keys. Details: [protocol.md](protocol.md), *Shaping*.

### The catalogue is a checked-in data file, and tests hold the code to it

Today the only machine-readable description of the tools is the sidecar's C# code, 2,661 lines of `Program.cs`
whose schemas are turned into `tool-arguments.json` by a test (`tests/StationGodMCP.Tests/ToolArgumentsFileTests.cs:16-28`).
The mod embeds that file to check argument names (`ApiHost.cs:190-206`). The catalogue replaces both. It is a set of
JSON files, one per method, assembled into one `catalogue.json` that the mod embeds and serves, the sidecar turns into
MCP tools, and the libraries turn into typed calls. JSON rather than C# attributes, because the handlers use game
types that the test project cannot compile (`tests/StationGodMCP.Tests/StationGodMCP.Tests.csproj:15-19` compiles only
`Pure`, `Api/Shared` and `Api/Views`), so a catalogue in code would be out of reach of the tests that matter. A test
suite holds the handlers to the catalogue from three sides: the method list, every argument name the handlers read,
and the reply views. Details: [catalogue.md](catalogue.md).

### Permission levels per connection, from keys the owner issues

Each connection gets a level when it signs in: read, write or cheat. A key in a small file next to the mod's config
names a client, its level, and any single methods it is allowed beyond its level. A local connection without a key
gets a configured level. Remote connections always need a key. Cheat is special: even a key whose level is cheat
uses cheat tools only while the owner has switched cheats on for it in the game, with a console command and a time
limit, unless the owner marked that key as always allowed.

An honest limit belongs here. On the owner's own machine an agent with a shell runs as the same Windows user as the
dashboard, can read any key file and can talk to the pipe directly. Levels therefore stop mistakes, not a determined
agent. The in-game switch is the one thing an agent cannot reach without already having cheat, because running a
console command is itself a cheat tool. Details: [protocol.md](protocol.md), *Sign-in and permissions*.

### Fair scheduling by lanes

The mod already limits how long one frame spends on requests: 4 ms by default, 2 ms while a building job holds the
game tick, and always at least one request per frame (`src/StationGodMCP.Mod/Pure/Runtime/FrameBudget.cs:13-37`;
`src/StationGodMCP.Mod/StationGodRequestDispatcher.cs:34-78`). Requests are served first in, first out, which was fair
only because each connection had one request in flight (owner notes `reference/PERFORMANCE-STRATEGY.md`, section 2.1,
not in the repository). With several requests per connection that is no longer true. The new scheduler sorts
requests into lanes by their cost class in the catalogue (refined by measured cost): subscription sampling, light
requests and heavy requests, each with its own share of the frame, and it takes requests from each connection in
turn. A heavy request still runs in one piece (the game's objects can be touched only on the main thread), so it can
still make one long frame; the scheduler makes sure such frames are spaced out and never stall the dashboard's
reads. Details: [scheduling.md](scheduling.md).

### Subscriptions push changes

A client subscribes to a set of device reads in the same form `read_devices` takes, at an interval in game seconds.
The mod reads them all in one frame, as `read_devices` does, compares with what it last sent, and pushes only what
changed, with a sequence number. Changes waiting for a slow client are merged, newest value wins. Subscriptions belong
to their connection; the libraries renew them after a reconnect and tell the caller when the world changed under them.
`sample_logic` moves into the mod on top of this, so the sidecar holds no game logic. Details:
[protocol.md](protocol.md), *Subscriptions*, and [scheduling.md](scheduling.md).

### The sidecar takes its tool list from the mod, with a built-in copy

An agent session usually starts before the game. The sidecar therefore carries the catalogue it was built with and
answers `tools/list` from it at once. When it connects and the mod's catalogue differs, it switches to the mod's and
tells the agent that the tool list changed. Whether every MCP client acts on that notice is GUESS; a client that does
not keeps the built-in list, and the mod still checks every call, so a stale list fails safely with a clear error.

### Client libraries live in this repository

The Python library lives under `clients/python` here and the C# client is a project next to the sidecar, both
generated in part from the catalogue and released with the mod. They own connections, reconnects, resending only
reads after a broken connection (the dashboard's rule today, `transport.py:84-110`, now driven by the catalogue
instead of a hand-kept list), output files, paging and subscriptions. The dashboard and the scripts depend on the
Python library rather than each carrying a pipe client.

## What changes for each kind of client

**Agents through the sidecar.** Nothing to do. Tool names and arguments stay the same; `fields` and `output_file`
work as before, `fields` now also over a remote game without shipping the unwanted keys. A tool the connection's level
does not allow answers `permission_denied` with the level it needs. Very large replies arrive as a file pointer even
without `output_file`.

**The dashboard.** Its transport becomes the Python library. Card reads can move from polling to subscriptions,
card by card, with polling kept as the fallback. It gets a write-level key. Cards that use tools the owner classes as
cheat need a decision (see *Questions for the owner*).

**Scripts using the dashboard's `PipeTransport`.** No edits: `PipeTransport`, `GameError` and `GameUnreachable`
keep their names and behaviour. A script that wants shaping passes `fields`.

**Test-server tools.** `tsclient.Client` keeps its interface and its refusal to touch anything but the test pipe. The
test server gets its own key file in its own config folder.

**Workshop players.** Nothing to do. Players who want to keep agents off cheat tools set the local level to write.

**Anything written against today's pipe protocol.** Keeps working: a connection that does not say `hello` is served as
today, until the owner decides to switch the old protocol off.

## What this design does not solve

Playing as a client on a remote host. Tools that depend on "the player" or "the camera" use the host's own local
player and camera: `looking_at` and every `at {crosshair}` (`src/StationGodMCP.Mod/Api/LookingAt.cs:146-186`, which
notes a dedicated server has no camera), distances from the player
(`src/StationGodMCP.Mod/Api/Shared/Game/PlayerOrigin.cs:9`), refunds into the player's inventory
(`src/StationGodMCP.Mod/Api/Shared/Game/Build/PlacePlanner.cs:1083`), and `highlight` and `show_preview`, which only
the game running the mod draws (`src/StationGodMCP.Mod/Api/Shared/Game/Highlights.cs:15`). When the owner plays as a
client of a dedicated server, these find no player or draw where nobody looks. Fixing that needs a player argument
for the player-based tools and a client-side part for drawing; it is outside this design.

The open internet. TCP stays unencrypted; the design relies on a private network or VPN.

A single heavy request still costs one long frame. The scheduler spreads them out but cannot split them.

Agents do not receive pushes. MCP tool calls are request and reply; agents use `sample_logic` and polling, scripts
and the dashboard use subscriptions.

## Questions for the owner

These change what a player or the dashboard can do, so they are the owner's to decide. Each has a recommendation.

**Which tools count as cheat.** The catalogue needs a class for every method. Some are clear: `run_console_command`
and `move_gas` (its own description says it bypasses the game's physics) are cheat; logic writes, chip programming
and building tools that charge materials as a player would are write. Others are not clear, and the dashboard uses
several of them (StationeersScriptDashboard `scripts/locker_sort.py:577-944`, `scripts/ore_to_vault.py:35`,
`scripts/smelter.py:1850`, `:2639`, `scripts/traders.py:388-665`): `move_item` (moves an item between any two slots in
the world, as a click would but without walking there), `vault_deposit` and `vault_withdraw` (skip the vault's import
slot and vend queue), `trader_buy` and `trader_sell`, `plant_genes` when it edits, `paste_blueprint` (BlueprintMod
places pieces without the game's placement checks), `place_structure` with `free: true`, and `write_memory`.
The recommendation is in [catalogue.md](catalogue.md), *Method classes*: `move_item`, the vault tools, the trader
tools and `write_memory` as write, because they use the game's own moves and bookkeeping; edits by `plant_genes`,
`paste_blueprint`, `place_structure free` and the two above as cheat. If the owner classes any tool the dashboard uses
as cheat, the dashboard either loses that card's action or gets that one method granted, which bends "never cheat".

**What a local connection without a key may do.** Keeping today's behaviour means cheat, so a Workshop install keeps
working as it does now. Choosing write keeps agents off cheat tools by default, but existing players lose `move_gas`
and the console tool until they change a setting. Recommended: cheat by default for Workshop players, and write on the
owner's machine through the setting.

**How the owner says yes to cheats.** Either a standing key with cheat level (simple, but every agent started with it
can cheat at any time), or the in-game switch with a time limit (one console command each time, and an agent can never
switch it on itself). Recommended: the in-game switch for agents, a standing key only for the owner's own scripts.

**When to switch the old protocol off.** The old line-per-request protocol keeps every existing client alive during
the move. Once the dashboard, scripts and test tools are on the library, it can be refused. Recommended: keep it until
every client in the owner's repositories has moved, then refuse it on the owner's machine first and in the Workshop
release one version later.
