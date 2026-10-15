# Sharing the game's main thread

Everything StationGod does with the game's objects happens on the main thread, between frames, because the game's
objects may be touched only there. Every millisecond spent there is a millisecond the frame is late, so calls wait
their turn: each frame gets a budget, each call is sorted into a lane by what it is expected to cost, at most one
heavy call runs in a frame, and due subscription samples get their own share first. The first light call and the
first due sample of a frame always run, so no queue can stall. Calls from one client take turns with calls from
another, so an agent with sixteen calls in flight cannot push the dashboard's one read to the back. All of this runs
from `StationGodMod.Update`, which calls the frame once per frame on the host
(`src/StationGodMCP.Mod/StationGodMod.cs:206-230`).

## The frame

`RunFrame` runs five lanes in order and one sweep (`src/StationGodMCP.Mod/Pure/Scheduling/FrameScheduler.cs:234-245`):

1. **Samples.** The subscription lane takes due samples, oldest due first and round robin across connections, while
   it has spent less than its share; the first sample of a frame always runs
   (`FrameScheduler.cs:257-270`; `Pure/Subscriptions/SubscriptionEngine.cs:56-59`, `:676-694`).
2. **Light.** Calls predicted light are taken in rounds (one call per client per round, from its streams in turn)
   until the frame has spent the budget, or 64 calls, whichever comes first; the first light call always runs
   (`FrameScheduler.cs:293-326`; `SchedulerSettings.cs:25-27`).
3. **Heavy.** At most one call a frame. While the frame is under budget it is the first call by the same rounds;
   when the frame is over, only a call that has waited `HeavyMaxWaitFrames` frames may run, and it takes its place in
   the rounds so a one-call client is not passed over forever (`FrameScheduler.cs:329-367`, `:369-400`).
4. **Slice.** Work deferred over frames — a walk's first call building its ids, a `read sample` run — runs one slice
   in what the frame's calls left of the budget, and over budget takes a single step once it has waited
   `HeavyMaxWaitFrames` frames (`FrameScheduler.cs:276-292`; `Protocol/DeferredCalls.cs:23`;
   `Protocol/SubscriptionHub.cs:21-27`).
5. **Jobs.** The running building job's step (`HeldTickJobs.Lane`) runs on the slice lane's rule in what is left
   after it, so StationGod's job steps are inside the frame's budget too (`FrameScheduler.cs`, `RunSlice`;
   `Api/Shared/Game/HeldTickJobs.cs`, `JobLane`). A job state with long work does it as `Work` (below) and goes on
   next frame.
6. A call whose deadline passed before it started is answered `game_timeout` when a lane's search meets it, unrun
   (`FrameScheduler.cs:452-470`).

The budget is `[Performance] RequestBudgetMs`, 4 ms by default; 0 means unlimited. While a building job holds the
game tick the budget is at most 2 ms, because every extra request millisecond lengthens the pause the job asked for
(`Pure/Runtime/FrameBudget.cs:12-19`, `:33-37`; `Api/Shared/Game/HeldTickJobs.cs:38-70`). The subscription lane's
share is `SubscriptionBudgetMs` (1.5 ms), capped at half the frame's budget; so with the defaults subscriptions get
up to 1.5 ms and light calls at least 2.5 ms, and while a job holds the tick at most 1 ms and 1 ms
(`Pure/Scheduling/SchedulerSettings.cs:94-117`).

## Work over frames

