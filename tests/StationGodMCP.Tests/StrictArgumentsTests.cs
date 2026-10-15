#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Verb calls are checked in full on the connection's reader thread before anything runs: every option problem at
/// once (invalid_argument), and a shape that cannot be used is invalid_shape. With StrictArguments off the main
/// thread checks the options instead, before the handler runs.
/// </summary>
public sealed class StrictArgumentsTests
{
    [Fact]
    public async Task EveryOptionProblemIsRefusedAtOnceAndNothingRuns()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("s", "read",
            new JObject { ["what"] = "logic", ["id"] = 5, ["logic"] = "On", ["gatway"] = "world" });

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        JArray problems = (JArray)reply["error"]!["data"]!["problems"]!;
        Assert.Equal(new[] { "id", "gatway" }, problems.Select(problem => (string?)problem["path"]).ToArray());
        Assert.Equal("gateway", (string?)problems[1]["nearest"]![0]);
        Assert.Equal("gateway", (string?)reply["error"]!["data"]!["valid"]![0]);
        Assert.StartsWith("read logic: id: must be a string or a list", (string?)reply["error"]!["message"]);
        Assert.Null(reply["elapsed_ms"]);
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task ARangeProblemNamesItsPathAndTheRange()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("c", "find", new JObject { ["what"] = "thing", ["limit"] = 0 });

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        JToken problem = Assert.Single((JArray)reply["error"]!["data"]!["problems"]!);
        Assert.Equal("limit", (string?)problem["path"]);
        Assert.Equal("must be at least 1", (string?)problem["problem"]);
    }

    [Fact]
    public async Task AListEntrysProblemNamesItsPath()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("n", "find", JObject.Parse("""{"what":"thing","prefab":["ItemIronOre",true]}"""));

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Equal("prefab[1]", (string?)Assert.Single((JArray)reply["error"]!["data"]!["problems"]!)["path"]);
    }

    [Theory]
    [InlineData("""["reference_id"]""")]
    [InlineData("""{"fields":["prefab-name"]}""")]
    [InlineData("""{"fields":["things..x"]}""")]
    [InlineData("""{"fields":[]}""")]
    [InlineData("""{"fields":[7]}""")]
    [InlineData("""{"fields":["x"],"colour":"red"}""")]
    [InlineData("""{"limit":{"things":-1}}""")]
    [InlineData("""{"max_bytes":10}""")]
    public async Task AShapeVersionTwoCannotUseIsInvalidShape(string shape)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send($$"""{"type":"call","id":"m","method":"find","params":{"what":"thing"},"shape":{{shape}}}""");

        JObject reply = client.Next();

        Assert.Equal("invalid_shape", (string?)reply["error"]!["code"]);
        Assert.NotEmpty((JArray)reply["error"]!["data"]!["problems"]!);
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task MoreThan256SelectorsIsInvalidShape()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        JArray fields = new JArray(Enumerable.Range(0, 257).Select(index => (object)$"name_{index}").ToArray());
        client.Send(new JObject
        {
            ["type"] = "call", ["id"] = "m", ["method"] = "find", ["params"] = new JObject { ["what"] = "thing" },
            ["shape"] = new JObject { ["fields"] = fields }
        }.ToString(Formatting.None));

        Assert.Equal("invalid_shape", (string?)client.Next()["error"]!["code"]);
    }

    [Fact]
    public async Task AGoodShapeReachesTheMainThread()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send("""{"type":"call","id":"g","method":"find","params":{"what":"thing"},"shape":{"fields":["things.position.x","reference_id"],"limit":{"things":5},"max_bytes":4096}}""");

        JObject reply = client.Next();

        Assert.True((bool)reply["ok"]!);
        Assert.True((bool)reply["shaped"]!);
    }

    [Fact]
    public async Task FieldsOmitAndListLimitsGivenAsOptionsAreTheCallsShape()
    {
        using PipeRig rig = new PipeRig();
        ConcurrentQueue<CallRequest> reached = Reaching(rig);
        using V2Client client = await V2Client.Connect(rig);

        JObject accepted = client.Call("f", "find", JObject.Parse(
            """{"what":"thing","fields":["things.reference_id"],"omit":["scanned"],"list_limits":{"things":3}}"""));
        JObject refused = client.Call("b", "find", JObject.Parse("""{"what":"thing","fields":["things..x"]}"""));

        Assert.True((bool)accepted["ok"]!);
        ShapeRequest shape = Assert.Single(reached).Shape!;
        Assert.NotNull(shape.Fields);
        Assert.NotNull(shape.Omit);
        Assert.Equal(3, shape.LimitOf("things"));
        Assert.Equal("invalid_shape", (string?)refused["error"]!["code"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWhatsDefaultListCapsAreTheShapesLimitsTheCallDoesNotSet(bool strict)
    {
        // review D, R-D-2: find food cuts food, drinks and packages at 3 and not_counted at 5 (x-default-limits)
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: strict));
        ConcurrentQueue<CallRequest> reached = Reaching(rig);
        using V2Client client = await V2Client.Connect(rig);

        client.Call("a", "find", JObject.Parse("""{"what":"food"}"""));
        client.Call("b", "find", JObject.Parse("""{"what":"food","list_limits":{"drinks":40}}"""));
        client.Call("c", "find", JObject.Parse("""{"what":"thing"}"""));

        CallRequest[] calls = reached.ToArray();
        Assert.Equal(3, calls[0].Shape!.LimitOf("food"));
        Assert.Equal(5, calls[0].Shape!.LimitOf("not_counted"));
        Assert.Equal(40, calls[1].Shape!.LimitOf("drinks"));
        Assert.Equal(3, calls[1].Shape!.LimitOf("packages"));
        Assert.Null(calls[2].Shape);
    }

    [Fact]
    public void TheLenientReadingReportsWhatTheStrictOneRefuses()
    {
        ShapeRequest? lenient = ShapeRequest.Lenient(JObject.Parse("""{"fields":["prefab-name","x"],"limit":{"count":1}}"""));
        List<string> problems = new List<string>();
        ShapeRequest? strict = ShapeRequest.Strict(JObject.Parse("""{"fields":["prefab-name","x"],"limit":{"count":1}}"""),
            new HashSet<string> { "things" }, problems);

        Assert.NotNull(lenient);
        Assert.Equal("""{"things":[{"x":1}],"fields_unmatched":["prefab-name"],"fields_valid":["things","x","y"]}""",
            ShapingChecks.Mod(ShapingChecks.Parse("""{"things":[{"x":1,"y":2}]}"""), lenient!).Json);
        Assert.Null(strict);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public async Task OnAStrictConnectionAProblemIsRefusedOnceAndTheMainThreadDoesNotLookAgain()
    {
        using PipeRig rig = new PipeRig();
        ConcurrentQueue<CallRequest> reached = Reaching(rig);
        using V2Client client = await V2Client.Connect(rig);

        JObject refused = client.Call("u", "find", new JObject { ["what"] = "thing", ["prefab_contains"] = "Ore" });
        JObject accepted = client.Call("k", "find", new JObject { ["what"] = "thing", ["prefab"] = "*Ore*" });

        Assert.Equal("invalid_argument", (string?)refused["error"]!["code"]);
        JToken problem = Assert.Single((JArray)refused["error"]!["data"]!["problems"]!);
        Assert.Equal("prefab_contains", (string?)problem["path"]);
        Assert.Contains("prefab", problem["nearest"]!.Values<string>());
        Assert.Null(client.NextOrNull(200));
        Assert.True((bool)accepted["ok"]!);
        CallRequest ran = Assert.Single(reached);
        Assert.Equal("k", ran.Id);
        Assert.True(ran.ArgumentNamesChecked);
    }

    [Fact]
    public async Task OnALenientConnectionTheMainThreadStillRefusesAProblem()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false));
        ConcurrentQueue<CallRequest> reached = Reaching(rig);
        using V2Client client = await V2Client.Connect(rig);
        JObject parameters = new JObject { ["what"] = "thing", ["prefab_contains"] = "Ore" };

        Assert.True((bool)client.Call("u", "find", parameters)["ok"]!);

        CallRequest ran = Assert.Single(reached);
        Assert.False(ran.ArgumentNamesChecked);
        VerbOutcome.Refused onMainThread = Assert.IsType<VerbOutcome.Refused>(
            TestVerbs.Every.Run(ran.Method, ran.Params, ran.Shape, ran.ArgumentNamesChecked));
        Assert.Equal("invalid_argument", onMainThread.Error.Code);
        Assert.StartsWith("find thing: prefab_contains: not an option of find thing", onMainThread.Error.Message);
    }

    // The calls that reach the fake main thread, each answered as the fake mod answers any call.
    private static ConcurrentQueue<CallRequest> Reaching(PipeRig rig)
    {
        ConcurrentQueue<CallRequest> reached = new ConcurrentQueue<CallRequest>();
        rig.Mod.CallHook = (call, _) =>
        {
            reached.Enqueue(call.Request);
            return null;
        };
        return reached;
    }
}
