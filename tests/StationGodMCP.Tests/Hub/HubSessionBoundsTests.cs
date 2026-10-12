#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>A session's bounds (request size, close, slow callers, streams and calls) and its pass-through rules.</summary>
public sealed class HubSessionBoundsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ALineOverMaxRequestBytesIsRefusedRequestTooLargeAndEndsTheSession()
    {
        FakeHubLink link = new() { MaxRequestBytes = 1024 };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        caller.Send(HubCaller.Call("1", "thing_health", """{"reference_ids":[""" + "\"" + new string('5', 3000) + "\"]}"));
        JsonElement refused = await caller.NextAsync();

        Assert.Equal(JsonValueKind.Null, refused.GetProperty("id").ValueKind);
        Assert.Equal("request_too_large", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1024, refused.GetProperty("error").GetProperty("data").GetProperty("limit").GetInt32());
        Assert.Equal(HubSessionEnd.RequestTooLarge, await caller.Running.WaitAsync(Patience));
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task CloseIsBoundedWhenACallNeverEndsAndTheGameIsDown()
    {
        FakeHubLink link = new() { Answer = (_, _) => new TaskCompletionSource<GameReply>().Task };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        caller.Send(HubCaller.Call("1", "thing_health"));
        await WaitUntil(() => link.Calls.Count == 1);
        link.Reachable = false;

        Stopwatch closing = Stopwatch.StartNew();
        await caller.EndAsync();

        Assert.True(closing.Elapsed < TimeSpan.FromSeconds(3), $"The close took {closing.Elapsed}.");
        Assert.Empty(link.EndedStreams);
    }

    [Fact]
    public async Task OnlyHubStreamsThatCarriedACallAreEndedOnClose()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        await caller.CallAsync("1", "thing_health", "{}", stream: 5);
        long five = link.CallsTo("thing_health")[0].Stream;

        await caller.EndAsync();

        Assert.Equal(new[] { five }, link.EndedStreams);
        Assert.NotEqual(caller.Session.MainStream, five);
    }

    [Fact]
    public async Task ACallerThatStopsReadingIsEndedSlowClient()
    {
        string big = "\"" + new string('x', 64 * 1024) + "\"";
        FakeHubLink link = new() { Answer = (_, _) => Task.FromResult(FakeGameLink.Ok("{\"text\":" + big + "}")) };
        await using HubCaller caller = HubCaller.Start(link, slowAfter: TimeSpan.FromMilliseconds(300));
        await caller.HelloAsync();

        for (int index = 0; index < 40; index++)
        {
            caller.Send(HubCaller.Call("c" + index, "thing_health"));
        }

        HubSessionEnd end = await caller.Running.WaitAsync(Patience);
        _ = caller.StartDraining();

        Assert.Equal(HubSessionEnd.SlowClient, end);
    }

    [Fact]
    public async Task ASubscriptionsFirstEventRaisedRightAfterItsReplyFollowsTheReplyAndOthersAreNotKept()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Method == "subscribe"
                ? FakeGameLink.Ok("""{"subscription":"s40","interval_s":1.0,"values":1,"frame":5,"result":{}}""")
                : FakeGameLink.Ok("{}"))
        };
        link.AfterReply = call =>
        {
            if (call.Method == "subscribe")
            {
                // On the link's reader, before the session's call wakes: another session's event, then this one's.
                link.Raise("""{"type":"event","event":"update","subscription":"s77","seq":1,"result":{}}""");
                link.Raise("""{"type":"event","event":"update","subscription":"s40","seq":1,"frame":6,"result":{}}""");
            }
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        JsonElement reply = await caller.CallAsync("1", "subscribe", "{}");
        JsonElement first = await caller.NextAsync();
        JsonElement next = await caller.CallAsync("2", "thing_health");

        Assert.Equal("s1", reply.GetProperty("result").GetProperty("subscription").GetString());
        Assert.Equal("event", first.GetProperty("type").GetString());
        Assert.Equal("s1", first.GetProperty("subscription").GetString());
        Assert.Equal(6, first.GetProperty("frame").GetInt64());
        Assert.Equal("2", next.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ASessionHoldsAtMost64OpenStreams()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        // Stream 0 is open from the start, so 63 more reach the bound.
        for (int stream = 1; stream < HubSession.MaxStreams; stream++)
        {
            Assert.True((await caller.CallAsync("s" + stream, "thing_health", "{}", stream)).GetProperty("ok").GetBoolean());
        }

        JsonElement refused = await caller.CallAsync("over", "thing_health", "{}", stream: 100);
        caller.Send("""{"type":"stream_end","stream":1}""");
        JsonElement afterEnd = await caller.CallAsync("again", "thing_health", "{}", stream: 100);

        Assert.Equal("too_many_streams", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(64, refused.GetProperty("error").GetProperty("data").GetProperty("limit").GetInt32());
        Assert.True(afterEnd.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ASessionHoldsAtMostThePerStreamLimitTimes64CallsInFlight()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHubLink link = new()
        {
            Welcome = FakeHubLink.Element(FakeHubLink.ModWelcome.Replace("\"max_in_flight\":16", "\"max_in_flight_per_stream\":1")),
            Answer = async (_, _) =>
            {
                await release.Task;
                return FakeGameLink.Ok("{}");
            }
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        for (int index = 0; index < 64; index++)
        {
            caller.Send(HubCaller.Call("c" + index, "thing_health"));
        }

        JsonElement refused = await caller.CallAsync("over", "thing_health");
        release.SetResult();

        Assert.Equal("over", refused.GetProperty("id").GetString());
        Assert.Equal("too_many_in_flight", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(64, refused.GetProperty("error").GetProperty("data").GetProperty("limit").GetInt32());
        Assert.Equal(64, link.Calls.Count);
    }

    [Fact]
    public async Task APassThroughTakesOnlyReadOnlyMethodsWritesGoThroughVerb()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        JsonElement write = await caller.CallAsync("1", "write_logic", """{"reference_id":"5","logic_type":"On","value":1}""");
        JsonElement hold = await caller.CallAsync("2", "hold_restore", """{"clear_all":true}""");
        JsonElement unknown = await caller.CallAsync("3", "no_such_method");
        JsonElement read = await caller.CallAsync("4", "thing_health", """{"reference_ids":["5"]}""");

        Assert.Equal("not_allowed", write.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("verb", write.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("not_allowed", hold.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("method_not_found", unknown.GetProperty("error").GetProperty("code").GetString());
        Assert.True(read.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "thing_health" }, link.Calls.Select(call => call.Method));
    }

    [Fact]
    public async Task AWriterFaultOtherThanIoEndsTheSessionOutputBrokenAndCloseCompletes()
    {
        FakeHubLink link = new();
        HubSession session = new(link, new VerbPipeline(link), new HubSessionSettings());
        using AnonymousPipeServerStream toSession = new(PipeDirection.Out);
        using AnonymousPipeClientStream input = new(PipeDirection.In, toSession.ClientSafePipeHandle);
        Task<HubSessionEnd> running = Task.Run(() => session.RunAsync(input, new ThrowingStream(), CancellationToken.None));

        byte[] hello = Encoding.UTF8.GetBytes(HubCaller.Hello + "\n");
        toSession.Write(hello, 0, hello.Length);
        HubSessionEnd end = await running.WaitAsync(Patience);
        await session.CloseAsync().WaitAsync(Patience);
        toSession.Dispose();

        Assert.Equal(HubSessionEnd.OutputBroken, end);
    }

    [Fact]
    public async Task APassedReplyKeepsShapedQueueMsAndTheModsMaybeRan()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult<GameReply>(call.Method == "thing_health"
                ? new GameReply.Ok(FakeHubLink.Element("{}"), 3, 0.5, false) { Shaped = true, QueueMs = 2.5 }
                : new GameReply.Refused("internal_error", "Partly ran.", null) { MaybeRan = true })
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        JsonElement shaped = await caller.CallAsync("1", "thing_health", "{}");
        JsonElement refused = await caller.CallAsync("2", "read_logic", """{"reference_id":"5","logic_type":"On"}""");

        Assert.True(shaped.GetProperty("shaped").GetBoolean());
        Assert.Equal(2.5, shaped.GetProperty("queue_ms").GetDouble());
        Assert.True(refused.GetProperty("error").GetProperty("maybe_ran").GetBoolean());
    }

    [Fact]
    public async Task TheWelcomeDoesNotAdvertiseResume()
    {
        FakeHubLink link = new()
        {
            Welcome = FakeHubLink.Element(FakeHubLink.ModWelcome.Replace("\"cursors\"]", "\"cursors\",\"resume\"]"))
        };
        await using HubCaller caller = HubCaller.Start(link);

        JsonElement welcome = await caller.HelloAsync();

        Assert.DoesNotContain("resume", welcome.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        Assert.Contains("pipeline", welcome.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
    }

    [Fact]
    public async Task AWorldChangeTheLinkLearnsReachesTheCallerOnceAsWorldChanged()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        // The mod's own event is covered by the link's WorldChanged, so it is not passed on a second time.
        link.Raise("""{"type":"event","event":"world_changed","world":{"id":"world-2","save":"other","epoch":1}}""");
        link.World = FakeHubLink.Element("""{"id":"world-2","save":"other","epoch":1}""");
        link.ChangeWorld("world-2");
        JsonElement changed = await caller.NextAsync();
        JsonElement next = await caller.CallAsync("1", "thing_health");

        Assert.Equal("world_changed", changed.GetProperty("event").GetString());
        Assert.Equal("world-2", changed.GetProperty("world").GetProperty("id").GetString());
        Assert.Equal("other", changed.GetProperty("world").GetProperty("save").GetString());
        Assert.Equal("1", next.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AWorldOver64CharactersOrAShapeThatIsNoObjectIsInvalid()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        caller.Send("""{"type":"call","id":"1","method":"thing_health","world":""" + "\"" + new string('w', 65) + "\"}");
        JsonElement world = await caller.NextAsync();
        caller.Send("""{"type":"call","id":"2","method":"thing_health","shape":"fields"}""");
        JsonElement shape = await caller.NextAsync();

        Assert.Equal("invalid_argument", world.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalid_argument", shape.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task ACursorIsForgottenWhenItsWalkEndsOrItsStreamEnds()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Parameters.TryGetProperty("cursor_id", out JsonElement _)
                ? FakeGameLink.Ok("""{"things":[]}""")
                : FakeGameLink.Ok("""{"things":[],"cursor_id":"k""" + call.Stream + "\"}"))
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        JsonElement walk = await caller.CallAsync("1", "find_things", """{"cursor":true}""");
        string first = walk.GetProperty("result").GetProperty("cursor_id").GetString()!;
        await caller.CallAsync("2", "find_things", "{\"cursor_id\":\"" + first + "\"}");
        JsonElement other = await caller.CallAsync("3", "find_things", """{"cursor":true}""", stream: 4);
        string second = other.GetProperty("result").GetProperty("cursor_id").GetString()!;
        caller.Send("""{"type":"stream_end","stream":4}""");

        caller.Send("{\"type\":\"cursor_close\",\"cursor_id\":\"" + first + "\"}");
        caller.Send("{\"type\":\"cursor_close\",\"cursor_id\":\"" + second + "\"}");
        await caller.CallAsync("4", "thing_health");

        Assert.Empty(link.ClosedCursors);
    }

    [Fact]
    public async Task ACallerThatLeavesMidCallGetsNoReplyItsStreamsEndAndItsSubscriptionsGo()
    {
        FakeHubLink link = new()
        {
            Answer = async (call, cancellation) =>
            {
                if (call.Method == "subscribe")
                {
                    return FakeGameLink.Ok("""{"subscription":"s40","interval_s":1.0,"values":1,"frame":5,"result":{}}""");
                }

                if (call.Method == "unsubscribe")
                {
                    return FakeGameLink.Ok("""{"subscription":"s40","ended":true}""");
                }

                await Task.Delay(Timeout.Infinite, cancellation);
                return FakeGameLink.Ok("{}");
            }
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        await caller.CallAsync("1", "subscribe", "{}");
        caller.Send(HubCaller.Call("2", "sample_logic"));
        await WaitUntil(() => link.CallsTo("sample_logic").Count == 1);

        await caller.EndAsync();

        Assert.Empty(await caller.LastLinesWithinAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(new[] { caller.Session.MainStream }, link.EndedStreams);
        Assert.Equal("""{"subscription":"s40"}""", Assert.Single(link.CallsTo("unsubscribe")).Parameters.GetRawText());
    }

    [Fact]
    public async Task StreamEndOfAStreamThatCarriedASubscriptionEndsItsRelay()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Method == "subscribe"
                ? FakeGameLink.Ok("""{"subscription":"s40","interval_s":1.0,"values":1,"frame":5,"result":{}}""")
                : FakeGameLink.Ok("{}"))
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        await caller.CallAsync("1", "subscribe", "{}", stream: 3);

        caller.Send("""{"type":"stream_end","stream":3}""");
        JsonElement unknown = await caller.CallAsync("2", "unsubscribe", """{"subscription":"s1"}""");
        link.Raise("""{"type":"event","event":"update","subscription":"s40","seq":2,"result":{}}""");
        JsonElement next = await caller.CallAsync("3", "thing_health");
        await caller.EndAsync();

        Assert.Equal("unknown_subscription", unknown.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("3", next.GetProperty("id").GetString());
        Assert.Empty(link.CallsTo("unsubscribe"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>An output whose every write fails with something other than an IO fault.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new InvalidOperationException("The output is broken.");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The output is broken.");
    }
}
