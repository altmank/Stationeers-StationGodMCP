# Clients: the libraries, the sidecar, and moving over

[Back to the overview](README.md)

Two official client libraries speak the protocol: one in Python for the dashboard, scripts and test tools, one in C#
inside the sidecar. Both live in this repository. They own the things a caller should not have to get right on their
own: connecting and signing in, sending several calls at once, reconnecting, resending only what is safe, walking
pages, keeping subscriptions alive, and writing output files on the caller's machine. This page gives their
interfaces, the exact rules for output files, and the steps that move each existing client over.

## What every library does the same way

**Connecting.** Pipe by name (default `StationGodMCP`) or TCP by host and port (default 8765), as today's sidecar
takes them (`src/StationGodMCP.Server/Program.cs:489-514`; `docs/configuration.md`, *Sidecar options*). The pipe is
opened for overlapped I/O, so a pending read never blocks a write on the same connection ([protocol.md](protocol.md),
*Transports*); today's Python clients open it synchronously (StationeersScriptDashboard `stationscript/transport.py:125`).
Connect timeouts: 1 second for the pipe (the dashboard's today, `transport.py:56`), 3 seconds for TCP (the sidecar's
today, `Program.cs:25`). A busy pipe is retried until the timeout and a missing one too, telling the two apart in the
message (`transport.py:120-139`).

**Keys.** Optionally a client name and a key. The key is read from an environment variable, never from an argument, so
it does not show in process lists, as the sidecar's secret today (`docs/configuration.md`). The variable's default name
depends on the target, so a process set up for the live game never offers its key to the test server or the other way
round: `STATIONGOD_KEY_<PIPE>` for a pipe and `STATIONGOD_KEY_<HOST>_<PORT>` for TCP, with every character that is not
a letter or digit replaced by `_` and letters in upper case (`STATIONGOD_KEY_STATIONGODMCP`,
`STATIONGOD_KEY_STATIONGODMCP_TEST`). A caller may name another variable.

**Negotiating.** With `protocol="auto"` (the default), send `hello` with protocol `[2]`; if the answer is version 2,
use it; if it is the old mod's `method_not_found` with a null id, fall back to version 1 on the same connection
([protocol.md](protocol.md), *Old clients*). With `protocol="v1"`, speak version 1 from the start: one call at a time,
today's lenient argument handling, and `shape` still sent. On version 1, the library applies `fields` itself only to a
reply not marked `shaped` (an old mod).

**Calling.** One connection, several calls in flight up to the server's `max_in_flight`; more wait in the library.
A call returns the shaped `result` or raises an error carrying `code`, `message` and `data`.

**Reconnecting.** A broken connection is reopened on the next call (and at once if subscriptions are open), with
backoff from 100 ms doubling to 5 s, as the mod's own pipe listener backs off (`src/StationGodMCP.Mod/StationGodPipeServer.cs:240-259`).
Calls in flight when it broke are handled by today's dashboard rule (`transport.py:84-110`), with the catalogue
deciding what is a read:

- a call whose line was never written is sent again on the new connection;
- a call that was written and not answered is sent again only if its effective class is read and its method has no
  `x-effects` ([catalogue.md](catalogue.md), *What each part means*), so `highlight` and `show_preview`, which draw, are
  never sent twice, as the dashboard's list leaves them out today (`transport.py:23`);
- nothing is sent again unless the new `welcome.server.world.id` equals the last one seen;
- any other call fails with `Unreachable(maybe_ran=True)` and a message saying it may have reached the game.

The libraries never resend a call because of an error reply, whatever the code; the error goes to the caller, whom the
catalogue's `caller_may_resend` advises.

**World changes.** Every library subscribes to the `world` topic. When `world.id` changes, or differs after a
reconnect, it tells the caller, because reference ids may now mean other things; the dashboard already rebinds every
card on reconnect for this reason (`stationscript/runner.py:350-360`).

**Catalogue.** Each library carries the catalogue it was built with and fetches the mod's when the hash in `welcome`
differs. It uses the catalogue for effective classes, `x-effects`, paging (`x-paging`) and `x-duration` deadlines.

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

