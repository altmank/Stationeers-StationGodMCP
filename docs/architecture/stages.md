# The plan, stage by stage

[Back to the overview](README.md)

Fifteen stages, each small enough to build, test and ship on its own, each leaving every existing client working.
They are ordered by what they give for what they risk: shaping first, because it helps every client at once and
touches one well-tested path; then the catalogue, which everything later reads; then the pipe I/O that version 2
needs, proved on its own; then the new protocol in three steps, permissions, the libraries, the moves, and last the
parts that only pay once the rest is in place.

| Stage | What | Builds on | Implemented by |
| --- | --- | --- | --- |
| 1 | Shaping in the mod, on today's protocol | none | zoran-dotnet-developer, general agent for the check script |
| 2 | The catalogue | 1 | zoran-dotnet-developer |
| 3 | Full-duplex pipes, version 1 only (spike) | none | zoran-dotnet-developer (mod), general agent (Python pipe module and duplex probe) |
| 4 | Protocol version 2: hello, calls in flight, order, cancel, events | 2, 3 | zoran-dotnet-developer, general agent for the probe |
| 5 | Strict argument checking on version 2 | 4 | zoran-dotnet-developer |
| 6 | Sign-in, permissions, and version 2 over TCP | 4 | zoran-dotnet-developer |
| 7 | The Python library | 3, 4 (6 for keys) | general agent |
| 8 | Existing Python clients onto the library, lenient first | 7 | general agent |
| 9 | The sidecar as a thin adapter on the C# client | 5, 6 | zoran-dotnet-developer |
| 10 | Fair scheduling by lanes | 4 | zoran-dotnet-developer, general agent for the load script |
| 11 | Subscriptions | 10 | zoran-dotnet-developer (mod, C# client), general agent (Python) |
| 12 | `sample_logic` in the mod | 10 | zoran-dotnet-developer |
| 13 | Dashboard reads by subscription | 8, 11 | general agent |
| 14 | Skipping costly parts of replies | 1, 2 | zoran-dotnet-developer |
| 15 | Switching the old protocol off | 8, 9, 13, owner's decision | zoran-dotnet-developer |

Stage 3 can run beside stages 1 and 2, and stages 7 and 10 beside 5 and 6; nothing else runs in parallel on the mod,
because each mod stage changes the request path the next one builds on.

## Rules for every stage

- **Code style.** C# follows the owner's rules in the zoran-dotnet-developer agent, cited by rule slug in its report.
  In the mod (netstandard2.1): no records, `init` accessors or `required` members, which would need compiler shims
  (`IsExternalInit`, `RequiredMemberAttribute`), so sealed classes and readonly structs; no LINQ on per-call or
  per-frame paths or over world-sized collections (already banned by `BannedSymbols.txt`). Pure logic goes in
  `src/StationGodMCP.Mod/Pure/` with unit tests, so the test project can compile it
  (`tests/StationGodMCP.Tests/StationGodMCP.Tests.csproj:15-19`). The sidecar and the C# client (net8) may use records,
  as the sidecar does today.
- **Briefs.** Each implementer gets a self-contained brief: this folder, the stage's section, the owner's notes
  (`CLAUDE.md`), and for C# the stationeers-mod skill's `implementation-brief.md`.
- **No measuring or recording systems.** No stage adds code, settings or tools that measure costs or record calls
  or replies for later comparison. Acceptance is by tests: offline unit tests on synthetic data, and direct calls
  against the test server.
- **Tests.** The full suite stays green (1,711 tests at 1.10.0, owner notes `CLAUDE.md`, State). Every changed wire shape
  gets a wire test. A stage is not done while any of its acceptance checks is unrun; an unrun live check goes into the
  owner's `TODO.md` with its exact steps.
- **Versions.** No stage owns a version number, because stages may ship in a different order or together. A stage
  that changes the mod or the sidecar takes the next free minor version when it is released, bumped in the five places
  `build.ps1` checks, adds a CHANGELOG section and updates `About/About.xml`'s change log. Before deciding what that change log
  holds, ask the owner whether the last version was published to Steam (the owner's standing rule).
- **Docs.** Player docs (README, `docs/*.md`) change in the same commit as the behaviour they describe and never say
  what is or is not tested in game. This folder is updated when a stage changes a decision.
- **Python.** Every Python command names its interpreter: `py -3.12`.
- **Deploying.** To the owner's game only through the deploy queue (`deploy-queue.ps1 add StationGodMCP -Note ...`),
  because the game is usually running.
- **The test server.** Only while the owner's game is closed and at least 9 GB of memory is free
  (StationeersTestServer `testserver.json`, `min_free_gb`); a watchdog stops it when the game starts. Testers only test
  and report; they write nothing to any repository. The world comes up paused; `mcp.py wait` unpauses it.

The commands below are for Git Bash (POSIX quoting: JSON in single quotes). From PowerShell 5.1 the inner double
quotes would be stripped when calling `py`; use Git Bash, or pass the JSON through a file. Every live check starts the
same way:

```bash
cd "<StationGodMCP repository>"
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
cd "<StationeersTestServer>"
powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File start-server.ps1 -Save fixround -Wait
py -3.12 mcp.py wait          # prints mod_version, pipe_name StationGodMCP-Test, missing_count, clock
py -3.12 paste.py paste blueprints/compact-bathroom.blueprint --anchor -1311 222 -711   # only where a check needs it
```

and ends with `powershell -NoProfile -File stop-server.ps1`. "The fixtures" means the `fixround` save with the
`compact-bathroom` blueprint pasted at the anchor in `testserver.json`, as the 1.10.0 equivalence run used (owner notes
`CLAUDE.md`, State, 1.10.0). A check that compares two reads of a changing world pauses it first
(`py -3.12 mcp.py console pause true`) and unpauses after.

## Stage 1: shaping in the mod

**Scope.** The mod honours an optional `shape` key in the request envelope, on today's protocol, with the version-1
rules of [protocol.md](protocol.md), *Shaping*: `fields` (single names and dotted paths, any case, no count limit; a
selector that does not parse is listed in `fields_unmatched`, not refused), `limit` (a key that is not a top-level list
is ignored) and `max_bytes`, plus `fields_unmatched`, `shape_truncated`, `reply_too_large`, and `shaped: true` on the
reply envelope whenever a `shape` was applied. Shaping happens in a filtering JSON writer wrapped round the existing
serialiser, so a reply without `shape` is written byte for byte as today. The sidecar sends `fields` to the mod as
`shape.fields` and applies its own `FieldSelection` only when the reply is not marked `shaped`, so an old mod keeps
working (it reads only `id`, `method` and `params`, `src/StationGodMCP.Mod/Api/ApiHost.cs:156-165`) and a new one is not
shaped twice. With `output_file` and no `fields`, nothing is sent.

**Files.**
- New, pure: `src/StationGodMCP.Mod/Pure/Shaping/` (`ShapeRequest` parsed into a closed set of selector classes,
  `FieldSelectors` with the grammar, `ShapingJsonWriter`, `ShapeOutcome`).
- `src/StationGodMCP.Mod/Api/ApiHost.cs` (read `shape` beside `params`; pass it to serialising; error replies never
  shaped), `src/StationGodMCP.Mod/Api/Shared/ApiJson.cs` (a shaped `WriteShared`),
  `src/StationGodMCP.Mod/Api/Views/HostViews.cs` (`shaped` on `ReplyView`, written only when true).
- `src/StationGodMCP.Server/Program.cs` (`SendToGameAsync`, `ParseGameResponse`), `ReplyShaping.cs` (forward
  `shape.fields`; skip `FieldSelection` on a shaped reply).
- `tools/shape_check.py` (new, general agent).
- Docs: README *Large replies*, `docs/devices-and-logic.md` (the pipe envelope), CHANGELOG.

**Acceptance, offline.**
- Same results as the sidecar, compared as parsed JSON with key order, for a set of single-name selectors per reply
  (the keys its entries have, a mixed-case name, an unknown name, a name with surrounding spaces, 100 names), over
  synthetic replies written for the tests in `tests/StationGodMCP.Tests/Fixtures/Shaping/`: one per reply form of the
  methods the dashboard, the scripts and agents call most (`thing_health`, `list_devices`, `find_things`,
  `grid_survey`, `read_devices`, `game_clock`), the edge cases (empty lists, lists of non-objects, dictionary keys with
  capitals, nested lists), and the `FieldSelection` cases in `tests/StationGodMCP.Tests/FileOutputTests.cs:118-141`.
- No change without `shape`: every view the wire tests serialise gives byte-identical output through the new path
  (same serialiser on both sides).
- Paths: nested keys, dictionary keys with capitals (`logic.Temperature`), a path through a list inside an entry, two
  selectors on one key (union), a single name beside a path on the same list, a path whose first name is not a list
  (unmatched), a selector with a `-` or a stray dot (unmatched, not refused).
- `limit` cuts and reports `shape_truncated`; `max_bytes` gives `reply_too_large` with `counts`.
- The sidecar: a transcript test with a path selector keeps the path's keys (no second pass on a `shaped` reply); a fake
  old mod (no `shaped`) still gets `FieldSelection`; `shape.fields` is sent only when `fields` is given.

**Acceptance, live (test server, fixtures).** `tools/shape_check.py` sends version-1 lines with and without `shape`
and compares, with the world paused; it records nothing.
1. `py -3.12 tools/shape_check.py --pipe StationGodMCP-Test --method thing_health --ids-from-find kind=structure,limit=84
   --fields reference_id,damage_ratio,is_broken,condition`: the shaped reply equals the unshaped reply projected in
   Python by the same rules, and is at most a quarter of its bytes.
2. The same for `list_devices` with `fields: [reference_id, prefab_name, display_name]`, and for a path selector on
   `find_things` (`things.position.x`).
3. Through the sidecar: `py -3.12 livetest/lt.py thing_health '{"reference_ids": ["<id>", "<id>"], "fields":
   ["reference_id", "damage_ratio"]}'` answers as before the stage; with `"fields": ["things.position.x"]` on
   `find_things`, entries keep `position.x`.

**Risk.** Low to medium: every reply passes through the new writer. Guarded by the byte-identical test over all wire
views and the parsed-JSON comparison against the sidecar.

**As built.** Where the code needed a choice the text above leaves open:
- The version-1 reading of `shape` never refuses: a `shape` that is not an object is ignored (no `shaped` mark); an
  empty `fields` means no fields; a selector that is not a string is listed in `fields_unmatched` by its JSON text; a
  `limit` entry that is not a whole number from 0 to 100,000 and a `max_bytes` outside 1,024 to 16,777,216 are
  ignored, as are unknown keys.
- A top-level list that no path names and no single name reaches has its entries emptied, which is what the
  sidecar's rule gives for a name no entry has. A key kept whole (a single name, or a path that stops there) still
  counts a deeper path through it as matched when that path's key is present.
- `fields_unmatched` and then `shape_truncated` are written as the last keys of the result object, and `shaped` as
  the last key of the envelope. `max_bytes` is compared with the whole reply line's UTF-8 bytes; `reply_too_large`
  carries `data` `{bytes, limit, counts}` through an optional `data` on `ErrorView`, which errors without data do not
  write.
- The byte-identity check runs inside the wire tests' own helper (`WireCheck.New`), so every view any wire test
  serialises is written through the shaping writer with nothing to leave out and compared byte for byte, in the reply
  envelope too; the same views are also shaped with every other entry key plus an unknown name and compared with the
  sidecar's result. The fixtures in `Fixtures/Shaping/` are synthetic.
- The live checks ran on the test server (fixround, compact-bathroom pasted, paused): `thing_health` on 84 ids with
  four fields came to 0.122 of the unshaped bytes, `list_devices` with three fields to 0.42, `find_things` with
  `things.position.x` to 0.063, each equal to the projection; through the new sidecar a path selector kept
  `position.x`, and the old sidecar still shaped its own replies.
- The common start's paste needs the blueprint's absolute path: `paste.py` reads a relative name under the game's own
  Blueprints folder. `setup.ps1` cannot replace the test server's sidecar while an MCP client runs it; the sidecar
  checks then use a copy of the new build's portable zip.

## Stage 2: the catalogue

**Scope.** Everything in [catalogue.md](catalogue.md) except what later stages use it for: the `catalogue/` sources,
bootstrapped once from the sidecar's tool definitions by a one-shot test (`STATIONGOD_BOOTSTRAP_CATALOGUE=1`), with the
shared description texts as includes, then completed by hand with classes (as recommended, pending the owner), costs,
ranges, paging order and the top-level reply keys of every method; `shared_reply_keys`; the assembled
`catalogue.json`; consistency tests 1 to 7 (test 5 only for methods given `x-views`); the run-time drift counter in
`mod_info`. The mod embeds `catalogue.json` and takes its argument-name check from it (same behaviour as
`tool-arguments.json` today). The sidecar builds `tools/list` from its embedded copy, projecting away the keywords
today's schemas leave out. `tool-arguments.json`, `ToolArguments.cs` and `ToolArgumentsFileTests.cs` go; so does
`ToolDefinitions` once the `tools/list` comparison passes.

**Files.** `catalogue/**` (new), `catalogue.json` (new, root), `StationGodMCP.csproj` (embed it instead of
`tool-arguments.json`), `src/StationGodMCP.Mod/Pure/Catalogue/` (loader, schema-subset validator, class and cost rules;
new), `src/StationGodMCP.Mod/Api/ApiHost.cs`, `Api/Shared/DeclaredArguments.cs`, `Api/Shared/Args.cs` (drift counter
against a precomputed set), `Api/ModInfo.cs`, `Api/Views/RuntimeViews.cs`, `src/StationGodMCP.Server/Program.cs`,
`src/StationGodMCP.Server/ToolArguments.cs` (deleted), `src/StationGodMCP.Server/StationGodMCP.Server.csproj` (embed),
`build.ps1` (version check, copy), `tests/StationGodMCP.Tests/ToolArgumentsFileTests.cs` (deleted),
`tests/StationGodMCP.Tests/Catalogue*Tests.cs` (new).

**Acceptance, offline.** The seven consistency tests in [catalogue.md](catalogue.md) pass; the sidecar's `tools/list`
from the catalogue, projected, equals the one from `ToolDefinitions` as parsed JSON; the schema-subset validator
accepts and refuses the cases the sidecar's `ArgumentCheck` tests cover today (unknown names with the nearest one,
wrong types, enum case folding, `3.0` and `1e2` as integers, nulls as omitted, repeated keys); a catalogue using an
unsupported keyword fails to load with a clear message; the hash of `catalogue.json` computed in C# and in Python is
the same.

**Acceptance, live (test server).**
1. Before upgrading: `py -3.12 livetest/lt.py list > before-list.txt` and `py -3.12 livetest/lt.py schema place_cables >
   before-schema.json`. After: the same commands give the same 91 names and the same schema as parsed JSON.
2. `py -3.12 tools/read_devices_equivalence.py --pipe StationGodMCP-Test --exact` with the world paused passes as it did.
3. `py -3.12 mcp.py find_things '{"prefab": "x"}'` is refused `invalid_argument` naming `prefab_contains`.
4. After 1 to 3, `py -3.12 mcp.py mod_info` shows `runtime.catalogue_drift` empty.

**Risk.** Medium: a large mechanical move of descriptions and schemas. The `tools/list` comparison and the
handler-argument test make a slip visible.

## Stage 3: full-duplex pipes (spike)

**Scope.** Prove, and then ship, the pipe I/O version 2 needs, with nothing of version 2 yet.
- Mod: create pipe instances for overlapped I/O. First `PipeOptions.Asynchronous`; if Unity's Mono does not give
  overlapped named pipes on Windows (GUESS until this stage), create them through `CreateNamedPipe` with
  `FILE_FLAG_OVERLAPPED`. One reader and one writer per connection, the writer fed from an outbound queue; the main
  thread hands finished replies to the queue instead of a listener thread waiting per request. Timeouts move with
  it: each request in flight has an atomic state (queued, running, answered) and a deadline (30 seconds for version 1,
  as today). One timer thread for the whole mod checks deadlines every 100 ms; for a request still queued at its
  deadline it changes the state to answered and puts `game_timeout` on the connection's queue. The main thread skips a
  request whose state is no longer queued, and a request that has started runs to the end and is answered normally, as
  today (`src/StationGodMCP.Mod/StationGodRequestDispatcher.cs:52-58`, `:80-96`). The state change guarantees exactly
  one reply per request. Replace the first-line
  watchdog's `CancelSynchronousIo` (`src/StationGodMCP.Mod/StationGodPipeServer.cs:262-318`) with an asynchronous read
  with a timeout, or `CancelIoEx`. Raise the instances from 4 (`StationGodPipeServer.cs:23`) to `[Server]
  MaxPipeConnections`, default 32. Version 1 behaviour unchanged: one request, one reply, in order, per connection.
- Python: `clients/python/stationgod/pipe.py`, overlapped `CreateFileW`/`ReadFile`/`WriteFile` through `ctypes`.
- `tools/duplex_probe.py`: a client that keeps a read pending on one thread while writing requests on another.
- A setting `[Server] OverlappedPipes` (default true); setting it to false brings back today's synchronous pipe
  code, unchanged, as a fallback.

**Files.** `src/StationGodMCP.Mod/StationGodPipeServer.cs`, `src/StationGodMCP.Mod/StationGodRequestDispatcher.cs`
(completion into a queue), `src/StationGodMCP.Mod/StationGodMod.cs` (settings), new
`src/StationGodMCP.Mod/Protocol/` (connection, outbound queue), `src/StationGodMCP.Mod/Pure/Protocol/` (line reader,
queue rules); `tests/StationGodMCP.Tests/StationGodMCP.Tests.csproj` (also compiles `Protocol/`, which therefore
holds no game types); `clients/python/stationgod/pipe.py`, `tools/duplex_probe.py` (general agent); `docs/configuration.md`,
CHANGELOG.

**Acceptance, offline.**
- Version-1 transcripts: request and reply lines written for the test replay byte for byte through the new pipe code
  with a fake dispatcher.
- The duplex test on a real Windows named pipe in the test process: the server side (the mod's connection class, built
  against `netstandard2.1` and run under .NET) holds a pending read while its writer sends a line; the client holds a
  pending read while it writes a request; in both directions the line arrives within 100 ms. The Python `pipe.py`
  passes the same test against a pipe the test creates.
- First-line timeout: a client that connects and sends nothing is dropped after 10 seconds and frees its instance.
- Request timeout: with the fake dispatcher never reaching a request, the client gets `game_timeout` after 30 seconds
  and the request never runs; a request the dispatcher starts at 29.9 seconds is answered with its result, not
  `game_timeout`; never both.

**Acceptance, live (test server).**
1. Version 1 unchanged: `py -3.12 mcp.py mod_info`, `py -3.12 validate.py` and `py -3.12 replaceable.py` run as before;
   `py -3.12 livetest/lt.py game_clock '{}'` answers.
2. `py -3.12 tools/duplex_probe.py --pipe StationGodMCP-Test`: with a read pending, a `game_clock` request written from
   another thread is answered; round trip at most one frame plus 100 ms. This proves the mod's pipe under Unity's Mono.
3. `py -3.12 tools/duplex_probe.py --pipe StationGodMCP-Test --connections 33`: 32 connections are served; the 33rd waits
   for a free instance and is served once one closes.

**Risk.** High for this stage alone, which is why it is alone: threads and I/O in the game process. The setting turns it
back to today's code. If neither overlapped route works under Unity's Mono, the plan stops here and the owner decides
between loopback TCP for local clients and a version 2 without pushes.

**As built.** Where the code needed a choice the text above leaves open:
- The mod takes the Windows route at once: `CreateNamedPipeW` with `FILE_FLAG_OVERLAPPED`, `ReadFile` and `WriteFile`
  with an `OVERLAPPED` and event each in unmanaged memory, `CancelIoEx` to end a wait (`Protocol/NativePipe.cs`).
  The same code then runs under .NET in the tests and under the game's Mono, and nothing depends on how Mono's
  `PipeStream` implements asynchronous reads. It works under the game's Mono: on the test server the duplex probe's
  round trip was 8.7 ms, 32 connections were held, the 33rd found the pipe busy and was served 31 ms after one
  closed. Off Windows, or when those calls are missing, the synchronous pipe is used, as with `OverlappedPipes =
  false` (checked live: `(4 instances)` in the log, calls answered).
- One listener thread keeps up to four instances waiting, never more instances than `MaxPipeConnections` in all
  (Windows enforces it through the pipe's instance count); instances refuse remote clients
  (`PIPE_REJECT_REMOTE_CLIENTS`).
- Each connection has a reader and a writer thread (`Protocol/Connection.cs`); the reader owns the connection's life.
  A version-1 connection still takes its next line only after the previous one is answered. The first complete line
  must arrive within 10 seconds in all, timed by an overlapped read that `CancelIoEx` ends.
- Lines end at a line feed, a carriage return before it dropped and a byte order mark at the very start dropped
  (`Pure/Protocol/LineFramer.cs`); a lone carriage return no longer ends a line as `StreamReader` let it, which no
  known client relies on.
- Calls in flight are `QueuedCall`s with a `CallState` (`Pure/Protocol/CallState.cs`); the deadline watch is one
  `System.Threading.Timer` every 100 ms for the whole mod. A call the main thread has started is answered with its
  result even when it finishes after its deadline; today's listener answered `game_timeout` at 30 seconds and dropped
  the late result.
- The TCP listener and the synchronous pipe keep their threads and call `Dispatch`, which now queues a `LineCall` and
  waits for its answer; the same deadline watch answers them.
- The Python pipe module is the client library's (`clients/python/stationgod/pipe.py`, stage 7), tested there against
  a pipe its tests create; `tools/duplex_probe.py` uses it.
- Live check 1: `validate.py` needs the blueprint and anchor and `replaceable.py` the ids; both ran end to end on the
  fixtures, with one verdict that is the game's own rule (the fixture's purifier has no frame below it).

## Stage 4: protocol version 2

**Scope.** On the stage-3 connections: the first-line classifier; `hello` and `welcome` (anonymous only; every
version-2 pipe connection gets today's level), with `server.instance_id` and `server.world`; `call`, `reply` with `type`,
`shaped`, `queue_ms` and `frame`; up to 16 calls in flight per connection; `duplicate_id`; deadlines and `cancel`; the
per-connection ordering rule; taking calls from each connection in turn (a simple round-robin in the dispatcher; lanes
come in stage 10); the `catalogue` protocol method; `ping`, `world_changed` and `goodbye`; the slow-client rule; request
size limit; `mod_info` connections. Argument checking stays as on version 1 (names only) until stage 5. A setting
`[Server] Protocol2` (default true) turns version 2 off.

**Files.** `src/StationGodMCP.Mod/Protocol/` (hello, calls, events), `src/StationGodMCP.Mod/Pure/Protocol/` (message
parsing, first-line classifier, call ordering), `src/StationGodMCP.Mod/Pure/WorldScope.cs` (a world id per load),
`src/StationGodMCP.Mod/StationGodRequestDispatcher.cs`, `Api/ApiHost.cs`, `Api/ModInfo.cs`, views;
`tools/protocol_probe.py` (new, general agent); `docs/configuration.md`, CHANGELOG.

**Acceptance, offline.**
- First-line classifier: `hello` starts version 2; a request object, a non-JSON line and an empty object start version 1
  (answered as today).
- Call ordering: with a fake clock and fake handlers, a read after a write on one connection starts after the write is
  answered; two reads may swap; a write waits for every earlier call.
- Ids: a duplicate in flight gets `duplicate_id`; the same id after its reply is fine.
- Framing: CR LF accepted; blank lines ignored; a 5 MiB line gives `request_too_large` and closes.
- Outbound queue: a 16 MiB reply is queued and sent to a reading client; a client that reads nothing for 35 seconds
  gets `goodbye slow_client`.
- A new world id on every world load edge; `instance_id` differs between two mod instances.

**Acceptance, live (test server).**
1. Version 1 unchanged, as in stage 3, check 1.
2. `py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test hello`: `welcome` with `protocol` 2,
   `server.pipe_name` `StationGodMCP-Test`, a `world.id`, `limits.max_in_flight` 16, `features` including `shape` and
   `cancel`.
3. `... burst --method game_clock --count 16`: 16 replies on one connection, each id answered once; a 17th in flight
   gets `too_many_in_flight`.
4. `... order --device <a fixture device with a writable Setting>`: sends `write_logic` Setting 7 and then `read_logic`
   Setting without waiting; the read answers 7. Restores the old value after.
5. `... cancel`: sends a `grid_survey` of the fixture area and a `game_clock`, cancels the second at once; it answers
   `cancelled` or, if it had started, its result.
6. Restart the server (`stop-server.ps1`, `start-server.ps1 -Save fixround -Wait`) and run `hello` again: a different
   `instance_id` and `world.id`.

**Risk.** Medium: new message handling on proven I/O. Version 1 keeps its path, and the setting turns version 2 off.

**As built.** Where the code needed a choice the text above leaves open:
- A first line is version 2 when it is a JSON object whose `type` is `hello` (`Pure/Protocol/ClientMessage.cs`,
  `FirstLine`); messages are parsed on the connection's reader thread into a closed set (hello, auth, call, cancel,
  bye, or a refusal that says what to answer). Until stage 6 a hello with `auth: "key"`, or any hello over TCP, is
  answered `unauthorized` and closed; every keyless pipe connection gets today's level (`cheat`, standing).
- A call with a bad `id`, an unknown key, an unknown `type`, a second hello, `auth` without a challenge, or a line
  that is not a JSON object gets a `protocol_error` reply (null id, `data.line_start`), then `goodbye`
  `protocol_error`, and closes. A call without `method` gets `method_not_found`, and `params` that is not an object, a
  `deadline_ms` outside 100 to 600,000 or a key given twice get `invalid_argument`, all with the call's id; the
  connection stays open.
- `welcome.limits` lists `max_in_flight`, `max_request_bytes` and `max_reply_bytes`; the subscription limits come
  with subscriptions (stage 11). `features` is `shape`, `shape.paths`, `cancel`. `world.save` is the game's
  `XmlSaveLoad.Instance.CurrentStationName` (CODE), `dedicated` is Unity's batch mode, `game_state` the
  `GameManager.GameState` name. The main thread publishes these as a snapshot when they change (`ServerFacts`), so no
  connection thread touches a game object. `world.id` is new the first frame a world runs (`WorldScope`).
- The protocol method `catalogue` is answered on the reader thread with the embedded catalogue as compact JSON (the
  file itself is indented; the hash is of the file's bytes). Protocol methods have their own sources,
  `catalogue/protocol/<method>.json`, assembled into `protocol_methods`; test 2 holds them to the protocol layer's
  table (`ProtocolMethods.Names`).
- Every ok reply carries `shaped` (false without a shape). Version-2 replies are always written through the shaping
  writer, which counts the top-level lists, so a reply over 16 MiB is `reply_too_large` with `counts`. Answers for
  calls that never ran (`game_timeout`, `cancelled`, `shutting_down`, refusals) carry no `elapsed_ms`, `queue_ms` or
  `frame`.
- The dispatcher takes calls through `ICallScheduler`; stage 4's `RoundRobinScheduler` takes one call per connection
  per round and, within a connection, the earliest call the order rule (`CallOrder`) lets start. Because the main
  thread runs one call to its answer before taking the next, the rule comes down to each connection's calls in
  order; it matters once lanes take a later call first (stage 10). A call's effective class comes from the
  catalogue's class rules at its params; an unknown method counts as a read. Version-1 lines, the TCP listener and the
  synchronous pipe share the same ring.
- A version-2 line not taken by the client within 35 seconds (one write) closes the connection: its unstarted calls
  are dropped, a `goodbye` `slow_client` is tried for a second, then it closes. `ping` follows 30 seconds without a
  line sent. The optional idle close is not built. When the host leaves its world, waiting calls are answered
  `shutting_down` and clients get `goodbye` `world_unloaded`; when the mod stops, `goodbye` `shutting_down`.
- `mod_info` `runtime.connections` lists each open connection: `client_id`, `client`, `label` (the hello's name),
  `transport`, `protocol`, `level`, `in_flight`, `served`, `bytes_sent`; absent on the synchronous pipe.
- Live (test server, fixtures): checks 1 to 5 passed (welcome as specified with `dedicated` true and `save`
  `fixround`; 16 answered once each and `b16` refused `too_many_in_flight`; the read after `write_logic` Setting 7
  answered 7 in the same frame, restored; the cancelled `game_clock` answered `cancelled`). Check 6 (a restart gives a
  new `instance_id` and `world.id`) and the library's live tests were not run: the owner's game started and the
  watchdog stopped the server; they are in the owner's TODO.

## Stage 5: strict checking on version 2

**Scope.** Version-2 calls are checked against the catalogue in full ([catalogue.md](catalogue.md), *The schema
subset*): nested unknown names with a path, types, ranges, enums, patterns, required arguments; `invalid_argument` with
`data.problems`; `invalid_shape` for malformed `shape` and more than 256 selectors. Version 1 stays lenient. A setting
`[Server] StrictArguments` (default true) turns it off for version 2.

**Files.** `src/StationGodMCP.Mod/Pure/Catalogue/` (validator use), `src/StationGodMCP.Mod/Protocol/`,
`src/StationGodMCP.Mod/Pure/Shaping/` (version-2 rules), `tests/StationGodMCP.Tests/`, CHANGELOG.

**Acceptance, offline.** The validator cases from stage 2 through the version-2 path; the same requests through the
version-1 path behave as today; malformed `shape` cases on version 2 are `invalid_shape` and on version 1 still
answered with `fields_unmatched`.

**Acceptance, live (test server).** `py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test strict --method
connections --params '{"reference_id": "1", "limit": 99999}'` answers `invalid_argument` with the path `limit` and the
range; the same params over version 1 (`py -3.12 mcp.py connections '{"reference_id": "1", "limit": 99999}'`) behave as
today.

**Risk.** Low: version 1, which every existing client speaks, is untouched.

**As built.** Where the code needed a choice the text above leaves open:
- The check runs on the connection's reader thread before the call is queued, so a refused call costs the main thread
  nothing and its reply has no `elapsed_ms`. The shape is checked first (`invalid_shape`), then the params
  (`invalid_argument`); both list every problem in `data.problems` as `{path, problem}` and join the sentences in
  `message`. Shape problems carry the path `shape`. A method the catalogue does not have, and `sample_logic` (answered
  by the sidecar), are left to the main thread, which answers as on version 1.
- `limit` may name only a key the method's `reply` describes as a list (`CatalogueMethod.ReplyLists`). A path
  selector whose first name is not a reply list is not refused: it is reported in `fields_unmatched`, as on version 1.
- Live (test server): not run; the owner's game was running (owner's TODO).

## Stage 6: sign-in, permissions, and version 2 over TCP

**Scope.** The *Sign-in and permissions* part of [protocol.md](protocol.md): the clients file and its reload; the
challenge and HMAC proof with the 10-second sign-in limit; levels, class rules from the catalogue, grants; armed and
standing cheat; the `stationgod allow`, `deny` and `clients` console commands, with arming per `client_id` or per key
name, refused while `run_console_command` runs and refused by `run_console_command` itself; `[Access]
AnonymousPipeLevel`, `AnonymousCheat`, `LegacyPipeLevel`, `LegacyTcpLevel` (shipped defaults keep today's behaviour on
the pipe; legacy TCP keeps cheat until the owner chooses option A, see the overview); version 2 over TCP with keys, `[Server] MaxTcpConnections` 8, the listener
starting with a secret or a TCP key; `goodbye revoked`; `cheat_armed` and `cheat_disarmed` events. The sidecar program
gains `key new`. A test-only setting `[Access] AllowArmingFromToolConsole` (default false; documented as never to be set
on a played game) lets the dedicated test server, which has no console a tester can type into, be armed through
`run_console_command`.

**Files.** `src/StationGodMCP.Mod/StationGodTcpServer.cs`, `src/StationGodMCP.Mod/StationGodMod.cs`
(`RemoteSettings.Load`, access settings), `src/StationGodMCP.Mod/Protocol/` (sign-in),
`src/StationGodMCP.Mod/Pure/Access/` (clients file, levels, permission decision, HMAC proof, arming with an injected
clock; new), `src/StationGodMCP.Mod/Api/Shared/Game/StationGodCommands.cs` (new), `src/StationGodMCP.Mod/Api/RunConsoleCommand.cs`
(the refusal and the flag), `src/StationGodMCP.Server/Program.cs` (`key new`); docs: README *Safety*,
`docs/configuration.md` (*Access*, the clients file), `docs/install.md` (remote access with keys, `serverrun` for an
owner playing as a client), CHANGELOG.

**Acceptance, offline.**
- The permission decision for every combination of level, class, grant, armed or standing, and the class rules:
  `place_cables` with no `dry_run` is read, with `dry_run: false` write; `place_structure` with `free: true` and
  `dry_run: false` cheat; `move_gas` with `dry_run: true` read; `plant_genes` without `genes` read.
- HMAC proof vectors: a fixed key, nonce text, client and transport give a fixed hex string (shared with the Python
  tests); `auth.client` unlike `hello.client.name` is `unauthorized`; a sign-in not finished in 10 seconds is closed.
- The clients file: a short key, an unknown level, a duplicate name and a bad transport each disable only that client,
  logged by name and fingerprint, never the key.
- Arming: by `client_id` arms one connection, by key name all of that key's; expiry on time; `run_console_command
  "stationgod allow x"` refused with `permission_denied`; the command itself refusing while the tool's flag is set.
- TCP: with a TCP key and no secret the listener starts; legacy sign-in is refused then; with a secret, legacy TCP
  gets `LegacyTcpLevel` (cheat by default, write when so set), logs a warning at load and logs each legacy sign-in.
- `key new` writes a valid entry and prints the key only to its own output.

**Acceptance, live (test server).** The test server's own config folder holds its clients file; restore it and the
config afterwards.
1. On the host: `StationGodMCP.Server.exe key new probe-read read --config "<StationeersTestServer>/server/BepInEx/config"`,
   and the same for `probe-write write` and `probe-cheat cheat`. Then `py -3.12 mcp.py read_console '{}'` shows no key.
2. `py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test --client probe-read --key-env K call read_logic ...`
   succeeds; `write_logic` gives `permission_denied` with `required` write; `place_cables` without `dry_run` succeeds;
   with `dry_run: false, confirm: true` gives `permission_denied`.
3. With `probe-write`: `write_logic` succeeds (restore the value); `move_gas` with `dry_run: false` gives
   `permission_denied` with `required` cheat.
4. With `probe-cheat`: `move_gas` with `dry_run: true` succeeds; a real `move_gas` of 1 mol between two fixture canisters
   gives `cheat_not_armed`. `py -3.12 mcp.py console 'stationgod allow probe-cheat 1'` prints a refusal (the default
   forbids arming through the tool).
5. Set `AllowArmingFromToolConsole = true` in the test server's config, restart, connect `probe-cheat` again, read its
   `client_id`, and run `py -3.12 mcp.py console 'stationgod allow <client_id> 1'`: the probe receives `cheat_armed`, the
   real `move_gas` succeeds, a second `probe-cheat` connection is still not armed; after a minute the first receives
   `cheat_disarmed`. Move the gas back, set the switch back to false.
6. TCP: set `[Remote MCP] Enabled = true`, `BindAddress = 127.0.0.1`, `Port = 18765`, give `probe-read` the `tcp`
   transport, no secret, restart; the probe over TCP gets `welcome` with `transport` tcp; a wrong key gets
   `unauthorized`. Then set a `Secret` and restart: today's sidecar with `--host 127.0.0.1 --port 18765` and
   `STATIONGODMCP_SECRET` answers `mod_info` and the log shows its legacy sign-in; with `LegacyTcpLevel = write`,
   `run_console_command` through it is `permission_denied`. Restore.

**Risk.** Medium: a mistake could lock clients out. Every shipped default keeps today's behaviour; option A for
legacy TCP, if the owner chooses it, is the one deliberate break and gets its own CHANGELOG note.

## Stage 7: the Python library

**Scope.** `clients/python/stationgod` as [clients.md](clients.md) describes it, on stage 3's `pipe.py`, the shared
fixtures in `clients/fixtures`, `build.ps1` copying `catalogue.json` into the package and `release.ps1` adding a zip of
it to the release assets. No mod change.

**Files.** `clients/python/**`, `clients/fixtures/**`, `build.ps1`, `release.ps1`, and
`tests/StationGodMCP.Tests/FileOutputTests.cs` reading the shared fixtures.

**Acceptance, offline** (`py -3.12 -m pytest clients/python/tests`, no game).
- A fake server in the tests speaks version 1 and version 2 over loopback TCP and a real overlapped named pipe.
- Resending: an unwritten call resent; a written read resent once; a written `highlight` not resent; a written write
  raises `Unreachable(maybe_ran=True)`; a `place_cables` dry run counts as a read, a confirmed run does not; nothing is
  resent after a reconnect that shows a different `world.id`; no error reply is resent.
- Version-1 fallback: the old mod's answer to `hello` switches to version 1 on the same connection; `fields` then
  applied locally, only to replies not marked `shaped`, with results equal to the sidecar's on the shared fixtures.
- Output files: every fixture in `clients/fixtures/output_file`; pruning keeps 200 and the newest; a failed write
  answers inline with `output_file_error`.
- HMAC vectors equal the mod's; the default key variable for `StationGodMCP-Test` is `STATIONGOD_KEY_STATIONGODMCP_TEST`.
- `iterate` walks pages by `x-paging` until `has_more` is false.
- Subscriptions: `SubscriptionRefused` and `TooOld` raised where they should be; resubscribe after a reconnect only with
  the same `world.id`.

**Acceptance, live (test server, fixtures).** `py -3.12 -m pytest clients/python/tests -m live --pipe StationGodMCP-Test`:
connect and `welcome`; `thing_health` by ids with `fields` equals the projection of the full reply; with the world
paused, `iterate("find_things", kind="structure", page_size=100)` gives the same ids as one call with a large `limit`;
`output_file=True` writes a readable file and a correct pointer; restarting the server during a run of reads
(`stop-server.ps1`, `start-server.ps1 -Save fixround -Wait`) ends in a world-changed callback, and new calls work after.

**Risk.** Low: nothing uses it yet.

## Stage 8: existing Python clients onto the library

**Scope.** Dashboard steps 1, 2, 3, 4, 6 and 7 in [clients.md](clients.md): the transport as a wrapper over the
library on version 1 (lenient), with the path fallback and the old code kept as `transport_v1.py`; the request log and
the replay check; then version 2. `fields` in the cards that read large replies, each card in a copy moved in whole.
StationeersTestServer `tsclient.py` rewritten over the library with all its exports and its guard in `client()`. The
CheatEngineExpert scripts need no edit. No mod change.

**Files.** StationeersScriptDashboard `stationscript/transport.py`, `stationscript/transport_v1.py` (new: today's code),
`stationscript/__main__.py`, `stationscript/metrics.py`, `tests/test_transport.py`, the listed cards; StationeersTestServer
`tsclient.py`; this repository's `tests/StationGodMCP.Tests/CatalogueReplayTests.cs` (new; zoran-dotnet-developer for
this one file).

**Acceptance, offline.**
- The dashboard's test suite (`py -3.12 -m pytest tests` in StationeersScriptDashboard) green, with `test_transport.py`
  testing the wrapper against the library's fake server: same errors, same meter calls, no `READ_ONLY` list; and the
  path fallback (library not installed, found by `STATIONGOD_CLIENT_PATH`) and the last fallback (`transport_v1`).
- Each card edited for `fields` keeps its own tests green.
- `CatalogueReplayTests` (committed): a set of calls written by hand from what the dashboard's cards and the scripts
  send (method and the params their code builds), plus a handful of synthetic calls that prove it refuses what it
  should: zero refusals by the version-2 validator for the first set. Only after this passes does the wrapper switch to
  `protocol="auto"`.

**Acceptance, live.**
1. Test server: `py -3.12 validate.py` and `py -3.12 replaceable.py` run as before on the new `tsclient`. With
   `testserver.json`'s `pipe` temporarily set to `StationGodMCP`, `py -3.12 mcp.py mod_info` exits with the refusal
   from `client()` and sends nothing. Restore `testserver.json`.
2. A CheatEngineExpert script against the test server: `py -3.12 Cheats/Stationeers/tools/rocket/flight_log.py`, with the
   transport's pipe name pointed at `StationGodMCP-Test` for this run (the wrapper reads `STATIONSCRIPT_PIPE`), runs
   for 10 seconds and logs readings. The same script started with another interpreter finds the library through the
   path fallback.
3. The owner's game, after the owner agrees and the dashboard is restarted: the dashboard runs with `--dry-run` (it
   logs writes and sends none) for 10 minutes; every card's status matches the status before the change; `/api/state`
   shows reply KB a minute lower than before the change for the cards given `fields`. Then normal mode.

**Risk.** Medium for the owner's daily tool. The wrapper keeps every name and stays lenient until the replay proves the
strict path; the dry run catches regressions before any write.

## Stage 9: the sidecar as a thin adapter

**Scope.** `src/StationGodMCP.Client` (net8) per [clients.md](clients.md); `ReplyShaping`, `FieldSelection`,
`OutputFolder` and `ArgumentCheck` move there (`ArgumentCheck` used only for the version-1 fallback); the sidecar keeps
MCP and uses one persistent version-2 connection; `tools/list` from the catalogue with the full schemas, `listChanged:
true` and `notifications/tools/list_changed` on a catalogue change; the inline size limit; `--client`, `--key-env`,
`--inline-limit-kb`.

**Files.** `src/StationGodMCP.Client/**` (new), `src/StationGodMCP.Server/*`, `StationGodMCP.sln`,
`tests/StationGodMCP.Tests` (MCP transcript tests against an in-process fake mod), docs: `docs/configuration.md`
(*Sidecar options*), README *Large replies*, CHANGELOG.

**Acceptance, offline.**
- MCP transcripts: `initialize` advertises `listChanged: true`; `tools/list` equals the catalogue-derived list;
  `tools/call` with a bad argument returns the fake mod's `invalid_argument` in today's result shape; `fields` arrives as
  `shape.fields` and is not applied again to a `shaped` reply; `output_file` writes per the shared fixtures; a 300 KB
  reply without `output_file` becomes a pointer with `auto_output_file: true`.
- Against a fake version-1 mod: calls work, arguments checked by `ArgumentCheck` as today, `fields` applied locally,
  `sample_logic` answered by the sidecar's loop.
- Five concurrent `tools/call` messages are answered over one connection.

**Acceptance, live (test server).**
1. Before upgrading, record `py -3.12 livetest/lt.py <tool> '<args>'` for ten representative calls (`mod_info`,
   `list_devices`, `find_things`, `thing_health` with `fields`, `connections`, `grid_survey`, a `place_cables` dry run,
   `read_devices`, `get_ic_status`, `game_clock`) with the world paused; after upgrading, the same calls give the same
   results as parsed JSON, apart from times and frame numbers.
2. A script that writes five `tools/call` lines to the sidecar without waiting gets five answers; `mod_info` shows one
   connection for the sidecar.
3. The sidecar started with `--host 127.0.0.1 --port 18765 --client probe-read --key-env K` against the test server's
   TCP (stage 6 config) answers `mod_info`.

**Risk.** Medium: every agent call goes through it. The recorded comparison and the version-1 fallback limit the damage;
a previous sidecar build can be re-registered in a minute.

## Stage 10: fair scheduling by lanes

**Scope.** [scheduling.md](scheduling.md): the pure frame scheduler with the three lanes, per-item prediction,
round-robin across connections, the heavy-lane waiting bound, the subscription share capped at half the budget,
per-method moving averages, the new `[Performance]` settings, and `mod_info.runtime.lanes`.

**Files.** `src/StationGodMCP.Mod/Pure/Scheduling/` (new), `src/StationGodMCP.Mod/StationGodRequestDispatcher.cs`,
`src/StationGodMCP.Mod/StationGodMod.cs`, `src/StationGodMCP.Mod/Pure/Runtime/MethodTimings.cs`,
`src/StationGodMCP.Mod/Api/Views/RuntimeViews.cs`, `docs/configuration.md`, CHANGELOG; `tools/lane_check.py` (new,
general agent).

**Acceptance, offline.** The scheduler tests listed in [scheduling.md](scheduling.md), *How it is tested*.

**Acceptance, live (test server, fixtures).** `py -3.12 tools/lane_check.py --pipe StationGodMCP-Test --seconds 120`:
connection A sends a 20-item `read_devices` every 0.25 s; connection B keeps two `grid_survey` calls of the whole
fixture area and one unfiltered `find_things` in flight at all times. Run once on the stage-9 build and once on this
one. Pass when, on this build, A's `queue_ms` 95th percentile is at most two frames' worth (frame time from
`mod_info.runtime.frames`) and lower than on the stage-9 build; `mod_info.runtime.lanes.heavy` shows no frame with two
heavy calls and a longest wait of at most 10 frames; B's calls all complete; A's `read_devices` stays in the light lane.

**Risk.** Medium: a scheduling mistake shows as latency or a stuck lane, not as a crash. The waiting bound and the
"first call always runs" rule keep every lane moving.

## Stage 11: subscriptions

**Scope.** [protocol.md](protocol.md), *Subscriptions*: `subscribe` and `unsubscribe`, topics `devices` and `world`,
whole-reading updates sent only on change (clock excluded), one queued update per subscription with its `seq` kept,
the per-subscription, per-connection (64 subscriptions, 8,192 values) and global limits, `subscription_ended`; the sampling lane from [scheduling.md](scheduling.md). Subscription
support in the C# client and the Python library.

**Files.** `src/StationGodMCP.Mod/Pure/Subscriptions/` (comparison, admission; new),
`src/StationGodMCP.Mod/Protocol/` (subscription registry, sampling), `src/StationGodMCP.Mod/Api/ReadDevices.cs` (the
shared read path), `catalogue/methods/subscribe.json`, `unsubscribe.json`, `src/StationGodMCP.Client/`,
`clients/python/stationgod/subscriptions.py`, docs: `docs/devices-and-logic.md`, CHANGELOG.

**Acceptance, offline.**
- Comparison: a sequence of readings sends an update only when a value other than the clock changes; an item that
  starts failing counts as a change; no JSON is built for an unchanged reading.
- Queueing: with the outbound queue held, new readings replace the queued update and keep its `seq`; `seq` has no gaps.
- Admission: the 65th subscription, the 8,193rd value on a connection and the 1,025th in one subscription are refused;
  the projected-load limit refuses with the projection.
- Python and C#: resubscribe after a reconnect only with the same `world.id`; `SubscriptionRefused` on a refusal.

**Acceptance, live (test server, fixtures).**
1. `py -3.12 tools/protocol_probe.py --pipe StationGodMCP-Test subscribe --items <10 fixture devices: On, Setting>
   --interval 1`: a first reading, then no `update` while nothing changes, even though the clock moves.
2. `py -3.12 mcp.py write_logic '{"reference_id": "<id>", "logic_type": "On", "value": 0}'`: one `update` within two
   seconds whose `frame` is greater than the write's; it shows `On` 0. Restore the value.
3. `py -3.12 mcp.py console 'pause true'`: no updates while paused; `pause false` resumes them.
4. The probe stops reading its pipe for 20 seconds while a fixture value is toggled every second: afterwards it receives
   one update per subscription with the newest value, and `seq` has no gap.
5. `mod_info.runtime.subscriptions` shows the subscriptions and a mean sampling time per frame.

**Risk.** Medium: new main-thread work. Admission limits its cost, and `SubscriptionBudgetMs = 0` turns it off.

## Stage 12: sample_logic in the mod

**Scope.** `sample_logic` becomes a mod method with today's arguments, limits, clock and reply
(`src/StationGodMCP.Server/Program.cs:332-455`): its own real-time sampler, not a subscription, taking samples in the
subscription lane; `x-duration` from `duration_seconds`; the version-1 listener's wait (`StationGodPipeServer.cs:24`,
`StationGodRequestDispatcher.cs:80-96`) and the sidecar's and libraries' timeouts extended by the duration. The sidecar
calls the mod's method on a mod that has it and keeps its loop for older mods.

**Files.** `src/StationGodMCP.Mod/Pure/Sampling/` (new: schedule, change list, rounding), `src/StationGodMCP.Mod/Api/SampleLogic.cs`
(new), `src/StationGodMCP.Mod/Api/ApiHost.cs`, `src/StationGodMCP.Mod/StationGodRequestDispatcher.cs`,
`src/StationGodMCP.Mod/StationGodPipeServer.cs`, `catalogue/methods/sample_logic.json`, `src/StationGodMCP.Client/`,
`src/StationGodMCP.Server/Program.cs`, `clients/python/stationgod/client.py`, CHANGELOG.

**Acceptance, offline.** For synthetic reading sequences, the mod's change list, `sample_count`, `change_count` and
elapsed-seconds rounding equal the sidecar loop's; intervals of 0.05 s are kept; a paused game does not stop the sampler;
argument bounds as today (1-32 targets, 0.05-5 s, 0.1-30 s, at most 120 samples).

**Acceptance, live (test server, fixtures).** While a fixture value is toggled every second:
`py -3.12 livetest/lt.py sample_logic '{"targets": [{"reference_id": "<id>", "logic_type": "On"}], "duration_seconds":
30, "interval_seconds": 0.05}'` completes without `game_unavailable` or `game_timeout`, and its `change_count` equals the
same call through the stage-11 sidecar loop within one; the same over version 1 (`py -3.12 mcp.py sample_logic ...`)
also completes.

**Risk.** Low to medium: a long-running call on the request path; the extended timeouts are the risky part and are
tested on both protocols.

## Stage 13: dashboard reads by subscription

**Scope.** Dashboard step 5 in [clients.md](clients.md): the runner subscribes per card, gateway and chunk and reads
`sub.state` at each tick, falling back to `read_devices` per chunk when refused or without the feature. Cards unchanged.
No mod change.

**Files.** StationeersScriptDashboard `stationscript/runner.py`, `stationscript/station.py`, `tests/test_runner.py`.

**Acceptance, offline.** `test_runner.py`: with a fake library, a card's handles get the same values from a
subscription as from `read_devices`; a refused chunk polls while its siblings subscribe; a rebind closes and reopens its
subscriptions; without the feature the runner calls `read_devices` as before.

**Acceptance, live (owner's game, with the owner's OK).** The dashboard restarted with `--dry-run` for 10 minutes: every
card's status matches a status snapshot taken before; `/api/state` shows `read_devices` calls a minute down by at least
90 % against stage 8, while the cards' other polls (`atmosphere_contents`, `inspect_slots`, `thing_health` and the rest)
are unchanged and are named in the report as the remaining load; `mod_info.runtime.subscriptions` shows the dashboard's
subscriptions with none refused. Then normal mode for 30 minutes with the same statuses.

**Risk.** Medium for the owner's daily tool; the dry run and the per-chunk fallback contain it.

## Stage 14: skipping costly parts of replies

**Scope.** `args.Shape.Wants(list, key)` for handlers, and `x-costly` entries, with full entry schemas and `x-views`,
for the parts known to be costly, starting with `thing_health`'s `networks`
(`src/StationGodMCP.Mod/Api/ThingHealth.cs:294`). Each method is its own small change.

**Files.** `src/StationGodMCP.Mod/Api/Shared/Args.cs`, the chosen handlers, their catalogue entries.

**Acceptance.** Offline: for each changed method, the views give the same values for every kept key whether the costly
part was skipped or not (view-level tests), and consistency test 5 covers its new `x-views`. Live (test server,
fixtures, paused): `py -3.12 tools/shape_check.py` for the method, with and without the costly key in `fields`: the kept
keys are equal and `mod_info.runtime` `handler_ms` mean is lower without it.

**Risk.** Low per method.

## Stage 15: switching the old protocol off

**Scope.** Only after the owner decides (overview, *Questions for the owner*); the recommendation is after every client
in the owner's repositories has moved plus one clean release on version 2. First `[Access] LegacyPipeLevel = none` and
`LegacyTcpLevel = none` in the owner's own config; one release later, those defaults in the shipped mod, with a
change-log note that stays in the Workshop change log for several versions; later still, removing the version-1 code
path, the sidecar's `ArgumentCheck` and `sample_logic` loop, and the dashboard's `transport_v1.py`.

**Acceptance.** Live: on the test server with the legacy levels at `none`, a version-1 line gets `unauthorized` and the
connection closes; the library, the sidecar and every migrated client work.

**Risk.** Breaks any client not moved. That is why it is last and the owner's call.
