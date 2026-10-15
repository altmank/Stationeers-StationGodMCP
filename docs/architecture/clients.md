# Clients

Two client libraries speak StationGod. The **C# client** (`src/StationGodMCP.Client`) is the sidecar's own library: it
talks to the mod over the pipe or TCP, and owns connecting, calls in flight, the catalogue, reconnecting and output
files. The **Python library `sg`** (`clients/python/sg`) talks only to a hub, over the hub protocol ([hub.md](hub.md)),
and is what `run_script` code, Python programs and the dashboard use. Nothing else speaks to the mod, and no other
client keeps a second copy of the mod's rules: options, shaping and permissions are the mod's or the hub's
(`src/StationGodMCP.Client/StationGodClient.cs:8-20`).

## The C# client

`GameLink` is the class the sidecar and the hub use, one per process, over one connection kept for the session
(`src/StationGodMCP.Server/Program.cs:58`, `src/StationGodMCP.Server/Hub/HubMode.cs:13`). The `IGameLink` seam is what the hub sees
(`src/StationGodMCP.Client/GameLink.Seam.cs:6-16`). It:

- says `hello` and needs a `welcome`; over TCP the shared secret comes first, read from `STATIONGODMCP_SECRET` or the
  variable `--secret-env` names, never from an argument (`ClientOptions.cs:10-14`, `:35-38`);
- keeps calls in flight on streams, at most `max_in_flight` per stream and `max_in_flight_per_connection` in all,
  admitted round robin by stream with stream 0 first (`Admission.cs:10-16`); every call carries the world id it was
  made in (`GameLink.cs:8-19`);
- answers every call as a `GameReply`: `Ok` (result, `shaped`, `frame`, `elapsed_ms`, `queue_ms`, `budget_hit`),
  `Refused` (code, message, data, `maybe_ran`) or `Unreachable` (message, `maybe_ran`); none is thrown
  (`GameLink.Seam.cs:123-186`);
- resends only what is safe after a break: a call never written goes again; a written call with no reply is sent
  again, once, only when it reads at its arguments and has no `x-effects`; an error reply is never resent; nothing is
  sent into another world (`StationGodClient.cs:14-20`). The catalogue decides what reads (`GameCatalogue.cs:65-69`,
  `:82-104`);
- carries the catalogue it was built with and fetches the mod's when the `welcome` hash differs, then uses that for
  effective classes (`x-effects`) and its deadline grace (`x-cost`'s duration) (`GameCatalogue.cs:50-60`, `:65-115`;
  `GameLink.cs:141-146`, `:763-800`);
- owns `output_file`: checked before the call is sent, written on the caller's machine, pruned (7 days, newest 200),
  answered as a pointer `{output_file, bytes, tool, counts, summary, in_file_only, truncated}`; a write that fails
  answers the reply with `output_file_error` added (`OutputFiles.cs:8-10`, `:76-85`, `:121-148`);
- keeps subscriptions: after a reconnect into the same world it subscribes again with the same request; into another
  world it closes and says why (`Subscription.cs:8-18`).

The sidecar adds a safety net on top: a reply larger than `--inline-limit-kb` (default 200 KB) is written to a file
like `output_file: true`, with `auto_output_file: true` added (`SidecarOptions.cs:19`, `McpAdapter.cs:433-457`).

`StationGodClient` is the standalone public class in the same project with the full reconnect and subscription
bookkeeping; its calls come back as a `CallOutcome` — `Answered`, `Refused` or `NoAnswer` (with `MaybeRan` and
`WorldChanged`) — and the tests use it (`CallOutcome.cs:7-50`; `tests/StationGodMCP.Tests/Sidecar/ClientTests.cs`).

## The Python library: sg

### Where it is and how it is installed

`clients/python/sg/`, standard library only, Python 3.10 or later (`clients/python/pyproject.toml`, *requires-python*).

```powershell
py -3 -m pip install -e <StationGodMCP repository>\clients\python    # for programs that import sg
```

A hub does not need it installed: it starts each run worker with the folder that holds `sg` on its command line.

