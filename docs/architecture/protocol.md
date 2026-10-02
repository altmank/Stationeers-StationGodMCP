# The StationGod wire protocol, version 2

[Back to the overview](README.md)

This page is the contract between the mod and every client: the sidecar, the client libraries, and anything else
that connects to the pipe or the TCP port. It starts with a short tour, then gives the exact rules. Words in capitals
(MUST, SHOULD, MAY) have their usual meaning in specifications.

## A short tour

A client opens the pipe (or a TCP connection), sends one `hello` line naming the protocol it speaks, and gets one
`welcome` line back that says who it is now (its client name and permission level), which world it reached, and the
limits that apply. From then on it sends `call` lines and receives `reply` lines, matched by id, several at a time if
it likes. If it subscribed to device values, `event` lines arrive between replies whenever those values change.

```text
-> {"type":"hello","protocol":[2],"client":{"name":"dashboard","version":"3.0.0"}}
<- {"type":"challenge","nonce":"q8Xy..."}
-> {"type":"auth","client":"dashboard","proof":"5d0c..."}
<- {"type":"welcome","protocol":2,"client_id":"c4","level":"write", ...}
-> {"type":"call","id":"1","method":"thing_health","params":{"reference_ids":["364","365"]},"shape":{"fields":["reference_id","damage_ratio"]}}
<- {"type":"reply","id":"1","ok":true,"result":{"results":[{"reference_id":"364","damage_ratio":0.0}, ...]},"elapsed_ms":0.41}
```

A client that sends an ordinary request as its first line instead of `hello` is served exactly as today; see
*Old clients*.

## Framing

Both transports carry the same byte stream.

- A message is one JSON object encoded as UTF-8 without a byte-order mark, followed by one line feed (`\n`, 0x0A). A
  carriage return before the line feed is ignored. Blank lines are ignored, as today
  (`src/StationGodMCP.Mod/StationGodPipeServer.cs:189`).
- A message never contains a raw line feed: JSON escapes it inside strings. Both sides MUST write compact JSON.
- A client message longer than `limits.max_request_bytes` (default 4 MiB) closes the connection after a
  `request_too_large` error reply with a null id. Today's protocol has no such limit (`StationGodPipeServer.cs:186-194`
  reads lines without a bound); the limit applies to version-2 connections only.
