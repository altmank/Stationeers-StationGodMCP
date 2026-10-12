#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Streams on the wire (protocol.md, Streams): a call's stream key, welcome's feature and limits, in-flight limits per
/// stream and per connection, stream_end cancelling a stream's unstarted calls and refusing the stream from then on,
/// and a slow client ending every stream through the same path. The scheduler's side is in FrameSchedulerStreamTests.
/// </summary>
public sealed class StreamWireTests
{
    [Fact]
    public async Task WelcomeListsStreamsAndTheInFlightLimits()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject limits = (JObject)client.Welcome["limits"]!;
        Assert.Contains("streams", client.Welcome["features"]!.Values<string>());
        Assert.Equal(16, (int)limits["max_in_flight"]!);
        Assert.Equal(16, (int)limits["max_in_flight_per_stream"]!);
        Assert.Equal(256, (int)limits["max_in_flight_per_connection"]!);
    }

    [Theory]
    [InlineData(3L)]
    [InlineData(0L)]
    [InlineData(9007199254740992L)]
    public async Task ACallOnAStreamRunsInThatStreamsContext(long stream)
    {
        using PipeRig rig = Loose();
        CallContext seen = CallContext.None;
        rig.Mod.CallHook = (_, _) =>
        {
            seen = CallContext.Current;
            return null;
        };
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = CallOn(client, "a1", stream);

        Assert.True((bool)reply["ok"]!, reply.ToString());
        Assert.Equal(new CallContext(ClientId(client), stream), seen);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("9007199254740993")]
    [InlineData("true")]
    [InlineData("[]")]
    public async Task AnInvalidStreamIsRefusedAndTheConnectionStays(string stream)
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        client.Send($$"""{"type":"call","id":"x","method":"game_clock","params":{},"stream":{{stream}}}""");

        JObject refused = client.Next();

        Assert.Equal("x", (string?)refused["id"]);
        Assert.Equal("invalid_argument", (string?)refused["error"]!["code"]);
        Assert.True((bool)client.Call("after", "game_clock", new JObject())["ok"]!);
        Assert.Equal(1, rig.Mod.Ran);
    }

    [Fact]
    public async Task EndingAStreamCancelsItsUnstartedCallsAndNoOthers()
    {
        using PipeRig rig = new PipeRig(mainThread: false, settings: new ProtocolSettings(32, strictArguments: false));
        using V2Client client = await V2Client.Connect(rig);
        client.Send(CallLine("a", 1));
        client.Send(CallLine("b", 1));
        client.Send(CallLine("c", 2));
        client.Send(CallLine("d", 0));
        client.Send("""{"type":"stream_end","stream":1}""");

        HashSet<string> cancelled = new HashSet<string>();
        for (int index = 0; index < 2; index++)
        {
            JObject reply = client.Next();
            Assert.Equal("cancelled", (string?)reply["error"]!["code"]);
            cancelled.Add((string)reply["id"]!);
        }

        WaitForStreamEnds(rig, 1);
        rig.Mod.RunPending();
        HashSet<string> answered = new HashSet<string>();
        for (int index = 0; index < 2; index++)
        {
            JObject reply = client.Next();
            Assert.True((bool)reply["ok"]!, reply.ToString());
            answered.Add((string)reply["id"]!);
        }

        Assert.Equal(new HashSet<string> { "a", "b" }, cancelled);
        Assert.Equal(new HashSet<string> { "c", "d" }, answered);
        Assert.Equal(new[] { "c", "d" }, Sorted(rig.Mod.RunOrder));
        Assert.Null(client.NextOrNull(200));
    }

    [Fact]
    public async Task ACallOnAnEndedStreamIsRefusedAndEndingItAgainDoesNothing()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        Assert.True((bool)CallOn(client, "a", 5)["ok"]!);
        client.Send("""{"type":"stream_end","stream":5}""");
        client.Send("""{"type":"stream_end","stream":5}""");

        JObject refused = CallOn(client, "b", 5);

        Assert.False((bool)refused["ok"]!);
        Assert.Equal("stream_ended", (string?)refused["error"]!["code"]);
        Assert.Equal(5, (long)refused["error"]!["data"]!["stream"]!);
        Assert.Null(refused["elapsed_ms"]);
        Assert.True((bool)CallOn(client, "c", 6)["ok"]!);
        Assert.Null(client.NextOrNull(200));
    }

    [Fact]
    public async Task AStreamThatWasNeverUsedCanBeEndedAndIsRefusedAfter()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        client.Send("""{"type":"stream_end","stream":12}""");

        JObject refused = CallOn(client, "a", 12);

        Assert.Equal("stream_ended", (string?)refused["error"]!["code"]);
    }

    [Theory]
    [InlineData("""{"type":"stream_end","stream":0}""")]
    [InlineData("""{"type":"stream_end","stream":-2}""")]
    [InlineData("""{"type":"stream_end","stream":"3"}""")]
    [InlineData("""{"type":"stream_end","stream":2.5}""")]
    [InlineData("""{"type":"stream_end","stream":9007199254740993}""")]
    [InlineData("""{"type":"stream_end"}""")]
    public async Task EndingStreamZeroOrNoValidStreamIsRefusedWithANullIdAndTheConnectionStays(string line)
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(line);

        JObject refused = client.Next();

        Assert.Equal("invalid_argument", (string?)refused["error"]!["code"]);
        Assert.Equal(JTokenType.Null, refused["id"]!.Type);
        Assert.True((bool)CallOn(client, "after", 0)["ok"]!);
        Assert.Empty(rig.Mod.StreamEnds);
    }

    [Fact]
    public async Task AStreamEndWithAnUnknownKeyIsAProtocolError()
    {
        using PipeRig rig = Loose();
        using V2Client client = await V2Client.Connect(rig);
        client.Send("""{"type":"stream_end","stream":1,"id":"x"}""");

        Assert.Equal("protocol_error", (string?)client.Next()["error"]!["code"]);
        Assert.Equal("protocol_error", (string?)client.Next()["reason"]);
    }

    [Fact]
    public async Task EachStreamHoldsSixteenCallsInFlightAndTheConnection256()
    {
        // No call may reach its deadline however long a busy machine takes to send them all.
        Scheduling.FakeClock clock = new Scheduling.FakeClock();
        using PipeRig rig = new PipeRig(mainThread: false, settings: new ProtocolSettings(32, strictArguments: false),
            frameClock: clock, deadlineClock: clock);
        using V2Client client = await V2Client.Connect(rig);
        for (int index = 0; index <= 16; index++)
        {
            client.Send(CallLine($"s1-{index}", 1));
        }

        JObject perStream = client.Next();
        Assert.Equal("s1-16", (string?)perStream["id"]);
        Assert.Equal("too_many_in_flight", (string?)perStream["error"]!["code"]);
        Assert.Equal(16, (int)perStream["error"]!["data"]!["limit"]!);
        Assert.Equal("stream", (string?)perStream["error"]!["data"]!["scope"]);

        for (long stream = 2; stream <= 16; stream++)
        {
            for (int index = 0; index < 16; index++)
            {
                client.Send(CallLine($"s{stream}-{index}", stream));
            }
        }

        client.Send(CallLine("over", 17));
        JObject perConnection = client.Next();
        Assert.Equal("over", (string?)perConnection["id"]);
        Assert.Equal(256, (int)perConnection["error"]!["data"]!["limit"]!);
        Assert.Equal("connection", (string?)perConnection["error"]!["data"]!["scope"]);

        rig.Mod.RunPending();
        int answered = 0;
        while (client.NextOrNull(1000) is JObject reply && (bool)reply["ok"]!)
        {
            answered++;
        }

        Assert.Equal(256, answered);
    }

    [Fact]
    public async Task ASlowClientEndsEveryStreamThroughTheStreamPathBeforeItCloses()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, slowClientMilliseconds: 1000, strictArguments: false));
        using V2Client client = await V2Client.Connect(rig);
        string clientId = ClientId(client);
        client.StopReading();
        for (int index = 0; index < 4; index++)
        {
            client.Send(new JObject
            {
                ["type"] = "call", ["id"] = $"s{index}", ["method"] = "game_clock", ["stream"] = index,
                ["params"] = new JObject { ["reply_bytes"] = 1024 * 1024 }
            }.ToString(Formatting.None));
        }

        Stopwatch watch = Stopwatch.StartNew();
        while (rig.Host.Open.Count > 0 && watch.ElapsedMilliseconds < 10000)
        {
            Thread.Sleep(50);
        }

        Assert.Empty(rig.Host.Open);
        (object source, StreamEnd end) = Assert.Single(rig.Mod.StreamEnds);
        Assert.True(end.IsWholeConnection);
        Assert.Equal(clientId, end.Owner);
        Assert.Same(source, Assert.Single(rig.Mod.ClosedSources));
    }

    // ---- the lane scheduler: streams and clients from the calls ----

    [Fact]
    public void ACallOnAnotherStreamIsNotHeldBehindAWriteOfTheSameConnection()
    {
        LaneScheduler scheduler = new LaneScheduler(Unlimited);
        List<string> order = new List<string>();
        object connection = new object();
        scheduler.Add(new StreamCall(connection, "hub", 1, "survey", order, MethodClass.Read, CostClass.World));
        scheduler.Add(new StreamCall(connection, "hub", 1, "write", order, MethodClass.Write));
        scheduler.Add(new StreamCall(connection, "hub", 1, "after-write", order));
        scheduler.Add(new StreamCall(connection, "hub", 2, "other-stream", order));

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "other-stream", "survey" }, order);
    }

    [Fact]
    public void ConnectionsThatGaveTheSameNameShareOneTurnARound()
    {
        LaneScheduler scheduler = new LaneScheduler(Unlimited);
        List<string> order = new List<string>();
        object first = new object();
        object second = new object();
        object agent = new object();
        for (int index = 1; index <= 2; index++)
        {
            scheduler.Add(new StreamCall(first, "dashboard", 0, $"d1-{index}", order));
            scheduler.Add(new StreamCall(second, "dashboard", 0, $"d2-{index}", order));
        }

        scheduler.Add(new StreamCall(agent, "agent", 0, "a-1", order));
        scheduler.Add(new StreamCall(agent, "agent", 0, "a-2", order));

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "d1-1", "a-1", "d2-1", "a-2", "d1-2", "d2-2" }, order);
    }

    [Fact]
    public void EndingAStreamInTheSchedulerDropsItsWaitingCallsAndTellsTheStreamStores()
    {
        StreamStores stores = new StreamStores();
        RecordingStore store = new RecordingStore();
        stores.Register(store);
        LaneScheduler scheduler = new LaneScheduler(Unlimited, stores);
        List<string> order = new List<string>();
        object connection = new object();
        scheduler.Add(new StreamCall(connection, "hub", 1, "s1", order));
        scheduler.Add(new StreamCall(connection, "hub", 2, "s2", order));
        scheduler.EndStreams(connection, StreamEnd.Of("c1", 1));

        scheduler.RunFrame(false, null, new NoRunner());

        Assert.Equal(new[] { "s2" }, order);
        Assert.Equal(1, Assert.Single(store.Ends).Stream);
    }

    [Fact]
    public void AConnectionsEndReachesTheStreamStoresAtTheNextFrame()
    {
        StreamStores stores = new StreamStores();
        RecordingStore store = new RecordingStore();
        stores.Register(store);
        LaneScheduler scheduler = new LaneScheduler(Unlimited, stores);
        object connection = new object();
        scheduler.Add(new StreamCall(connection, "hub", 3, "s3", new List<string>()));
        scheduler.EndStreams(connection, StreamEnd.AllOf("c7"));
        scheduler.Close(connection);
        Assert.Empty(store.Ends);

        scheduler.RunFrame(false, null, new NoRunner());

        StreamEnd end = Assert.Single(store.Ends);
        Assert.True(end.IsWholeConnection);
        Assert.Equal("c7", end.Owner);
    }

    [Fact]
    public void AWorldChangeClearsEveryStreamStoreEntirely()
    {
        StreamStores stores = new StreamStores();
        RecordingStore store = new RecordingStore();
        stores.Register(store);

        stores.WorldChanged();

        Assert.True(Assert.Single(store.Ends).IsWorld);
    }

    [Fact]
    public void AStoreThatThrowsDoesNotKeepTheOthersFromHearing()
    {
        StreamStores stores = new StreamStores();
        RecordingStore store = new RecordingStore();
        stores.Register(new ThrowingStore());
        stores.Register(store);

        stores.End(StreamEnd.Of("c1", 4));

        Assert.Single(store.Ends);
    }

    // No budget, so a slow test machine cannot end a frame early.
    private static readonly SchedulerSettings Unlimited = new SchedulerSettings(0, 1.5, 1, 10, 16);

    private static PipeRig Loose() => new PipeRig(settings: new ProtocolSettings(32, strictArguments: false));

    private static string ClientId(V2Client client) => (string)client.Welcome["client_id"]!;

    private static string CallLine(string id, long stream) =>
        new JObject { ["type"] = "call", ["id"] = id, ["method"] = "game_clock", ["params"] = new JObject(), ["stream"] = stream }
            .ToString(Formatting.None);

    private static JObject CallOn(V2Client client, string id, long stream)
    {
        client.Send(CallLine(id, stream));
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            JObject message = client.Next(Math.Max(1, 5000 - (int)watch.ElapsedMilliseconds));
            if ((string?)message["type"] == "reply" && (string?)message["id"] == id)
            {
                return message;
            }
        }
    }

    private static void WaitForStreamEnds(PipeRig rig, int count)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (rig.Mod.StreamEnds.Count < count && watch.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
        }
    }

    private static string[] Sorted(IEnumerable<string> names)
    {
        List<string> sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        return sorted.ToArray();
    }

    private sealed class RecordingStore : IStreamStore
    {
        internal List<StreamEnd> Ends { get; } = new List<StreamEnd>();

        public void Ended(StreamEnd end) => Ends.Add(end);
    }

    private sealed class ThrowingStore : IStreamStore
    {
        public void Ended(StreamEnd end) => throw new InvalidOperationException("broken store");
    }

    private sealed class StreamCall : QueuedCall
    {
        private readonly object _source;
        private readonly string _client;
        private readonly CallContext _context;
        private readonly List<string> _ran;

        internal StreamCall(object source, string client, long stream, string name, List<string> ran,
            MethodClass methodClass = MethodClass.Read, CostClass cost = CostClass.Instant)
            : base(DeadlineAfterMs(60000), new CallProfile(name, methodClass, new CallCost(cost, 1)))
        {
            _source = source;
            _client = client;
            _context = new CallContext("c1", stream);
            _ran = ran;
            Name = name;
        }

        internal string Name { get; }

        internal override object? Source => _source;

        internal override string? ClientName => _client;

        internal override CallContext Context => _context;

        internal override CallOutcome Run(ICallRunner runner, double queueWaitMs)
        {
            _ran.Add(Name);
            return new CallOutcome(Name, null);
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
