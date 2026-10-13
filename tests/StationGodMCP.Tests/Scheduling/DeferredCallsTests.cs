#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// Calls deferred over frames with work of their own (Protocol/DeferredCalls, a walk's build): the work runs one slice
/// per frame after the frame's calls in what is left of the budget, under its call's context, and answers the call
/// when ready; a frame with no budget left runs no slice until the work has waited HeavyMaxWaitFrames frames; a
/// cancel, the call's stream ending, its connection closing, its deadline passing or another world loading stop it
/// and free its work.
/// </summary>
public sealed class DeferredCallsTests
{
    private const double BudgetMs = 4.0;
    private static readonly CallContext Hub = new CallContext("c1", 2);

    // The frame's other calls come on another stream: the deferred call holds its own until it answers.
    private static readonly CallContext Busy = new CallContext("c1", 5);

    private readonly FakeClock _clock = new FakeClock();
    private readonly object _connection = new object();
    private string _world = "w1";

    [Fact]
    public void TheWorkRunsAfterTheFrameCallsInWhatIsLeftAndAnswersWhenReady()
    {
        LaneScheduler scheduler = Scheduler();
        FakeWork work = new FakeWork(_clock, 0.5, 20);
        DeferredCall call = new DeferredCall(_connection, Hub, work);
        scheduler.Add(call);

        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Null(call.Answer);
        Assert.Equal(1, scheduler.Deferred.Count);
        int frames = 1;
        while (call.Answer == null)
        {
            double startMs = _clock.NowMs;
            scheduler.Add(new CostCall(_connection, Busy, _clock, 1.5));
            scheduler.RunFrame(false, null, new NoRunner());
            Assert.True(_clock.NowMs - startMs <= BudgetMs, $"frame {frames} spent {_clock.NowMs - startMs} ms");
            Assert.InRange(work.LastSliceSteps, 1, 5);
            frames++;
        }

        Assert.Equal("ready", call.Answer);
        Assert.Equal("find.thing", call.AnsweredMethod);
        Assert.Equal(Hub, work.SteppedIn);
        Assert.Equal(0, scheduler.Deferred.Count);
        Assert.Equal(4, frames);
    }

    [Fact]
    public void WithNoBudgetLeftTheWorkWaitsThenTakesOneStep()
    {
        LaneScheduler scheduler = Scheduler(heavyMaxWaitFrames: 2);
        FakeWork work = new FakeWork(_clock, 0.5, 1000);
        scheduler.Add(new DeferredCall(_connection, Hub, work));
        scheduler.RunFrame(false, null, new NoRunner());
        int before = work.Steps;

        List<int> steps = new List<int>();
        for (int frame = 0; frame < 6; frame++)
        {
            scheduler.Add(new CostCall(_connection, Busy, _clock, 5.0));
            int taken = work.Steps;
            scheduler.RunFrame(false, null, new NoRunner());
            steps.Add(work.Steps - taken);
        }

        Assert.True(before > 0);
        Assert.Equal(new[] { 0, 1, 0, 0, 1, 0 }, steps);
    }

    [Fact]
    public void ACancelAfterTheStartStopsTheWork()
    {
        LaneScheduler scheduler = Scheduler();
        FakeWork work = new FakeWork(_clock, 0.5, 1000);
        DeferredCall call = new DeferredCall(_connection, Hub, work);
        scheduler.Add(call);
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.False(call.Drop("cancelled"));
        call.RequestCancel();
        int steps = work.Steps;
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal("refused cancelled", call.Answer);
        Assert.Equal(steps, work.Steps);
        Assert.Equal(0, scheduler.Deferred.Count);
    }

    [Fact]
    public void TheEndOfItsStreamStopsTheWorkAndOtherStreamsGoOn()
    {
        LaneScheduler scheduler = Scheduler();
        DeferredCall ended = new DeferredCall(_connection, Hub, new FakeWork(_clock, 0.5, 1000));
        DeferredCall other = new DeferredCall(_connection, new CallContext("c1", 3), new FakeWork(_clock, 0.5, 1000));
        scheduler.Add(ended);
        scheduler.Add(other);
        scheduler.RunFrame(false, null, new NoRunner());

        scheduler.EndStreams(_connection, StreamEnd.Of("c1", 2));
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal("refused cancelled", ended.Answer);
        Assert.Null(other.Answer);
        Assert.Equal(1, scheduler.Deferred.Count);
    }

    [Fact]
    public void AClosedConnectionDropsItsWork()
    {
        LaneScheduler scheduler = Scheduler();
        DeferredCall call = new DeferredCall(_connection, Hub, new FakeWork(_clock, 0.5, 1000));
        scheduler.Add(call);
        scheduler.RunFrame(false, null, new NoRunner());

        scheduler.Close(_connection);
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(0, scheduler.Deferred.Count);
        Assert.Null(call.Answer);
    }

    [Fact]
    public void ThePassedDeadlineAndAnotherWorldStopTheWork()
    {
        LaneScheduler scheduler = Scheduler();
        DeferredCall late = new DeferredCall(_connection, Hub, new FakeWork(_clock, 0.5, 1000));
        scheduler.Add(late);
        scheduler.RunFrame(false, null, new NoRunner());
        _clock.Advance(late.DeadlineAtMs);
        scheduler.RunFrame(false, null, new NoRunner());

        DeferredCall moved = new DeferredCall(_connection, Hub, new FakeWork(_clock, 0.5, 1000), 600000);
        scheduler.Add(moved);
        scheduler.RunFrame(false, null, new NoRunner());
        _world = "w2";
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal("refused game_timeout", late.Answer);
        Assert.Equal("refused world_changed", moved.Answer);
        Assert.Equal(0, scheduler.Deferred.Count);
    }

