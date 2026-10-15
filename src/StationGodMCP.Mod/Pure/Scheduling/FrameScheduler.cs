#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>
/// Shares the main thread between clients, one frame at a time (scheduling.md, One frame, step by step):
/// <list type="number">
/// <item>the subscription lane takes due samples until it has spent its share (the first always runs);</item>
/// <item>the light lane takes calls in rounds until the frame has spent its budget (the first always runs);</item>
/// <item>the heavy lane runs at most one call by the same rounds: any call while the frame is under budget, and over
/// budget only one that has waited HeavyMaxWaitFrames frames;</item>
/// <item>the slice lane (calls deferred over frames, such as a walk's build) runs one slice in what is left of the
/// budget, and over budget a single step once it has waited HeavyMaxWaitFrames frames;</item>
/// <item>the job lane (the running building job's steps, HeldTickJobs) does the same with what is left after the slice,
/// so StationGod's job steps are inside the frame's budget too.</item>
/// </list>
/// Rounds have two levels: each client (the connections opened with the same client key, or one connection opened
/// without one) has one turn per round, and within a client its streams, a stream being one (connection, stream
/// number), take that client's turns in round robin. A call is sorted into its lane when it arrives (CostPredictor).
/// On one stream a write or cheat call starts only once every earlier call of that stream is answered, and nothing
/// starts before an earlier write or cheat of that stream is answered; reads may pass reads, and calls on different
/// streams never wait for each other (protocol.md, Order on one stream). A call the runner leaves unanswered goes on
/// over later frames and holds its stream until it answers: no later call of that stream starts meanwhile, reads
/// included. Holds are looked at as each frame starts and again after the subscription lane; ending the stream or
/// closing the connection lets go of them at once. A call whose deadline has passed is answered game_timeout when the
/// scheduler meets it, unrun and uncharged.
/// Main thread only, and allocation-free per frame: listener threads hand their calls to the main thread, which
/// enqueues them before RunFrame. Stream and client nodes are reused once empty, so neither a new stream nor a frame
/// allocates once the scheduler has seen as many at once before.
/// </summary>
internal sealed class FrameScheduler<TCall> where TCall : class
{
    private const int ExpectedConnections = 64;

    private readonly SchedulerSettings _settings;
    private readonly ICallRunner<TCall> _runner;
    private readonly IMonotonicClock _clock;
    private readonly CostPredictor _predictor;
    private readonly Ring<Client> _light = new Ring<Client>(Lane.Light);
    private readonly Ring<Client> _heavy = new Ring<Client>(Lane.Heavy);
    private readonly Dictionary<object, Client> _clients = new Dictionary<object, Client>(ExpectedConnections);
    private readonly Stack<Waiting> _spare = new Stack<Waiting>(ExpectedConnections);
    private readonly Stack<StreamQueue> _spareStreams = new Stack<StreamQueue>(ExpectedConnections);
    private readonly Stack<Client> _spareClients = new Stack<Client>(ExpectedConnections);
    private readonly List<Held> _held = new List<Held>(ExpectedConnections);
    private long _arrivals;
    private int _expired;
    private int _sliceWaitFrames;
    private int _jobWaitFrames;

