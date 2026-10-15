# The mod's wire, protocol 2

[Back to the overview](README.md)

This page is the contract between the mod and whatever connects to its pipe or TCP port: the sidecar and its hub
([hub.md](hub.md)), the client libraries, and `sg`. Everything below is the mod's own wire; the hub protocol a script,
a program and the dashboard speak is hub.md's, and where the two differ this page describes the layer under it. The
mod speaks protocol 2 and nothing else.

## The wire at a glance

A client opens the pipe (or a TCP connection, *Signing in over TCP*), sends one `hello` line and gets one `welcome`
line back naming it, the world it reached, the catalogue and the limits. From then on it sends `call` lines and reads
`reply` lines, matched by `id`, several at a time. A call is a verb: `method` names the verb, and `params` carries
`what` (the kind of thing the verb works on) and the verb's options. Events arrive between replies: subscription
updates, job endings, world changes, a ping.

```text
-> {"type":"hello","protocol":[2],"client":{"name":"dashboard","version":"3.0.0","library":"stationgodmcp"},"features":["shape"]}
<- {"type":"welcome","protocol":2,"client_id":"c4","client":"anonymous",
    "server":{"mod_version":"1.33.2","instance_id":"8f0c2d5e9a1b4c7d","pipe_name":"StationGodMCP","transport":"pipe",
              "role":"host","dedicated":false,"world":{"id":"3b9e1a40c2d84f6e","save":"Vulcan2_4","epoch":3},
              "game_state":"Running"},
    "catalogue":{"hash":"sha256:4f2a...","methods":98,"protocol_methods":6},
    "limits":{"max_in_flight":16,"max_in_flight_per_stream":16,"max_in_flight_per_connection":256,
              "max_request_bytes":4194304,"max_reply_bytes":16777216},
    "features":["shape","shape.paths","shape.omit","cancel","streams","cursors","release"]}
-> {"type":"call","id":"1","method":"find","params":{"what":"thing","prefab":"*Battery*","near":"player","radius":30,"limit":10}}
<- {"type":"reply","id":"1","ok":true,"shaped":false,
    "result":{"things":[...],"count":10,"total":27,"offset":0,"has_more":true},
    "elapsed_ms":0.61,"queue_ms":0.2,"frame":81234}
-> {"type":"call","id":"2","method":"write","params":{"what":"logic","id":"4820585","logic":"On","value":1,"confirm":true}}
<- {"type":"reply","id":"2","ok":true,"shaped":false,"result":{...},"elapsed_ms":1.4,"queue_ms":0.8,"frame":81240}
```

## Framing

- One message is one JSON object in UTF-8 without a byte-order mark, ending in a line feed; a carriage return before
  it is ignored and blank lines are ignored. A message never contains a raw line feed: JSON escapes it inside strings.
  Both ends write compact JSON (`Pure/Protocol/LineFramer.cs:10-14`).
- A message longer than `limits.max_request_bytes` (4 MiB, `Protocol/CallSession.cs:48`) is answered
  `request_too_large` with a null id, then `goodbye`, and the connection closes (`CallSession.cs:153-158`).
- A key given twice in one message is refused `invalid_argument` (`Pure/Protocol/ClientMessage.cs:60-64`); a message
  whose `type` does not allow one of its keys is `protocol_error` (`ClientMessage.cs:86-96`).
- Numbers are written the mod's way: NaN and the infinities are the strings `"NaN"`, `"Infinity"` and `"-Infinity"`
  (`Api/Shared/ApiJson.cs:136`). Reference ids are decimal strings.
- A reply larger than `limits.max_reply_bytes` (16 MiB, `Protocol/ProtocolHost.cs:19`) or the call's `shape.max_bytes`
  is not sent: the call answers `reply_too_large` (*Shaping and the reply cap*).

## Messages

Client to server (`Pure/Protocol/ClientMessage.cs:37-81`):

| `type` | When | Keys |
| --- | --- | --- |
| `hello` | the first message, once | *Hello and welcome* below. |
| `call` | any time after `welcome` | *Calls* below. |
| `cancel` | any time | `id` of a call in flight; no reply. |
| `stream_end` | any time | `stream` 1 to 2^53; no reply (*Streams*). |
| `release` | any time | `stream` 0 to 2^53; no reply (*Streams*). |
| `cursor_close` | any time | `cursor_id`; no reply (*Streams*). |
| `session_end` | any time | none; no reply (*Held restores*). |
| `bye` | before closing | none; no reply. |