    [Fact]
    public void WorkThatFitsItsFirstSliceAnswersInThatFrame()
    {
        LaneScheduler scheduler = Scheduler();
        DeferredCall call = new DeferredCall(_connection, Hub, new FakeWork(_clock, 0.5, 2));
        scheduler.Add(call);

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal("ready", call.Answer);
    }

    [Fact]
    public void ItsOwnersSessionEndStopsTheWork()
    {
        LaneScheduler scheduler = Scheduler();
        DeferredCall call = new DeferredCall(_connection, Hub, new FakeWork(_clock, 0.5, 1000));
        scheduler.Add(call);
        scheduler.RunFrame(false, null, new NoRunner());

        scheduler.EndSession("c1");
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal("refused cancelled", call.Answer);
        Assert.Equal(0, scheduler.Deferred.Count);
    }

    [Fact]
    public void ACallAShutdownAnsweredIsDroppedUnstepped()
    {
        LaneScheduler scheduler = Scheduler();
        FakeWork work = new FakeWork(_clock, 0.5, 1000);
        DeferredCall call = new DeferredCall(_connection, Hub, work);
        scheduler.Add(call);
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.True(call.IsDeferred);
        Assert.True(call.State.TryFinish());
        int steps = work.Steps;
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(steps, work.Steps);
        Assert.Equal(0, scheduler.Deferred.Count);
    }

    [Fact]
    public void WorkThatThrowsIsAnsweredAndTheFrameGoesOn()
    {
        LaneScheduler scheduler = Scheduler();
        FakeWork work = new FakeWork(_clock, 0.5, 1000);
        DeferredCall call = new DeferredCall(_connection, Hub, work);
        scheduler.Add(call);
        scheduler.RunFrame(false, null, new NoRunner());

        work.Throws = true;
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal("refused internal_error", call.Answer);
        Assert.Equal(0, scheduler.Deferred.Count);
    }

    private LaneScheduler Scheduler(int heavyMaxWaitFrames = 10) =>
        new LaneScheduler(new SchedulerSettings(BudgetMs, 0, 1, heavyMaxWaitFrames, 16), new StreamStores(), _clock,
            () => new WorldFacts(_world, null, 0));

    /// <summary>Work that costs the clock stepMs a step and is ready after steps steps.</summary>
    private sealed class FakeWork : IDeferredWork
    {
        private readonly FakeClock _clock;
        private readonly double _stepMs;
        private readonly int _needed;

        internal FakeWork(FakeClock clock, double stepMs, int needed)
        {
            _clock = clock;
            _stepMs = stepMs;
            _needed = needed;
        }

        internal int Steps { get; private set; }

        internal int LastSliceSteps { get; private set; }

        internal CallContext SteppedIn { get; private set; }

        internal bool Throws { get; set; }

        public bool Step(double untilMs)
        {
            if (Throws)
            {
                throw new InvalidOperationException("broken work");
            }

            SteppedIn = CallContext.Current;
            int before = Steps;
            do
            {
                _clock.Advance(_stepMs);
                Steps++;
            }
            while (Steps < _needed && _clock.NowMs < untilMs);

            LastSliceSteps = Steps - before;
            return Steps >= _needed;
        }

        public string Finish() => "ready";
    }

    /// <summary>A call whose handler starts work that goes on over frames.</summary>
    private sealed class DeferredCall : QueuedCall
    {
        private readonly object _source;
        private readonly CallContext _context;
        private readonly IDeferredWork _work;

        internal DeferredCall(object source, CallContext context, IDeferredWork work, int deadlineMs = 60000)
            : base(DeadlineAfterMs(deadlineMs), new CallProfile("find.thing", MethodClass.Read,
                new CallCost(CostClass.World, 1)))
        {
            _source = source;
            _context = context;
            _work = work;
        }

        internal string? Answer { get; private set; }

        internal string? AnsweredMethod { get; private set; }

        internal override object? Source => _source;

        internal override CallContext Context => _context;

        internal override CallOutcome Run(ICallRunner runner, double queueWaitMs)
        {
            CallOutcome first = CallOutcome.Continued(_work, "find.thing");
            return _work.Step(CurrentFrame.EndMs) ? new CallOutcome(_work.Finish(), "find.thing") : first;
        }

        internal override string TimeoutReply() => "timeout";

        internal override string RefusalReply(string code, string message, object? data = null) => "refused " + code;

        internal override void Deliver(string reply, string? method)
        {
            Answer = reply;
            AnsweredMethod = method;
        }
    }

    /// <summary>A light call that costs the clock costMs.</summary>
    private sealed class CostCall : QueuedCall
    {
        private readonly object _source;
        private readonly CallContext _context;
        private readonly FakeClock _clock;
        private readonly double _costMs;

        internal CostCall(object source, CallContext context, FakeClock clock, double costMs)
            : base(DeadlineAfterMs(60000), new CallProfile("game_clock", MethodClass.Read,
                new CallCost(CostClass.Instant, 1)))
        {
            _source = source;
            _context = context;
            _clock = clock;
            _costMs = costMs;
        }

        internal override object? Source => _source;

        internal override CallContext Context => _context;

        internal override CallOutcome Run(ICallRunner runner, double queueWaitMs)
        {
            _clock.Advance(_costMs);
            return new CallOutcome("ok", null);
        }

        internal override string TimeoutReply() => "timeout";

        internal override string RefusalReply(string code, string message, object? data = null) => "refused " + code;

        internal override void Deliver(string reply, string? method)
        {
        }
    }

    private sealed class NoRunner : ICallRunner
    {
        public CallOutcome RunCall(ProtocolCall call, double queueWaitMs) => throw new InvalidOperationException();
    }
}
