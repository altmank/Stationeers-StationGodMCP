#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Sidecar;

/// <summary>
/// MCP transcripts through the sidecar against an in-process fake game: the tool list from the catalogue and its
/// change, arguments checked by the mod (the sidecar checks only its own), fields sent as shape and never applied
/// again, output files and the inline limit, sample_logic, and several calls over one connection.
/// </summary>
public sealed class SidecarTranscriptTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sgm-sidecar-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAdvertisesAChangingToolList()
    {
        await using McpAdapter sidecar = Sidecar(new GameTarget.Pipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N")));

        JsonElement result = Result(await sidecar.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""));

        Assert.True(result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
        Assert.Equal(Program.ServerVersion, result.GetProperty("serverInfo").GetProperty("version").GetString());
        Assert.Equal(ToolCatalogue.Instructions, result.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task TheToolListIsTheBuiltInCataloguesBeforeAnyGame()
    {
        await using McpAdapter sidecar = Sidecar(new GameTarget.Pipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N")));

        JsonElement result = Result(await sidecar.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        Assert.Equal(ToolCatalogue.Tools.GetRawText(), result.GetProperty("tools").GetRawText());
        Assert.Equal(93, result.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task AnotherCatalogueChangesTheToolsAndTellsTheAgent()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.CatalogueHash = "sha256:" + new string('c', 64);
        game.CatalogueJson = ClientTests.CatalogueWithout("weather");
        ConcurrentQueue<string> notices = new();
        await using McpAdapter sidecar = Sidecar(game.Target, notice =>
        {
            notices.Enqueue(notice);
            return Task.CompletedTask;
        });

        await sidecar.HandleAsync(Call(1, "game_clock", "{}"));
        JsonElement tools = Result(await sidecar.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")).GetProperty("tools");

        Assert.Equal("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""", Assert.Single(notices));
        Assert.Equal(92, tools.GetArrayLength());
        Assert.DoesNotContain("weather", tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        JsonElement unknown = await Reply(sidecar, Call(3, "weather", "{}"));
        Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task OnVersionTwoABadArgumentIsTheModsToRefuse()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Method == "move_item"
            ? call.Error("invalid_argument", "Unknown argument 'quantty'; did you mean 'quantity'?")
            : call.Ok("{}"));
        await using McpAdapter sidecar = Sidecar(game.Target);
        await sidecar.HandleAsync(Call(1, "game_clock", "{}"));

        JsonElement result = Result(await sidecar.HandleAsync(Call(2, "move_item", """{"reference_id":"1","quantty":2}""")));

        AssertError(result, "invalid_argument");
        Assert.Equal("""{"reference_id":"1","quantty":2}""", Assert.Single(game.CallsTo("move_item")).Params.GetRawText());
    }

    [Fact]
    public async Task FieldsGoAsShapeAndAShapedReplyIsKeptAsItCame()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok("""{"things":[{"position":{"x":1}}]}""", shaped: true));
        await using McpAdapter sidecar = Sidecar(game.Target);

        JsonElement result = Result(await sidecar.HandleAsync(Call(1, "find_things", """{"kind":"structure","fields":["things.position.x"]}""")));

        FakeCall call = Assert.Single(game.CallsTo("find_things"));
        Assert.Equal("""{"kind":"structure"}""", call.Params.GetRawText());
        Assert.Equal("""{"fields":["things.position.x"]}""", call.Shape!.Value.GetRawText());
        Assert.Equal("""{"things":[{"position":{"x":1}}]}""", result.GetProperty("structuredContent").GetRawText());
    }

    [Fact]
    public async Task OnVersionTwoASelectorTheModWouldRefuseIsReportedUnmatched()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Method == "find_things"
            ? call.Ok("""{"things":[{"reference_id":"1"}]}""", shaped: true)
            : call.Ok("{}"));
        await using McpAdapter sidecar = Sidecar(game.Target);
        await sidecar.HandleAsync(Call(1, "game_clock", "{}"));

        JsonElement result = Result(await sidecar.HandleAsync(Call(2, "find_things",
            """{"fields":[" reference_id ","prefab-name","reference_id"]}""")));

        Assert.Equal("""{"fields":["reference_id"]}""", Assert.Single(game.CallsTo("find_things")).Shape!.Value.GetRawText());
        Assert.Equal("""{"things":[{"reference_id":"1"}],"fields_unmatched":["prefab-name"]}""",
            result.GetProperty("structuredContent").GetRawText());
    }

    [Fact]
    public async Task ALargeReplyGoesToAFileOnItsOwn()
    {
        await using FakeGame game = FakeGame.OnPipe();
        string big = "[" + string.Join(",", Enumerable.Range(0, 3000).Select(n => $$"""{"reference_id":"{{n}}","prefab_name":"{{new string('x', 80)}}"}""")) + "]";
        game.Answer = call => Task.FromResult<string?>(call.Ok($$"""{"count":3000,"things":{{big}}}"""));
        await using McpAdapter sidecar = Sidecar(game.Target);

        JsonElement pointer = Result(await sidecar.HandleAsync(Call(1, "find_things", "{}"))).GetProperty("structuredContent");

        Assert.True(pointer.GetProperty("auto_output_file").GetBoolean());
        Assert.Equal(3000, pointer.GetProperty("counts").GetProperty("things").GetInt32());
        Assert.True(pointer.GetProperty("bytes").GetInt64() > 300 * 1024);
        Assert.True(File.Exists(pointer.GetProperty("output_file").GetString()));
    }

    [Fact]
    public async Task AnInlineLimitOfZeroKeepsEveryReplyInline()
    {
        await using FakeGame game = FakeGame.OnPipe();
        string big = new('x', 300 * 1024);
        game.Answer = call => Task.FromResult<string?>(call.Ok($$"""{"text":"{{big}}"}"""));
        await using McpAdapter sidecar = new(new SidecarOptions(new ClientOptions(game.Target), new OutputFolder(_folder), 0));

        JsonElement reply = Result(await sidecar.HandleAsync(Call(1, "get_ic_source", """{"reference_id":"1"}"""))).GetProperty("structuredContent");

        Assert.Equal(big.Length, reply.GetProperty("text").GetString()!.Length);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task AnErrorIsNeverWrittenToAFile()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Error("thing_not_found", "No thing with reference id 9."));
        await using McpAdapter sidecar = Sidecar(game.Target);

        JsonElement result = Result(await sidecar.HandleAsync(Call(1, "find_things", """{"output_file":true}""")));

        AssertError(result, "thing_not_found");
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task SampleLogicGoesToTheMod()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok("""{"sample_count":3,"changes":[]}"""));
        await using McpAdapter sidecar = Sidecar(game.Target);

        JsonElement result = Result(await sidecar.HandleAsync(Call(1, "sample_logic", """{"targets":[{"reference_id":"1","logic_type":"On"}]}""")));

        Assert.Equal(3, result.GetProperty("structuredContent").GetProperty("sample_count").GetInt32());
        Assert.Single(game.CallsTo("sample_logic"));
        Assert.Empty(game.CallsTo("read_logic_many"));
    }

    [Fact]
    public async Task FiveToolCallsAreAnsweredOverOneConnection()
    {
        await using FakeGame game = FakeGame.OnPipe();
        TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Answer = async call =>
        {
            if (call.Method != "read_logic")
            {
                return call.Ok("{}");
            }

            if (game.CallsTo("read_logic").Count() == 5)
            {
                allArrived.TrySetResult();
            }

            await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return call.Ok("""{"value":1}""");
        };
        string input = string.Concat(Enumerable.Range(1, 5).Select(id =>
            Call(id, "read_logic", """{"reference_id":"1","logic_type":"On"}""") + "\n"));
        using MemoryStream output = new();

        await Program.ServeAsync(new SidecarOptions(new ClientOptions(game.Target), new OutputFolder(_folder), 200 * 1024),
            new MemoryStream(Encoding.UTF8.GetBytes(input)), output);

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Enumerable.Range(1, 5), lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetInt32()).Order());
        Assert.All(lines, line => Assert.False(JsonDocument.Parse(line).RootElement.GetProperty("result").GetProperty("isError").GetBoolean()));
        Assert.Equal(1, game.Connections);
    }

    [Fact]
    public async Task TheSidecarSignsInOverTcpWithTheSharedSecret()
    {
        await using FakeGame game = FakeGame.OnTcp();
        GameTarget.Tcp target = (GameTarget.Tcp)game.Target;
        SidecarOptions options = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse(
            ["--host", "127.0.0.1", "--port", target.Port.ToString(), "--client", "probe-read"],
            name => name == ClientOptions.DefaultSecretVariable ? FakeGame.TestSecret : null)).Options;
        await using McpAdapter sidecar = new(options);

        JsonElement result = Result(await sidecar.HandleAsync(Call(1, "mod_info", "{}")));

        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("probe-read", game.Received.Skip(1).First().GetProperty("client").GetProperty("name").GetString());
    }

    [Fact]
    public void TheOptionsKeepTodaysNamesAndDefaults()
    {
        SidecarOptions defaults = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse([], _ => null)).Options;
        SidecarOptions fromEnvironment = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse([],
            name => name switch
            {
                "STATIONGODMCP_PIPE_NAME" => "StationGodMCP-Test",
                "STATIONGODMCP_OUTPUT_DIR" => "C:\\out",
                _ => null
            })).Options;
        SidecarOptions given = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse(
            ["--pipe", "StationGodMCP-Test", "--output-dir", "C:\\o", "--inline-limit-kb", "0", "--secret-env", "S"], name => name == "S" ? "x" : null)).Options;

        Assert.Equal(new GameTarget.Pipe("StationGodMCP"), defaults.Client.Target);
        Assert.Equal(200 * 1024, defaults.InlineLimitBytes);
        Assert.Equal(new GameTarget.Pipe("StationGodMCP-Test"), fromEnvironment.Client.Target);
        Assert.Equal(Path.GetFullPath("C:\\out"), fromEnvironment.Output.Path);
        Assert.Equal(new GameTarget.Pipe("StationGodMCP-Test"), given.Client.Target);
        Assert.Equal(0, given.InlineLimitBytes);
        Assert.Equal("S", given.Client.SecretVariable);
        Assert.Equal(new GameTarget.Tcp("h", 8765),
            Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse(["--host", "h"], _ => null)).Options.Client.Target);
        Assert.IsType<SidecarOptions.Parsed.Invalid>(SidecarOptions.Parse(["--host", "h", "--port", "0"], _ => null));
        Assert.IsType<SidecarOptions.Parsed.Invalid>(SidecarOptions.Parse(["--inline-limit-kb", "-1"], _ => null));
    }

    private McpAdapter Sidecar(GameTarget target, Func<string, Task>? notify = null) =>
        new(new SidecarOptions(new ClientOptions(target), new OutputFolder(_folder), SidecarOptions.DefaultInlineLimitKb * 1024), notify);

    private static async Task<JsonElement> Reply(McpAdapter sidecar, string line)
    {
        using JsonDocument document = JsonDocument.Parse((await sidecar.HandleAsync(line))!);
        return document.RootElement.Clone();
    }

    private static JsonElement Result(string? line)
    {
        using JsonDocument document = JsonDocument.Parse(line!);
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertError(JsonElement result, string code)
    {
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(code, result.GetProperty("structuredContent").GetProperty("code").GetString());
    }

    private static string Call(int id, string tool, string arguments) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""";
}
