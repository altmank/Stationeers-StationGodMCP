# The StationGod wire protocol, version 2

[Back to the overview](README.md)

This page is the contract between the mod and every client: the sidecar, the client libraries, and anything else
that connects to the pipe or the TCP port. It starts with a short tour, then gives the exact rules. Words in capitals
(MUST, SHOULD, MAY) have their usual meaning in specifications.

## A short tour

A client opens the pipe (or a TCP connection, after the shared secret; see *Signing in over TCP*), sends one `hello`
line naming the protocol it speaks, and gets one `welcome` line back that says who it is now (its connection id),
which world it reached, and the limits that apply. From then on it sends `call` lines and receives `reply` lines, matched by id, several at a time if
it likes. If it subscribed to device values, `event` lines arrive between replies whenever those values change.

```text
-> {"type":"hello","protocol":[2],"client":{"name":"dashboard","version":"3.0.0"}}
<- {"type":"welcome","protocol":2,"client_id":"c4","client":"anonymous", ...}
-> {"type":"call","id":"1","method":"thing_health","params":{"reference_ids":["364","365"]},"shape":{"fields":["reference_id","damage_ratio"]}}
<- {"type":"reply","id":"1","ok":true,"shaped":true,"result":{"results":[{"reference_id":"364","damage_ratio":0.0}, ...]},"elapsed_ms":0.41,"queue_ms":9.6,"frame":81234}
```

A client that sends an ordinary request as its first line instead of `hello` is served exactly as today; see
*Old clients*.

## Framing

Both transports carry the same byte stream, and both ends MUST be able to read and write it at the same time (see
*Transports*).

- A message is one JSON object encoded as UTF-8 without a byte-order mark, followed by one line feed (`\n`, 0x0A). A
  carriage return before the line feed is ignored. Blank lines are ignored, as today
  (`src/StationGodMCP.Mod/StationGodPipeServer.cs:189`).
- A message never contains a raw line feed: JSON escapes it inside strings. Both sides MUST write compact JSON.
- A client message longer than `limits.max_request_bytes` (default 4 MiB) closes the connection after a
  `request_too_large` reply with a null id. Today's protocol has no such limit (`StationGodPipeServer.cs:186-194`
  reads lines without a bound); the limit applies to version-2 connections only.
- A reply longer than `limits.max_reply_bytes` (default 16 MiB) is not sent; the call is answered with
  `reply_too_large` (see *Errors*), whose `data` carries the size and the length of every top-level list so the caller
  can narrow the request.
- Numbers follow the mod's serialiser: NaN and the infinities are the strings `"NaN"`, `"Infinity"` and `"-Infinity"`
  (`src/StationGodMCP.Mod/Api/Shared/ApiJson.cs:47`). Reference ids are decimal strings.
- A message in which a key appears twice is refused with `invalid_argument`, as today
  (`src/StationGodMCP.Mod/Api/ApiHost.cs:208-228`).

## Messages

Every version-2 message has a `type`. Unknown top-level keys in a client message are refused with `protocol_error`;
unknown keys in a server message MUST be ignored by clients, so the server can add fields without a new version.

### Client to server

| `type` | When | Keys |
| --- | --- | --- |
| `hello` | First message, once. | `protocol` (array of integers, the major versions the client speaks, required), `client` (object: `name` string 1-64 characters of letters, digits, `-`, `_`, `.`; `version` string; `library` string, optional), `features` (array of strings the client understands, optional), `resume` (string 16-128 characters of letters, digits, `-`, `_`, a token the client chose, optional; see *Resuming*). |
| `call` | Any time after `welcome`. | `id` (string, 1-64 characters, required), `method` (required), `params` (object, optional, `{}` when absent), `shape` (object, optional, see *Shaping*), `deadline_ms` (integer 100-600000, optional, default 30000), `stream` (integer 0 to 2^53, optional, default 0; see *Streams*), `rename` (object, optional; see *Rename*), `world` (string 1-64 characters, a world id from `welcome` or `world_changed`, optional; see *A call's world*). |
| `cancel` | Any time after `welcome`. | `id` of a call in flight. |
| `stream_end` | Any time after `welcome`. | `stream` (integer 1 to 2^53, required). No reply; see *Streams*. |
| `cursor_close` | Any time after `welcome`. | `cursor_id` (string, required). No reply; see *Walks*. |
| `session_end` | Any time after `welcome`. | none. No reply: the client's session is ending abruptly, so the mod applies its held restores (see *Held restores*). |
| `bye` | Optional, before closing. | none |

### Server to client

| `type` | When | Keys |
| --- | --- | --- |
| `welcome` | After `hello`. | See below. |
| `reply` | Once per `call`. | `id`, `ok`, then `result` (when `ok`) or `error` (when not), `shaped` (true when the mod applied `shape`), `elapsed_ms` (main-thread time of the handler, as today, `src/StationGodMCP.Mod/Api/Views/HostViews.cs:10-50`), `queue_ms` (time from receipt to start), `frame` (the mod's frame counter when the handler ran), `budget_hit` (only when true: the frame's request budget was reached as the handler finished; see *Replies*). |
| `event` | Any time after `welcome`. | `event` (name), then the event's keys; see *Subscriptions* and *Connection events*. |
| `goodbye` | Before the server closes the connection on purpose. | `reason`: `shutting_down`, `world_unloaded`, `idle`, `slow_client`, `protocol_error`, `resumed_elsewhere` (another connection presented this connection's resume token; *Resuming*). |

The `welcome` message:

```json
{
  "type": "welcome",
  "protocol": 2,
  "client_id": "c4",
  "client": "anonymous",
  "server": {
    "mod_version": "1.12.0",
    "instance_id": "8f0c2d5e9a1b4c7d",
    "pipe_name": "StationGodMCP",
    "transport": "pipe",
    "role": "host",
    "dedicated": false,
    "world": {"id": "3b9e1a40c2d84f6e", "save": "Vulcan2_4", "epoch": 3},
    "game_state": "Running"
  },
  "catalogue": {"hash": "sha256:4f2a...", "methods": 92, "protocol_methods": 6},
  "limits": {
    "max_in_flight": 16,
    "max_in_flight_per_stream": 16,
    "max_in_flight_per_connection": 256,
    "max_request_bytes": 4194304,
    "max_reply_bytes": 16777216,
    "max_subscriptions": 64,
    "max_subscription_values": 8192,
    "max_values_per_subscription": 1024,
    "min_subscription_interval_s": 0.5
  },
  "features": ["shape", "shape.paths", "shape.omit", "subscriptions", "cancel", "streams", "cursors", "resume", "rename"],
  "resume": {"token": "hub-7f3a9c2e41d84b6a", "resumed": false, "window_s": 60}
}
```

- `client_id`: the server's name for this connection, unique while the mod runs; `mod_info` lists connections by it,
  beside the name the client gave in `hello`.
- `client`: always `anonymous`; the name from `hello` is shown in `mod_info`, and connections that gave the same
  name share one turn in the scheduler's rounds ([scheduling.md](scheduling.md), *One frame, step by step*).
- `limits.max_in_flight` and `max_in_flight_per_stream`: calls in flight on one stream; a client that sends no
  `stream` uses stream 0 only, so this is its whole limit. `max_in_flight_per_connection`: all streams together.
- `resume`: only when `hello` carried `resume`: the token, whether state held for it was picked up (`resumed`), and
  how many seconds the state is held after a close (`window_s`, 0 when resuming is off, when `resumed` is always
  false). `features` lists `resume` when the window is above 0 (*Resuming*).
- `restores`: only on a resume token, and only when there is something to say: what the mod's held restores did for
  that token since its last `welcome`, `{applied: [...], skipped: [...], dropped: n}` (*Held restores*).
- `server.instance_id`: random, new each time the mod loads.
- `server.world.id`: random, new each time a world finishes loading. It is the world's identity for clients: two
  welcomes with the same `world.id` reached the same loaded world, across reconnects. A game restart or a save load
  always gives a new id. `world.save` is the save's name where the game exposes it (which member is GUESS; null if
  none), for people to read; `world.epoch` is today's counter of worlds left since the mod loaded, which restarts at 0
  with the game (`src/StationGodMCP.Mod/Pure/WorldScope.cs:21-22`, `:43`; `src/StationGodMCP.Mod/Api/ModInfo.cs:44`)
  and so is not an identity.

## Versions and negotiation

The protocol has a major version, an integer. This page defines 2, the only one the mod speaks. Version 1 (one
request line, one reply line, no `hello`) was removed in stage 15 (*Old clients*).

1. The client sends `hello` with every major version it speaks.
2. The server picks the highest one it also speaks and answers in it. If there is none, it answers
   `{"type":"reply","id":null,"ok":false,"error":{"code":"unsupported_protocol","message":"...","data":{"supported":[2]}}}`
   and closes.
3. Within a major version, additions are announced by name in `welcome.features` and by the catalogue's hash.
   A client MUST NOT use a feature the server did not list. The server MUST accept every message this page defines
   for the version it chose.

The catalogue has its own identity, `catalogue.hash`: SHA-256 of the exact bytes of `catalogue.json` as built and
embedded (no re-serialising, so every language computes the same hash). A client that carries a built-in catalogue
compares hashes and fetches the server's with the protocol method `catalogue` when they differ
([catalogue.md](catalogue.md), *Who reads it*).

Methods evolve inside the catalogue, not by protocol version. Adding a method, an optional argument or a reply key is
compatible. Renaming or removing one keeps the old name as a deprecated alias for at least one minor release, listed in
the catalogue, as `thing_health` kept `min_ratio` (CHANGELOG 1.x, *Arguments are checked against each tool's schema*).

## Calls and replies

### Ids

A call's `id` MUST NOT equal the id of another call of the same connection still in flight, whatever their streams. A
call that reuses one is answered at once with `duplicate_id` carrying that id; the earlier call is unaffected and the
connection stays open.

### Streams

A stream is an ordered line of calls inside one connection (feature `streams`). A hub that holds one connection for
many callers gives each its own: 0 for direct calls, one stream per script run. A call names its stream with
`stream`; absent, null or 0 is the connection's default stream 0, so a client that never sends `stream` sees no
difference. A `stream` that is not an integer from 0 to 2^53 (a number without a fraction counts) is refused
`invalid_argument` with the call's id, and nothing is queued.

`{"type":"stream_end","stream":N}` says the client is done with stream N. It has no reply, like `cancel`. Then:

- the stream's calls that have not started are answered `cancelled` and never run; calls already running finish and
  are answered normally;
- the stream's subscriptions end without an event, and its unfinished `sample_logic` calls are answered `cancelled`
  (the samples taken so far are dropped; the method only reads);
- whatever the mod keeps per stream (walk cursors and the like) is dropped, and its held restores are cleared
  unwritten (*Held restores*);
- the stream is never used again on this connection, nor on a connection that resumes its token (*Resuming*): a later
  call on it is refused `stream_ended` (`data: {stream}`), and a later `stream_end` of it does nothing. A stream may
  be ended before any call used it.

The calls are answered at once, on the connection's own thread; the rest happens at the start of the next frame. A
`stream_end` does not touch building jobs, queued or running, nor job snapshots or undo data: a run's job outlives its
stream. Stream 0 never ends: `stream_end` of 0, or one without a `stream` from 1 to 2^53, is refused `invalid_argument`
with a null id (the message has none), and the connection stays open; an unknown key in it is a `protocol_error`.

A connection remembers the streams it ended as ranges of numbers, so a hub that numbers its runs upward costs a
few ranges, one more for each stream still open below the highest ended one. At most 1,024 ranges are kept; past
that the two lowest merge, and the streams in the gap between them count as ended too. Closing the connection, or a
slow client (*Outbound queue and slow clients*), ends every stream of it, unless its `hello` carried a resume token:
then its streams are the token's, held for the resume window and ended when it passes (*Resuming*). A world change
clears what the mod keeps per stream on every connection.

### Walks

A walk pages one search through to its end, each entry at most once and in the search's own order (feature
`cursors`; `find_things` today). The first call passes `cursor: true` and runs as it would without it: scan, filter,
sort, the first `limit` entries. When more follow, the mod keeps the reference ids of the rest, in that order, under a
cursor owned by the call's connection and stream, and the reply adds `cursor_id` (a short string such as `"k12"`,
never reused while the mod runs). With nothing more to come there is no cursor and no `cursor_id`. `offset` with
`cursor` is refused `invalid_argument`.