    internal FrameScheduler(SchedulerSettings settings, ICallRunner<TCall> runner, IMonotonicClock clock,
        CostPredictor predictor)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _predictor = predictor ?? throw new ArgumentNullException(nameof(predictor));
    }

    /// <summary>Frames run so far.</summary>
    internal long Frame { get; private set; }

    /// <summary>Started calls still unanswered, each holding its stream.</summary>
    internal int Holding => _held.Count;

    /// <summary>
    /// A new connection. client is the key it shares its place in the rounds by (compared with Equals: the
    /// mod passes the name from hello); null makes it a client of its own.
    /// </summary>
    internal Connection Open(object? client = null)
    {
        Client? node = null;
        if (client == null || !_clients.TryGetValue(client, out node))
        {
            node = _spareClients.Count > 0 ? _spareClients.Pop() : new Client();
            node.Key = client;
            if (client != null)
            {
                _clients.Add(client, node);
            }
        }

        node!.Connections++;
        return new Connection(node);
    }

    /// <summary>
    /// Drops the connection's waiting calls on every stream unrun and unanswered, lets go of its streams' holds, and
    /// refuses any more.
    /// </summary>
    internal void Close(Connection connection)
    {
        if (connection.IsClosed)
        {
            return;
        }

        for (int index = _held.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_held[index].Queue.Owner, connection))
            {
                LetGo(index);
            }
        }

        List<StreamQueue> live = connection.Live;
        while (live.Count > 0)
        {
            DropAll(live[live.Count - 1]);
        }

        connection.MarkClosed();
        Client client = connection.Client;
        if (--client.Connections == 0)
        {
            if (client.Key != null)
            {
                _clients.Remove(client.Key);
            }

            client.Key = null;
            _spareClients.Push(client);
        }
    }

    /// <summary>
    /// Drops the stream's waiting calls unrun and unanswered (whoever ended the stream answers them) and lets go of its
    /// hold (whoever ended it stops its started calls); the connection's other streams are untouched. Returns how many
    /// calls it dropped.
    /// </summary>
    internal int EndStream(Connection connection, long stream)
    {
        if (!connection.Streams.TryGetValue(stream, out StreamQueue? queue))
        {
            return 0;
        }

        for (int index = _held.Count - 1; index >= 0 && queue.Holds > 0; index--)
        {
            if (ReferenceEquals(_held[index].Queue, queue))
            {
                LetGo(index);
            }
        }

        int dropped = queue.Calls.Count;
        DropAll(queue);
        return dropped;
    }

    /// <summary>Queues a call on the connection's stream 0; see the overload with a stream.</summary>
    internal Admission Enqueue(Connection connection, TCall call, CallProfile profile, double deadlineMs) =>
        Enqueue(connection, 0, call, profile, deadlineMs);

    /// <summary>
    /// Queues a call on one stream of the connection in the lane its profile predicts, or refuses it: past
    /// MaxInFlight waiting on the stream, past MaxInFlightPerConnection waiting on the connection, or on a closed
    /// connection. deadlineMs is on the scheduler's clock; PositiveInfinity for none.
    /// </summary>
    internal Admission Enqueue(Connection connection, long stream, TCall call, CallProfile profile, double deadlineMs)
    {
        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        if (connection.IsClosed)
        {
            return Admission.Refused(AdmissionRefusal.ConnectionClosed);
        }

        connection.Streams.TryGetValue(stream, out StreamQueue? queue);
        if (queue != null && queue.Calls.Count >= _settings.MaxInFlight)
        {
            return Admission.Refused(AdmissionRefusal.TooManyInFlight);
        }

        if (connection.WaitingCalls >= _settings.MaxInFlightPerConnection)
        {
            return Admission.Refused(AdmissionRefusal.TooManyInFlightOnConnection);
        }

        queue ??= OpenStream(connection, stream);
        Lane lane = _predictor.LaneFor(profile, _settings.HeavyThresholdMs);
        Waiting waiting = _spare.Count > 0 ? _spare.Pop() : new Waiting();
        waiting.Call = call;
        waiting.Profile = profile;
        waiting.Lane = lane;
        waiting.DeadlineMs = deadlineMs;
        waiting.ArrivedAfterFrame = Frame;
        waiting.Arrival = _arrivals++;
        waiting.Owner = connection;
        waiting.Stream = stream;
        queue.Calls.Add(waiting);
        Joined(queue, lane);
        return Admission.Queued(lane);
    }

    /// <summary>Removes a call of stream 0 that has not started; see the overload with a stream.</summary>
    internal bool Cancel(Connection connection, TCall call) => Cancel(connection, 0, call);

    /// <summary>
    /// Removes a call of the stream that has not started; false when it is not waiting there (it ran, expired or never
    /// came).
    /// </summary>
    internal bool Cancel(Connection connection, long stream, TCall call)
    {
        if (!connection.Streams.TryGetValue(stream, out StreamQueue? queue))
        {
            return false;
        }

        List<Waiting> calls = queue.Calls;
        for (int index = 0; index < calls.Count; index++)
        {
            Waiting waiting = calls[index];
            if (ReferenceEquals(waiting.Call, call))
            {
                RemoveAt(queue, index);
                Recycle(waiting);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One frame: samples, then light calls, then at most one heavy call, then one slice of deferred work, then the
    /// running job's step in what is left. startedMs is when StationGod's work this frame began on the scheduler's
    /// clock (its Update's start), so the budget covers what it did before the scheduler too; NaN starts it now. While it
    /// runs, CurrentFrame holds the frame's start and budget, so a reply built inside it can tell whether the budget
    /// was reached and work sized to the frame knows where to stop.
    /// </summary>
    internal FrameOutcome RunFrame(bool jobHoldsTick, ISampleLane? samples, ISliceLane? slices = null,
        ISliceLane? jobs = null, double startedMs = double.NaN)
    {
        Frame++;
        _expired = 0;
        FrameShares shares = _settings.SharesFor(jobHoldsTick);
        double startMs = double.IsNaN(startedMs) ? _clock.NowMs : Math.Min(startedMs, _clock.NowMs);
        CurrentFrame.Begin(startMs, shares.BudgetMs, FrameBudget.StepReserveMs);
        try
        {
            LetGoOfAnswered();
            int taken = RunSamples(samples, startMs, shares.SubscriptionMs);
            LetGoOfAnswered();
            int light = RunLight(startMs, shares.BudgetMs, out bool lightStopped);
            int heavy = RunHeavy(startMs, shares.BudgetMs, out bool heavyStopped);
            RunSlice(slices, CurrentFrame.EndMs, ref _sliceWaitFrames);
            RunSlice(jobs, CurrentFrame.EndMs, ref _jobWaitFrames);
            return new FrameOutcome(taken, light, heavy, _expired, _clock.NowMs - startMs, lightStopped || heavyStopped);
        }
        finally
        {
            CurrentFrame.End();
        }
    }

    private int RunSamples(ISampleLane? samples, double startMs, double shareMs)
    {
        if (samples == null)
        {
            return 0;
        }

        int taken = 0;
        using ProfScope lane = Prof.Scope(ProfId.SubscriptionLane);
        while ((taken == 0 || _clock.NowMs - startMs < shareMs) && samples.RunNextDueSample())
        {
            taken++;
        }

        return taken;
    }

    // Deferred work (and the job lane after it) gets what the frame's calls left of its budget; a frame with none left
    // runs no slice, unless the work has waited HeavyMaxWaitFrames such frames, when it takes a single step, so it
    // always moves. Each lane counts its own waiting frames.
    private void RunSlice(ISliceLane? slices, double untilMs, ref int waitFrames)
    {
        if (slices == null || !slices.HasWork)
        {
            waitFrames = 0;
            return;
        }

        if (_clock.NowMs >= untilMs && ++waitFrames <= _settings.HeavyMaxWaitFrames)
        {
            return;
        }

        waitFrames = 0;
        slices.RunSlice(untilMs);
    }

    private int RunLight(double startMs, double budgetMs, out bool budgetStopped)
    {
        budgetStopped = false;
        int served = 0;
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            for (int turn = _light.Count; turn > 0; turn--)
            {
                if (served >= SchedulerSettings.MaxLightCallsPerFrame || (served > 0 && _clock.NowMs - startMs >= budgetMs))
                {
                    budgetStopped = _light.Count > 0;
                    return served;
                }

                Client? client = _light.First;
                if (client == null)
                {
                    break;
                }

                Waiting? next = TakeTurn(client, Lane.Light);
                _light.MoveToBackIfLinked(client);
                if (next != null)
                {
                    Run(next);
                    served++;
                    progressed = true;
                }
            }
        }

        return served;
    }

    private int RunHeavy(double startMs, double budgetMs, out bool budgetStopped)
    {
        budgetStopped = false;
        if (_clock.NowMs - startMs >= budgetMs)
        {
            if (RunWaitedHeavy(out bool anyEligible))
            {
                return 1;
            }

            budgetStopped = anyEligible;
            return 0;
        }

        for (int turn = _heavy.Count; turn > 0; turn--)
        {
            Client? client = _heavy.First;
            if (client == null)
            {
                break;
            }

            Waiting? next = TakeTurn(client, Lane.Heavy);
            _heavy.MoveToBackIfLinked(client);
            if (next != null)
            {
                Run(next);
                return 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// A frame over budget: the heavy lane's rounds, counting only calls that have waited HeavyMaxWaitFrames. The first
    /// client in turn order with such a call runs one from its first stream in turn order that has one; that stream and
    /// that client go to the back of their rounds, the clients passed over keep their places. anyEligible tells whether
    /// any heavy call could have started, waited or not.
    /// </summary>
    private bool RunWaitedHeavy(out bool anyEligible)
    {
        anyEligible = false;
        Client? nextClient;
        for (Client? client = _heavy.First; client != null; client = nextClient)
        {
            // Eligible answers expired calls, which may unlink the stream or the client being looked at.
            nextClient = _heavy.After(client);
            Ring<StreamQueue> streams = client.Streams(Lane.Heavy);
            StreamQueue? nextQueue;
            for (StreamQueue? queue = streams.First; queue != null; queue = nextQueue)
            {
                nextQueue = streams.After(queue);
                int index = Eligible(queue, Lane.Heavy);
                if (index < 0)
                {
                    continue;
                }

                anyEligible = true;
                Waiting waiting = queue.Calls[index];
                if (Frame - waiting.ArrivedAfterFrame - 1 < _settings.HeavyMaxWaitFrames)
                {
                    continue;
                }

                RemoveAt(queue, index);
                streams.MoveToBackIfLinked(queue);
                _heavy.MoveToBackIfLinked(client);
                Run(waiting);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The client's turn in a lane: its streams in that lane are tried in their round-robin order, each going to the
    /// back of the client's round after it is tried, until one has a call that may start. Removes and returns that
    /// call, or null when every stream is held back by an earlier write.
    /// </summary>
    private Waiting? TakeTurn(Client client, Lane lane)
    {
        Ring<StreamQueue> streams = client.Streams(lane);
        for (int tries = streams.Count; tries > 0; tries--)
        {
            StreamQueue? queue = streams.First;
            if (queue == null)
            {
                return null;
            }

            Waiting? next = Take(queue, lane);
            streams.MoveToBackIfLinked(queue);
            if (next != null)
            {
                return next;
            }
        }

        return null;
    }

    /// <summary>Removes and returns the stream's first call that may start in this lane, or null.</summary>
    private Waiting? Take(StreamQueue queue, Lane lane)
    {
        int index = Eligible(queue, lane);
        if (index < 0)
        {
            return null;
        }

        Waiting waiting = queue.Calls[index];
        RemoveAt(queue, index);
        return waiting;
    }

    /// <summary>
    /// The index of the stream's first call in this lane that may start now, or -1. Expired calls met on the way are
    /// answered and removed. A write or cheat call may start only first in line, and stops the search either way; a
    /// stream holding an unanswered started call starts nothing.
    /// </summary>
    private int Eligible(StreamQueue queue, Lane lane)
    {
        List<Waiting> calls = queue.Calls;
        double now = _clock.NowMs;
        int index = 0;
        while (index < calls.Count)
        {
            Waiting waiting = calls[index];
            if (waiting.DeadlineMs <= now)
            {
                RemoveAt(queue, index);
                _runner.Expire(waiting.Call!);
                _expired++;
                Recycle(waiting);
                continue;
            }

            if (queue.Holds > 0)
            {
                return -1;
            }

            if (waiting.Profile.IsOrdered)
            {
                return index == 0 && waiting.Lane == lane ? index : -1;
            }

            if (waiting.Lane == lane)
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private void Run(Waiting waiting)
    {
        TCall call = waiting.Call!;
        CallProfile profile = waiting.Profile;
        Connection owner = waiting.Owner!;
        long stream = waiting.Stream;
        Recycle(waiting);
        double before = _clock.NowMs;
        _runner.Run(call);
        _predictor.Observe(profile, _clock.NowMs - before);
        if (!_runner.IsAnswered(call))
        {
            Hold(owner, stream, call);
        }
    }

    /// <summary>A started call goes on unanswered: its stream starts nothing more until it answers.</summary>
    private void Hold(Connection owner, long stream, TCall call)
    {
        if (!owner.Streams.TryGetValue(stream, out StreamQueue? queue))
        {
            queue = OpenStream(owner, stream);
        }

        queue.Holds++;
        _held.Add(new Held(queue, call));
    }

    private void LetGoOfAnswered()
    {
        for (int index = _held.Count - 1; index >= 0; index--)
        {
            if (_runner.IsAnswered(_held[index].Call))
            {
                StreamQueue queue = _held[index].Queue;
                LetGo(index);
                ReleaseIfIdle(queue);
            }
        }
    }

    // Takes a hold out by moving the last one into its place; the caller releases the stream once nothing is left there.
    private void LetGo(int index)
    {
        _held[index].Queue.Holds--;
        int last = _held.Count - 1;
        _held[index] = _held[last];
        _held.RemoveAt(last);
    }

    private StreamQueue OpenStream(Connection connection, long stream)
    {
        StreamQueue queue = _spareStreams.Count > 0 ? _spareStreams.Pop() : new StreamQueue(_settings.MaxInFlight);
        queue.Owner = connection;
        queue.Id = stream;
        queue.LiveIndex = connection.Live.Count;
        connection.Live.Add(queue);
        connection.Streams.Add(stream, queue);
        return queue;
    }

    /// <summary>A call joined the stream: the stream joins its client's round and the client the lane's round if new there.</summary>
    private void Joined(StreamQueue queue, Lane lane)
    {
        Connection connection = queue.Owner!;
        connection.WaitingCalls++;
        if (queue.Added(lane) == 1)
        {
            connection.Client.Streams(lane).AddLast(queue);
        }

        if (connection.Client.Added(lane) == 1)
        {
            TurnsOf(lane).AddLast(connection.Client);
        }
    }

    /// <summary>
    /// Takes a call out of its stream: a stream with nothing left in the lane leaves its client's round there, a
    /// client with nothing left in the lane leaves the lane's round, and a stream with nothing left at all is reused.
    /// </summary>
    private void RemoveAt(StreamQueue queue, int index)
    {
        Lane lane = queue.Calls[index].Lane;
        queue.Calls.RemoveAt(index);
        Connection connection = queue.Owner!;
        Client client = connection.Client;
        connection.WaitingCalls--;
        if (queue.Removed(lane) == 0)
        {
            client.Streams(lane).Remove(queue);
        }

        if (client.Removed(lane) == 0)
        {
            TurnsOf(lane).Remove(client);
        }

        ReleaseIfIdle(queue);
    }

    private void DropAll(StreamQueue queue)
    {
        List<Waiting> calls = queue.Calls;
        while (calls.Count > 0)
        {
            Waiting waiting = calls[calls.Count - 1];
            RemoveAt(queue, calls.Count - 1);
            Recycle(waiting);
        }

        ReleaseIfIdle(queue);
    }

    /// <summary>A stream with no call waiting and none started unanswered is reused.</summary>
    private void ReleaseIfIdle(StreamQueue queue)
    {
        if (queue.Owner != null && queue.Calls.Count == 0 && queue.Holds == 0)
        {
            Release(queue);
        }
    }

    private void Release(StreamQueue queue)
    {
        Connection connection = queue.Owner!;
        connection.Streams.Remove(queue.Id);
        List<StreamQueue> live = connection.Live;
        StreamQueue last = live[live.Count - 1];
        live[queue.LiveIndex] = last;
        last.LiveIndex = queue.LiveIndex;
        live.RemoveAt(live.Count - 1);
        queue.Owner = null;
        _spareStreams.Push(queue);
    }

    private void Recycle(Waiting waiting)
    {
        waiting.Call = null;
        waiting.Owner = null;
        _spare.Push(waiting);
    }

    private Ring<Client> TurnsOf(Lane lane) => lane == Lane.Light ? _light : _heavy;

    /// <summary>One connection: its client, its streams with calls waiting or held, and the calls' total.</summary>
    internal sealed class Connection
    {
        internal Connection(Client client)
        {
            Client = client;
        }

        /// <summary>Calls waiting, every stream and both lanes.</summary>
        internal int WaitingCalls { get; set; }

        internal bool IsClosed { get; private set; }

        internal Client Client { get; }

        /// <summary>The streams with calls waiting or held by a started call, by number.</summary>
        internal Dictionary<long, StreamQueue> Streams { get; } = new Dictionary<long, StreamQueue>();

        /// <summary>The same streams as a list (each knows its index), for closing without allocating.</summary>
        internal List<StreamQueue> Live { get; } = new List<StreamQueue>();

        internal void MarkClosed() => IsClosed = true;
    }

    /// <summary>
    /// A node that sits in up to one ring per lane at once: a client in a lane's rounds, a stream in its client's
    /// rounds of that lane.
    /// </summary>
    internal abstract class RingNode
    {
        private readonly RingNode?[] _previous = new RingNode?[2];
        private readonly RingNode?[] _next = new RingNode?[2];
        private readonly bool[] _linked = new bool[2];
        private readonly int[] _waiting = new int[2];

        internal bool IsLinked(Lane lane) => _linked[(int)lane];

        /// <summary>Calls waiting in the lane under this node.</summary>
        internal int WaitingIn(Lane lane) => _waiting[(int)lane];

        internal int Added(Lane lane) => ++_waiting[(int)lane];

        internal int Removed(Lane lane) => --_waiting[(int)lane];

        internal RingNode? NextIn(int lane) => _next[lane];

        internal RingNode? PreviousIn(int lane) => _previous[lane];

        internal void Link(int lane, RingNode? previous, RingNode? next)
        {
            _previous[lane] = previous;
            _next[lane] = next;
            _linked[lane] = true;
        }

        internal void SetNext(int lane, RingNode? next) => _next[lane] = next;

        internal void SetPrevious(int lane, RingNode? previous) => _previous[lane] = previous;

        internal void Unlink(int lane)
        {
            _previous[lane] = null;
            _next[lane] = null;
            _linked[lane] = false;
        }
    }

    /// <summary>
    /// A client: one or more connections opened with the same key. It has one turn per round in each lane it has calls
    /// in, and its streams with calls in a lane take those turns in round robin.
    /// </summary>
    internal sealed class Client : RingNode
    {
        private readonly Ring<StreamQueue> _lightStreams = new Ring<StreamQueue>(Lane.Light);
        private readonly Ring<StreamQueue> _heavyStreams = new Ring<StreamQueue>(Lane.Heavy);

        internal object? Key { get; set; }

        /// <summary>Open connections that share this client.</summary>
        internal int Connections { get; set; }

        internal Ring<StreamQueue> Streams(Lane lane) => lane == Lane.Light ? _lightStreams : _heavyStreams;
    }

    /// <summary>
    /// One stream of a connection: its calls waiting in the scheduler, in arrival order, and how many of its started
    /// calls are still unanswered.
    /// </summary>
    internal sealed class StreamQueue : RingNode
    {
        internal StreamQueue(int maxInFlight)
        {
            Calls = new List<Waiting>(maxInFlight);
        }

        internal Connection? Owner { get; set; }

        internal long Id { get; set; }

        /// <summary>Its index in its connection's Live list.</summary>
        internal int LiveIndex { get; set; }

        internal List<Waiting> Calls { get; }

        /// <summary>Its started calls still unanswered; while any is, none of its calls starts.</summary>
        internal int Holds { get; set; }
    }

    /// <summary>A started call still unanswered and the stream it holds.</summary>
    private readonly struct Held
    {
        internal Held(StreamQueue queue, TCall call)
        {
            Queue = queue;
            Call = call;
        }

        internal StreamQueue Queue { get; }

        internal TCall Call { get; }
    }

    /// <summary>One waiting call; reused once it leaves, so arrivals do not allocate.</summary>
    internal sealed class Waiting
    {
        internal TCall? Call { get; set; }

        internal CallProfile Profile { get; set; }

        internal Lane Lane { get; set; }

        internal double DeadlineMs { get; set; }

        /// <summary>FrameScheduler.Frame when it arrived: frames run before it came.</summary>
        internal long ArrivedAfterFrame { get; set; }

        /// <summary>Its place in the order of every arrival, all connections together.</summary>
        internal long Arrival { get; set; }

        /// <summary>The connection and stream it came on: the stream it holds if it goes on unanswered.</summary>
        internal Connection? Owner { get; set; }

        internal long Stream { get; set; }
    }

    /// <summary>
    /// A round: the nodes with calls in one lane, in turn order, as a doubly linked list through the nodes themselves,
    /// so joining, leaving from anywhere and going to the back allocate nothing. A node joins at the back when its
    /// first call in the lane arrives, goes to the back after its turn while it still has calls there, and leaves as
    /// soon as it has none, so one-call clients are served first come, first served.
    /// </summary>
    internal sealed class Ring<TNode> where TNode : RingNode
    {
        private readonly int _lane;
        private TNode? _head;
        private TNode? _tail;

        internal Ring(Lane lane)
        {
            _lane = (int)lane;
        }

        internal int Count { get; private set; }

        internal TNode? First => _head;

        internal TNode? After(TNode node) => (TNode?)node.NextIn(_lane);

        internal void AddLast(TNode node)
        {
            node.Link(_lane, _tail, null);
            if (_tail == null)
            {
                _head = node;
            }
            else
            {
                _tail.SetNext(_lane, node);
            }

            _tail = node;
            Count++;
        }

        internal void Remove(TNode node)
        {
            if (!node.IsLinked((Lane)_lane))
            {
                return;
            }

            RingNode? previous = node.PreviousIn(_lane);
            RingNode? next = node.NextIn(_lane);
            if (previous == null)
            {
                _head = (TNode?)next;
            }
            else
            {
                previous.SetNext(_lane, next);
            }

            if (next == null)
            {
                _tail = (TNode?)previous;
            }
            else
            {
                next.SetPrevious(_lane, previous);
            }

            node.Unlink(_lane);
            Count--;
        }

        /// <summary>After a node's turn: to the back while it is still in the round.</summary>
        internal void MoveToBackIfLinked(TNode node)
        {
            if (node.IsLinked((Lane)_lane) && !ReferenceEquals(node, _tail))
            {
                Remove(node);
                AddLast(node);
            }
        }
    }
}