`clients/python/stationgod/` in this repository, standard library only, Python 3.10 or later. On the owner's machine
it is installed into, and run by, Python 3.12 started as `py -3.12` (the default `py`); `python` on the PATH there is
another tool's private environment and is not used. Every install and run command in this plan names `py -3.12`.

```powershell
py -3.12 -m pip install -e <StationGodMCP repository>\clients\python
```

Windows named pipes through `ctypes` with overlapped I/O (`CreateFileW` with `FILE_FLAG_OVERLAPPED`, `ReadFile` and
`WriteFile` with an `OVERLAPPED` structure and an event, `GetOverlappedResult`); TCP through `socket`. Released as a zip
with each GitHub release, like the sidecar archives.

```text
clients/python/
  pyproject.toml
  stationgod/
    __init__.py         connect(), Client, errors
    _catalogue.json     copied from catalogue.json by build.ps1
    catalogue.py        loading, effective class, paging data
    pipe.py             overlapped named-pipe I/O
    connection.py       pipe and TCP, framing, hello/auth, reader thread
    client.py           calls, reconnect, resend rule, version-1 fallback
    shaping.py          fields for old mods (the sidecar's rules)
    output.py           output files
    subscriptions.py    subscriptions
  tests/
clients/fixtures/       shared with the C# tests
```

### Interface

```python
import stationgod

game = stationgod.connect()                                   # local pipe StationGodMCP, no key
game = stationgod.connect(pipe="StationGodMCP-Test")          # the test server
game = stationgod.connect(pipe="StationGodMCP", client="dashboard")            # key from STATIONGOD_KEY_STATIONGODMCP
game = stationgod.connect(host="10.8.0.2", port=8765, client="agents", key_env="MY_KEY")
game = stationgod.connect(protocol="v1")                      # today's protocol, lenient checking

game.welcome            # the welcome message as a dict (server.pipe_name, server.world, level, limits, ...)
game.level              # "read", "write" or "cheat"

# Any method, keyword arguments as the catalogue names them; fields, output_file, limit and max_bytes are shaping.
health = game.call("thing_health", reference_ids=ids, fields=["reference_id", "damage_ratio", "is_broken"])
pointer = game.call("find_things", kind="structure", output_file=True)

# Every page of a method with x-paging, one entry at a time (not a snapshot; see the method's order).
for thing in game.iterate("find_things", kind="structure", page_size=500):
    ...

# Several calls at once from one thread.
futures = [game.call_async("read_logic", reference_id=r, logic_type="On") for r in refs]
values = [f.result() for f in futures]

# Subscriptions.
sub = game.subscribe(items=[{"reference_id": "811234", "logic": ["Temperature", "Pressure"]}],
                     include=["clock"], interval_s=1)
state = sub.state       # the last read_devices result received, with sub.seq and sub.frame
sub.wait(timeout=5)     # block until the next update, True if one came
sub.on_update(lambda state: ...)   # or a callback, on the library's event thread
sub.close()

game.on_world_changed(lambda world: ...)
game.on_call = lambda method, ms, reply_bytes, elapsed_ms, queue_ms: ...   # metering hook for the dashboard
game.close()
```

Errors:

| Exception | When |
| --- | --- |
| `stationgod.GameError(code, message, data)` | The mod answered with an error. Subclasses for codes callers often branch on: `PermissionDenied`, `CheatNotArmed`, `InvalidArgument`, `NotFound` (`thing_not_found`, `device_not_found`, ...), `SubscriptionRefused` (`subscription_limit`). |
| `stationgod.Unreachable(message, maybe_ran)` | No answer: no pipe, the connection broke, or no reply in time. `maybe_ran` is true when the call was written and is not a read. |
| `stationgod.TooOld(message)` | The server cannot do what was asked: a feature not in `welcome.features` (subscriptions on an old mod), or a method it does not have. |

Threads. A `Client` is safe to use from several threads. One reader thread per connection matches replies to waiting
calls and runs subscription callbacks; a callback that raises is logged and does not stop the reader.

Timeouts. A call waits for its `deadline_ms` (default 30 seconds, plus the method's `x-duration` at the arguments
given) plus 5 seconds, then raises `Unreachable`, the same split today's sidecar makes between "no pipe" and "no reply
in time" (`Program.cs:298-310`).

