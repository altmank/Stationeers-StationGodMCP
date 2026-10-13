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
/// Script and program main lists with a hub cursor walked by MoreAsync (offset pages of the same verb call),
/// freed at the end, with the stream and on a world change; job waits on the jobs topic with one poll after (a null
/// after a reconnect included), finality by the mod's final states, and a refused poll answered.
/// </summary>
public sealed class CursorAndJobTests
{
    private static VerbReply More(VerbPipeline pipeline, long stream, string cursor, int? limit = null) =>
        pipeline.MoreAsync(stream, cursor, limit, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void AListPagesByOffset()
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

        Same("""{"what":"item","prefab":"ItemIronOre","offset":2}""", game.CallsTo("find")[1].Params());
        Same("""{"entries":[{"reference_id":"3"}]}""", page);
    }

    [Fact]
    public void ALimitOnTheScriptFaceIsTheFirstEntriesWithNoCursor()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"}],"has_more":true}""");

        JsonNode reply = game.Pipeline().Call("find", "thing", """{"limit":1}""", Face.Script).Done()!;

        Assert.Null(reply["cursor"]);
        Assert.Null(game.Single("find").Params()["cursor"]);
    }

    [Fact]
    public void CursorsGoWithTheirStreamAndOnAWorldChangeAndBelongToTheirStream()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"}],"has_more":true}""");
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
        game.Answer = call => call.Method == "build"
            ? FakeGameLink.Ok("""{"job_id":"run-9","status":"queued"}""")
            : FakeGameLink.Ok("""{"job_id":"run-9","status":"applied","held_ms":12}""");

        JsonNode reply = game.Pipeline().Call("build", "cable", """{"waypoints":[[0,0,0],[1,0,0]],"confirm":true}""").Done()!;

        Assert.Equal(new[] { "run-9" }, game.Waited);
        Same("""{"what":"job","id":"run-9"}""", game.Single("inspect").Params());
        Assert.Single(game.CallsTo("build"));
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

        Assert.Equal(2, game.CallsTo("inspect").Count);
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

        Assert.Equal(final ? 1 : 2, game.CallsTo("inspect").Count);
        Assert.Equal(final ? 0 : 1, game.Waited.Count);
    }
}