Each later call passes `cursor_id` and optionally `limit`, nothing else: the walk keeps its first call's filter and
order, so other arguments are refused `invalid_argument` rather than ignored. A later page:

- takes the next kept ids, without a scan (its cost class is `bounded`, not `world`, so it runs in the light lane);
- skips ids whose thing is gone or being destroyed, and goes on until it holds `limit` things or the ids run out;
  the reply's `skipped` counts the ids passed over on that page;
- does not check the filter again: a thing that stopped matching is still walked, with its current values, and things
  built after the first call are not in the walk;
- describes each thing as the first page does, distances measured from where the player is at that call;
- keeps the reply's keys: `offset` is the walk's entries passed before the page (returned or skipped), `total` the
  walk's length at its first call, `has_more` whether ids remain, `scanned` 0; `cursor_id` while more remain.

A cursor is freed when the page that takes its last ids is answered; by `{"type":"cursor_close","cursor_id":"k12"}`
from the connection that owns it (no reply, like `cancel`; an unknown id or another connection's cursor does
nothing); when its stream ends or its connection closes (with a resume token: when the token's window passes without
a resume, *Resuming*); on a world change; and by the bound on all cursors together:
1,000,000 ids (8 bytes each, 8 MB) and 1,024 cursors, the least recently used freed first, so a walk still being paged
outlives the short walks a loop opens and drops. `cursor_close` reaches the main thread through the same inbox as
calls and `stream_end`, at the start of the next frame and before that frame's calls, so a page still in flight when
its cursor is closed may answer `cursor_expired`.

A `cursor_id` that is unknown, freed, evicted, or owned by another connection or stream is answered `cursor_expired`,
`data: {cursor_id, position}`, `position` being how many things the walk had returned. The mod remembers the
positions of the last 256 freed cursors; past those, and for another owner's cursor, `position` is null. Another
stream's cursor is `cursor_expired`, not `invalid_argument`: the answer does not tell a stream whether another has
that cursor, and the way on is the same, start the walk again. A walk longer than the whole bound is refused
`invalid_argument` on its first call.

### Order on one stream

A version-2 connection may have up to `limits.max_in_flight` calls in flight on each stream (16) and
`limits.max_in_flight_per_connection` (256) on all its streams together. A call beyond either is answered at once
with `too_many_in_flight`, whose `data` is `{limit, scope}` with `scope` `stream` or `connection`, and never queued.

Replies may arrive in any order and are matched by `id`. The server keeps this order on each stream of a connection:

- A call whose effective class is write or cheat (see *Method classes*) does not start until every earlier
  call on the same stream has been answered.
- No call starts until every earlier write or cheat call on the same stream has been answered.
- Reads may overtake reads, and calls on different streams never wait for each other.

The order covers handlers and their replies, not work that continues after the reply. A building tool's real run
answers with a `job_id` while its job goes on over later frames
(`src/StationGodMCP.Mod/Api/Shared/Game/HeldTickJobs.cs:14-31`); a read sent after that reply sees the world as the job
has left it so far. Clients that need the job's result poll it with the method's `job_id` argument.

Replies and events carry `frame`, the mod's frame counter when the handler or the sample ran. For writes the handler
applies to the world before it replies (logic and memory writes, chip source and pins, labels, paint, item moves), a
subscription event whose `frame` is greater than the write's reply `frame` reflects the write; one with an equal or
smaller `frame` may not. For writes the game applies later, `frame` promises nothing: `move_gas` queues its transfer
for the next atmospherics tick and answers with a `transfer_id`, building tools answer with a `job_id`, and both are
polled with those ids.

### Rename

A call may carry `rename`: how its caller says the handler's argument names and the tool names the mod's messages
point to, for a client that offers its own surface (the verb layer) over the handler methods.

```json
{"type": "call", "id": "c7", "method": "connections", "params": {"reference_id": "123"},
 "rename": {"args": {"reference_id": "id", "min": "box", "max": "box"},
            "tools": {"rooms": "survey {what: \"rooms\"}"}}}
```

- Each entry maps a handler name (a catalogue name: a lower-case letter, then lower-case letters, digits or `_`, up to
  64 characters) to the phrase the caller uses (1 to 128 characters, no control characters). Several names may share
  one phrase (`min` and `max` as `box`), and a phrase need not be a name. `args` and `tools` are apart, so an argument
  and a tool of the same name map apart. At most 256 entries in all; `null`, `{}` or no entries is no map.