```text
clients/python/
  generate_catalogue.py   writes sg/_catalogue.py, sg/_verbs.py and sg/_verbs.pyi from catalogue/verbs
  sg/
    __init__.py           connect(), close(), the verbs and helpers, Error, Stopped
    _transport.py         hello/welcome, calls matched by id, events; the child hub (Job Object on Windows)
    _session.py           the current transport and run; params.line; error mapping
    _replies.py           Reply, Row, MainList (paging with the hub's `more`)
    _verbs.py, _verbs.pyi generated: one function per verb (not help or run_script); IDE stubs, one overload per what
    _helpers.py           wait, watch, changes, jobs, hold, ref, script, help
    _catalogue.py         generated tables: verbs, whats, write whats, main lists, topics
    lib.py                saved scripts as modules (`from sg.lib import <name>`)
    worker.py             the run_script worker
  tests/                  unittest, a fake hub in tests/fakehub.py
```

### Connecting

```python
import sg

sg.connect()                          # a child hub: StationGodMCP.Server --hub (the argument, STATIONGODMCP_HUB, or PATH)
sg.connect(r"C:\...\StationGodMCP.Server.exe", "--pipe", "StationGodMCP-Test")
sg.close()
```

`sg.connect` starts `<hub> --hub <the given arguments>` (a `.dll` is run with `dotnet`), says `hello` and waits up to
10 seconds for the `welcome` (`clients/python/sg/_transport.py:23-28`, `:263-300`). The child dies with the program:
its stdin is closed at exit, and on Windows it is in a kill-on-close Job Object made before the child starts
(`_transport.py:310-330`). A second `sg.connect()` closes the first connection; inside a run both `connect()` and
`close()` raise, because the run's connection belongs to the hub (`__init__.py:29-57`).

### Calls and replies

- Each verb has a generated function: `sg.find('thing', prefab='*Battery*', near='player', radius=30)`. Options left
  as `None` are not sent; any other option the hub knows may be passed through `**options` (`_calls.py:8-17`).
  `help` is `sg.help`, and `run_script` is not something a script calls (`generate_catalogue.py:29`).
- Every call is the hub's `verb` method with `{verb, what, args, line}`; `line` is the calling line of the run's code,
  `None` in a program (`_session.py:108-124`).
- A reply is a `Reply` (a dict). Iterating one raises `TypeError` naming its main list; every dict in a reply is a
  `Row`, whose `KeyError` carries the keys it has (`_replies.py:1-11`, `:17-38`). The main list is a `MainList`: the
  page the call answered; `walk()` goes through every entry page by page with the hub's `more`, `all()` pages in the
  rest, `more` says entries remain, and nothing else fetches a page (cost design A4): a `for`, `len`, an index, `in`,
  `sorted`, `+`, a copy and `plain()` see the entries fetched so far (`_replies.py`, `MainList`).
- Errors raise `sg.Error(code, message, fix, maybe_ran, call)`; the run's write rule and a stop raise `sg.Stopped`
  (a `BaseException`, so `finally` and `with sg.hold` still run); a report pass ends with a private `BaseException`
  (`_errors.py:1-11`, `:46-53`).

### Helpers

`sg.wait(s)` (inside a run `time.sleep` is `sg.wait`), `sg.watch(reads, until, timeout)` → `(rows, met)`,
`sg.changes(reads, timeout, every=1)`, `sg.jobs(timeout)`, `sg.hold(ids, logic, value=None, after=None)`,
`sg.ref('r7')`, `sg.script(name)`, `sg.help(verb, what)` (`_helpers.py:25`, `:32`, `:69`, `:124`, `:387`, `:440`,
`:445`, `:457`).

- `watch` and `changes` subscribe to the devices topic when the hub's welcome lists `subscriptions` and every read is
  a devices-form read; otherwise they poll `read logic`, 1 s for a light read and 5 s for one that asks atmosphere.
  A refused, ended or world-dropped subscription falls back to polling for the rest of the timeout
  (`_helpers.py:32-68`, `:185-202`).
- `sg.follow("find", what, interval_s=10, **options)` subscribes to the find topic and returns a `Following`
  (`_follow.py`): `.result` the find's reply with every list whole, kept current as `find_update` events arrive
  (removed keys dropped, changed entries replaced, added ones appended, `rest` taken whole), `.as_of_s`, `.next(timeout)`
  the next change or None, `.live`, `.end_reason`, `.close()` (and `with`). There is no polling fallback: a hub without
  `subscriptions` raises `too_old`, a refused subscribe raises the game's error.
- `hold` registers the restore with the mod before it writes, so the mod also applies it at session end or after an
  unresumed disconnect, in the same world, while the target still holds the run's last written value
  (`_helpers.py:387-430`).

