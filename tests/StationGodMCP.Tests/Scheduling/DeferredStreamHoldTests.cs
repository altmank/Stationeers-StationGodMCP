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
/// protocol.md, Order on one stream: a deferred call (a find page built over frames, a read sample run) holds its stream
/// until it answers, so a write sent after it on that stream starts only once it has answered and the find sees the
/// world from before the write; other streams go on meanwhile. Every way a deferred call can end lets go of its stream.
/// </summary>
public sealed class DeferredStreamHoldTests
{
    private const double BudgetMs = 4.0;
    private static readonly CallContext Hub = new CallContext("c1", 2);
    private static readonly CallContext Other = new CallContext("c1", 3);

    private readonly FakeClock _clock = new FakeClock();
    private readonly object _connection = new object();
    private readonly List<string> _log = new List<string>();
    private readonly int[] _world = { 1 };
    private string _worldId = "w1";

    [Fact]
    public void AFindThenAWriteOnOneStreamFindsTheWorldBeforeTheWriteWhileAnotherStreamGoesOn()
    {
        LaneScheduler scheduler = Scheduler();
        LoggedCall find = Find(steps: 3);
        LoggedCall write = Write(Hub);
        LoggedCall read = Read(Other);
        scheduler.Add(find);
        scheduler.Add(write);
        scheduler.Add(read);

        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Null(find.Answer);
        Assert.Null(write.Answer);
        Assert.Equal("read", read.Answer);
        for (int frame = 0; frame < 4 && write.Answer == null; frame++)
        {
            scheduler.RunFrame(false, null, new NoRunner());
        }

        Assert.Equal("saw 1", find.Answer);
        Assert.Equal("wrote", write.Answer);
        Assert.Equal(2, _world[0]);
        Assert.Equal(new[] { "find started", "read", "find answered", "write" }, _log);
        Assert.Equal(0, scheduler.Holding);
    }

    [Fact]
    public void AReadSentAfterAFindWaitsForItToo()
    {
        LaneScheduler scheduler = Scheduler();
        LoggedCall find = Find(steps: 3);
        LoggedCall read = Read(Hub);
        scheduler.Add(find);
        scheduler.Add(read);

        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Null(read.Answer);
        RunUntilAnswered(scheduler, read);

        Assert.Equal(new[] { "find started", "find answered", "read" }, _log);
    }

    [Fact]
    public void AReadSampleRunHoldsItsStreamUntilItEnds()
    {
        LaneScheduler scheduler = Scheduler();
        LoggedCall run = ReadSample();
        LoggedCall write = Write(Hub);
        scheduler.Add(run);
        scheduler.Add(write);
        scheduler.RunFrame(false, null, new NoRunner());
        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Null(write.Answer);

        run.Watch!.Ready = true;
        RunUntilAnswered(scheduler, write);

        Assert.Equal("samples", run.Answer);
        Assert.Equal("wrote", write.Answer);
    }

    // ---- every ending lets go of the stream ----

    [Fact]
    public void ACancelLetsGoOfTheStream() =>
        EndingFreesTheStream(Find(steps: 1000), (_, call) => call.RequestCancel(), "refused cancelled");

    [Fact]
    public void ThePassedDeadlineLetsGoOfTheStream() =>
        EndingFreesTheStream(Find(steps: 1000, deadlineMs: 1000),
            (_, call) => _clock.Advance(call.DeadlineAtMs), "refused game_timeout");

    [Fact]
    public void AnotherWorldLetsGoOfTheStream() =>
        EndingFreesTheStream(Find(steps: 1000), (_, _) => _worldId = "w2", "refused world_changed");

    [Fact]
    public void TheSessionEndLetsGoOfTheStream() =>
        EndingFreesTheStream(Find(steps: 1000), (scheduler, _) => scheduler.EndSession("c1"), "refused cancelled");

