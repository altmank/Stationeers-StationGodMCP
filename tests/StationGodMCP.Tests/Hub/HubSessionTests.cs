#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

public sealed class HubSessionTests
{
    [Fact]
    public async Task HelloIsAnsweredWithTheModsWelcomeTheCallersIdAndPipeline()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link, new HubSessionSettings(Face.Program) { PassThrough = true, ClientId = "program-1" });

        JsonElement welcome = await caller.HelloAsync();

        Assert.Equal("welcome", welcome.GetProperty("type").GetString());
        Assert.Equal(2, welcome.GetProperty("protocol").GetInt32());
        Assert.Equal("program-1", welcome.GetProperty("client_id").GetString());
        Assert.Equal("Running", welcome.GetProperty("server").GetProperty("game_state").GetString());
        Assert.Equal("world-1", welcome.GetProperty("server").GetProperty("world").GetProperty("id").GetString());
        Assert.Equal(new[] { "shape", "subscriptions", "cancel", "streams", "cursors", "pipeline" },
            welcome.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        Assert.False(welcome.TryGetProperty("resume", out _));
        Assert.False(welcome.TryGetProperty("run", out _));
    }

    [Fact]
    public async Task BeforeTheGameIsReachableTheWelcomeSaysUnreachableAndCallsAnswerGameUnreachable()
    {
        FakeHubLink link = new() { Reachable = false, Welcome = null };
        await using HubCaller caller = HubCaller.Start(link);

        JsonElement welcome = await caller.HelloAsync();
        JsonElement reply = await caller.CallAsync("1", "find", """{"what":"thing","name":"5"}""");

        Assert.Equal("Unreachable", welcome.GetProperty("server").GetProperty("game_state").GetString());
        Assert.Equal(new[] { "pipeline" }, welcome.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("game_unreachable", reply.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AModMethodGoesOnTheCallersMappedStreamAndItsReplyComesBackUnderTheCallersId()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(FakeGameLink.Ok("""{"stream":""" + call.Stream + "}"))
        };
        await using HubCaller first = HubCaller.Start(link);
        await using HubCaller second = HubCaller.Start(link);
        await first.HelloAsync();
        await second.HelloAsync();

        JsonElement firstZero = await first.CallAsync("a", "find", """{"what":"thing","name":"5"}""");
        JsonElement secondZero = await second.CallAsync("a", "find", """{"what":"thing","name":"5"}""");
        JsonElement firstThree = await first.CallAsync("b", "find", """{"what":"thing"}""", stream: 3);
        JsonElement firstThreeAgain = await first.CallAsync("c", "find", """{"what":"thing"}""", stream: 3);

        Assert.Equal("reply", firstZero.GetProperty("type").GetString());
        Assert.Equal("a", firstZero.GetProperty("id").GetString());
        Assert.True(firstZero.GetProperty("ok").GetBoolean());
        Assert.Equal(1, firstZero.GetProperty("frame").GetInt64());
        long[] streams =
        [
            StreamOf(firstZero), StreamOf(secondZero), StreamOf(firstThree)
        ];
        Assert.DoesNotContain(0L, streams);
        Assert.Equal(3, streams.Distinct().Count());
        Assert.Equal(first.Session.MainStream, StreamOf(firstZero));
        Assert.Equal(StreamOf(firstThree), StreamOf(firstThreeAgain));
        Assert.Equal("""{"what":"thing","name":"5"}""", link.CallsTo("find")[0].Parameters.GetRawText());
    }

    [Fact]
    public async Task AnIdStillInFlightIsRefusedDuplicateIdAndTheFirstCallIsUnaffected()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHubLink link = new()
        {
            Answer = async (_, _) =>
            {
                await release.Task;
                return FakeGameLink.Ok("""{"value":1}""");
            }
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        caller.Send(HubCaller.Call("7", "read"));
        JsonElement duplicate = await caller.CallAsync("7", "read");
        release.SetResult();
        JsonElement first = await caller.NextAsync();

        Assert.Equal("7", duplicate.GetProperty("id").GetString());
        Assert.Equal("duplicate_id", duplicate.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, first.GetProperty("result").GetProperty("value").GetInt32());
        Assert.Single(link.CallsTo("read"));
    }

    [Fact]
    public async Task CancelCancelsTheCallInFlight()
    {
        FakeHubLink link = new()
        {
            Answer = async (_, cancellation) =>
            {
                await Task.Delay(Timeout.Infinite, cancellation);
                return FakeGameLink.Ok("{}");
            }
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        caller.Send(HubCaller.Call("9", "read", """{"what":"sample","id":"5","logic":"On"}"""));
        await WaitUntil(() => link.CallsTo("read").Count == 1);
        caller.Send("""{"type":"cancel","id":"9"}""");
        JsonElement reply = await caller.NextAsync();

        Assert.Equal("9", reply.GetProperty("id").GetString());
        Assert.Equal("cancelled", reply.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StreamEndEndsTheMappedHubStreamAndLaterCallsOnItAreRefused()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        await caller.CallAsync("1", "find", """{"what":"thing"}""", stream: 2);
        long mapped = link.CallsTo("find")[0].Stream;

        caller.Send("""{"type":"stream_end","stream":2}""");
        JsonElement refused = await caller.CallAsync("2", "find", """{"what":"thing"}""", stream: 2);

        Assert.Equal(new[] { mapped }, link.EndedStreams);
        Assert.Equal("stream_ended", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, refused.GetProperty("error").GetProperty("data").GetProperty("stream").GetInt64());
        Assert.Single(link.CallsTo("find"));

        await caller.EndAsync();
        // The main stream carried no call, so the close has nothing to end.
        Assert.Equal(new[] { mapped }, link.EndedStreams);
    }

    [Fact]
    public async Task AnUnknownKeyIsAProtocolErrorAndEndsTheSession()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        caller.Send("""{"type":"call","id":"1","method":"find","colour":"red"}""");
        JsonElement unknown = await caller.NextAsync();

        Assert.Equal("protocol_error", unknown.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("colour", unknown.GetProperty("error").GetProperty("message").GetString());
        Assert.StartsWith("{\"type\":\"call\"", unknown.GetProperty("error").GetProperty("data").GetProperty("line_start").GetString());
        Assert.Equal("1", unknown.GetProperty("id").GetString());
        Assert.Equal(HubSessionEnd.ProtocolError, await caller.Running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task SubscriptionsAreRelayedUnderTheCallersIdAndEndedOnClose()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Method == "subscribe"
                ? FakeGameLink.Ok("""{"subscription":"s40","interval_s":1.0,"values":1,"frame":5,"result":{"results":[]}}""")
                : FakeGameLink.Ok("""{"ended":true}"""))
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        JsonElement subscribed = await caller.CallAsync("1", "subscribe", """{"id":"5","logic":["On"]}""");
        link.Raise("""{"type":"event","event":"update","subscription":"s99","seq":1,"result":{}}""");
        link.Raise("""{"type":"event","event":"update","subscription":"s40","seq":1,"frame":9,"result":{"results":[1]}}""");
        JsonElement update = await caller.NextAsync();
        await caller.EndAsync();

        Assert.Equal("s1", subscribed.GetProperty("result").GetProperty("subscription").GetString());
        Assert.Equal(1.0, subscribed.GetProperty("result").GetProperty("interval_s").GetDouble());
        Assert.Equal("update", update.GetProperty("event").GetString());
        Assert.Equal("s1", update.GetProperty("subscription").GetString());
        Assert.Equal(9, update.GetProperty("frame").GetInt64());
        GameCall subscribe = Assert.Single(link.CallsTo("subscribe"));
        Assert.Equal(caller.Session.MainStream, subscribe.Stream);
        GameCall unsubscribe = Assert.Single(link.CallsTo("unsubscribe"));
        Assert.Equal("""{"subscription":"s40"}""", unsubscribe.Parameters.GetRawText());
    }

    [Fact]
    public async Task UnsubscribeEndsTheModSubscriptionAndAnotherSessionsIdIsUnknown()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Method == "subscribe"
                ? FakeGameLink.Ok("""{"subscription":"s40","interval_s":1.0,"values":1,"frame":5,"result":{}}""")
                : FakeGameLink.Ok("""{"subscription":"s40","ended":true}"""))
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        await caller.CallAsync("1", "subscribe", "{}");

        JsonElement unknown = await caller.CallAsync("2", "unsubscribe", """{"subscription":"s40"}""");
        JsonElement ended = await caller.CallAsync("3", "unsubscribe", """{"subscription":"s1"}""");
        await caller.EndAsync();

        Assert.Equal("unknown_subscription", unknown.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("s1", ended.GetProperty("result").GetProperty("subscription").GetString());
        Assert.Equal("""{"subscription":"s40"}""", Assert.Single(link.CallsTo("unsubscribe")).Parameters.GetRawText());
    }

    [Fact]
    public async Task VerbGoesThroughThePipelineOnTheProgramFaceAndTheSessionsStream()
    {
        FakeHubLink link = new();
        FaceGate gate = new();
        await using HubCaller caller = HubCaller.Start(link, gate: gate);
        await caller.HelloAsync();

        JsonElement written = await caller.CallAsync("1", "verb",
            """{"verb":"write","what":"logic","args":{"id":"1","logic":"On","value":1}}""");
        JsonElement refused = await caller.CallAsync("2", "verb", """{"verb":"write","what":"nonsense"}""");

        Assert.True(written.GetProperty("ok").GetBoolean());
        Assert.Equal(Face.Program, Assert.Single(gate.Faces));
        GameCall sent = Assert.Single(link.Calls);
        Assert.Equal(caller.Session.MainStream, sent.Stream);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_argument", refused.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AFailedVerbAnswersTheHubErrorObject()
    {
        FakeHubLink link = new()
        {
            Answer = (_, _) => Task.FromResult<GameReply>(new GameReply.Unreachable("The pipe broke.", maybeRan: true))
        };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        JsonElement reply = await caller.CallAsync("1", "verb", """{"verb":"write","what":"logic","args":{"id":"1","logic":"On","value":1}}""");

        JsonElement error = reply.GetProperty("error");
        Assert.Equal("game_unavailable", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("maybe_ran").GetBoolean());
    }

    [Fact]
    public async Task RefMoreWaitAndHoldAnswerNotAvailableUntilExtrasAreGiven()
    {
        FakeHubLink link = new();
        await using HubCaller plain = HubCaller.Start(link);
        await using HubCaller extended = HubCaller.Start(link, new HubSessionSettings(Face.Program) { PassThrough = true, Extras = new EchoExtras() });
        await plain.HelloAsync();
        await extended.HelloAsync();

        List<string> codes = [];
        foreach (string method in new[] { "ref", "more", "wait", "hold" })
        {
            codes.Add((await plain.CallAsync(method, method, """{"id":"r1"}""")).GetProperty("error").GetProperty("code").GetString()!);
        }

        JsonElement echoed = await extended.CallAsync("1", "wait", """{"s":1,"kind":"wait","in_loop":false,"line":4}""");

        Assert.Equal(new[] { "not_available", "not_available", "not_available", "not_available" }, codes);
        Assert.Equal("wait", echoed.GetProperty("result").GetProperty("method").GetString());
        Assert.Equal(4, echoed.GetProperty("result").GetProperty("line").GetInt32());
        Assert.Equal(extended.Session.MainStream, echoed.GetProperty("result").GetProperty("stream").GetInt64());
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task RunMessagesAreAProtocolErrorWithoutARunChannel()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();

        caller.Send("""{"type":"run_analysis","literals":[],"while_lines":[],"writes":false,"holds":false,"syntax_error":null}""");
        JsonElement refused = await caller.NextAsync();

        Assert.Equal("protocol_error", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HubSessionEnd.ProtocolError, await caller.Running.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ARunsSessionCarriesItsRunInTheWelcomeAndTakesOneAnalysisAndItsEnd()
    {
        FakeHubLink link = new();
        RecordingRun run = new();
        await using HubCaller caller = HubCaller.Start(link, new HubSessionSettings(Face.Script)
        {
            Run = new JsonObject { ["id"] = "script-4", ["face"] = "script" },
            RunChannel = run,
            MainStream = 41
        });

        JsonElement welcome = await caller.HelloAsync();
        caller.Send("""{"type":"run_analysis","literals":["122643"],"while_lines":[],"writes":true,"holds":false,"syntax_error":null}""");
        caller.Send("""{"type":"run_end","state":"done","returned":null,"printed":"","error":null}""");
        JsonElement afterEnd = await caller.CallAsync("1", "wait", """{"s":1,"kind":"wait","in_loop":false}""");
        caller.Send("""{"type":"run_analysis","literals":[],"while_lines":[],"writes":false,"holds":false,"syntax_error":null}""");
        JsonElement second = await caller.NextAsync();
        await caller.EndAsync();

        Assert.Equal("script-4", welcome.GetProperty("run").GetProperty("id").GetString());
        Assert.Equal(new[] { "run_analysis", "run_end" }, run.Messages);
        Assert.Equal("session_ended", afterEnd.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("protocol_error", second.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(link.EndedStreams);
    }

    [Fact]
    public async Task AScriptSessionRefusesModMethodsAndSendsNothing()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link, new HubSessionSettings(Face.Script)
        {
            Run = new JsonObject { ["id"] = "script-5" },
            RunChannel = new RecordingRun(),
            MainStream = 7
        });
        await caller.HelloAsync();
        caller.Send("""{"type":"run_analysis","literals":[],"while_lines":[],"writes":true,"holds":false,"syntax_error":null}""");

        List<string> codes = [];
        foreach (string method in new[] { "write", "drop_job", "hold_restore", "stream_costs" })
        {
            codes.Add((await caller.CallAsync(method, method, "{}")).GetProperty("error").GetProperty("code").GetString()!);
        }

        caller.Send("""{"type":"stream_end","stream":3}""");
        JsonElement streamEnd = await caller.NextAsync();
        caller.Send("""{"type":"session_end"}""");
        JsonElement sessionEnd = await caller.NextAsync();
        JsonElement otherStream = await caller.CallAsync("w", "wait", """{"s":1,"kind":"wait","in_loop":false}""", stream: 2);

        Assert.All(codes, code => Assert.Equal("not_allowed", code));
        Assert.Equal("not_allowed", streamEnd.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("not_allowed", sessionEnd.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalid_argument", otherStream.GetProperty("error").GetProperty("code").GetString());
        Assert.False(link.SessionEnded);
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task AScriptSessionSubscribesAndItsUpdatesReachTheRun()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Method == "subscribe"
                ? FakeGameLink.Ok("""{"subscription":"s40","interval_s":1.0,"values":1,"frame":5,"result":{"results":[1]}}""")
                : FakeGameLink.Ok("""{"subscription":"s40","ended":true}"""))
        };
        await using HubCaller caller = await ScriptSession(link);

        JsonElement subscribed = await caller.CallAsync("1", "subscribe", DeviceReads);
        link.Raise("""{"type":"event","event":"update","subscription":"s40","seq":1,"frame":9,"result":{"results":[1]}}""");
        JsonElement update = await caller.NextAsync();
        JsonElement unsubscribed = await caller.CallAsync("2", "unsubscribe", """{"subscription":"s1"}""");

        Assert.Equal("s1", subscribed.GetProperty("result").GetProperty("subscription").GetString());
        Assert.Equal("update", update.GetProperty("event").GetString());
        Assert.Equal("s1", update.GetProperty("subscription").GetString());
        Assert.Equal("s1", unsubscribed.GetProperty("result").GetProperty("subscription").GetString());
        GameCall subscribe = Assert.Single(link.CallsTo("subscribe"));
        Assert.Equal(7, subscribe.Stream);  // the run's own stream, not a hub stream of its own
        Assert.Equal(DeviceReads, subscribe.Parameters.GetRawText());
    }

    [Fact]
    public async Task ARunsEndEndsItsSubscription()
    {
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Method == "subscribe"
                ? FakeGameLink.Ok("""{"subscription":"s40","interval_s":1.0,"values":1,"frame":5,"result":{}}""")
                : FakeGameLink.Ok("""{"subscription":"s40","ended":true}"""))
        };
        await using HubCaller caller = await ScriptSession(link);
        await caller.CallAsync("1", "subscribe", DeviceReads);

        await caller.EndAsync();

        GameCall unsubscribe = Assert.Single(link.CallsTo("unsubscribe"));
        Assert.Equal("""{"subscription":"s40"}""", unsubscribe.Parameters.GetRawText());
    }

    [Fact]
    public async Task AJobEndedReachesOnlyTheRunThatStartedTheJob()
    {
        int next = 40;
        FakeHubLink link = new()
        {
            Answer = (call, _) => Task.FromResult(call.Method == "subscribe"
                ? FakeGameLink.Ok("{\"subscription\":\"s" + next++ + "\",\"interval_s\":1.0,\"values\":0,\"frame\":5}")
                : FakeGameLink.Ok("""{"subscription":"s40","ended":true}"""))
        };
        RecordingRun mine = new();
        mine.Jobs.Add("run-3");
        await using HubCaller own = await ScriptSession(link, mine);
        await using HubCaller other = await ScriptSession(link);
        await using HubCaller program = HubCaller.Start(link);
        await program.HelloAsync();
        await own.CallAsync("1", "subscribe", """{"topic":"jobs"}""");
        await other.CallAsync("1", "subscribe", """{"topic":"jobs"}""");
        await program.CallAsync("1", "subscribe", """{"topic":"jobs"}""");

        link.Raise("""{"type":"event","event":"job_ended","subscription":"s40","seq":1,"frame":9,"job":{"job_id":"run-3","tool":"build"}}""");
        link.Raise("""{"type":"event","event":"job_ended","subscription":"s40","seq":2,"frame":10,"job":{"job_id":"run-9"}}""");
        link.Raise("""{"type":"event","event":"job_ended","subscription":"s41","seq":1,"frame":10,"job":{"job_id":"run-3"}}""");
        link.Raise("""{"type":"event","event":"job_ended","subscription":"s42","seq":1,"frame":10,"job":{"job_id":"run-9"}}""");
        link.Raise("""{"type":"event","event":"update","subscription":"s41","seq":1,"frame":11,"result":{"results":[]}}""");

        List<string> ownLines = await own.LastLinesWithinAsync(TimeSpan.FromMilliseconds(300));
        List<string> otherLines = await other.LastLinesWithinAsync(TimeSpan.FromMilliseconds(300));
        List<string> programLines = await program.LastLinesWithinAsync(TimeSpan.FromMilliseconds(300));

        JsonElement ownJob = FakeHubLink.Element(Assert.Single(ownLines));
        Assert.Equal("job_ended", ownJob.GetProperty("event").GetString());
        Assert.Equal("s1", ownJob.GetProperty("subscription").GetString());
        Assert.Equal("run-3", ownJob.GetProperty("job").GetProperty("job_id").GetString());
        JsonElement otherUpdate = FakeHubLink.Element(Assert.Single(otherLines));
        Assert.Equal("update", otherUpdate.GetProperty("event").GetString());
        JsonElement programJob = FakeHubLink.Element(Assert.Single(programLines));
        Assert.Equal("job_ended", programJob.GetProperty("event").GetString());
        Assert.Equal("run-9", programJob.GetProperty("job").GetProperty("job_id").GetString());
    }

    // A run's session with its analysis sent, as Run builds it: MainStream 7, a script's Face.
    private static async Task<HubCaller> ScriptSession(FakeHubLink link, IRunChannel? channel = null)
    {
        HubCaller caller = HubCaller.Start(link, new HubSessionSettings(Face.Script)
        {
            Run = new JsonObject { ["id"] = "script-5" },
            RunChannel = channel ?? new RecordingRun(),
            MainStream = 7
        });
        await caller.HelloAsync();
        caller.Send("""{"type":"run_analysis","literals":[],"while_lines":[],"writes":true,"holds":false,"syntax_error":null}""");
        return caller;
    }

    private const string DeviceReads = """{"reads":[{"id":"5","logic":["On"]}],"interval_s":1}""";

    [Fact]
    public async Task ARunsFirstCallBeforeItsAnalysisIsAProtocolError()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link, new HubSessionSettings(Face.Script)
        {
            Run = new JsonObject { ["id"] = "script-6" },
            RunChannel = new RecordingRun(),
            MainStream = 8
        });
        await caller.HelloAsync();

        JsonElement early = await caller.CallAsync("1", "wait", """{"s":1,"kind":"wait","in_loop":false}""");

        Assert.Equal("protocol_error", early.GetProperty("error").GetProperty("code").GetString());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HubSession(link, new VerbPipeline(link), new HubSessionSettings(Face.Script) { MainStream = 0 }));
    }

    [Fact]
    public async Task ACursorIsClosedOnlyWhenThisSessionsReplyCarriedIt()
    {
        FakeHubLink link = new() { Answer = (_, _) => Task.FromResult(FakeGameLink.Ok("""{"things":[],"cursor_id":"k12"}""")) };
        await using HubCaller caller = HubCaller.Start(link);
        await caller.HelloAsync();
        await caller.CallAsync("1", "find", """{"what":"thing","cursor":true}""");

        caller.Send("""{"type":"cursor_close","cursor_id":"k99"}""");
        caller.Send("""{"type":"cursor_close","cursor_id":"k12"}""");
        await caller.CallAsync("2", "find");

        Assert.Equal(new[] { "k12" }, link.ClosedCursors);
    }

    [Fact]
    public async Task ACallBeforeHelloIsAProtocolErrorAndAnOldProtocolIsUnsupported()
    {
        FakeHubLink link = new();
        await using HubCaller early = HubCaller.Start(link);
        await using HubCaller old = HubCaller.Start(link);

        JsonElement beforeHello = await early.CallAsync("1", "find");
        old.Send("""{"type":"hello","protocol":[1],"client":{"name":"old","version":"1"}}""");
        JsonElement unsupported = await old.NextAsync();

        Assert.Equal("protocol_error", beforeHello.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("unsupported_protocol", unsupported.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, unsupported.GetProperty("id").ValueKind);
        Assert.Equal(HubSessionEnd.ProtocolError, await old.Running.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task SessionEndFromTheHubCallerEndsTheLinksSession()
    {
        FakeHubLink link = new();
        await using HubCaller caller = HubCaller.Start(link, new HubSessionSettings(Face.Program) { PassThrough = true, EndsLinkSession = true });
        await caller.HelloAsync();

        caller.Send("""{"type":"session_end"}""");
        JsonElement refused = await caller.CallAsync("1", "find");

        Assert.True(link.SessionEnded);
        Assert.True(caller.Session.SessionEndSent);
        Assert.Equal("session_ended", refused.GetProperty("error").GetProperty("code").GetString());
    }

    private static long StreamOf(JsonElement reply) => reply.GetProperty("result").GetProperty("stream").GetInt64();

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }

    private sealed class FaceGate : IWriteGate
    {
        public List<Face> Faces { get; } = [];

        public WriteDecision Decide(VerbCall call, WriteTargets targets)
        {
            Faces.Add(call.Face);
            return new WriteDecision.Act();
        }
    }

    private sealed class EchoExtras : IPipelineExtras
    {
        public Task<VerbReply> RefAsync(PipelineRequest request, CancellationToken cancellation) => Echo(request);

        public Task<VerbReply> MoreAsync(PipelineRequest request, CancellationToken cancellation) => Echo(request);

        public Task<VerbReply> WaitAsync(PipelineRequest request, CancellationToken cancellation) => Echo(request);

        public Task<VerbReply> HoldAsync(PipelineRequest request, CancellationToken cancellation) => Echo(request);

        private static Task<VerbReply> Echo(PipelineRequest request) => Task.FromResult<VerbReply>(new VerbReply.Done(
            new JsonObject { ["method"] = request.Method, ["line"] = request.Line, ["stream"] = request.Stream }));
    }

    private sealed class RecordingRun : IRunChannel
    {
        public List<string> Messages { get; } = [];

        public HashSet<string> Jobs { get; } = [];

        public bool StartedJob(string jobId) => Jobs.Contains(jobId);

        public Task AnalysisAsync(JsonElement message)
        {
            Messages.Add(message.GetProperty("type").GetString()!);
            return Task.CompletedTask;
        }

        public Task EndedAsync(JsonElement message)
        {
            Messages.Add(message.GetProperty("type").GetString()!);
            return Task.CompletedTask;
        }
    }
}
