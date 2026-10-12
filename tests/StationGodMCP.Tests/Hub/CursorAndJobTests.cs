#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// Script and program main lists with a hub cursor walked by MoreAsync (find_things' walk cursor, else offset pages),
/// freed at the end, with the stream and on a world change; job waits on the jobs topic with one poll after (a null
/// after a reconnect included), finality by the mod's final states, and a refused poll answered.
/// </summary>
public sealed class CursorAndJobTests
{
    private static VerbReply More(VerbPipeline pipeline, long stream, string cursor, int? limit = null) =>
        pipeline.MoreAsync(stream, cursor, limit, CancellationToken.None).GetAwaiter().GetResult();

    [Theory]
    [InlineData(Face.Script)]
    [InlineData(Face.Program)]
    public void FindThingWalksTheModsCursor(Face face)
    {
        FakeGameLink game = new();
        game.Answer = call => (string?)call.Params()["cursor_id"] switch
        {
            null => FakeGameLink.Ok("""{"things":[{"reference_id":"1"},{"reference_id":"2"}],"has_more":true,"cursor_id":"k7"}"""),
            "k7" => FakeGameLink.Ok("""{"things":[{"reference_id":"3"}],"has_more":true,"cursor_id":"k8"}"""),
            _ => FakeGameLink.Ok("""{"things":[{"reference_id":"4"}],"has_more":false}""")
        };
        VerbPipeline pipeline = game.Pipeline();

        JsonNode first = pipeline.Call("find", "thing", """{"name":"Light"}""", face).Done()!;
        string cursor = (string)first["cursor"]!;
        JsonNode second = More(pipeline, 0, cursor, 5).Done()!;
        JsonNode last = More(pipeline, 0, cursor).Done()!;

        Same("""{"name_contains":"Light","cursor":true}""", game.CallsTo("find_things")[0].Params());
        Assert.Equal("things", (string)first["main"]!);
        Assert.Null(first["cursor_id"]);
        Same("""{"cursor_id":"k7","limit":5}""", game.CallsTo("find_things")[1].Params());
        Same("""{"entries":[{"reference_id":"3"}],"cursor":"c1"}""", second);
        Same("""{"entries":[{"reference_id":"4"}]}""", last);
        Assert.Equal("cursor_expired", More(pipeline, 0, cursor).Failed().Code);
    }

    [Fact]
    public void AListWithoutAWalkPagesByOffset()
    {
        FakeGameLink game = new();
        game.Answer = call => (int?)call.Params()["offset"] switch
        {
            null => FakeGameLink.Ok("""{"items":[{"reference_id":"1"},{"reference_id":"2"}],"offset":0,"has_more":true}"""),
            2 => FakeGameLink.Ok("""{"items":[{"reference_id":"3"}],"offset":2,"has_more":false}"""),
            _ => FakeGameLink.Refused("invalid_argument", "unexpected page")
        };
        VerbPipeline pipeline = game.Pipeline();

        string cursor = (string)pipeline.Call("find", "item", """{"prefab":"ItemIronOre"}""", Face.Script).Done()!["cursor"]!;
        JsonNode page = More(pipeline, 0, cursor).Done()!;

        Same("""{"prefab":"ItemIronOre","offset":2}""", game.CallsTo("find_items")[1].Params());
        Same("""{"entries":[{"reference_id":"3"}]}""", page);
    }