- A reply longer than `limits.max_reply_bytes` (default 16 MiB) is not sent; the request is answered with
  `reply_too_large` (see *Errors*), whose `data` carries the size and the length of every top-level list so the
  caller can narrow the request.
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
| `hello` | First message, once. | `protocol` (array of integers, the major versions the client speaks, required), `client` (object: `name` string 1-64 characters of letters, digits, `-`, `_`, `.`; `version` string; `library` string, optional), `features` (array of strings the client understands, optional), `auth` (`"key"` when the client will prove a key, else omitted). |
| `auth` | After `challenge`, once. | `client` (the key's client name), `proof` (lowercase hex HMAC-SHA256, see *Sign-in*). |
| `call` | Any time after `welcome`. | `id` (string, 1-64 characters, unique among this connection's requests in flight, required), `method` (required), `params` (object, optional, `{}` when absent), `shape` (object, optional, see *Shaping*), `deadline_ms` (integer 100-600000, optional, default 30000). |
| `cancel` | Any time after `welcome`. | `id` of a call in flight. |
| `bye` | Optional, before closing. | none |

### Server to client

| `type` | When | Keys |
| --- | --- | --- |
| `challenge` | After a `hello` with `auth: "key"`. | `nonce` (base64 of 32 random bytes). |
| `welcome` | After `hello` (no key) or after a good `auth`. | See below. |
| `reply` | Once per `call`. | `id`, `ok`, then `result` (when `ok`) or `error` (when not), `elapsed_ms` (main-thread time of the handler, as today, `src/StationGodMCP.Mod/Api/Views/HostViews.cs:10-50`), `queue_ms` (time from receipt to start). |
| `event` | Any time after `welcome`. | `event` (name), then the event's keys; see *Subscriptions* and *Connection events*. |
| `goodbye` | Before the server closes the connection on purpose. | `reason`: `shutting_down`, `world_unloaded`, `idle`, `slow_client`, `protocol_error`, `revoked`. |

The `welcome` message:

```json
{
  "type": "welcome",
  "protocol": 2,
  "client_id": "c4",
  "client": "dashboard",
  "level": "write",
  "grants": ["vault_deposit"],
  "cheat": {"armed": false, "until_utc": null, "standing": false},
  "server": {
    "mod_version": "1.12.0",
    "pipe_name": "StationGodMCP",
    "transport": "pipe",
    "role": "host",
    "dedicated": false,
    "world_epoch": 3,
    "game_state": "Running"
  },
  "catalogue": {"hash": "sha256:4f2a...", "methods": 92, "protocol_methods": 3},
  "limits": {
    "max_in_flight": 16,
    "max_request_bytes": 4194304,
    "max_reply_bytes": 16777216,
    "max_subscriptions": 32,
    "max_subscription_values": 1024,
    "min_subscription_interval_s": 0.5
  },
  "features": ["shape", "shape.paths", "subscriptions", "cancel"]
}
```

`client_id` is the server's name for this connection, unique while the mod runs; `mod_info` lists connections by it.
`client` is the key's client name, or `anonymous`. `server.world_epoch` counts worlds left since the mod loaded, as
`mod_info.runtime.world_epoch` does today (`docs/devices-and-logic.md`, *Health and game updates*).

## Versions and negotiation

The protocol has a major version, an integer. This page defines 2; version 1 is today's protocol.

1. The client sends `hello` with every major version it speaks.
2. The server picks the highest one it also speaks and answers in it. If there is none, it answers
   `{"type":"reply","id":null,"ok":false,"error":{"code":"unsupported_protocol","message":"...","data":{"supported":[2]}}}`
   and closes.
3. Within a major version, additions are announced by name in `welcome.features` and by the catalogue's hash.
   A client MUST NOT use a feature the server did not list. The server MUST accept every message this page defines
   for the version it chose.

The catalogue has its own identity, `catalogue.hash`: SHA-256 of the canonical catalogue (keys sorted, no
whitespace). A client that carries a built-in catalogue compares hashes and fetches the server's with the protocol
method `catalogue` when they differ ([catalogue.md](catalogue.md), *Who reads it*).

Methods evolve inside the catalogue, not by protocol version. Adding a method, an optional argument or a reply key is
compatible. Renaming or removing one keeps the old name as a deprecated alias for at least one minor release, listed in
the catalogue, as `thing_health` kept `min_ratio` (CHANGELOG 1.x, *Arguments are checked against each tool's schema*).

## Calls and replies

### Order on one connection

A version-2 connection may have up to `limits.max_in_flight` calls in flight. A call beyond that is answered at once
with `too_many_in_flight` and never queued.

Replies may arrive in any order and are matched by `id`. The server keeps this order on a connection:

- A call whose effective class is write or cheat (see *Sign-in and permissions*) does not start until every earlier
  call on the same connection has finished.
- No call starts until every earlier write or cheat call on the same connection has finished.
- Reads may overtake reads.

So a client that sends a write and then a read on one connection reads after the write, and a client that sends two
reads gets them in whatever order the scheduler finds cheapest. Across connections there is no order beyond what the
game's main thread imposes.

### Deadlines and cancelling

`deadline_ms` is how long the client will wait. A call that has not started when its deadline passes is dropped
unrun and answered `game_timeout`, as today (`src/StationGodMCP.Mod/StationGodRequestDispatcher.cs:80-96`); one that
has started runs to the end and is answered normally. A method whose catalogue entry declares `x-duration` (for
example `sample_logic`) gets its declared duration added to its deadline.

`cancel` drops a call that has not started; its reply is `cancelled`. A call that has started cannot be stopped and is
answered normally. Cancelling an unknown id does nothing.

### Replies

```json
{"type":"reply","id":"7","ok":true,"result":{...},"elapsed_ms":1.37,"queue_ms":14.2}
{"type":"reply","id":"8","ok":false,"error":{"code":"thing_not_found","message":"No thing with reference id 99."},"elapsed_ms":0.05,"queue_ms":3.0}
```

`result` is the method's reply object as the catalogue describes it, after shaping. `elapsed_ms` is absent when the
call never reached a handler (`cancelled`, `game_timeout`, `too_many_in_flight`, protocol errors), as today
(`HostViews.cs:47-49`).

### Protocol methods

Three methods belong to the protocol itself. They are listed in the catalogue's `protocol_methods` section, are read
class, and are called like any other method.

| Method | Params | Result |
| --- | --- | --- |
| `catalogue` | `{}` | The full catalogue ([catalogue.md](catalogue.md)). |
| `subscribe` | See *Subscriptions*. | `{subscription, interval_s, values, snapshot}` |
| `unsubscribe` | `{subscription}` | `{subscription, ended: true}` |

## Errors

Every error is `{code, message}` with an optional `data` object, today's shape plus `data`
(`src/StationGodMCP.Mod/Api/Shared/CommonViews.cs:78-89`). `message` is for people and may change; `code` is for
programs and does not.

Tool errors keep their codes (there are well over a hundred, from `thing_not_found` to `gas_check_failed`); the
catalogue lists them all with a description ([catalogue.md](catalogue.md), *Errors*). The protocol adds or fixes the
meaning of these:

| Code | Meaning | `data` | Safe to resend |
| --- | --- | --- | --- |
| `protocol_error` | A message the protocol does not allow: not a JSON object, unknown `type`, unknown top-level key, a call before `welcome`, a repeated `hello`. The connection is closed after the reply. | `{line_start}` (first 80 characters) | no |
| `unsupported_protocol` | No common major version. | `{supported}` | no |
| `unauthorized` | Bad or missing key proof, unknown client name, an anonymous TCP connection, or a key not allowed on this transport. The connection is closed. Same code as today's TCP refusal (`src/StationGodMCP.Mod/StationGodTcpServer.cs:187-192`). | none | no |
| `permission_denied` | The method, at the arguments given, needs a higher level than the connection has. Nothing ran. | `{required, level, method}` | no |
| `cheat_not_armed` | The connection's level allows cheat, but the owner has not switched cheats on for it. Nothing ran. | `{client}` | no |
| `method_not_found` | No such method. As today (`ApiHost.cs:159-161`). | `{nearest}` when one is close | no |
| `invalid_argument` | The arguments break the catalogue: unknown name at any level where the schema closes it, wrong type, value out of range or not in the enum, missing required argument, array too short or long, a key given twice. Nothing ran. | `{problems: [{path, problem}]}` | no |
| `invalid_shape` | `shape` is malformed (see *Shaping*). Nothing ran. | `{problems}` | no |
| `too_many_in_flight` | More calls in flight than `limits.max_in_flight`. Nothing ran. | `{limit}` | yes |
| `request_too_large` | A message longer than `limits.max_request_bytes`. The connection is closed. | `{limit}` | no |
| `reply_too_large` | The reply would exceed `shape.max_bytes` or `limits.max_reply_bytes`. The method ran. | `{bytes, limit, counts}` | only if the method is read class |
| `game_timeout` | The call was not started before its deadline. Nothing ran. As today. | none | yes |
| `cancelled` | Cancelled before it started. Nothing ran. | none | yes |
| `shutting_down` | The mod is stopping or the world is unloading; the call was not started. | none | yes |
| `game_changed` | A game member this method needs is missing in this game build. As today. | none | no |
| `internal_error` | A bug. The method may have partly run. As today. | none | no |
| `subscription_limit` | A subscription would exceed a connection or global limit. | `{limit, requested, projected_ms_per_s}` | no |
| `unknown_subscription` | `unsubscribe` of an id this connection does not have. | none | no |

"Safe to resend" means a client library MAY send the same call again automatically. Every other resend is the
caller's decision. Connection failures are not error replies; see *Reconnecting*.

## Shaping

Shaping says which parts of a reply the caller wants. The mod applies it while it writes the reply, so keys left out
cost neither serialisation nor transport, and, where the method declares it, the handler skips computing them.

`shape` is an optional object on a `call`:

| Key | Type | Meaning |
| --- | --- | --- |
| `fields` | array of 1-64 selector strings | Which keys to keep in the entries of the reply's lists. |
| `limit` | object: list name to integer 0-100000 | Keep at most this many entries of that top-level list. |
| `max_bytes` | integer 1024-16777216 | Refuse to send a reply larger than this (`reply_too_large`). |

An unknown key in `shape`, a selector that breaks the grammar below, or a `limit` naming a key the catalogue says is
not a top-level list of this method, is `invalid_shape`.

### Field selectors

A selector is one or more names joined by dots. A name is the wire name of a key, matched exactly (case matters).

```text
selector = name *( "." name )
name     = 1*( lowercase letter / digit / "_" )
```

A selector with one name, such as `reference_id`, means what `fields` means today in the sidecar
(`src/StationGodMCP.Server/ReplyShaping.cs:55-98`): in every object entry of every top-level list of the reply, keep
that key. It does not touch top-level keys that are not lists, nor entries that are not objects.

A selector with two or more names, such as `results.damage_ratio` or `things.position.x`, applies only to the
top-level list named by its first name, and inside each object entry of that list keeps the path given by the rest:
the key named by the second name, and, if there is a third, only that key inside the second's object value, and so
on. A path through a value that is a list applies to every object entry of that list. A list named by a path selector
is projected only by path selectors that name it; single-name selectors also apply to it.

When several selectors keep the same key at the same place, the key is kept with the union of what they keep below
it; a selector that names a key without going deeper keeps that key's whole value.

After applying the selectors, the mod adds `fields_unmatched` to the reply's top-level object, an array of every
selector that matched no key in any entry, when the reply had at least one object entry in a list it applied to.
This is today's rule (`ReplyShaping.cs:89-94`) extended to paths. A reply with no list entries gets no
`fields_unmatched`, as today.

Errors are never shaped: an error reply is always whole.

The reference for these rules is the sidecar's `FieldSelection` for single names; the mod's implementation MUST give
byte-identical results to it on every fixture of `tests/StationGodMCP.Tests/FileOutputTests.cs` and
`ReplySlimmingTests.cs` (see [stages.md](stages.md), stage 1).

### List limits

`limit: {"things": 20}` keeps the first 20 entries of the top-level list `things` after the method has built it. This
saves serialisation and transport, not the method's own work; a method with its own paging arguments (`offset`,
`limit`, which the catalogue marks with `x-paging`) saves both and is preferred. When `limit` cuts a list, the mod adds
`shape_truncated: {"things": 84}` (each cut list's length before the cut) to the reply's top-level object.

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

## Sign-in and permissions

### Levels and classes

Every connection has a level: `read`, `write` or `cheat`, each including the ones before it.

Every method has a class in the catalogue: `read` (changes nothing in the world), `write` (changes the world the way a
player or a chip can, through the game's own rules) or `cheat` (does what no player can). A method's class may depend
on its arguments through `x-class-when` (for example every building tool is read class while `dry_run` is true or
absent, because a dry run changes nothing). The class worked out from the arguments is the call's *effective class*.

A call runs only if the connection's level is at least the effective class, or the method is in the connection's
`grants`. A cheat call also needs cheat to be armed for the connection (below). Otherwise the call is refused before
anything runs: `permission_denied` or `cheat_not_armed`.

### Keys

The owner issues keys in `BepInEx\config\net.xceled.stationeers.stationgodmcp.clients.json`, next to the mod's config
file (`src/StationGodMCP.Mod/StationGodMod.cs:175`):

```json
{
  "clients": [
    {"name": "dashboard", "key": "base64-of-32-random-bytes", "level": "write", "grants": [], "transports": ["pipe"]},
    {"name": "agents", "key": "...", "level": "cheat", "cheat": "armed", "transports": ["pipe", "tcp"]},
    {"name": "owner-scripts", "key": "...", "level": "cheat", "cheat": "standing", "transports": ["pipe"]}
  ]
}
```

- `name`: unique, the grammar of `hello.client.name`.
- `key`: at least 32 random bytes, base64. The mod refuses to load a shorter key (logged, that client disabled).
- `level`: the ceiling.
- `grants`: method names allowed beyond the level (an escape hatch for one method; empty by default).
- `cheat`: `armed` (default; cheat calls need the in-game switch) or `standing` (no switch needed).
- `transports`: where the key may be used.

The file is read at load and again when its timestamp changes, as the lint rules file is
(`CLAUDE.md`, State, 1.7.0: `Game/Lint/LintRuleFiles`). A client removed or downgraded while connected gets
`goodbye {reason: "revoked"}` and is closed. A missing file means no keys. The mod writes the file only through the
console command `stationgod key new <name> <level>` (below), never on its own.

### Proving a key

The key itself never crosses the wire.

1. Client: `hello` with `auth: "key"`.
2. Server: `challenge` with a fresh 32-byte nonce.
3. Client: `auth` with `client` (its name in the clients file) and `proof` =
   hex(HMAC-SHA256(key bytes, UTF-8 of `"stationgod-v2\n" + nonce + "\n" + client + "\n" + transport`)), where
   `transport` is `pipe` or `tcp`.
4. Server: compares in fixed time, as today (`StationGodTcpServer.cs:231-245`); `welcome` on success, `unauthorized`
   and close on failure. Three failures from one remote address within a minute make that address wait 10 seconds
   before its next connection is read.

This stops a passive listener from learning the key. It does not stop a listener from reading or altering the
session after sign-in; for that the connection needs a VPN or private network (see *Transports*).

### Connections without a key

A pipe connection whose `hello` has no `auth` gets the level `[Access] AnonymousPipeLevel` (`read`, `write`, `cheat`
or `none`, default `cheat`, today's behaviour; the owner's question in the overview). Anonymous cheat is standing:
no switch needed, as today. `none` refuses anonymous connections with `unauthorized`.

A TCP connection without `auth` is refused, `unauthorized`.

### The in-game switch for cheats

The mod adds console commands through the game's own `CommandLine.AddCommand` (game decompile
`Util.Commands/CommandLine.cs:144`):

| Command | Effect |
| --- | --- |
| `stationgod allow <client> [minutes]` | Arms cheat for that client name for the given minutes (default 15, at most 240). Connected and future connections of that client are armed until then. An `event` `cheat_armed` goes to its connections. |
| `stationgod deny <client>` | Disarms at once; `cheat_disarmed` event. |
| `stationgod clients` | Lists connections: client id, name, transport, level, armed until, calls in flight, subscriptions. |
| `stationgod key new <name> <level>` | Adds a client to the clients file with a fresh key and prints the key once. |

Arming lives in memory only; it ends when the game closes. `run_console_command` is cheat class, so a connection that
is not already armed cannot arm itself through it.

### What levels are, honestly

On one Windows account every local process can read every file the owner can, including the clients file, and can
open the pipe. Levels protect against mistakes and against an agent that follows its instructions; they are not a
security boundary against a local program that sets out to get round them. Over TCP, keys are a real boundary as far
as the network is private.

## Transports

### Named pipe

- Name: `\\.\pipe\<[Pipe] Name>`, as today (`src/StationGodMCP.Mod/StationGodMod.cs:327-347`; `docs/configuration.md`).
- Instances: `[Server] MaxConnections`, default 32, shared with TCP. Today the pipe has 4 instances
  (`StationGodPipeServer.cs:23`), too few once each agent session, the dashboard and scripts each keep a connection.
- Each connection has its own reading thread and writes through a per-connection outbound queue, so the main thread
  never writes to a pipe. The first line must arrive within 10 seconds, as today (`StationGodPipeServer.cs:262-303`).
- Access: the pipe is created with the default security of the game's process. Which local accounts that admits is
  GUESS (believed: full access for the owner's account, administrators and SYSTEM, read-only for others, which is not
  enough to send a request).

### TCP

- Settings `[Remote MCP] Enabled`, `BindAddress`, `Port`, as today (`StationGodMod.cs:198-295`). The default bind
  address stays `0.0.0.0` for compatibility; the documentation tells owners to bind the VPN's interface address.
- Same messages as the pipe. `hello` MUST carry `auth: "key"`.
- No TLS. The mod does not encrypt. Unity's Mono can host `SslStream` in principle, but certificate handling for
  players is a support burden and untested in this game (GUESS); a private network or VPN (WireGuard, Tailscale,
  ZeroTier) is the supported way to reach a game on another machine.
- Keepalive: the server sends `{"type":"event","event":"ping"}` after 30 seconds of silence on a connection; a client
  that has sent nothing and received no reply for 120 seconds may be closed with `goodbye {reason: "idle"}` unless it
  holds subscriptions.

### Outbound queue and slow clients

Each connection's outbound queue holds replies and events in order. Replies are never dropped. If a connection's queue
holds more than 8 MiB or the oldest message waits more than 35 seconds (today's send timeout,
`StationGodTcpServer.cs:27`), the connection is closed with `goodbye {reason: "slow_client"}`; its calls not yet
started are dropped and its subscriptions end. Events are merged before that point (see *Subscriptions*).

## Subscriptions

A subscription asks the mod to read a set of device values at an interval and push what changed.

### Subscribing

`subscribe` params:

| Key | Type | Meaning |
| --- | --- | --- |
| `topic` | `devices` (default), `world`, `job` | What to watch. |
| `items` | array, as `read_devices` `items` | For `devices`: what to read. Same parts, same bounds (`src/StationGodMCP.Mod/Pure/DeviceReads/DeviceReadRequest.cs:16-22`: at most 128 items and 1,024 values). |
| `include` | array, as `read_devices` `include` | For `devices`: `clock`. |
| `gateway_id` | string | For `devices`, as `read_devices`. |
| `job_id` | string | For `job`: the job to follow. |
| `interval_s` | number 0.5-3600 | For `devices`: how often to read, in game seconds by default. Default 1. |
| `clock` | `game` (default) or `real` | Game-time intervals stop while the game is paused. |
| `mode` | `changes` (default) or `snapshot` | `changes` pushes only changed values; `snapshot` pushes the whole read whenever anything changed. |
| `tolerance` | object: logic or gas name to `{abs, rel}` | For `devices`: a number counts as changed only if it moved by more than `abs` or by more than `rel` times its last sent value. Default: any change. |

The reply: `{subscription: "s3", interval_s: 1.0, values: 412, snapshot: <a full read_devices result>}`. The snapshot
is the first reading; events carry changes against it.

Topics:

- `devices`: device reads as above.
- `world`: no params; pushes `world_changed` when the world epoch moves (a save loaded or left) and `game_state` when
  the game state changes (Running, Paused, Loading). Cheap; every library subscribes to it.
- `job`: pushes `job` events when a building job's status changes, so a client need not poll `job_id`
  (`src/StationGodMCP.Mod/Api/Shared/Game/HeldTickJobs.cs:138`).

### Update events

```json
{"type":"event","event":"update","subscription":"s3","seq":42,"game_time_s":84211.5,"late_ms":0,
 "changes":{"results":[{"index":0,"logic":{"Temperature":881.2}},{"index":3,"ok":false,"error":{"code":"thing_not_found","message":"..."}}]}}
```

- `seq` counts from 1 per subscription and grows by one per event. A client that sees a gap has missed events and
  MUST treat its state as stale until the next `resync`.
- `changes` in `changes` mode has the shape of a `read_devices` result holding only what changed: an item appears with
  its `index` and only the parts and values that changed; an item that started or stopped failing appears with `ok` and
  `error`. Applying `changes` to the last state, part by part and key by key, gives the new state. In `snapshot` mode
  the event carries `result`, the whole read, instead of `changes`.
- All values of one event were read in the same frame, so a control loop sees one consistent state, as
  `read_devices` gives today.
- `late_ms`: how much later than due this read was, because of the frame budget (see [scheduling.md](scheduling.md)).

### Merging, resyncing and limits

- If a subscription's previous event is still in the connection's outbound queue when a new reading is due, the two
  are merged into one event (newest value wins) and `seq` is not increased for the merged one.
- If merging is not possible (the queue is past half its limit), the mod drops the subscription's queued events and
  sends, as soon as the queue drains below a quarter, one event with `"resync": true` carrying the whole state as
  `result`, with the next `seq`.
- Limits per connection: `limits.max_subscriptions` (32) and `limits.max_subscription_values` (1,024 values across
  them, counted as `read_devices` counts them). Across all connections, the projected sampling cost must fit the
  subscription share of the frame budget ([scheduling.md](scheduling.md), *Subscriptions*); a subscription that would
  not fit is refused with `subscription_limit`.

### Ending

A subscription ends when the client unsubscribes, when the connection closes, or when the mod ends it with an event:

```json
{"type":"event","event":"subscription_ended","subscription":"s3","reason":"world_changed"}
```

Reasons: `world_changed` (reference ids may mean other things in another world), `revoked` (the client lost the
level), `limit` (the owner lowered a limit). Subscriptions do not survive a reconnect. The libraries keep each
subscription's request and subscribe again after reconnecting, and tell the caller with a fresh snapshot that the
state may have jumped ([clients.md](clients.md), *Subscriptions*).

### Connection events

| `event` | Keys | When |
| --- | --- | --- |
| `ping` | none | After 30 s of silence. |
| `world_changed` | `world_epoch` | The world epoch moved (sent to every connection, subscribed or not). |
| `cheat_armed` | `until_utc` | The owner armed cheat for this client. |
| `cheat_disarmed` | none | The owner disarmed it, or the time ran out. |

## Reconnecting

A connection can break at any time: the game closed, the save reloaded, the mod restarted. The rules a client follows
(the libraries do this; [clients.md](clients.md)):

- A call whose `call` line could not be written did not reach the game; it may be sent again on a new connection.
- A call that was written but not answered may have run. It may be sent again automatically only if its effective
  class is read (from the catalogue, at the arguments given). This is today's dashboard rule
  (StationeersScriptDashboard `stationscript/transport.py:84-110`), with the catalogue instead of a hand-kept list.
- After reconnecting, compare `welcome.server.world_epoch` with the last one seen: if it moved, every reference id
  the client holds may now name something else.

## Old clients

The mod tells a version-1 client from a version-2 one by its first line: an object with `type: "hello"` starts
version 2; anything else is a version-1 request (on the pipe) or a version-1 sign-in (on TCP).

A version-1 connection is served exactly as today:

- One request line, one reply line, in order (`StationGodPipeServer.cs:174-209`). The reply envelope is today's
  `{id, ok, result, elapsed_ms}` or `{id, ok, error, elapsed_ms}`, no `type`
  (`src/StationGodMCP.Mod/Api/Views/HostViews.cs:10-50`).
- Arguments are checked only for unknown top-level names, as today (`DeclaredArguments.cs:51-77`), and otherwise read
  leniently by each tool (ids as JSON integers accepted, `src/StationGodMCP.Server/ArgumentCheck.cs:17`).
- A `shape` key in a version-1 request is honoured with the rules above. Old clients never send one, and this lets a
  version-1 client get shaping before it moves to version 2. This is the only version-1 change.
- Level: on the pipe, `[Access] LegacyPipeLevel` (default `cheat`, today); on TCP, today's `{type: "auth", secret}`
  first line against `[Remote MCP] Secret` (`StationGodTcpServer.cs:174-229`), at `[Access] LegacyTcpLevel` (default
  `cheat`). Either may be set to `none` to refuse version 1 there.
- Permissions apply to version-1 calls too, at that level. With the defaults nothing changes.

The other direction: a version-2 client against an old mod. The old mod reads `hello` as a request with no method and
answers `{"id":null,"ok":false,"error":{"code":"method_not_found",...}}` (`ApiHost.cs:157-161`). The libraries take
that answer as "version 1 only" and fall back: they speak version 1 on the same connection, apply `fields`
themselves (the sidecar's rules), emulate subscriptions by polling `read_devices`, and treat every method not in their
built-in catalogue's read class as unsafe to resend. Over TCP the old mod closes the connection after the failed
sign-in (`StationGodTcpServer.cs:187-192`); the library then reconnects with version 1 if it was given a legacy secret,
and otherwise reports that the server is too old.

## Appendix: one complete exchange

```text
-> {"type":"hello","protocol":[2],"client":{"name":"agents","version":"1.12.0","library":"stationgod-cs/1.12.0"},"auth":"key"}
<- {"type":"challenge","nonce":"wUq0x0...=="}
-> {"type":"auth","client":"agents","proof":"9f1c..."}
<- {"type":"welcome","protocol":2,"client_id":"c9","client":"agents","level":"cheat","grants":[],"cheat":{"armed":false,"until_utc":null,"standing":false},"server":{...},"catalogue":{...},"limits":{...},"features":["shape","shape.paths","subscriptions","cancel"]}
-> {"type":"call","id":"a1","method":"grid_survey","params":{"room_id":"r12"},"shape":{"fields":["devices.reference_id","devices.prefab_name"]}}
-> {"type":"call","id":"a2","method":"game_clock","params":{}}
<- {"type":"reply","id":"a2","ok":true,"result":{"game_time_s":84211.5,"paused":false,"time_of_day_ratio":0.41,"days_past":12},"elapsed_ms":0.02,"queue_ms":9.8}
<- {"type":"reply","id":"a1","ok":true,"result":{...},"elapsed_ms":38.4,"queue_ms":31.0}
-> {"type":"call","id":"a3","method":"move_gas","params":{"from":"1201","to":"1305","gases":["Oxygen"],"amount_mol":10}}
<- {"type":"reply","id":"a3","ok":false,"error":{"code":"cheat_not_armed","message":"move_gas is a cheat tool; the owner has not allowed cheats for 'agents'. In the game console: stationgod allow agents","data":{"client":"agents"}}}
```
