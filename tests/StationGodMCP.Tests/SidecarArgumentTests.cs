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
    [Fact]
    public void AMisspeltArgumentIsRefusedWithTheNearestName()
    {
        string problem = Assert.Single(Problems("move_item", """{"reference_id":"889","to_id":"332","to_slot":4,"quantty":2}"""));

        Assert.StartsWith("Unknown argument 'quantty'; did you mean 'quantity'?", problem);
    }

    [Fact]
    public void AnUnknownArgumentInsideABatchNamesItsPath()
    {
        string problem = Assert.Single(Problems("move_item", """{"moves":[{"reference_id":"1","to_id":"2","to_slot":0,"qty":1}]}"""));

        Assert.StartsWith("Unknown argument 'moves[0].qty' (known here: reference_id, quantity, to_id, to_slot, merge).", problem);
    }

    [Fact]
    public void AMisspeltFilterNoLongerWidensToTheWholeWorld()
    {
        Assert.Contains("did you mean 'name_contains'?", Assert.Single(Problems("list_devices", """{"name_contain":"DV1"}""")));
    }

    [Theory]
    [InlineData("write_logic", """{"reference_id":"377","logic_type":"Setting","value":5,"valu":6}""", "valu")]
    [InlineData("move_gas", """{"from":"323","to":"476","bogus":1,"dry_run":true}""", "bogus")]
    [InlineData("rooms", """{"bogus":1}""", "bogus")]
    [InlineData("solar_aim", """{"reference_id":"364","extra":1}""", "extra")]
    [InlineData("item_totals", """{"offset":1}""", "offset")]
    [InlineData("find_things", """{"reference_id":"5"}""", "reference_id")]
    [InlineData("remove_pipes", """{"reference_ids":["5"],"allow_contents":true}""", "allow_contents")]
    [InlineData("upgrade_pipes", """{"network_id":"5","grade":"gas"}""", "grade")]
    [InlineData("clean_pipes", """{"network_id":"5","bogus_arg":1}""", "bogus_arg")]
    [InlineData("place_structure", """{"prefab":"StructureFrameIron","at":[1,2,3],"free":true,"bogus":1}""", "bogus")]
    [InlineData("undo_job", """{"job_id":"place-9","free":true}""", "free")]
    public void EveryLiveTestCaseIsRefused(string tool, string arguments, string name)
    {
        Assert.StartsWith($"Unknown argument '{name}'", Assert.Single(Problems(tool, arguments)));
    }

    [Fact]
    public void EveryToolRefusesAnArgumentItDoesNotDeclare()
    {
        foreach (string tool in ToolNames())
        {
            Assert.True(Problems(tool, """{"zz_not_an_argument":1}""").Count(problem =>
                problem.StartsWith("Unknown argument 'zz_not_an_argument'", StringComparison.Ordinal)) == 1, tool);
        }
    }

    [Fact]
    public void EveryToolAsksNoArgumentsButItsRequiredOnes()
    {
        foreach (string tool in ToolNames())
        {
            string[] expected = Required(tool).Select(name => $"Argument '{name}' is required").ToArray();
            string[] problems = Problems(tool, "{}").Select(problem => problem.Split(new[] { '.', ':' })[0]).ToArray();

            Assert.True(expected.SequenceEqual(problems), tool);
        }
    }

    private static IEnumerable<string> Required(string tool) =>
        Program.InputSchemas[tool].TryGetProperty("required", out JsonElement required)
            ? required.EnumerateArray().Select(name => name.GetString()!)
            : Enumerable.Empty<string>();

    [Fact]
    public void AMistypedGuardFlagIsRefused()
    {
        Assert.Contains(
            "did you mean 'allow_breach'?",
            Assert.Single(Problems("remove_structure", """{"reference_ids":["448"],"allow_breech":true}""")));
    }

    [Fact]
    public void AnIdSentAsANumberIsRefusedWithTheQuotedForm()
    {
        Assert.Equal(
            "Argument 'reference_id' must be a string, e.g. \"364\" in quotes.",
            Assert.Single(Problems("solar_aim", """{"reference_id":364}""")));
    }

    [Fact]
    public void NullIsAnOmittedArgument()
    {
        Assert.Empty(Problems("list_devices", """{"name_contains":null,"gateway_id":null}"""));
    }

    [Theory]
    [InlineData("""{"reference_id":"1","to_id":"2","to_slot":"auto"}""")]
    [InlineData("""{"reference_id":"1","to_id":"2","to_slot":3}""")]
    public void EitherFormOfAOneOfPasses(string arguments)
    {
        Assert.Empty(Problems("move_item", arguments));
    }

    [Fact]
    public void AValueNoFormTakesNamesEveryForm()
    {
        Assert.Equal(
            "Argument 'to_slot' must be an integer or a string.",
            Assert.Single(Problems("move_item", """{"reference_id":"1","to_id":"2","to_slot":true}""")));
    }

    [Fact]
    public void TheObjectFormOfAOneOfExplainsItsOwnProblem()
    {
        Assert.StartsWith(
            "Unknown argument 'from.room_idd'; did you mean 'room_id'?",
            Assert.Single(Problems("move_gas", """{"from":{"room_idd":"4"},"delete":true}""")));
    }

    [Theory]
    [InlineData("""{"reference_id":"1","to_id":"2","to_slot":0,"quantity":1.5}""", "Argument 'quantity' must be an integer.")]
    [InlineData("""{"reference_id":"1","to_id":"2","to_slot":0,"merge":"yes"}""", "Argument 'merge' must be true or false.")]
    [InlineData("""{"moves":{}}""", "Argument 'moves' must be an array.")]
    public void TypesAreHeldToTheSchema(string arguments, string expected)
    {
        Assert.Equal(expected, Assert.Single(Problems("move_item", arguments)));
    }

    [Fact]
    public void AnObjectOfFreeKeysChecksEachValue()
    {
        Assert.Equal(
            "Argument 'genes.GrowthSpeedMultiplier' must be a number.",
            Assert.Single(Problems("plant_genes", """{"reference_id":"1","genes":{"GrowthSpeedMultiplier":"high"}}""")));
    }

    [Fact]
    public void EveryProblemIsListed()
    {
        Assert.Equal(2, Problems("move_item", """{"reference_id":1,"quantty":2}""").Count);
    }

    [Fact]
    public void ArgumentsMustBeAnObject()
    {
        Assert.Equal("The arguments must be a JSON object.", Assert.Single(Problems("game_clock", "[]")));
    }

    [Theory]
    [InlineData("name_contain", "name_contains")]
    [InlineData("min_moles", "min_mol")]
    [InlineData("qty", null)]
    [InlineData("colour", "color")]
    public void TheNearestNameIsASlipNotAnotherWord(string given, string? expected)
    {
        Assert.Equal(expected, ArgumentCheck.Nearest(given, new[] { "name_contains", "min_mol", "quantity", "color", "limit" }));
    }

    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }

    private static IEnumerable<string> ToolNames() => Program.InputSchemas.Keys;
}

