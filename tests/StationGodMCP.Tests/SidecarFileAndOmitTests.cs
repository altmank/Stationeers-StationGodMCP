#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The sidecar's omit argument (sent as shape.omit, unparsed selectors reported in omit_unmatched) and its file
/// arguments (set_ic_source source_file: the file's text sent as source, so a 60 KB Lua source never passes through
/// the caller), and get_ic_source's output_file holding a 60 KB source.
/// </summary>
public sealed class SidecarFileAndOmitTests : IDisposable
{
    private static readonly string HubSource = "-- hub\n" + string.Concat(Enumerable.Repeat("local x = 1 -- é\n", 3600));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sgm-file-args-" + Guid.NewGuid().ToString("N"));

    public SidecarFileAndOmitTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task SourceFileIsSentAsSource()
    {
        string path = Path.Combine(_folder, "hub.lua");
        File.WriteAllText(path, HubSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        (FakeCall forwarded, JsonElement result) = await Call("set_ic_source",
            JsonSerializer.Serialize(new { reference_id = "300", source_file = path }), """{"source_length":1}""");

        Assert.Equal(HubSource, forwarded.Params.GetProperty("source").GetString());
        Assert.False(forwarded.Params.TryGetProperty("source_file", out _));
        Assert.Equal("300", forwarded.Params.GetProperty("reference_id").GetString());
        Assert.Equal(1, result.GetProperty("source_length").GetInt32());
    }

    [Theory]
    [InlineData("both")]
    [InlineData("relative")]
    [InlineData("missing")]
    [InlineData("not_text")]
    public async Task ASourceFileThatCannotBeSentIsRefusedBeforeTheGame(string problem)
    {
        string path = Path.Combine(_folder, "bad.lua");
        File.WriteAllBytes(path, new byte[] { 0x2D, 0x2D, 0xC3, 0x28 });
        object arguments = problem switch
        {
            "both" => new { reference_id = "300", source = "x", source_file = path },
            "relative" => new { reference_id = "300", source_file = "hub.lua" },
            "missing" => new { reference_id = "300", source_file = Path.Combine(_folder, "none.lua") },
            _ => (object)new { reference_id = "300", source_file = path }
        };

        await using FakeGame game = FakeGame.OnPipe();
        JsonElement result = await CallWith(game, "set_ic_source", JsonSerializer.Serialize(arguments));

        Assert.Equal("invalid_argument", result.GetProperty("code").GetString());
        Assert.Empty(game.CallsTo("set_ic_source"));
    }

    [Fact]
    public void TheSchemaTakesSourceOrSourceFile()
    {
        JsonElement schema = Program.InputSchemas["set_ic_source"];

        Assert.True(schema.GetProperty("properties").TryGetProperty("source_file", out _));
        Assert.DoesNotContain("source", schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.Contains("reference_id", schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public async Task OmitIsSentAsShapeOmitAndAnUnparsedOneIsReported()
    {
        (FakeCall forwarded, JsonElement result) = await Call("get_ic_status",
            """{"reference_id":"300","omit":["source"," runtime.registers ","a-b"],"fields":["index"]}""",
            """{"reference_id":"300","pins":[{"index":0}]}""", shaped: true);

        Assert.Equal("""{"reference_id":"300"}""", forwarded.Params.GetRawText());
        Assert.Equal("""{"fields":["index"],"omit":["source","runtime.registers"]}""", forwarded.Shape!.Value.GetRawText());
        Assert.Equal("""["a-b"]""", result.GetProperty("omit_unmatched").GetRawText());
    }

    [Fact]
    public void EveryShapedToolTakesOmit()
    {
        foreach (string tool in Program.InputSchemas.Keys)
        {
            bool shaped = !ToolCatalogue.SmallReplies.Contains(tool);
            Assert.Equal(shaped, Program.InputSchemas[tool].GetProperty("properties").TryGetProperty("omit", out _));
        }
    }

    [Fact]
    public async Task GetIcSourceToAFileAnswersAShortPointerForA60KbSource()
    {
        string reply = JsonSerializer.Serialize(new
        {
            gateway_id = "world", reference_id = "300", source = HubSource, line_number = 0, language = "lua",
            source_length = HubSource.Length
        });
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok(reply, false));

        string? line = await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_ic_source","arguments":{"reference_id":"300","output_file":"hub"}}}""",
            new Program.GameTransportSettings(game.Target), new OutputFolder(_folder));

        Assert.True(Encoding.UTF8.GetByteCount(line!) < 2048, $"{line!.Length} characters inline");
        Assert.DoesNotContain("local x", line);
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "hub.json")));
        Assert.Equal(HubSource, file.RootElement.GetProperty("source").GetString());
    }

    private async Task<(FakeCall Forwarded, JsonElement Result)> Call(string tool, string arguments, string result,
        bool shaped = false)
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok(result, shaped));
        JsonElement reply = await CallWith(game, tool, arguments);
        return (Assert.Single(game.CallsTo(tool)), reply);
    }

    private async Task<JsonElement> CallWith(FakeGame game, string tool, string arguments)
    {
        string? line = await Program.HandleMcpMessageAsync(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""",
            new Program.GameTransportSettings(game.Target), new OutputFolder(_folder));
        using JsonDocument reply = JsonDocument.Parse(line!);
        return reply.RootElement.GetProperty("result").GetProperty("structuredContent").Clone();
    }
}
