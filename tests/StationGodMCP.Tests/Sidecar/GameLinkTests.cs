#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using Xunit;
using static StationGodMCP.Tests.Sidecar.ClientTests;

namespace StationGodMCP.Tests.Sidecar;

/// <summary>
/// The hub's GameLink against the in-process fake game: stream tags, admission, the world id, resuming, stream_end and
/// session_end, hold_restore, stream_costs, job waits and release.
/// </summary>
public sealed class GameLinkTests
{
    private const string ReadOn = """{"what":"logic","id":"1","logic":"On"}""";

    [Fact]
    public async Task NewStreamsNumberUpwardFromOne()
    {
        await using GameLink link = new(new ClientOptions(new GameTarget.Pipe("unused")));

        Assert.Equal([1L, 2L, 3L], new[] { link.NewStream(), link.NewStream(), link.NewStream() });
    }

    [Fact]
    public async Task AFirstConnectThatFailsIsRetriedUntilTheGameAnswers()
    {
        // The hub answers hello before the game is up; the link must come up on its own when the game does, so
        // LinkUp (a session's refreshed welcome) reaches the caller without any call being made.
        string name = "StationGodMCP-fake-late-" + Guid.NewGuid().ToString("N");
        await using GameLink link = new(new ClientOptions(new GameTarget.Pipe(name)));
        int ups = 0;
        link.LinkUp += () => Interlocked.Increment(ref ups);

        Assert.False(await link.ConnectAsync());
        Assert.Null(link.Welcome);

        await using FakeGame game = FakeGame.OnPipe(name);
        Stopwatch watch = Stopwatch.StartNew();
        while (link.Welcome == null && watch.ElapsedMilliseconds < 15000)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(link.Welcome);
        Assert.Equal("world-1", link.WorldId);
        Assert.Equal(1, Volatile.Read(ref ups));
    }