    [Fact]
    public void AShutdownAnswerLetsGoOfTheStream() =>
        EndingFreesTheStream(Find(steps: 1000), (_, call) =>
        {
            Assert.True(call.IsDeferred && call.State.TryFinish());
            call.Deliver("refused shutting_down", null);
        }, "refused shutting_down");

    [Fact]
    public void WorkThatThrowsLetsGoOfTheStream()
    {
        LoggedCall find = Find(steps: 1000);
        EndingFreesTheStream(find, (_, _) => find.Work!.Throws = true, "refused internal_error");
    }

    [Fact]
    public void AReadSampleRunsEndLetsGoOfTheStream() =>
        EndingFreesTheStream(ReadSample(), (_, call) => call.Watch!.Ready = true, "samples");

    [Fact]
    public void TheStreamsEndLetsGoOfItAndAnswersTheRunCancelled()
    {
        LaneScheduler scheduler = Scheduler();
        LoggedCall run = ReadSample();
        scheduler.Add(run);
        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Equal(1, scheduler.Holding);

        scheduler.EndStreams(_connection, StreamEnd.Of("c1", 2));
        LoggedCall write = Write(Hub);
        scheduler.Add(write);
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal("wrote", write.Answer);
        Assert.Equal("refused cancelled", run.Answer);
        Assert.Equal(0, scheduler.Holding);
    }

    [Fact]
    public void AWholeConnectionsEndLetsGoOfEveryStreamItHolds()
    {
        LaneScheduler scheduler = Scheduler();
        scheduler.Add(ReadSample());
        scheduler.Add(Find(steps: 1000));
        scheduler.Add(Write(Other));
        scheduler.RunFrame(false, null, new NoRunner());

        scheduler.EndStreams(_connection, StreamEnd.AllOf("c1"));
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(0, scheduler.Holding);
        Assert.Equal(0, scheduler.Deferred.Count);
    }

    [Fact]
    public void TheConnectionsCloseLetsGoOfItsStreams()
    {
        LaneScheduler scheduler = Scheduler();
        object other = new object();
        scheduler.Add(ReadSample());
        scheduler.Add(Find(steps: 1000, context: Other));
        LoggedCall stays = ReadSample(other);
        scheduler.Add(stays);
        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Equal(3, scheduler.Holding);

        scheduler.Close(_connection);
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(1, scheduler.Holding);
        Assert.Equal(1, scheduler.Deferred.Count);
        stays.Watch!.Ready = true;
        RunUntilAnswered(scheduler, stays);
        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Equal("samples", stays.Answer);
        Assert.Equal(0, scheduler.Holding);
    }

    private void EndingFreesTheStream(LoggedCall deferred, Action<LaneScheduler, LoggedCall> end, string answer)
    {
        LaneScheduler scheduler = Scheduler();
        scheduler.Add(deferred);
        LoggedCall write = Write(Hub, 600000);
        scheduler.Add(write);
        scheduler.RunFrame(false, null, new NoRunner());
        scheduler.RunFrame(false, null, new NoRunner());
        Assert.Null(write.Answer);
        Assert.Equal(1, scheduler.Holding);

        end(scheduler, deferred);
        RunUntilAnswered(scheduler, write);

        Assert.Equal(answer, deferred.Answer);
        Assert.Equal("wrote", write.Answer);
        Assert.Equal(0, scheduler.Holding);
    }

    private static void RunUntilAnswered(LaneScheduler scheduler, LoggedCall call)
    {
        for (int frame = 0; frame < 5 && call.Answer == null; frame++)
        {
            scheduler.RunFrame(false, null, new NoRunner());
        }
    }

    private LaneScheduler Scheduler() =>
        new LaneScheduler(new SchedulerSettings(BudgetMs, 0, 1, 10, 16), new StreamStores(), _clock,
            () => new WorldFacts(_worldId, null, 0));

