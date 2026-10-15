#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// Per-stream cost counters (protocol.md, Protocol methods, stream_costs): calls and whole main-thread ms add up per
/// (owner, stream), serialising included; a frame over budget counts once per stream; a stream's end clears its counts
/// and a world change does not; the lane scheduler times each call on its clock; a subscription's samples are counted
/// where they are taken (samples, sample_ms) and its update events where their lines are serialised (updates,
/// update_ms), the latter written from connection writer threads; stream_costs answers the caller's own owner only,
/// across a resume; and a counted call or sample allocates nothing once its stream has an entry.
/// </summary>
public sealed class StreamCostsTests
{
    private const string Token = "hub-session-0123456789abcdef";

    [Fact]
    public void CallsAndMillisecondsAddUpPerStream()
    {
        StreamCosts costs = new StreamCosts();
        CallContext run = new CallContext("c1", 2);

        costs.Record(run, 1.5, false, 1);
        costs.Record(run, 2.0, false, 1);
        costs.Record(run, 0.25, false, 2);
        costs.Record(new CallContext("c1", 3), 9.0, false, 2);

        StreamCost cost = costs.Of(run);
        Assert.Equal(2, cost.Stream);
        Assert.Equal(3, cost.Calls);
        Assert.Equal(3.75, cost.MainThreadMs);
        Assert.Equal(0, cost.OverBudgetFrames);
        Assert.Equal(1, costs.Of(new CallContext("c1", 3)).Calls);
    }

    [Fact]
    public void AFrameOverBudgetCountsOncePerStream()
    {
        StreamCosts costs = new StreamCosts();
        CallContext first = new CallContext("c1", 2);
        CallContext second = new CallContext("c1", 3);

        costs.Record(first, 1.0, true, 7);
        costs.Record(first, 1.0, true, 7);
        costs.Record(second, 1.0, true, 7);
        costs.Record(first, 1.0, false, 8);
        costs.Record(first, 1.0, true, 9);

        Assert.Equal(2, costs.Of(first).OverBudgetFrames);
        Assert.Equal(1, costs.Of(second).OverBudgetFrames);
    }

    [Fact]
    public void ACallWithNoContextIsNotCounted()
    {
        StreamCosts costs = new StreamCosts();

        costs.Record(CallContext.None, 4.0, true, 1);

        Assert.Equal(0, costs.Count);
    }

    [Fact]
    public void SubscriptionSamplesAndTheirMillisecondsAddUpPerStream()
    {
        StreamCosts costs = new StreamCosts();
        CallContext run = new CallContext("c1", 2);

        costs.Record(run, 1.5, false, 1);
        costs.RecordSample(run, 0.4);
        costs.RecordSample(run, 0.6);
        costs.RecordSample(new CallContext("c1", 3), 9.0);
        costs.RecordSample(CallContext.None, 1.0);

        StreamCost cost = costs.Of(run);
        Assert.Equal(1, cost.Calls);  // a sample is not a call
        Assert.Equal(2, cost.Samples);
        Assert.Equal(1.0, cost.SampleMs, 9);
        Assert.Equal(1, costs.Of(new CallContext("c1", 3)).Samples);
        Assert.Equal(0, costs.Of(CallContext.None).Samples);
    }

    [Fact]
    public void AStreamWithOnlySamplesIsListedForItsOwner()
    {
        StreamCosts costs = new StreamCosts();
        costs.RecordSample(new CallContext("r1", 4), 0.5);
        List<StreamCost> listed = new List<StreamCost>();

        costs.Of("r1", listed);

        StreamCost only = Assert.Single(listed);
        Assert.Equal(4, only.Stream);
        Assert.Equal(0, only.Calls);
        Assert.Equal(1, only.Samples);
        Assert.Equal(0.5, only.SampleMs, 9);
    }