Server to client:

| `type` | When | Keys |
| --- | --- | --- |
| `welcome` | after `hello` | *Hello and welcome* below. |
| `reply` | once per `call` | `id`, `ok`, `result` or `error`, and the times and flags of *Replies* below. |
| `event` | any time | `event` and its keys: `update`, `job_ended`, `world_changed`, `game_state`, `subscription_ended`, `ping`. A `world_changed` reaches every welcomed connection when a world finishes loading (`Protocol/CallSession.cs:202-206`). |
| `goodbye` | before the server closes a connection on purpose | `reason`: `shutting_down`, `world_unloaded`, `slow_client`, `protocol_error` or `resumed_elsewhere` (`Protocol/Wire.cs:268-275`, `Protocol/Connection.cs:405-412`, `Protocol/ResumeHolds.cs:69-125`). |

## Hello and welcome

`hello` is the first message, once:

| Key | Meaning |
| --- | --- |
| `protocol` | array of integers, the major versions the client speaks; required, and the mod answers only if it holds 2 (`Pure/Protocol/ClientMessage.cs:212-226`, `Protocol/CallSession.cs:218-228`). |
| `client.name` | 1 to 64 letters, digits, `-`, `_` or `.`; required (`ClientMessage.cs:233-238`). |
| `client.version` | a string, optional. |
| `client.library` | a string, optional. |
| `features` | array of strings the client understands, optional (`ClientMessage.cs:240-254`). |
| `resume` | a token the client chose, 16 to 128 letters, digits, `-` or `_`, optional; see *Resuming* (`ClientMessage.cs:256-262`). |

The `welcome` names the connection, the server it reached and the limits that apply:

- `client_id`: the mod's name for this connection, unique while the mod runs (`Protocol/Connection.cs:115`).
- `client`: always `anonymous` (`CallSession.cs:50`); the name given in hello is shown as the connection's label where
  the mod reports connections.
- `server`: `mod_version`, `instance_id` (random, new each time the mod loads), `pipe_name`, `transport`, `role`
  (`host`), `dedicated`, `world` and `game_state` (`Protocol/Wire.cs:11-92,154-182`). `world.id` is random and new each time a
  world finishes loading: it is the world's identity across reconnects. `world.save` is the save's name where the game
  exposes one, for people to read; `world.epoch` is a counter of worlds left since the mod loaded and restarts with
  the game, so it is not an identity. `world.id` is empty while no world runs (the menu, a load in progress).
- `catalogue`: `hash`, `methods` (the whats over every verb) and `protocol_methods`
  (`Protocol/Wire.cs:184-198`, `Pure/Catalogue/VerbDocument.cs:36-58`).
- `limits`: `max_in_flight` and `max_in_flight_per_stream` (calls in flight on one stream, default 16), the same two
  numbers, `max_in_flight_per_connection` (256), `max_request_bytes` (4 MiB) and `max_reply_bytes` (16 MiB)
  (`Protocol/Wire.cs:200-244`); the subscription limits are added when the server has subscriptions (*Subscriptions*).
- `features`: what this server does beyond the basics, by name: `shape`, `shape.paths`, `shape.omit`, `cancel`,
  `streams`, `cursors` and `release`, plus `subscriptions` and `resume` when they are on (`CallSession.cs:53-59`). A
  client MUST NOT use a feature the server did not list.
- `resume` and `restores`: only when `hello` carried a resume token (*Resuming*, *Held restores*).

### The catalogue

The catalogue has its own identity, `welcome.catalogue.hash`: SHA-256 over the verb files' bytes as built and
embedded, with every line ending a bare LF, so every language computes the same hash from the same files
(`src/StationGodMCP.Client/GameCatalogue.cs:50-56`). A client that carries a copy of the catalogue and sees a
different hash asks the mod for its own with a `call` whose `method` is `catalogue` and no params; the reply's
`result` is the assembled document (`{"shared", "errors", "verbs"}`, the model `catalogue/verbs/*.json` holds), read
off the embedded files, without touching the game (`Protocol/CallSession.cs:450-462`). The hub does this too: it reads
the classes and rules of the mod it talks to from that document (`src/StationGodMCP.Client/GameLink.cs:763-800`).
`catalogue` is one of the protocol methods below.

