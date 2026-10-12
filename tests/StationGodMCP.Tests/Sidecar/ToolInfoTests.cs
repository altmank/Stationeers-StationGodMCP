#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Sidecar;

/// <summary>
/// The method catalogue's help (ToolHelp) in three levels and the see each mod error carries; through the sidecar, the
/// help verb answers the generated pages without the game.
/// </summary>
public sealed class ToolInfoTests
{
    private static readonly ToolHelp Help = ToolHelp.Of(StationGodMCP.Client.GameCatalogue.BuiltIn.Document);

    [Fact]
    public void TheRootListsTheSharedTopics()
    {
        JsonElement reply = Found(Help.Answer(null, null, null));

        Assert.Contains("tool_info", reply.GetProperty("text").GetString());
        Assert.Equal(Help.RootTopics, reply.GetProperty("topics").EnumerateArray().Select(entry => entry.GetProperty("topic").GetString()));
    }

    [Fact]
    public void AToolListsItsOwnAndItsSharedTopics()
    {
        JsonElement reply = Found(Help.Answer("tool_info", null, null));

        Assert.Equal("tool_info", reply.GetProperty("tool").GetString());
        Assert.Contains(reply.GetProperty("topics").EnumerateArray(), entry => entry.GetProperty("topic").GetString() == "levels");
    }

    [Fact]
    public void ATopicGivesItsTextAndSubtopicsAndASubtopicItsText()
    {
        JsonElement topic = Found(Help.Answer("tool_info", "levels", null));
        JsonElement subtopic = Found(Help.Answer("tool_info", "levels", "pointers"));

        Assert.Contains(topic.GetProperty("subtopics").EnumerateArray(), entry => entry.GetProperty("subtopic").GetString() == "pointers");
        Assert.Equal("pointers", subtopic.GetProperty("subtopic").GetString());
        Assert.Contains("(see positions)", subtopic.GetProperty("text").GetString());
    }

    [Fact]
    public void ASharedTopicAnswersAloneAndThroughATool()
    {
        JsonElement alone = Found(Help.Answer(null, "shaping", null));
        JsonElement throughTool = Found(Help.Answer("tool_info", "shaping", "fields"));

        Assert.False(alone.TryGetProperty("tool", out _));
        Assert.Equal("fields", throughTool.GetProperty("subtopic").GetString());
    }

    [Fact]
    public void AMissRefusesNamingTheNearestNamesAndPointsAtTheLevels()
    {
        HelpAnswer.Refused tool = Assert.IsType<HelpAnswer.Refused>(Help.Answer("plce_structure", null, null));
        HelpAnswer.Refused orphan = Assert.IsType<HelpAnswer.Refused>(Help.Answer(null, null, "fields"));

        Assert.Equal("help_not_found", tool.Code);
        Assert.Contains("place_structure", tool.Message);
        Assert.Equal(new HelpPointer("tool_info", "levels", null), tool.See);
        Assert.Equal("invalid_argument", orphan.Code);
    }

    [Fact]
    public async Task TheSidecarAnswersHelpFromTheGeneratedPagesWithoutTheGame()
    {
        await using McpAdapter sidecar = Sidecar();

        JsonElement result = Result(await sidecar.HandleAsync(Call(1, "help", """{"verb":"find"}""")));
        JsonElement missing = Result(await sidecar.HandleAsync(Call(2, "help", """{"topic":"pagng"}""")));

        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.StartsWith("List things in the world", result.GetProperty("structuredContent").GetProperty("text").GetString());
        Assert.True(missing.GetProperty("isError").GetBoolean());
        Assert.Equal("help_not_found", missing.GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Equal("paging",
            missing.GetProperty("structuredContent").GetProperty("data").GetProperty("nearest")[0].GetString());
    }

    [Fact]
    public async Task WithoutTheGameAVerbAnswersGameUnavailable()
    {
        await using McpAdapter sidecar = Sidecar();

        JsonElement unreachable = Result(await sidecar.HandleAsync(Call(1, "read", """{"what":"clock"}""")));

        Assert.True(unreachable.GetProperty("isError").GetBoolean());
        Assert.Equal("game_unavailable", unreachable.GetProperty("structuredContent").GetProperty("code").GetString());
    }

    [Fact]
    public void TheModsErrorsCarryTheSeeTheirCodeHas()
    {
        string json = ApiJson.WriteFresh(new ErrorView("thing_not_found", "No thing with reference id 4."));
        string unregistered = ApiJson.WriteFresh(new ErrorView("x", "y"));

        Assert.Equal("""{"code":"thing_not_found","message":"No thing with reference id 4.","see":{"topic":"errors","subtopic":"not_found"}}""", json);
        Assert.Equal("""{"code":"x","message":"y"}""", unregistered);
    }

    private static JsonElement Found(HelpAnswer answer) => Assert.IsType<HelpAnswer.Found>(answer).Reply;

    private static McpAdapter Sidecar() =>
        new(new SidecarOptions(new ClientOptions(new GameTarget.Pipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N"))),
            new OutputFolder(Path.Combine(Path.GetTempPath(), "sgm-toolinfo-" + Guid.NewGuid().ToString("N"))),
            SidecarOptions.DefaultInlineLimitKb * 1024));

    private static JsonElement Result(string? line)
    {
        using JsonDocument document = JsonDocument.Parse(line!);
        return document.RootElement.GetProperty("result").Clone();
    }

    private static string Call(int id, string tool, string arguments) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""";
}
