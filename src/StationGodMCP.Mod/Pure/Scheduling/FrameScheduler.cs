#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>
/// Shares the main thread between connections, one frame at a time (scheduling.md, One frame, step by step):
/// <list type="number">
/// <item>the subscription lane takes due samples until it has spent its share (the first always runs);</item>
/// <item>the light lane takes calls round-robin across connections, one per connection per round, until the frame has
/// spent its budget (the first always runs);</item>
/// <item>the heavy lane runs at most one call: round-robin while the frame is under budget, or, whatever the frame has
/// spent, the oldest one once it has been passed over in HeavyMaxWaitFrames frames.</item>
/// </list>
/// A call is sorted into its lane when it arrives (CostPredictor). On one connection a write or cheat call starts only
/// once every earlier call is answered, and nothing starts before an earlier write or cheat is answered; reads may pass
/// reads (protocol.md, Order on one connection). A call whose deadline has passed is answered game_timeout when the
/// scheduler meets it, unrun and uncharged.
/// Main thread only, and allocation-free per frame: listener threads hand their calls to the main thread, which
/// enqueues them before RunFrame.
/// </summary>
internal sealed class FrameScheduler<TCall> where TCall : class
{
    private const int ExpectedConnections = 64;

    private readonly SchedulerSettings _settings;
    private readonly ICallRunner<TCall> _runner;
    private readonly IMonotonicClock _clock;
    private readonly CostPredictor _predictor;
    private readonly Turns _light = new Turns(Lane.Light);
    private readonly Turns _heavy = new Turns(Lane.Heavy);
    private readonly Stack<Waiting> _spare = new Stack<Waiting>(ExpectedConnections);
    private long _arrivals;
    private int _expired;

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

    /// <summary>A new connection's place in the rounds.</summary>
    internal Connection Open() => new Connection(_settings.MaxInFlight);

    /// <summary>Drops the connection's waiting calls unrun and unanswered, and refuses any more.</summary>
    internal void Close(Connection connection)
    {
        foreach (Waiting waiting in connection.Calls)
        {
            Recycle(waiting);
        }

        connection.MarkClosed();
    }

    /// <summary>
    /// Queues a call in the lane its profile predicts, or refuses it. deadlineMs is on the scheduler's clock;
    /// PositiveInfinity for none.
    /// </summary>
    internal Admission Enqueue(Connection connection, TCall call, CallProfile profile, double deadlineMs)
    {
        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        if (connection.IsClosed)
        {
            return Admission.Refused(AdmissionRefusal.ConnectionClosed);
        }

        if (connection.Calls.Count >= _settings.MaxInFlight)
        {
            return Admission.Refused(AdmissionRefusal.TooManyInFlight);
        }

        Lane lane = _predictor.LaneFor(profile, _settings.HeavyThresholdMs);
        Waiting waiting = _spare.Count > 0 ? _spare.Pop() : new Waiting();
        waiting.Call = call;
        waiting.Profile = profile;
        waiting.Lane = lane;
        waiting.DeadlineMs = deadlineMs;
        waiting.ArrivedAfterFrame = Frame;
        waiting.Arrival = _arrivals++;
        connection.Calls.Add(waiting);
        TurnsOf(lane).Join(connection);
        return Admission.Queued(lane);
    }

    /// <summary>Removes a call that has not started; false when it is not waiting (it ran, expired or never came).</summary>
    internal bool Cancel(Connection connection, TCall call)
    {
        List<Waiting> calls = connection.Calls;
        for (int index = 0; index < calls.Count; index++)
        {
            Waiting waiting = calls[index];
            if (ReferenceEquals(waiting.Call, call))
            {
                RemoveAt(connection, index);
                Recycle(waiting);
                return true;
            }
        }

        return false;
    }

    /// <summary>One frame: samples, then light calls, then at most one heavy call.</summary>
    internal FrameOutcome RunFrame(bool jobHoldsTick, ISampleLane? samples)
    {
        Frame++;
        _expired = 0;
        FrameShares shares = _settings.SharesFor(jobHoldsTick);
        double startMs = _clock.NowMs;
        int taken = RunSamples(samples, startMs, shares.SubscriptionMs);
        int light = RunLight(startMs, shares.BudgetMs, out bool lightStopped);
        int heavy = RunHeavy(startMs, shares.BudgetMs, out bool heavyStopped);
        return new FrameOutcome(taken, light, heavy, _expired, _clock.NowMs - startMs, lightStopped || heavyStopped);
    }

    private int RunSamples(ISampleLane? samples, double startMs, double shareMs)
    {
        if (samples == null)
        {
            return 0;
        }

        int taken = 0;
        while ((taken == 0 || _clock.NowMs - startMs < shareMs) && samples.RunNextDueSample())
        {
            taken++;
        }

        return taken;
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
                    budgetStopped = _light.AnyWaiting();
                    return served;
                }

