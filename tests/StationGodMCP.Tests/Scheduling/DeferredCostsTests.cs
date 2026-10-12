#nullable enable

using System;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// A deferred call's slices cost the main thread too: each slice's ms count in its stream's main_thread_ms
/// (stream_costs) and in its connection's ms for its method (mod_info connections), the call itself once.
/// </summary>
public sealed class DeferredCostsTests : IDisposable
{
    private static readonly CallContext Hub = new CallContext("c1", 2);

    private readonly FakeClock _clock = new FakeClock();
    private readonly DeadlineWatch _deadlines = new DeadlineWatch();

    public void Dispose() => _deadlines.Dispose();

    [Fact]
    public void ACallTakingThreeSlicesCountsEverySliceAndOneCall()
    {
        LaneScheduler scheduler = new LaneScheduler(new SchedulerSettings(4.0, 0, 1, 10, 16), new StreamStores(), _clock,
            () => new WorldFacts("w1", null, 0));
        Connection connection = new Connection(new NoTransport(),
            new ProtocolHost(new ProtocolSettings(1), new NoQueue(), _deadlines, null));
        SlicedCall call = new SlicedCall(connection, Hub, _clock, new SlicedWork(_clock, 3, 2.0, 0.5));
        scheduler.Add(call);

        for (int frame = 0; frame < 5 && call.Answer == null; frame++)
        {
            scheduler.RunFrame(false, null, new NoRunner());
        }

        StreamCost cost = scheduler.Costs.Of(Hub);
        MethodCallCount method = connection.Calls.Top(10)[0];
        Assert.Equal("ready", call.Answer);
        Assert.Equal(1, cost.Calls);
        Assert.Equal(1.0 + 2.0 + 2.0 + 2.5, cost.MainThreadMs, 6);
        Assert.Equal("find_things", method.Method);
        Assert.Equal(1, method.Calls);
        Assert.Equal(1.0 + 2.0 + 2.0 + 2.5, method.TotalMs, 6);
    }

    [Fact]
    public void AFrameOverBudgetCountsOnceForTheStreamWhetherItsCallOrItsSliceReachedIt()
    {
        LaneScheduler scheduler = new LaneScheduler(new SchedulerSettings(4.0, 0, 1, 0, 16), new StreamStores(), _clock,
            () => new WorldFacts("w1", null, 0));
        SlicedCall call = new SlicedCall(new object(), Hub, _clock, new SlicedWork(_clock, 2, 5.0, 0.0), runMs: 4.5);
        scheduler.Add(call);

        scheduler.RunFrame(false, null, new NoRunner());
        scheduler.RunFrame(false, null, new NoRunner());

        StreamCost cost = scheduler.Costs.Of(Hub);
        Assert.Equal("ready", call.Answer);
        Assert.Equal(2, cost.OverBudgetFrames);
        Assert.Equal(4.5 + 5.0 + 5.0, cost.MainThreadMs, 6);
    }

    /// <summary>Work of steps steps, one per slice at stepMs each, whose reply costs finishMs to build.</summary>
    private sealed class SlicedWork : IDeferredWork
    {
        private readonly FakeClock _clock;
        private readonly int _needed;
        private readonly double _stepMs;
        private readonly double _finishMs;
        private int _steps;

        internal SlicedWork(FakeClock clock, int needed, double stepMs, double finishMs)
        {
            _clock = clock;
            _needed = needed;
            _stepMs = stepMs;
            _finishMs = finishMs;
        }

        public bool Step(double untilMs)
        {
            _clock.Advance(_stepMs);
            return ++_steps >= _needed;
        }

        public string Finish()
        {
            _clock.Advance(_finishMs);
            return "ready";
        }
    }

    /// <summary>
    /// A find that costs runMs to start and goes on with its work, counted for its connection as the dispatcher counts
    /// a call it ran.
    /// </summary>
    private sealed class SlicedCall : QueuedCall
    {
        private readonly object _source;
        private readonly CallContext _context;
        private readonly FakeClock _clock;
        private readonly IDeferredWork _work;
        private readonly double _runMs;

        internal SlicedCall(object source, CallContext context, FakeClock clock, IDeferredWork work, double runMs = 1.0)
            : base(DeadlineAfterMs(60000), new CallProfile("find_things", MethodClass.Read,
                new CallCost(CostClass.Instant, 1)))
        {
            _source = source;
            _context = context;
            _clock = clock;
            _work = work;
            _runMs = runMs;
        }

        internal string? Answer { get; private set; }

        internal override object? Source => _source;

        internal override CallContext Context => _context;

        internal override CallOutcome Run(ICallRunner runner, double queueWaitMs)
        {
            _clock.Advance(_runMs);
            CountOnConnection("find_things", true, _runMs);
            return CallOutcome.Continued(_work, "find_things");
        }

        internal override string TimeoutReply() => "timeout";

        internal override string RefusalReply(string code, string message, object? data = null) => "refused " + code;

        internal override void Deliver(string reply, string? method) => Answer = reply;
    }

    private sealed class NoRunner : ICallRunner
    {
        public CallOutcome RunCall(ProtocolCall call, double queueWaitMs) => throw new InvalidOperationException();
    }

    private sealed class NoTransport : IByteTransport
    {
        public string Kind => "pipe";

        public string Peer => "test";

        public int Read(byte[] buffer, int timeoutMilliseconds) => 0;

        public WriteResult Write(byte[] buffer, int count, int timeoutMilliseconds) => WriteResult.Closed;

        public void Cancel()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoQueue : ICallQueue
    {
        public void Submit(QueuedCall call)
        {
        }

        public void Withdraw(QueuedCall call)
        {
        }

        public void StreamEnded(object source, StreamEnd end)
        {
        }

        public void CursorClosed(string owner, string cursorId)
        {
        }

        public void PinsReleased(string owner, long stream)
        {
        }

        public void SessionEnded(string owner)
        {
        }

        public void Closed(object source)
        {
        }

        public void OwnerHeld(string owner)
        {
        }

        public void OwnerResumed(string owner, object source)
        {
        }
    }
}