/// <summary>Every tool error has one envelope; malformed lines are answered (live test 1.4.4).</summary>
public sealed class SidecarReplyTests
{
    [Fact]
    public async Task AnArgumentErrorHasTheEnvelope()
    {
        JsonElement result = Result(await Handle(Call(1, "move_item", """{"reference_id":"1","quantty":2}"""), AbsentPipe()));

        AssertError(result, "invalid_argument");
    }

    [Fact]
    public async Task SampleLogicRangeErrorsAreArgumentErrors()
    {
        JsonElement result = Result(await Handle(
            Call(2, "sample_logic", """{"targets":[{"reference_id":"1","logic_type":"On"}],"duration_seconds":0.05}"""),
            AbsentPipe()));

        AssertError(result, "invalid_argument");
        Assert.Equal("Argument 'duration_seconds' must be from 0.1 to 30.", result.GetProperty("structuredContent").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AMissingPipeSaysSo()
    {
        JsonElement result = Result(await Handle(Call(3, "game_clock", "{}"), AbsentPipe()));

        AssertError(result, "game_unavailable");
        Assert.StartsWith("No StationGodMCP pipe ", result.GetProperty("structuredContent").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheGamesOwnErrorHasTheSameEnvelope()
    {
        await using FakeGame game = FakeGame.OnPipe(FakeProtocol.OldMod);
        game.Answer = call => Task.FromResult<string?>(call.Error("thing_not_found", "No thing with reference id 9."));

        JsonElement result = Result(await Handle(Call(4, "describe_device", """{"reference_id":"9"}"""),
            new Program.GameTransportSettings(game.Target)));

        AssertError(result, "thing_not_found");
    }

    [Fact]
    public async Task ANonJsonLineIsAParseErrorWithItsId()
    {
        JsonElement reply = await Handle(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"write_logic","arguments":{"value":Infinity}}}""",
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
