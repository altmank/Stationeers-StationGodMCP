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
    [InlineData("\n")]
    [InlineData("\n\n  \n")]
    public void ATrailingLineEndAddsNoLine(string end)
    {
        string source = string.Join("\n", Enumerable.Repeat("yield", 128)) + end;

        Assert.Empty(Ic10Source.Notes(source, source));
        Assert.Equal(128, Ic10Source.EditorLines(source).Length);
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
        IcSelectorsView view = new IcSelectorsView(Place, DeviceViewFixtures.NewDevice(), new List<PinTargetView>(),
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

    [Fact]
    public async Task ARepeatedToolNameIsAnInvalidRequest()
    {
        JsonElement reply = await Handle(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read","name":"write","arguments":{}}}""");

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
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"write","arguments":{"what":"logic","id":"99999999","logic":"On","value":1e309}}}""");
        JsonElement structured = reply.GetProperty("result").GetProperty("structuredContent");

        Assert.Equal("invalid_argument", structured.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> Handle(string line)
    {
        string? reply = await Program.HandleMcpMessageAsync(line,
            Program.GameTransportSettings.ForPipe("StationGodMCP-absent-" + Guid.NewGuid().ToString("N")));
        using JsonDocument document = JsonDocument.Parse(reply!);
        return document.RootElement.Clone();
    }
}