    [Fact]
    public void ALimitOnTheScriptFaceIsTheFirstEntriesWithNoCursor()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"}],"has_more":true}""");

        JsonNode reply = game.Pipeline().Call("find", "thing", """{"limit":1}""", Face.Script).Done()!;

        Assert.Null(reply["cursor"]);
        Assert.Null(game.Single("find_things").Params()["cursor"]);
    }

    [Fact]
    public void CursorsGoWithTheirStreamAndOnAWorldChangeAndBelongToTheirStream()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"}],"has_more":true,"cursor_id":"k1"}""");
        VerbPipeline pipeline = game.Pipeline();

        string first = Open(pipeline, 3);
        Assert.Equal("cursor_expired", More(pipeline, 4, first).Failed().Code);
        pipeline.EndStream(3);
        Assert.Equal("cursor_expired", More(pipeline, 3, first).Failed().Code);
        string second = Open(pipeline, 5);
        game.ChangeWorld("world-2");
        Assert.Equal("cursor_expired", More(pipeline, 5, second).Failed().Code);
        Assert.Equal(0, pipeline.Cursors.Count);
    }

    private static string Open(VerbPipeline pipeline, long stream) =>
        (string)pipeline.CallAsync(new VerbCall("find", "thing", new JsonObject(), Face.Script, stream), CancellationToken.None)
            .GetAwaiter().GetResult().Done()!["cursor"]!;

    // ---- job waits -------------------------------------------------------------------------------------------------

    [Fact]
    public void AConfirmedJobWaitsOnTheJobsTopicThenPollsOnce()
    {
        FakeGameLink game = new();
        game.WaitJob = _ => JsonDocument.Parse("""{"job_id":"run-9","status":"applied"}""").RootElement.Clone();
        game.Answer = call => call.Params()["job_id"] == null
            ? FakeGameLink.Ok("""{"job_id":"run-9","status":"queued"}""")
            : FakeGameLink.Ok("""{"job_id":"run-9","status":"applied","held_ms":12}""");

        JsonNode reply = game.Pipeline().Call("build", "cable", """{"waypoints":[[0,0,0],[1,0,0]],"confirm":true}""").Done()!;

        Assert.Equal(new[] { "run-9" }, game.Waited);
        Same("""{"job_id":"run-9"}""", game.CallsTo("place_cables")[1].Params());
        Assert.Equal(2, game.CallsTo("place_cables").Count);
        Assert.Equal("applied", (string)reply["status"]!);
        Assert.Null(reply["running"]);
    }

    [Fact]
    public void ANullAfterAReconnectPollsOnceAndAJobStillGoingAnswersRunning()
    {
        FakeGameLink game = new();
        game.WaitJob = _ => null;
        game.Answer = call => FakeGameLink.Ok("""{"job_id":"replace-2","status":"verifying"}""");

        JsonNode reply = game.Pipeline().Call("inspect", "job", """{"id":"replace-2","max_wait":5}""").Done()!;

        Assert.Equal(2, game.CallsTo("replace_walls").Count);
        Assert.Equal("replace-2", (string)reply["job_id"]!);
        Assert.True((bool)reply["running"]!);
    }

    [Fact]
    public void ARefusedPollIsTheAnswer()
    {
        FakeGameLink game = new();
        game.WaitJob = _ => null;
        int calls = 0;
        game.Answer = _ => ++calls == 1
            ? FakeGameLink.Ok("""{"job_id":"place-3","status":"waiting"}""")
            : FakeGameLink.Refused("dropped", "Job place-3 was dropped from the queue.");

        VerbReply.Failed failed = game.Pipeline().Call("inspect", "job", """{"id":"place-3"}""").Failed();

        Assert.Equal("dropped", failed.Code);
    }

    [Theory]
    [InlineData("applied", true)]
    [InlineData("applied_with_differences", true)]
    [InlineData("applied_unchecked", true)]
    [InlineData("stopped", true)]
    [InlineData("refused", true)]
    [InlineData("failed", true)]
    [InlineData("queued", false)]
    [InlineData("waiting", false)]
    [InlineData("scheduled", false)]
    [InlineData("verifying", false)]
    public void FinalityIsTheModsFinalStates(string status, bool final)
    {
        FakeGameLink game = new();
        game.WaitJob = _ => null;
        game.Answer = _ => FakeGameLink.Ok($$"""{"job_id":"place-1","status":"{{status}}"}""");

        game.Pipeline().Call("inspect", "job", """{"id":"place-1"}""").Done();

        Assert.Equal(final ? 1 : 2, game.CallsTo("place_structure").Count);
        Assert.Equal(final ? 0 : 1, game.Waited.Count);
    }
}
