#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Resuming across a reconnect (protocol.md, Resuming) with walks: a hello with resume keeps the connection's walk
/// cursors and ended streams under the token, held for the window after a close and picked up by the next connection
/// that presents it; the window passing, a bye, a world change or no token at all end that state as stream_end would.
/// A live token is taken over. Subscriptions and sample_logic runs across a resume are in ResumeSubscriptionTests.
/// </summary>
public sealed class ResumeWireTests
{
    private const string Token = "hub-session-0123456789abcdef";
    private const int WindowMs = 60000;

    private readonly WalkCursors _cursors = new WalkCursors();
    private readonly FakeClock _clock = new FakeClock();

    [Theory]
    [InlineData("\"short\"")]
    [InlineData("\"has spaces in it 0123456789\"")]
    [InlineData("\"dots.are.not.allowed.here\"")]
    [InlineData("12345678901234567890")]
    [InlineData("true")]
    public async Task AMalformedResumeTokenIsRefusedLikeAnyHelloError(string resume)
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Open(rig);
        client.Send($$"""{"type":"hello","protocol":[2],"client":{"name":"hub","version":"1"},"resume":{{resume}}}""");

        JObject refused = client.Next();
        Assert.Equal("protocol_error", (string?)refused["error"]!["code"]);
        Assert.Contains("hello.resume", (string?)refused["error"]!["message"]);
        Assert.Equal("protocol_error", (string?)client.Next()["reason"]);
    }

    [Fact]
    public void ATokenIsSixteenToOneHundredTwentyEightLettersDigitsDashesOrUnderscores()
    {
        Assert.True(ClientMessage.IsResumeToken(new string('a', 16)));
        Assert.True(ClientMessage.IsResumeToken("AZaz09-_" + new string('x', 120)));
        Assert.False(ClientMessage.IsResumeToken(new string('a', 15)));
        Assert.False(ClientMessage.IsResumeToken(new string('a', 129)));
        Assert.False(ClientMessage.IsResumeToken("abcdefghijklmnop!"));
        Assert.False(ClientMessage.IsResumeToken(null));
    }

    [Fact]
    public async Task WelcomeListsResumeAndAnswersTheTokenWithNothingResumedTheFirstTime()
    {
        using PipeRig rig = Rig();
        using V2Client plain = await V2Client.Connect(rig);
        using V2Client client = await Resume(rig);

        Assert.Contains("resume", plain.Welcome["features"]!.Values<string>());
        Assert.Null(plain.Welcome["resume"]);
        Assert.Equal(new JObject { ["token"] = Token, ["resumed"] = false, ["window_s"] = 60.0 }, client.Welcome["resume"]);
    }

    [Fact]
    public async Task AWalkGoesOnAfterTheSocketDropsAndTheTokenComesBackWithinTheWindow()
    {
        using PipeRig rig = Rig();
        string cursor;
        using (V2Client first = await Resume(rig))
        {
            JObject opened = Walk(first, "1", null, stream: 4);
            Assert.Equal(new JArray("1", "2"), opened["result"]!["things"]);
            cursor = (string)opened["result"]!["cursor_id"]!;
        }

        WaitHeld(rig, 1);
        _clock.Advance(WindowMs - 1);
        using V2Client second = await Resume(rig);
        JObject next = Walk(second, "2", cursor, stream: 4);

        Assert.True((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.True((bool)next["ok"]!, next.ToString());
        Assert.Equal(new JArray("3", "4"), next["result"]!["things"]);
        Assert.Equal(0, rig.Holds.Held);
    }

    [Fact]
    public async Task AfterTheWindowTheWalkAnswersCursorExpiredWithItsPosition()
    {
        using PipeRig rig = Rig();
        string cursor;
        using (V2Client first = await Resume(rig))
        {
            cursor = (string)Walk(first, "1", null, stream: 4)["result"]!["cursor_id"]!;
        }

        WaitHeld(rig, 1);
        _clock.Advance(WindowMs);
        WaitUntil(() => _cursors.Count == 0);
        using V2Client second = await Resume(rig);
        JObject expired = Walk(second, "2", cursor, stream: 4);

        Assert.False((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal("cursor_expired", (string?)expired["error"]!["code"]);
        Assert.Equal(2, (int)expired["error"]!["data"]!["position"]!);
        Assert.Equal(0, rig.Holds.Held);
    }

    [Fact]
    public async Task WithoutATokenAClosedConnectionsWalksEndAsBefore()
    {
        using PipeRig rig = Rig();
        using (V2Client client = await V2Client.Connect(rig))
        {
            Walk(client, "1", null, stream: 4);
            Assert.Equal(1, _cursors.Count);
        }

        WaitUntil(() => _cursors.Count == 0);
        Assert.Equal(0, rig.Holds.Held);
    }

    [Fact]
    public async Task WithTheWindowAtZeroATokenIsAnsweredButNothingIsHeld()
    {
        using PipeRig rig = Rig(windowMs: 0);
        using (V2Client client = await Resume(rig))
        {
            Assert.DoesNotContain("resume", client.Welcome["features"]!.Values<string>());
            Assert.Equal(new JObject { ["token"] = Token, ["resumed"] = false, ["window_s"] = 0.0 },
                client.Welcome["resume"]);
            Walk(client, "1", null, stream: 4);
        }

        WaitUntil(() => _cursors.Count == 0);
        using V2Client again = await Resume(rig);
        Assert.False((bool)again.Welcome["resume"]!["resumed"]!);
        Assert.Equal(0, rig.Holds.Held);
    }

    [Fact]
    public async Task EndedStreamsStayEndedAfterAResume()
    {
        using PipeRig rig = Rig();
        using (V2Client first = await Resume(rig))
        {
            first.Send("""{"type":"stream_end","stream":5}""");
            Assert.True((bool)first.Call("sync", "game_clock", new JObject())["ok"]!);
        }

        WaitHeld(rig, 1);
        using V2Client second = await Resume(rig);
        second.Send("""{"type":"call","id":"late","method":"game_clock","params":{},"stream":5}""");
        JObject refused = second.Next();

        Assert.Equal("stream_ended", (string?)refused["error"]!["code"]);
        Assert.True((bool)CallOn(second, "open", 6)["ok"]!);
    }

    [Fact]
    public async Task AByeEndsTheTokensStateAtOnce()
    {
        using PipeRig rig = Rig();
        using (V2Client first = await Resume(rig))
        {
            Walk(first, "1", null, stream: 4);
            first.Send("""{"type":"bye"}""");
            Assert.True(first.WaitClosed(5000));
        }

        WaitUntil(() => _cursors.Count == 0);
        using V2Client second = await Resume(rig);

        Assert.False((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal(0, rig.Holds.Held);
    }

    [Fact]
    public async Task AWorldChangeEndsEveryHeldTokenAtOnce()
    {
        using PipeRig rig = Rig();
        using (V2Client first = await Resume(rig))
        {
            Walk(first, "1", null, stream: 4);
        }

        WaitHeld(rig, 1);
        rig.Holds.WorldChanged();
        WaitUntil(() => _cursors.Count == 0);
        using V2Client second = await Resume(rig);

        Assert.False((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal(0, rig.Holds.Held);
    }

    [Fact]
    public async Task ASecondConnectionWithALiveTokenTakesItOverAndTheFirstIsToldWhy()
    {
        using PipeRig rig = Rig();
        using V2Client first = await Resume(rig);
        string cursor = (string)Walk(first, "1", null, stream: 4)["result"]!["cursor_id"]!;

        using V2Client second = await Resume(rig);
        JObject goodbye = first.Next();
        JObject next = Walk(second, "2", cursor, stream: 4);

        Assert.True((bool)second.Welcome["resume"]!["resumed"]!);
        Assert.Equal("goodbye", (string?)goodbye["type"]);
        Assert.Equal("resumed_elsewhere", (string?)goodbye["reason"]);
        Assert.True(first.WaitClosed(5000));
        Assert.Equal(new JArray("3", "4"), next["result"]!["things"]);
        WaitUntil(() => rig.Host.Open.Count == 1);
        Assert.Equal(0, rig.Holds.Held);
        Assert.Equal(1, _cursors.Count);
    }

    [Fact]
    public async Task TheConnectionsOwnerIsTheTokensKeyNotItsClientId()
    {
        using PipeRig rig = Rig();
        CallContext seen = CallContext.None;
        rig.Mod.CallHook = (_, _) =>
        {
            seen = CallContext.Current;
            return null;
        };
        using V2Client client = await Resume(rig);

        Assert.True((bool)CallOn(client, "1", 3)["ok"]!);
        Assert.Matches("^r[0-9]+$", seen.Owner);
        Assert.NotEqual((string)client.Welcome["client_id"]!, seen.Owner);
        Assert.Equal(3, seen.Stream);
    }

    [Fact]
    public void AHeldTokenEndsOnTheFirstTickAtItsWindowsEndAndTickAllocatesNothingBefore()
    {
        RecordingQueue calls = new RecordingQueue();
        ResumeHolds holds = new ResumeHolds(1000, calls, _clock);
        Connection connection = Unstarted(calls);
        holds.Claim(Token, connection, out bool resumed);
        holds.Release(connection);
        Assert.False(resumed);
        Assert.Equal(new[] { connection.Owner }, calls.Held);

        _clock.Advance(999);
        holds.Tick();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 1000; frame++)
        {
            holds.Tick();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Empty(calls.Ended);

        _clock.Advance(1);
        holds.Tick();
        holds.Tick();
        StreamEnd end = Assert.Single(calls.Ended);
        Assert.True(end.IsWholeConnection);
        Assert.Equal(connection.Owner, end.Owner);
        Assert.Equal(0, holds.Held);
    }

    [Fact]
    public void AResumedTokenKeepsItsOwnerAndATokenPresentedAfterItsEndGetsANewOne()
    {
        RecordingQueue calls = new RecordingQueue();
        ResumeHolds holds = new ResumeHolds(1000, calls, _clock);
        Connection first = Unstarted(calls);
        Connection second = Unstarted(calls);
        Connection third = Unstarted(calls);
        EndedStreams ended = holds.Claim(Token, first, out _);
        holds.Release(first);

        Assert.Same(ended, holds.Claim(Token, second, out bool resumed));
        Assert.True(resumed);
        Assert.Equal(first.Owner, second.Owner);
        Assert.Equal(new[] { second.Owner }, calls.Resumed);

        holds.Goodbye(second);
        holds.Release(second);
        holds.Claim(Token, third, out bool again);
        Assert.False(again);
        Assert.NotEqual(second.Owner, third.Owner);
        Assert.Equal(second.Owner, Assert.Single(calls.Ended).Owner);
    }

    // ---- the rig: a walk over ids 1..10, two a page, kept in the test's own cursor store ----

    private PipeRig Rig(int windowMs = WindowMs)
    {
        StreamStores stores = new StreamStores();
        stores.Register(_cursors);
        PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false), stores: stores,
            resumeWindowMs: windowMs, resumeClock: _clock);
        rig.Mod.CallHook = (call, _) => call.Request.Method == "walk" ? new CallOutcome(WalkReply(call), "walk") : null;
        return rig;
    }

    private string WalkReply(ProtocolCall call)
    {
        string? cursorId = call.Request.Params?.Value<string>("cursor_id");
        JObject reply = new JObject { ["type"] = "reply", ["id"] = call.Request.Id };
        if (cursorId == null)
        {
            string? opened = _cursors.Open(CallContext.Current, new long[] { 3, 4, 5, 6, 7, 8, 9, 10 }, 2);
            reply["ok"] = true;
            reply["result"] = new JObject { ["things"] = new JArray("1", "2"), ["cursor_id"] = opened };
            return reply.ToString(Formatting.None);
        }

        List<string> page = new List<string>();
        if (!_cursors.TryNext(CallContext.Current, cursorId, 2, static id => id.ToString(), page, out WalkStep step))
        {
            reply["ok"] = false;
            reply["error"] = new JObject
            {
                ["code"] = WalkCursors.CursorExpiredCode, ["message"] = "expired",
                ["data"] = new JObject { ["cursor_id"] = cursorId, ["position"] = step.ExpiredPosition }
            };
            return reply.ToString(Formatting.None);
        }

        reply["ok"] = true;
        reply["result"] = new JObject { ["things"] = new JArray(page.ToArray()), ["cursor_id"] = step.CursorId };
        return reply.ToString(Formatting.None);
    }

    private static async Task<V2Client> Resume(PipeRig rig, string token = Token)
    {
        V2Client client = await V2Client.Open(rig);
        client.Send(new JObject
        {
            ["type"] = "hello", ["protocol"] = new JArray(2), ["client"] = new JObject { ["name"] = "hub", ["version"] = "1" },
            ["resume"] = token
        }.ToString(Formatting.None));
        client.Welcome = client.Next();
        Assert.Equal("welcome", (string?)client.Welcome["type"]);
        return client;
    }

    private static JObject Walk(V2Client client, string id, string? cursorId, long stream)
    {
        JObject parameters = cursorId == null ? new JObject() : new JObject { ["cursor_id"] = cursorId };
        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "walk", ["params"] = parameters, ["stream"] = stream
        }.ToString(Formatting.None));
        return NextReply(client, id);
    }

    private static JObject CallOn(V2Client client, string id, long stream)
    {
        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = id, ["method"] = "game_clock", ["params"] = new JObject(), ["stream"] = stream
        }.ToString(Formatting.None));
        return NextReply(client, id);
    }

    private static JObject NextReply(V2Client client, string id)
    {
        while (true)
        {
            JObject message = client.Next();
            if ((string?)message["type"] == "reply" && (string?)message["id"] == id)
            {
                return message;
            }
        }
    }

    private static void WaitHeld(PipeRig rig, int held) => WaitUntil(() => rig.Holds.Held == held);

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

    // A connection that never starts: what ResumeHolds keys by, without a transport behind it.
    private static Connection Unstarted(ICallQueue calls) =>
        new Connection(new NoTransport(), new ProtocolHost(new ProtocolSettings(1), calls, new DeadlineWatch(), null));

    /// <summary>The main thread's side of ResumeHolds, recorded.</summary>
    private sealed class RecordingQueue : ICallQueue
    {
        internal List<StreamEnd> Ended { get; } = new List<StreamEnd>();

        internal List<string> Held { get; } = new List<string>();

        internal List<string> Resumed { get; } = new List<string>();

        public void Submit(QueuedCall call)
        {
        }

        public void Withdraw(QueuedCall call)
        {
        }

        public void StreamEnded(object source, StreamEnd end) => Ended.Add(end);

        public void CursorClosed(string owner, string cursorId)
        {
        }

        public void Closed(object source)
        {
        }

        public void OwnerHeld(string owner) => Held.Add(owner);

        public void OwnerResumed(string owner, object source) => Resumed.Add(owner);
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
}