    private LoggedCall Find(int steps, int deadlineMs = 60000, CallContext? context = null) =>
        new LoggedCall(_connection, context ?? Hub, "find", Read("find.thing"), deadlineMs,
            run: call =>
            {
                _log.Add("find started");
                call.Work = new SteppedWork(_clock, steps, () =>
                {
                    _log.Add("find answered");
                    return "saw " + _world[0];
                });
                return call.Work.Step(CurrentFrame.EndMs)
                    ? new CallOutcome(call.Work.Finish(), "find.thing")
                    : CallOutcome.Continued(call.Work, "find.thing");
            });

    // A read sample run: its call waits in the slice lane until the run ends (Watch.Ready), then answers its samples.
    private LoggedCall ReadSample(object? source = null) =>
        new LoggedCall(source ?? _connection, Hub, "read sample", Read("read.sample"), 60000,
            run: call =>
            {
                call.Watch = new WatchedRun();
                return CallOutcome.Continued(call.Watch, "read.sample");
            });

    private LoggedCall Write(CallContext context, int deadlineMs = 60000) =>
        new LoggedCall(_connection, context, "write",
            new CallProfile("write_logic", MethodClass.Write, new CallCost(CostClass.Instant, 1)), deadlineMs,
            run: _ =>
            {
                _world[0]++;
                _log.Add("write");
                return new CallOutcome("wrote", "write_logic");
            });

    private LoggedCall Read(CallContext context) =>
        new LoggedCall(_connection, context, "read", Read("read_logic"), 60000,
            run: _ =>
            {
                _log.Add("read");
                return new CallOutcome("read", "read_logic");
            });

    private static CallProfile Read(string method) =>
        new CallProfile(method, MethodClass.Read, new CallCost(CostClass.Instant, 1));

    /// <summary>Work that takes one 1 ms step a slice and answers after its steps with what answer reads then.</summary>
    private sealed class SteppedWork : IDeferredWork
    {
        private readonly FakeClock _clock;
        private readonly int _needed;
        private readonly Func<string> _answer;
        private int _steps;

        internal SteppedWork(FakeClock clock, int needed, Func<string> answer)
        {
            _clock = clock;
            _needed = needed;
            _answer = answer;
        }

        internal bool Throws { get; set; }

        public bool Step(double untilMs)
        {
            if (Throws)
            {
                throw new InvalidOperationException("broken work");
            }

            _clock.Advance(1.0);
            return ++_steps >= _needed;
        }

        public ReplyLine Finish() => _answer();
    }

    /// <summary>A sample run's call: ready once the run ends.</summary>
    private sealed class WatchedRun : IDeferredWork
    {
        internal bool Ready { get; set; }

        public bool Step(double untilMs) => Ready;

        public ReplyLine Finish() => "samples";
    }

    /// <summary>A call that runs what the test gives it and keeps its answer.</summary>
    private sealed class LoggedCall : QueuedCall
    {
        private readonly object _source;
        private readonly CallContext _context;
        private readonly Func<LoggedCall, CallOutcome> _run;

        internal LoggedCall(object source, CallContext context, string name, CallProfile profile, int deadlineMs,
            Func<LoggedCall, CallOutcome> run)
            : base(DeadlineAfterMs(deadlineMs), profile)
        {
            _source = source;
            _context = context;
            _run = run;
            Name = name;
        }

        internal string Name { get; }

        internal string? Answer { get; private set; }

        internal SteppedWork? Work { get; set; }

        internal WatchedRun? Watch { get; set; }

        internal override object? Source => _source;

        internal override CallContext Context => _context;

        internal override CallOutcome Run(ICallRunner runner, double queueWaitMs) => _run(this);

        internal override string TimeoutReply() => "timeout";

        internal override string RefusalReply(string code, string message, object? data = null) => "refused " + code;

        internal override void Deliver(ReplyLine line, string? method) => Answer = line.Render();
    }

    private sealed class NoRunner : ICallRunner
    {
        public CallOutcome RunCall(ProtocolCall call, double queueWaitMs) => throw new InvalidOperationException();
    }
}