- `params` stay in handler names: the client translates them before it sends. The map changes only what the mod
  prints: argument names and paths in `invalid_argument` (the full check's `problems` included, `path` too), the
  nearest-name suggestion and the list of names a method takes, the ways to get more in `truncated`, an error's
  `see.tool`, and the argument and tool names in handlers' refusals, warnings and notes. A name the map does not hold
  prints as it is.
- A call without `rename` gets today's text byte for byte, and allocates nothing more (`Vocabulary`,
  `src/StationGodMCP.Mod/Pure/Protocol/Vocabulary.cs`). Text a later frame builds (a job's own steps) is printed
  without the map.
- A `rename` that cannot be used is refused `invalid_argument` with the call's id, on the reader thread, like
  `deadline_ms`; nothing runs and the connection stays open. Welcome lists the feature `rename`; an older server
  refuses the key as an unknown one (`protocol_error`), so a client sends it only when the feature is listed.

### Deadlines and cancelling

`deadline_ms` is how long the client will wait. A call that has not started when its deadline passes is dropped
unrun and answered `game_timeout`, as today (`src/StationGodMCP.Mod/StationGodRequestDispatcher.cs:80-96`); one that
has started runs to the end and is answered normally. A method whose catalogue entry declares `x-duration` (for
example `sample_logic`) gets its declared duration, at the arguments given, added to its deadline, and every client
timeout adds it too.

`cancel` drops a call that has not started; its reply is `cancelled`. A call that has started cannot be stopped and is
answered normally. Cancelling an unknown id does nothing. A building run queued with `wait: true` has been answered
already, so `cancel` cannot reach it; `drop_job` takes it out of the job queue before it starts (*Protocol methods*).

### A call's world

A call may carry `world`, the world id the caller means: `welcome.server.world.id`, or the id of the last
`world_changed` event. The mod compares it with the current world's id on the main thread, as the call starts, not
when it arrives, so a call queued before a world load and started after it is refused `world_changed`, unrun, with
`data: {world: {id, save, epoch}}`, the current world as `welcome` names it (`id` empty while no world runs: the menu,
or a load in progress). A write sent for one save never acts on the save loaded after it, which reuses the same
reference ids. A call without `world` runs in whatever world is current, as before. A `world` that is not a string of
1 to 64 characters is refused `invalid_argument` with the call's id, and nothing is queued. The check costs one string
compare per call that carries it.

### Replies

```json
{"type":"reply","id":"7","ok":true,"shaped":false,"result":{...},"elapsed_ms":1.37,"queue_ms":14.2,"frame":81240}
{"type":"reply","id":"8","ok":false,"error":{"code":"thing_not_found","message":"No thing with reference id 99."},"elapsed_ms":0.05,"queue_ms":3.0,"frame":81240}
```

`result` is the method's reply object as the catalogue describes it, after shaping. `elapsed_ms` and `frame` are absent
when the call never reached a handler (`cancelled`, `game_timeout`, `too_many_in_flight`, `duplicate_id`, protocol
errors), as `elapsed_ms` is today (`HostViews.cs:47-49`).

`budget_hit` follows `frame` and is written only when true (`,"budget_hit":true`, 18 bytes; the usual reply gains
none). It is true when, as this call's handler finished and before its reply was serialised, StationGod's request time
in this frame had reached the frame's request budget (`[Performance] RequestBudgetMs`, at most 2 ms while a job holds
the game tick): this call or earlier calls in the frame spent it, a heavy call's overrun included. The frame's time is
counted from its start, subscription samples included. It is never true when the budget is unlimited (0), nor for a
call answered outside the scheduler's frame. A call that finished before the budget was reached says nothing even if
later calls in the same frame reached it, because its reply is built before the frame ends. A deferred call
(`sample_logic`) takes the flag for the frame its reply is built in, not the one it started in. Replies for calls that
never ran (`game_timeout`, `cancelled`, `too_many_in_flight`) never carry it. The per-frame `budget_stops` count in
`mod_info` is unchanged.

`budget_hit` is decided before the reply is serialised, since it is part of the reply, so a call whose own serialising
pushes the frame over its budget says false; so does its `elapsed_ms`, which is the handler's time only. A large
reply's JSON (a walk's page of 500 entries) can cost more than its handler. The per-stream counters of `stream_costs`
(*Protocol methods*) are the measure that includes it: each call's whole main-thread time from its start to its
serialised reply, and its frame counted over budget when the budget had been reached by the end of that.

Inside the mod a call's reply is handed from the main thread to the connection's outbound queue when the handler and
serialising finish; no thread waits for it, unlike today's listener, which blocks per request
(`StationGodRequestDispatcher.cs:84`).

### Protocol methods

Six methods belong to the protocol itself. They are listed in the catalogue's `protocol_methods` section and are
called like any other method. `catalogue`, `subscribe`, `unsubscribe` and `stream_costs` are read class. `drop_job` and
`hold_restore` are write class: each changes what will happen to the world, so on its connection it waits for every earlier call to be answered and later
calls wait for it (*Order on one stream*), a poll sent after it answers the drop, and a client never sends it again
after a broken connection (*Reconnecting*).

| Method | Params | Result |
| --- | --- | --- |
| `catalogue` | `{}` | The full catalogue ([catalogue.md](catalogue.md)). |
| `subscribe` | See *Subscriptions*. | `{subscription, interval_s, values, frame, result}` |
| `unsubscribe` | `{subscription}` | `{subscription, ended: true}` |
| `drop_job` | `{job_id}` | `{job_id, dropped: true}` |
| `stream_costs` | `{streams?: [n, ...]}` | `{streams: [{stream, calls, main_thread_ms, over_budget_frames}]}` |
| `hold_restore` | `{set?: [{reference_id, logic_type, value}], clear?: [{reference_id, logic_type}], clear_all?}` | `{restores: [{reference_id, logic_type, value}]}`; see *Held restores* |

`drop_job` takes a building run that waits in the job queue (`wait: true`, answered `status: queued`) out of the queue
before it starts; the jobs behind it move up by one, and nothing in the world changes. A later poll of its `job_id`
answers `status: refused` with error `dropped`. A job that has started or ended is refused `job_not_waiting`, an id
that is not running, queued or kept `job_not_found` (`src/StationGodMCP.Mod/Api/Shared/Game/HeldTickJobs.cs`,
`DropWaiting`). Any connection may drop any queued job.

`stream_costs` reads what the caller's own streams have cost the game's main thread, for a hub's cost line per run.
For each stream the mod counts, from the stream's first call: `calls`, the calls run on the main thread;
`main_thread_ms`, their whole main-thread time from the start of each call to its serialised reply, which
`elapsed_ms` leaves out (*Replies*); and `over_budget_frames`, the frames in which one of its calls ended with the
frame's request time at or past the budget, as `budget_hit` judges it but after serialising, each frame counted once
per stream however many of its calls ran in it. A `sample_logic` call counts its start only; its later samples are the
subscription lane's. Without `streams` the reply lists every stream of the caller with counts, in stream order; with
`streams` (1 to 256 numbers) it lists each one asked, in the order asked, with 0s for a stream that has none. The
counts belong to the caller's connection, or to its resume token, so they survive a resume (*Resuming*); another
connection's streams are never read. A stream's counts end with it (`stream_end`, the connection's close without a
token, a token's window passing), so a hub reads a run's final counts with `stream_costs` before it sends that run's
`stream_end`; a world change keeps them. The `stream_costs` call's own cost is counted after its reply is built, so it
is not in that reply.

## Errors

Every error is `{code, message}` with an optional `data` object, today's shape plus `data`
(`src/StationGodMCP.Mod/Api/Shared/CommonViews.cs:78-89`). `message` is for people and may change; `code` is for
programs and does not.

Every error also carries `see`, `{tool, topic, subtopic}` with only `topic` always present: the `tool_info` node
that explains its code (a shared topic when `tool` is absent), taken from the code's entry in the catalogue's
`errors` (its `see`). The mod adds it to every error view, a batch item's included (`ErrorGuide`, filled when the
catalogue loads); the MCP server adds it to its own errors (`invalid_argument`, `game_unavailable`,
`help_not_found`). A code the catalogue does not register has no `see`. `tool_info` is answered by the MCP server
from the catalogue, with or without the game ([catalogue.md](catalogue.md), *Help and the text rubric*); a pipe
client that calls it gets `method_not_found` saying so.

Tool errors keep their codes (well over a hundred, from `thing_not_found` to `gas_check_failed`); the catalogue lists
them all with a description ([catalogue.md](catalogue.md), *Errors*). The protocol adds or fixes the meaning of these.
"Caller may resend" is advice to the program that made the call; the client libraries never resend an error reply on
their own (see [clients.md](clients.md), *Reconnecting*).

