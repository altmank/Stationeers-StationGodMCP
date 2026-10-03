#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Device fixes from round 2 of the headless live test (2026-09-29): IC10 source notes, batch selectors counted over
/// the holder's data network, compile error fields, and the sidecar's checks for numbers past a double, keys given
/// twice, integers written 3.0 or 1e2, enums and the deprecated argument names.
/// </summary>
public sealed class DevicesRound2Tests
{
    private static readonly IcPlace Place =
        new IcPlace("world", new ThingId(939), new ThingView(new ThingId(939), "StructureCircuitHousing", "Housing"));

    [Fact]
    public void APlainSourceHasNoNotes()
    {
        Assert.Empty(Ic10Source.Notes("move r0 3\nyield\nj 0", "move r0 3\nyield\nj 0"));
    }

    [Fact]
    public void CrlfBecomesLf()
    {
        const string given = "move r0 3\r\ns db Setting r0\r\nyield\r\nj 1\r\n";
        string stored = Ic10Source.WithUnixLineEnds(given);

        Assert.Equal("move r0 3\ns db Setting r0\nyield\nj 1\n", stored);
        Assert.Equal("crlf_normalised", Assert.Single(Ic10Source.Notes(given, stored)).Code);
    }

    [Fact]
    public void NonAsciiIsNamed()
    {
        const string given = "# café ✓ 温度\nalias température r0";

        Assert.True(Ic10Source.HasNonAscii(given));
        Assert.Equal("non_ascii_replaced",
            Assert.Single(Ic10Source.Notes(given, "# caf? ? ??\nalias temp?rature r0")).Code);
    }

