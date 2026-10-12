#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Tests.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// The call context (ARCH-4-04): who a call on the main thread runs for, set around each call's run by the lane
/// scheduler and around each later step of what a call started, and None outside them.
/// </summary>
public sealed class CallContextTests
{
    [Fact]
    public void OutsideACallTheContextIsNone()
    {
        Assert.True(CallContext.Current.IsNone);
        Assert.Equal(CallContext.None, CallContext.Current);
    }

    [Fact]
    public void ContextsAreEqualByConnectionAndStream()
    {
        Assert.Equal(new CallContext("c4", 2), new CallContext("c4", 2));
        Assert.Equal(new CallContext("c4", 2).GetHashCode(), new CallContext("c4", 2).GetHashCode());
        Assert.NotEqual(new CallContext("c4", 2), new CallContext("c4", 3));
        Assert.NotEqual(new CallContext("c4", 2), new CallContext("c5", 2));
        Assert.False(new CallContext("c4", 0).IsNone);
    }

    [Fact]
    public void EnteringAContextSetsItUntilTheScopeEndsAndRestoresTheOneBefore()
    {
        CallContext outer = new CallContext("c1", 0);
        CallContext inner = new CallContext("c2", 7);

        using (CallContext.Enter(outer))
        {
            Assert.Equal(outer, CallContext.Current);
            using (CallContext.Enter(inner))
            {
                Assert.Equal(inner, CallContext.Current);
            }

            Assert.Equal(outer, CallContext.Current);
        }

        Assert.True(CallContext.Current.IsNone);
    }

    [Fact]
    public void AScopeLeftByAnExceptionStillRestoresTheContext()
    {
        Assert.Throws<InvalidOperationException>(FailInsideAContext);

        Assert.True(CallContext.Current.IsNone);
    }

    private static void FailInsideAContext()
    {
        using (CallContext.Enter(new CallContext("c1", 3)))
        {
            throw new InvalidOperationException("handler failed");
        }
    }

    [Fact]
    public void ACallSeesItsContextWhileItRunsAndNoneAfter()
    {
        LaneScheduler scheduler = new LaneScheduler(new SchedulerSettings(0, 1.5, 1, 10, 16));
        object connection = new object();
        ContextCall call = new ContextCall(connection, new CallContext("c9", 0));
        scheduler.Add(call);

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new CallContext("c9", 0), call.Seen);
        Assert.True(CallContext.Current.IsNone);
    }

    [Fact]
    public void CallsFromDifferentConnectionsAndStreamsEachSeeTheirOwn()
    {
        LaneScheduler scheduler = new LaneScheduler(new SchedulerSettings(0, 1.5, 1, 10, 16));
        object first = new object();
        object second = new object();
        ContextCall[] calls =
        {
            new ContextCall(first, new CallContext("c1", 0)),
            new ContextCall(first, new CallContext("c1", 5)),
            new ContextCall(second, new CallContext("c2", 0)),
            new ContextCall(second, new CallContext("c2", 5)),
        };
        foreach (ContextCall call in calls)
        {
            scheduler.Add(call);
        }

        scheduler.RunFrame(false, null, new NoRunner());

        foreach (ContextCall call in calls)
        {
            Assert.Equal(call.Context, call.Seen);
        }
    }

    // HeldTickJobs needs the game (GameManager, the game tick) and cannot run here. It keeps the context a job was
    // started in (CallContext.Current inside the starting call) and enters it around each later step, as below.
    [Fact]
    public void AContextCapturedWhenSomethingStartsIsTheOneItsLaterStepsRunIn()
    {
        CallContext captured;
        using (CallContext.Enter(new CallContext("c3", 4)))
        {
            captured = CallContext.Current;
        }

        CallContext stepSaw;
        using (CallContext.Enter(captured))
        {
            stepSaw = CallContext.Current;
        }

        Assert.Equal(new CallContext("c3", 4), stepSaw);
        Assert.True(CallContext.Current.IsNone);
    }

    [Fact]
    public void EachSampleLogicSampleRunsInTheContextOfTheCallThatStartedIt()
    {
        ContextReader reader = new ContextReader();
        LogicSampler<BatchItemView> sampler = new LogicSampler<BatchItemView>(reader, LogicResultComparer.Instance);
        sampler.Start(new ConnectionId("c1"), Arguments(), 0.0, stream: 2);
        sampler.Start(new ConnectionId("c2"), Arguments(), 0.0, stream: 0);

        sampler.Poll(new RealTimeTick(1, 0.0, DateTimeOffset.UnixEpoch), CountBudget.Unlimited, new NoEvents());

        Assert.Equal(new[] { new CallContext("c1", 2), new CallContext("c2", 0) }, reader.Seen);
        Assert.True(CallContext.Current.IsNone);
    }

    private static SampleLogicArguments Arguments()
    {
        JObject parameters = new JObject
        {
            ["targets"] = new JArray(new JObject { ["reference_id"] = "100", ["logic_type"] = "On" }),
            ["duration_seconds"] = 1.0,
            ["interval_seconds"] = 0.5,
        };
        return Assert.IsType<SampleLogicParse.Parsed>(SampleLogicArguments.Of(parameters)).Arguments;
    }

    private sealed class ContextReader : ILogicSampleReader<BatchItemView>
    {
        internal List<CallContext> Seen { get; } = new List<CallContext>();

        public LogicSampleRead Read(SampleLogicArguments arguments, List<BatchItemView> into)
        {
            Seen.Add(CallContext.Current);
            return LogicSampleRead.Read("world");
        }
    }

    private sealed class NoEvents : ISampleLogicEvents<BatchItemView>
    {
        public void Finished(SampleLogicRun<BatchItemView> run, SampleLogicOutcome<BatchItemView> outcome)
        {
        }
    }

    private sealed class ContextCall : QueuedCall
    {
        private readonly object _source;
        private readonly CallContext _context;

        internal ContextCall(object source, CallContext context)
            : base(DeadlineAfter(60000), new CallProfile("read_logic", MethodClass.Read, new CallCost(CostClass.Instant, 1)))
        {
            _source = source;
            _context = context;
        }

        internal CallContext Seen { get; private set; }

        internal override object? Source => _source;

        internal override CallContext Context => _context;

        internal override CallOutcome Run(ICallRunner runner, double queueWaitMs)
        {
            Seen = CallContext.Current;
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