                Connection connection = _light.Next();
                Waiting? next = Take(connection, Lane.Light);
                _light.Rejoin(connection);
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
        Connection? owner = null;
        Waiting? oldest = null;
        foreach (Connection connection in _heavy.Queue)
        {
            int index = Eligible(connection, Lane.Heavy);
            if (index >= 0 && (oldest == null || connection.Calls[index].Arrival < oldest.Arrival))
            {
                owner = connection;
                oldest = connection.Calls[index];
            }
        }

        if (owner == null || oldest == null)
        {
            return 0;
        }

        if (Frame - oldest.ArrivedAfterFrame - 1 >= _settings.HeavyMaxWaitFrames)
        {
            RemoveAt(owner, owner.Calls.IndexOf(oldest));
            Run(oldest);
            return 1;
        }

        if (_clock.NowMs - startMs >= budgetMs)
        {
            budgetStopped = true;
            return 0;
        }

        for (int turn = _heavy.Count; turn > 0; turn--)
        {
            Connection connection = _heavy.Next();
            Waiting? next = Take(connection, Lane.Heavy);
            _heavy.Rejoin(connection);
            if (next != null)
            {
                Run(next);
                return 1;
            }
        }

        return 0;
    }

    /// <summary>Removes and returns the connection's first call that may start in this lane, or null.</summary>
    private Waiting? Take(Connection connection, Lane lane)
    {
        int index = Eligible(connection, lane);
        if (index < 0)
        {
            return null;
        }

        Waiting waiting = connection.Calls[index];
        RemoveAt(connection, index);
        return waiting;
    }

    /// <summary>
    /// The index of the connection's first call in this lane that may start now, or -1. Expired calls met on the way
    /// are answered and removed. A write or cheat call may start only first in line, and stops the search either way.
    /// </summary>
    private int Eligible(Connection connection, Lane lane)
    {
        List<Waiting> calls = connection.Calls;
        double now = _clock.NowMs;
        int index = 0;
        while (index < calls.Count)
        {
            Waiting waiting = calls[index];
            if (waiting.DeadlineMs <= now)
            {
                RemoveAt(connection, index);
                _runner.Expire(waiting.Call!);
                _expired++;
                Recycle(waiting);
                continue;
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
        Recycle(waiting);
        double before = _clock.NowMs;
        _runner.Run(call);
        _predictor.Observe(profile, _clock.NowMs - before);
    }

    private static void RemoveAt(Connection connection, int index)
    {
        Waiting waiting = connection.Calls[index];
        connection.Calls.RemoveAt(index);
        connection.Removed(waiting.Lane);
    }

    private void Recycle(Waiting waiting)
    {
        waiting.Call = null;
        _spare.Push(waiting);
    }

    private Turns TurnsOf(Lane lane) => lane == Lane.Light ? _light : _heavy;

    /// <summary>One connection's calls waiting in the scheduler, in arrival order.</summary>
    internal sealed class Connection
    {
        private readonly int[] _waiting = new int[2];
        private readonly bool[] _inTurns = new bool[2];

        internal Connection(int maxInFlight)
        {
            Calls = new List<Waiting>(maxInFlight);
        }

        /// <summary>Calls waiting, both lanes.</summary>
        internal int WaitingCalls => Calls.Count;

        internal bool IsClosed { get; private set; }

        internal List<Waiting> Calls { get; }

        internal int WaitingIn(Lane lane) => _waiting[(int)lane];

        internal void Added(Lane lane) => _waiting[(int)lane]++;

        internal void Removed(Lane lane) => _waiting[(int)lane]--;

        internal bool InTurns(Lane lane) => _inTurns[(int)lane];

        internal void SetInTurns(Lane lane, bool value) => _inTurns[(int)lane] = value;

        internal void MarkClosed()
        {
            Calls.Clear();
            _waiting[(int)Lane.Light] = 0;
            _waiting[(int)Lane.Heavy] = 0;
            IsClosed = true;
        }
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
    }

    /// <summary>
    /// A lane's rounds: the connections with calls in that lane, in turn order. A connection joins at the back when its
    /// first call in the lane arrives, goes to the back after its turn while it still has calls there, and leaves when
    /// it has none, so one-call connections are served first come, first served.
    /// </summary>
    private sealed class Turns
    {
        private readonly Lane _lane;

        internal Turns(Lane lane)
        {
            _lane = lane;
        }

        internal Queue<Connection> Queue { get; } = new Queue<Connection>(ExpectedConnections);

        internal int Count => Queue.Count;

        internal void Join(Connection connection)
        {
            connection.Added(_lane);
            if (!connection.InTurns(_lane))
            {
                connection.SetInTurns(_lane, true);
                Queue.Enqueue(connection);
            }
        }

        internal Connection Next() => Queue.Dequeue();

        /// <summary>Puts a connection that just had its turn back at the end, or lets it leave when it has nothing here.</summary>
        internal void Rejoin(Connection connection)
        {
            if (!connection.IsClosed && connection.WaitingIn(_lane) > 0)
            {
                Queue.Enqueue(connection);
            }
            else
            {
                connection.SetInTurns(_lane, false);
            }
        }

        internal bool AnyWaiting()
        {
            foreach (Connection connection in Queue)
            {
                if (connection.WaitingIn(_lane) > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
