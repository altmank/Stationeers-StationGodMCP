#nullable enable

using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Protocol;
using Xunit;

namespace StationGodMCP.Tests.VocabularyChecks;

/// <summary>
/// The call key rename: read on the connection's reader thread like the other call keys, refused invalid_argument with
/// the call's id when it cannot be used, carried to the main thread on the call, and used by the full argument check
/// so every problem names arguments as the caller does. A call without it is answered as before.
/// </summary>
public sealed class RenameWireTests
{
    private const string VerbNames =
        """{"args": {"reference_id": "id", "limit": "count", "items": "reads"}, "tools": {"describe_device": "inspect"}}""";

    private static JObject Call(V2Client client, string method, string parameters, string? rename = null)
    {
        JObject call = new JObject
        {
            ["type"] = "call", ["id"] = "r", ["method"] = method, ["params"] = JObject.Parse(parameters)
        };
        if (rename != null)
        {
            call["rename"] = JToken.Parse(rename);
        }

        client.Send(call.ToString(Formatting.None));
        return client.Next();
    }

    private static JToken OnlyProblem(JObject reply)
    {
        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        return Assert.Single((JArray)reply["error"]!["data"]!["problems"]!);
    }

    [Fact]
    public async Task WelcomeSaysTheServerTakesRename()
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        Assert.Contains("rename", client.Welcome["features"]!.Values<string>());
    }

    [Theory]
    [InlineData("[]", "rename must be an object")]
    [InlineData("""{"args": {"Reference_Id": "id"}}""", "rename.args key 'Reference_Id' is not a catalogue name")]
    [InlineData("""{"tools": {"find_items": 7}}""", "rename.tools.find_items must be a string")]
    [InlineData("""{"words": {}}""", "rename takes args and tools only")]
    public async Task AMalformedMapIsRefusedWithTheCallsIdAndNothingRuns(string rename, string expected)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = Call(client, "find_things", "{}", rename);

        Assert.Equal("r", (string?)reply["id"]);
        Assert.False((bool)reply["ok"]!);
        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Contains(expected, (string?)reply["error"]!["message"]);
        Assert.Equal(0, rig.Mod.Ran);
        Assert.True((bool)client.Call("after", "find_things", new JObject())["ok"]!);
    }

    [Fact]
    public async Task TheMapReachesTheMainThreadWithTheCallAndParamsStayInHandlerNames()
    {
        using PipeRig rig = new PipeRig();
        RenameMap? seen = null;
        JObject? parameters = null;
        rig.Mod.CallHook = (call, _) =>
        {
            seen = call.Request.Rename;
            parameters = call.Request.Params;
            return null;
        };
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = Call(client, "describe_device", """{"reference_id": "5"}""", VerbNames);

        Assert.True((bool)reply["ok"]!, reply.ToString());
        Assert.NotNull(seen);
        Assert.Equal("id", seen!.Argument("reference_id"));
        Assert.Equal("inspect", seen.Tool("describe_device"));
        Assert.Equal("5", (string?)parameters!["reference_id"]);
    }

    [Fact]
    public async Task ACallWithoutAMapCarriesNone()
    {
        using PipeRig rig = new PipeRig();
        bool sawMap = true;
        rig.Mod.CallHook = (call, _) =>
        {
            sawMap = call.Request.Rename != null;
            return null;
        };
        using V2Client client = await V2Client.Connect(rig);

        Call(client, "describe_device", """{"reference_id": "5"}""");

        Assert.False(sawMap);
    }

    [Theory]
    [InlineData(null, "reference_id", "Argument 'reference_id' is required.")]
    [InlineData(VerbNames, "id", "Argument 'id' is required.")]
    public async Task AMissingRequiredArgumentIsNamedAsTheCallerSaysIt(string? rename, string path, string problem)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JToken found = OnlyProblem(Call(client, "describe_device", "{}", rename));

        Assert.Equal(path, (string?)found["path"]);
        Assert.Equal(problem, (string?)found["problem"]);
    }

    [Theory]
    [InlineData(null, "Unknown argument 'reference_idd'; did you mean 'reference_id'?")]
    [InlineData(VerbNames, "Unknown argument 'reference_idd'; did you mean 'id'?")]
    public async Task AnUnknownArgumentsNearestNameIsTheCallersName(string? rename, string problem)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = Call(client, "describe_device", """{"reference_id": "5", "reference_idd": "5"}""", rename);

        Assert.Equal(problem, (string?)OnlyProblem(reply)["problem"]);
    }

    [Theory]
    [InlineData(null, "limit", "Argument 'limit' must be")]
    [InlineData(VerbNames, "count", "Argument 'count' must be")]
    public async Task AnInvalidValueIsNamedAsTheCallerSaysIt(string? rename, string path, string start)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JToken found = OnlyProblem(Call(client, "connections", """{"reference_id": "1", "limit": 99999}""", rename));

        Assert.Equal(path, (string?)found["path"]);
        Assert.StartsWith(start, (string?)found["problem"]);
    }

    [Theory]
    [InlineData(null, "items[0].bogus", "Unknown argument 'items[0].bogus'.")]
    [InlineData(VerbNames, "reads[0].bogus", "Unknown argument 'reads[0].bogus'.")]
    public async Task ANestedPathIsNamedAsTheCallerSaysIt(string? rename, string path, string problem)
    {
        using PipeRig rig = new PipeRig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = Call(client, "read_devices",
            """{"items": [{"reference_id": "5", "logic": ["On"], "bogus": 1}]}""", rename);

        JToken found = OnlyProblem(reply);
        Assert.Equal(path, (string?)found["path"]);
        Assert.StartsWith(problem, (string?)found["problem"]);
    }
}