## Calls

A `call` carries:

| Key | Meaning |
| --- | --- |
| `id` | a string of 1 to 64 characters, unique on the connection while the call is in flight. |
| `method` | required: a verb, or one of the protocol methods. |
| `params` | an object, `{}` when absent. A verb call is `{"what": "<what>", ...options}` (`Pure/Verbs/VerbDispatcher.cs:42-46`); a whatless verb (undo, help, run_script) takes no `what`. |
| `shape` | optional, see *Shaping and the reply cap*. |
| `deadline_ms` | an integer 100 to 600000, default 30000. A what that declares its own duration (a sampled read) gets it added at the arguments given (`CallSession.cs:383-385`). |
| `stream` | an integer 0 to 2^53, default 0; see *Streams* (`Pure/Protocol/ClientMessage.cs:350-358`). |
| `world` | a world id (`welcome.server.world.id` or a `world_changed` id), optional; see below. |

**Admission.** One handler per (verb, what) answers a verb call (`VerbDispatcher`). A name that is no verb, or a verb
or what with no handler, is refused `method_not_found` listing the verbs; nothing runs
(`Pure/Verbs/VerbError.cs:27-35`, `VerbDispatcher.cs:112-130`).

**The option check.** The what (required for a verb with whats; the nearest what when it is not one) and every option
are checked against the what's schema from the catalogue: types, enum (trimmed, any case), bounds, list lengths, list
items and required options. Every problem comes back at once as one `invalid_argument` in verb names, with
`data.problems` `[{path, problem, nearest?}]` and, for an unknown name, `data.valid`, the what's options with the
three nearest first (`Pure/Verbs/OptionCheck.cs:25-104`, `Pure/Verbs/VerbError.cs:63-119`). JSON `null` is an omitted
option. The hub's own options (`output_file`, `whole`, `repeat`, `max_wait`, and a file option such as `code_file`)
never reach the game: one given here is a problem naming the hub as its taker (`OptionCheck.cs:22,106-121`). With
`[Server] StrictArguments` (the default) the connection's reader thread checks before queueing; without it the main
thread checks before the handler runs (`CallSession.cs:348-386`).

**Order on a stream.** A call whose effective class is write or cheat does not start until every earlier call on its
stream is answered, and no later call on that stream starts until it is answered. Reads may pass reads; calls on
different streams never wait for each other; a call that goes on over later frames (a paged find, a sampled read)
holds its stream until it answers. Reads and writes in detail are [scheduling.md](scheduling.md)'s.

**A call's world.** A call may carry the world it means. The mod compares it with the current world as the call
starts, not when it arrives, so a call queued before a world load and started after it is refused `world_changed`,
unrun, with the current world as `data.world` (`Protocol/CallScheduler.cs:146-152,187-194`); a call whose work goes on
over frames is refused the same way when its world is left under it (`Protocol/DeferredCalls.cs:142-146`). A write sent
for one save never acts on the save loaded after it, which reuses the same reference ids. A call without `world` runs
in whatever world is current.

**Deadlines and cancelling.** `deadline_ms` is how long the client will wait. A call that has not started when its
deadline passes is answered `game_timeout` without running (`Protocol/Calls.cs:353-425`); one that has started runs
to the end. `cancel` drops a call that has not started, answering `cancelled`; a call that has started cannot be
stopped that way. Cancelling or timing out an unknown id does nothing (`CallSession.cs:462-486`).

### Protocol methods

