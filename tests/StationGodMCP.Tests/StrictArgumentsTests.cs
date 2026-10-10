#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Stage 5: calls are checked against the catalogue in full before anything runs, and a shape that cannot be used is
/// invalid_shape; with StrictArguments off the lenient reading reports it in fields_unmatched instead.
/// </summary>
public sealed class StrictArgumentsTests
{
    public static IEnumerable<object[]> Cases() => CatalogueValidatorTests.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task VersionTwoRefusesWhatTheCatalogueRefuses(string method, string arguments, bool accepted)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        JToken parameters = JToken.Parse(arguments);
        client.Send(new JObject { ["type"] = "call", ["id"] = "s", ["method"] = method, ["params"] = parameters }
            .ToString(Formatting.None));

        JObject reply = client.Next();

        if (accepted)
        {
            Assert.True((bool)reply["ok"]!, reply.ToString());
            Assert.Equal(1, rig.Mod.Ran);
            return;
        }

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        JArray problems = (JArray)reply["error"]!["data"]!["problems"]!;
        Assert.NotEmpty(problems);
        Assert.All(problems, problem => Assert.False(string.IsNullOrEmpty((string?)problem["problem"])));
        Assert.Null(reply["elapsed_ms"]);
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task ARangeProblemNamesItsPathAndTheRange()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("c", "connections", new JObject { ["reference_id"] = "1", ["limit"] = 99999 });

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        JToken problem = Assert.Single((JArray)reply["error"]!["data"]!["problems"]!);
        Assert.Equal("limit", (string?)problem["path"]);
        Assert.Contains("1000", (string?)problem["problem"]);
    }

    [Fact]
    public async Task ANestedUnknownNameIsRefusedWithItsPath()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("n", "read_devices",
            JObject.Parse("""{"items":[{"reference_id":"5","logic":["On"],"bogus":1}]}"""));

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Contains(((JArray)reply["error"]!["data"]!["problems"]!).Select(problem => (string?)problem["path"]),
            path => path != null && path.StartsWith("items[0]"));
    }

    [Theory]
    [InlineData("""["reference_id"]""")]
    [InlineData("""{"fields":["prefab-name"]}""")]
    [InlineData("""{"fields":["things..x"]}""")]
    [InlineData("""{"fields":[]}""")]
    [InlineData("""{"fields":[7]}""")]
    [InlineData("""{"fields":["x"],"colour":"red"}""")]
    [InlineData("""{"limit":{"count":1}}""")]
    [InlineData("""{"limit":{"things":-1}}""")]
    [InlineData("""{"max_bytes":10}""")]
    public async Task AShapeVersionTwoCannotUseIsInvalidShape(string shape)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send($$"""{"type":"call","id":"m","method":"find_things","params":{},"shape":{{shape}}}""");

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
            ["type"] = "call", ["id"] = "m", ["method"] = "find_things", ["params"] = new JObject(),
            ["shape"] = new JObject { ["fields"] = fields }
        }.ToString(Formatting.None));

        Assert.Equal("invalid_shape", (string?)client.Next()["error"]!["code"]);
    }

    [Fact]
    public async Task AGoodShapeReachesTheMainThread()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);
        client.Send("""{"type":"call","id":"g","method":"find_things","params":{},"shape":{"fields":["things.position.x","reference_id"],"limit":{"things":5},"max_bytes":4096}}""");

        JObject reply = client.Next();

        Assert.True((bool)reply["ok"]!);
        Assert.True((bool)reply["shaped"]!);
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
    public async Task WithStrictArgumentsOffOnlyNamesAreChecked()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false));
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("o", "connections", new JObject { ["reference_id"] = "1", ["limit"] = 99999 });

        Assert.True((bool)reply["ok"]!);
    }

    [Fact]
    public async Task OnAStrictConnectionAnUnknownNameIsRefusedOnceAndTheMainThreadDoesNotLookAgain()
    {
        using PipeRig rig = new PipeRig();
        ConcurrentQueue<CallRequest> reached = Reaching(rig);
        using V2Client client = await V2Client.Connect(rig);

        JObject refused = client.Call("u", "list_containers", new JObject { ["prefab"] = "ItemDirtyOre" });
        JObject accepted = client.Call("k", "list_containers", new JObject { ["prefab_contains"] = "ItemDirtyOre" });

        Assert.Equal("invalid_argument", (string?)refused["error"]!["code"]);
        Assert.Equal("prefab", (string?)Assert.Single((JArray)refused["error"]!["data"]!["problems"]!)["path"]);
        Assert.Null(client.NextOrNull(200));
        Assert.True((bool)accepted["ok"]!);
        CallRequest ran = Assert.Single(reached);
        Assert.Equal("k", ran.Id);
        Assert.True(ran.ArgumentNamesChecked);
        Declared.Check(new CallRequest("u", "list_containers", new JObject { ["prefab"] = "ItemDirtyOre" }, null,
            ran.ArgumentNamesChecked));
    }

    [Fact]
    public async Task OnALenientConnectionTheMainThreadStillRefusesAnUnknownName()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: false));
        ConcurrentQueue<CallRequest> reached = Reaching(rig);
        using V2Client client = await V2Client.Connect(rig);
        JObject parameters = new JObject { ["prefab"] = "ItemDirtyOre" };

        Assert.True((bool)client.Call("u", "list_containers", parameters)["ok"]!);

        CallRequest ran = Assert.Single(reached);
        Assert.False(ran.ArgumentNamesChecked);
        ApiException onMainThread = Assert.Throws<ApiException>(() => Declared.Check(ran));
        ApiException byName = Assert.Throws<ApiException>(() => Declared.Check("list_containers", parameters));
        Assert.Equal(ApiErrors.InvalidArgumentCode, onMainThread.Code);
        Assert.Equal(byName.Message, onMainThread.Message);
        Assert.StartsWith("Unknown argument 'prefab'; did you mean 'prefab_contains'?", onMainThread.Message);
    }

    private static readonly DeclaredArguments Declared = new DeclaredArguments(TestCatalogue.File.Value.Catalogue);

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