    [Fact]
    public void AStreamsEndClearsItsSamplesAndAWorldChangeKeepsThem()
    {
        StreamCosts costs = new StreamCosts();
        costs.RecordSample(new CallContext("c1", 2), 0.5);
        costs.RecordSample(new CallContext("c2", 2), 0.5);

        costs.Ended(StreamEnd.World);
        Assert.Equal(1, costs.Of(new CallContext("c1", 2)).Samples);

        costs.Ended(StreamEnd.Of("c1", 2));

        Assert.Equal(0, costs.Of(new CallContext("c1", 2)).Samples);
        Assert.Equal(1, costs.Of(new CallContext("c2", 2)).Samples);
    }

    [Fact]
    public void UpdateEventsAndTheirMillisecondsAddUpPerStream()
    {
        StreamCosts costs = new StreamCosts();
        CallContext run = new CallContext("c1", 2);

        costs.Record(run, 1.5, false, 1);
        costs.RecordUpdate(run, 0.4);
        costs.RecordUpdate(run, 0.6);
        costs.RecordUpdate(new CallContext("c1", 3), 9.0);
        costs.RecordUpdate(CallContext.None, 1.0);

        StreamCost cost = costs.Of(run);
        Assert.Equal(1, cost.Calls);  // an update is not a call
        Assert.Equal(2, cost.Updates);
        Assert.Equal(1.0, cost.UpdateMs, 9);
        Assert.Equal(1, costs.Of(new CallContext("c1", 3)).Updates);
        Assert.Equal(0, costs.Of(CallContext.None).Updates);
    }

    [Fact]
    public void AStreamWithOnlyUpdatesIsListedForItsOwner()
    {
        StreamCosts costs = new StreamCosts();
        costs.RecordUpdate(new CallContext("r1", 4), 0.5);
        List<StreamCost> listed = new List<StreamCost>();

        costs.Of("r1", listed);

        StreamCost only = Assert.Single(listed);
        Assert.Equal(4, only.Stream);
        Assert.Equal(0, only.Calls);
        Assert.Equal(1, only.Updates);
        Assert.Equal(0.5, only.UpdateMs, 9);
    }

    [Fact]
    public void AStreamsEndClearsItsUpdatesAndAWorldChangeKeepsThem()
    {
        StreamCosts costs = new StreamCosts();
        costs.RecordUpdate(new CallContext("c1", 2), 0.5);
        costs.RecordUpdate(new CallContext("c2", 2), 0.5);

        costs.Ended(StreamEnd.World);
        Assert.Equal(1, costs.Of(new CallContext("c1", 2)).Updates);

        costs.Ended(StreamEnd.Of("c1", 2));

        Assert.Equal(0, costs.Of(new CallContext("c1", 2)).Updates);
        Assert.Equal(1, costs.Of(new CallContext("c2", 2)).Updates);
    }

    [Fact]
    public void UpdatesRecordedFromManyWriterThreadsAllCount()
    {
        // The update counts are written from connection writer threads, one lock the only thing between them.
        StreamCosts costs = new StreamCosts();
        CallContext run = new CallContext("c1", 2);

        Parallel.For(0, 8, _ =>
        {
            for (int update = 0; update < 1000; update++)
            {
                costs.RecordUpdate(run, 0.001);
            }
        });

        StreamCost cost = costs.Of(run);
        Assert.Equal(8000, cost.Updates);
        Assert.Equal(8.0, cost.UpdateMs, 9);
    }

    [Fact]
    public void AStreamsEndClearsItsCountsAndAWorldChangeKeepsThem()
    {
        StreamCosts costs = new StreamCosts();
        costs.Record(new CallContext("c1", 2), 1.0, false, 1);
        costs.Record(new CallContext("c1", 3), 1.0, false, 1);
        costs.Record(new CallContext("c2", 2), 1.0, false, 1);

        costs.Ended(StreamEnd.World);
        Assert.Equal(3, costs.Count);
        costs.Ended(StreamEnd.Of("c1", 2));
        Assert.Equal(0, costs.Of(new CallContext("c1", 2)).Calls);
        Assert.Equal(1, costs.Of(new CallContext("c1", 3)).Calls);
        costs.Ended(StreamEnd.AllOf("c1"));

        Assert.Equal(1, costs.Count);
        Assert.Equal(1, costs.Of(new CallContext("c2", 2)).Calls);
    }

