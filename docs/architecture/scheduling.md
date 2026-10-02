# Sharing the game's main thread

[Back to the overview](README.md)

Everything StationGod does with the game's objects happens on the game's main thread, between frames, because the
game's objects may be touched only there. Every millisecond spent there is a millisecond the frame is late. This page
says how that time is shared between clients today, what requests are known to cost, and how version 2 shares it:
by lanes with their own share of each frame, taking requests from each connection in turn, with a separate share for
subscriptions.

## How it works today

Once per frame, on the host only, the mod's `Update` runs the request queue and then its own per-frame work: building
jobs, previews, highlights and the flight recorder (`src/StationGodMCP.Mod/StationGodMod.cs:95-123`).

Listener threads put each request into one queue and wait for its reply (`src/StationGodMCP.Mod/StationGodRequestDispatcher.cs:80-96`).
The main thread takes requests first in, first out, at most 64 per frame (`StationGodRequestDispatcher.cs:25`,
`:34-78`), and stops early once the frame has spent its request budget: 4 ms by default, `[Performance]
RequestBudgetMs`, at most 2 ms while a building job holds the game tick, and always at least one request per frame so
the queue moves (`src/StationGodMCP.Mod/Pure/Runtime/FrameBudget.cs:7-37`; `StationGodMod.cs:301-321`). A request
not reached within 30 seconds is answered `game_timeout` by its listener and skipped unrun, without costing budget
(`StationGodRequestDispatcher.cs:52-58`, `:88-96`).

Serialising the reply also happens on the main thread, through one shared serializer
(`src/StationGodMCP.Mod/Api/ApiHost.cs:127-148`, `src/StationGodMCP.Mod/Api/Shared/ApiJson.cs:20-38`). The 1.10.0
review considered serialising on the listener threads instead and cut it (owner notes, `CLAUDE.md`, State, 1.10.0).

Building jobs are a separate mechanism. One job runs at a time across all building tools; it asks the game to hold its
tick and advances once per frame after the requests; up to 8 more wait in a queue
(`src/StationGodMCP.Mod/Api/Shared/Game/HeldTickJobs.cs:14-31`, `:176`). While a job holds the tick, every extra
request millisecond lengthens the pause, which is why the budget drops to 2 ms then.

First in, first out was fair because every connection had at most one request in flight and waited for its reply
before sending the next (owner notes `reference/PERFORMANCE-STRATEGY.md`, section 2.1, not in the repository). With
version 2, a connection may have several requests in flight, and that argument no longer holds.

## What requests cost

The mod measures itself. Since 1.9.1 `mod_info.runtime` reports, per method, the main-thread time of the handler and
of serialising, the wait in the queue and the reply size, each as total, mean and maximum, and per frame the requests
served, the time spent and how often the budget held requests back (`src/StationGodMCP.Mod/Pure/Runtime/MethodTimings.cs:34-90`,
`src/StationGodMCP.Mod/Api/Views/RuntimeViews.cs:14-146`; `docs/devices-and-logic.md`, *Health and game updates*).
Those numbers have not yet been read from the owner's game: the live check for 1.9.1 is still open (owner notes,
`TODO.md`, "1.9.1 performance phase 1: live checks"). Stage 0 of the plan reads them ([stages.md](stages.md)).

What is known today comes from the dashboard's own meter, which times every pipe call it makes
(StationeersScriptDashboard `stationscript/metrics.py`), read over a 60-second window on 2026-10-01 before the 1.10.0
changes (owner notes `reference/PERFORMANCE-STRATEGY.md`, section 1.2):

| What | Measured |
| --- | --- |
| Dashboard calls | 2,456 a minute, 6,950 KB of replies a minute, the pipe busy 57.5 % of the time (92-96 % late in a long session) |
| By method | `inspect_slots` 970 a minute (14.3 ms round trip, 1.6 KB); `atmosphere_contents` 758 (14.4 ms, 2.4 KB); `read_logic_many` 215 (12.5 ms, 5.5 KB) |
| Smelter card | 29 calls a tick, 462 ms a tick on average (973 ms at worst) |
| Large replies | `list_devices` for the whole world 89 KB, `item_totals` 79 KB, `plants` 48 KB every 10 s, `find_items` on the player 47 KB, `thing_health` for 256 ids about 20 KB |

