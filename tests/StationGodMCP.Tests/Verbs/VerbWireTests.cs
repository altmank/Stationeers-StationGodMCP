#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// Verb calls over a real pipe (PLAN 2.0 wire form {"method": "&lt;verb&gt;", "params": {"what", ...options}}): the
/// reader thread admits a verb whose (verb, what) has a handler and checks its options; the main thread binds the
/// what's options class and runs the handler, whose reply the shared writers built and the call's shape shapes. A
/// verb, what or per-tool name with no handler is method_not_found, listing the verbs; the protocol methods go on.
/// </summary>
public sealed class VerbWireTests
{
    // read logic: a row per id, "404" not found; the reply's gateway_id from the option.
    private static readonly VerbHandler ReadLogic = VerbHandler.For(ReadLogicOptions.Spec, ReadLogicOptions.Bind,
        options =>
        {
            Rows rows = new();
            foreach (string id in options.Selector.Ids!.Ids)
            {
                if (id == "404")
                {
                    rows.Failed(id, new VerbError("not_found", $"No thing {id}."));
                }
                else
                {
                    rows.Succeeded(id, new JObject { ["logic_type"] = options.Logic?.DeepClone(), ["value"] = 1.0 });
                }
            }

            return rows.For(options.Selector.Ids, new JObject { ["gateway_id"] = options.Gateway ?? "world" });
        });

    private static readonly VerbDispatcher Table = TestVerbs.Only(ReadLogic);

    // The fake main thread runs every verb call through VerbCalls, as the mod's dispatcher does.
    private static PipeRig Rig(VerbDispatcher? verbs = null, bool strict = true)
    {
        VerbDispatcher table = verbs ?? Table;
        PipeRig rig = new(settings: new ProtocolSettings(32, strictArguments: strict), verbs: table);
        VerbRunner runner = new(table, ProtocolSettings.DefaultMaxReplyBytes, static () => 1);
        rig.Mod.CallHook = (call, queueMs) => VerbCalls.Handles(call.Request.Method)
            ? VerbCalls.Run(runner, call, queueMs)
            : null;
        return rig;
    }

    private static string Message(JObject reply) => (string)reply["error"]!["message"]!;

    [Fact]
    public async Task AVerbCallRunsItsHandlerAndAnswersTheRows()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("r", "read",
            JObject.Parse("""{"what": "logic", "id": ["11", "404"], "logic": "On", "gateway": "gw1"}"""));