    [Theory]
    [InlineData(128, 10, null)]
    [InlineData(129, 10, "over_editor_lines")]
    [InlineData(10, 90, null)]
    [InlineData(10, 91, "over_editor_line_length")]
    public void TheEditorsLineLimitsAreWarnings(int lines, int width, string? expected)
    {
        string source = string.Join("\n", Enumerable.Repeat(new string('#', width), lines));

        Assert.Equal(expected, Ic10Source.Notes(source, source).Select(note => note.Code).SingleOrDefault());
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    public void TheEditorsSizeLimitIsAWarning(int size, bool warned)
    {
        // 64-character lines keep the line count and width inside the editor's limits.
        string source = string.Join("\n", Enumerable.Repeat(new string('#', 63), 64)).Substring(0, 4095);
        source += new string('#', size - source.Length);
        string[] codes = Ic10Source.Notes(source, source).Select(note => note.Code).ToArray();

        Assert.Equal(warned, codes.Contains("over_editor_size"));
    }

    [Fact]
    public void UniquenessCountsOnlyTheHousingsNetwork()
    {
        // Two sensors named alike, one on the housing's network: its selector is unique for the chip.
        BatchSelectors reach = new BatchSelectors(new (long, int, int?)[] { (1016, -1252983604, 77), (939, 5, 6) });

        Assert.Equal(1, reach.CountOf(-1252983604, 77));
        Assert.True(reach.Reaches(1016));
        Assert.False(reach.Reaches(2000));
        Assert.Equal(2, reach.DeviceCount);
    }

    [Fact]
    public void ADeviceListedTwiceCountsOnce()
    {
        BatchSelectors reach = new BatchSelectors(new (long, int, int?)[] { (1, 5, 6), (1, 5, 6), (2, 5, 6) });

        Assert.Equal(2, reach.CountOf(5, 6));
        Assert.Equal(0, reach.CountOf(5, null));
    }

    [Fact]
    public void AnUnreachedDeviceIsNeverUnique()
    {
        ThingView sensor = new ThingView(new ThingId(1026), "StructureGasSensor", "Outdoor");

        Assert.False(new StableSelectorView(sensor, 1, 2, 1, reachable: false).Unique);
        Assert.True(new StableSelectorView(sensor, 1, 2, 1, reachable: true).Unique);
        Assert.False(new StableSelectorView(sensor, 1, 2, 2, reachable: true).Unique);
    }

    [Fact]
    public void AHolderWithNoNetworkHasNoBatchCount()
    {
        IcSelectorsView view = new IcSelectorsView(Place, DeviceWireTests.NewDevice(), new List<PinTargetView>(),
            new List<AliasView>(), new List<StableSelectorView>(), null);
        JObject json = JObject.Parse(WireCheck.New(view));

        Assert.Equal(JTokenType.Null, json["batch_device_count"]!.Type);
    }

    [Fact]
    public void ACompileErrorHasItsOwnLineAndType()
    {
        ChipState state = new ChipState(0, true, "0", "None",
            "Error <color=red>UnrecognisedInstruction</color> at line 1\n", new CompileError(1, "UnrecognisedInstruction"));
        IcChip chip = new IcChip(IcChip.Ic10, new ThingView(new ThingId(940), "ItemIntegratedCircuit10", "IC10"),
            "move r0 3\nbogusop r1", null);
        List<SourceNote> notes = new List<SourceNote> { new SourceNote("crlf_normalised", "...") };
        JObject json = JObject.Parse(WireCheck.New(new IcSourceSetView(Place, chip, state, notes)));

        Assert.Equal(1, (int)json["compile_error_line"]!);
        Assert.Equal("UnrecognisedInstruction", (string?)json["compile_error_type"]);
        Assert.Equal("crlf_normalised", (string?)json["warnings"]![0]!["code"]);
    }

    [Fact]
    public void ACleanChipHasNoCompileError()
    {
        JObject json = JObject.Parse(WireCheck.New(new IcControlView(Place, new IcControlOutcome("step", true, 0),
            new ChipState(1, false, "", "None", "OK"),
            new IcChip(IcChip.Ic10, new ThingView(new ThingId(940), "ItemIntegratedCircuit10", "IC10"), "yield", null))));

        Assert.Equal(JTokenType.Null, json["compile_error_line"]!.Type);
        Assert.Equal(JTokenType.Null, json["compile_error_type"]!.Type);
    }

    [Theory]
    [InlineData("write_logic", """{"reference_id":"99999999","logic_type":"On","value":1e309}""", "value")]
    [InlineData("write_logic", """{"reference_id":"99999999","logic_type":"On","value":-1e309}""", "value")]
    [InlineData("write_memory", """{"reference_id":"939","start_address":20,"values":[1e309]}""", "values[0]")]
    public void ANumberPastADoubleIsRefused(string tool, string arguments, string path)
    {
        Assert.StartsWith($"Argument '{path}' must be a finite number",
            Assert.Single(Problems(tool, arguments)));
    }

    [Theory]
    [InlineData("set_ic_pins", """{"reference_id":"939","pins":{"d5":"1016","d5":"1026"}}""", "pins.d5")]
    [InlineData("read_logic", """{"reference_id":"939","reference_id":"1016","logic_type":"PrefabHash"}""", "reference_id")]
    public void AKeyGivenTwiceIsRefused(string tool, string arguments, string path)
    {
        Assert.Equal($"Argument '{path}' is given twice; name each key once.", Assert.Single(Problems(tool, arguments)));
    }

    [Theory]
    [InlineData("1e2")]
    [InlineData("3.0")]
    [InlineData("7")]
    public void AnIntegerMayHaveAnExponentOrAZeroFraction(string written)
    {
        string arguments = $$"""{"reference_id":"939","start_address":{{written}},"count":1}""";
        Assert.Empty(Problems("read_memory", arguments));
    }

    [Fact]
    public void AFractionIsStillNoInteger()
    {
        Assert.Equal("Argument 'start_address' must be an integer.",
            Assert.Single(Problems("read_memory", """{"reference_id":"939","start_address":2.5,"count":1}""")));
    }

    [Fact]
    public void AFractionalLogicTypeGetsNoQuoteHint()
    {
        Assert.Equal("Argument 'logic_type' must be a string or an integer.",
            Assert.Single(Problems("read_logic", """{"reference_id":"939","logic_type":12.5}""")));
    }

    [Theory]
    [InlineData("""{"reference_id":"939","action":"step"}""")]
    [InlineData("""{"reference_id":"939","action":"Pause"}""")]
    [InlineData("""{"reference_id":"939","action":" resume "}""")]
    public void AnEnumTakesAnyCaseAsTheModDoes(string arguments)
    {
        Assert.Empty(Problems("control_ic_execution", arguments));
    }

    [Fact]
    public void AValueTheEnumDoesNotListIsRefused()
    {
        Assert.Equal("Argument 'action' must be one of pause, step, resume, restart; did you mean 'pause'?",
            Assert.Single(Problems("control_ic_execution", """{"reference_id":"939","action":"paus"}""")));
        Assert.Equal("Argument 'action' must be one of pause, step, resume, restart.",
            Assert.Single(Problems("control_ic_execution", """{"reference_id":"939","action":"bogus"}""")));
    }

    [Fact]
    public void ANumberEnumComparesValues()
    {
        Assert.Empty(Problems("paste_blueprint", """{"name":"x","anchor":[0,0,0],"rotation":90.0}"""));
        Assert.StartsWith("Argument 'rotation' must be one of 0, 90, 180, 270",
            Assert.Single(Problems("paste_blueprint", """{"name":"x","anchor":[0,0,0],"rotation":45}""")));
    }

    [Fact]
    public void AnEnumInsideAOneOfStillTakesTheOtherForm()
    {
        Assert.Empty(Problems("move_item", """{"reference_id":"1","to_id":"2","to_slot":"AUTO"}"""));
        Assert.StartsWith("Argument 'to_slot' must be one of auto",
            Assert.Single(Problems("move_item", """{"reference_id":"1","to_id":"2","to_slot":"first"}""")));
    }

    [Theory]
    [InlineData("water_sources", """{"min_moles":2}""")]
    [InlineData("thing_health", """{"min_ratio":0.25}""")]
    public void TheDocumentedOldNamesAreDeclared(string tool, string arguments)
    {
        Assert.Empty(Problems(tool, arguments));
    }

    [Fact]
    public async Task ARepeatedToolNameIsAnInvalidRequest()
    {
        JsonElement reply = await Handle(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_logic","name":"write_logic","arguments":{}}}""");

        Assert.Equal(5, reply.GetProperty("id").GetInt32());
        Assert.Equal(-32600, reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("'name' is given twice", reply.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnObjectIdIsAnInvalidRequest()
    {
        JsonElement reply = await Handle("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""");

        Assert.Equal(JsonValueKind.Null, reply.GetProperty("id").ValueKind);
        Assert.Equal(-32600, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task AnOverflowingNumberIsAnArgumentErrorNotAnInternalOne()
    {
        JsonElement reply = await Handle(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"write_logic","arguments":{"reference_id":"99999999","logic_type":"On","value":1e309}}}""");
        JsonElement structured = reply.GetProperty("result").GetProperty("structuredContent");

        Assert.Equal("invalid_argument", structured.GetProperty("code").GetString());
    }

    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }

    private static async Task<JsonElement> Handle(string line)
    {
        string? reply = await Program.HandleMcpMessageAsync(line,
            Program.GameTransportSettings.ForPipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N")));
        using JsonDocument document = JsonDocument.Parse(reply!);
        return document.RootElement.Clone();
    }
}
