#nullable enable

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// output_file and fields: the sidecar's own arguments on every tool that can give a large reply. fields goes to the
/// mod as shape.fields; output_file never reaches the game and is written by the client library (OutputChoice,
/// OutputFolder), to the rules every client shares (clients/fixtures/output_file).
/// </summary>
public sealed class FileOutputTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sgm-output-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("find_things")]
    [InlineData("find_items")]
    [InlineData("item_totals")]
    [InlineData("connections")]
    [InlineData("grid_survey")]
    [InlineData("list_devices")]
    [InlineData("vault_contents")]
    [InlineData("network_snapshot")]
    [InlineData("plan_cable_route")]
    [InlineData("plan_pipe_route")]
    [InlineData("plan_chute_route")]
    [InlineData("place_cables")]
    [InlineData("place_structure")]
    [InlineData("remove_structure")]
    [InlineData("remove_pipes")]
    [InlineData("upgrade_cables")]
    [InlineData("rocket_status")]
    [InlineData("rocket_forecast")]
    [InlineData("rocket_flight_log")]
    [InlineData("lint_layout")]
    [InlineData("get_ic_source")]
    [InlineData("sample_logic")]
    public void EveryLargeReplyToolTakesBoth(string tool)
    {
        JsonElement properties = Program.InputSchemas[tool].GetProperty("properties");

        Assert.True(properties.TryGetProperty("output_file", out _), tool);
        Assert.True(properties.TryGetProperty("fields", out _), tool);
    }

    [Fact]
    public void EveryToolButTheSmallOnesTakesBoth()
    {
        foreach ((string tool, JsonElement schema) in Program.InputSchemas)
        {
            bool takes = schema.GetProperty("properties").TryGetProperty("output_file", out _);
            Assert.True(takes != ToolCatalogue.SmallReplies.Contains(tool), tool);
        }
    }

    [Theory]
    [InlineData("""{"output_file":true}""")]
    [InlineData("""{"output_file":"ores"}""")]
    [InlineData("""{"output_file":false,"fields":["reference_id"]}""")]
    public void TheSchemaTakesEveryForm(string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        Assert.Empty(ArgumentCheck.Problems(Program.InputSchemas["find_things"], document.RootElement));
    }

    [Fact]
    public void NeitherArgumentIsSentToTheGame()
    {
        SidecarArguments call = SidecarArguments.Take(Json(
            """{"prefab_contains":"Ore","output_file":"ores","fields":["reference_id"," position "]}"""));

        Assert.Equal("""{"prefab_contains":"Ore"}""", call.Forwarded.GetRawText());
        Assert.Equal("""["reference_id"," position "]""", call.Fields!.Value.GetRawText());
        Assert.Equal("ores.json", Assert.IsType<OutputTarget.Named>(Assert.IsType<OutputChoice.ToFile>(call.Output).Target).Name);
    }

    [Fact]
    public void FalseAnswersInTheReply()
    {
        Assert.IsType<OutputChoice.Inline>(SidecarArguments.Take(Json("""{"output_file":false}""")).Output);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a\b")]
    [InlineData("C:x")]
    [InlineData(".hidden")]
    [InlineData("a..b")]
    [InlineData("")]
    [InlineData("name with spaces")]
    public void AFileNameMustStayInTheFolder(string name)
    {
        Assert.IsType<OutputChoice.Refused>(OutputChoice.Of(Json(JsonSerializer.Serialize(name))));
    }

    [Fact]
    public void ARefusedNameIsToldTheFolderAndMachineTheFileGoesTo()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sg-output-where");
        string where = OutputFolder.From(folder, null).WhereFilesGo("L5PRO");

        Assert.Contains(Path.GetFullPath(folder), where);
        Assert.Contains("on L5PRO", where);
        OutputChoice.Refused refused = Assert.IsType<OutputChoice.Refused>(
            OutputChoice.Of(Json(JsonSerializer.Serialize(@"C:	mp\survey.json"))));
        Assert.Contains("no folders", refused.Message);
    }

    /// <summary>Every shared fixture, as the Python library runs them: the file name, the pointer, the file's content.</summary>
    [Fact]
    public void EverySharedFixture()
    {
        string[] fixtures = Directory.GetFiles(SharedFixtures, "*.json");
        Assert.True(fixtures.Length > 5);
        foreach (string path in fixtures)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            RunFixture(Path.GetFileName(path), document.RootElement);
        }
    }

    [Fact]
    public void FieldsKeepOnlyTheNamedKeysOfEachListEntry()
    {
        JsonElement reply = new FieldSelection(new[] { "reference_id", "position" }).Apply(Json(
            """{"count":2,"things":[{"reference_id":"1","prefab_name":"Ore","position":{"x":1}},{"reference_id":"2","held_in":[]}],"local_player":{"reference_id":"9","name":"LU"}}"""));

        Assert.Equal(
            """{"count":2,"things":[{"reference_id":"1","position":{"x":1}},{"reference_id":"2"}],"local_player":{"reference_id":"9"}}""",
            reply.GetRawText());
    }

    [Fact]
    public void AFieldNoEntryHasIsListed()
    {
        JsonElement reply = new FieldSelection(new[] { "reference_id", "positon" }).Apply(Json(
            """{"things":[{"reference_id":"1","position":{"x":1}}]}"""));

        Assert.Equal(new[] { "positon" },
            reply.GetProperty("fields_unmatched").EnumerateArray().Select(name => name.GetString()).ToArray());
    }

    [Fact]
    public void AnEmptyListReportsNoUnmatchedField()
    {
        JsonElement reply = new FieldSelection(new[] { "anything" }).Apply(Json("""{"things":[]}"""));

        Assert.False(reply.TryGetProperty("fields_unmatched", out _));
    }

    [Fact]
    public void TheFileHoldsTheWholeReplyAndThePointerItsShape()
    {
        OutputFolder folder = new(_folder);
        string big = new('x', 400);
        JsonElement reply = Json(
            $$$"""{"count":2,"total":270,"has_more":false,"things":[{"a":1},{"a":2}],"local_player":{"name":"LU"},"preflight":{"text":"{{{big}}}"}}""");

        JsonElement pointer = folder.Write("find_things", new OutputTarget.Named("ores.json"), reply);

        string path = pointer.GetProperty("output_file").GetString()!;
        Assert.Equal(Path.Combine(folder.Path, "ores.json"), path);
        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonSerializer.Serialize(reply), JsonSerializer.Serialize(written.RootElement));
        Assert.Equal(new FileInfo(path).Length, pointer.GetProperty("bytes").GetInt64());
        Assert.Equal("find_things", pointer.GetProperty("tool").GetString());
        Assert.Equal(2, pointer.GetProperty("counts").GetProperty("things").GetInt32());
        Assert.Equal(270, pointer.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal("LU", pointer.GetProperty("summary").GetProperty("local_player").GetProperty("name").GetString());
        Assert.Equal("preflight", pointer.GetProperty("in_file_only")[0].GetString());
        Assert.False(pointer.GetProperty("summary").TryGetProperty("preflight", out _));
    }

    [Fact]
    public void AnAutomaticNameCarriesTheTool()
    {
        OutputFolder folder = new(_folder);

        JsonElement first = folder.Write("grid_survey", new OutputTarget.Auto(), Json("""{"cells":[]}"""));
        JsonElement second = folder.Write("grid_survey", new OutputTarget.Auto(), Json("""{"cells":[]}"""));

        string name = Path.GetFileName(first.GetProperty("output_file").GetString()!);
        Assert.StartsWith("grid_survey-", name);
        Assert.EndsWith(".json", name);
        Assert.NotEqual(first.GetProperty("output_file").GetString(), second.GetProperty("output_file").GetString());
    }

    [Fact]
    public void TheFolderKeepsTheNewestFilesWithinItsBounds()
    {
        OutputFolder folder = new(_folder);
        Directory.CreateDirectory(_folder);
        DateTime now = DateTime.UtcNow;
        for (int index = 0; index < OutputFolder.MaximumFiles + 5; index++)
        {
            string file = Path.Combine(_folder, $"old-{index:000}.json");
            File.WriteAllText(file, "{}");
            File.SetLastWriteTimeUtc(file, now.AddMinutes(-index - 1));
        }

        string expired = Path.Combine(_folder, "expired.json");
        File.WriteAllText(expired, "{}");
        File.SetLastWriteTimeUtc(expired, now - OutputFolder.MaximumAge - TimeSpan.FromHours(1));
        string other = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(other, "kept: not ours");

        folder.Write("find_things", new OutputTarget.Named("fresh.json"), Json("{}"));

        Assert.Equal(OutputFolder.MaximumFiles, Directory.GetFiles(_folder, "*.json").Length);
        Assert.True(File.Exists(Path.Combine(_folder, "fresh.json")));
        Assert.True(File.Exists(Path.Combine(_folder, "old-000.json")));
        Assert.False(File.Exists(Path.Combine(_folder, $"old-{OutputFolder.MaximumFiles - 1:000}.json")));
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void AFailedWriteAnswersTheReplyInline()
    {
        File.WriteAllText(_folder, "a file where the folder should be");
        try
        {
            JsonElement reply = new OutputFolder(_folder).Write("find_things", new OutputTarget.Auto(),
                Json("""{"things":[{"a":1}]}"""));

            Assert.Equal(1, reply.GetProperty("things").GetArrayLength());
            Assert.StartsWith("Could not write ", reply.GetProperty("output_file_error").GetString());
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Fact]
    public void TheFolderComesFromTheOptionThenTheEnvironmentThenLocalAppData()
    {
        Assert.Equal(Path.GetFullPath("C:\\a"), OutputFolder.From("C:\\a", "C:\\b").Path);
        Assert.Equal(Path.GetFullPath("C:\\b"), OutputFolder.From(null, "C:\\b").Path);
        Assert.EndsWith(Path.Combine("StationGodMCP", "output"), OutputFolder.From(" ", null).Path);
    }

    [Fact]
    public async Task ACallWithBothWritesTheShapedReplyAndForwardsNeither()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok(
            """{"count":2,"things":[{"reference_id":"1","position":{"x":1}},{"reference_id":"2","position":{"x":2}}]}""", shaped: true));

        string? line = await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_things","arguments":{"prefab_contains":"ItemDirtyOre","fields":["reference_id","position"],"output_file":"ore"}}}""",
            new Program.GameTransportSettings(game.Target), new OutputFolder(_folder));

        FakeCall forwarded = Assert.Single(game.CallsTo("find_things"));
        Assert.Equal("""{"prefab_contains":"ItemDirtyOre"}""", forwarded.Params.GetRawText());
        Assert.Equal("""{"fields":["reference_id","position"]}""", forwarded.Shape!.Value.GetRawText());
        using JsonDocument reply = JsonDocument.Parse(line!);
        JsonElement pointer = reply.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(2, pointer.GetProperty("counts").GetProperty("things").GetInt32());
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "ore.json")));
        Assert.Equal("""{"reference_id":"2","position":{"x":2}}""",
            JsonSerializer.Serialize(file.RootElement.GetProperty("things")[1]));
    }

    [Fact]
    public void TheSidecarSuggestsTheNameAWordShort()
    {
        using JsonDocument document = JsonDocument.Parse("""{"prefab":"ItemDirtyOre"}""");

        Assert.StartsWith("Unknown argument 'prefab'; did you mean 'prefab_contains'?",
            Assert.Single(ArgumentCheck.Problems(Program.InputSchemas["list_containers"], document.RootElement)));
    }

    private static string SharedFixtures =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "clients", "fixtures", "output_file"));

    private void RunFixture(string name, JsonElement fixture)
    {
        JsonElement? given = fixture.TryGetProperty("output_file", out JsonElement value) ? value : null;
        OutputChoice choice = OutputChoice.Of(given);
        if (fixture.TryGetProperty("expected_error", out _))
        {
            Assert.True(choice is OutputChoice.Refused, name);
            return;
        }

        if (fixture.TryGetProperty("expected_inline", out _))
        {
            Assert.True(choice is OutputChoice.Inline, name);
            return;
        }

        string folder = Path.Combine(_folder, Path.GetFileNameWithoutExtension(name));
        JsonElement pointer = new OutputFolder(folder).Write(fixture.GetProperty("method").GetString()!,
            Assert.IsType<OutputChoice.ToFile>(choice).Target, fixture.GetProperty("result"));

        string written = pointer.GetProperty("output_file").GetString()!;
        Assert.Equal(Path.GetFullPath(folder), Path.GetDirectoryName(written));
        string fileName = Path.GetFileName(written);
        if (fixture.TryGetProperty("expected_file_name", out JsonElement expectedName))
        {
            Assert.Equal(expectedName.GetString(), fileName);
        }
        else
        {
            Assert.Matches(fixture.GetProperty("expected_file_name_pattern").GetString()!, fileName);
        }

        Assert.Equal(new FileInfo(written).Length, pointer.GetProperty("bytes").GetInt64());
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(written));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture.GetProperty("expected_file").GetRawText()),
            JsonNode.Parse(file.RootElement.GetRawText())), name);
        JsonObject withoutPath = JsonNode.Parse(pointer.GetRawText())!.AsObject();
        withoutPath.Remove("output_file");
        withoutPath.Remove("bytes");
        JsonObject expected = JsonNode.Parse(fixture.GetProperty("expected_pointer").GetRawText())!.AsObject();
        Assert.True(JsonNode.DeepEquals(expected, withoutPath), $"{name}: {withoutPath.ToJsonString()}");
        Assert.Equal(expected.Select(pair => pair.Key), withoutPath.Select(pair => pair.Key));
    }

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