Six methods belong to the protocol itself rather than to a verb. They run on the main thread (the catalogue is
answered on the connection's reader thread) and are listed in `protocol_methods`:

| Method | Params | Result |
| --- | --- | --- |
| `catalogue` | `{}` | the assembled verb document (above). |
| `subscribe` | see *Subscriptions* | `{subscription, interval_s, values, frame, result?}`. |
| `unsubscribe` | `{subscription}` | `{subscription, ended: true}`. |
| `drop_job` | `{job_id}` | `{job_id, dropped: true}`: a building run still queued with `wait: true` is taken out before it starts. |
| `stream_costs` | `{streams?: [n, ...]}` | `{streams: [{stream, calls, main_thread_ms, over_budget_frames, samples, sample_ms, updates, update_ms}]}`, the caller's own streams (up to 256 asked, in the order asked) (`Protocol/StreamCostCalls.cs:16-19`). |
| `hold_restore` | `{set?: [{reference_id, logic_type, value}], clear?: [{reference_id, logic_type}], clear_all?}` | `{restores: [{reference_id, logic_type, value}]}`, the stream's restores after the change (*Held restores*). |

`catalogue`, `subscribe`, `unsubscribe` and `stream_costs` are read class; `drop_job` and `hold_restore` are write
class, so on their stream they wait for every earlier call and later calls wait for them (`Protocol/CallScheduler.cs:361-380`,
`src/StationGodMCP.Client/GameCatalogue.cs:20-25`).

## Replies

```json
{"type":"reply","id":"7","ok":true,"shaped":false,"result":{...},"elapsed_ms":1.37,"queue_ms":14.2,"frame":81240}
{"type":"reply","id":"8","ok":false,"error":{"code":"thing_not_found","message":"No thing with reference id 99."},"elapsed_ms":0.05,"queue_ms":3.0,"frame":81240}
```

`result` is the what's reply as the catalogue describes it, after shaping. `shaped` is true when the mod applied the
call's shape. `elapsed_ms` is the handler's main-thread time, `queue_ms` the wait from receipt to start, `frame` the
mod's frame counter when the handler ran, and `budget_hit` (written only when true) says the frame's request budget
was reached as the reply was built; when it is true is [scheduling.md](scheduling.md)'s. The times and `frame` are
absent when the call never reached a handler (`game_timeout`, `cancelled`, `too_many_in_flight`, `duplicate_id`,
protocol errors) (`Api/Views/HostViews.cs:14-73`, `Protocol/Calls.cs:243-280`).

Every error is `{code, message}` with optional `data` and `see` (`Api/Shared/CommonViews.cs:81-104`). `message` is for
people and may change; `code` is for programs and does not; `see` is `{tool, topic, subtopic}` and names the help node
that explains the code, taken from the catalogue's `errors`, for a code the catalogue registers. The codes are
listed, each with its help node, in `catalogue/verbs/_errors.json` (224 entries). The hub's protocol adds `fix` (what to send
next), `maybe_ran`, `stopped` and `pass_end` to errors it answers itself; those are hub.md's fields, not the mod's
(`src/StationGodMCP.Server/Verbs/VerbCall.cs:112-119`).

The protocol's own codes, the ones a client acts on:

| Code | Meaning | `data` | Caller may resend |
| --- | --- | --- | --- |
| `protocol_error` | A message the protocol does not allow: not a JSON object, an unknown `type` or top-level key, a call before `welcome`, a repeated `hello`. The connection closes after the reply. | `{line_start}` | no |
| `unsupported_protocol` | No common major version. The connection closes. | `{supported: [2]}` | no |
| `unauthorized` | Over TCP, the shared-secret sign-in is missing or wrong. The connection closes (`Protocol/Connection.cs:538-545`). | none | no |
| `method_not_found` | No such verb or protocol method, or no handler for that (verb, what). Nothing ran. | none | no |
| `invalid_argument` | The options break the what's schema, a protocol method's arguments break its own check, or a `stream`, `stream_end` or `release` is malformed. Nothing ran. | `{problems, valid?}` | no |
| `invalid_shape` | `shape` is malformed. Nothing ran. | `{problems}` | no |
| `duplicate_id` | The id is already in flight on this connection. Nothing ran. | none | no |
| `too_many_in_flight` | More calls in flight on the stream or on the connection than the welcome's limits. Nothing ran. | `{limit, scope}`, `scope` `stream` or `connection` | yes |
| `stream_ended` | The call's stream was ended with `stream_end`; streams are never reused. Nothing ran. | `{stream}` | on another stream |
| `session_ended` | The connection sent `session_end`; nothing it sends after that runs. Nothing ran. | `{stream}` | in a new session |
| `game_timeout` | The call was not started before its deadline. Nothing ran. | none | yes |
| `cancelled` | Cancelled before it started, by `cancel` or a stream's end. Nothing ran. | none | yes |
| `shutting_down` | The mod is stopping or the world is unloading; the call was not started. | none | yes |
| `world_changed` | The call's `world` is not the current world. Nothing ran. | `{world: {id, save, epoch}}`, the current world | no |
| `reply_too_large` | The reply would pass `shape.max_bytes` or `limits.max_reply_bytes`. The method ran. | `{bytes, limit, counts}` | only if the method is read class |
| `cursor_expired` | A later page named a cursor that is gone. Nothing ran. | `{cursor_id, position}` | no; start the walk again |
| `undo_expired` | The job's kept data was evicted under the memory bound. Nothing ran. | `{job_id}` | with the job's own facts when the message asks for them |
| `too_many_restores` | A `hold_restore` change would pass 256 restores on the stream or 4,096 on the owner. Nothing changed. | `{limit, scope}` | after clearing some |
| `subscription_limit` | A subscription would pass a per-subscription, connection or sampling limit. Nothing changed. | `{limit, requested}` | no; poll instead |
| `unknown_subscription` | `unsubscribe` of an id this connection does not have. | none | no |

