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
-> {"type":"hello","protocol":[2],"client":{"name":"dashboard","version":"3.0.0"},"auth":"key"}
<- {"type":"challenge","nonce":"q8Xy..."}
-> {"type":"auth","client":"dashboard","proof":"5d0c..."}
<- {"type":"welcome","protocol":2,"client_id":"c4","level":"write", ...}
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
| `hello` | First message, once. | `protocol` (array of integers, the major versions the client speaks, required), `client` (object: `name` string 1-64 characters of letters, digits, `-`, `_`, `.`; `version` string; `library` string, optional), `features` (array of strings the client understands, optional), `auth` (`"key"` when the client will prove a key, else omitted). |
| `auth` | After `challenge`, once. | `client` (the key's name; MUST equal `hello.client.name`), `proof` (lowercase hex, see *Proving a key*). |
| `call` | Any time after `welcome`. | `id` (string, 1-64 characters, required), `method` (required), `params` (object, optional, `{}` when absent), `shape` (object, optional, see *Shaping*), `deadline_ms` (integer 100-600000, optional, default 30000). |
| `cancel` | Any time after `welcome`. | `id` of a call in flight. |
| `bye` | Optional, before closing. | none |

### Server to client

| `type` | When | Keys |
| --- | --- | --- |
| `challenge` | After a `hello` with `auth: "key"`. | `nonce` (base64 of 32 random bytes). |
| `welcome` | After `hello` (no key) or after a good `auth`. | See below. |
| `reply` | Once per `call`. | `id`, `ok`, then `result` (when `ok`) or `error` (when not), `shaped` (true when the mod applied `shape`), `elapsed_ms` (main-thread time of the handler, as today, `src/StationGodMCP.Mod/Api/Views/HostViews.cs:10-50`), `queue_ms` (time from receipt to start), `frame` (the mod's frame counter when the handler ran). |
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
  "grants": [],
  "cheat": {"armed": false, "until_utc": null, "standing": false},
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
  "features": ["shape", "shape.paths", "subscriptions", "cancel"]
}
```

- `client_id`: the server's name for this connection, unique while the mod runs; `mod_info` and the console list
  connections by it, and the owner approves cheats for it.
- `client`: the key's name, or `anonymous`.
- `server.instance_id`: random, new each time the mod loads.
- `server.world.id`: random, new each time a world finishes loading. It is the world's identity for clients: two
  welcomes with the same `world.id` reached the same loaded world, across reconnects. A game restart or a save load
  always gives a new id. `world.save` is the save's name where the game exposes it (which member is GUESS; null if
  none), for people to read; `world.epoch` is today's counter of worlds left since the mod loaded, which restarts at 0
  with the game (`src/StationGodMCP.Mod/Pure/WorldScope.cs:21-22`, `:43`; `src/StationGodMCP.Mod/Api/ModInfo.cs:44`)
  and so is not an identity.

## Versions and negotiation

The protocol has a major version, an integer. This page defines 2; version 1 is today's protocol.

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

- A call whose effective class is write or cheat (see *Sign-in and permissions*) does not start until every earlier
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

Tool errors keep their codes (well over a hundred, from `thing_not_found` to `gas_check_failed`); the catalogue lists
them all with a description ([catalogue.md](catalogue.md), *Errors*). The protocol adds or fixes the meaning of these.
"Caller may resend" is advice to the program that made the call; the client libraries never resend an error reply on
their own (see [clients.md](clients.md), *Reconnecting*).

| Code | Meaning | `data` | Caller may resend |
| --- | --- | --- | --- |
| `protocol_error` | A message the protocol does not allow: not a JSON object, unknown `type`, unknown top-level key, a call before `welcome`, a repeated `hello`, sign-in not finished within 10 seconds of connecting. The connection is closed after the reply. | `{line_start}` (first 80 characters) | no |
| `unsupported_protocol` | No common major version. | `{supported}` | no |
| `unauthorized` | Bad or missing key proof, unknown client name, `auth.client` not equal to `hello.client.name`, an anonymous TCP connection, or a key not allowed on this transport. The connection is closed. Same code as today's TCP refusal (`src/StationGodMCP.Mod/StationGodTcpServer.cs:187-192`). | none | no |
| `permission_denied` | The method, at the arguments given, needs a higher level than the connection has; or `run_console_command` was asked to run a `stationgod` command. Nothing ran. | `{required, level, method}` | no |
| `cheat_not_armed` | The connection's level allows cheat, but the owner has not approved it now. Nothing ran. | `{client_id, client}` | no |
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

`shape` is an optional object on a `call`, and on a version-1 request (see *Old clients*):

| Key | Type | Meaning |
| --- | --- | --- |
| `fields` | array of selector strings, at least 1 | Which keys to keep in the entries of the reply's lists. |
| `limit` | object: list name to integer 0-100000 | Keep at most this many entries of that top-level list. |
| `max_bytes` | integer 1024-16777216 | Refuse to send a reply larger than this (`reply_too_large`). |

When the mod applied a `shape`, its reply envelope carries `shaped: true`. A client that applies `fields` itself for
old mods (the sidecar, the libraries' version-1 fallback) MUST NOT apply it again to a reply marked `shaped`.

### Field selectors

A selector is one or more names joined by dots, after trimming surrounding white space as the sidecar does today
(`src/StationGodMCP.Server/ReplyShaping.cs:65`). A name is matched exactly, case included, against a key as it appears
on the wire: object properties (snake case) and dictionary keys alike, which keep their own case (`Temperature` in
logic values, gas names; `ApiJson.cs:51-55`, `ProcessDictionaryKeys = false`).

```text
selector = name *( "." name )
name     = 1*( ALPHA / DIGIT / "_" )
```

A selector with one name, such as `reference_id`, means what `fields` means today in the sidecar
(`ReplyShaping.cs:55-98`): in every object entry of every top-level list of the reply, keep that key. It does not touch
top-level keys that are not lists, nor entries that are not objects.

A selector with two or more names, such as `results.damage_ratio` or `things.position.x`, applies only to the
top-level list named by its first name. Inside each object entry of that list it keeps the path given by the rest:
the key named by the second name, and, if there is a third, only that key inside the second's object value, and so on.
A path through a value that is a list applies to every object entry of that list. A list named by a path selector is
projected only by path selectors that name it and by single-name selectors. A selector whose first name is not a
top-level list of the reply matches nothing.

When several selectors keep the same key at the same place, the key is kept with the union of what they keep below
it; a selector that names a key without going deeper keeps that key's whole value.

After applying the selectors, the mod adds `fields_unmatched` to the reply's top-level object, an array of every
selector that matched no key in any entry, when the reply had at least one object entry in a top-level list. This is
today's rule (`ReplyShaping.cs:89-94`) extended to paths. A reply with no list entries gets no `fields_unmatched`, as
today.

Compatibility. Every `fields` value the sidecar accepts today (an array of at least one string, no further limits,
`src/StationGodMCP.Server/Program.cs:2188-2194`) is accepted on version 1 and through the sidecar, with today's results
for single names: a selector that does not follow the grammar (a name with `-`, an empty string after trimming, a
stray dot) is not refused but listed in `fields_unmatched`, as an unknown name is today. On version 2 such a selector
is `invalid_shape`, as is a `shape` with an unknown key or more than 256 selectors.

Errors are never shaped: an error reply is always whole.

The reference for single-name selectors is the sidecar's `FieldSelection`. The mod's results MUST equal it as parsed
JSON, key order included, on the reference set described in [stages.md](stages.md) (stage 1): every reply recorded in
stage 0, which covers the methods the dashboard, the scripts and agents call.

### List limits

`limit: {"things": 20}` keeps the first 20 entries of the top-level list `things` after the method has built it. This
saves formatting and transport, not the method's own work; a method with its own paging arguments (`offset`, `limit`,
which the catalogue marks with `x-paging`) saves both and is preferred. When `limit` cuts a list, the mod adds
`shape_truncated: {"things": 84}` (each cut list's length before the cut) to the reply's top-level object. On version 1
a `limit` naming a key that is not a top-level list is ignored; on version 2 it is `invalid_shape`.

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
player or a chip can, through the game's own rules) or `cheat` (does what no player can). Which method is which is the
owner's decision (overview, *Questions for the owner*); the catalogue records the answer. A method's class may depend
on its arguments through `x-class-when` (for example every building tool is read class while `dry_run` is true or
absent, because a dry run changes nothing). The class worked out from the arguments is the call's *effective class*.

A call runs only if the connection's level is at least the effective class, or the method is in the connection's
`grants`. A cheat call also needs cheat to be armed for the connection (below), unless the connection's cheat is
standing. Otherwise the call is refused before anything runs: `permission_denied` or `cheat_not_armed`.

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
- `key`: base64 of at least 32 random bytes. The mod refuses to load a shorter key (logged, that client disabled).
- `level`: the ceiling.
- `grants`: method names allowed beyond the level (an escape hatch for one method; empty by default).
- `cheat`: `armed` (default; cheat calls need the owner's approval in the game) or `standing` (no approval needed).
- `transports`: where the key may be used.

Keys are made on the host, outside the game, by the sidecar program:
`StationGodMCP.Server.exe key new <name> <level> [--config <BepInEx config folder>]`. It writes the entry into the file
and prints the key once to its own terminal. The mod never prints a key, never writes the file, and logs only a key's
name and a short fingerprint (the first 8 hex digits of its SHA-256). This keeps keys out of the game's console buffer,
which `read_console` returns to any reader (`src/StationGodMCP.Mod/Api/ReadConsole.cs:10`), and out of the game's logs.

The file is read at load and again when its timestamp changes, as the lint rules file is (owner notes `CLAUDE.md`,
State, 1.7.0: `Game/Lint/LintRuleFiles`). A client removed or downgraded while connected gets
`goodbye {reason: "revoked"}` and is closed. A missing file means no keys.

### Proving a key

The key itself never crosses the wire.

1. Client: `hello` with `auth: "key"`.
2. Server: `challenge` with `nonce`, the base64 text of 32 fresh random bytes.
3. Client: `auth` with `client` (its name in the clients file, equal to `hello.client.name`) and
   `proof` = lowercase hex of HMAC-SHA256 with the key as HMAC key (the base64-decoded bytes) over the UTF-8 bytes of
   the text `"stationgod-v2\n" + nonce + "\n" + client + "\n" + transport`, where `nonce` is the base64 text exactly as
   received and `transport` is `pipe` or `tcp`.
4. Server: compares in fixed time, as today (`StationGodTcpServer.cs:231-245`); `welcome` on success, `unauthorized`
   and close on failure.

The whole sign-in, from connecting to `welcome`, must finish within 10 seconds, the time today's pipe allows for the
first line (`StationGodPipeServer.cs:269`); otherwise `protocol_error` and close. There is no throttling of failed
attempts: a 256-bit key cannot be guessed, and the separate connection limits for TCP (below) keep failed attempts from
crowding out local clients.

This stops a passive listener from learning the key. It does not stop a listener from reading or altering the
session after sign-in; for that the connection needs a VPN or private network (see *Transports*).

### Connections without a key

A pipe connection whose `hello` has no `auth` gets the level `[Access] AnonymousPipeLevel` (`read`, `write`, `cheat` or
`none`) with `[Access] AnonymousCheat` (`standing` or `armed`) when that level is cheat. `none` refuses anonymous
connections with `unauthorized`. Shipped defaults until the owner answers the overview's question: `cheat` and
`standing`, which is today's behaviour; the recommendation is `write`.

A TCP connection without `auth` is refused, `unauthorized`.

### The owner's approval for cheats

The mod adds one console command through the game's own `CommandLine.AddCommand` (game decompile
`Util.Commands/CommandLine.cs:144`):

| Command | Effect |
| --- | --- |
| `stationgod allow <client_id or key name> [minutes]` | Arms cheat for one connection (a `client_id` such as `c9`) or for every present and future connection of one key (a key name), for the given minutes (default 15, at most 240), counted from now. An `event` `cheat_armed` goes to each connection armed. |
| `stationgod deny <client_id or key name>` | Disarms at once; `cheat_disarmed` event. |
| `stationgod clients` | Lists connections: client id, key name, transport, level, armed until, calls in flight, subscriptions. |

Arming a key name arms every agent that uses that key, including subagents; arming a `client_id` arms one connection.
The console lists both so the owner can choose.

Where the command may be typed: the host's own console, the dedicated server's console, and, for an owner playing as a
client of a remote host, the owner's own console as `serverrun stationgod allow c9 15`. The game forwards `serverrun`
from a client to the host and runs it there when the client's and the server's `ServerAuthSecret` settings match
(game decompile `Util.Commands/ServerRunCommand.cs`, `Util.Commands/ServerRunCommandMessage.cs:15-36`,
`Util.Commands/CommandLine.cs:78`). Where the server has no `ServerAuthSecret`, the remote owner arms nothing and uses a
key with standing cheat instead.

Where it may not: `run_console_command` refuses any command whose first word is `stationgod` with `permission_denied`
before running it, and the `stationgod` command itself refuses to act while `run_console_command` is executing (a flag
the tool sets for the length of its call, `src/StationGodMCP.Mod/Api/RunConsoleCommand.cs:17`), so no connection,
whatever its level, can approve itself, approve others or extend its own time.

Arming lives in memory only; it ends when the game closes.

### What levels are, honestly

On one Windows account every local process can read every file the owner can, including the clients file, and can
open the pipe. Levels protect against mistakes and against an agent that follows its instructions; they are not a
security boundary against a local program that sets out to get round them. Over TCP, keys are a real boundary as far
as the network is private.

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
  off without a secret (`StationGodMod.cs:243-250`). In version 2 it starts when `Enabled` is true and there is either a
  secret or at least one key whose `transports` include `tcp`. The default bind address stays `0.0.0.0` for
  compatibility; the documentation tells owners to bind the VPN's interface address.
- `[Server] MaxTcpConnections`, default 8, counted apart from the pipe's, so remote connections and unfinished
  sign-ins never take a local client's place.
- Same messages as the pipe. `hello` MUST carry `auth: "key"`.
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
  counted as `read_devices` counts them). These are sized to hold every card of the dashboard on one connection; stage 0
  measures the dashboard's actual values per tick and the limits are set at no less than twice that.
- Across all connections: the projected sampling cost must fit the subscription share of the frame budget
  ([scheduling.md](scheduling.md), *Subscriptions*).

A subscription that would pass a limit is refused with `subscription_limit`. A client so refused polls the same items
with `read_devices` at the same interval instead; the libraries report the refusal and the caller polls (the dashboard's runner already has that path).

### Ending

A subscription ends when the client unsubscribes, when the connection closes, or when the mod ends it with an event:

```json
{"type":"event","event":"subscription_ended","subscription":"s3","reason":"world_changed"}
```

Reasons: `world_changed` (reference ids may mean other things in another world), `revoked` (the client lost the
level), `limit` (the owner lowered a limit). Subscriptions do not survive a reconnect. The libraries keep each
subscription's request and subscribe again after reconnecting only when `welcome.server.world.id` is the one they last
saw; otherwise they close it and tell the caller ([clients.md](clients.md), *Subscriptions*).

### sample_logic

`sample_logic` keeps today's arguments, limits and reply: targets as `read_logic_many` takes them (1 to 32), interval
0.05 to 5 seconds, duration 0.1 to 30 seconds, at most 120 samples, on the real clock, running whether or not the game
is paused, and reporting the changes with elapsed real seconds (`src/StationGodMCP.Server/Program.cs:332-455`,
`:359-376`, `:448`). It is not a subscription: it has its own sampler in the mod, which takes each sample in the first
frame at or after it is due and counts in the subscription lane's time ([scheduling.md](scheduling.md)). Its catalogue
entry declares `x-duration` from `duration_seconds`, so the call's deadline and every timeout on the way, including the
version-1 listener's 30-second wait (`StationGodPipeServer.cs:24`, `StationGodRequestDispatcher.cs:80-96`) and the
sidecar's 35-second reply timeout (`Program.cs:26`), add the duration.

### Connection events

| `event` | Keys | When |
| --- | --- | --- |
| `ping` | none | After 30 s of silence. |
| `world_changed` | `world` (the new `{id, save, epoch}`) | A world finished loading (sent to every connection, subscribed or not). |
| `cheat_armed` | `until_utc` | The owner armed cheat for this connection. |
| `cheat_disarmed` | none | The owner disarmed it, or the time ran out. |

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

The mod tells a version-1 client from a version-2 one by its first line: an object with `type: "hello"` starts
version 2; anything else is a version-1 request (on the pipe) or a version-1 sign-in (on TCP).

A version-1 connection is served as today:

- One request line, one reply line, in order (`StationGodPipeServer.cs:174-209`). The reply envelope is today's
  `{id, ok, result, elapsed_ms}` or `{id, ok, error, elapsed_ms}`, no `type`
  (`src/StationGodMCP.Mod/Api/Views/HostViews.cs:10-50`).
- Arguments are checked only for unknown top-level names, as today (`DeclaredArguments.cs:51-77`), and otherwise read
  leniently by each tool (ids as JSON integers accepted, `src/StationGodMCP.Server/ArgumentCheck.cs:17`).
- A `shape` key in a version-1 request is honoured with the version-1 rules above, and the reply then carries
  `shaped: true`. Old clients never send `shape` and ignore the extra key. This is the only version-1 change, and it lets
  a version-1 client get shaping before it moves to version 2.
- Level on the pipe: `[Access] LegacyPipeLevel`, which by default follows `AnonymousPipeLevel` (so today's `cheat` until
  the owner chooses otherwise). Level on TCP: today's `{type: "auth", secret}` first line against `[Remote MCP] Secret`
  (`StationGodTcpServer.cs:174-229`), at `[Access] LegacyTcpLevel` (`none`, `read`, `write` or `cheat`). The secret
  crosses the network in plain text, so cheat there undoes what keys protect. Whether to allow it is the owner's
  decision (overview, *Questions for the owner*): option A caps legacy TCP at write, which breaks the cheat tools of
  old remote sidecars; option B keeps cheat until version 1 is retired. Until the owner decides, the shipped default is
  B (`cheat` when a secret is set), which keeps every existing client working. While legacy TCP is on, the mod logs a
  warning at load and logs each legacy TCP sign-in with its remote address, so the owner can see whether anyone still
  uses it.
- `none` refuses version 1 on that transport.
- Permissions apply to version-1 calls too, at that level.

The other direction: a version-2 client against an old mod. The old mod reads `hello` as a request with no method and
answers `{"id":null,"ok":false,"error":{"code":"method_not_found",...}}` (`ApiHost.cs:157-161`). The libraries take
that answer as "version 1 only" and fall back: they speak version 1 on the same connection, apply `fields` themselves
(the sidecar's rules), offer no subscriptions (callers poll), and treat every method not in their built-in catalogue's
read class as unsafe to resend. Over TCP the old mod closes the connection after the failed sign-in
(`StationGodTcpServer.cs:187-192`); the library then reconnects with version 1 if it was given a legacy secret, and
otherwise reports that the server is too old.

## Appendix: one complete exchange

```text
-> {"type":"hello","protocol":[2],"client":{"name":"agents","version":"1.12.0","library":"stationgod-cs/1.12.0"},"auth":"key"}
<- {"type":"challenge","nonce":"wUq0x0...=="}
-> {"type":"auth","client":"agents","proof":"9f1c..."}
<- {"type":"welcome","protocol":2,"client_id":"c9","client":"agents","level":"cheat","grants":[],"cheat":{"armed":false,"until_utc":null,"standing":false},"server":{...},"catalogue":{...},"limits":{...},"features":["shape","shape.paths","subscriptions","cancel"]}
-> {"type":"call","id":"a1","method":"grid_survey","params":{"room_id":"r12"},"shape":{"fields":["devices.reference_id","devices.prefab_name"]}}
-> {"type":"call","id":"a2","method":"game_clock","params":{}}
<- {"type":"reply","id":"a2","ok":true,"shaped":false,"result":{"game_time_s":84211.5,"paused":false,"time_of_day_ratio":0.41,"days_past":12},"elapsed_ms":0.02,"queue_ms":9.8,"frame":81301}
<- {"type":"reply","id":"a1","ok":true,"shaped":true,"result":{...},"elapsed_ms":38.4,"queue_ms":31.0,"frame":81302}
-> {"type":"call","id":"a3","method":"move_gas","params":{"from":"1201","to":"1305","gases":["Oxygen"],"amount_mol":10}}
<- {"type":"reply","id":"a3","ok":false,"error":{"code":"cheat_not_armed","message":"move_gas is a cheat tool and the owner has not approved cheats for c9 (agents). In the game console: stationgod allow c9","data":{"client_id":"c9","client":"agents"}}}
```