    [Fact]
    public void AnOwnersStreamsAreListedInStreamOrderAndNoOtherOwners()
    {
        StreamCosts costs = new StreamCosts();
        costs.Record(new CallContext("r1", 9), 1.0, false, 1);
        costs.Record(new CallContext("c1", 4), 1.0, false, 1);
        costs.Record(new CallContext("r1", 0), 1.0, false, 1);
        costs.Record(new CallContext("r1", 4), 1.0, false, 1);
        List<StreamCost> listed = new List<StreamCost>();

        costs.Of("r1", listed);

        Assert.Equal(new long[] { 0, 4, 9 }, StreamsOf(listed));
    }

    [Fact]
    public void ACountedCallAllocatesNothingOnceItsStreamHasAnEntry()
    {
        StreamCosts costs = new StreamCosts();
        CallContext[] contexts =
        {
            new CallContext("c1", 0), new CallContext("c1", 7), new CallContext("r12", 3), new CallContext("r12", 4)
        };
        foreach (CallContext context in contexts)
        {
            costs.Record(context, 0.1, true, 1);
        }

        long allocated = ThreadAllocations.Of(() =>
        {
            for (int call = 0; call < 10000; call++)
            {
                costs.Record(contexts[call % contexts.Length], 0.1, call % 3 == 0, call / 8);
                costs.RecordSample(contexts[call % contexts.Length], 0.1);
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TheLaneSchedulerTimesTheWholeCallSerialisingIncluded()
    {
        FakeClock clock = new FakeClock();
        LaneScheduler scheduler = new LaneScheduler(SchedulerRig.WithBudget(1.0), new StreamStores(), clock);
        CallContext run = new CallContext("r1", 5);
        TimedCall call = new TimedCall(clock, run, handlerMs: 0.6, serialiseMs: 0.7);
        scheduler.Add(call);

        scheduler.RunFrame(false, null, new NoRunner());

        StreamCost cost = scheduler.Costs.Of(run);
        Assert.Equal(1, cost.Calls);
        Assert.Equal(1.3, cost.MainThreadMs, 9);
        Assert.Equal(1, cost.OverBudgetFrames);
        Assert.False(call.BudgetHitBeforeSerialising);
    }

    [Fact]
    public void TheLaneSchedulerPassesAStreamsEndToTheCounts()
    {
        FakeClock clock = new FakeClock();
        LaneScheduler scheduler = new LaneScheduler(SchedulerRig.WithBudget(1.0), new StreamStores(), clock);
        object source = new object();
        scheduler.Add(new TimedCall(clock, new CallContext("r1", 5), 0.2, 0.1, source));
        scheduler.Add(new TimedCall(clock, new CallContext("r1", 6), 0.2, 0.1, source));
        scheduler.RunFrame(false, null, new NoRunner());

        scheduler.EndStreams(source, StreamEnd.Of("r1", 5));
        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(0, scheduler.Costs.Of(new CallContext("r1", 5)).Calls);
        Assert.Equal(1, scheduler.Costs.Of(new CallContext("r1", 6)).Calls);
    }

    // ---- stream_costs on the wire ----

    [Fact]
    public async Task StreamCostsAnswersTheCallersStreamsWithTheirWholeCallTimeAndFramesPastTheFourMsBudget()
    {
        // The main thread runs on a fake clock that only a call's hold moves: a held call spends exactly its 5 ms, past
        // the 4 ms budget, and an unheld one none, however busy the machine is.
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false),
            frameClock: new FakeClock());
        using V2Client client = await V2Client.Connect(rig);
        using V2Client other = await V2Client.Connect(rig);
        CallOn(client, "a", 3, new JObject { ["hold_ms"] = 5 });
        CallOn(client, "b", 3, new JObject { ["hold_ms"] = 5 });
        CallOn(client, "c", 4, new JObject());
        CallOn(other, "d", 3, new JObject());

        JObject reply = CallOn(client, "costs", 0, new JObject(), "stream_costs");
        JArray streams = (JArray)reply["result"]!["streams"]!;

        Assert.True((bool)reply["ok"]!, reply.ToString());
        Assert.Equal(new long[] { 3, 4 }, Streams(streams));
        Assert.Equal(2, (long)streams[0]["calls"]!);
        Assert.Equal(10.0, (double)streams[0]["main_thread_ms"]!);
        Assert.Equal(0.0, (double)streams[1]["main_thread_ms"]!);
        Assert.Equal(2, (long)streams[0]["over_budget_frames"]!);
        Assert.Equal(0, (long)streams[0]["samples"]!);       // no subscription here
        Assert.Equal(0.0, (double)streams[0]["sample_ms"]!);
        Assert.Equal(0, (long)streams[0]["updates"]!);
        Assert.Equal(0.0, (double)streams[0]["update_ms"]!);
        Assert.Equal(1, (long)streams[1]["calls"]!);
        Assert.Equal(0, (long)streams[1]["over_budget_frames"]!);
        JArray others = (JArray)CallOn(other, "costs", 0, new JObject(), "stream_costs")["result"]!["streams"]!;
        Assert.Equal(new long[] { 3 }, Streams(others));
        Assert.Equal(1, (long)others[0]["calls"]!);
    }

    [Fact]
    public async Task AskedStreamsComeInTheOrderAskedAndAnEndedStreamReadsZero()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        CallOn(client, "a", 3, new JObject());
        CallOn(client, "b", 4, new JObject());
        client.Send("""{"type":"stream_end","stream":3}""");

        JObject asked = CallOn(client, "costs", 0, new JObject { ["streams"] = new JArray(4, 3, 9) }, "stream_costs");
        JObject all = CallOn(client, "all", 0, new JObject(), "stream_costs");

        JArray streams = (JArray)asked["result"]!["streams"]!;
        Assert.Equal(new long[] { 4, 3, 9 }, Streams(streams));
        Assert.Equal(new long[] { 1, 0, 0 }, Calls(streams));
        Assert.Equal(new long[] { 0, 4 }, Streams((JArray)all["result"]!["streams"]!));
    }

    [Theory]
    [InlineData("""{"streams":[]}""")]
    [InlineData("""{"streams":[-1]}""")]
    [InlineData("""{"streams":["3"]}""")]
    [InlineData("""{"streams":3}""")]
    [InlineData("""{"stream":3}""")]
    public async Task BadArgumentsAreRefused(string parameters)
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = CallOn(client, "costs", 0, JObject.Parse(parameters), "stream_costs");

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
    }

