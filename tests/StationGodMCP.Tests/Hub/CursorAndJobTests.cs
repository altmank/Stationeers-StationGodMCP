#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Client;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// Script and program main lists with a hub cursor walked by MoreAsync on the mod's walk cursor (a reply with more but
/// no walk names the rest unreachable), freed at the end, with the stream, on a world change and by the store's count
/// and idle bounds; job waits on the jobs topic with one poll after (a null after a reconnect included), finality by the mod's
/// final states, and a refused poll answered.
/// </summary>
public sealed class CursorAndJobTests
{
    private static VerbReply More(VerbPipeline pipeline, long stream, string cursor, int? limit = null) =>
        pipeline.MoreAsync(stream, cursor, limit, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void AListWithMoreButNoWalkNamesTheRestUnreachableAndPagesNothingByOffset()
    {
        FakeGameLink game = new();
        game.Answer = _ => FakeGameLink.Ok(
            """{"items":[{"reference_id":"1"},{"reference_id":"2"}],"offset":0,"has_more":true}""");
        VerbPipeline pipeline = game.Pipeline();

        JsonNode reply = pipeline.Call("find", "item", """{"prefab":"ItemIronOre"}""", Face.Script).Done()!;

        Assert.Null(reply["cursor"]);
        Assert.Contains("keeps no walk", (string)reply["rest_unreachable"]!);
        Assert.Single(game.CallsTo("find"));
        Assert.Equal(0, pipeline.Cursors.Count);
    }

    // find device's reply for one page, as its handler writes it: 30 devices found, 25 offered of it (ListReply).
    private static string DevicePage(int offset, int count, string? cursorId)
    {
        List<DeviceView> devices = new List<DeviceView>();
        for (int index = offset; index < offset + count; index++)
        {
            devices.Add(new DeviceView(new ThingView(new ThingId(500 + index), "StructureGasSensor",
                "Gas Sensor " + index), -1252983604, "Assets.Scripts.Objects.Pipes.GasSensor", 0,
                new DeviceWear(false, null, null)));
        }

        Newtonsoft.Json.JsonSerializer serializer = ApiJson.Fresh();
        JObject reply = ListReply.OfPage("devices", new Page<DeviceView>(devices, 30, offset, 25, offset + count < 30),
            device => JToken.FromObject(device, serializer), ReplyFields.Of(("gateway_id", "world")));
        if (cursorId != null)
        {
            reply["cursor_id"] = cursorId;
        }

        return reply.ToString(Formatting.None);
    }

    [Fact]
    public void AProgramFaceFindDeviceWalksTheWholeListThroughMore()
    {
        FakeGameLink game = new();
        game.Answer = call => FakeGameLink.Ok((string?)call.Params()["cursor"] == "open"
            ? DevicePage(0, 25, "k3")
            : DevicePage(25, 5, null));
        VerbPipeline pipeline = game.Pipeline();

        JsonNode reply = pipeline.Call("find", "device", "{}", Face.Program).Done()!;
        JsonNode page = More(pipeline, 0, (string)reply["cursor"]!).Done()!;

        // The mod's first page keeps its walk (cursor_id), and the hub goes on with it.
        Assert.True((bool)reply["has_more"]!);
        Assert.Equal(25, reply["devices"]!.AsArray().Count);
        Assert.Equal(30, (int)reply["total"]!);
        Assert.Equal(25, (int)reply["limit"]!);
        Assert.Equal("world", (string?)reply["gateway_id"]);
        Same("""{"what":"device","cursor":"open"}""", game.CallsTo("find")[0].Params());
        Same("""{"what":"device","cursor":"k3"}""", game.CallsTo("find")[1].Params());
        Assert.Equal(5, page["entries"]!.AsArray().Count);
        Assert.Equal("525", (string?)page["entries"]![0]!["reference_id"]);
        Assert.Equal(30, reply["devices"]!.AsArray().Count + page["entries"]!.AsArray().Count);
        Assert.Equal(0, pipeline.Cursors.Count);
    }

    [Fact]
    public void AWalkOnTheModsCursorPagesWithoutOffsets()
    {
        FakeGameLink game = new();
        game.Answer = call => (string?)call.Params()["cursor"] switch
        {
            "open" => FakeGameLink.Ok(
                """{"things":[{"reference_id":"1"},{"reference_id":"2"}],"offset":0,"has_more":true,"cursor_id":"k7"}"""),
            "k7" => FakeGameLink.Ok("""{"things":[{"reference_id":"3"}],"has_more":false}"""),
            _ => FakeGameLink.Refused("invalid_argument", "unexpected page")
        };
        VerbPipeline pipeline = game.Pipeline();

        JsonNode reply = pipeline.Call("find", "thing", "{}", Face.Script).Done()!;
        JsonNode page = More(pipeline, 0, (string)reply["cursor"]!).Done()!;

        // The first page asks for the mod's walk and passes its cursor_id on; the next page goes on mine, not offset.
        Assert.Equal("k7", (string)reply["cursor_id"]!);
        Same("""{"what":"thing","cursor":"open"}""", game.CallsTo("find")[0].Params());
        Same("""{"what":"thing","cursor":"k7"}""", game.CallsTo("find")[1].Params());
        Same("""{"entries":[{"reference_id":"3"}]}""", page);
    }

    [Fact]
    public void AModCursorExpiryIsTheAnswerAndFreesTheHubCursor()
    {
        FakeGameLink game = new();
        game.Answer = call => (string?)call.Params()["cursor"] == "open"
            ? FakeGameLink.Ok("""{"things":[{"reference_id":"1"}],"has_more":true,"cursor_id":"k7"}""")
            : FakeGameLink.Refused("cursor_expired", "Cursor k7 is gone.");
        VerbPipeline pipeline = game.Pipeline();

        JsonNode reply = pipeline.Call("find", "thing", "{}", Face.Script).Done()!;
        VerbReply.Failed failed = More(pipeline, 0, (string)reply["cursor"]!).Failed();

        Assert.Equal("cursor_expired", failed.Code);
        Assert.Equal(0, pipeline.Cursors.Count);
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

    [Fact]
    public void TheLeastRecentlyUsedCursorGoesWhenTooManyAreHeld()
    {
        HubCursors cursors = new(maximumCursors: 3, clockMs: () => 0);
        WhatEntry what = VerbExampleTests.WhatOf("find.thing");
        string first = cursors.Open(1, what, new JsonObject(), "things", "k1").Id;
        string second = cursors.Open(1, what, new JsonObject(), "things", "k2").Id;
        string third = cursors.Open(1, what, new JsonObject(), "things", "k3").Id;
        Assert.NotNull(cursors.Get(1, first));

        string fourth = cursors.Open(1, what, new JsonObject(), "things", "k4").Id;

        Assert.Equal(3, cursors.Count);
        Assert.Null(cursors.Get(1, second));
        Assert.NotNull(cursors.Get(1, first));
        Assert.NotNull(cursors.Get(1, third));
        Assert.NotNull(cursors.Get(1, fourth));
    }

    [Fact]
    public void ACursorIdleLongerThanTheBoundIsDropped()
    {
        long now = 0;
        HubCursors cursors = new(maximumIdleMs: 1000, clockMs: () => now);
        WhatEntry what = VerbExampleTests.WhatOf("find.thing");
        string paged = cursors.Open(1, what, new JsonObject(), "things", "k1").Id;
        string idle = cursors.Open(1, what, new JsonObject(), "things", "k2").Id;

        now = 900;
        Assert.NotNull(cursors.Get(1, paged));
        now = 1500;

        Assert.NotNull(cursors.Get(1, paged));
        Assert.Null(cursors.Get(1, idle));
        Assert.Equal(1, cursors.Count);
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
