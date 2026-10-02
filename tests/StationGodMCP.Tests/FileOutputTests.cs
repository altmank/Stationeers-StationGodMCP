#nullable enable

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// 1.9.0 output_file and fields: the sidecar's own answer to a large reply, for every tool that can give one, written
/// once for all of them (ReplyShaping, OutputFolder) and never sent to the game.
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
        (ReplyShaping shaping, JsonElement forwarded) = ReplyShaping.Take(Json(
            """{"prefab_contains":"Ore","output_file":"ores","fields":["reference_id"," position "]}"""));

        Assert.Equal("""{"prefab_contains":"Ore"}""", forwarded.GetRawText());
        Assert.Equal(new[] { "reference_id", "position" }, shaping.Fields!.Names);
        Assert.Equal("ores.json", Assert.IsType<OutputTarget.Named>(shaping.Output).Name.Value);
    }

    [Fact]
    public void FalseAnswersInTheReply()
    {
        (ReplyShaping shaping, _) = ReplyShaping.Take(Json("""{"output_file":false}"""));

        Assert.Null(shaping.Output);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a\\b")]
    [InlineData("C:x")]
    [InlineData(".hidden")]
    [InlineData("a..b")]
    [InlineData("")]
    [InlineData("name with spaces")]
    public void AFileNameMustStayInTheFolder(string name)
    {
        ToolFailure refused = Assert.Throws<ToolFailure>(() =>
            ReplyShaping.Take(Json(JsonSerializer.Serialize(new { output_file = name }))));

        Assert.Equal("invalid_argument", refused.Code);
    }

    [Fact]
    public void FieldsKeepOnlyTheNamedKeysOfEachListEntry()
    {
        JsonElement reply = new FieldSelection(new[] { "reference_id", "position" }).Apply(Json(
            """{"count":2,"things":[{"reference_id":"1","prefab_name":"Ore","position":{"x":1}},{"reference_id":"2","held_in":[]}],"local_player":{"reference_id":"9","name":"LU"}}"""));

        Assert.Equal(
            """{"count":2,"things":[{"reference_id":"1","position":{"x":1}},{"reference_id":"2"}],"local_player":{"reference_id":"9","name":"LU"}}""",
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

        JsonElement pointer = folder.Write("find_things", new OutputTarget.Named(OutputFileNameOf("ores")), reply);

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

        folder.Write("find_things", new OutputTarget.Named(OutputFileNameOf("fresh")), Json("{}"));

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
        string pipeName = "StationGodMCP-test-" + Guid.NewGuid().ToString("N");
        await using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        string? forwarded = null;
        Task game = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using StreamReader reader = new(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using StreamWriter writer = new(server, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            forwarded = await reader.ReadLineAsync();
            await writer.WriteLineAsync(
                """{"id":"x","ok":true,"result":{"count":2,"things":[{"reference_id":"1","prefab_name":"ItemDirtyOre","position":{"x":1}},{"reference_id":"2","prefab_name":"ItemDirtyOre","position":{"x":2}}]}}""");
        });

        string? line = await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_things","arguments":{"prefab_contains":"ItemDirtyOre","fields":["reference_id","position"],"output_file":"ore"}}}""",
            Program.GameTransportSettings.ForPipe(pipeName), new OutputFolder(_folder));
        await game;

        using JsonDocument request = JsonDocument.Parse(forwarded!);
        Assert.Equal("""{"prefab_contains":"ItemDirtyOre"}""", request.RootElement.GetProperty("params").GetRawText());
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
            Assert.Single(ArgumentCheck.Problems(Program.InputSchemas["find_things"], document.RootElement)));
    }

    private static OutputFileName OutputFileNameOf(string name) =>
        Assert.IsType<OutputTarget.Named>(OutputTarget.Of(Json(JsonSerializer.Serialize(name)))).Name;

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