        Assert.True((bool)reply["ok"]!, reply.ToString());
        JObject result = (JObject)reply["result"]!;
        Assert.Equal("gw1", (string?)result["gateway_id"]);
        Assert.Equal(new[] { "11", "404" }, result["results"]!.Select(row => (string?)row["reference_id"]).ToArray());
        Assert.Equal("On", (string?)result["results"]![0]!["logic_type"]);
        Assert.Equal("not_found", (string?)result["results"]![1]!["error"]!["code"]);
        Assert.Equal(1, (int)result["success_count"]!);
        Assert.Equal(1, (int)result["error_count"]!);
        Assert.NotNull(reply["elapsed_ms"]);
    }

    [Fact]
    public async Task OneIdAnswersTheSingleShapeAndItsErrorIsTheRefusal()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject one = client.Call("o", "read", JObject.Parse("""{"what": "logic", "id": "11", "logic": "On"}"""));
        JObject missing = client.Call("m", "read", JObject.Parse("""{"what": "logic", "id": "404", "logic": "On"}"""));

        Assert.Equal("""{"gateway_id":"world","reference_id":"11","logic_type":"On","value":1.0,"truncated":[]}""",
            one["result"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.False((bool)missing["ok"]!);
        Assert.Equal("not_found", (string?)missing["error"]!["code"]);
        Assert.Equal("No thing 404.", Message(missing));
    }

    [Fact]
    public async Task TheCallsShapeShapesTheHandlersReply()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("f", "read",
            JObject.Parse("""{"what": "logic", "id": ["1", "2"], "logic": "On", "fields": ["results.reference_id"]}"""));

        Assert.True((bool)reply["shaped"]!);
        Assert.Equal("""[{"reference_id":"1"},{"reference_id":"2"}]""",
            reply["result"]!["results"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public async Task AValueAKindCannotUseIsRefusedBeforeTheHandlerRuns()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("e", "read", JObject.Parse("""{"what": "logic", "id": ["1", ""], "logic": "On"}"""));

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Equal("read logic: id[1]: an id is a thing's id, a non-empty string.", Message(reply));
    }

    [Fact]
    public async Task OnALenientConnectionTheMainThreadChecksTheOptions()
    {
        using PipeRig rig = Rig(strict: false);
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("l", "read", JObject.Parse("""{"what": "logic", "id": "1", "logc": "On"}"""));

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Equal(1, rig.Mod.Ran);
        Assert.Contains("logc: not an option of read logic (nearest: logic)", Message(reply));
    }

    [Fact]
    public async Task AnUnknownWhatIsAnOptionProblemNamingTheNearest()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("w", "read", JObject.Parse("""{"what": "logik", "id": "1"}"""));

        Assert.Equal("invalid_argument", (string?)reply["error"]!["code"]);
        Assert.Equal("read: what: 'logik' is not a what of read (nearest: logic).", Message(reply));
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task AWhatWithNoHandlerIsAnUnknownMethod()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("c", "read", TestVerbs.Clock());

        Assert.Equal("method_not_found", (string?)reply["error"]!["code"]);
        Assert.StartsWith("Unknown method 'read' with what 'clock': nothing ran.", Message(reply));
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task AVerbWithNoHandlerIsAnUnknownMethodWhateverItsOptions()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject plain = client.Call("f", "find", JObject.Parse("""{"what": "thing"}"""));
        JObject wrong = client.Call("g", "find", JObject.Parse("""{"what": "nothing", "bogus": 1}"""));

        Assert.Equal("method_not_found", (string?)plain["error"]!["code"]);
        Assert.StartsWith("Unknown method 'find': nothing ran.", Message(plain));
        Assert.Equal("method_not_found", (string?)wrong["error"]!["code"]);
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Theory]
    [InlineData("read_logic")]
    [InlineData("list_devices")]
    [InlineData("sample_logic")]
    [InlineData("mod_info")]
    [InlineData("run_script")]
    [InlineData("nonsense")]
    public async Task APerToolMethodIsRefusedListingTheVerbs(string method)
    {
        using PipeRig rig = Rig(TestVerbs.Every);
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("t", method, new JObject { ["reference_id"] = "1" });

        Assert.Equal("method_not_found", (string?)reply["error"]!["code"]);
        Assert.Equal(
            $"Unknown method '{method}': nothing ran. A call is a verb, {{\"method\": \"<verb>\", \"params\": " +
            "{\"what\": ..., options}}, with verb one of " + string.Join(", ", VerbSurface.VerbNames) + ".",
            Message(reply));
        Assert.Equal(0, rig.Mod.Ran);
    }

    [Fact]
    public async Task TheProtocolMethodsGoOn()
    {
        using PipeRig rig = Rig();
        using V2Client client = await V2Client.Connect(rig);

        JObject catalogue = client.Call("k", "catalogue", new JObject());
        JObject costs = client.Call("s", "stream_costs", new JObject());

        Assert.True((bool)catalogue["ok"]!);
        Assert.True((bool)costs["ok"]!, costs.ToString());
        Assert.NotNull(costs["result"]!["streams"]);
    }

    [Fact]
    public void AWriteVerbIsOrderedOnItsStreamAndAReadIsNot()
    {
        Assert.NotNull(WriteLogicOptions.Spec.Write);
        Assert.Equal("acts", WriteLogicOptions.Spec.Write!.Route);
        Assert.Null(ReadLogicOptions.Spec.Write);
        Assert.False(VerbSurface.Verb("write")!.ReadOnly);
        Assert.True(VerbSurface.Verb("read")!.ReadOnly);
    }
}