A round trip of about 14 ms for calls that do little work means the cost was the wait for the next frame, not the
mod's code. Since then the dashboard reads each card's devices in one `read_devices` call per gateway per tick
(StationeersScriptDashboard commit `e62012a`, `stationscript/runner.py:491-528`) and the smelter went from about 1,550
to 560 calls a minute, its tick from 530 to 190 ms on average (commit `d347e88`).

The main-thread cost per request was estimated, not measured, by adding up its stages: roughly 0.04-0.1 ms for a small
read such as `read_logic`, 0.15-0.4 ms for one `inspect_slots` or `atmosphere_contents`, and 3-13 ms plus the tool's own
game reads for a reply of 50-95 KB, most of it serialising (`reference/PERFORMANCE-STRATEGY.md`, section 1.3). The owner's brief for this work reports `thing_health` on 84
objects at 62.5 KB a call (not re-measured here; the 24 keys per object are in
`src/StationGodMCP.Mod/Api/Views/ThingHealthViews.cs:196-249`). Planning and survey tools can take far longer:
`deep_miner_spots` caps its search at 8 seconds (owner notes, `CLAUDE.md`, State, 1.7.0); whether that search runs on
the main thread is GUESS.

## Lanes

Version 2 sorts every call into a lane when it arrives:

| Lane | What goes there | Share of the frame |
| --- | --- | --- |
| subscriptions | sampling due subscriptions | up to `SubscriptionBudgetMs`, default 1.5 ms, and at least one due subscription per frame |
| light | calls of cost `instant` or `bounded` whose measured mean stays at or under `HeavyThresholdMs` (1 ms) | the rest of `RequestBudgetMs`, and at least one call per frame |
| heavy | calls of cost `world`, `plan` or `job`, and any call whose measured mean exceeds `HeavyThresholdMs` | at most one call per frame, only while the frame is under budget, or once its oldest call has waited `HeavyMaxWaitFrames` frames (10) |

The cost comes from the catalogue, at the call's arguments (`cost`, `x-cost-when`; [catalogue.md](catalogue.md)). The
measured mean is a moving average of the method's main-thread time (handler plus serialising), over its last 32 calls,
kept per method beside today's totals. Measurement can move a method to the heavy lane but never to the light one,
so a scan that happened to be cheap on an empty world is not trusted on a full one.

Calls of cost `stream` (`sample_logic`) do not occupy a lane while they run: they register an internal subscription
and are answered when their duration ends.

Building jobs stay as they are: one at a time, advanced after the requests (`StationGodMod.cs:119`). Starting a job
(a confirmed run of a building tool) is a heavy call, because its preflight is a full plan.

## One frame, step by step

```text
budget  = FrameBudget.For(RequestBudgetMs, jobHoldsTick).LimitMs      today's rule: 4 ms, 2 ms while a job holds the tick
spent   = 0

1. Subscriptions: take due subscriptions in round-robin order across connections; sample each (one read of all its
   items); stop when spent >= SubscriptionBudgetMs, except that the first due one always runs.
2. Light lane: take calls in round-robin order across connections (one call per connection per round, skipping a
   connection whose next call must wait for an earlier write); run each; stop when spent >= budget, except that the
   first light call always runs.
3. Heavy lane: if the lane has a call and (spent < budget or its oldest call has waited >= HeavyMaxWaitFrames
   frames), take one call, round-robin across connections, and run it.
4. Expired calls (deadline passed before start) are answered game_timeout as they are met, without cost.
5. HeldTickJobs.Tick(), previews, highlights, flight recorder, as today.
```

Why this order. Subscriptions and light calls are what keeps the dashboard's cards live; they are cheap and they come
first, so a heavy call arriving in the same frame waits until they are done. A heavy call cannot be split, so the lane
runs at most one per frame and only when the frame has room; the waiting bound guarantees it still runs within about
10 frames when the light lane is always busy. Round-robin per connection means an agent with sixteen calls in flight
gets one turn per round like a dashboard with one.

The per-connection ordering rule from [protocol.md](protocol.md) (*Order on one connection*) is enforced here: a
connection's call that must wait for an earlier write on the same connection is skipped in its round, not run out of
order. A write in the heavy lane therefore holds back that connection's later light calls, which is what the client
asked for by sending them in that order.

What the owner sees. A frame with nothing heavy costs at most about the budget, as today. A frame with a heavy call
costs the budget plus that call, as today for a frame that met one, but such frames are never back to back while light
work is waiting, and the dashboard's reads never queue behind a survey.

