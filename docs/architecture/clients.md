# Clients: the libraries, the sidecar, and moving over

[Back to the overview](README.md)

Two official client libraries speak the protocol: one in Python for the dashboard, scripts and test tools, one in C#
inside the sidecar. Both are generated in part from the catalogue and both live in this repository. They own the
things a caller should not have to get right on their own: connecting and signing in, sending several calls at once,
reconnecting, resending only what is safe, walking pages, keeping subscriptions alive, and writing output files on
the caller's machine. This page gives their interfaces, the exact rules for output files, and the steps that move each
existing client over.

## What every library does the same way

**Connecting.** Pipe by name (default `StationGodMCP`) or TCP by host and port (default 8765), as today's sidecar
takes them (`src/StationGodMCP.Server/Program.cs:489-514`; `docs/configuration.md`, *Sidecar options*). Optionally a
client name and a key, the key read from an environment variable named by the caller (default
`STATIONGOD_KEY`), never from an argument, so it does not show in process lists, as the sidecar's secret today
(`docs/configuration.md`). Connect timeouts: 1 second for the pipe (the dashboard's today, StationeersScriptDashboard
`stationscript/transport.py:56`), 3 seconds for TCP (the sidecar's today, `Program.cs:25`). A busy pipe is retried until
the timeout and a missing one too, telling the two apart in the message (`transport.py:120-139`).

**Negotiating.** Send `hello` with protocol `[2]`. If the answer is version 2, use it. If the answer is the old mod's
`method_not_found` with a null id, fall back to version 1 on the same connection ([protocol.md](protocol.md), *Old
clients*): no `hello`, one call at a time, `fields` applied by the library itself, subscriptions emulated by polling
`read_devices` at their interval.

**Calling.** One connection, several calls in flight up to the server's `max_in_flight`; more wait in the library.
A call returns the shaped `result` or raises an error carrying `code`, `message` and `data`.

**Reconnecting.** A broken connection is reopened on the next call (and at once if subscriptions are open), with
backoff from 100 ms doubling to 5 s, as the mod's own pipe listener backs off (`src/StationGodMCP.Mod/StationGodPipeServer.cs:240-259`).
Calls in flight when it broke are handled by one rule, today's dashboard rule (`transport.py:84-110`) with the catalogue
deciding what is a read:

- a call whose line was never written is sent again on the new connection;
- a call that was written and not answered is sent again only if its effective class is read, worked out from the
  catalogue at its arguments ([catalogue.md](catalogue.md), *What each part means*);
- any other call fails with `Unreachable(maybe_ran=True)` and a message saying it may have reached the game.

Errors whose catalogue entry says `resend: always` (`game_timeout`, `cancelled`, `too_many_in_flight`,
`shutting_down`) are not resent by the library; it reports them and the caller decides. A library never resends a
write on its own.

**World changes.** Every library subscribes to the `world` topic. When `world_epoch` moves (or differs after a
reconnect) it tells the caller, because reference ids may now mean other things; the dashboard already rebinds every
card on reconnect for this reason (`stationscript/runner.py:350-360`).

**Catalogue.** Each library carries the catalogue it was built with and fetches the mod's when the hash in `welcome`
differs. It uses the catalogue for effective classes, paging (`x-paging`), shaping lists and `x-duration` deadlines.

## Output files

`output_file` writes a reply to a file on the caller's machine and returns a short pointer instead. It stays in the
clients because the file must land where the caller can read it; with a remote game, the mod is on another machine.
Today it lives in the sidecar (`src/StationGodMCP.Server/ReplyShaping.cs:100-151`,
`src/StationGodMCP.Server/OutputFolder.cs:8-137`); both libraries implement exactly these rules, and the sidecar uses the
C# one.

- **Argument.** `true`: a file named `<method>-<local time yyyyMMdd-HHmmss-fff>-<4 hex characters>.json`. `false`: as if
  absent. A string: the file name; letters, digits, `-`, `_` and `.`, 1 to 80 characters after trimming, not starting
  with `.`, no `..`; `.json` is added unless it ends so, ignoring case. Anything else is `invalid_argument` before the
  call is sent (`ReplyShaping.cs:108-151`).
- **Folder.** The caller's explicit option, else the environment variable `STATIONGODMCP_OUTPUT_DIR`, else
  `%LOCALAPPDATA%\StationGodMCP\output` (`OutputFolder.cs:9-39`). Created if missing.
- **Order.** `fields` is sent to the mod as `shape.fields`; the file holds the reply as it comes back, shaped
  (`ReplyShaping.cs:45-49`). `output_file` itself is never sent to the mod.
- **Content.** The reply's `result`, as indented UTF-8 JSON without escaping of non-ASCII characters
  (`OutputFolder.cs:24-28`). Two libraries may indent differently; files are equal as parsed JSON, not as bytes.
- **Writing.** To a temporary file in the same folder, then renamed over the target, so a reader never sees half a file
  and a named file is replaced whole (`OutputFolder.cs:51-54`).
- **Pointer.** `{output_file: <full path>, bytes: <file size>, tool: <method>, counts: {<list key>: <length>},
  summary: {<key>: <value>}, in_file_only: [<key>]}`: every top-level list's length in `counts`; every other top-level
  value whose JSON text is at most 300 characters in `summary`; the rest named in `in_file_only`, which is left out when
  empty (`OutputFolder.cs:75-113`). The key stays `tool` for compatibility.
- **Pruning.** After each write, delete `*.json` files in the folder older than 7 days, then all but the newest 200,
  never the file just written; a file that cannot be deleted is skipped silently, since another client may hold it
  (`OutputFolder.cs:18-19`, `:116-136`).
- **Failure.** If the file cannot be written, answer the reply itself with `output_file_error` added, because the call
  has run and its reply must not be lost (`OutputFolder.cs:43-65`).
- **Errors.** An error reply is never written to a file.

Both libraries run the same fixtures: `clients/fixtures/output_file/*.json`, each a reply, the arguments and the
expected pointer and file content. The C# tests (`tests/StationGodMCP.Tests/FileOutputTests.cs` today) and the Python
tests load the same folder.

**The sidecar's safety net.** An agent that forgets `output_file` on a huge reply fills its context with it, which
the owner has flagged as a recurring cost (the CheatEngineExpert workspace's standing order on heavy StationGod
payloads). The sidecar therefore writes any reply larger than `--inline-limit-kb` (default 200) to a file as if
`output_file: true` had been given, and adds `auto_output_file: true` to the pointer. `--inline-limit-kb 0` turns it
off. The Python library has no such default: scripts want their data.

## The Python library

### Where it lives and how it is installed

`clients/python/stationgod/` in this repository, standard library only, Python 3.10 or later (the owner's machine has
3.11). Windows named pipes through `ctypes` as the dashboard does today (`transport.py:120-153`); TCP through `socket`.
Installed by path: `py -m pip install -e <this repository>\clients\python`. Released as a zip with each GitHub release,
like the sidecar archives.

```text
clients/python/
  pyproject.toml
  stationgod/
    __init__.py         connect(), Client, errors
    _catalogue.json     copied from catalogue.json by build.ps1
    methods.py          generated: one typed stub per method
    catalogue.py        loading, hashing, effective class, paging data
    connection.py       pipe and TCP, framing, hello/auth, reader thread
    client.py           calls, reconnect, resend rule, v1 fallback
    shaping.py          fields for v1 servers (the sidecar's rules)
    output.py           output files
    subscriptions.py    subscriptions and their mirrors
  tools/generate.py     writes methods.py from the catalogue
  tests/
clients/fixtures/       shared with the C# tests
```

### Interface

```python
import stationgod

game = stationgod.connect()                                   # local pipe StationGodMCP, no key
game = stationgod.connect(pipe="StationGodMCP-Test")          # the test server
game = stationgod.connect(pipe="StationGodMCP", client="dashboard", key_env="STATIONGOD_KEY")
game = stationgod.connect(host="10.8.0.2", port=8765, client="agents", key_env="STATIONGOD_KEY")

game.welcome            # the welcome message as a dict (server.pipe_name, server.world_epoch, level, limits, ...)
game.level              # "read", "write" or "cheat"

# Any method, keyword arguments as the catalogue names them; fields, output_file, limit and max_bytes are shaping.
health = game.call("thing_health", reference_ids=ids, fields=["reference_id", "damage_ratio", "is_broken"])
pointer = game.call("find_things", kind="structure", output_file=True)

# Generated stubs: the same call, with names checked as you type.
health = game.methods.thing_health(reference_ids=ids, fields=["reference_id", "damage_ratio"])

# Every page of a method with x-paging, one entry at a time.
for thing in game.iterate("find_things", kind="structure", page_size=500):
    ...

# Several calls at once from one thread.
futures = [game.call_async("read_logic", reference_id=r, logic_type="On") for r in refs]
values = [f.result() for f in futures]

# Subscriptions.
sub = game.subscribe(items=[{"reference_id": "811234", "logic": ["Temperature", "Pressure"]}],
                     include=["clock"], interval_s=1)
state = sub.state       # the full read_devices result as of sub.seq, kept up to date by events
sub.wait(timeout=5)     # block until the next change, True if one came
sub.on_update(lambda changes, state: ...)   # or a callback, on the library's event thread
sub.close()

game.on_world_changed(lambda epoch: ...)
game.on_call = lambda method, ms, reply_bytes, elapsed_ms, queue_ms: ...   # metering hook for the dashboard
game.close()
```

Errors:

| Exception | When |
| --- | --- |
| `stationgod.GameError(code, message, data)` | The mod answered with an error. Subclasses for codes callers often branch on: `PermissionDenied`, `CheatNotArmed`, `InvalidArgument`, `NotFound` (`thing_not_found`, `device_not_found`, ...). |
| `stationgod.Unreachable(message, maybe_ran)` | No answer: no pipe, the connection broke, or no reply in time. `maybe_ran` is true when the call was written and is not a read. |
| `stationgod.TooOld(message)` | The server cannot do what was asked: a feature not in `welcome.features`, or a method it does not have. |

Threads. A `Client` is safe to use from several threads. One reader thread per connection matches replies to waiting
calls and runs subscription callbacks; a callback that raises is logged and does not stop the reader.

Timeouts. A call waits for its `deadline_ms` (default 30 seconds, plus the method's `x-duration`) plus 5 seconds,
then raises `Unreachable`, the same split today's sidecar makes between "no pipe" and "no reply in time"
(`Program.cs:298-310`).

### Subscriptions in the library

`subscribe` keeps the request. After a reconnect it subscribes again with the same request and replaces `state` with
the new snapshot; `on_update` then receives `changes=None` and the new state, meaning "start over". After
`subscription_ended` with `world_changed`, it does not subscribe again on its own: the caller's ids may be wrong, so
it raises the world-changed callback and marks the subscription closed. On a version-1 server it polls `read_devices`
with the same items at the interval and produces the same `state` and `changes`.

## The C# client and the sidecar

### The client

A new project, `src/StationGodMCP.Client` (net8, no dependencies), referenced by the sidecar and the tests. It holds:

- `StationGodConnection`: one pipe or TCP connection; `hello`, sign-in, a reader loop that completes a
  `TaskCompletionSource` per call id, one write lock, events to subscribers.
- `StationGodClient`: `CallAsync(method, JsonElement parameters, Shape? shape, CancellationToken)` returning a sealed
  result type (a reply or an error, `return-result-not-exception` in the owner's C# rules); reconnect and the resend
  rule; version-1 fallback.
- `Catalogue`: the catalogue as records, the class-rule evaluator, the hash.
- `ReplyShaping`, `FieldSelection`, `OutputFolder`: moved from the sidecar (`src/StationGodMCP.Server/ReplyShaping.cs`,
  `OutputFolder.cs`), `FieldSelection` kept for version-1 servers.

The sidecar's own code already uses records on net8 (`ReplyShaping.cs:12`, `:60`, `:101`, `:125`); the client does
too. The mod targets netstandard2.1 and uses no records, which would need a compiler shim there
(`StationGodMCP.csproj`, `TargetFramework`); its new protocol code uses sealed classes, by the owner's rule against
shims, and no LINQ on per-call or per-frame paths.

### What stays in the sidecar

The sidecar becomes MCP on one side and `StationGodClient` on the other:

- `initialize`, `ping`, `tools/list`, `tools/call`, notifications, as today (`Program.cs:89-154`), with `listChanged:
  true`.
- `tools/list` from the catalogue ([catalogue.md](catalogue.md), *Who reads it*). `ToolDefinitions` (`Program.cs:559`
  onward, about 2,000 lines) is deleted.
- `tools/call`: take `fields`, `output_file` and the safety net's limit out of the arguments; send the call with the rest
  as `params` and `fields` as `shape.fields`; write the file if asked; wrap the result or error in today's MCP result
  shape (`src/StationGodMCP.Server/ToolReplies.cs:25-36`).
- Argument checking is the mod's. The sidecar checks only what it consumes itself (`output_file`, `fields`), so a
  wrong argument gets the mod's `invalid_argument`, which names the nearest declared argument as both do today
  (`src/StationGodMCP.Mod/Api/Shared/DeclaredArguments.cs:66-76`; `src/StationGodMCP.Server/ArgumentCheck.cs:6-17`). While the mod is unreachable, a call cannot run anyway and
  answers `game_unavailable` as today.
- `sample_logic` is a mod method once subscriptions exist ([stages.md](stages.md), stage 9); the sidecar's loop
  (`Program.cs:332-455`) is deleted.
- One connection for the life of the sidecar, opened at the first call (an agent session usually starts before the game,
  so connecting at `initialize` would fail for nothing).
- Options: today's `--pipe`, `--host`, `--port`, `--output-dir`; `--secret-env` keeps its meaning for a version-1 server;
  new `--client <name>`, `--key-env <VAR>` (default `STATIONGOD_KEY`) and `--inline-limit-kb`.

`game_unavailable` keeps its two messages, no pipe answered versus the game took the call and did not reply in time
(`Program.cs:222-310`; README, tool errors).

## Moving existing clients over

Order: the library first, then the clients that import it indirectly through the dashboard's transport, then the
dashboard's own reads, then the test tools. Every step works against both an old and a new mod, so nothing waits for a
deploy.

### The dashboard

The dashboard is a long-running Python process (`py -m stationscript`, StationeersScriptDashboard
`stationscript/__main__.py:19-30`). Its cards, the files in `scripts/`, are reloaded when they change on disk
(`stationscript/runner.py:700-735`); the `stationscript` package is not, and needs a dashboard restart. A card edited in
place can be loaded half-written and break the live card, so cards are edited in a copy outside `scripts/` and moved in
whole, in one step (the owner's standing rule for this repository).

1. **Install the library** into the Python the dashboard runs with (`py -m pip install -e ...\clients\python`).
2. **Transport.** Rewrite `stationscript/transport.py` as a wrapper over `stationgod.Client` with the same names:
   `PipeTransport(name, connect_timeout, meter)` and its `call(method, params)` returning `result`; `GameError(code,
   message)` and `GameUnreachable` as subclasses of the library's errors; `meter.record(method, ms, bytes)` fed from
   the library's `on_call` hook, now with the mod's `elapsed_ms` and `queue_ms` as well. The hand-kept `READ_ONLY` list
   (`transport.py:23-35`) goes: the catalogue decides. Restart the dashboard. Every card and every script that
   imports `PipeTransport` now runs on the library with no other change.
3. **Key.** Issue a `dashboard` key at write level (`stationgod key new dashboard write` in the game console), store it
   in an environment variable for the dashboard's process, and pass `client="dashboard"`. New `__main__` options
   `--pipe`, `--host`, `--port`, `--client`, `--key-env`. Cards that use tools the owner classes as cheat need the
   owner's decision first (overview, *Questions for the owner*); until then the key's level stays at what the owner
   sets.
4. **Reads by subscription.** The runner's `_read` (`runner.py:491-528`) makes one `read_devices` call per gateway per
   card tick. With subscriptions available (`"subscriptions"` in `welcome.features`), the runner instead subscribes once
   per card and gateway when the card binds, with the same items, at the card's interval, in `snapshot` mode, and at
   each tick reads `sub.state` instead of calling. The card's handle API (`h["On"]`, `h.slot(0)`) does not change. When a
   card rebinds (reconnect, reload, world change) its subscriptions are closed and made again. Without the feature, the
   runner keeps calling `read_devices`. This changes only the runner, which needs a restart, not the cards.
5. **Shaping in cards.** Cards that read large replies for a few keys pass `fields`, in a copy moved in whole:
   `solar_tracker.py:185` and `smelter.py:2282` (`thing_health` by ids), `base_damage.py:28` (`thing_health` scan),
   `locker_sort.py`'s `item_totals` and `find_items` reads, `plants.py`. Each is a separate small edit, checked against
   the card's tests in `tests/`.
6. **Metrics.** `stationscript/metrics.py` shows mod time and queue time beside the round trip per method.

### Scripts that use the dashboard's transport

The CheatEngineExpert tools under `Cheats/Stationeers/tools` construct `PipeTransport` directly (for example
`rocket/flight_log.py:10`, `rocket/burn_watch.py:17`, `rocket/oxidiser_watch.py:25`, `coolant_watch.py:16`,
`feed_vault.py:63`, `sort_storage.py:165`, `layout_solver/game.py:40`, `rocket_rebuild/rb.py:16`,
`power/finish_when_printed.py:20`). After dashboard step 2 they run on the library unchanged, at the anonymous pipe
level. Optional follow-ups, each its own small change: `fields` on `burn_watch.py`'s `thing_health` scan and
`oxidiser_watch.py`'s `find_things` pages; a subscription instead of the one-second `read_logic_many` loop in
`flight_log.py`.

### The test server's tools

`StationeersTestServer/tsclient.py` keeps its `Client(pipe_name)`, `call`, `console`, `client()`, `ensure_running` and
`wait_ready`, rewritten over the library. Its guard stays: refuse the default pipe name, and refuse a server whose
`welcome.server.pipe_name` (or, on a version-1 server, `mod_info.pipe_name`) is not the test pipe (`tsclient.py:90-101`).
The leak test drives the test server through `tsclient` (TerraformingReloaded `tools/LeakTest/leaktest.py:67-68`,
`:100`, `:116-126`) and needs no change. `livetest/lt.py` drives the real sidecar over MCP (`livetest/lt.py:1-30`) and
needs no change either; `setup.ps1` already copies the new sidecar into the test server's `sidecar` folder. LiveCheck is
a plugin inside the server, not a client (TerraformingReloaded `tools/LiveCheck/Watch.cs:19`), and is untouched.

The test server has its own config folder, so its own clients file and access settings; testers use the anonymous
pipe there at cheat level, as today.

### Agents

Nothing to do in their registrations. Agents that should not cheat by default get `--client agents --key-env
STATIONGOD_KEY` with a cheat-level key whose cheat is armed by the owner in game when wanted, once the owner has
answered the questions in the overview.
