# The plan, stage by stage

[Back to the overview](README.md)

Thirteen stages, each small enough to build, test and ship on its own, each leaving every existing client working.
They are ordered by what they give for what they risk: shaping first, because it helps every client at once and
touches one well-tested path; then the catalogue, which everything later reads; then the new protocol, permissions,
the libraries, the moves, and last the parts that only pay once the rest is in place.

| Stage | What | Builds on | Implemented by |
| --- | --- | --- | --- |
| 0 | Baseline: measure today's costs | none | general agent |
| 1 | Shaping in the mod, on today's protocol | 0 | zoran-dotnet-developer, general agent for the check script |
| 2 | The catalogue | 1 | zoran-dotnet-developer |
| 3 | Protocol version 2 on the pipe | 2 | zoran-dotnet-developer, general agent for the probe |
| 4 | Sign-in, permissions, and version 2 over TCP | 3 | zoran-dotnet-developer |
| 5 | The Python library | 3 (4 for keys) | general agent |
| 6 | Existing Python clients onto the library | 5 | general agent |
| 7 | The sidecar as a thin adapter on the C# client | 4 | zoran-dotnet-developer |
| 8 | Fair scheduling by lanes | 3 | zoran-dotnet-developer, general agent for the load script |
| 9 | Subscriptions, and `sample_logic` in the mod | 8 | zoran-dotnet-developer (mod, C# client), general agent (Python) |
| 10 | Dashboard reads by subscription | 6, 9 | general agent |
| 11 | Skipping costly parts of replies | 1, 2 | zoran-dotnet-developer |
| 12 | Switching the old protocol off | 6, 7, 10, owner's decision | zoran-dotnet-developer |

Stages 5 and 8 can run beside 4 and 7; nothing else runs in parallel on the mod, because each mod stage changes the
request path the next one builds on.

## Rules for every stage

- **Code style.** C# follows the owner's rules in the zoran-dotnet-developer agent, cited by rule slug in its report.
  In the mod (netstandard2.1): no records, which would need a shim, so sealed classes and readonly structs; no LINQ on
  per-call or per-frame paths or over world-sized collections. Pure logic goes in `src/StationGodMCP.Mod/Pure/` with
  unit tests, so the test project can compile it (`tests/StationGodMCP.Tests/StationGodMCP.Tests.csproj:15-19`).
  The sidecar and the C# client (net8) may use records, as the sidecar does today.
- **Briefs.** Each implementer gets a self-contained brief: this folder, the stage's section, the owner's notes
  (`CLAUDE.md`), and for C# the stationeers-mod skill's `implementation-brief.md`.
- **Tests.** The full suite stays green (1,711 tests at 1.10.0, owner notes `CLAUDE.md`, State). Every changed wire shape
  gets a wire test. A stage is not done while any of its acceptance checks is unrun; an unrun live check goes into the
  owner's `TODO.md` with its exact steps.
- **Versions.** Every stage that changes the mod or the sidecar bumps the minor version in the five places `build.ps1`
  checks, adds a CHANGELOG section and updates `About/About.xml`'s change log. Before deciding what that change log
  holds, ask the owner whether the last version was published to Steam (the owner's standing rule).
- **Docs.** Player docs (README, `docs/*.md`) change in the same commit as the behaviour they describe and never say
  what is or is not tested in game. This folder is updated when a stage changes a decision.
- **Deploying.** To the owner's game only through the deploy queue (`deploy-queue.ps1 add StationGodMCP -Note ...`),
  because the game is usually running.
- **The test server.** Only while the owner's game is closed and at least 9 GB of memory is free
  (StationeersTestServer `testserver.json`, `min_free_gb`); a watchdog stops it when the game starts. Testers only test
  and report; they write nothing to any repository. The world comes up paused: unpause with `python mcp.py console
  pause false`, as `mcp.py wait` does.

Every live check below starts the same way, from a PowerShell prompt:

```powershell
cd <StationGodMCP repository>
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
cd <StationeersTestServer>
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\start-server.ps1 -Wait
python mcp.py wait          # prints mod_version, pipe_name StationGodMCP-Test, missing_count, clock
```

and ends with `powershell -NoProfile -File .\stop-server.ps1`. "The fixtures" means the test world with the save and
blueprint the 1.10.0 equivalence run used (save `fixround`, blueprint `compact-bathroom` pasted; owner notes
`CLAUDE.md`, State, 1.10.0).

## Stage 0: baseline

**Scope.** A read-only script, `tools/protocol_baseline.py`, that talks version 1 to a pipe named on the command line
and, for a fixed set of calls, records reply bytes, round-trip time and the mod's `elapsed_ms`, then saves
`mod_info.runtime`. The set: `thing_health` by 84 ids, `thing_health` scan with `limit` 500, `list_devices` for the
world, `find_things` with `kind: structure`, `grid_survey` of one room, `read_devices` of 20 items with logic and one
atmosphere each, `game_clock`; each 20 times. Output: a JSON report in `tools/baseline/` (git-ignored).

**Files.** `tools/protocol_baseline.py` (new), `.gitignore`.

**Acceptance.**
- Offline: the script's `--self-test` runs its report builder on canned replies and checks every field is present.
- Live, test server: after the common start, `python tools/protocol_baseline.py --pipe StationGodMCP-Test --out
  tools/baseline/test-1.10.0.json`; the report has every method with 20 samples, `bytes` per reply, and a
  `mod_info.runtime` block whose `methods` list each called method with `handler_ms`, `serialize_ms`, `queue_wait_ms`
  and `reply_bytes`.
- Optional, owner's game, read-only, only with the owner's OK: the same with `--pipe StationGodMCP` while the dashboard
  runs. This also closes the 1.9.1 live check on `mod_info.runtime` (owner notes, `TODO.md`).

**Risk.** None to the game: reads only.

## Stage 1: shaping in the mod

**Scope.** The mod honours an optional `shape` key in the request envelope, on today's protocol: `fields` (single names
and dotted paths), `limit` and `max_bytes`, exactly as [protocol.md](protocol.md), *Shaping*, defines them, plus
`fields_unmatched`, `shape_truncated`, and the errors `invalid_shape` and `reply_too_large`. Shaping happens in a
filtering JSON writer wrapped round the existing serialiser, so a reply without `shape` is written byte for byte as
today. The sidecar sends `fields` to the mod as `shape.fields` and still applies its own `FieldSelection` afterwards,
which gives the same result on a new mod and keeps an old mod working, because an old mod reads only `id`, `method` and
`params` (`src/StationGodMCP.Mod/Api/ApiHost.cs:156-165`). With `output_file` and no `fields`, nothing is sent.
Version 1.11.0.

**Files.**
- New, pure: `src/StationGodMCP.Mod/Pure/Shaping/` (`ShapeRequest` parsed into a closed set of selector classes,
  `FieldSelectors` with the grammar, `ShapingJsonWriter`, `ShapeOutcome`).
- `src/StationGodMCP.Mod/Api/ApiHost.cs` (read `shape` beside `params`; pass it to serialising; error replies never
  shaped), `src/StationGodMCP.Mod/Api/Shared/ApiJson.cs` (a shaped `WriteShared`).
- `src/StationGodMCP.Server/Program.cs`, `ReplyShaping.cs` (forward `shape.fields`).
- Docs: README *Large replies*, `docs/devices-and-logic.md` (the pipe envelope), CHANGELOG.

**Acceptance, offline.**
- Same results as the sidecar: every fixture in `tests/StationGodMCP.Tests/FileOutputTests.cs` and
  `ReplySlimmingTests.cs` shaped by the mod's writer equals, byte for byte, the sidecar's `FieldSelection.Apply` on the
  same reply.
- No change without `shape`: every view the wire tests serialise gives byte-identical output through the new path.
- Paths: nested keys, a path through a list inside an entry, two selectors on one key (union), a single name beside a
  path on the same list, `fields_unmatched` for a path no entry has, none when the reply has no list entries.
- `limit` cuts and reports `shape_truncated`; `max_bytes` gives `reply_too_large` with `counts`; malformed `shape`
  (unknown key, bad selector, `limit` on a key that is not a list) gives `invalid_shape`.
- The sidecar forwards `shape.fields` only when `fields` is given.

**Acceptance, live (test server).** A small script, `tools/shape_check.py`, sends version-1 lines with and without
`shape`.
1. `python tools/shape_check.py --pipe StationGodMCP-Test --method thing_health --ids-from "find_things kind=structure
   limit=84" --fields reference_id,damage_ratio,is_broken,condition`: the shaped reply equals the unshaped reply
   projected in Python by the same rules, and is at most a quarter of its bytes.
2. The same for `list_devices` with `fields: [reference_id, prefab_name, display_name]`, and for a path selector on
   `find_things` (`things.position.x`).
3. `mod_info.runtime.methods` for `thing_health`: `serialize_ms` mean of the shaped calls lower than the unshaped ones.
4. Through the sidecar: `python livetest\lt.py thing_health "{\"reference_ids\": [...], \"fields\":
   [\"reference_id\", \"damage_ratio\"]}"` answers as before the stage.

**Risk.** Low to medium: every reply passes through the new writer. Guarded by the byte-identical test over all wire
views.

## Stage 2: the catalogue

**Scope.** Everything in [catalogue.md](catalogue.md) except what later stages use it for: the `catalogue/` sources,
bootstrapped once from the sidecar's tool definitions by a one-shot test (`STATIONGOD_BOOTSTRAP_CATALOGUE=1`), then
completed by hand with classes, costs, ranges, reply schemas and view names; the assembled `catalogue.json`; the
consistency tests 1 to 7; the run-time drift counter in `mod_info`. The mod embeds `catalogue.json` and takes its
argument-name check from it (same behaviour as `tool-arguments.json` today). The sidecar builds `tools/list` from its
embedded copy. `tool-arguments.json`, `ToolArguments.cs` and `ToolArgumentsFileTests.cs` go; so does `ToolDefinitions`
once the byte-identical `tools/list` test passes. Version 1.12.0.

**Files.** `catalogue/**` (new), `catalogue.json` (new, root), `StationGodMCP.csproj` (embed it),
`src/StationGodMCP.Mod/Pure/Catalogue/` (loader, schema-subset validator, class and cost rules),
`src/StationGodMCP.Mod/Api/ApiHost.cs`, `Api/Shared/DeclaredArguments.cs`, `Api/Shared/Args.cs` (record names read),
`Api/ModInfo.cs`, `Api/Views/RuntimeViews.cs`, `src/StationGodMCP.Server/Program.cs`, `StationGodMCP.Server.csproj`,
`build.ps1` (version check), `tests/StationGodMCP.Tests/Catalogue*Tests.cs` (new).

**Acceptance, offline.** The seven consistency tests in [catalogue.md](catalogue.md) pass; the sidecar's `tools/list`
from the catalogue equals the one from `ToolDefinitions` byte for byte; the schema-subset validator accepts and refuses
the cases the sidecar's `ArgumentCheck` tests cover today (unknown names with the nearest one, wrong types, enum case
folding, `3.0` and `1e2` as integers, nulls as omitted, repeated keys); a catalogue using an unsupported keyword
fails to load with a clear message.