| Code | Meaning | `data` | Caller may resend |
| --- | --- | --- | --- |
| `protocol_error` | A message the protocol does not allow: not a JSON object, unknown `type`, unknown top-level key, a call before `welcome`, a repeated `hello`. The connection is closed after the reply. | `{line_start}` (first 80 characters) | no |
| `unsupported_protocol` | No common major version. | `{supported}` | no |
| `unauthorized` | Over TCP, the shared secret is missing or wrong. The connection is closed. Same code as today's TCP refusal (`src/StationGodMCP.Mod/StationGodTcpServer.cs:187-192`). | none | no |
| `method_not_found` | No such method. As today (`ApiHost.cs:159-161`). | `{nearest}` when one is close | no |
| `invalid_argument` | The arguments break the catalogue (version 2) or a tool's own reading (both versions), or a call's `stream` or a `stream_end` is malformed (a `stream_end` refusal has a null id). Nothing ran. | `{problems: [{path, problem}]}` on version 2; none for `stream` and `stream_end` | no |
| `invalid_shape` | Version 2 only: `shape` is malformed (see *Shaping*). Nothing ran. | `{problems}` | no |
| `duplicate_id` | The id is already in flight on this connection. Nothing ran. | none | no |
| `too_many_in_flight` | More calls in flight on the stream than `limits.max_in_flight`, or on the connection than `max_in_flight_per_connection`. Nothing ran. | `{limit, scope}`, `scope` `stream` or `connection` | yes |
| `stream_ended` | The call's stream was ended with `stream_end`; streams are never reused. Nothing ran. | `{stream}` | on another stream |
| `cursor_expired` | A walk's later page names a cursor that is gone: its walk ended, `cursor_close`, its stream's or connection's end, a world change, the memory bound, or another connection or stream owns it (see *Walks*). Nothing ran. | `{cursor_id, position}`, `position` null when unknown | no; start the walk again |
| `request_too_large` | A message longer than `limits.max_request_bytes`. The connection is closed. | `{limit}` | no |
| `reply_too_large` | The reply would exceed `shape.max_bytes` or `limits.max_reply_bytes`. The method ran. | `{bytes, limit, counts}` | only if the method is read class |
| `game_timeout` | The call was not started before its deadline. Nothing ran. As today. | none | yes |
| `cancelled` | Cancelled before it started, by `cancel` or `stream_end`. Nothing ran. Also a `sample_logic` call whose stream ended before its run finished: its samples are dropped. | none | yes |
| `shutting_down` | The mod is stopping or the world is unloading; the call was not started. | none | yes |
| `game_changed` | A game member this method needs is missing in this game build. As today. | none | no |
| `internal_error` | A bug. The method may have partly run. As today. | none | no |
| `subscription_limit` | A subscription would exceed a per-subscription, connection or global limit. | `{limit, requested, projected_ms_per_s}` | no; poll instead |
| `unknown_subscription` | `unsubscribe` of an id this connection does not have. | none | no |
| `world_changed` | The call's `world` is not the current world: another world loaded after the call was sent. Nothing ran (*A call's world*). | `{world: {id, save, epoch}}`, the current world (`id` empty while none runs) | no; find the ids again |
| `too_many_restores` | A `hold_restore` change would pass 256 restores on the stream or 4,096 on the owner (the connection, or its resume token). Nothing changed. | `{limit, scope}`, `scope` `stream` or `owner` | after clearing some |

## Shaping

Shaping says which parts of a reply the caller wants. The mod applies it while it writes the reply: keys left out are
neither formatted nor sent. The serialiser still reads every property of the reply's objects, so shaping saves
formatting and transport, not the work of building the reply; where a method declares costly parts, the handler also
skips building them (below).

`shape` is an optional object on a `call`:

| Key | Type | Meaning |
| --- | --- | --- |
| `fields` | array of selector strings, at least 1 | Which keys to keep in the entries of the reply's lists. |
| `omit` | array of selector strings, at least 1 | Which keys to leave out, as paths from the reply's top (feature `shape.omit`). |
| `limit` | object: list name to integer 0-100000 | Keep at most this many entries of that top-level list. |
| `max_bytes` | integer 1024-16777216 | Refuse to send a reply larger than this (`reply_too_large`). |

When the mod applied a `shape`, its reply envelope carries `shaped: true`; clients never apply `fields` themselves.

### Field selectors

A selector is one or more names joined by dots, after trimming surrounding white space as the sidecar does today
(`src/StationGodMCP.Server/ReplyShaping.cs:65`). A name is matched exactly, case included, against a key as it appears
on the wire: object properties (snake case) and dictionary keys alike, which keep their own case (`Temperature` in
logic values, gas names; `ApiJson.cs:51-55`, `ProcessDictionaryKeys = false`).

```text
selector = name *( "." name )
name     = 1*( ALPHA / DIGIT / "_" )
```

At the reply's top-level object: a key that a single-name selector names is kept whole; a key a path starts at is kept
with only what the paths reach inside it; a number, string, boolean or null is kept; every other list or object is
shaped as below and left out when it had something and kept none of it (an empty list stays). A list nothing names
keeps only its object entries.

A selector with one name, such as `reference_id`, keeps that key in every object entry of every top-level list and
inside every top-level object (each treated as one entry).

A selector with two or more names is a path, read two ways. From each object entry of every top-level list:
`occupant.prefab_name` keeps the key `occupant` of each entry and, inside its object value, only `prefab_name`, and so
on at any depth. And, when its first name is a top-level list, from that list's entries: `things.position.x` keeps
`position.x` of each `things` entry. A path through a value that is a list applies to every object entry of that list
(`held_in.reference_id`). A list named by the first name of a path is projected by the paths that name it, the paths
read from each entry and the single-name selectors.

When several selectors keep the same key at the same place, the key is kept with the union of what they keep below
it; a selector that names a key without going deeper keeps that key's whole value.

After applying the selectors, the mod adds `fields_unmatched` to the reply's top-level object, an array of every
selector that matched no key, and `fields_valid`, the keys the reply had (its top-level keys and the keys of the
entries and objects it shaped, sorted, at most 100, including costly keys the handler skipped). A reply with an empty
top-level list and no list entry gets neither: the name may be missing only because nothing was listed.

Near misses. Before reporting, the mod looks for a safe match among the keys the reply had for each unmatched single
name and `list.key` path (Pure/Shaping/FieldMatch): the same words respelt (`REFERENCE_ID`, `displayName`) or
reordered (`used_slots` for `slots_used`), one naming qualifier more or fewer (`display`, `count` after two or more
words, or a unit: `name` for `display_name`, `distance` for `distance_m`), or one slip in a word of four letters or
more. When exactly one key matches so, and it is not a costly key the handler skipped, the reply is written again
with that selector read as the key, and `fields_mapped` {given: key} says so. Other unmatched selectors stay in
`fields_unmatched`; `fields_closest` {given: [keys]} names up to five near keys of each that has any (also one other
word more or fewer, singular for plural, two slips). When every unmatched selector has near keys there, `fields_valid`
is left out: the near keys answer it, and the whole key list would only repeat the reply (some sixty keys on `planet`).

Compatibility. Every `fields` value the sidecar accepts (an array of at least one string, no further limits) is
accepted through the sidecar, with the old results for single names: a selector that does not follow the grammar (a
name with `-`, an empty string after trimming, a stray dot) is not sent but listed in `fields_unmatched` by the
sidecar, as an unknown name is. Sent to the mod, such a selector is `invalid_shape`, as is a `shape` with an unknown
key or more than 256 selectors; with `[Server] StrictArguments = false` the mod reads a shape leniently instead and
lists such a selector in `fields_unmatched`.

`omit` leaves keys out instead of keeping them. Each selector is a path from the reply's top-level object: `source`
leaves out the key `source`, `runtime.registers` leaves `registers` out of the object `runtime`; a list on the way
applies the rest of the path to each of its object entries (`members.position`). A selector with one name also leaves
that key out of every top-level object and every object entry of a top-level list (`body` leaves out `target.body`).
`omit` wins over `fields` where both name a key. The mod adds `omit_unmatched`,
an array of every `omit` selector that left nothing out (a path below an omitted key counts as applied), to the reply's
top-level object. Selectors that do not follow the grammar are treated as for `fields`, reported in `omit_unmatched`.

Errors are never shaped: an error reply is always whole.

The reference for single-name selectors is the sidecar's `FieldSelection`. The mod's results MUST equal it as parsed
JSON, key order included, on the test fixtures described in [stages.md](stages.md) (stage 1).

### List limits