`game_changed` (a game member this build lacks) and `internal_error` (a bug; the call may have partly run) keep
today's meaning. Whether a broken client may send an unstarted call again is the libraries' rule: only calls the
catalogue marks read class and free of effects are resent ([clients.md](clients.md)).

## Shaping and the reply cap

`shape` is an optional object on a call:

| Key | Type | Meaning |
| --- | --- | --- |
| `fields` | array of selector strings, at least 1 | Which keys to keep in the entries of the reply's lists. |
| `omit` | array of selector strings, at least 1 | Which keys to leave out, as paths from the reply's top. |
| `limit` | object: list name to integer 0-100000 | Keep at most this many entries of that top-level list. |
| `max_bytes` | integer 1024-16777216 | Refuse to send a reply larger than this (`reply_too_large`). |

The mod applies the shape while it writes the reply, so keys left out are neither formatted nor sent
(`Pure/Shaping/ShapingJsonWriter.cs:20-44`). `fields`, `omit` and `list_limits` may also be given as options on the
call; each replaces the same key of `shape` (`Pure/Verbs/Fields.cs:49-73`). A selector is one or more names joined by
dots, matched exactly (case included) against the wire names. The writer adds `fields_mapped`,
`fields_unmatched` (with `fields_closest` and `fields_valid` where useful) and `omit_unmatched` to the reply's top,
each naming selectors that could not be used; near keys are offered as a safe way on (`ShapingJsonWriter.cs:24-44`,
`Pure/Shaping/FieldMapping.cs`). Every verb reply also carries `truncated`: one entry per list the reply holds back
entries of, however it was cut (the handler's own paging, a `limit`, a default cap), with what it returned, the total
and how to get more; `truncated: []` means nothing was held back (`ShapingJsonWriter.cs:845-875`).

The cap is the mod's: a reply over `limits.max_reply_bytes`, or over the shape's `max_bytes`, is replaced by
`reply_too_large` carrying its size, the limit and the length of every top-level list, so the caller can narrow the
request (`Api/Shared/ReplyTooLarge.cs:9-42`, `Protocol/VerbCalls.cs:134-146`). The direct face's smaller cap (a reply
over 12 KB cut to about 4 KB with `ref`, `cut` and `next`; `whole: true` lifts the cut; `repeat` asks a call again)
belongs to the hub and never reaches the mod: those options are taken out before the call is sent
(`src/StationGodMCP.Server/Verbs/VerbPipeline.cs:33-37,663-680`; [reply-budget.md](reply-budget.md)).

## Streams

A stream is an ordered line of calls inside one connection (feature `streams`). A hub that holds one connection for
many callers gives each its own: stream 0 for the direct caller, one stream per script run. A call names its stream;
absent, null or 0 is the connection's default stream 0, so a client that never sends `stream` sees no difference. A
`stream` that is not an integer from 0 to 2^53 is refused `invalid_argument` and nothing is queued
(`Pure/Protocol/ClientMessage.cs:118-135,350-358`).

A connection may have `limits.max_in_flight` calls in flight on one stream (16) and `max_in_flight_per_connection`
(256) on all its streams together; a call beyond either is answered `too_many_in_flight` and never queued
(`CallSession.cs:388-415`, `SchedulerSettings.cs:20-23`).

