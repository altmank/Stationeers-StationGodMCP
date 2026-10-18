#nullable enable

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
/// inspect job on the normalised id list a program or script face sends (T-C-6c-1): every id - a mod job, a bulk write
/// or a run - is answered as its row there, the mod is asked for the ids that are its own, and a job the reply names is
/// still waited for on the script face.
/// </summary>
public sealed class JobListTests
{
    private static Task<VerbReply> Run(VerbPipeline pipeline, string verb, string? what, string options, Face face,
        IWriteGate? gate = null, long stream = 0) =>
        pipeline.CallAsync(new VerbCall(verb, what, Options(options), face, stream) { Gate = gate }, CancellationToken.None);

    // The mod's answer to an id list (D13): one row per id, the job's own keys in its row.
    private static string JobRows(params string[] ids)
    {
        JsonArray rows = [];
        foreach (string id in ids)
        {
            rows.Add(new JsonObject
            {
                ["index"] = rows.Count,
                ["ok"] = true,
                ["reference_id"] = id,
                ["job_id"] = id,
                ["status"] = "applied"
            });
        }

        return new JsonObject
        {
            ["results"] = rows,
            ["count"] = rows.Count,
            ["success_count"] = rows.Count,
            ["error_count"] = 0
        }.ToJsonString();
    }

    [Fact]
    public void AProgramFaceModJobIdReachesTheMod()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "inspect" ? FakeGameLink.Ok(JobRows("place-2")) : FakeGameLink.Ok("{}");

        JsonNode reply = game.Pipeline().Call("inspect", "job", """{"id":"place-2"}""", Face.Program).Done()!;

        Same("""{"what":"job","id":["place-2"]}""", game.Single("inspect").Params());
        Same(JobRows("place-2"), reply);
    }

    [Fact]
    public void APureListAsksTheModOnceWithTheWholeListAndAnswersItsRows()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "inspect" ? FakeGameLink.Ok(JobRows("place-2", "place-3")) : FakeGameLink.Ok("{}");

        JsonNode reply = game.Pipeline().Call("inspect", "job", """{"id":["place-2","place-3"]}""", Face.Program).Done()!;

        Same("""{"what":"job","id":["place-2","place-3"]}""", game.Single("inspect").Params());
        Same(JobRows("place-2", "place-3"), reply);
    }

    [Fact]
    public void AJobListOfAModJobAndABulkWriteAnswersBothRows()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method switch
        {
            "write" => FakeGameLink.Ok(
                """{"results":[{"index":0,"ok":true,"reference_id":"1"},{"index":1,"ok":true,"reference_id":"2"}]}"""),
            "inspect" when call.Params()["id"] is JsonArray => FakeGameLink.Ok(JobRows("place-2", "place-3")),
            "inspect" => FakeGameLink.Ok("""{"job_id":"place-2","status":"applied"}"""),
            _ => FakeGameLink.Ok("{}")
        };
        VerbPipeline pipeline = game.Pipeline();
        pipeline.Call("write", "label", """{"id":["1","2"],"label":"new"}""").Done();

        JsonNode reply = pipeline.Call("inspect", "job", """{"id":["place-2","write-1"]}""", Face.Program).Done()!;

        Assert.Equal(new[] { "place-2", "write-1" },
            reply["results"]!.AsArray().Select(row => (string)row!["reference_id"]!));
        Assert.Equal(2, (int)reply["count"]!);
        Assert.Equal(2, (int)reply["success_count"]!);
        Assert.Equal(0, (int)reply["error_count"]!);
        Assert.Equal("place-2", (string)game.CallsTo("inspect").Last().Params()["id"]!);
    }

    [Fact]
    public async Task AScriptFacesJobWaitStillWorks()
    {
        FakeGameLink game = new();
        game.WaitJob = _ => JsonDocument.Parse("""{"job_id":"run-9","status":"applied"}""").RootElement.Clone();
        game.Answer = call => call.Method == "build"
            ? FakeGameLink.Ok("""{"job_id":"run-9","status":"queued"}""")
            : FakeGameLink.Ok("""{"job_id":"run-9","status":"applied","held_ms":12}""");

        JsonNode reply = (await Run(game.Pipeline(), "build", "cable", """{"waypoints":[[0,0,0],[1,0,0]]}""",
            Face.Script, new ScriptedGate(new WriteDecision.Act()))).Done()!;

        Assert.Equal(new[] { "run-9" }, game.Waited);
        Same("""{"what":"job","id":"run-9"}""", game.Single("inspect").Params());
        Assert.Single(game.CallsTo("build"));
        Assert.Equal("applied", (string)reply["status"]!);
        Assert.Null(reply["running"]);
    }

    [Fact]
    public async Task ARunIdOnTheNormalisedListIsRefused()
    {
        FakeGameLink game = new();

        VerbReply.Failed failed = (await Run(game.Pipeline(), "inspect", "job", """{"id":"script-4"}""", Face.Program))
            .Failed();

        Assert.Equal("not_available", failed.Code);
        Assert.Contains("inspect job for a script run", failed.Message);
        Assert.Empty(game.Calls);
    }
}
