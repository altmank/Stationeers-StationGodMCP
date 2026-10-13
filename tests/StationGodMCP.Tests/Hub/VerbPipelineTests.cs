#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// The pipeline: every call goes to the mod as its verb ({method: verb, params: {what, ...options}}) without the hub's own
/// options, a list for a one-id write is refused naming run_script, the faces' per-id lists, help pages and stubs, the
/// recording, the write gate, job waits and the placeholders for the pieces to come.
/// </summary>
public sealed class VerbPipelineTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sgm-pipeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // ---- the wire form --------------------------------------------------------------------------------------

    [Fact]
    public void ACallGoesToTheModAsItsVerbWithWhatAndItsOptions()
    {
        GameCall call = Sent("find", "thing", """{"name":"Tank","near":"player","radius":10,"fields":["reference_id"]}""");

        Assert.Equal("find", call.Method);
        Assert.Equal("""{"what":"thing","name":"Tank","near":"player","radius":10}""", call.Params().ToJsonString());
        Assert.Contains("reference_id", call.Shape!.Value.GetRawText());
    }

    [Fact]
    public void AWhatlessVerbSendsNoWhat()
    {
        GameCall call = Sent("undo", null, """{"id":"place-4","confirm":true}""");

        Assert.Equal("undo", call.Method);
        Assert.Equal("""{"id":"place-4","confirm":true}""", call.Params().ToJsonString());
    }

    [Fact]
    public void TheHubsOwnOptionsNeverReachTheMod()
    {
        GameCall call = Sent("inspect", "job", """{"id":"place-2","max_wait":0,"omit":["steps"],"list_limits":{"steps":2}}""");

        Assert.Equal("""{"what":"job","id":"place-2"}""", call.Params().ToJsonString());
    }

    [Fact]
    public void AnIdListGoesWholeInOneCall()
    {
        GameCall call = Sent("inspect", "device", """{"id":["1","2"]}""");

        Assert.Equal("""{"what":"device","id":["1","2"]}""", call.Params().ToJsonString());
    }

    [Fact]
    public void AWriteActsWithConfirmTrueAndReportsWithoutIt()
    {
        GameCall acted = Sent("write", "logic", """{"id":"1","logic":"On","value":1}""");
        FakeGameLink game = new();
        JsonNode? report = game.Pipeline().Call("remove", "pipe", """{"id":"1"}""").Done();

        Assert.True((bool)acted.Params()["confirm"]!);
        Assert.Null(game.Single("remove").Params()["confirm"]);
        Assert.True((bool)report!["report_only"]!);
    }

    [Fact]
    public void AFileOptionIsSentAsTheTextOfTheOptionItNames()
    {
        Directory.CreateDirectory(_folder);
        string file = Path.Combine(_folder, "chip.ic10");
        File.WriteAllText(file, "yield");

        GameCall call = Sent("write", "chip", $$"""{"id":"1","code_file":{{JsonValue.Create(file).ToJsonString()}}}""");

        Assert.Equal("yield", (string)call.Params()["code"]!);
        Assert.Null(call.Params()["code_file"]);
    }

    [Theory]
    [InlineData("write", "chip", """{"id":["1","2"],"run":"pause"}""")]
    [InlineData("write", "screen", """{"id":["1","2"],"element":"start"}""")]
    [InlineData("write", "uplink", """{"id":["1","2"],"downlink":"3"}""")]
    [InlineData("write", "flight_log", """{"id":["1","2"],"action":"stop"}""")]
    [InlineData("write", "memory", """{"id":["1","2"],"start":0,"values":[1]}""")]
    [InlineData("write", "genes", """{"id":["1","2"],"genes":{"a":1}}""")]
    public void AListForAOneIdWriteIsRefusedNamingRunScript(string verb, string what, string options)
    {
        FakeGameLink game = new();

        VerbReply.Failed failed = game.Pipeline().Call(verb, what, options).Failed();

        Assert.Equal("invalid_argument", failed.Code);
        Assert.Contains("run_script", Assert.Single(failed.Problems()));
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void OnTheScriptFaceOneIdOfAListWhatGoesAsAList()
    {
        FakeGameLink game = new();

        game.Pipeline().Call("inspect", "device", """{"id":"1"}""", Face.Script).Done();
        game.Pipeline().Call("inspect", "device", """{"id":"1"}""").Done();

        Assert.Equal("""["1"]""", game.Calls.First().Params()["id"]!.ToJsonString());
        Assert.Equal("\"1\"", game.Calls.Last().Params()["id"]!.ToJsonString());
    }

    // ---- help --------------------------------------------------------------------------------------------------

    [Fact]
    public void HelpServesTheGeneratedPages()
    {
        VerbPipeline pipeline = new FakeGameLink().Pipeline();

        Assert.Equal(Page("help", "_index.txt"), (string)pipeline.Call("help", null).Done()!["text"]!);
        Assert.Equal(Page("help", "find.txt"), (string)pipeline.Call("help", null, """{"verb":"find"}""").Done()!["text"]!);
        Assert.Equal(Page("help", "write.logic.txt"), (string)pipeline.Call("help", "logic", """{"verb":"write"}""").Done()!["text"]!);
        Assert.Equal(Page("help", "undo.txt"), (string)pipeline.Call("help", null, """{"verb":"undo"}""").Done()!["text"]!);
        Assert.Equal(Page("help", "topic.paging.txt"), (string)pipeline.Call("help", null, """{"topic":"paging"}""").Done()!["text"]!);
        Assert.Equal(Page("help", "topic.types.BuildJob.txt"),
            (string)pipeline.Call("help", null, """{"topic":"types","subtopic":"BuildJob"}""").Done()!["text"]!);
    }

    [Fact]
    public void HelpInPythonServesTheStub()
    {
        VerbPipeline pipeline = new FakeGameLink().Pipeline();

        JsonNode? stub = pipeline.Call("help", null, """{"verb":"write","what":"logic","lang":"python"}""").Done();

        Assert.Equal(Page("stubs", "write.logic.py"), (string)stub!["text"]!);
        Assert.Equal("python", (string)stub["lang"]!);
        Assert.Equal(Page("stubs", "undo.py"), (string)pipeline.Call("help", null, """{"verb":"undo","lang":"python"}""").Done()!["text"]!);
        Assert.Equal("help_not_found", pipeline.Call("help", null, """{"verb":"write","lang":"python"}""").Failed().Code);
    }

    [Fact]
    public void AHelpMissNamesTheNearest()
    {
        VerbReply.Failed failed = new FakeGameLink().Pipeline().Call("help", null, """{"verb":"write","what":"logc"}""").Failed();

        Assert.Equal("help_not_found", failed.Code);
        Assert.Equal("logic", (string)failed.Data!["nearest"]![0]!);
    }

    // ---- recording ---------------------------------------------------------------------------------------------

    [Fact]
    public void EachCallIsRecordedAtTheInputBeforeItIsChecked()
    {
        string path = Path.Combine(_folder, "rec", "calls.jsonl");
        using (CallRecorder recorder = new(path))
        {
            VerbPipeline pipeline = new FakeGameLink().Pipeline(recorder: recorder);
            pipeline.Call("write", "logic", """{"id":"1","logic":"On","value":1}""");
            pipeline.Call("finde", null, """{"nme":"x"}""", Face.Script);
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        JsonNode first = JsonNode.Parse(lines[0])!;
        Assert.Equal(new[] { "t_ms", "face", "stream", "in_flight", "verb", "what", "options" },
            first.AsObject().Select(property => property.Key));
        Assert.Equal("""{"face":"direct","stream":0,"in_flight":1,"verb":"write","what":"logic","options":["id","logic","value"]}""",
            Without(first, "t_ms"));
        Assert.Equal("""{"face":"script","stream":0,"in_flight":1,"verb":"finde","what":null,"options":["nme"]}""",
            Without(JsonNode.Parse(lines[1])!, "t_ms"));
    }

    [Fact]
    public async Task ConcurrentCallsWriteWholeLinesAndCountWhatIsInFlight()
    {
        string path = Path.Combine(_folder, "calls.jsonl");
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncLink link = new(release.Task);
        using (CallRecorder recorder = new(path))
        {
            VerbPipeline pipeline = new(link, recorder: recorder);
            List<Task<VerbReply>> calls = Enumerable.Range(0, 20).Select(_ =>
                pipeline.CallAsync(new VerbCall("read", "clock", new JsonObject(), Face.Direct), CancellationToken.None)).ToList();
            release.SetResult();
            await Task.WhenAll(calls);
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(20, lines.Length);
        List<int> inFlight = lines.Select(line => (int)JsonNode.Parse(line)!["in_flight"]!).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 20), inFlight);
    }

    // ---- the write gate ----------------------------------------------------------------------------------------

    [Fact]
    public void TheGateIsAskedOnceForAWriteWithItsResolvedTargetsAndNeverForARead()
    {
        RecordingGate gate = new(new WriteDecision.Act());
        FakeGameLink game = new();
        VerbPipeline pipeline = game.Pipeline(gate);

        pipeline.Call("write", "logic", """{"writes":[{"id":"1","logic":"On","value":1},{"id":2,"logic":"On","value":0}]}""").Done();
        pipeline.Call("swap", "cable", """{"network":{"id":"7","port":1},"into":"heavy","materials":"player"}""").Done();
        pipeline.Call("read", "logic", """{"id":"1","logic":"On"}""").Done();

        Assert.Equal(2, gate.Asked.Count);
        WriteTarget writes = Assert.Single(gate.Asked[0].Targets.Targets);
        Assert.Equal("writes[].id", writes.Path);
        Assert.Equal("acts-on", writes.Role);
        Assert.Equal(new[] { "1", "2" }, writes.Ids);
        WriteTarget network = gate.Asked[1].Targets.Targets.Single(target => target.Path == "network");
        Assert.Equal("""{"id":"7","port":1}""", network.Selector!.ToJsonString());
        Assert.Equal(new[] { "player" }, gate.Asked[1].Targets.Targets.Single(target => target.Role == "pays-from").Ids);
    }

    [Fact]
    public void AStopFromTheGateSendsNothingAndCarriesItsRowsAndFix()
    {
        FakeGameLink game = new();
        JsonArray rows = new(new JsonObject { ["id"] = "1" });
        VerbPipeline pipeline = game.Pipeline(new RecordingGate(new WriteDecision.Stop(rows, "nothing acted: send it again")));

        VerbReply.Failed failed = pipeline.Call("write", "logic", """{"id":"1","logic":"On","value":1}""").Failed();

        Assert.True(failed.Stopped);
        Assert.Equal("nothing acted: send it again", failed.Fix);
        Assert.Equal("""[{"id":"1"}]""", failed.Data!["rows"]!.ToJsonString());
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void ThePassThroughGateLetsEveryWriteAct()
    {
        Assert.IsType<WriteDecision.Act>(PassThroughGate.Instance.Decide(
            new VerbCall("write", "logic", new JsonObject(), Face.Direct), new WriteTargets([])));
    }

    // ---- jobs ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AConfirmedJobIsPolledUntilItIsFinal()
    {
        FakeGameLink game = new();
        int polls = 0;
        game.Answer = call => call.Method == "build"
            ? FakeGameLink.Ok("""{"job_id":"run-9","status":"queued"}""")
            : FakeGameLink.Ok(++polls < 2 ? """{"job_id":"run-9","status":"running"}""" : """{"job_id":"run-9","status":"applied"}""");

        JsonNode? reply = game.Pipeline().Call("build", "cable", """{"waypoints":[[0,0,0],[1,0,0]],"confirm":true}""").Done();

        Assert.Equal("applied", (string)reply!["status"]!);
        Assert.Equal(2, game.CallsTo("inspect").Count);
        Assert.Equal("""{"what":"job","id":"run-9"}""", game.CallsTo("inspect")[1].Params().ToJsonString());
    }

    [Fact]
    public void MaxWaitZeroAnswersAtOnce()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"job_id":"place-2","status":"queued"}""");

        JsonNode? reply = game.Pipeline().Call("inspect", "job", """{"id":"place-2","max_wait":0}""").Done();

        Assert.Equal("queued", (string)reply!["status"]!);
        Assert.Single(game.Calls);
    }

    // ---- what other pieces bring -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("run_script", null, """{"code":"return 1"}""", "run_script lands with the runs piece.")]
    [InlineData("inspect", "job", """{"id":"script-4"}""", "runs piece")]
    [InlineData("server", "stop", """{"id":"script-4"}""", "runs piece")]
    [InlineData("undo", null, """{"id":"script-4"}""", "runs piece")]
    public void WhatALaterPieceBringsIsNotAvailable(string verb, string? what, string options, string message)
    {
        FakeGameLink game = new();

        VerbReply.Failed failed = game.Pipeline().Call(verb, what, options).Failed();

        Assert.Equal("not_available", failed.Code);
        Assert.Contains(message, failed.Message);
        Assert.Empty(game.Calls);
    }

    [Fact]
    public void TheModsRefusalAndAnUnreachableGameAreFailures()
    {
        FakeGameLink game = new();
        game.Answer = _ => new GameReply.Refused("thing_not_found", "No thing with id 9.", JsonDocument.Parse("""{"id":"9"}""").RootElement);
        VerbReply.Failed refused = game.Pipeline().Call("inspect", "device", """{"id":"9"}""").Failed();
        game.Answer = _ => new GameReply.Unreachable("broke", maybeRan: true);
        VerbReply.Failed unreachable = game.Pipeline().Call("write", "logic", """{"id":"9","logic":"On","value":1}""").Failed();

        Assert.Equal("""{"code":"thing_not_found","message":"No thing with id 9.","data":{"id":"9"}}""", refused.ToJson().ToJsonString());
        Assert.Equal("game_unavailable", unreachable.Code);
        Assert.True(unreachable.MaybeRan);
    }

    private static string Without(JsonNode line, string key)
    {
        JsonObject copy = (JsonObject)line.DeepClone();
        copy.Remove(key);
        return copy.ToJsonString();
    }

    private static string Page(string folder, string file) =>
        File.ReadAllText(Path.Combine(StationGodMCP.Tests.CatalogueChecks.CatalogueFiles.RepositoryRoot(), "generated", "verbs",
            folder, file)).Replace("\r\n", "\n");

    private sealed class RecordingGate(WriteDecision decision) : IWriteGate
    {
        public List<(VerbCall Call, WriteTargets Targets)> Asked { get; } = new();

        public WriteDecision Decide(VerbCall call, WriteTargets targets)
        {
            Asked.Add((call, targets));
            return decision;
        }
    }

    // A link whose calls all wait for one release, so every call is in flight at once.
    private sealed class AsyncLink(Task release) : IGameLink
    {
        public string? WorldId => null;

        public event Action<string?>? WorldChanged
        {
            add { }
            remove { }
        }

        public long NewStream() => throw new NotSupportedException();

        public async Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
        {
            await release;
            return FakeGameLink.Ok("{}");
        }

        public Task EndStreamAsync(long stream) => throw new NotSupportedException();

        public Task SessionEndAsync() => Task.CompletedTask;

        public Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation) =>
            throw new NotSupportedException();

        public Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation) =>
            throw new NotSupportedException();

        public Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation) =>
            throw new NotSupportedException();
    }
}