### Runs: the worker

The hub pre-starts workers so one is ready when a run comes:

```text
python -s -P -c "<sg.worker.BOOT>" <folder that holds sg>
environment: every PYTHON* variable removed; PYTHONHASHSEED=0, PYTHONIOENCODING=utf-8
```

`sg.worker.BOOT` is `import sys; sys.path.insert(0, sys.argv[1]); import sg.worker; sg.worker.main()`
(`worker.py:47`). `-s` leaves the user site out and `-P` keeps the working directory off `sys.path`; a worker with
hash randomisation on refuses to run, because set and str-keyed order would differ between a report run and its
confirmed run. A worker serves one run and exits: it moves the protocol to private descriptors numbered 100 and up
(fd 0 becomes the null device, fd 1 stderr), says `hello` and takes `run.code` from the `welcome`, sends
`run_analysis` before any code runs, runs the code as a function body, and always sends `run_end` — `done`, `stopped`,
`pass_end` or `failed` — then exits at once (`worker.py:1-45`, `:57-100`). The hub keeps the run's end state itself
and kills the worker after `run_end` ([hub.md](hub.md), *Runs*).

A run also loads saved scripts: `from sg.lib import <name>` or `sg.script('<name>')` reads the store the welcome
names (`run.scripts_dir`, `run.world`), runs the code below the store's header, and shares one module per worker
(`lib.py:1-16`, `:40-60`).

### Tests

`py -3 -m unittest discover -s clients/python/tests` from the repository: the transport, verb calls, replies and
paging, errors, the helpers, the worker, and a test that fails when the generated files are stale against
`catalogue/verbs` (`clients/python/tests/test_generated.py:23-28`).

## The hub sg talks to

`sg` speaks the hub protocol, which is the mod's wire with the hub as the server ([hub.md](hub.md), *The hub
protocol*). The parts a client of it relies on, as the code stands:

- `hello` names protocol `[2]`, the client and the `pipeline` feature; `welcome` is the mod's last welcome with the
  hub's own `client_id` for the caller, `features` plus `pipeline`, and no `resume`/`restores`
  (`HubSession.cs:322-360`; `_transport.py:23-25`, `:75-100`).
- The hub answers `verb` (the pipeline, on the session's face), `ref`, `more`, `wait`, `hold`, and relays
  `subscribe`/`unsubscribe` one for one to the mod; a read-only mod method passes through on the caller's mapped
  stream (`HubSession.cs:8-12`, `:521-535`). A run's session may call `verb`, `ref`, `more`, `wait`, `hold`,
  `subscribe` and `unsubscribe` (`HubSession.cs:41-49`).
- An error is the mod's error object plus, when they apply, `fix` and `maybe_ran`; a write-rule stop carries
  `stopped: true`, a report pass end `pass_end: true` (`_errors.py:1-6`, `:39-53`).
- Events are `update`, `subscription_ended`, `world_changed`, `job_ended` and `game_state`
  (`src/StationGodMCP.Mod/Pure/Subscriptions/SubscriptionViews.cs:55-69`, `:88-105`, `:135`); `on_event` handlers run on
  the reader thread and must not make `sg` calls themselves (`_transport.py:139-158`).

## The dashboard's child hub

The dashboard (StationeersScriptDashboard) vendors `sg` in `stationscript/_vendor/sg`, pinned to a StationGodMCP
commit in `_vendor/SOURCE.txt`, so the dashboard and every script that imports its transport run the same client
whatever interpreter starts them (StationeersScriptDashboard `stationscript/transport.py:1-14`).

`PipeTransport` keeps **one child hub for the whole process**: on first use it calls
`sg.connect(hub_dll, *hub_arguments(...), client="stationscript")`, where the arguments after `--hub` are
`--pipe <name>` or `--host <host> --port <port>`; the shared secret is the hub's own `STATIONGODMCP_SECRET` from the
environment, never an argument. A second child hub for another game is refused, since `sg` has one session and it
would close the first behind its callers' backs (StationeersScriptDashboard `stationscript/transport.py:84-90`,
`:132-140`, `:225-240`). Its `call(verb, params)` sends the hub's `verb` method and returns plain JSON; `subscribe`
gives card reads the devices topic; `on_world_changed` rides the hub's `world_changed` event
(StationeersScriptDashboard `stationscript/transport.py:154-205`).
