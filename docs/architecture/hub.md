# The hub

[Back to the overview](README.md)

The hub is the sidecar binary (`StationGodMCP.Server`) in its next role: the one program between agents, scripts and
programs on one side and the mod on the other. It holds one game connection and gives every caller its own stream on
it. This page is the contract the hub's parts are built to: the hub's wire protocol (what a script worker, a program's
`sg.connect()` and the dashboard speak), the worker's messages, the journal file, the recording, and the C# seams
between the parts. The design and its reasons are in PLAN v9 (sections 2.0 to 2.4) and SPEC-NOTES; what this page
describes is the built system, and the code wins where the two differ.

## Modes

| Command line | Upstream (callers) | Adopts journals |
| --- | --- | --- |
| `StationGodMCP.Server` (default) | MCP on stdio: the 15 verbs, help and run_script | yes |
| `StationGodMCP.Server --hub` | the hub protocol on stdio (below): a child hub for one program | never |

Both modes hold one `GameLink` (the mod connection) and one `VerbPipeline`. MCP mode adds one `RunHost` and one
journal (`src/StationGodMCP.Server/Program.cs:58-74`); `--hub` serves no runs and keeps none, only a pipeline with
its script store (`Hub/HubMode.cs:11-20`). Script
workers connect to their hub over their own stdin and stdout and speak the hub protocol too, so `sg` has one
transport.

Options added to today's (`docs/configuration.md`, Sidecar options):
- `--hub`: the mode above.
- `--confirmed-ceiling-s <n>`: the longest a confirmed run's reply (and `inspect job` on a live confirmed run) waits,
  whatever `max_wait` says. Default 30; 0 turns the ceiling off (phase 2's scripts arm, PLAN 3.2).
- `--record <path>`: append the recording (below) to this file. Off when absent.
- `--journal-dir <path>`: where journals live. Default `%LOCALAPPDATA%\StationGodMCP\journal` (Windows) or
  `$XDG_STATE_HOME/stationgodmcp/journal`.
- `--python <path>`: the Python for script workers. Default `py -3` on Windows, else `python3`.
- `--scripts-dir <path>`: where the saved scripts live (`shared/` and `world/<world id>/`); the four `script` whats
  and `run_script {name}` need it (`SidecarOptions.cs`).

## Streams

The hub numbers streams upward from 1 and never reuses one (the mod refuses an ended stream, `stream_ended`).
- Stream 0 is the MCP client's direct calls.
- Each script run gets a new stream at start and ends it (`stream_end`) after reading its final `stream_costs`.
- Each hub-protocol caller's stream `n` (a program, the dashboard) is mapped to a fresh hub stream; its
  `stream_end n` ends the mapped one.

Admission: the hub keeps at most `limits.max_in_flight_per_stream` calls in flight per stream and
`max_in_flight_per_connection` in all. Calls waiting for a slot are admitted round robin by stream, stream 0 first in
each round. Every call carries the world id the hub last saw (`welcome.server.world.id`, then each `world_changed`).

Resume: the hub sends `resume: "hub-<16 hex>"` (one token per hub process) in every `hello`. After a break it
reconnects with the same token. A call written but not answered across a break answers `unreachable` with
`maybe_ran: true`; only calls the catalogue marks read class and free of effects are sent again
([clients.md](clients.md)). `release` and `cursor_close` are sent only when the mod lists `release` and `cursors`,
and this mod does: its kept jobs carry pinned snapshots and final statuses
(`src/StationGodMCP.Client/GameLink.cs:437-457`, `src/StationGodMCP.Mod/Pure/Jobs/KeptJobs.cs:9-44`).

## The hub protocol

The mod's wire protocol, version 2 (`protocol.md`), with the hub as the server. Everything there holds unless this
section says otherwise.

- `welcome` is the mod's last welcome as the hub saw it, with `client_id` the hub's own name for the caller,
  `features` the mod's plus `pipeline`, and the link's own `resume` and `restores` keys dropped (the token is the
  link's, not the caller's) (`Hub/HubSession.cs:325-364`). Before the game is reachable the hub answers `hello` with a welcome whose
  `server.game_state` is `Unreachable`, and calls answer `game_unreachable` until it is. A session answered that way
  is sent the mod's welcome once the link first gets it (one extra `welcome`, before any event); `sg` keeps the
  latest welcome, so a watch started after the game answers subscribes where it would otherwise have polled.
- A `call` whose `method` is a mod method is passed to the mod on the caller's mapped stream, its reply passed back
  with the caller's id. `subscribe` and `unsubscribe` are relayed, every topic (`find` too, whose first reply may
  come frames later): the hub holds one mod subscription per caller subscription and passes its events (`update`,
  `find_update`, ...) through with the caller's subscription id. Two callers following the same find or the same
  devices share the mod's one evaluation or read of them (protocol.md, Subscriptions), so the hub keeps nothing shared
  of its own.
