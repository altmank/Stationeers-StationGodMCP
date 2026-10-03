#nullable enable

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Server;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// paint and fields. Reproduced: paint's catalogue said its reply stays small (x-shaping none), so its schema took no
/// fields or output_file, yet a call that passed fields anyway was neither refused nor checked: the sidecar's check of
/// its own arguments found no schema for fields and let it through, and the game shaped the reply. A tool's schema and
/// what it does disagreed, and paint's results (one per thing, up to 256) were not small. Now paint takes fields, omit
/// and output_file like every list tool, and a sidecar argument a tool's schema does not take is refused.
/// </summary>
public sealed class PaintShapingTests
{
    [Fact]
    public void PaintTakesTheShapingArguments()
    {
        JsonElement properties = Program.InputSchemas["paint"].GetProperty("properties");

        Assert.True(properties.TryGetProperty("fields", out _));
        Assert.True(properties.TryGetProperty("omit", out _));
        Assert.True(properties.TryGetProperty("output_file", out _));
    }

    [Fact]
    public async Task PaintFieldsReachTheGameAsShape()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok(
            """{"count":1,"results":[{"index":0,"ok":true}],"success_count":1,"error_count":0}""", shaped: true));

        string? line = await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"paint","arguments":{"reference_ids":["5"],"color":"Blue","fields":["index","ok"]}}}""",
            new Program.GameTransportSettings(game.Target));

        FakeCall forwarded = Assert.Single(game.CallsTo("paint"));
        Assert.Equal("""{"fields":["index","ok"]}""", forwarded.Shape!.Value.GetRawText());
        using JsonDocument reply = JsonDocument.Parse(line!);
        Assert.False(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ASidecarArgumentASmallReplyToolDoesNotTakeIsRefused()
    {
        string tool = ToolCatalogue.SmallReplies.First();
        await using FakeGame game = FakeGame.OnPipe();

        string? line = await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":""" + JsonSerializer.Serialize(tool) +
            ""","arguments":{"fields":["x"]}}}""",
            new Program.GameTransportSettings(game.Target));

        using JsonDocument reply = JsonDocument.Parse(line!);
        JsonElement result = reply.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("invalid_argument", result.GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Empty(game.CallsTo(tool));
    }
}
