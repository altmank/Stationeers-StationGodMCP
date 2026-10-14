#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Server;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The sidecar holds every tool call to the input schema tools/list publishes (live test 1.4.4).</summary>
public sealed class SidecarArgumentTests
{

    [Theory]
    [InlineData("name_contain", "name_contains")]
    [InlineData("min_moles", "min_mol")]
    [InlineData("qty", null)]
    [InlineData("colour", "color")]
    public void TheNearestNameIsASlipNotAnotherWord(string given, string? expected)
    {
        Assert.Equal(expected, ArgumentCheck.Nearest(given, new[] { "name_contains", "min_mol", "quantity", "color", "limit" }));
    }

}

/// <summary>Every tool error has one envelope; malformed lines are answered (live test 1.4.4).</summary>
public sealed class SidecarReplyTests
{
    [Fact]
    public async Task AMissingPipeSaysSo()
    {
        JsonElement result = Result(await Handle(Call(3, "read", """{"what":"clock"}"""), AbsentPipe()));

        AssertError(result, "game_unavailable");
        Assert.StartsWith("No StationGodMCP pipe ", result.GetProperty("structuredContent").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheGamesOwnErrorHasTheSameEnvelope()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Error("thing_not_found", "No thing with reference id 9."));

        JsonElement result = Result(await Handle(Call(4, "inspect", """{"what":"device","id":"9"}"""),
            new Program.GameTransportSettings(game.Target)));

        AssertError(result, "thing_not_found");
    }

    [Fact]
    public async Task ANonJsonLineIsAParseErrorWithItsId()
    {
        JsonElement reply = await Handle(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"write","arguments":{"what":"logic","value":Infinity}}}""",
            AbsentPipe());

        Assert.Equal(7, reply.GetProperty("id").GetInt32());
        Assert.Equal(-32700, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ALineWithNoReadableIdIsAParseErrorWithANullId()
    {
        JsonElement reply = await Handle("nope", AbsentPipe());

        Assert.Equal(JsonValueKind.Null, reply.GetProperty("id").ValueKind);
        Assert.Equal(-32700, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task JsonThatIsNoRequestIsAnInvalidRequest()
    {
        JsonElement reply = await Handle("[1]", AbsentPipe());

        Assert.Equal(-32600, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static void AssertError(JsonElement result, string code)
    {
        Assert.True(result.GetProperty("isError").GetBoolean());
        JsonElement structured = result.GetProperty("structuredContent");
        Assert.Equal(code, structured.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.String, structured.GetProperty("message").ValueKind);
        using JsonDocument text = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(structured.GetRawText(), text.RootElement.GetRawText());
    }

    private static JsonElement Result(JsonElement reply) => reply.GetProperty("result");

    private static async Task<JsonElement> Handle(string line, Program.GameTransportSettings transport)
    {
        string? reply = await Program.HandleMcpMessageAsync(line, transport);
        using JsonDocument document = JsonDocument.Parse(reply!);
        return document.RootElement.Clone();
    }

    private static string Call(int id, string tool, string arguments) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""";

    private static Program.GameTransportSettings AbsentPipe() =>
        Program.GameTransportSettings.ForPipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N"));
}