- Feature `pipeline` adds these methods, answered by the hub:

| Method | Params | Result |
| --- | --- | --- |
| `verb` | `{verb, what?, args?}` | the verb's reply as the caller's face gives it (below) |
| `ref` | `{id}` | for `rN`: `{ref, ids, reply}` (the shown ids and the whole reply); for `script-N`: `{ref, returned, printed}` |
| `more` | `{cursor, limit?}` | `{entries, cursor?}`: the next entries of a script or program reply's main list |
| `wait` | `{s, kind: "wait" \| "watch", in_loop}` | `{}` after `s` seconds; see *Runs* |
| `hold` | `{phase: "enter" \| "exit", ids, logic, value?, after?}` | enter: `{after: [..]}` (the values to restore, read when not given); exit: `{rows: [..]}` |

A hub-protocol error is the mod's error object plus, when they apply, `fix` (a sentence that says what to send next) and
`maybe_ran` (bool). A `verb` reply that is refused by the write rule carries `"stopped": true` in the error object;
one that ends a report pass carries `"pass_end": true`. `sg` raises `sg.Stopped` for the first, its private pass-end
exception for the second, and `sg.Error(code, message, fix, maybe_ran)` for every other error.

Session limits, beyond `protocol.md`'s: a line over the mod's `limits.max_request_bytes` is answered
`request_too_large` (null id) and ends the session; a call that would be sent over it is refused unsent
(`request_too_large`, `data {limit, bytes}`); a session holds at most 64 open streams (`too_many_streams`) and
64 x `max_in_flight_per_stream` calls in flight (`too_many_in_flight`, scope `session`); a caller whose oldest queued
line has waited 35 s is ended (`slow_client`); a mod method a session may not pass through answers `not_allowed`.

### Faces

| Face | Who | Options | Replies | Writes |
| --- | --- | --- | --- | --- |
| direct | the MCP client | the tool list's | single shape for one id; over 12 KB cut to about 4 KB with `ref`, `cut`, `next`; refs minted | typed ids act (move item included); build, swap, remove, trade, undo and cheats report until `confirm`; refs, networks and spreads report until `confirm` |
| script | a run's worker | the stubs' (`confirm`, `max_wait`, `queue`, `offset` refused, D7) | per-id whats always `{results: [...]}` (D13); never cut; no refs minted | the run's route (*Runs*) |
| program | a `--hub` caller | the stubs' plus `confirm` and `list_limits` | as script | as direct |

On the script and program faces a list reply's first page is the handler's first page (or the first `limit`
entries, D3); when more remain, the reply's top level carries `"cursor": "<hub cursor>"` and the main list's key is
named in `"main": "<key>"`. `sg`'s main list (a list subclass, note 23) is the page the call answered: `walk()` goes on
page by page with `more` and `all()` pages in the rest; nothing else fetches a page. The hub backs a cursor with the
mod's walk cursor (every paged list keeps one, cost design A4) and opens none when the mod kept no walk (the reply
says `rest_unreachable`). Hub cursors are bounded: 256 per hub, the least recently used evicted first, and none kept
idle past ten minutes (`Verbs/HubCursors.cs`); a dropped one answers `cursor_expired`.