`{"type":"stream_end","stream":N}` says the client is done with stream N. It has no reply, like `cancel`. Its
unstarted calls are answered `cancelled` and never run; calls already running finish; whatever the mod keeps per
stream is dropped, and its held restores are cleared unwritten. The stream is never used again by its owner: a later
call on it is refused `stream_ended` (`data: {stream}`), and a later `stream_end` of it does nothing
(`CallSession.cs:488-505`, `Pure/Protocol/StreamEnd.cs:113-124,148-190`). Stream 0 never ends: `stream_end` of 0, or
one without a `stream` from 1 to 2^53, is refused `invalid_argument` with a null id, and the connection stays open
(`ClientMessage.cs:409-431`).

A connection remembers the streams it ended as ranges of numbers, so a hub that numbers its runs upward costs a few
ranges; at most 1,024 ranges are kept, and past that the two lowest merge (`StreamEnd.cs:125,186-190`). Closing
the connection, or a slow client (*Outbound queue and slow clients*), ends every stream of it, unless its `hello`
carried a resume token: then its streams are the token's, held for the resume window (*Resuming*). A world change
clears what the mod keeps per stream on every connection (`Protocol/StreamStores.cs:120`).

Building jobs and their undo data are not per stream: a stream's end leaves jobs queued or running, their snapshots
and their final statuses alone. Those are kept in two stores with a memory budget each and one set of pins over
both: `release` of a stream drops the pins, so memory may evict them, and `undo` of an evicted job answers
`undo_expired` (`Pure/Jobs/KeptJobs.cs:9-44`, `KeptJobStore.cs:12-16`, `Protocol/ClientMessage.cs:439-460`).

Every paged list pages one way (cost design A4): the first page takes the matching entries' keys in order, describes
only its own entries, and with the wire option `cursor` keeps the rest under a cursor (`cursor_id`) when more follows;
later pages with that id are taken from the kept keys with no scan, gone entries skipped and counted and no filter
checked again (`Api/Shared/WalkRequest.cs`). The world's lists (`find` thing, item, device, container, frame,
damaged; `inspect health`; `survey` area and network) are taken over frames (`Verbs/ThingListPage.cs`); one thing's
list (`inspect screen`, `read flight_log`, the silo store in `inspect contents`) at once, and only for a call naming
one id: a call naming several pages each by offset and keeps no cursor (`Api/Shared/KeyedPage.cs`). A key is a
reference id, or for entries that are not things their place: a cell (`Pure/Paging/CellKey.cs`), a screen element, a
sample's sequence number, a silo entry, a `find item` row or group (whose records the cursor keeps). A cursor belongs to the (owner, stream) that opened it, is freed
when the page that takes its last ids answers, by `{"type":"cursor_close","cursor_id":"..."}` from that connection
(no reply), when its stream or connection ends, on a world change, and by the bounds: 1,000,000 ids and 1,024
cursors in all, the least recently used evicted first; using a freed, evicted or another stream's cursor is
`cursor_expired`, whose `position` is remembered for the last 256 freed cursors (`Pure/Paging/WalkCursors.cs:11-51`).

## Subscriptions

A subscription asks the mod to read a set of device values at an interval and push the read when it changed.
`subscribe` params:

| Key | Type | Meaning |
| --- | --- | --- |
| `topic` | `devices` (default), `world`, `jobs` or `find` | What to watch. |
| `id`, `logic`, `reads`, `gateway`, `include` | as read logic's options | For `devices`: the same parts and bounds as one read logic call (at most 1,024 values). |
| `what` and the find's options | as a `find` call's | For `find`: `thing`, `item`, `device`, `container`, `frame`, `damaged`, `pad`, `water`, `food`, `plant`, `room` or `gateway`, with that find's options but its paging (`limit`, `offset`, `cursor`, `order`) and the shaping ones (`fields`, `omit`, ...), which are refused: the subscription holds the whole list. |
| `interval_s` | number 0.5 to 3600 | For `devices`: how often to read, in game seconds; default 1. For `find`: how often its live facts are read again, 1 to 3600, default 10. Game time stops while the game is paused, so a paused game sends nothing. |

