#nullable enable

using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.HandlerSelectorTests;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>Two live-gate findings: the hub filtering a list the mod had cut, and "player" as a payer.</summary>
public sealed class LiveGateFixTests
{
    // list_devices as the mod answers it: 844 devices, cut to 25 unless the shape asks for more.
    private static GameReply Devices(GameCall call)
    {
        int asked = call.Shape is { } shape && JsonNode.Parse(shape.GetRawText())!["limit"]?["devices"] is { } limit ? (int)limit : 25;
        JsonArray devices = new();
        for (int index = 0; index < 844 && index < asked; index++)
        {
            devices.Add(new JsonObject
            {
                ["reference_id"] = (5000 + index).ToString(),
                ["prefab_name"] = index >= 841 ? "StructureMatterlinkCapacitor" : "StructureGasSensor"
            });
        }

        JsonObject reply = new() { ["devices"] = devices, ["count"] = devices.Count };
        if (devices.Count < 844)
        {
            reply["truncated"] = new JsonArray(new JsonObject
            {
                ["list"] = "devices", ["returned"] = devices.Count, ["total"] = 844,
                ["more"] = "pass list_limits {\"devices\": 844} (shape.limit on the pipe; max 100000)"
            });
        }

        return FakeGameLink.Ok(reply.ToJsonString());
    }

    [Fact]
    public void FindDeviceFiltersTheWholeListNotTheModsFirstCut()
    {
        FakeGameLink game = new() { Answer = Devices };

        JsonNode reply = game.Pipeline().Call("find", "device", """{"prefab":"*Capacitor*"}""").Done()!;

        Assert.Equal("""{"limit":{"devices":100000}}""", game.Single("list_devices").Shape!.Value.GetRawText());
        Assert.Equal(3, reply["devices"]!.AsArray().Count);
        Assert.Null(reply["truncated"]);
    }

    [Fact]
    public void TheModsTruncatedNotesAreSaidInVerbTerms()
    {
        FakeGameLink game = new()
        {
            Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"}],"truncated":[{"list":"things","returned":1,"total":900,"more":"pass list_limits {\"things\": 900} (shape.limit on the pipe; max 100000)"}]}""")
        };

        JsonNode reply = game.Pipeline().Call("find", "thing", "{}").Done()!;

        Assert.Equal("pass list_limits {\"things\": 900}", (string)reply["truncated"]![0]!["more"]!);
    }

    [Theory]
    [InlineData("build", "pipe", """{"grade":"insulated_gas","cells":[[1,2,3],[1.5,2,3]],"materials":"player"}""", "place_pipes", null)]
    [InlineData("build", "structure", """{"prefab":"StructureWallLight","at":[1,2,3],"materials":["player"]}""", "place_structure", null)]
    [InlineData("swap", "wall", """{"id":["5"],"into":"StructureCompositeWall","materials":["4001","player"]}""", "replace_walls", """["4001","77"]""")]
    [InlineData("remove", "structure", """{"id":["5"],"materials":"player"}""", "remove_structure", null)]
    public void PlayerAsAPayerIsTheHandlersDefaultOrThePlayersId(string verb, string what, string options, string handler,
        string? fromId)
    {
        GameCall sent = Sent(verb, what, options, handler);

        Assert.Equal(fromId, sent.Params()["from_id"]?.ToJsonString());
    }

    [Fact]
    public void RefundToPlayerIsThePlayersInventory()
    {
        Assert.Equal("inventory", (string)Sent("remove", "cable", """{"id":["5"],"refund_to":"player"}""", "remove_cables").Params()["refund_to"]!);
        Assert.Equal("""["4001","inventory"]""",
            Sent("remove", "pipe", """{"id":["5"],"refund_to":["4001","player"]}""", "remove_pipes").Params()["refund_to"]!.ToJsonString());
    }
}