Every handler that can loop over more than a page's worth of things does its work as `Work`
(`src/StationGodMCP.Mod/Pure/Scheduling/Work.cs`): a body is an iterator that does a bounded step (one candidate, one
batch of a search's expansions, one piece of an apply) and yields; nested bodies are chained by yielding their steps,
and a body sets its `Answer` before it ends. `Work.Step` takes steps until the clock reaches the slice's end, at least
one, so a slice overruns by one step at most. A handler answers through `WorkResult`
(`Api/Shared/WorkResult.cs`): stepped in its own frame up to the budget, answered at once if it ends there, else
deferred (`VerbOutcome.Pending`) and stepped in the slice lane. A started call's stop (cancel requested, deadline
passed) is met at every yield (`WorkStop`, set by `Protocol/DeferredCalls.cs` around each slice); the slice lane then
answers it. Job states step their preflight, apply and checks the same way in the job lane, with the game tick held
throughout, so no tick sees a half-done change; only frames pass between steps. Two kinds of apply stay in one step,
because Unity destroys a thing at the end of the frame it was destroyed in: a run's or remove structure's removals
(every `OnServer.Destroy` lands at the same frame's end, so no removal reads a network another one's destroy already
rebuilt), and the swaps and changed pieces of a chute network (`Chute.OnDestroy` rebuilds its neighbours' networks
whatever was done first). A caller that runs a call itself and needs its answer at once (undo's replay) takes it to
its end in one frame (`VerbOutcome.Settled`).

A pending result may answer another pending outcome: the work goes on with it (`PendingVerbCall` steps into it in the
same slice while time is left), so a write's report is described over frames after its plan (swap's
`Describing`/`UpgradeReports.Building`, `StructureReports.Building`). A step longer than `WorkSteps.LongStepMs` (1 ms)
breaks the bound a step promises and is logged with its body, at most once a second.

The reply is never written in the frame (cost design A5): the game's thread hands the plain views to the
connection's writer thread (`Protocol/CallReplyLine.cs`), which writes the text once, shaped and capped as it goes.
`main_thread_ms` counts the handler; `serialize_ms` is the writer thread's. After a load, `World/LoadWarmup.cs`
compiles the mod's methods and the ones they call directly (`CalledMethods`), 1 ms of each frame, so a first call
does not pay for compiling them.

## Order on a stream

A call carries a stream number; calls on different streams never wait for each other, and calls on one stream keep
their order where it matters: a write or cheat call starts only once every earlier call of that stream is answered,
and no later call of it starts before the write is answered; reads may pass reads. A call the runner leaves
unanswered (a deferred call) holds its stream until it answers, reads included; ending the stream or closing the
connection lets it go at once (`Pure/Scheduling/FrameScheduler.cs:19-28`, `:452-476`;
`src/StationGodMCP.Mod/Protocol/CallSession.cs:18-26`).

## Rounds

Rounds have two levels. First by **client**: connections that gave the same name in `hello` share one place in the
round, and a connection without a name is a client of its own, so a hub holding one connection for all its programs
is one client beside the dashboard (`FrameScheduler.cs:19-28`, `:71-95`; `Protocol/CallScheduler.cs:245-256`). Then
by **stream** within the client: each stream takes one call per client turn, in turn order. A client whose streams
are all held by earlier writes loses its turn; one with any stream that may start does not
(`FrameScheduler.cs:411-450`).

## Cost classes and the prediction

Each what states an `x-cost` class in the catalogue; the mod's scheduler reads the class and, where the class has a
`per` option, the number of items the call asks for (`src/StationGodMCP.Mod/Pure/Catalogue/CallClasses.cs:14-22`;
`Pure/Scheduling/CallProfile.cs:44-50`):

| Class | Meaning |
| --- | --- |
| `instant` | One thing, a handful of values. |
| `bounded` | Grows with the request, capped by the call's own bounds (128 items, 1,024 values, ...). |
| `world` | Walks a world-sized collection: every thing, every device, every cell of an area. |
| `plan` | Searches or simulates: route planning, dry runs, forecasts. |
| `job` | Starts or advances a building job that holds the game tick. |
| `stream` | Runs over time: `read {what: "sample"}`. |

The class decides the lane. `world`, `plan` and `job` are heavy by nature, whatever they measured last time, so a
scan that was cheap on an empty world is not trusted on a full one. `instant` and `bounded` are predicted from
measurement: a moving average of the call's main-thread time per item over the method's last 32 calls **at that cost
class** (so `read` by one id and by many do not share a history), times this call's items. A method with no history
is light; its first call is what the prediction learns from. A call predicted above `HeavyThresholdMs` (1.0 ms) goes
to the heavy lane, so a 5-item `read logic` stays light while a 128-item one may not
(`Pure/Scheduling/CostPredictor.cs:19-58`, `:60-104`).

