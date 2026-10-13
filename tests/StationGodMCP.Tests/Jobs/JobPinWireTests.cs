#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Jobs;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Tests.Scheduling;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Jobs;

/// <summary>
/// Job pins on the wire (protocol.md, Job pins): a job is pinned to the (owner, stream) of the call that started it;
/// release (no reply, through the scheduler's inbox) drops a stream's pins, an ended stream's too; stream_end keeps
/// them; an owner's end (a tokenless connection's close, session_end, a resume window that passed) releases them; a
/// resume token's pins survive a break and its resume. The "start" method stands in for a building tool: it pins the
/// job id it is given to the call's context, as HeldTickJobs.Start does.
/// </summary>
public sealed class JobPinWireTests
{
    private const string Token = "hub-session-0123456789abcdef";
    private const int WindowMs = 60000;

    private readonly KeptJobs _kept = new KeptJobs();
    private readonly FakeClock _clock = new FakeClock();

    [Fact]
    public async Task WelcomeListsTheReleaseFeature()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        Assert.Contains("release", client.Welcome["features"]!.Values<string>());
    }

    [Fact]
    public async Task AJobIsPinnedToTheStreamOfTheCallThatStartedIt()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        Start(client, "place-1", stream: 4);
        Start(client, "place-2", stream: 0);

        Assert.True(Pinned(rig, "place-1"));
        Assert.True(Pinned(rig, "place-2"));
    }

    [Fact]
    public async Task StreamEndKeepsThePinsAndAReleaseOfTheEndedStreamDropsThemWithNoReply()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        Start(client, "place-1", stream: 4);
        Start(client, "place-2", stream: 5);

        client.Send("""{"type":"stream_end","stream":4}""");
        Sync(client, "after-end");
        Assert.True(Pinned(rig, "place-1"));

        client.Send("""{"type":"release","stream":4}""");
        Sync(client, "after-release");

        Assert.False(Pinned(rig, "place-1"));
        Assert.True(Pinned(rig, "place-2"));
        Assert.True(rig.Mod.PinReleases.TryDequeue(out (string Owner, long Stream) released));
        Assert.Equal(((string)client.Welcome["client_id"]!, 4L), released);
        Assert.Null(client.NextOrNull(200));
    }

    [Fact]
    public async Task StreamZeroCanBeReleased()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        Start(client, "place-1", stream: 0);

        client.Send("""{"type":"release","stream":0}""");
        Sync(client, "after-release");

        Assert.False(Pinned(rig, "place-1"));
    }

    [Fact]
    public async Task AReleaseFromAnotherConnectionLeavesThePinsAlone()
    {
        using PipeRig rig = Rig();
        using V2Client owner = await V2Client.Connect(rig);
        using V2Client other = await V2Client.Connect(rig);
        Start(owner, "place-1", stream: 4);

        other.Send("""{"type":"release","stream":4}""");
        Sync(other, "after-release");

        Assert.True(Pinned(rig, "place-1"));
    }

    [Theory]
    [InlineData("""{"type":"release","stream":-1}""")]
    [InlineData("""{"type":"release","stream":"4"}""")]
    [InlineData("""{"type":"release","stream":2.5}""")]
    [InlineData("""{"type":"release","stream":9007199254740993}""")]
    [InlineData("""{"type":"release"}""")]
    public async Task AReleaseWithoutAValidStreamIsRefusedWithANullIdAndTheConnectionStays(string line)
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send(line);

        JObject refused = client.Next();

        Assert.Equal("invalid_argument", (string?)refused["error"]!["code"]);
        Assert.Equal(JTokenType.Null, refused["id"]!.Type);
        Assert.True((bool)Sync(client, "after")["ok"]!);
        Assert.Empty(rig.Mod.PinReleases);
    }

    [Fact]
    public async Task AReleaseWithAnUnknownKeyIsAProtocolError()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send("""{"type":"release","stream":1,"id":"x"}""");

        Assert.Equal("protocol_error", (string?)client.Next()["error"]!["code"]);
        Assert.Equal("protocol_error", (string?)client.Next()["reason"]);
    }

    [Fact]
    public async Task ATokenlessConnectionsCloseReleasesItsPins()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await V2Client.Connect(rig))
        {
            Start(client, "place-1", stream: 4);
            Assert.True(Pinned(rig, "place-1"));
        }

        WaitUntil(() => !Pinned(rig, "place-1"));
    }

    [Fact]
    public async Task SessionEndReleasesTheOwnersPins()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);
        Start(client, "place-1", stream: 4);

        client.Send("""{"type":"session_end"}""");

        WaitUntil(() => !Pinned(rig, "place-1"));
    }

    [Fact]
    public async Task AResumeTokensPinsSurviveTheBreakAndTheResumeAndGoWhenTheWindowPasses()
    {
        using PipeRig rig = Rig();
        using (V2Client first = await Resume(rig))
        {
            Start(first, "place-1", stream: 4);
        }

        WaitUntil(() => rig.Holds.Held == 1);
        using (V2Client second = await Resume(rig))
        {
            Assert.True((bool)second.Welcome["resume"]!["resumed"]!);
            Sync(second, "after-resume");
            Assert.True(Pinned(rig, "place-1"));
        }

        WaitUntil(() => rig.Holds.Held == 1);
        Assert.True(Pinned(rig, "place-1"));
        _clock.Advance(WindowMs);
        WaitUntil(() => !Pinned(rig, "place-1"));
    }

    [Fact]
    public void TheSchedulerTellsThePinStoresAtTheNextFrame()
    {
        StreamStores stores = new StreamStores();
        KeptJobs kept = new KeptJobs();
        stores.Register(kept);
        LaneScheduler scheduler = new LaneScheduler(SchedulerSettings.Default, stores);
        kept.Pin("place-1", new CallContext("c1", 2));

        scheduler.ReleasePins("c1", 2);
        Assert.True(kept.Pins.IsPinned("place-1"));
        scheduler.RunFrame(false, null, new IdleRunner());

        Assert.False(kept.Pins.IsPinned("place-1"));
    }

    [Fact]
    public void AReleaseReachesOnlyPinStoresAndAThrowingOneDoesNotStopTheOthers()
    {
        StreamStores stores = new StreamStores();
        RecordingPins first = new RecordingPins(throws: true);
        RecordingPins second = new RecordingPins(throws: false);
        stores.Register(new PlainStore());
        stores.Register(first);
        stores.Register(second);

        stores.ReleasePins("c3", 7);

        Assert.Equal(new[] { ("c3", 7L) }, second.Released);
    }

    [Fact]
    public void TheModsStoresIncludeTheKeptJobs()
    {
        KeptJobs.Main.Pin("pin-check", new CallContext("pin-owner", 3));

        StreamStores.Main.ReleasePins("pin-owner", 3);

        Assert.False(KeptJobs.Main.Pins.IsPinned("pin-check"));
    }

    private PipeRig Rig()
    {
        StreamStores stores = new StreamStores();
        stores.Register(_kept);
        PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false), stores: stores,
            resumeWindowMs: WindowMs, resumeClock: _clock);
        // build {what: "structure"} stands in for any call that starts a job; job is the fake main thread's own param.
        rig.Mod.CallHook = (call, _) => call.Request.Method == "build" ? new CallOutcome(Started(call), "build") : null;
        return rig;
    }

    // On the fake main thread, in the call's context: the job is pinned as HeldTickJobs.Start pins it.
    private string Started(ProtocolCall call)
    {
        string job = call.Request.Params!.Value<string>("job")!;
        _kept.Pin(job, CallContext.Current);
        return new JObject
        {
            ["type"] = "reply", ["id"] = call.Request.Id, ["ok"] = true, ["result"] = new JObject { ["job_id"] = job }
        }.ToString(Formatting.None);
    }

    private bool Pinned(PipeRig rig, string job)
    {
        bool pinned = false;
        rig.Mod.OnMainThread(() => pinned = _kept.Pins.IsPinned(job));
        return pinned;
    }

    private static void Start(V2Client client, string job, long stream)
    {
        JObject reply = CallOn(client, "start-" + job, "build", new JObject { ["what"] = "structure", ["job"] = job },
            stream);
        Assert.True((bool)reply["ok"]!, reply.ToString());
    }

    // A call on stream 0 after a message: its reply means the frame that took the message in has run.
    private static JObject Sync(V2Client client, string id) => CallOn(client, id, "read", TestVerbs.Clock(), 0);

    private static JObject CallOn(V2Client client, string id, string method, JObject parameters, long stream)
    {
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
        Assert.Equal("welcome", (string?)client.Welcome["type"]);
        return client;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException("the condition never held");
            }

            Thread.Sleep(10);
        }
    }

    private sealed class IdleRunner : ICallRunner
    {
        public CallOutcome RunCall(ProtocolCall call, double queueWaitMs) => throw new InvalidOperationException("no calls");
    }

    private sealed class PlainStore : IStreamStore
    {
        public void Ended(StreamEnd end)
        {
        }
    }

    private sealed class RecordingPins : IPinStore
    {
        private readonly bool _throws;

        internal RecordingPins(bool throws) => _throws = throws;

        internal List<(string, long)> Released { get; } = new List<(string, long)>();

        public void Ended(StreamEnd end)
        {
        }

        public void Release(string owner, long stream)
        {
            if (_throws)
            {
                throw new InvalidOperationException("broken store");
            }

            Released.Add((owner, stream));
        }
    }
}