`limit: {"things": 20}` keeps the first 20 entries of the top-level list `things` after the method has built it. This
saves formatting and transport, not the method's own work; a method with its own paging arguments (`offset`, `limit`,
which the catalogue marks with `x-paging`) saves both and is preferred. When `limit` cuts a list, the mod adds
an entry for that list to the reply's `truncated` (below). A `limit`
naming a key that is not a top-level list is `invalid_shape` (ignored with `StrictArguments` off).

### Truncation notice

Every reply to a call ends with `truncated`: one entry per list the reply holds back entries of, whether the handler
paged or capped it (`limit`, `offset`, `holders_limit`, `max_devices`, a report's `limit`, a stack window, a log tail)
or the writer cut it (`shape.limit`, the method's `x-default-limits`):
`{"list": "things", "returned": 8, "total": 412, "more": "pass limit (max 500) or offset 8"}`. `list` is a top-level
key or a path (`totals[].top_holders`, `dry_run.cells`); `total` is a real count, and `at_least: true` marks a lower
bound where counting would cost what the cut saves (a search that stopped at its count). `truncated: []` means
nothing was held back. The writer adds it after the reply's keys, so `fields` and `omit` never remove it, and the
`output_file` pointer carries it whole. Handlers note their cuts through `Pure.Shaping.Truncations` (or, for a view
that caps a list it carries, `ITruncatingView`); `tests/.../Budget/TruncationNoticeTests.cs` holds every cap to it.

### Shaping before the reply is built

A method's catalogue entry MAY list `x-costly` parts: reply keys that cost real game work, such as `networks` in
`thing_health` (`src/StationGodMCP.Mod/Api/ThingHealth.cs:294`). When `fields` is given and keeps none of a costly
part, the handler skips computing it. Handlers ask through one call on their arguments (`args.Shape.Wants("things",
"networks")`), so a handler that does not ask behaves exactly as without shaping. A handler MUST give the same
values for every key the caller kept whether or not it skipped a costly part.

### What stays on the client

`output_file` is not part of the protocol. It writes the reply on the caller's machine; with a remote game the mod
cannot. The libraries and the sidecar implement it on top of `shape` (see [clients.md](clients.md), *Output files*).
When both are used, `fields` applies first and the file holds the shaped reply, as today (`ReplyShaping.cs:45-49`).

## Method classes

Every method has a class in the catalogue: `read` (changes nothing in the world), `write` (changes the world the way a
player or a chip can, through the game's own rules) or `cheat` (does what no player can). A method's class may depend
on its arguments through `x-class-when` (for example every building tool is read class while `dry_run` is true or
absent, because a dry run changes nothing). The class worked out from the arguments is the call's *effective class*.

The class is information, not a permission: every connection can call every method. The libraries resend only read
calls after a broken connection (*Reconnecting*), the server orders write and cheat calls on a connection (*Order on
one connection*), and agents use cheat to tell the owner when a tool is a cheat before they use it.

## Signing in over TCP

A TCP connection's first line MUST be the shared-secret sign-in, `{"type":"auth","secret":"..."}`, checked in fixed
time against `[Remote MCP] Secret` (`StationGodTcpServer.cs:231-245`). The server answers `{"ok":true}`, or
`unauthorized` and closes. After it the connection talks exactly as a pipe connection, `hello` first. The mod logs each sign-in with the client's address. A pipe connection has no
sign-in.

On either transport the first line must arrive within 10 seconds of connecting, the time today's pipe allows
(`StationGodPipeServer.cs:269`); otherwise the connection is closed.

The secret crosses the network in plain text, as does everything after it; the connection needs a VPN or private
network (see *Transports*).

## Transports

Both ends of a version-2 connection read and write at the same time: replies, pings and events are written while the
other side may be in the middle of sending. On Windows this needs overlapped I/O on the pipe handle; a handle opened
synchronously lets one operation run at a time, so a pending read would block every write. Today both ends open
synchronous handles: the mod with `PipeOptions.None` (`src/StationGodMCP.Mod/StationGodPipeServer.cs:103-104`), the
Python clients with `CreateFileW(..., 0, None)` (StationeersScriptDashboard `stationscript/transport.py:125`;
StationeersTestServer `tsclient.py:75`). Version 2 requires overlapped handles at both ends; stage 3 proves them before
anything else is built on them ([stages.md](stages.md)).

### Named pipe

- Name: `\\.\pipe\<[Pipe] Name>`, as today (`src/StationGodMCP.Mod/StationGodMod.cs:327-347`; `docs/configuration.md`).
- Opened with overlapped I/O (`PipeOptions.Asynchronous`, or the Windows API with `FILE_FLAG_OVERLAPPED` if Unity's Mono
  does not provide it; GUESS until stage 3). The first-line timeout uses `CancelIoEx` or an asynchronous read with a
  timeout instead of today's `CancelSynchronousIo` (`StationGodPipeServer.cs:262-318`).
- Instances: `[Server] MaxPipeConnections`, default 32. Today the pipe has 4 instances (`StationGodPipeServer.cs:23`),
  too few once each agent session, the dashboard and scripts each keep a connection.
- Each connection has one reader and writes through its own outbound queue, so the main thread never writes to a pipe.
- Access: the pipe is created with the default security of the game's process. Which local accounts that admits is
  GUESS (believed: full access for the owner's account, administrators and SYSTEM, read-only for others, which is not
  enough to send a request).

### TCP

- Settings `[Remote MCP] Enabled`, `BindAddress`, `Port`, `Secret` (`StationGodMod.cs:198-295`). Today the listener stays
  off without a secret (`StationGodMod.cs:243-250`). In version 2 likewise: it starts when `Enabled` is true and the
  secret is set. The default bind address stays `0.0.0.0` for compatibility; the documentation tells owners to bind
  the VPN's interface address.
- `[Server] MaxTcpConnections`, default 8, counted apart from the pipe's, so remote connections and unfinished
  sign-ins never take a local client's place.
- Same messages as the pipe, after the shared secret (*Signing in over TCP*).
- No TLS. The mod does not encrypt. Unity's Mono can host `SslStream` in principle, but certificate handling for
  players is a support burden and untested in this game (GUESS); a private network or VPN (WireGuard, Tailscale,
  ZeroTier) is the supported way to reach a game on another machine.
- Keepalive: the server sends `{"type":"event","event":"ping"}` after 30 seconds of silence on a connection; a client
  that has sent nothing and received no reply for 120 seconds may be closed with `goodbye {reason: "idle"}` unless it
  holds subscriptions.

### Outbound queue and slow clients

Each connection's outbound queue holds replies and events in order. Replies are never dropped and do not count against
any byte limit beyond `max_reply_bytes` each, so a single reply of the largest allowed size is queued normally. Events
are bounded by subscription merging (below): at most one queued update per subscription. A connection is closed with
`goodbye {reason: "slow_client"}` only when the oldest message in its queue has waited 35 seconds without being taken
by the client (today's send timeout, `StationGodTcpServer.cs:27`); its calls not yet started are dropped and its
subscriptions end.

The connection has one socket and one writer for all its streams, so a write that timed out cannot be pinned on one
stream. A slow client ends every stream of the connection the way `stream_end` ends one (unstarted calls dropped,
here unanswered since the client takes nothing; subscriptions and `sample_logic` runs ended; per-stream stores
cleared), then is closed. A slow client whose `hello` carried a resume token only has its unstarted calls dropped and
its `sample_logic` runs ended: the rest is the token's, held as after any other close (*Resuming*).

## Subscriptions

A subscription asks the mod to read a set of device values at an interval and push the read when it changed. The
dashboard is the first consumer; the parts it needs are defined here. Further options (pushing only the changed
values, tolerances, real-time intervals, following building jobs) wait until a client needs them, and would come as
new `features`.

### Subscribing

`subscribe` params:

| Key | Type | Meaning |
| --- | --- | --- |
| `topic` | `devices` (default), `world` or `jobs` | What to watch. |
| `items` | array, as `read_devices` `items` | For `devices`: what to read. Same parts, same bounds per subscription (`src/StationGodMCP.Mod/Pure/DeviceReads/DeviceReadRequest.cs:16-22`: at most 128 items and 1,024 values). |
| `include` | array, as `read_devices` `include` | For `devices`: `clock`. |
| `gateway_id` | string | For `devices`, as `read_devices`. |
| `interval_s` | number 0.5-3600 | For `devices`: how often to read, in game seconds. Default 1. Game time stops while the game is paused, so a paused game sends nothing. |

The reply: `{subscription: "s3", interval_s: 1.0, values: 412, frame: 81250, result: <a full read_devices result>}`.
`result` is the first reading.

Topics:

- `devices`: device reads as above.
- `world`: no params; pushes `world_changed` when a world finishes loading (a new `world.id`) and `game_state` when the
  game state changes (Running, Paused, Loading). Cheap; every library subscribes to it.
- `jobs`: no params; pushes `job_ended` when a held-tick job (a confirmed `place_*`, `remove_*`, `upgrade_*`,
  `clean_*`, `replace_*`, `place_structure` or `remove_structure` run, and `undo_job`'s jobs) reaches its final
  status. Jobs are global, so every `jobs` subscription of every connection hears of every job, whichever client
  started it. A script or hub waits on this instead of polling `job_id`.

### Job events

```json
{"type":"event","event":"job_ended","subscription":"s7","seq":3,"frame":81990,
 "job":{"job_id":"run-41","tool":"place_pipes","status":"applied","created_ids":["101","102"],"removed":["55"],
        "changed":[{"old_reference_id":"60","new_reference_id":"102","target_prefab_name":"StructurePipeTJunction"}],
        "placed":[{"reference_id":"101","prefab_name":"StructurePipeStraight"}]}}
```

- One event per job, sent from the main thread in the frame the job ends: it finished (any final status: `applied`,
  `applied_with_differences`, `applied_unchecked`, `stopped`, `gas_lost`, `refused`, a step that threw), or it was
  dropped from the queue before it started (status `refused`): by `drop_job` (`error.code` `dropped`), by a refusal
  as it left the queue (the gas hold's code, `game_not_running`, `internal_error`), or because the world it was queued
  in was left (`game_not_running`). Steps and queue moves before that send nothing.
- `job` holds the job's facts, taken from the same final view a `job_id` poll answers from then on: `job_id`, `tool`,
  `status`, `error` (`{code, message}`, when the job has one), and, each only when it holds something, the lists
  `undo_job` plans from: `created_ids` (every id built, new and changed pieces), `removed` (ids removed outright),
  `changed` (a run's swapped pieces, old for new) and `placed` (new things with their prefab). These are the lists
  `undo_job` takes in its request for a job past the last 16 the mod keeps. The reports (preflight, final check,
  verification, materials) are left out: poll the job with `job_id` for them while it is among the last 16. A 500-piece
  run's `job` is about 44 KB, most of it `placed`.
- `seq` is 1 for the subscription's first event and one more for each after it. Job events are never merged or
  dropped: each is a line of its own in the connection's outbound queue, and jobs end one at a time. A client too
  slow to take its lines is closed like any other (`slow_client`).
- A new world leaves `jobs` subscriptions in place (a job id names one job while the mod runs); a running job of the
  last world still ends with an event.

### Update events

```json
{"type":"event","event":"update","subscription":"s3","seq":42,"frame":81312,"game_time_s":84211.5,"late_ms":0,
 "result":{"gateway_id":"world","clock":{...},"results":[...]}}
```

- `result` is the whole read in `read_devices`'s reply shape. All its values were read in the same frame, so a control
  loop sees one consistent state, as `read_devices` gives today.
- A reading is sent only if some value other than the clock differs from the last reading sent for that subscription
  (numbers compared as numbers, other values as values, item errors by code). The clock never counts as a change; it
  rides along in every event that is sent.
- `seq` is 1 for the first event of a subscription and the last sent `seq` plus 1 after that. If a new reading is ready
  while the subscription's previous event is still in the outbound queue, the queued event's `result`, `frame`,
  `game_time_s` and `late_ms` are replaced by the new reading's, and it keeps its `seq`. So at most one update per
  subscription waits in the queue, `seq` never has gaps, and a slow client simply sees fewer, fresher events.
- `late_ms`: how much later than due this read was, because of the frame budget (see [scheduling.md](scheduling.md)).

### Limits

- Per subscription: the bounds of one `read_devices` call (128 items, 1,024 values).
- Per connection: `limits.max_subscriptions` (64) and `limits.max_subscription_values` (8,192 values across them,
  counted as `read_devices` counts them). These are sized to hold every card of the dashboard on one connection, and
  a connection's streams share them: they are not per stream.
- Across all connections: the projected sampling cost must fit the subscription share of the frame budget
  ([scheduling.md](scheduling.md), *Subscriptions*).

A subscription that would pass a limit is refused with `subscription_limit`. A client so refused polls the same items
with `read_devices` at the same interval instead; the libraries report the refusal and the caller polls (the dashboard's runner already has that path).

### Ending

A subscription belongs to the stream of the `subscribe` call that made it. Any stream of the connection may
unsubscribe it. It ends when the client unsubscribes, when its stream ends or the connection closes (both without an
event), or when the mod ends it with an event:

```json
{"type":"event","event":"subscription_ended","subscription":"s3","reason":"world_changed"}
```

Reasons: `world_changed` (reference ids may mean other things in another world), `limit` (the owner lowered a
limit). Subscriptions survive a reconnect only through a resume token (*Resuming*). The libraries keep each
subscription's request and subscribe again after reconnecting only when `welcome.server.world.id` is the one they last
saw; otherwise they close it and tell the caller ([clients.md](clients.md), *Subscriptions*).

### sample_logic

`sample_logic` keeps today's arguments, limits and reply: targets as `read_logic_many` takes them (1 to 32), interval
0.05 to 5 seconds, duration 0.1 to 30 seconds, at most 120 samples, on the real clock, running whether or not the game
is paused, and reporting the changes with elapsed real seconds (`src/StationGodMCP.Server/Program.cs:332-455`,
`:359-376`, `:448`). It is not a subscription: it has its own sampler in the mod, which takes each sample in the first
frame at or after it is due and counts in the subscription lane's time ([scheduling.md](scheduling.md)). Its catalogue
entry declares `x-duration` from `duration_seconds`, so the call's deadline and every client's wait for its reply add
the duration. A `stream_end` of its call's stream answers an unfinished run `cancelled` (*Streams*). A run ends,
unanswered, when its call's connection closes, resume token or not: the reply belonged to that connection (*Resuming*).

### Connection events

| `event` | Keys | When |
| --- | --- | --- |
| `ping` | none | After 30 s of silence. |
| `world_changed` | `world` (the new `{id, save, epoch}`) | A world finished loading (sent to every connection, subscribed or not). |

### As built: subscription core

The pure parts live in `src/StationGodMCP.Mod/Pure/Subscriptions/` and `Pure/Sampling/`, with the game-side readers
in `src/StationGodMCP.Mod/Subscriptions/GameReaders.cs`. What they settle beyond the text above:

- `SubscriptionEngine` owns every connection's subscriptions. `Subscribe` admits, reads once (the reply's `result`,
  also the comparison baseline) and registers; `Poll` runs once a frame in the subscription lane; `Unsubscribe`,
  `DropConnection` (silent) and `ApplyLimits` (with events) end them; `WorldChanged` ends every devices
  subscription with `world_changed`. Ids (`s1`, `s2`, ...) are never reused while the mod runs.
- A devices sample is read through `read_devices`' own handler, so it reads exactly what that call reads. Readings are
  compared with the last reading offered to the client (not the last one written), value by value by
  `ReadDevicesComparer`; NaN equals NaN.
- Each subscription has one `UpdateSlot`, which is what sits in the outbound queue. A changed reading offered while
  the slot waits replaces its contents and keeps its `seq`; the writer thread takes the slot when it reaches it. A
  slot of an ended subscription writes nothing.
- `Resync` makes the next sample due at once and sends it even unchanged, with the next `seq`. Every update carries
  the whole reading, so a resync is also how a client that lost its state gets it back without unsubscribing.
- Sampling is on a grid of `interval_s` from the subscribe; a late sample does not shift the grid, and a frame that
  missed several points takes one sample. Due samples go round-robin across connections, oldest due first within
  one; the connection the lane's budget stopped at goes first next frame.
- Admission checks, in order: subscriptions off (`SubscriptionBudgetMs` 0), 128 items, 1,024 values, 64 subscriptions
  (world-topic ones count), 8,192 values, then the projected load. A request over one call's bounds is
  `subscription_limit`, not `invalid_argument`. `data` is `{name, limit, requested, projected_ms_per_s}`, where
  `name` is the `welcome.limits` key passed (`max_projected_ms_per_s` and `max_items_per_subscription` for the two
  limits `welcome` does not list). Costs per sample use the starting figures of [scheduling.md](scheduling.md) as
  constants; nothing measures them.
- A read that throws while sampling ends that subscription with reason `read_failed`, a third reason beside the
  two above.
- `SubscribeJobs` admits a `jobs` subscription under the same per-connection count as a world one; `JobEnded` gives
  each `jobs` subscription its next `seq`; `WorldChanged` leaves them; `ApplyLimits` ends the newest of any topic
  first.
- `WorldIdentity` mints a random 16-hex-digit `world.id` on the first frame a world is running after none was, using
  the same notion of running as the per-world stores (`GameState` neither `None` nor `Loading`).
- `sample_logic`'s `LogicSampler` keeps the sidecar loop's arguments, messages, change list (each result compared as
  its JSON text would be: the error message counts, and 0 differs from -0), counts and millisecond rounding. Its
  samples sit on a grid of `interval_seconds` from the first sample, the last at `duration_seconds`; a frame within a
  microsecond of a due time counts as on it. A read that fails as a whole fails the call with that error, as the
  sidecar returned it.

### As built: the wiring of subscriptions and sample_logic

- `Protocol/SubscriptionHub.cs` holds the `SubscriptionEngine` and the `LogicSampler` and is the scheduler's
  `ISampleLane`: each `RunNextDueSample` takes one `sample_logic` sample if one is due, else one subscription sample, so
  the lane's share and its first-sample rule cover both.
- `subscribe` and `unsubscribe` are protocol methods (`ProtocolMethods.Names`, `catalogue/protocol/`) that run on the
  main thread as calls, in the light lane, with the usual reply (`elapsed_ms`, `queue_ms`, `frame`). Their `items` and
  `include` schemas are `read_devices`' own (`catalogue/defs/device_read_items.json`, `device_read_include.json`).
- An update waits in the connection's outbound queue as an `IOutboundLine` over its subscription's `UpdateSlot`; the
  writer thread makes the event's line when it reaches it, so a merged update is written with its newest reading.
  Every subscription event is the view's keys after `"type":"event"`.
- `welcome.features` lists `subscriptions`, and `welcome.limits` gains `max_subscriptions`, `max_subscription_values`,
  `max_values_per_subscription` and `min_subscription_interval_s`, whenever the server has the hub (always in the mod).
- World identity: the world id is `WorldScope`'s, the one `welcome` already carries; the core's own `WorldIdentity` was
  a second source of ids and is gone (`WorldId` stays). The hub hears `WorldChanged` where the mod already announces a
  new world to every connection, and `ObserveGameState` every frame after the server's facts are published.
- A closed connection posts its end; at the next frame its subscriptions and `sample_logic` runs are dropped without an
  event.
- Job ends: `HeldTickJobs.Ended` is the seam. `HeldTickJobs` calls it with a job's final view where it keeps that view
  for polls (`Remember`), and for each queued job a world change drops; the mod sets it to `SubscriptionHub.JobEnded`
  with the frame. With no `jobs` subscription the hub returns at once; otherwise it turns the view into its facts once
  (`Pure/Layout/JobEndFacts.cs`) and the engine sends them to each subscription. The mod's unload abandons a running
  job with no event: the servers are stopped before it.
- A subscription's sample reads through `ReadDevicesApi.Read` with the request parsed once at `subscribe`; only the
  gateway is looked up again each sample, since it may have gone.
- `sample_logic`: arguments refused by `SampleLogicArguments.Of` are answered at once; otherwise the
  call is left running and answered when its run ends, shaped as asked. Its `elapsed_ms` is the run's real time from
  start to last sample, since it never holds the main thread for more than one sample. Its catalogue entry no longer
  says `x-runs-in: sidecar`; the sidecar forwards it (its own loop and `x-runs-in` went in stage 15).
- `x-duration` is read by the mod's catalogue model (`CatalogueMethod.DurationMs`): a call's deadline adds the
  duration at the call's arguments (the argument given, at most `max_s`, else `max_s`).
- `Resync` is in the engine but has no message on the wire yet; `ApplyLimits` has no caller, since the limits are read
  once at load.

### As built: streams

- `ClientMessage` reads `stream` on a call and the `stream_end` message (`EndOfStream`); `CallSession` counts calls
  in flight per stream and in all, keeps the ended streams (`Pure/Protocol/StreamEnd.cs`, `EndedStreams`) and refuses
  calls on them, answers a `stream_end`'s unstarted calls and posts the end (`ICallQueue.StreamEnded`).
- At the next frame `LaneScheduler` drops the stream's waiting calls (`FrameScheduler.EndStream`) and tells
  `StreamStores`, the one place a per-stream store (`IStreamStore`, keyed by `CallContext`) hears of ended streams;
  `SubscriptionHub` drops the stream's subscriptions (`SubscriptionEngine.DropStream`) and `sample_logic` runs. A
  closed connection reaches the same places for every stream; `WorldStores` clears every stream store on a world
  change.
- `mod_info`'s connections keep one `in_flight` per connection, all streams together; the default reply gains nothing.

### As built: walks

- `Pure/Paging/WalkCursors.cs` is the store: cursors by number in a dictionary and on a recency list, each holding a
  `long[]` of the ids after its first page; the bounds, eviction and the ring of the last freed cursors' positions.
  It is an `ICursorStore` (an `IStreamStore` that also hears `cursor_close`), registered in `StreamStores.Main`.
- `Api/Shared/WalkRequest.cs` reads `cursor` and `cursor_id`, keeps a first page's rest (`Keep`), takes a later page
  (`Next`), writes the truncation note naming the cursor and raises `cursor_expired`. `find_things` uses it; another
  handler that sorts its whole list before paging needs only those calls and the two catalogue arguments.
- `ClientMessage.CursorClose` parses `cursor_close`; `CallSession` posts it (`ICallQueue.CursorClosed`), and
  `LaneScheduler` hands it to `StreamStores.CloseCursor` when it takes its inbox in.

### As built: stream costs

- `Pure/Scheduling/StreamCosts.cs` keeps the counters by `CallContext`: an entry made at a stream's first call, then a
  dictionary lookup and three additions per call, nothing allocated (about 8 ns a call on net8, about 50 ns with the
  two clock reads and the budget check around it). The frame counter is `FrameScheduler.Frame`.
- `LaneScheduler` times each call it runs on the scheduler's clock, the same span `StationGodRequestDispatcher.RunCall`
  measures for `mod_info`, and records it with `CurrentFrame.BudgetReached` read at the end. It passes every stream
  end it hears to the counters with the stores; a world change does not reach them.
- `Protocol/StreamCostCalls.cs` answers `stream_costs` on the main thread, routed by the dispatcher like `drop_job`.
  The hub's final counts come from reading before `stream_end`, rather than from counts kept past the end: nothing
  is held for streams that have gone, and a run's last call is answered before its hub ends the stream.

### As built: resuming

- `CallContext` and `StreamEnd` key by an owner rather than a connection: the connection's `client_id`, or the owner
  key (`r1`, `r2`, ...) of the resume token it claimed (`Connection.Owner`). Every per-stream store, the subscription
  engine and the sampler key by it, so a resumed connection reaches the held state by its owner key alone.
- `Protocol/ResumeHolds.cs` keeps the tokens: `Claim` at `hello` (resume, or take over), `Goodbye` on `bye`, `Release`
  when a connection ends (held, or ended after a bye), `Tick` once a frame on the main thread (one volatile read while
  nothing is held; no allocation), `WorldChanged` through `WorldStores`. A token whose state ended gets a new owner key
  when presented again. One instance serves the pipe's and TCP's hosts.
- The main thread hears of holds and resumes through `ICallQueue.OwnerHeld` and `OwnerResumed`, posted under the
  holds' lock, so they arrive in order with the ends. `SubscriptionEngine.Pause` stops a held owner's sampling;
  `Resume` forces each devices subscription's next sample and requeues its `UpdateSlot` (a new epoch, so an entry left
  in the old connection's queue writes nothing).

### As built: held restores

- `Protocol/HeldRestores.cs` is the store: per `CallContext`, the stream's restores by (reference id, logic type) and
  in registration order, each with its world id and the stream's last written value, and the per-owner counts for the
  bounds. It is an `ISessionEndStore` (an `IStreamStore` that also hears an owner's abrupt end) in `StreamStores.Main`,
  so stream ends clear it and a world change drops it; `SessionEnded` applies. `Protocol/HoldRestoreCalls.cs` answers
  `hold_restore`, routed by the dispatcher like `drop_job`.
- `LogicOps.Write` tells the store each write's read-back value (`Wrote`: one field read while nothing is held, one
  dictionary lookup per write while something is); restores write through `LogicOps.SetValue`, the same game write.
  The game side sits behind `IRestoreDevices` (`Api/Shared/Game/RestoreDevices.cs`).
- Abrupt ends reach the store through `ICallQueue.SessionEnded`, posted to the scheduler's inbox ahead of the owner's
  stream ends: by `CallSession` on `session_end` and on a slow client without a token, by `ProtocolHost.Ended` for a
  connection without a token that never sent `bye` (`Connection.SaidBye`), and by `ResumeHolds` when a window passes
  (not on a world change or after a `bye`). `Connection.ResumeToken` keys the results kept for `welcome`.

## Reconnecting

A connection can break at any time: the game closed, the save reloaded, the mod restarted. The rules a client follows
(the libraries do this; [clients.md](clients.md)):

- A call whose `call` line could not be written did not reach the game; it may be sent again on a new connection.
- A call that was written but not answered may have run. It may be sent again automatically only if its effective
  class is read and its catalogue entry has no `x-effects` (from the catalogue, at the arguments given). This is
  today's dashboard rule (StationeersScriptDashboard `stationscript/transport.py:84-110`), with the catalogue instead of a
  hand-kept list; that list already leaves out `highlight` and `show_preview`, which draw (`transport.py:23`).
- Any automatic resend, and any resubscribe, happens only when the new `welcome.server.world.id` equals the last one
  seen. A different id means another world: every reference id the client holds may now name something else. A call
  that carries `world` is held to it by the mod as well (*A call's world*).

### Resuming

A client that holds runs across a break (feature `resume`; a hub) sends `resume: "<token>"` in `hello`: a string of 16
to 128 letters, digits, `-` and `_` that it chose, the same on every connection of its session. A malformed token is
refused like any other bad `hello` (`protocol_error`, then `goodbye` and close). The mod then keeps the connection's
per-run state under the token rather than under the connection:

- the streams it ended, so a stream id stays never-reused across the reconnect (`stream_ended`);
- its walk cursors (*Walks*);
- its subscriptions (*Subscriptions*);
- its streams' cost counters (`stream_costs`, *Protocol methods*);
- its held restores (*Held restores*).

When the connection closes for any reason but `bye` (the socket dropped, `slow_client`, a protocol error, the server
stopping), that state is held for `[Server] ResumeWindowSeconds` (default 60; `welcome.resume.window_s`). A
connection that sends the same token within the window picks it up (`welcome.resume.resumed: true`): its next page of
a walk follows the last, an `unsubscribe` of an old subscription works, an ended stream stays ended. While held, the
token's subscriptions take no samples, so a client that is away costs the game nothing; after the resume, each devices
subscription's next sample is taken at once and sent whole, changed or not, with the next `seq` (an update that was
waiting in the old connection's queue keeps its `seq`). The client knows nothing reached it in between, so it reads
the update as the subscription's current state.

What is not resumed: calls. A closed connection's calls that had not started are dropped unanswered, and a call that
had started is answered on the connection that sent it, which is gone; the client's resend rules above decide what
to send again. A `sample_logic` run ends, unanswered, when its call's connection closes, since its reply cannot reach
anyone; the client calls it again if it still wants it.

The state ends, as a `stream_end` would end each of its streams, when the window passes without a resume (checked
once per frame; the token's held restores apply first, *Held restores*), at once on `bye` (a clean goodbye means the session is over, so nothing is kept for it), and at once
on a world change, since its reference ids may name other things now; a connection that then sends the token gets
`resumed: false` and starts empty. A token presented while another connection still holds it is taken over: the new
connection resumes the state, and the old one is sent `goodbye {reason: "resumed_elsewhere"}` and closed. A broken
connection over a VPN can look open until the slow-client timeout, so a refusal would lock the client out for that
long. Without a token nothing changes: the connection's state ends with it. `[Server] ResumeWindowSeconds 0` turns
resuming off: `welcome` does not list `resume`, a token is answered `resumed: false, window_s: 0`, and every
connection keeps its state to itself.

### Held restores

A hub runs scripts that turn a device on and turn it back off when they finish (`with sg.hold(pump, 'On', 1,
after=0)`). When its own client ends the session, it closes the hub's input and kills it about 0.4 s later, so the
script's cleanup never runs. A held restore is that cleanup kept in the mod: a logic write the mod makes itself if the
stream's session ends abruptly.

`hold_restore` changes the calling stream's restores and answers the stream's list after the change, in registration
order: `{restores: [{reference_id, logic_type, value}]}`, `logic_type` as `write_logic` gives it (`{id, name}`). Its
params apply in this order: `clear_all` (true removes every restore of the stream), `clear` (each entry removes the
restore for that device and logic type, if the stream has one), then `set` (each entry adds a restore, or takes the
place of the stream's restore for the same device and logic type). Only logic writes can be held. A `set` entry must
name a device in the world (no gateway scope) that both writes and reads the logic type, since the overwrite check
below reads it; otherwise the whole call is refused with that device's error (`device_not_found`,
`logic_not_writable`, `logic_not_readable`, `invalid_logic_type`) and nothing changes. A stream holds at most 256
restores, and an owner (the connection, or its resume token) 4,096 over all its streams: a change past either is
refused `too_many_restores` (`data: {limit, scope}`) whole.

The mod applies a stream's restores, at the start of the next frame, on the main thread, through `write_logic`'s own
game write, when its owner's session ends abruptly:

- the owner sends `session_end` (a hub sends it when its input closes, within the 0.4 s): every stream of the owner
  applies at once. The connection's calls that have not started are answered `cancelled` first, so none of them
  writes after a restore; the connection and its streams stay open;
- a resume token's window passes without a resume (*Resuming*);
- a connection without a resume token closes without `bye`: the socket dropped, a slow client, a protocol error.
  Without a token there is no window to wait for, so this applies at the close. With `[Server] ResumeWindowSeconds
  0` every connection is in this case.

Each restore is written only if both hold:

- the world is the one it was registered in (`welcome.server.world.id`); otherwise it is dropped;
- the device still holds the value this stream last wrote to that logic type: the value `write_logic` or
  `write_logic_many` read back after the stream's last write to it, or, if the stream has not written it since it
  first registered the restore, the value the device held then (setting the restore again changes the value to
  write, not this one). A device that holds another value was changed by someone
  else (the player, a chip, another stream of the same client), and its restore is skipped, so a pump the player
  turned back on by hand stays on. A device that is gone, or refuses the write, is skipped too.

A normal end writes nothing: `stream_end` clears the stream's restores, `bye` clears every restore of the connection
(or of its token), and a hub clears a run's restores itself when the run ends. A world change drops every restore of
every owner, counted as dropped. A resumed token keeps its restores, with the rest of its state.

Results are counted in `mod_info`'s `include_counters` (`runtime.restores {applied, skipped, dropped}`) and, for an
owner whose connection holds a resume token, kept for the next `welcome` on that token, once: `restores: {applied:
[{stream, reference_id, logic_type, value}], skipped: [{..., reason, current}], dropped: n}`. `reason` is `changed`
(with `current`, the device's value then) or the device's error code; `current` is absent when the device could not
be read. Each list holds at most 32 entries, and `unlisted` (present only when above 0) counts the rest. Results for
at most 64 tokens are kept; past that the oldest token's are forgotten. A connection without a token has no later
`welcome` to carry them, so its results are counted only.

## Old clients

There are none. Until stage 15 the mod also spoke version 1, chosen by a first line that was not a `hello`, and the
libraries fell back to it on an older mod. Both are gone, with no switch to bring them back: the mod and its clients
ship together and are updated together. Every connection's first message (after the shared secret over TCP) must be
`hello`; anything else is answered `protocol_error` (null id, `data.line_start`), then `goodbye` `protocol_error`, and
the connection closes. Blank lines before it are ignored and do not stop the 10-second first-line timeout. A client
that meets an older mod gets that mod's answer to `hello`, which is no `welcome`, and gives up with an error naming it.

## Appendix: one complete exchange

```text
-> {"type":"hello","protocol":[2],"client":{"name":"agents","version":"1.12.0","library":"stationgod-cs/1.12.0"}}
<- {"type":"welcome","protocol":2,"client_id":"c9","client":"anonymous","server":{...},"catalogue":{...},"limits":{...},"features":["shape","shape.paths","shape.omit","subscriptions","cancel","streams","cursors","rename"]}
-> {"type":"call","id":"a1","method":"grid_survey","params":{"room_id":"r12"},"shape":{"fields":["devices.reference_id","devices.prefab_name"]}}
-> {"type":"call","id":"a2","method":"game_clock","params":{}}
<- {"type":"reply","id":"a2","ok":true,"shaped":false,"result":{"game_time_s":84211.5,"paused":false,"time_of_day_ratio":0.41,"days_past":12},"elapsed_ms":0.02,"queue_ms":9.8,"frame":81301}
<- {"type":"reply","id":"a1","ok":true,"shaped":true,"result":{...},"elapsed_ms":38.4,"queue_ms":31.0,"frame":81302}
```