The program face is a reading: PLAN names programs as `sg` users but gives no write rule for them; they get the
direct rule, since nothing reviews a program's run as a whole. So a program's pass-through takes only read-class mod
methods with no `x-effects` (plus `subscribe`, `unsubscribe`, `cursor_close`, `release`); every write goes through
`verb`, where the write rule and the recording apply. A script worker's session takes no mod methods at all: only
`verb`, `ref`, `more`, `wait` and `hold`, plus `subscribe` and `unsubscribe` (a subscription only reads the mod's own
topics and cannot take a write past the run's gate, and `sg.watch` and `sg.changes` need them), on its run's stream
(`Hub/HubSession.cs:42-45`).

## Runs

A run is one `run_script` call. Its worker is a pre-started Python process from the pool (`PYTHONHASHSEED=0`, in a
Windows Job Object with kill-on-close so workers die with the hub), started as
`python -s -P -c "<sg.worker.BOOT>" <dir holding sg>` with every `PYTHON*` variable removed from its
environment and `PYTHONHASHSEED=0` set (`-I` would ignore the seed); the worker refuses to run with hash randomisation on. A worker serves one run and exits; the pool starts the next while
a run runs, so no state of one run reaches another. The hub refuses every call of a session after its `run_end`, and
takes a run's end state from its own record (a stop it delivered is `stopped`, whatever the worker says).
The worker sends `hello` and gets its run in the `welcome` of a hub-protocol session, with one extra key:

```json
{"type":"welcome", ..., "features":[..., "pipeline"],
 "run":{"id":"script-4","code":"...","confirm":true,"face":"script","limits":{"cut_bytes":4096}}}
```

The worker then, before any user code runs, sends one `run_analysis` message (no reply):

```json
{"type":"run_analysis","literals":["122643",15,"Open"],"while_lines":[3,4],"writes":true,"holds":false,
 "syntax_error":null}
```

- `literals`: every int and str constant in the code, f-string pieces included (an int and its str form match, note
  26).
- `while_lines`: lines inside a `while` body (for the report pass's second-wait rule).
- `writes`, `holds`: whether the code calls a write verb (`write`, `build`, `swap`, `remove`, `move`, `trade`, `undo`,
  or `server` with a write what) or `sg.hold`. A run with neither is read-only (its reply waits as a report run's). The AST is advisory: a run's first write call
  switches its reply wait to the confirmed ceiling.
- The worker sends `run_analysis` once for its own code and once for each saved script it loads (`sg.lib`); the
  hub forwards each to the run, which merges them, a loaded script's literals included
  (`Hub/HubSession.cs:866-889`, `Runs/Run.cs:880-909`). One after the run has ended is refused `protocol_error`.

Each `sg` call is a `call` with `method` `verb`, `ref`, `more`, `wait` or `hold`, and `params.line` (the code line,
1-based) added; `subscribe` and `unsubscribe` are sent without a line, since the mod's own protocol methods take
their arguments alone (`clients/python/sg/_session.py:110-122`). When the code ends the worker sends `run_end` (no
reply) and exits:

```json
{"type":"run_end","state":"done","returned":{...},"printed":"...","error":null}
```

`state` is `done`, `stopped` (an `sg.Stopped` reached the top), `pass_end` (a report pass ended) or `failed` (any
other exception; `error` is the failed-run text of note 22, built in the worker).

### The seen set

One set per hub (a documented limit: runs and the direct face share it). It holds:
- ids in text the hub sent the MCP client: in a JSON reply after the cut, every string of 1 to 19 digits, or integer,
  that is the value, or an element of the value, of a key on an allow-list: `id`, `ids`, `reference_id`,
  `reference_ids`, `created_ids`, `placed`, `removed`, `targets`, `thing_id`, `device_id`, or ending `_reference_id`
  (other `_id` keys name networks, jobs and the like); every report row's id; and in a finished run's printed and
  returned text (within their cut), every run of 1 to 19 digits not touching a letter, digit, `.` or `-`. They are
  added when the text is sent (a run_script reply whose request was cancelled is never read, so it adds nothing), and a
  run's printed and returned value after the run ends. Nothing a script's worker is answered adds to it.
- each run's `literals` (for that run only), its selectors written in code (every string and number in a `network`,
  `near`, `room`, `box` value, or build blueprint's `at`, equal to one of its literals by value, floats included;
  booleans are switches, not selectors), a ref written as a literal that the agent was shown, its own builds
  (`placed`, swap results), and `player`.

The set is cleared on a world change: ids are reused across saves.

A write's targets are its `x-target` paths (acts-on and pays-from), resolved to ids. A target passes when it is in
the set; a selector target passes when the selector does. A write whose what declares no `x-target` (`server
command`) stops a confirmed run: it can only be checked by its own report, on the direct face.

A script may stop or inspect only its own run (`server stop`, `inspect job`); runs are the agent's to stop, inspect
and undo. User Python can always open its own connection to the mod (sockets, files); the seen rule governs what a
run does through `sg`, not what the Python language can reach (a public-release term, deferred).

### Routes

- **Report run** (`confirm` absent): every write is reported, never sent as an act. The mod schedules a report of a
  confirm-route what by its `x-cost-report`, and an acting call by its `x-cost` (`VerbSurface.cs:146`,
  `CallSession.cs:348-388`); the report's own reply is `report_only` with its `targets`, which the hub lists with the
  what's undo mark (`Run.Gate.cs:517-528`). A row is `[id or cell, name, current -> new]` with the undo mark, warnings
  (`driven_by`, lint) and lifted guards (`PASSED`). Waits are real until the first write. After it, a `wait` with
  `in_loop` true, or any `watch`, counts: the first returns at once, the second answers `pass_end`. `sg.watch` in a
  report run returns its current rows as met.
- **Confirmed run** (`confirm: true`): writes act on seen targets. An unseen target refuses the call with
  `stopped: true`, code `unseen_target`, `data.rows` the call's targets as report rows (now seen), and `fix`:
  "nothing acted: send it again with confirm: true" when nothing acted, else "these acted: [...]; undo script-N
  first, then send it again". The run is not resumed.
- `confirm` in a script call is refused `invalid_argument` ("confirm belongs to the run").

### Stop

A stop (`server {what: "stop", id?}`, a user cancel, the world change, an unseen target) is delivered once, as the
`stopped: true` error of the run's next `verb`, `wait` or `hold` call (at once to a call waiting in `wait`). After it:
- held restores (`hold` exit), and logic writes to targets the run already wrote, act and are footered `CLEANUP`;
- any other write answers an error row and does not raise (no `stopped` flag), so the rest of a `finally` runs;
- `wait` answers `{}` at once, without raising;
- after a world change no cleanup acts: every write answers an error row.

The worker is killed 5 s after the stop, longer while a call is in flight but never past 30 s, or at once on a second
stop. A swallowed stop ends at the kill.

The stop answers at once (`RunHost.StopAsync`): `{id, first_line, state, line, left, in_flight, undo}`. `left` is each
logic target the run has written whose last written value differs from its value before the run, with the code line
of that write (what a `finally` restores after the stop shows in the run's end notice, which reads the targets again);
`in_flight` is each job the run started that has not ended, with its fate (a started job is not stopped: it runs to its
end); `undo` is `undo script-N` when the run acted a write its undo reverts. With no id every live run is stopped,
answered as `{runs, count, left}` with one combined `left` (each entry naming its `run`). A job id (`place-41`) goes to
the mod's `drop_job`: a job still waiting in the queue is taken out (`state: "dropped"`), a started one is refused
`job_not_waiting`.

### Session end (the EOF order)

On stdin EOF (MCP mode or `--hub`):
1. The hub stops every run. For about 0.3 s it forwards only cleanup writes and `hold` exits; every other call is
   refused.
2. Then it sends `session_end` and forwards nothing more. The mod cancels calls still waiting and applies the held
   restores. A cleanup write cancelled there is journalled `not_sent`.

### Holds

`hold` enter: the hub reads `after` where it is not given, registers it with the mod first (`hold_restore set` on the
run's stream), then writes `value` (when given). `hold` exit: writes `after` (acting as `CLEANUP` after a stop), then
`hold_restore clear`. A hold the hub restores on a normal exit is reported `restored`. `ids` may be a list; int ids
match str ids.

## The journal

One file per hub process, `<journal-dir>/<pid>-<start unix ms>.jsonl`, opened with an exclusive lock (`FileShare.None`)
for the life of the hub. Lines are JSON, appended and flushed before the call they describe:

```json
{"t":"hub","hub":"8120-1760252000000","token":"hub-7f3a...","world":"3b9e1a40c2d84f6e","mcp":true}
{"t":"run","run":"script-4","first_line":"ff = '122643'","confirm":true,"world":"3b9e..."}
{"t":"call","n":17,"run":"script-4","verb":"write","what":"logic","targets":[{"id":"122643","logic":"Open","before":0,"new":1}],"undo":"undo","world":"3b9e..."}
{"t":"done","n":17,"ok":true,"after":[{"id":"122643","logic":"Open","value":1}]}
{"t":"not_sent","n":18}
{"t":"hold","run":"script-4","id":"122643","logic":"Open","after":0}
{"t":"end","run":"script-4","state":"stopped","left":[{"id":"122643","logic":"Open","value":1,"before":0}]}
{"t":"restores","applied":[...],"skipped":[...],"dropped":0}
{"t":"retired"}
```

- A hub serving an MCP client adopts, at start, every journal whose lock it can take, that is not `retired` and that
  served the same game (the hub line's `target`, pipe or host:port, when both say); a `--hub` child never adopts.
  Adopted runs keep their ids with the old hub's prefix: `script-<pid>.N`, or `script-<pid>-<last 5 digits of its
  start ms>.N` when two journals share a pid (a reused pid).
- Besides the lines above: `hold_cleared` (`{run, id, logic}`, a hold's registration gone: restored, cleared, or
  never made) and `world` (`{world, holds_dropped}`, a world change that dropped live runs' holds). The `hold` line is
  written before its `hold_restore set` goes. Each line is one write, so a line torn by a crash never runs into the
  next; adoption skips and counts torn lines.
- Adoption reads the old token's restore outcomes: connect with the old token; when `welcome.resume.resumed` is true
  (inside the window) send `session_end`, close without `bye`, and reconnect with the token after about a second; the
  `welcome.restores` it then gets (or got first) is appended as a `restores` line. Then `bye`.
- The next session's first reply carries one line per adopted run that left something, with before-values and the held
  restores applied or skipped. `undo script-<pid>.N` works from the journal in the same world.
- A journal retires (`retired` line, then the file may be deleted) when every run in it is ended and reported, after
  the reply that reported it has gone; an adopted journal that held restores whose outcomes could not be read is kept
  for the next hub.

## Recording

With `--record`, one JSON line per call at the hub input, before it is checked:

```json
{"t_ms":10234,"face":"direct","stream":0,"in_flight":1,"verb":"write","what":"logic","options":["id","logic","value"]}
```

`t_ms` is from the hub's start. `options` are the option names given (no values). Cuts and re-asks add lines:
`{"t_ms":..,"cut":{"ref":"r7","verb":"find","what":"thing","bytes":18342,"sent":4012}}` and
`{"t_ms":..,"re_ask":{"ref":"r7","calls_after":2,"how":"whole" | "repeat" | "same_call"}}` when one of the next 3 direct
calls asks again for a cut reply (`whole: true`, `repeat: "r7"`, or the same verb, what and options).

## Run replies

Text, in this order: the state line; the returned value (cut at 4 KB); printed output (cut at 4 KB); the failed-run
text; then the footer:
- writes grouped by outcome (`DONE`, `REPORTED`, `CLEANUP`, refused), each `verb what xN [undo mark]` with rows;
- `WARNINGS` (with `driven_by`), `PASSED`, `CONTESTED`, `CLEANUP`;
- `left:` after a stop (changed and not restored);
- `undo script-N` with the classes;
- the cost line: `cost script-N (<first line>): C calls, R calls/s, W writes, G game ms, M ms/s, F frames over
  budget, S samples, X ms sampling, U updates, tick held 0 ms, S s wall`, the game numbers from `stream_costs` (the
  held-tick number is not measured per run yet, `Runs/RunText.cs:87-99`).

A reply before the end: `script-N running: line L, S s in, +W <verb> <what>, R calls/s, M ms/s game`, then "follow it
with inspect {what: \"job\", id: \"script-N\"}". Report runs and read-only runs wait up to `max_wait` (default the
client's limit, 600 s); confirmed runs at most the confirmed ceiling.

Notices: a run that left something is one sticky `left:` line on every reply until it is inspected or undone by id; a
done notice comes once, on run_script and inspect job replies. Each names the id and first code line and ends "if you
did not start it, leave it".

## C# seams

These names are the contract between the pieces; each piece may add members but not change these.

```csharp
// StationGodMCP.Client: the one game connection (C1).
public interface IGameLink
{
    string? WorldId { get; }                       // from welcome, then world_changed; null while unreachable
    event Action<string?>? WorldChanged;
    long NewStream();                              // 1, 2, ... never reused
    Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation);
    Task EndStreamAsync(long stream);             // reads nothing; the caller reads stream_costs first
    Task SessionEndAsync();
    Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation);
    Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation);
    Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation); // jobs topic
}

public sealed class GameCall      // method, params, shape?, stream, deadline_ms; world added by the link
public abstract class GameReply   // Ok {Result, Frame, ElapsedMs, BudgetHit} | Refused {Code, Message, Data} | Unreachable {MaybeRan}
public sealed class StreamCost    // Stream, Calls, MainThreadMs, OverBudgetFrames

// StationGodMCP.Server: the verb pipeline (C2).
public enum Face { Direct, Script, Program }
public sealed class VerbCall      // Verb, What?, Args (JsonObject), Face, Stream, Line?
public abstract class VerbReply   // Done {Reply JsonNode} | Failed {Code, Message, Fix?, Data?, MaybeRan, Stopped, PassEnd}
public interface IWriteGate       // asked before every write, with the resolved targets
{
    WriteDecision Decide(VerbCall call, WriteTargets targets);   // Act | Report | Refuse(row) | Stop(rows, fix)
}
public interface IShownText { void Shown(string text); }        // the cap calls it with the text actually sent

// StationGodMCP.Server: runs (C4, C5).
public sealed class RunHost       // run_script, inspect job for script-N, server stop, undo script-N, EOF order
```

The direct face's gate is the direct write rule; a run's gate is its route and the seen set (C4).
