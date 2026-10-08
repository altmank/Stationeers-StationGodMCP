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
| `hello` | First message, once. | `protocol` (array of integers, the major versions the client speaks, required), `client` (object: `name` string 1-64 characters of letters, digits, `-`, `_`, `.`; `version` string; `library` string, optional), `features` (array of strings the client understands, optional). |
| `call` | Any time after `welcome`. | `id` (string, 1-64 characters, required), `method` (required), `params` (object, optional, `{}` when absent), `shape` (object, optional, see *Shaping*), `deadline_ms` (integer 100-600000, optional, default 30000). |
| `cancel` | Any time after `welcome`. | `id` of a call in flight. |
| `bye` | Optional, before closing. | none |

### Server to client

| `type` | When | Keys |
| --- | --- | --- |
| `welcome` | After `hello`. | See below. |
| `reply` | Once per `call`. | `id`, `ok`, then `result` (when `ok`) or `error` (when not), `shaped` (true when the mod applied `shape`), `elapsed_ms` (main-thread time of the handler, as today, `src/StationGodMCP.Mod/Api/Views/HostViews.cs:10-50`), `queue_ms` (time from receipt to start), `frame` (the mod's frame counter when the handler ran). |
| `event` | Any time after `welcome`. | `event` (name), then the event's keys; see *Subscriptions* and *Connection events*. |
| `goodbye` | Before the server closes the connection on purpose. | `reason`: `shutting_down`, `world_unloaded`, `idle`, `slow_client`, `protocol_error`. |

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
  "catalogue": {"hash": "sha256:4f2a...", "methods": 92, "protocol_methods": 3},
  "limits": {
    "max_in_flight": 16,
    "max_request_bytes": 4194304,
    "max_reply_bytes": 16777216,
    "max_subscriptions": 64,
    "max_subscription_values": 8192,
    "max_values_per_subscription": 1024,
    "min_subscription_interval_s": 0.5
  },
  "features": ["shape", "shape.paths", "shape.omit", "subscriptions", "cancel"]
}
```

- `client_id`: the server's name for this connection, unique while the mod runs; `mod_info` lists connections by it,
  beside the name the client gave in `hello`.
- `client`: always `anonymous`; the name from `hello` is shown in `mod_info`.
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

A call's `id` MUST NOT equal the id of another call of the same connection still in flight. A call that reuses one is
answered at once with `duplicate_id` carrying that id; the earlier call is unaffected and the connection stays open.

### Order on one connection

A version-2 connection may have up to `limits.max_in_flight` calls in flight. A call beyond that is answered at once
with `too_many_in_flight` and never queued.

Replies may arrive in any order and are matched by `id`. The server keeps this order on a connection:

- A call whose effective class is write or cheat (see *Method classes*) does not start until every earlier
  call on the same connection has been answered.
- No call starts until every earlier write or cheat call on the same connection has been answered.
- Reads may overtake reads.

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

### Deadlines and cancelling

`deadline_ms` is how long the client will wait. A call that has not started when its deadline passes is dropped
unrun and answered `game_timeout`, as today (`src/StationGodMCP.Mod/StationGodRequestDispatcher.cs:80-96`); one that
has started runs to the end and is answered normally. A method whose catalogue entry declares `x-duration` (for
example `sample_logic`) gets its declared duration, at the arguments given, added to its deadline, and every client
timeout adds it too.

`cancel` drops a call that has not started; its reply is `cancelled`. A call that has started cannot be stopped and is
answered normally. Cancelling an unknown id does nothing.

### Replies

```json
{"type":"reply","id":"7","ok":true,"shaped":false,"result":{...},"elapsed_ms":1.37,"queue_ms":14.2,"frame":81240}
{"type":"reply","id":"8","ok":false,"error":{"code":"thing_not_found","message":"No thing with reference id 99."},"elapsed_ms":0.05,"queue_ms":3.0,"frame":81240}
```

`result` is the method's reply object as the catalogue describes it, after shaping. `elapsed_ms` and `frame` are absent
when the call never reached a handler (`cancelled`, `game_timeout`, `too_many_in_flight`, `duplicate_id`, protocol
errors), as `elapsed_ms` is today (`HostViews.cs:47-49`).

Inside the mod a call's reply is handed from the main thread to the connection's outbound queue when the handler and
serialising finish; no thread waits for it, unlike today's listener, which blocks per request
(`StationGodRequestDispatcher.cs:84`).

### Protocol methods

Three methods belong to the protocol itself. They are listed in the catalogue's `protocol_methods` section, are read
class, and are called like any other method.

| Method | Params | Result |
| --- | --- | --- |
| `catalogue` | `{}` | The full catalogue ([catalogue.md](catalogue.md)). |
| `subscribe` | See *Subscriptions*. | `{subscription, interval_s, values, frame, result}` |
| `unsubscribe` | `{subscription}` | `{subscription, ended: true}` |

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
| `invalid_argument` | The arguments break the catalogue (version 2) or a tool's own reading (both versions). Nothing ran. | `{problems: [{path, problem}]}` on version 2 | no |
| `invalid_shape` | Version 2 only: `shape` is malformed (see *Shaping*). Nothing ran. | `{problems}` | no |
| `duplicate_id` | The id is already in flight on this connection. Nothing ran. | none | no |
| `too_many_in_flight` | More calls in flight than `limits.max_in_flight`. Nothing ran. | `{limit}` | yes |
| `request_too_large` | A message longer than `limits.max_request_bytes`. The connection is closed. | `{limit}` | no |
| `reply_too_large` | The reply would exceed `shape.max_bytes` or `limits.max_reply_bytes`. The method ran. | `{bytes, limit, counts}` | only if the method is read class |
| `game_timeout` | The call was not started before its deadline. Nothing ran. As today. | none | yes |
| `cancelled` | Cancelled before it started. Nothing ran. | none | yes |
| `shutting_down` | The mod is stopping or the world is unloading; the call was not started. | none | yes |
| `game_changed` | A game member this method needs is missing in this game build. As today. | none | no |
| `internal_error` | A bug. The method may have partly run. As today. | none | no |
| `subscription_limit` | A subscription would exceed a per-subscription, connection or global limit. | `{limit, requested, projected_ms_per_s}` | no; poll instead |
| `unknown_subscription` | `unsubscribe` of an id this connection does not have. | none | no |

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
word more or fewer, singular for plural, two slips), before `fields_valid`.

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

## Subscriptions

A subscription asks the mod to read a set of device values at an interval and push the read when it changed. The
dashboard is the first consumer; the parts it needs are defined here. Further options (pushing only the changed
values, tolerances, real-time intervals, following building jobs) wait until a client needs them, and would come as
new `features`.

### Subscribing

`subscribe` params:

| Key | Type | Meaning |
| --- | --- | --- |
| `topic` | `devices` (default) or `world` | What to watch. |
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
  counted as `read_devices` counts them). These are sized to hold every card of the dashboard on one connection.
- Across all connections: the projected sampling cost must fit the subscription share of the frame budget
  ([scheduling.md](scheduling.md), *Subscriptions*).

A subscription that would pass a limit is refused with `subscription_limit`. A client so refused polls the same items
with `read_devices` at the same interval instead; the libraries report the refusal and the caller polls (the dashboard's runner already has that path).

### Ending

A subscription ends when the client unsubscribes, when the connection closes, or when the mod ends it with an event:

```json
{"type":"event","event":"subscription_ended","subscription":"s3","reason":"world_changed"}
```

Reasons: `world_changed` (reference ids may mean other things in another world), `limit` (the owner lowered a
limit). Subscriptions do not survive a reconnect. The libraries keep each
subscription's request and subscribe again after reconnecting only when `welcome.server.world.id` is the one they last
saw; otherwise they close it and tell the caller ([clients.md](clients.md), *Subscriptions*).

### sample_logic

`sample_logic` keeps today's arguments, limits and reply: targets as `read_logic_many` takes them (1 to 32), interval
0.05 to 5 seconds, duration 0.1 to 30 seconds, at most 120 samples, on the real clock, running whether or not the game
is paused, and reporting the changes with elapsed real seconds (`src/StationGodMCP.Server/Program.cs:332-455`,
`:359-376`, `:448`). It is not a subscription: it has its own sampler in the mod, which takes each sample in the first
frame at or after it is due and counts in the subscription lane's time ([scheduling.md](scheduling.md)). Its catalogue
entry declares `x-duration` from `duration_seconds`, so the call's deadline and every client's wait for its reply add
the duration.

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

## Reconnecting

A connection can break at any time: the game closed, the save reloaded, the mod restarted. The rules a client follows
(the libraries do this; [clients.md](clients.md)):

- A call whose `call` line could not be written did not reach the game; it may be sent again on a new connection.
- A call that was written but not answered may have run. It may be sent again automatically only if its effective
  class is read and its catalogue entry has no `x-effects` (from the catalogue, at the arguments given). This is
  today's dashboard rule (StationeersScriptDashboard `stationscript/transport.py:84-110`), with the catalogue instead of a
  hand-kept list; that list already leaves out `highlight` and `show_preview`, which draw (`transport.py:23`).
- Any automatic resend, and any resubscribe, happens only when the new `welcome.server.world.id` equals the last one
  seen. A different id means another world: every reference id the client holds may now name something else.

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
<- {"type":"welcome","protocol":2,"client_id":"c9","client":"anonymous","server":{...},"catalogue":{...},"limits":{...},"features":["shape","shape.paths","shape.omit","subscriptions","cancel"]}
-> {"type":"call","id":"a1","method":"grid_survey","params":{"room_id":"r12"},"shape":{"fields":["devices.reference_id","devices.prefab_name"]}}
-> {"type":"call","id":"a2","method":"game_clock","params":{}}
<- {"type":"reply","id":"a2","ok":true,"shaped":false,"result":{"game_time_s":84211.5,"paused":false,"time_of_day_ratio":0.41,"days_past":12},"elapsed_ms":0.02,"queue_ms":9.8,"frame":81301}
<- {"type":"reply","id":"a1","ok":true,"shaped":true,"result":{...},"elapsed_ms":38.4,"queue_ms":31.0,"frame":81302}
```