    [Fact]
    public async Task TheStreamTagIsSentForStreamsOtherThanZero()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));

        Assert.IsType<GameReply.Ok>(await link.CallAsync(new GameCall("game_clock", Json("{}"), 0), default));
        Assert.IsType<GameReply.Ok>(await link.CallAsync(new GameCall("weather", Json("{}"), 3), default));

        Assert.False(Assert.Single(game.CallsTo("game_clock")).Message.TryGetProperty("stream", out _));
        Assert.Equal(3, Assert.Single(game.CallsTo("weather")).Message.GetProperty("stream").GetInt64());
    }

    [Fact]
    public async Task NoStreamTagIsSentWhenTheModHasNoStreams()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("streams");
        await using GameLink link = new(new ClientOptions(game.Target));

        await link.CallAsync(new GameCall("weather", Json("{}"), 3), default);

        Assert.False(Assert.Single(game.CallsTo("weather")).Message.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task ShapeAndDeadlineAreSentWhenGivenAndListed()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));

        await link.CallAsync(new GameCall("weather", Json("{}"), 2)
        {
            Shape = Json("""{"fields":["a"]}"""), DeadlineMs = 1500
        }, default);
        game.Features.Remove("shape");
        game.DropAll();
        await Eventually(() => game.Connections == 2 && link.WorldId != null);
        await link.CallAsync(new GameCall("game_clock", Json("{}"))
        {
            Shape = Json("""{"fields":["a"]}"""), DeadlineMs = 900
        }, default);
        await link.CallAsync(new GameCall("weather_forecast", Json("{}")), default);

        JsonElement first = Assert.Single(game.CallsTo("weather")).Message;
        Assert.Equal("""{"fields":["a"]}""", first.GetProperty("shape").GetRawText());
        Assert.Equal(1500, first.GetProperty("deadline_ms").GetInt32());
        JsonElement second = Assert.Single(game.CallsTo("game_clock")).Message;
        Assert.False(second.TryGetProperty("shape", out _));
        Assert.Equal(900, second.GetProperty("deadline_ms").GetInt32());
        Assert.False(Assert.Single(game.CallsTo("weather_forecast")).Message.TryGetProperty("deadline_ms", out _));
    }

    [Fact]
    public async Task AnOkReplyCarriesItsResultFrameTimeAndBudgetFlag()
    {
        await using FakeGame game = HubGame();
        game.Answer = call => Task.FromResult<string?>(
            "{\"type\":\"reply\",\"id\":" + call.Id.GetRawText() +
            ",\"ok\":true,\"result\":{\"v\":1},\"elapsed_ms\":1.5,\"queue_ms\":0.2,\"frame\":42,\"budget_hit\":true}");
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply.Ok ok = Assert.IsType<GameReply.Ok>(await link.CallAsync(new GameCall("weather", Json("{}")), default));

        Assert.Equal("""{"v":1}""", ok.Result.GetRawText());
        Assert.Equal(42, ok.Frame);
        Assert.Equal(1.5, ok.ElapsedMs);
        Assert.True(ok.BudgetHit);
    }

    [Fact]
    public async Task WaitingCallsAreAdmittedRoundRobinByStreamWithStreamZeroFirst()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerStream = 1;
        game.MaxInFlightPerConnection = 1;
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            await open.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        List<Task<GameReply>> calls = [link.CallAsync(new GameCall("weather", Json("{}"), 3), default)];
        await Eventually(() => game.Calls.Count == 1);
        for (int index = 0; index < 19; index++)
        {
            calls.Add(link.CallAsync(new GameCall("weather", Json("{}"), 3), default));
        }

        calls.Add(link.CallAsync(new GameCall("weather", Json("{}"), 0), default));
        calls.Add(link.CallAsync(new GameCall("weather", Json("{}"), 0), default));
        open.SetResult();
        await Task.WhenAll(calls);

        long[] expected = [3, 0, 3, 0, .. Enumerable.Repeat(3L, 18)];
        Assert.Equal(expected, game.Calls.Select(StreamOf).ToArray());
    }

    [Fact]
    public async Task NoMoreThanTheStreamLimitAreInFlightOnOneStream()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerStream = 2;
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            if (StreamOf(call) == 1)
            {
                await open.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        Task<GameReply>[] held = Enumerable.Range(0, 6)
            .Select(_ => link.CallAsync(new GameCall("weather", Json("{}"), 1), default)).ToArray();
        await Eventually(() => game.Calls.Count == 2);
        Assert.IsType<GameReply.Ok>(await link.CallAsync(new GameCall("weather", Json("{}"), 2), default));
        await Task.Delay(150);

        Assert.Equal(2, game.Calls.Count(call => StreamOf(call) == 1));
        open.SetResult();
        await Task.WhenAll(held);
        Assert.Equal(6, game.Calls.Count(call => StreamOf(call) == 1));
    }

    [Fact]
    public async Task NoMoreThanTheConnectionLimitAreInFlightInAll()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerConnection = 3;
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            await open.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        Task<GameReply>[] calls = Enumerable.Range(1, 5)
            .Select(stream => link.CallAsync(new GameCall("weather", Json("{}"), stream), default)).ToArray();
        await Eventually(() => game.Calls.Count == 3);
        await Task.Delay(150);

        Assert.Equal(3, game.Calls.Count);
        open.SetResult();
        await Task.WhenAll(calls);
        Assert.Equal(5, game.Calls.Count);
    }

    [Fact]
    public async Task EveryCallCarriesTheWorldIdAndAWorldChangeIsRaised()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        ConcurrentQueue<string?> worlds = new();
        link.WorldChanged += worlds.Enqueue;

        await link.CallAsync(new GameCall("game_clock", Json("{}")), default);
        Assert.Equal("world-1", link.WorldId);
        await game.PushAsync("""{"type":"event","event":"world_changed","world":{"id":"world-2","save":"x","epoch":1}}""");
        await Eventually(() => link.WorldId == "world-2");
        await link.CallAsync(new GameCall("weather", Json("{}")), default);

        Assert.Equal("world-1", Assert.Single(game.CallsTo("game_clock")).Message.GetProperty("world").GetString());
        Assert.Equal("world-2", Assert.Single(game.CallsTo("weather")).Message.GetProperty("world").GetString());
        Assert.Equal(["world-2"], worlds);
    }

    [Fact]
    public async Task AWorldChangedRefusalIsPassedThrough()
    {
        await using FakeGame game = HubGame();
        game.Answer = call => Task.FromResult<string?>(call.Method == "write"
            ? "{\"type\":\"reply\",\"id\":" + call.Id.GetRawText() +
              ",\"ok\":false,\"error\":{\"code\":\"world_changed\",\"message\":\"Another world.\",\"data\":{\"world\":{\"id\":\"w9\",\"save\":null,\"epoch\":2}}}}"
            : call.Ok("{}"));
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply.Refused refused = Assert.IsType<GameReply.Refused>(
            await link.CallAsync(new GameCall("write", Json("""{"reference_id":"1","logic_type":"On","value":1}""")), default));

        Assert.Equal("world_changed", refused.Code);
        Assert.Equal("Another world.", refused.Message);
        Assert.Equal("w9", refused.Data!.Value.GetProperty("world").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ANewWelcomeInAnotherWorldRaisesWorldChanged()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        ConcurrentQueue<string?> worlds = new();
        link.WorldChanged += worlds.Enqueue;
        Assert.True(await link.ConnectAsync());

        game.WorldId = "world-2";
        game.DropAll();

        await Eventually(() => link.WorldId == "world-2");
        Assert.Equal(["world-2"], worlds);
    }

    [Fact]
    public async Task TheResumeTokenIsTheSameInEveryHello()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.Matches("^hub-[0-9a-f]{16}$", link.ResumeToken);
        Assert.True(await link.ConnectAsync());

        game.DropAll();
        await Eventually(() => game.Connections == 2 && link.WorldId != null);

        string[] tokens = game.Received
            .Where(message => message.GetProperty("type").GetString() == "hello")
            .Select(message => message.GetProperty("resume").GetString()!).ToArray();
        Assert.Equal([link.ResumeToken, link.ResumeToken], tokens);
        Assert.Equal(link.ResumeToken, link.Welcome!.Value.GetProperty("resume").GetProperty("token").GetString());
    }

    [Fact]
    public async Task AWriteInFlightAcrossABreakAnswersMaybeRanAndIsNotSentAgain()
    {
        await using FakeGame game = HubGame();
        game.Answer = call =>
        {
            if (call.Method != "write")
            {
                return Task.FromResult<string?>(call.Ok("{}"));
            }

            game.DropAll();
            return Task.FromResult<string?>(null);
        };
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply.Unreachable unreachable = Assert.IsType<GameReply.Unreachable>(await link.CallAsync(
            new GameCall("write", Json("""{"reference_id":"1","logic_type":"On","value":1}"""), 4), default));

        Assert.True(unreachable.MaybeRan);
        Assert.Single(game.CallsTo("write"));
        await Eventually(() => game.Connections == 2);
    }

    [Fact]
    public async Task AReadInFlightAcrossABreakIsSentAgainOnce()
    {
        await using FakeGame game = HubGame();
        game.Answer = call =>
        {
            if (call.Method == "read" && game.CallsTo("read").Count() == 1)
            {
                game.DropAll();
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(call.Ok("""{"value":7}"""));
        };
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply reply = await link.CallAsync(new GameCall("read", Json(ReadOn), 2), default);

        Assert.Equal(7, Assert.IsType<GameReply.Ok>(reply).Result.GetProperty("value").GetInt32());
        Assert.Equal(2, game.CallsTo("read").Count());
    }

    [Fact]
    public async Task NoGameAnswersUnreachableThatDidNotRun()
    {
        await using GameLink link = new(new ClientOptions(new GameTarget.Pipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N"))));

        GameReply.Unreachable unreachable = Assert.IsType<GameReply.Unreachable>(
            await link.CallAsync(new GameCall("game_clock", Json("{}")), default));

        Assert.False(unreachable.MaybeRan);
        Assert.Null(link.WorldId);
    }

    [Fact]
    public async Task StreamEndAndSessionEndAreSentAsTheirLines()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));

        await link.EndStreamAsync(5);
        await link.EndStreamAsync(0);
        await link.SessionEndAsync();

        await Eventually(() => game.Received.Any(message => message.GetRawText() == """{"type":"session_end"}"""));
        Assert.Equal(["""{"type":"stream_end","stream":5}"""],
            game.Received.Where(message => message.GetProperty("type").GetString() == "stream_end").Select(message => message.GetRawText()));
    }

    [Fact]
    public async Task HoldRestoreIsCalledOnItsStream()
    {
        await using FakeGame game = HubGame();
        game.Answer = call => Task.FromResult<string?>(call.Method == "hold_restore"
            ? call.Ok("""{"restores":[{"reference_id":"9","logic_type":{"id":28,"name":"On"},"value":0}]}""")
            : call.Ok("{}"));
        await using GameLink link = new(new ClientOptions(game.Target));
        string change = """{"set":[{"reference_id":"9","logic_type":"On","value":0}]}""";

        GameReply reply = await link.HoldRestoreAsync(6, Json(change), default);

        FakeCall call = Assert.Single(game.CallsTo("hold_restore"));
        Assert.Equal(6, StreamOf(call));
        Assert.Equal(change, call.Params.GetRawText());
        Assert.Equal("9", Assert.IsType<GameReply.Ok>(reply).Result.GetProperty("restores")[0].GetProperty("reference_id").GetString());
    }

    [Fact]
    public async Task StreamCostsSendsTheStreamsAndReadsTheReply()
    {
        await using FakeGame game = HubGame();
        game.Answer = call => Task.FromResult<string?>(call.Method == "stream_costs"
            ? call.Ok("""{"streams":[{"stream":4,"calls":3,"main_thread_ms":1.25,"over_budget_frames":1,"samples":5,"sample_ms":2.5,"updates":2,"update_ms":0.6},{"stream":7,"calls":0,"main_thread_ms":0,"over_budget_frames":0}]}""")
            : call.Ok("{}"));
        await using GameLink link = new(new ClientOptions(game.Target));

        StreamCost[] costs = await link.StreamCostsAsync([4, 7], default);

        Assert.Equal("""{"streams":[4,7]}""", Assert.Single(game.CallsTo("stream_costs")).Params.GetRawText());
        Assert.Equal(2, costs.Length);
        Assert.Equal((4L, 3L, 1.25, 1L), (costs[0].Stream, costs[0].Calls, costs[0].MainThreadMs, costs[0].OverBudgetFrames));
        Assert.Equal((5L, 2.5, 2L, 0.6), (costs[0].Samples, costs[0].SampleMs, costs[0].Updates, costs[0].UpdateMs));
        Assert.Equal((7L, 0L, 0.0, 0L), (costs[1].Stream, costs[1].Calls, costs[1].MainThreadMs, costs[1].OverBudgetFrames));
        Assert.Equal((0L, 0.0, 0L, 0.0), (costs[1].Samples, costs[1].SampleMs, costs[1].Updates, costs[1].UpdateMs));
    }

    [Fact]
    public async Task StreamCostsIsEmptyWhenTheModRefusesIt()
    {
        await using FakeGame game = HubGame();
        game.Answer = call => Task.FromResult<string?>(call.Error("unknown_method", "No."));
        await using GameLink link = new(new ClientOptions(game.Target));

        Assert.Empty(await link.StreamCostsAsync([1], default));
    }

    [Fact]
    public async Task TheWorldAndJobsTopicsAreSubscribedAgainAfterAReconnectThatDidNotResume()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());
        await Eventually(() => Topics(game).Count == 2);

        game.DropAll();
        await Eventually(() => Topics(game).Count == 4);

        Assert.Equal(["jobs", "jobs", "world", "world"], Topics(game).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AResumedConnectionKeepsItsSubscriptions()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());
        await Eventually(() => Topics(game).Count == 2);

        game.Resumed = true;
        game.DropAll();
        await Eventually(() => game.Connections == 2 && link.WorldId != null);
        await link.CallAsync(new GameCall("game_clock", Json("{}")), default);

        Assert.Equal(2, Topics(game).Count);
    }

    [Fact]
    public async Task AJobWaitFindsAJobThatEndedBeforeIt()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        ConcurrentQueue<string> events = new();
        link.EventReceived += message => events.Enqueue(message.GetProperty("event").GetString()!);
        Assert.True(await link.ConnectAsync());

        await game.PushAsync(JobEnded("run-41", "applied"));
        await Eventually(() => events.Contains("job_ended"));
        JsonElement? job = await link.WaitJobAsync("run-41", TimeSpan.FromSeconds(5), default);

        Assert.Equal("applied", job!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AJobWaitEndsWhenItsJobEnds()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        Task<JsonElement?> waiting = link.WaitJobAsync("run-42", TimeSpan.FromSeconds(10), default);
        await game.PushAsync(JobEnded("run-1", "applied"));
        await game.PushAsync(JobEnded("run-42", "stopped"));

        JsonElement? job = await waiting;
        Assert.Equal("run-42", job!.Value.GetProperty("job_id").GetString());
        Assert.Equal("stopped", job.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AJobWaitAnswersNullWhenTheTimeoutPassesFirst()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        Assert.Null(await link.WaitJobAsync("run-43", TimeSpan.FromMilliseconds(100), default));
    }

    [Fact]
    public async Task OtherEventsAreRaisedForTheHubToRelay()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        ConcurrentQueue<string> events = new();
        link.EventReceived += message => events.Enqueue(message.GetProperty("event").GetString()!);
        Assert.True(await link.ConnectAsync());

        await game.PushAsync("""{"type":"event","event":"ping"}""");
        await game.PushAsync("""{"type":"event","event":"update","subscription":"s4","seq":1,"result":{}}""");

        await Eventually(() => events.Contains("update"));
        Assert.DoesNotContain("ping", events);
    }

    [Fact]
    public async Task ReleaseIsSentOnlyWhenTheModListsIt()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        await link.ReleaseAsync(3);
        await link.CallAsync(new GameCall("game_clock", Json("{}")), default);
        Assert.DoesNotContain(game.Received, message => message.GetProperty("type").GetString() == "release");

        game.Features.Add("release");
        game.DropAll();
        await Eventually(() => game.Connections == 2 && link.WorldId != null);
        await link.ReleaseAsync(3);
        await Eventually(() => game.Received.Any(message => message.GetRawText() == """{"type":"release","stream":3}"""));
    }

    // ---- review fixes ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACallMadeWhileDisconnectedIsNeverWrittenIntoTheNextWorld()
    {
        await using FakeGame game = HubGame();
        game.WelcomeDelay = number => number == 2 ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        game.WorldId = "world-2";
        game.DropAll();
        await Eventually(() => link.WorldId == null);
        GameReply reply = await link.CallAsync(new GameCall("write", Json(WriteOn)), default);

        GameReply.Refused refused = Assert.IsType<GameReply.Refused>(reply);
        Assert.Equal("world_changed", refused.Code);
        Assert.Equal("world-2", refused.Data!.Value.GetProperty("world").GetProperty("id").GetString());
        Assert.Empty(game.CallsTo("write"));
    }

    [Fact]
    public async Task InTheMenuWritesAreRefusedAndReadsGo()
    {
        await using FakeGame game = HubGame();
        game.WorldId = "";
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply write = await link.CallAsync(new GameCall("write", Json(WriteOn)), default);
        GameReply read = await link.CallAsync(new GameCall("read", Json(ReadOn)), default);

        Assert.Equal("world_changed", Assert.IsType<GameReply.Refused>(write).Code);
        Assert.Empty(game.CallsTo("write"));
        Assert.IsType<GameReply.Ok>(read);
        Assert.False(Assert.Single(game.CallsTo("read")).Message.TryGetProperty("world", out _));
    }

    [Fact]
    public async Task AReadBrokenTwiceIsNotSentAThirdTime()
    {
        await using FakeGame game = HubGame();
        game.Answer = call =>
        {
            if (call.Method != "read")
            {
                return Task.FromResult<string?>(call.Ok("{}"));
            }

            game.DropAll();
            return Task.FromResult<string?>(null);
        };
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply.Unreachable unreachable = Assert.IsType<GameReply.Unreachable>(
            await link.CallAsync(new GameCall("read", Json(ReadOn)), default));

        Assert.False(unreachable.MaybeRan);
        Assert.Equal(2, game.CallsTo("read").Count());
    }

    [Fact]
    public async Task ACallNotYetWrittenAtABreakGoesOnTheNextConnection()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerConnection = 1;
        TaskCompletionSource secondQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            if (call.Params.TryGetProperty("n", out JsonElement n) && n.GetInt32() == 1)
            {
                await secondQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                game.DropAll();
                return null;
            }

            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        Task<GameReply> first = link.CallAsync(new GameCall("write", Json("""{"reference_id":"1","logic_type":"On","value":1,"n":1}""")), default);
        await Eventually(() => game.Calls.Count == 1);
        Task<GameReply> second = link.CallAsync(new GameCall("write", Json("""{"reference_id":"1","logic_type":"On","value":1,"n":2}""")), default);
        secondQueued.SetResult();

        Assert.True(Assert.IsType<GameReply.Unreachable>(await first).MaybeRan);
        Assert.IsType<GameReply.Ok>(await second);
        Assert.Equal(2, game.CallsTo("write").Last().Connection);
    }

    [Fact]
    public async Task ACallWithNoReplyInTimeIsCancelledAndKeepsItsSlotUntilTheLateReply()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerStream = 1;
        TaskCompletionSource late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            if (call.Params.TryGetProperty("n", out JsonElement n) && n.GetInt32() == 1)
            {
                await late.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target) { ConnectTimeout = TimeSpan.FromMilliseconds(200) })
        {
            ReplyGrace = TimeSpan.Zero
        };
        Assert.True(await link.ConnectAsync());

        GameReply timedOut = await link.CallAsync(new GameCall("write",
            Json("""{"reference_id":"1","logic_type":"On","value":1,"n":1}"""), 5) { DeadlineMs = 100 }, default);
        Task<GameReply> next = link.CallAsync(new GameCall("weather", Json("{}"), 5), default);
        await Task.Delay(200);

        Assert.True(Assert.IsType<GameReply.Unreachable>(timedOut).MaybeRan);
        Assert.Contains(game.Received, message => message.GetProperty("type").GetString() == "cancel");
        Assert.Empty(game.CallsTo("weather"));
        late.SetResult();
        Assert.IsType<GameReply.Ok>(await next);
    }

    [Fact]
    public async Task ACallThatWaitedPastItsTimeForASlotIsNotSent()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerConnection = 1;
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            await open.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target) { ConnectTimeout = TimeSpan.FromMilliseconds(200) })
        {
            ReplyGrace = TimeSpan.Zero
        };
        Assert.True(await link.ConnectAsync());

        Task<GameReply> holder = link.CallAsync(new GameCall("weather", Json("{}"), 1), default);
        await Eventually(() => game.Calls.Count == 1);
        GameReply late = await link.CallAsync(new GameCall("write", Json(WriteOn), 2) { DeadlineMs = 100 }, default);

        Assert.False(Assert.IsType<GameReply.Unreachable>(late).MaybeRan);
        open.SetResult();
        await holder;
        Assert.Empty(game.CallsTo("write"));
    }

    [Fact]
    public async Task TheReplyTimeCountsFromTheWrite()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerConnection = 1;
        game.Answer = async call =>
        {
            await Task.Delay(call.Method == "weather" ? 500 : 300);
            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target) { ConnectTimeout = TimeSpan.FromMilliseconds(200) })
        {
            ReplyGrace = TimeSpan.Zero
        };
        Assert.True(await link.ConnectAsync());

        Task<GameReply> holder = link.CallAsync(new GameCall("weather", Json("{}"), 1), default);
        await Eventually(() => game.Calls.Count == 1);
        GameReply reply = await link.CallAsync(new GameCall("write", Json(WriteOn), 2) { DeadlineMs = 400 }, default);

        Assert.IsType<GameReply.Ok>(reply);
        await holder;
    }

    [Fact]
    public async Task NothingIsWrittenAfterSessionEnd()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerConnection = 1;
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            await open.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return call.Ok("{}");
        };
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());
        Task<GameReply> running = link.CallAsync(new GameCall("weather", Json("{}"), 1), default);
        await Eventually(() => game.Calls.Count == 1);
        Task<GameReply> waiting = link.CallAsync(new GameCall("write", Json(WriteOn), 2), default);

        await link.SessionEndAsync();

        Assert.False(Assert.IsType<GameReply.Unreachable>(await waiting).MaybeRan);
        Assert.False(Assert.IsType<GameReply.Unreachable>(
            await link.CallAsync(new GameCall("read", Json(ReadOn)), default)).MaybeRan);
        open.SetResult();
        Assert.IsType<GameReply.Ok>(await running);
        Assert.Single(game.Calls);
        await Eventually(() => game.Received.Any(message => message.GetRawText() == """{"type":"session_end"}"""));
    }

    [Fact]
    public async Task SessionEndIsNotHeldBehindAReconnectInProgress()
    {
        await using FakeGame game = HubGame();
        game.WelcomeDelay = number => number == 2 ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        game.DropAll();
        await Eventually(() => link.IsConnecting && game.Connections == 2);
        Stopwatch clock = Stopwatch.StartNew();
        await link.SessionEndAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"session_end took {clock.Elapsed}");
        await Eventually(() => game.Received.Any(message => message.GetRawText() == """{"type":"session_end"}"""));
    }

    [Fact]
    public async Task ByeIsSentOnCloseOnlyAfterSessionEnd()
    {
        await using FakeGame game = HubGame();
        GameLink plain = new(new ClientOptions(game.Target));
        Assert.True(await plain.ConnectAsync());
        await plain.DisposeAsync();
        await Eventually(() => game.OpenConnections == 0);
        Assert.DoesNotContain(game.Received, message => message.GetProperty("type").GetString() == "bye");

        GameLink ended = new(new ClientOptions(game.Target));
        Assert.True(await ended.ConnectAsync());
        await ended.SessionEndAsync();
        await ended.DisposeAsync();
        await Eventually(() => game.Received.Any(message => message.GetProperty("type").GetString() == "bye"));
    }

    [Fact]
    public async Task CloseDuringAConnectClosesTheNewConnection()
    {
        await using FakeGame game = HubGame();
        game.WelcomeDelay = _ => TimeSpan.FromMilliseconds(500);
        GameLink link = new(new ClientOptions(game.Target));

        Task<bool> connecting = link.ConnectAsync();
        await Eventually(() => game.Connections == 1);
        await link.DisposeAsync();

        Assert.False(await connecting);
        await Eventually(() => game.OpenConnections == 0);
        Assert.Null(link.WorldId);
    }

    [Fact]
    public async Task CloseFailsWaitingCallsAndJobWaitsAndLaterCallsAtOnce()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("subscriptions");
        game.MaxInFlightPerConnection = 1;
        game.Answer = _ => Task.FromResult<string?>(null);
        GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());
        _ = link.CallAsync(new GameCall("weather", Json("{}"), 1), default);
        await Eventually(() => game.Calls.Count == 1);
        Task<GameReply> waiting = link.CallAsync(new GameCall("weather", Json("{}"), 2), default);
        Task<JsonElement?> job = link.WaitJobAsync("run-9", TimeSpan.FromSeconds(30), default);

        await link.DisposeAsync();

        Assert.False(Assert.IsType<GameReply.Unreachable>(await waiting.WaitAsync(TimeSpan.FromSeconds(2))).MaybeRan);
        Assert.Null(await job.WaitAsync(TimeSpan.FromSeconds(2)));
        Task<GameReply> after = link.CallAsync(new GameCall("weather", Json("{}")), default);
        Assert.IsType<GameReply.Unreachable>(await after.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task AJobWaitAnswersNullWhenTheLinkReconnects()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        Assert.True(await link.ConnectAsync());

        Task<JsonElement?> waiting = link.WaitJobAsync("run-44", TimeSpan.FromSeconds(30), default);
        game.DropAll();

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TheLinkComesBackAfterBreaksRightAfterEachWelcome()
    {
        await using FakeGame game = HubGame();
        game.DropAfterWelcome = number => number <= 5;
        await using GameLink link = new(new ClientOptions(game.Target));

        await link.ConnectAsync();

        await Eventually(() => game.Connections >= 6 && link.WorldId != null);
        Assert.IsType<GameReply.Ok>(await link.CallAsync(new GameCall("game_clock", Json("{}")), default));
    }

    [Fact]
    public async Task ThrowingHandlersDoNotEndTheConnection()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        ConcurrentQueue<string?> worlds = new();
        link.EventReceived += _ => throw new InvalidOperationException("handler");
        link.WorldChanged += _ => throw new InvalidOperationException("handler");
        link.WorldChanged += worlds.Enqueue;
        Assert.True(await link.ConnectAsync());

        await game.PushAsync("""{"type":"event","event":"update","subscription":"s4","seq":1,"result":{}}""");
        await game.PushAsync("""{"type":"event","event":"world_changed","world":{"id":"world-2","save":"x","epoch":1}}""");
        await Eventually(() => worlds.Contains("world-2"));

        Assert.IsType<GameReply.Ok>(await link.CallAsync(new GameCall("game_clock", Json("{}")), default));
        Assert.Equal(1, game.Connections);
    }

    [Fact]
    public async Task WorldChangedAfterAReconnectIsRaisedOutsideTheConnectLock()
    {
        await using FakeGame game = HubGame();
        await using GameLink link = new(new ClientOptions(game.Target));
        ConcurrentQueue<bool> connecting = new();
        link.WorldChanged += _ => connecting.Enqueue(link.IsConnecting);
        Assert.True(await link.ConnectAsync());

        game.WorldId = "world-2";
        game.DropAll();

        await Eventually(() => !connecting.IsEmpty);
        Assert.Equal([false], connecting);
    }

    [Fact]
    public async Task TopicsAndWorldChangeDoNotWaitForACatalogueFetchOrTheCallersToken()
    {
        await using FakeGame game = HubGame();
        game.CatalogueHash = "sha256:" + new string('c', 64);
        game.CatalogueDelay = TimeSpan.FromSeconds(3);
        await using GameLink link = new(new ClientOptions(game.Target));
        ConcurrentQueue<string?> worlds = new();
        link.WorldChanged += worlds.Enqueue;
        using CancellationTokenSource caller = new();

        Stopwatch clock = Stopwatch.StartNew();
        Assert.True(await link.ConnectAsync(caller.Token));
        caller.Cancel();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"connecting took {clock.Elapsed}");
        await Eventually(() => Topics(game).Count == 2);

        game.WorldId = "world-2";
        game.DropAll();
        await Eventually(() => worlds.Contains("world-2"));
        await Eventually(() => Topics(game).Count == 4);
    }

    [Fact]
    public async Task TheModsCatalogueIsTakenWhenItsHashDiffers()
    {
        await using FakeGame game = HubGame();
        game.CatalogueHash = "sha256:" + new string('d', 64);
        game.CatalogueJson = ClientTests.ModelWithout("read");
        await using GameLink link = new(new ClientOptions(game.Target));

        Assert.True(await link.ConnectAsync());

        await Eventually(() => link.Catalogue.Hash == game.CatalogueHash);
        Assert.False(link.Catalogue.Has("read"));
        Assert.Single(game.CallsTo("catalogue"));
    }

    [Fact]
    public async Task HoldRestoreIsRefusedWithoutStreams()
    {
        await using FakeGame game = HubGame();
        game.Features.Remove("streams");
        await using GameLink link = new(new ClientOptions(game.Target));

        GameReply reply = await link.HoldRestoreAsync(3, Json("""{"clear_all":true}"""), default);

        Assert.Equal("not_supported", Assert.IsType<GameReply.Refused>(reply).Code);
        Assert.Empty(game.CallsTo("hold_restore"));
    }

    private const string WriteOn = """{"what":"logic","id":"1","logic":"On","value":1,"confirm":true}""";

    private static FakeGame HubGame()
    {
        FakeGame game = FakeGame.OnPipe();
        game.Features = ["shape", "shape.paths", "shape.omit", "subscriptions", "cancel", "streams", "resume"];
        return game;
    }

    private static long StreamOf(FakeCall call) =>
        call.Message.TryGetProperty("stream", out JsonElement stream) ? stream.GetInt64() : 0;

    private static List<string> Topics(FakeGame game) =>
        game.CallsTo("subscribe").Select(call => call.Params.GetProperty("topic").GetString()!).ToList();

    private static string JobEnded(string jobId, string status) =>
        "{\"type\":\"event\",\"event\":\"job_ended\",\"subscription\":\"s2\",\"seq\":1,\"frame\":9,\"job\":{\"job_id\":\"" +
        jobId + "\",\"tool\":\"place_pipes\",\"status\":\"" + status + "\"}}";

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