### Growth: the cost gate's promise

Beside the class, each what's `x-cost` states its `growth`: how its main-thread cost may grow as the world does —
`by-id` (with the ids asked for), `index` (with the matches), `area` (with the room or box asked about) or `plan`
(with the plan's own bounds). A walk of the whole world is not a growth a call may declare. The class picks the lane
today; the growth is what the cost gate (`tools/sg-scoreboard/drivers/gate.py` in StationeersWorkspace) measures on
the normal and the large test world, together with every call's worst StationGod frame against the request budget
(server info's `update_ms`).

## Subscriptions

A subscription is a set of device reads the mod repeats and compares with what it last sent; it costs the same game
reads as the equivalent `read logic` poll, minus parsing and minus serialising when nothing changed — but it is paid
on the mod's schedule, so it has its own limits (`Pure/Subscriptions/SubscriptionEngine.cs:56-60`).

- **Interval.** 0.5 to 3,600 game seconds, 1 by default; game time stops while the game is paused, so a paused game
  samples nothing (`Pure/Subscriptions/SubscriptionLimits.cs:7-16`).
- **Cost estimate.** From the values read, the atmospheres and the reagents asked for, with starting figures (0.005
  ms per logic or slot value, 0.08 ms per atmosphere, 0.05 ms per reagents read) until the game has measured better
  ones (`SubscriptionLimits.cs:38-52`, `:60-75`).
- **Admission.** A `subscribe` is refused `subscription_limit` with data `{name, limit, requested, projected_ms_per_s}`
  when it would pass one `read logic` call's bounds (128 items, 1,024 values), when the connection would hold more
  than 64 subscriptions or 8,192 values, or when every subscription's projected load together would pass half the
  subscription lane's share at 30 frames a second — 22.5 ms of sampling per second with the defaults
  (`SubscriptionLimits.cs:88-116`, `:122-147`; `Pure/DeviceReads/DeviceReadRequest.cs:17-22`).
- **A refused client polls** the same items with `read logic` at the same interval instead, which is what `sg.watch`
  and `sg.changes` do ([clients.md](clients.md)).

## Settings

All under `[Performance]` in the mod's config, read once at load (`StationGodMod.cs:612-670`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `RequestBudgetMs` | 4 | Main-thread ms one frame may spend answering calls; 0 = unlimited. The first call of a frame always runs. |
| `SubscriptionBudgetMs` | 1.5 | The subscription lane's share, at most half the budget. 0 turns subscriptions off. |
| `HeavyThresholdMs` | 1.0 | A call predicted above this goes to the heavy lane. |
| `HeavyMaxWaitFrames` | 10 | A heavy call passed over this many frames runs even when the frame is over budget. |

## What the game reports

`server {what: "info"}` carries `runtime.frames`: busy frames, calls served, calls per frame (mean and maximum), the
frame's request milliseconds, budget stops and expired calls, with the percentiles and histogram only when
`counters` is asked for (`src/StationGodMCP.Mod/Verbs/ServerInfo.cs:24-51`; `Api/Views/RuntimeViews.cs:280-310`,
`:546-580`). A connection's `subscriptions` count is reported per connection (`RuntimeViews.cs:130-170`). There is
no per-lane report: the scheduler keeps its lane counts in the frame and does not export them.

## Tests

`tests/StationGodMCP.Tests/Scheduling/` covers the rules with an injected clock and fake calls: the first light call
and the budget stop (`FrameSchedulerTests.cs`), one heavy call a frame and the waited-heavy rounds
(`FrameSchedulerFairnessTests.cs`), rounds by client and stream, order and holds
(`FrameSchedulerStreamTests.cs`), the prediction (`CostPredictorTests.cs`), deferred work and slice steps
(`FrameSchedulerDeferredTests.cs`, `DeferredCallsTests.cs`), admissions and settings
(`SchedulerInvariantTests.cs`, `SchedulerSettingsTests.cs`).