## Subscriptions

A subscription is a set of device reads the mod repeats at an interval and compares with what it last sent. Its cost is
the same game reads as the equivalent `read_devices` poll, minus the request parsing, minus serialising whatever did
not change; but it is paid on the mod's schedule, so it needs its own limits.

Cost estimate. When a subscription is created, the mod estimates its cost per sample from the `read_devices` parts it
reads, using per-part figures measured on that game (moving averages of the sampling time per logic value, per slot
read, per atmosphere, per reagents read). Until enough samples exist it uses starting values: 0.005 ms per logic or slot
value, 0.08 ms per atmosphere, 0.05 ms per reagents read (from the estimates in `reference/PERFORMANCE-STRATEGY.md`,
section 1.3; GUESS until stage 0 measures them). The projected load of a subscription is its cost per sample times its
samples per real second (at normal game speed, one game second is one real second).

Admission. A `subscribe` is refused with `subscription_limit` when:

- the connection would hold more than 32 subscriptions or 1,024 values across them (the same value count `read_devices`
  allows in one call, `src/StationGodMCP.Mod/Pure/DeviceReads/DeviceReadRequest.cs:16-22`); or
- the projected load of every subscription on the mod, this one included, would exceed half of
  `SubscriptionBudgetMs` per frame at 30 frames a second (so, by default, 22.5 ms of sampling per second). The reply's
  `data` gives the projection, so the client can lower its rate or its items.

Interval. At least 0.5 game seconds, one game tick; values the game updates once per tick do not change faster
(GUESS for atmospheres, which the game's atmospherics thread updates on its own schedule). A sample is taken in the
first frame on or after it is due.

Running late. If the subscription lane cannot sample everything due in a frame, the oldest due samples go first next
frame. Every event reports `late_ms`, how far after its due time it was read. A subscription more than three intervals
late for a minute is reported in `mod_info` and its interval doubled, which is announced in its next event as
`interval_s`.

Comparing. The mod keeps, per subscription, the last value it sent for each item part (numbers as doubles, everything
else as its JSON text). A sample compares against those and builds `changes` only from differences beyond the
subscription's tolerance, so an unchanged sample costs the reads and the comparison but no serialising and no event.

## Settings

All under `[Performance]` in the mod's config, read at load, as `RequestBudgetMs` is today (`StationGodMod.cs:301-321`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `RequestBudgetMs` | 4 | As today: the frame's budget for calls; 0 is unlimited. |
| `SubscriptionBudgetMs` | 1.5 | The subscription lane's share, taken out of the same frame. 0 turns subscriptions off. |
| `HeavyThresholdMs` | 1.0 | A method whose recent mean main-thread time is above this goes to the heavy lane. |
| `HeavyMaxWaitFrames` | 10 | A heavy call waiting this many frames runs even when the frame is over budget. |

`[Server] MaxConnections` (32) and the per-connection `max_in_flight` (16) are in [protocol.md](protocol.md).

## What mod_info adds

`mod_info.runtime` gains:

- `lanes`: per lane, calls served, frames it ran in, the longest wait in frames, budget stops;
- `connections`: per connection, `client_id`, client name, transport, level, calls in flight and served, subscriptions
  and their values, bytes sent;
- `subscriptions`: count, values, mean and maximum sampling time per frame, events sent and merged, resyncs, late
  subscriptions;
- `catalogue_drift`: per method, argument names read that the catalogue does not declare (should be empty).

## How it is tested

The scheduler is a pure class in `Pure/` with the clock and the cost of each call injected, so its rules are unit
tests:

- the first light call of a frame always runs, and light calls stop at the budget;
- at most one heavy call per frame, and a heavy call that has waited `HeavyMaxWaitFrames` runs even over budget;
- round-robin: with connection A holding 16 light calls and B holding 1, B's call runs in the first round;
- order on one connection: a read sent after a write on the same connection never starts before the write finishes,
  and reads on one connection may pass each other;
- an expired call is answered `game_timeout`, not run and not charged;
- a measured mean above the threshold moves a light method to the heavy lane, and a cheap world scan stays heavy;
- subscription admission refuses the call that would pass the limits and gives the projection;
- with `RequestBudgetMs` 0 and no subscriptions, the order of calls from single-request connections is first in,
  first out, as today.

The live checks on the test server are in [stages.md](stages.md) (stage 8).