### Subscriptions in the library

`subscribe` keeps the request. After a reconnect it subscribes again with the same request only if
`welcome.server.world.id` is unchanged, and replaces `state` with the new first reading. If the world changed, or the
mod ended the subscription with `world_changed`, it closes the subscription and raises the world-changed callback,
because the caller's ids may be wrong. A `subscribe` refused with `subscription_limit` raises `SubscriptionRefused`; on
a version-1 server `subscribe` raises `TooOld`. In both cases the caller polls `read_devices` with the same items; the
dashboard's runner already has that path.

### As built in stage 7

The library follows the owner's rule that it stays a thin client: connecting, negotiating, signing in, the resend
rule, subscription bookkeeping and output files, plus what is generated from the catalogue. Everything else is the
mod's. Where the build differs from the text above, or the protocol left a choice open, this records it.

**What is generated, and what is not.**
- `clients/python/generate_catalogue.py` writes `stationgod/_methods.py` from `catalogue.json`: the catalogue's hash,
  a table per method (class, `x-class-when`, `x-effects`, `x-paging`, `x-duration`, declared and required argument
  names) and one stub per method on the client (`game.thing_health(reference_ids=[...])`; a Python keyword gets a
  trailing underscore, `move_gas(from_=...)`). `call(method, **params)` still works for any method. A test fails when
  the module is stale, so whoever changes `catalogue.json` reruns the generator. This replaces `_catalogue.json`:
  the package carries the table, not the whole file, and `build.ps1` does not need to copy anything.