    [Fact]
    public async Task TheCountsSurviveAResume()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false),
            resumeWindowMs: 60000, resumeClock: new FakeClock());
        using (V2Client first = await Resume(rig))
        {
            CallOn(first, "a", 3, new JObject());
            CallOn(first, "b", 3, new JObject());
        }

        Wait(() => rig.Holds.Held == 1);
        using V2Client second = await Resume(rig);
        JObject reply = CallOn(second, "costs", 0, new JObject { ["streams"] = new JArray(3) }, "stream_costs");

        Assert.True((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal(new long[] { 2 }, Calls((JArray)reply["result"]!["streams"]!));
    }

    [Fact]
    public void StreamCostsIsAProtocolMethodOfReadClassAnsweredOnTheMainThread()
    {
        Assert.Contains(StreamCostCalls.StreamCostsMethod, ProtocolMethods.Names);
        Assert.True(ProtocolMethods.RunsOnMainThread("stream_costs"));
        CallProfile profile = CallProfiles.Of("stream_costs", new JObject());
        Assert.Equal(MethodClass.Read, profile.Class);
        Assert.Equal(CostClass.Instant, profile.Cost);
    }

    private static PipeRig Loose() => new PipeRig(settings: new ProtocolSettings(32, strictArguments: false));

    // A call on the stream: read {what: "clock"} with the fake main thread's own params, or the method given.
    private static JObject CallOn(V2Client client, string id, long stream, JObject parameters, string? method = null)
    {
        if (method == null)
        {
            method = "read";
            parameters = TestVerbs.Clock(parameters);
        }

        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = method, ["params"] = parameters, ["stream"] = stream
        }.ToString(Formatting.None));
        while (true)
        {
            JObject message = client.Next();
            if ((string?)message["type"] == "reply" && (string?)message["id"] == id)
            {
                return message;
            }
        }
    }

    private static async Task<V2Client> Resume(PipeRig rig)
    {
        V2Client client = await V2Client.Open(rig);
        client.Send(new JObject
        {
            ["type"] = "hello", ["protocol"] = new JArray(2), ["client"] = new JObject { ["name"] = "hub", ["version"] = "1" },
            ["resume"] = Token
        }.ToString(Formatting.None));
        client.Welcome = client.Next();
        return client;
    }

    private static void Wait(Func<bool> condition)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException("the condition never held");
            }

            System.Threading.Thread.Sleep(10);
        }
    }

    private static long[] Streams(JArray streams)
    {
        long[] found = new long[streams.Count];
        for (int index = 0; index < found.Length; index++)
        {
            found[index] = (long)streams[index]["stream"]!;
        }

        return found;
    }

    private static long[] Calls(JArray streams)
    {
        long[] found = new long[streams.Count];
        for (int index = 0; index < found.Length; index++)
        {
            found[index] = (long)streams[index]["calls"]!;
        }

        return found;
    }

    private static long[] StreamsOf(List<StreamCost> costs)
    {
        long[] found = new long[costs.Count];
        for (int index = 0; index < found.Length; index++)
        {
            found[index] = costs[index].Stream;
        }

        return found;
    }

    private sealed class NoRunner : ICallRunner
    {
        public CallOutcome RunCall(ProtocolCall call, double queueWaitMs) => throw new InvalidOperationException();
    }
}