**Acceptance, live (test server).**
1. `python livetest\lt.py list`: 91 tool names, the same as before the stage (save the list before upgrading).
2. `python livetest\lt.py schema place_cables` equals the saved schema from before the stage.
3. `python tools\read_devices_equivalence.py` (the 1.10.0 equivalence script) passes as it did.
4. `python mcp.py find_things "{\"prefab\": \"x\"}"` is refused `invalid_argument` naming `prefab_contains`.
5. After 1 to 4, `python mcp.py mod_info` shows `runtime.catalogue_drift` empty.

**Risk.** Medium: a large mechanical move of descriptions and schemas. The byte-identical `tools/list` test and the
handler-argument test make a slip visible.

## Stage 3: protocol version 2 on the pipe

**Scope.** The connection layer of [protocol.md](protocol.md) for the pipe: telling version 1 from version 2 by the first
line; `hello` and `welcome` (anonymous only; every version-2 pipe connection gets today's level); `call`, `reply` with
`type`, `queue_ms`; up to 16 calls in flight per connection; deadlines and `cancel`; the per-connection ordering rule;
taking calls from each connection in turn (a simple round-robin in the dispatcher; lanes come in stage 8); strict
argument checking from the catalogue for version-2 calls; the `catalogue` protocol method; `ping`, `world_changed` and
`goodbye`; the outbound queue and slow-client rule; `[Server] MaxConnections` (32) pipe instances; request size limit;
`mod_info` connections. Version-1 connections keep today's code path. A setting `[Server] Protocol2` (default true)
turns version 2 off as a fallback. Version 1.13.0.

**Files.** `src/StationGodMCP.Mod/StationGodPipeServer.cs`, `StationGodRequestDispatcher.cs`, `StationGodMod.cs`
(settings), new `src/StationGodMCP.Mod/Protocol/` (connections, outbound queues, hello, events) and
`src/StationGodMCP.Mod/Pure/Protocol/` (message parsing, first-line classifier, call ordering, validator use),
`Api/ApiHost.cs`, `Api/ModInfo.cs`, views; `tools/protocol_probe.py` (new, general agent): a raw version-2 client for
the live checks; docs: `docs/configuration.md`, CHANGELOG.

**Acceptance, offline.**
- First-line classifier: `hello` starts version 2; a request object, a non-JSON line and an empty object start version 1
  (answered as today).
- Version-1 golden transcripts: recorded request and reply lines from today's mod replay byte for byte through the new
  pipe code with a fake dispatcher.
- Call ordering: with a fake clock and fake handlers, a read after a write on one connection starts after it; two reads
  may swap; a write waits for every earlier call.
- Validator for version-2 calls: nested unknown arguments refused with a path; ranges from the catalogue; integers
  written `3.0`; null as omitted.
- Framing: CR LF accepted; blank lines ignored; a 5 MiB line gives `request_too_large` and closes.
- Outbound queue: past 8 MiB or 35 seconds, `goodbye slow_client`; replies never dropped before that.

**Acceptance, live (test server).**
1. Version 1 unchanged: `python mcp.py mod_info`, `python validate.py` and `python replaceable.py` in StationeersTestServer
   run as before; `python livetest\lt.py game_clock "{}"` answers.
2. `python tools\protocol_probe.py --pipe StationGodMCP-Test hello`: `welcome` with `protocol` 2, `server.pipe_name`
   `StationGodMCP-Test`, `limits.max_in_flight` 16, `features` including `shape` and `cancel`.
3. `... burst --method game_clock --count 16`: 16 replies on one connection, each id answered once; a 17th in flight
   gets `too_many_in_flight`.
4. `... order --device <a fixture device with a writable Setting>`: sends `write_logic` Setting 7 and then `read_logic`
   Setting without waiting; the read answers 7. Restores the old value after.
5. `... cancel`: sends a `grid_survey` of the fixture area and a `game_clock`, cancels the second at once; the
   cancelled call answers `cancelled` or, if it had started, its result.
6. `... strict --method connections --params "{\"reference_id\": \"1\", \"limit\": 99999}"`: `invalid_argument` with
   the path `limit` and the range; the same params over version 1 behave as today.
7. `... connections --count 33`: 32 connections get `welcome`; the 33rd waits for a free instance and is served once
   one closes.

**Risk.** High: threads and I/O in the game process. Version 1 keeps its own path, `[Server] Protocol2 = false` turns
the new one off, and every pipe and listener failure is caught per connection as today
(`src/StationGodMCP.Mod/StationGodPipeServer.cs:97-140`).

## Stage 4: sign-in, permissions, and version 2 over TCP

**Scope.** The *Sign-in and permissions* part of [protocol.md](protocol.md): the clients file and its reload; the
challenge and HMAC proof; levels, class rules from the catalogue, grants; armed and standing cheat; the console commands
(`stationgod allow`, `deny`, `clients`, `key new`) through the game's `CommandLine.AddCommand`; `[Access]
AnonymousPipeLevel`, `LegacyPipeLevel`, `LegacyTcpLevel` (all default `cheat`); version 2 over TCP, keys required;
failure throttling; `goodbye revoked`; `cheat_armed` and `cheat_disarmed` events. Permissions also apply to version-1
calls at their configured level, which with the defaults changes nothing. Version 1.14.0.

**Files.** `src/StationGodMCP.Mod/StationGodTcpServer.cs`, `StationGodMod.cs`, `src/StationGodMCP.Mod/Protocol/`
(sign-in), `src/StationGodMCP.Mod/Pure/Access/` (clients file, levels, permission decision, HMAC proof, arming with an
injected clock), new `src/StationGodMCP.Mod/Game/StationGodCommands.cs`, `GameMembers.cs` (the console member, checked at
load like every other); docs: README *Safety*, `docs/configuration.md` (*Access*, the clients file), `docs/install.md`
(remote access with keys), CHANGELOG.

**Acceptance, offline.**
- The permission decision for every combination of level, class, grant, armed or standing, and the class rules:
  `place_cables` with no `dry_run` is read, with `dry_run: false` write; `place_structure` with `free: true` and
  `dry_run: false` cheat; `move_gas` with `dry_run: true` read; `plant_genes` without `genes` read.
- HMAC proof vectors: a fixed key, nonce, client and transport give a fixed hex string (shared with the Python tests).
- The clients file: a short key, an unknown level, a duplicate name and a bad transport each disable only that client,
  logged.
- Arming expires on time; disarming sends the event; with every default, a version-1 client can still call
  `run_console_command`.

**Acceptance, live (test server).** The test server's own config folder holds its clients file; restore it afterwards.
1. `python mcp.py console "stationgod key new probe-read read"` and the same for `probe-write write` and `probe-cheat
   cheat`; the keys appear in the server log.
2. `python tools\protocol_probe.py --pipe StationGodMCP-Test --client probe-read --key <key> call read_logic ...`
   succeeds; `write_logic` gives `permission_denied` with `required` write; `place_cables` without `dry_run` succeeds;
   with `dry_run: false, confirm: true` gives `permission_denied`.
3. With `probe-write`: `write_logic` succeeds (restore the value); `move_gas` with `dry_run: false` gives
   `permission_denied` with `required` cheat.
4. With `probe-cheat`: `move_gas` with `dry_run: true` succeeds; a real `move_gas` of 1 mol between two fixture canisters
   gives `cheat_not_armed`; `python mcp.py console "stationgod allow probe-cheat 1"`; the probe receives `cheat_armed`;
   the real `move_gas` succeeds; after a minute the probe receives `cheat_disarmed` and the next real `move_gas` gives
   `cheat_not_armed`. Move the gas back with the armed key or note the change.
5. TCP: set `[Remote MCP] Enabled = true`, `BindAddress = 127.0.0.1`, `Port = 18765`, `Secret = <long random>` in the test
   server's config, restart the server; the probe over TCP with `probe-read` gets `welcome` with `transport` tcp; a wrong
   key gets `unauthorized` and three wrong keys in a minute delay the next connection by 10 seconds; today's sidecar with
   `--host 127.0.0.1 --port 18765` and `STATIONGODMCP_SECRET` still answers `mod_info` (version-1 sign-in). Restore
   the config.

**Risk.** Medium: a mistake could lock clients out. Every default keeps today's behaviour, and the clients file is
read-only to the mod except through `key new`.

## Stage 5: the Python library

**Scope.** `clients/python/stationgod` as [clients.md](clients.md) describes it, with the generator for `methods.py`, the
shared fixtures in `clients/fixtures`, `build.ps1` copying `catalogue.json` into the package and `release.ps1` adding a
zip of it to the release assets. No mod change.

**Files.** `clients/python/**`, `clients/fixtures/**`, `build.ps1`, `release.ps1`, and the C# `FileOutputTests` reading
the shared fixtures.

**Acceptance, offline** (`py -m pytest clients/python/tests`, no game).
- A fake server in the tests speaks version 1 and version 2 over loopback TCP and a real named pipe.
- Resending: an unwritten call resent; a written read resent once; a written write raises `Unreachable(maybe_ran=True)`;
  a `place_cables` dry run counts as a read, a confirmed run does not.
- Version-1 fallback: the old mod's answer to `hello` switches to version 1 on the same connection; `fields` then
  applied locally with results equal to the sidecar's on the shared fixtures.
- Output files: every fixture in `clients/fixtures/output_file`; pruning keeps 200 and the newest; a failed write
  answers inline with `output_file_error`.
- HMAC vectors equal the mod's.
- `iterate` walks pages by `x-paging` until `has_more` is false.
- The generated `methods.py` is current (the generator run in check mode).

**Acceptance, live (test server).** `py -m pytest clients/python/tests -m live --pipe StationGodMCP-Test`:
connect and `welcome`; `thing_health` by ids with `fields` equals the projection of the full reply; `iterate("find_things",
kind="structure", page_size=100)` gives the same ids as one call with a large `limit`; `output_file=True` writes a
readable file and a correct pointer; restarting the server during a burst of reads (stop-server, start-server -Wait)
resumes the reads after `mcp.py wait`, and a write in flight at that moment raises `Unreachable` with `maybe_ran`.

**Risk.** Low: nothing uses it yet.

## Stage 6: existing Python clients onto the library

**Scope.** The dashboard's transport becomes a wrapper over the library (dashboard steps 1, 2, 3 and 6 in
[clients.md](clients.md)); `fields` in the cards that read large replies (step 5), each card in a copy moved in whole;
StationeersTestServer `tsclient.py` rewritten over the library with its guard kept. The CheatEngineExpert scripts need no
edit. No mod change.

**Files.** StationeersScriptDashboard `stationscript/transport.py`, `stationscript/__main__.py`,
`stationscript/metrics.py`, `tests/test_transport.py`, the listed cards; StationeersTestServer `tsclient.py`.

**Acceptance, offline.** The dashboard's test suite (`py -m pytest tests` in StationeersScriptDashboard) green, with
`test_transport.py` now testing the wrapper against the library's fake server: same errors, same meter calls, no
`READ_ONLY` list. Each card edited for `fields` keeps its own tests green.

**Acceptance, live.**
1. Test server: `python mcp.py wait`, then StationeersTestServer `validate.py` and `replaceable.py` run as before on
   the new `tsclient`; a `tsclient.Client` built for the default pipe name still refuses to start.
2. A CheatEngineExpert script against the test server: `python Cheats\Stationeers\tools\rocket\flight_log.py` with the
   pipe pointed at `StationGodMCP-Test` (one-off environment override) runs for 10 seconds and logs readings.
3. The owner's game, after the owner agrees and the dashboard is restarted: the dashboard runs with `--dry-run` (it
   logs writes and sends none) for 10 minutes; every card's status matches the status before the change; `/api/state`
   shows reply KB a minute lower than stage 0's figures for the cards given `fields`. Then normal mode.

**Risk.** Medium for the owner's daily tool. The wrapper keeps every name; the dry run catches regressions before any
write.

## Stage 7: the sidecar as a thin adapter

**Scope.** `src/StationGodMCP.Client` (net8) per [clients.md](clients.md); `ReplyShaping`, `FieldSelection` and
`OutputFolder` move there; the sidecar keeps MCP and uses one persistent version-2 connection; `tools/list` from the
catalogue with `listChanged: true` and `notifications/tools/list_changed` on a catalogue change; argument checking left
to the mod; the inline size limit; `--client`, `--key-env`, `--inline-limit-kb`. `ArgumentCheck` goes once the mod's
version-2 checking covers its tests. Version 1.15.0.

**Files.** `src/StationGodMCP.Client/**` (new), `src/StationGodMCP.Server/*`, `StationGodMCP.sln`,
`tests/StationGodMCP.Tests` (MCP transcript tests against an in-process fake mod), docs: `docs/configuration.md`
(*Sidecar options*), README *Large replies*, CHANGELOG.

**Acceptance, offline.**
- MCP transcripts: `initialize` advertises `listChanged: true`; `tools/list` equals the catalogue-derived list;
  `tools/call` with a bad argument returns the fake mod's `invalid_argument` in today's result shape; `fields` arrives as
  `shape.fields`; `output_file` writes per the shared fixtures; a 300 KB reply without `output_file` becomes a pointer with
  `auto_output_file: true`.
- Against a fake version-1 mod: calls work, `fields` applied locally, `sample_logic` still answered by the old loop
  until stage 9.
- Five concurrent `tools/call` messages are answered over one connection.

**Acceptance, live (test server).**
1. Before upgrading, record `python livetest\lt.py <tool> <args>` for ten representative calls (`mod_info`, `list_devices`,
   `find_things`, `thing_health` with `fields`, `connections`, `grid_survey`, a `place_cables` dry run, `read_devices`,
   `get_ic_status`, `game_clock`); after upgrading, the same calls give the same results apart from times.
2. A script that writes five `tools/call` lines to the sidecar without waiting gets five answers; `mod_info` shows one
   connection for the sidecar.
3. The sidecar started with `--host 127.0.0.1 --port 18765 --client probe-read --key-env STATIONGOD_KEY` against the
   test server's TCP (stage 4 config) answers `mod_info`.

**Risk.** Medium: every agent call goes through it. The recorded comparison and the version-1 fallback limit the damage;
a previous sidecar build can be re-registered in a minute.

## Stage 8: fair scheduling by lanes

**Scope.** [scheduling.md](scheduling.md): the pure frame scheduler with the three lanes, round-robin across connections,
the heavy-lane waiting bound, per-method moving averages, the new `[Performance]` settings, and `mod_info.runtime.lanes`.
Version 1.16.0.

**Files.** `src/StationGodMCP.Mod/Pure/Scheduling/` (new), `StationGodRequestDispatcher.cs`, `StationGodMod.cs`,
`Pure/Runtime/MethodTimings.cs`, `Api/Views/RuntimeViews.cs`, `docs/configuration.md`, CHANGELOG;
`tools/lane_check.py` (new, general agent).

**Acceptance, offline.** The scheduler tests listed in [scheduling.md](scheduling.md), *How it is tested*.

**Acceptance, live (test server).** `python tools\lane_check.py --pipe StationGodMCP-Test --seconds 120`: connection A
sends a 20-item `read_devices` every 0.25 s; connection B keeps two `grid_survey` calls of the whole fixture area and
one unfiltered `find_things` in flight at all times. Run once on the stage-7 build and once on this one. Pass when, on
this build, A's `queue_ms` 95th percentile is at most two frames' worth (frame time from `mod_info.runtime.frames`) and
lower than on the stage-7 build; `mod_info.runtime.lanes.heavy` shows no frame with two heavy calls and a longest wait
of at most 10 frames; B's calls all complete.

**Risk.** Medium: a scheduling mistake shows as latency or a stuck lane, not as a crash. The waiting bound and the
"first call always runs" rule keep every lane moving.

## Stage 9: subscriptions, and sample_logic in the mod

**Scope.** [protocol.md](protocol.md), *Subscriptions*: `subscribe` and `unsubscribe`, topics `devices`, `world` and
`job`, the sampling lane and admission from [scheduling.md](scheduling.md), changes, tolerance, merging, resync,
`subscription_ended`. `sample_logic` becomes a mod method with today's arguments and reply (`src/StationGodMCP.Server/Program.cs:332-455`),
built on an internal subscription; the sidecar's loop goes. Subscription support in the C# client and the Python
library, including polling emulation on a version-1 server. Version 1.17.0.

**Files.** `src/StationGodMCP.Mod/Pure/Subscriptions/` (state, comparison, merging, admission; new),
`src/StationGodMCP.Mod/Protocol/` (subscription registry, sampling), `Api/ReadDevices.cs` (the shared read path),
`catalogue/methods/sample_logic.json`, `subscribe.json`, `unsubscribe.json`, `src/StationGodMCP.Client/`,
`src/StationGodMCP.Server/Program.cs`, `clients/python/stationgod/subscriptions.py`, docs: `docs/devices-and-logic.md`,
CHANGELOG.

**Acceptance, offline.**
- Changes: a sequence of readings gives the expected `changes`; tolerance holds back small moves; an item that starts
  failing appears with `ok: false`; applying every event to the snapshot gives the last reading.
- Merging and resync: with the outbound queue held, events merge without new `seq`; past half the queue limit, one
  `resync` with the next `seq`.
- Admission: the 33rd subscription and the 1,025th value are refused; the projected-load limit refuses with the
  projection.
- `sample_logic`: the sidecar's present results for recorded sequences (count, change list, elapsed seconds rounding)
  are reproduced by the mod's version.
- Python and C#: the mirror equals the server's state after random changes, gaps and resyncs; a reconnect resubscribes
  and reports a fresh start.

**Acceptance, live (test server).**
1. `python tools\protocol_probe.py ... subscribe --items <10 fixture devices: On, Setting> --interval 1`: a snapshot,
   then no `update` while nothing changes.
2. `python mcp.py write_logic "{...On 0...}"` on one of them: one `update` within two seconds holding only that
   device's `On`. Restore the value.
3. `python mcp.py console "pause true"`: no updates while paused (game clock); `pause false` resumes them.
4. The probe stops reading its pipe for 60 seconds while a fixture value is toggled every second: afterwards it
   receives merged updates or one `resync`, and `seq` has no unexplained gap.
5. `python livetest\lt.py sample_logic "{\"targets\": [...], \"duration_seconds\": 5}"` while a value is toggled:
   the reply shape and `change_count` match the same run on the stage-8 build.
6. `mod_info.runtime.subscriptions` shows the subscriptions and a mean sampling time per frame.

**Risk.** Medium: new main-thread work. Admission limits its cost, and `SubscriptionBudgetMs = 0` turns it off.

## Stage 10: dashboard reads by subscription

**Scope.** Dashboard step 4 in [clients.md](clients.md): the runner subscribes per card and gateway in `snapshot` mode
and reads `sub.state` at each tick, falling back to `read_devices` without the feature. Cards unchanged. No mod change.

**Files.** StationeersScriptDashboard `stationscript/runner.py`, `stationscript/station.py`, `tests/test_runner.py`.

**Acceptance, offline.** `test_runner.py`: with a fake library, a card's handles get the same values from a
subscription as from `read_devices`; a rebind closes and reopens its subscriptions; without the feature the runner
calls `read_devices` as before.

**Acceptance, live (owner's game, with the owner's OK).** The dashboard restarted with `--dry-run` for 10 minutes: every
card's status matches a status snapshot taken before; `/api/state` shows game calls a minute at most half of stage 6's
figure; `mod_info.runtime.subscriptions` shows one subscription per bound card and gateway. Then normal mode for 30
minutes with the same statuses.

**Risk.** Medium for the owner's daily tool; the dry run and the `read_devices` fallback contain it.

## Stage 11: skipping costly parts of replies

**Scope.** `args.Shape.Wants(list, key)` for handlers, and `x-costly` entries for the parts stage 0 measured as costly,
starting with `thing_health`'s `networks` (`src/StationGodMCP.Mod/Api/ThingHealth.cs:294`). Each method is its own small
change. Version 1.18.0 for the first, patch versions after.

**Files.** `src/StationGodMCP.Mod/Api/Shared/Args.cs`, the chosen handlers, their catalogue entries.

**Acceptance.** Offline: for each changed method, the views give the same values for every kept key whether the costly
part was skipped or not (view-level tests). Live (test server): `tools\shape_check.py` for the method, with and without
the costly key in `fields`: the kept keys are equal and `mod_info.runtime` `handler_ms` mean is lower without it.

**Risk.** Low per method.

## Stage 12: switching the old protocol off

**Scope.** Only after the owner decides (overview, *Questions for the owner*). First `[Access] LegacyPipeLevel = none`
and `LegacyTcpLevel = none` in the owner's own config; one release later, those defaults in the shipped mod, with a
CHANGELOG note telling script writers to use the library; later still, removing the version-1 code path.

**Acceptance.** Live: on the test server with the legacy levels at `none`, a version-1 line gets `unauthorized` and the
connection closes; the library, the sidecar and every migrated client work.

**Risk.** Breaks any client not moved. That is why it is last and the owner's call.