- Before sending on version 2 the client refuses, with `invalid_argument` and `data.checked_by: "client"`, an argument
  name the method does not declare (naming the nearest, by the mod's own rule) and a required argument left out.
  Types, ranges and patterns are left to the mod. Version 1 sends everything unchecked, as today.
- The library never shapes a reply. It sends `fields`, `limit` and `max_bytes` as `shape` (on version 2 only when
  `welcome.features` lists `shape`; on version 1 always) and returns what comes back. Against a mod that does not
  shape, the reply comes back whole. This replaces "applies `fields` itself" above.
- `limit` given as an object is `shape.limit`; given as an integer it is the method's own argument.
- The class rules are evaluated only for the resend rule, never to refuse a call.

**Negotiating and signing in.**
- `hello` always carries `client` (`name`: the given client name or `stationgod-py`; `version`; `library`
  `stationgod-py/<version>`) and `features`. It carries `auth: "key"` only when a client name is given and its key
  variable is set; a name without a key connects anonymously under that name.
- Any first answer without a `type` key means an old mod. On the pipe the library speaks version 1 on the same
  connection. On TCP, where the old mod closes after its refused sign-in, it reconnects with the legacy secret from the
  environment variable `secret_env` (default `STATIONGODMCP_SECRET`, as the sidecar's) and otherwise raises `TooOld`.
- A version-1 connection has one call in flight. Replies are matched by the echoed `id`; a version-1 reply with a null
  id goes to the one call in flight.
- `close()` sends `bye` on version 2.

**Calls, timeouts and resending.**
- `deadline_ms` is sent only when the caller gives it; the mod adds `x-duration` itself. The client waits its connect
  timeout plus the deadline (30 s by default) plus `x-duration` (the argument given, else the method's `max_s`) plus
  5 s from when the call was made, then raises `Unreachable` and sends `cancel` when the server lists it.
- A written read is resent at most once; broken twice, it fails with `maybe_ran` false.
- `maybe_ran` is true when the call was written and is not safe to resend, so a written `highlight` reports true.
- In another world (a different `world.id` after reconnecting) nothing waiting is sent, written or not: those calls
  raise `WorldChanged`, a subclass of `Unreachable`.
- A broken connection is reopened by a caller's next call at once. The backoff (100 ms doubling to 5 s) paces only the
  library's own reconnecting, which runs while calls wait to be resent or subscriptions are open.
- When `welcome.catalogue.hash` (compared as the text `sha256:<lowercase hex>`) differs from the built-in one, the
  client calls `catalogue` once per hash on the new connection and uses its table; if that fails it keeps the built-in
  one. `on_call` is not called for the library's own calls (this fetch and the `world` subscription).

**World changes and subscriptions.**
- The world-changed callback fires once per new `world.id`: from a `world_changed` event (the connection event and
  the `world` topic's event are the same name and are told apart by nothing else) or from the welcome after a
  reconnect. It closes every open subscription with `end_reason` `world_changed`. A `subscription_ended` event closes
  that one subscription and wakes its waiters, without firing the callback, which the `world_changed` event does.
- A `game_state` event is read from its `game_state` key.
- The subscribe reply is bound to its subscription on the reader thread before the caller wakes, so an `update` sent
  straight after the reply is not lost.

**Tests.** Standard library only: `py -3.12 -m unittest discover -s clients/python/tests` (pytest also runs them).
The fake mod in `tests/fakemod.py` speaks both versions over loopback TCP and a real overlapped pipe. The live tests
run when `STATIONGOD_LIVE_PIPE` names the test server's pipe and refuse the default one. The shared fixtures are
`clients/fixtures/output_file/*.json` (the argument, the result as it came back, the expected file name or name
pattern, pointer without its path and byte count, and file content; or the expected error) and
`clients/fixtures/hmac/vectors.json`. A pointer's `summary` limit counts the compact JSON text of each value.

## The C# client and the sidecar

### The client

A new project, `src/StationGodMCP.Client` (net8, no dependencies), referenced by the sidecar and the tests. It holds:

- `StationGodConnection`: one pipe (`NamedPipeClientStream` with `PipeOptions.Asynchronous`, as the sidecar opens it
  today, `Program.cs:225`) or TCP connection; `hello`, sign-in, a reader loop that completes a `TaskCompletionSource`
  per call id, one write lock, events to subscribers.
- `StationGodClient`: `CallAsync(method, JsonElement parameters, Shape? shape, CancellationToken)` returning a sealed
  result type (a reply or an error, `return-result-not-exception` in the owner's C# rules); reconnect and the resend
  rule; version-1 fallback.
- `Catalogue`: the catalogue as records, the class-rule evaluator, the hash.
- `ReplyShaping`, `FieldSelection`, `OutputFolder`: moved from the sidecar (`src/StationGodMCP.Server/ReplyShaping.cs`,
  `OutputFolder.cs`); `FieldSelection` applies only to replies not marked `shaped`.
- `ArgumentCheck` (`src/StationGodMCP.Server/ArgumentCheck.cs`): kept for the version-1 fallback, so a new sidecar
  talking to an old mod still checks types and enums, until the old protocol is switched off.

The sidecar's own code already uses records on net8 (`ReplyShaping.cs:12`, `:60`, `:101`, `:125`); the client does
too. The mod targets netstandard2.1 (`StationGodMCP.csproj`, `TargetFramework`) and uses no records; its new code uses
neither records nor `init` accessors nor `required` members, all of which would need compiler shims there
(`IsExternalInit`, `RequiredMemberAttribute`), by the owner's rule against shims, and no LINQ on per-call or per-frame
paths (already enforced by `BannedSymbols.txt`).

### What stays in the sidecar

The sidecar becomes MCP on one side and `StationGodClient` on the other:

- `initialize`, `ping`, `tools/list`, `tools/call`, notifications, as today (`Program.cs:89-154`), with `listChanged:
  true`.
- `tools/list` from the catalogue ([catalogue.md](catalogue.md), *Who reads it*). `ToolDefinitions` (`Program.cs:559`
  onward, about 2,000 lines) is deleted.
- `tools/call`: take `fields`, `output_file` and the safety net's limit out of the arguments; send the call with the rest
  as `params` and `fields` as `shape.fields`; apply `fields` locally only if the reply is not marked `shaped`; write the
  file if asked; wrap the result or error in today's MCP result shape
  (`src/StationGodMCP.Server/ToolReplies.cs:25-36`).
- Argument checking on version 2 is the mod's: a wrong argument gets the mod's `invalid_argument`, which names the
  nearest declared argument as both check today (`src/StationGodMCP.Mod/Api/Shared/DeclaredArguments.cs:66-76`;
  `src/StationGodMCP.Server/ArgumentCheck.cs:6-17`). On version 1 the sidecar checks with `ArgumentCheck` as today.
- `sample_logic` is a mod method from stage 12 ([stages.md](stages.md)); against an older mod, the sidecar keeps its
  loop (`Program.cs:332-455`) until the old protocol is switched off.
- One connection for the life of the sidecar, opened at the first call (an agent session usually starts before the game,
  so connecting at `initialize` would fail for nothing).
- Options: today's `--pipe`, `--host`, `--port`, `--output-dir`; `--secret-env` keeps its meaning for a version-1 server;
  new `--client <name>`, `--key-env <VAR>` (default as above) and `--inline-limit-kb`.
- A second use of the same program, run by the owner on the host: `StationGodMCP.Server.exe key new <name> <level>
  [--config <BepInEx config folder>]` adds a client with a fresh key to the clients file and prints the key once to this
  terminal ([protocol.md](protocol.md), *Keys*).

`game_unavailable` keeps its two messages, no pipe answered versus the game took the call and did not reply in time
(`Program.cs:222-310`; README, tool errors).

### As built in stage 9

The owner's rule for this stage was the same as for the Python library: the client stays thin (connecting,
negotiating, signing in, the resend rule, subscription bookkeeping, output files) and the sidecar only translates.
Where the build differs from the text above, or the text left a choice open, this records it.

**The client.**
- `src/StationGodMCP.Client` (net8, no dependencies, warnings as errors) embeds `catalogue.json`; the sidecar no longer
  embeds its own copy and builds its tools from the client's.
- `StationGodClient.CallAsync(method, params, shape, deadlineMs, cancellation)` returns a `CallOutcome`: `Answered`
  (result and the `shaped` mark), `Refused` (the error object, the mod's or the sign-in's) or `NoAnswer` (message,
  `MaybeRan`, `WorldChanged`). `SubscribeAsync` returns `Subscribed`, `Unsupported` (version 1, or no `subscriptions`
  feature) or `Failed`. The client raises `CatalogueChanged`, `WorldChanged` and `EventReceived`, and keeps `Protocol`,
  `Welcome`, `World` and `GameState`. `GameTarget` is `Pipe` or `Tcp`, each with its default key variable; `KeyProof`
  makes the proof; `GameCatalogue` answers the hash, the effective class, `ResendSafe` and `Duration`.
- It follows the Python library's resolutions: an old mod is any first answer without a `type` key; over TCP the client
  then reconnects with the legacy secret, and without one answers `unauthorized` naming the variable (there is no
  separate "too old" outcome); the hash is the text `sha256:<lowercase hex>`, fetched once per hash, with the built-in
  catalogue kept when the fetch fails and used on version 1; the world-changed event fires once per new `world.id`,
  from the event or from a welcome after a reconnect, and closes every subscription; a `game_state` event is read from
  its `game_state` key; `deadline_ms` is sent only when given; `hello` names the given client or `stationgod-cs`, and
  offers a key only when a client name is given and its variable is set.
- Each call carries its own resend rule instead of a shared requeue: a call never written is tried on the next
  connection; a written read without `x-effects` is sent once more; a call waiting to be sent again keeps trying to
  connect with the backoff until its time runs out, and is not sent into another world. Each try gets a fresh id. The
  client's own reconnect loop runs only while subscriptions are open.
- Connect timeouts are the text's: 1 second for the pipe (the old sidecar waited 3) and 3 for TCP. The "no pipe
  answered" message keeps its wording with the new number.
- The client never shapes a reply. `FieldSelection` did not move into it.

**The sidecar.**
- `ReplyShaping` and `OutputFolder` are gone from it. `output_file` is the client's (`OutputChoice`, `OutputTarget`,
  `OutputFolder`), checked against the shared fixtures. A reply from the mod is passed on as it came, marked `shaped`
  or not: the mod owns shaping, so against a mod older than stage 1 `fields` is ignored rather than applied here. This
  replaces "fields applied locally" in stage 9's acceptance. `FieldSelection` stays in the sidecar only for the one reply
  it builds itself, `sample_logic`, and as the reference the mod's shaping tests compare against.
- `ArgumentCheck` stays in the sidecar, not the client, since it checks MCP schemas. It runs until a connection has said
  version 2: on version 1 and before the first connection, so a bad argument is still refused with no game running. On
  version 2 only the sidecar's own `fields` and `output_file` are checked here, and the arguments go to the mod as given.
- On version 2 the sidecar sends only the `fields` selectors that follow the grammar (trimmed, each once) and adds the
  others to the reply's `fields_unmatched` itself, so no selector accepted today is refused. If none follows the grammar,
  no shape is sent and the reply comes whole with all of them unmatched.
- `sample_logic` runs in the sidecar while the catalogue in use marks it `x-runs-in: sidecar`, and is forwarded once
  the mod's catalogue does not, so stage 12 needs no sidecar change.
- MCP messages are handled as they arrive, several at once; each response or notification is written as one line. At
  the end of input the sidecar waits for its calls in flight, then says `bye` on version 2.
- The inline limit compares the UTF-8 bytes of the result's JSON text. A pointer written by it carries
  `auto_output_file: true`; a write that fails answers inline with `output_file_error` and no flag.
- `--host` no longer needs the secret at start: a version-2 server signs in with a key, and the secret is asked for
  only when an old mod over TCP needs it.
- `key new` is stage 6's and not part of this stage.

**Tests.** `tests/StationGodMCP.Tests/Sidecar/`: `FakeGame` (an in-process game on a real overlapped pipe or loopback
TCP, speaking the old protocol or version 2 with the key challenge, calls answered out of order, events pushed),
`ClientTests` and `SidecarTranscriptTests`. The output-file fixtures and HMAC vectors are the shared ones, and the
built-in hash is checked against the one the Python generator wrote into `_methods.py`. The live checks have not run:
the owner's game was running when this stage was built.

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

1. **Install the library** into Python 3.12 (`py -3.12 -m pip install -e ...\clients\python`), the interpreter
   `py -m stationscript` runs.
2. **Transport.** Rewrite `stationscript/transport.py` as a wrapper over `stationgod.Client` with the same names:
   `PipeTransport(name, connect_timeout, meter)` and its `call(method, params)` returning `result`; `GameError(code,
   message)` and `GameUnreachable` as subclasses of the library's errors; `meter.record(method, ms, bytes)` fed from
   the library's `on_call` hook, now with the mod's `elapsed_ms` and `queue_ms` as well. The hand-kept `READ_ONLY` list
   (`transport.py:23-35`) goes: the catalogue decides. Finding the library: `import stationgod`; if that fails, the
   wrapper adds the library's source folder to `sys.path` (from the environment variable `STATIONGOD_CLIENT_PATH`, else
   the sibling checkout `..\StationeersMod\StationGodMCP\clients\python` relative to the dashboard's folder) and tries
   again; if it still fails, it keeps today's pipe code, moved unchanged into `stationscript/transport_v1.py`, and logs
   once that it did. The wrapper connects with `protocol="v1"`: today's lenient argument handling, with shaping. The pipe name comes from `PipeTransport`'s argument, else the environment variable
   `STATIONSCRIPT_PIPE`, else `StationGodMCP`.
   Restart the dashboard. Every card and every script that imports `PipeTransport` now runs on the library with no
   other change.
3. **The replay check, then version 2.** A test in this repository, `CatalogueReplayTests`, sends a committed set of
   calls through the mod's own version-2 validator; it must refuse none. The set is written by hand from the calls the
   cards and the scripts below make (method and the params their code builds), plus synthetic calls the validator must
   refuse. Each refusal is fixed in the card or the catalogue first. Only then does the wrapper switch to
   `protocol="auto"`.
4. **Key.** Make a `dashboard` key at write level on the host (`StationGodMCP.Server.exe key new dashboard write`), put
   it in `STATIONGOD_KEY_STATIONGODMCP` for the dashboard's process, and pass `client="dashboard"`. New `__main__` options
   `--pipe`, `--game-host`, `--game-port` (the existing `--port` is the dashboard's own web port,
   `stationscript/__main__.py:20`), `--client`, `--key-env`. Cards that use tools the owner classes as cheat need the
   owner's decision first (overview, *Questions for the owner*).
5. **Reads by subscription** (after the mod has subscriptions). The runner's `_read` (`runner.py:491-528`) makes one
   `read_devices` call per gateway per card tick, split further when a gateway's items pass one call's bounds
   (`runner.py:512`; `stationscript/station.py:62-74`). With subscriptions available (`"subscriptions"` in
   `welcome.features`), the runner instead subscribes once per card, gateway and chunk when the card binds, with the
   same items, at the card's interval, and at each tick reads `sub.state` instead of calling. A chunk refused with
   `SubscriptionRefused`, or a server without subscriptions, keeps calling `read_devices` for that chunk. The card's
   handle API (`h["On"]`, `h.slot(0)`) does not change. When a card rebinds (reconnect, reload, world change) its
   subscriptions are closed and made again. This changes only the runner, which needs a restart, not the cards. The
   cards' other calls (`atmosphere_contents`, `inspect_slots`, `thing_health` and the rest) stay polls.
6. **Shaping in cards.** Cards that read large replies for a few keys pass `fields`, in a copy moved in whole:
   `solar_tracker.py:185` and `smelter.py:2282` (`thing_health` by ids), `base_damage.py:28` (`thing_health` scan),
   `locker_sort.py`'s `item_totals` and `find_items` reads, `plants.py`. Each is a separate small edit, checked against
   the card's tests in `tests/`.
7. **Metrics.** `stationscript/metrics.py` shows mod time and queue time beside the round trip per method.

### Scripts that use the dashboard's transport

Thirteen CheatEngineExpert scripts under `Cheats/Stationeers/tools` construct `PipeTransport`: `arc_smelt.py`,
`co2_trickle.py`, `coolant_watch.py`, `feed_vault.py`, `heat_load.py`, `layout_solver/game.py`,
`power/finish_when_printed.py`, `rocket/burn_watch.py`, `rocket/flight_log.py`, `rocket/oxidiser_watch.py`,
`rocket_rebuild/rb.py`, `smelt_batch.py` and `sort_storage.py`. They reach the dashboard's package by adding its folder
to `sys.path` (for example `rocket/flight_log.py:3-4`, `layout_solver/game.py:11-12`). After dashboard step 2 they run
on the library unchanged, on version 1 with lenient checking, at the anonymous pipe level, and are started with
`py -3.12`. Started from another interpreter, the wrapper's path fallback still finds the library's sources. Optional
follow-ups, each its own small change: `fields` on `burn_watch.py`'s `thing_health` scan and `oxidiser_watch.py`'s
`find_things` pages.

### The test server's tools

StationeersTestServer `tsclient.py` keeps its exports, `Client(pipe_name)` with `call` and `console`, `client()`,
`config()`, `ModError`, `Unreachable`, `ensure_running`, `wait_ready`, `process_alive` and `server_pid`, rewritten over
the library. Its guard stays in `client()`: refuse the default pipe name, and refuse a server whose
`welcome.server.pipe_name` (or, on a version-1 server, `mod_info.pipe_name`) is not the test pipe (`tsclient.py:90-101`).
The leak test drives the test server through `tsclient`, using `client()`, `ensure_running` and `ModError`
(TerraformingReloaded `tools/LeakTest/leaktest.py:67-68`, `:100-101`, `:116-126`), and needs no change.
`livetest/lt.py` drives the real sidecar over MCP (`livetest/lt.py:1-30`) and needs no change either; `setup.ps1`
already copies the new sidecar into the test server's `sidecar` folder. LiveCheck is a plugin inside the server, not a
client (TerraformingReloaded `tools/LiveCheck/Watch.cs:19`), and is untouched.

The test server has its own config folder, so its own clients file and access settings; testers use the anonymous
pipe there at cheat level with standing cheat, as today.

### Agents

Nothing to do in their registrations. Agents that should not cheat by default get `--client agents` with a
cheat-level key whose cheat the owner approves in game when wanted, once the owner has answered the questions in the
overview.