The reply is `{subscription: "s3", interval_s: 1.0, values: 412, frame: 81250, result: <a full read logic result>}`
(`Pure/Subscriptions/SubscriptionViews.cs:9-32`). The `world` and `jobs` topics take `topic` alone
(`Pure/Subscriptions/SubscribeRequest.cs:105-133`).

A `find` subscription's reply is `{subscription, topic: "find", what, interval_s, frame, as_of_s, cost_ms, keyed,
result}`: `result` is the find's reply with every list whole (a walk's pages joined) and no paging keys, `as_of_s` the
game time its evaluation began, `cost_ms` the main-thread milliseconds it took, and `keyed` names each list whose
entries carry a key (`reference_id`, else `gateway_id`, `room_id`, `atmosphere_id`) with that field. The reply may come
some frames after the call: a query nobody holds yet is evaluated first, over frames, as a `find` call is
(`Protocol/SubscriptionHub.cs`, `Subscriptions/FindEvaluation.cs`).

A find query is evaluated once for all its subscribers, on any connection: again every `interval_s` (the shortest of
its subscribers'), since every find carries live facts (quantities, paint, damage values, growth), and earlier, at most
once a game second, when the world model tells of a change on its channels (things of its class filed or gone, cells
whose air, room or terrain changed, the damaged set, data networks; `Pure/Subscriptions/FindTopic.cs`).

Events, all as `{"type":"event","event":"<name>",...}`:

- `update`: `subscription`, `seq`, `frame`, `game_time_s`, `late_ms` and the whole reading as `result`. A reading is
  sent only when some value other than the clock differs from the last one sent; the clock rides along in every event
  that is sent. At most one update per subscription waits in the outbound queue, so a slow client simply sees fewer,
  fresher events and `seq` never has gaps (`SubscriptionViews.cs:55-85`, `Pure/Subscriptions/UpdateSlot.cs:34-39`).
- `find_update`: `subscription`, `seq`, `frame`, `as_of_s`, `keyed`, and what changed since the result this subscriber
  was last sent: `lists` (per keyed list `{added, changed, removed}`: whole entries added and changed, keys removed)
  and `rest` (every other key of the reply, whole, only when it changed). An evaluation that changed nothing sends
  nothing and takes no `seq`. A client applies an update by dropping the removed keys, replacing the changed entries,
  appending the added ones, and taking `rest` as the reply's other keys. After a resume the next update is whole:
  `whole: true` and `result` in place of `lists` and `rest` (`Pure/Subscriptions/FindSlot.cs`, `FindResult.cs`).
- `job_ended`: `subscription`, `seq`, `frame`, `job`: a held-tick job's final facts (`job_id`, `tool`, `status`,
  `error` and the lists undo plans from), sent once per job from the frame it ends in
  (`SubscriptionViews.cs:135-155`, `Protocol/SubscriptionHub.cs:216-240`). Jobs are global: every `jobs`
  subscription hears of every job.
- `world_changed` and `game_state`: for the `world` topic only; a new world finishes loading, or the game state
  changes (`SubscriptionViews.cs:105-131,158-175`).
- `subscription_ended`: `subscription`, `reason` (`world_changed`, `limit`, or `read_failed` when a sample's read or a
  find's evaluation failed: a gateway gone, a find refused), when the mod ends it
  (`SubscriptionViews.cs:88-102`).

Limits: at most `limits.max_subscriptions` (64) per connection, every topic, and `limits.max_subscription_values`
(8,192 values across its devices subscriptions), counted over the connection's streams together; and every
subscription's measured load together (a devices subscription's sample ms, a find query's evaluation ms, per game
second at its interval) at most half the subscription lane's share at 30 frames a second. A subscription past a limit
is refused `subscription_limit` (`Pure/Subscriptions/SubscriptionLimits.cs`). A client so refused polls the same items
with read logic (or the same find) at the same interval instead.

Ending: a subscription belongs to the stream of the `subscribe` call that made it; any stream of the connection may
unsubscribe it. It ends when the client unsubscribes, when its stream ends or the connection closes (both without
an event), or when the mod ends it with `subscription_ended`. It survives a break only through a resume token
(*Resuming*); the libraries subscribe again after reconnecting only into the same world.

## Resuming

A client may keep a resume token and present it in every `hello` (`Connection.cs:119-127,185-190`). The token owns state rather
than the connection: the streams it ended, its walk cursors, its subscriptions, its stream cost counters and its
held restores (`Protocol/ResumeHolds.cs:12-33`). The window is `[Server] ResumeWindowSeconds` (default 60, 0 turns
resuming off) (`StationGodMod.cs:132,566-572`, `ResumeHolds.cs:34-35`).

When the connection closes without `bye`, the token's state is held for the window; a hello presenting the token
within it picks the state up, and the mod answers `welcome.resume` `{token, resumed: true, window_s}`. A connection
presenting a token another connection still holds takes it over: the old connection is told
`goodbye {reason: "resumed_elsewhere"}` and ended (`ResumeHolds.cs:69-125`, `Protocol/Connection.cs:198-203`). Calls
are never resumed: a closed connection's unstarted calls are dropped unanswered and its sampled read runs end. When
the window passes, the token's state ends as a `stream_end` ends each of its streams, and its session ends, so its
held restores apply (`ResumeHolds.cs:143-169`, *Held restores*).

## Held restores

`hold_restore` registers logic writes the mod makes by itself when the owner's session ends abruptly: the client
sends `session_end` (the hub does when its own caller goes away), a resume token's window passes without a resume,
or a connection without a token closes without `bye` (`Protocol/HeldRestores.cs:41-65`). A stream's restore applies
only in the world it was registered in and only while the device still holds the value the stream last wrote: a
device changed by someone else has its restore skipped and reported. A restore that opens its device runs the write
rules as a checked write does, at apply time (`HeldRestores.cs:52-64`). At most 256 restores per stream and 4,096 per
owner; a change past either is refused whole (`HeldRestores.cs:68-69`).

`{"type":"session_end"}` marks the owner's session ended: its unstarted calls are answered `cancelled`, its held
restores apply at the next frame, and every later call of the owner is refused `session_ended` unrun. The connection
stays open (`CallSession.cs:507-535`). `bye` is the normal end: the client is closing, the token's state (if any)
ends when the connection closes, and held restores are not applied (`CallSession.cs:145-148`). The results of
applying are reported to the token's next welcome as `welcome.restores` `{applied, skipped, dropped}`
(`Protocol/Wire.cs:126-131`).

## Signing in over TCP

A TCP connection's first line MUST be the shared-secret sign-in, `{"type":"auth","secret":"..."}`, checked in fixed
time against the mod's configured secret; the server answers `{"ok":true}`, or `unauthorized` and closes
(`Protocol/Connection.cs:496-552`). After it the connection talks exactly as a pipe connection, `hello` first. A pipe
connection has no sign-in. On either transport the first line must arrive within 10 seconds of connecting, or the
connection is closed (`Protocol/ProtocolHost.cs:16`, `Protocol/Connection.cs:261-269`). The secret crosses the
network in plain text, as does everything after it; the connection needs a VPN or private network.

Both transports let each end read and write at the same time: the pipe is opened overlapped through the Windows API
(`Protocol/NativePipe.cs:10-16`), and each connection has one reader thread and one writer thread, so the main thread
never writes to a transport. The pipe takes `[Server] MaxPipeConnections` clients (default 32); TCP takes
`[Server] MaxTcpConnections` counted apart from the pipe's, and listens only when `[Remote MCP] Enabled` and a secret
are set (`Protocol/PipeListener.cs:10-16`, `Protocol/TcpAcceptor.cs:10-16,46`).

## Outbound queue and slow clients

Each connection's outbound queue holds replies and events in order; the writer thread takes them, and for a
subscription's update it builds the line when it reaches it, so a merged update is written as the newest state
(`Protocol/Connection.cs:157-180`, `Pure/Subscriptions/UpdateSlot.cs:34-39`). The server sends
`{"type":"event","event":"ping"}` after 30 seconds of silence on a connection (`Protocol/ProtocolHost.cs:20,47`,
`Connection.cs:329-350`). A connection is closed with `goodbye {reason: "slow_client"}` when the oldest queued line
has waited 35 seconds without being taken (`Protocol/ProtocolHost.cs:21,50`); its unstarted calls are dropped and its
streams end as `stream_end` ends them, unless its hello carried a resume token, whose state is held then
(`CallSession.cs:181-199`).