/// <summary>
/// A call that spends handlerMs, notes whether the frame's budget had been reached then (as a reply's budget_hit is
/// decided), and spends serialiseMs more before it answers, all on the given clock.
/// </summary>
internal sealed class TimedCall : QueuedCall
{
    private readonly FakeClock _clock;
    private readonly CallContext _context;
    private readonly double _handlerMs;
    private readonly double _serialiseMs;
    private readonly object? _source;

    internal TimedCall(FakeClock clock, CallContext context, double handlerMs, double serialiseMs, object? source = null)
        : base(long.MaxValue, new CallProfile("read_logic", MethodClass.Read, new CallCost(CostClass.Instant, 1)))
    {
        _clock = clock;
        _context = context;
        _handlerMs = handlerMs;
        _serialiseMs = serialiseMs;
        _source = source;
    }

    internal bool BudgetHitBeforeSerialising { get; private set; }

    internal override object? Source => _source;

    internal override CallContext Context => _context;

    internal override CallOutcome Run(ICallRunner runner, double queueWaitMs)
    {
        _clock.Advance(_handlerMs);
        BudgetHitBeforeSerialising = CurrentFrame.BudgetReached(_clock.NowMs);
        _clock.Advance(_serialiseMs);
        return new CallOutcome("{}", "read_logic");
    }

    internal override string TimeoutReply() => "{}";

    internal override string RefusalReply(string code, string message, object? data = null) => "{}";

    internal override void Deliver(ReplyLine line, string? method)
    {
        string reply = line.Render();
    }
}
