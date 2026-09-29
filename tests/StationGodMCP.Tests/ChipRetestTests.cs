#nullable enable

using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Chip fixes from the chip retest (2026-09-29): every long line named, lone CR line ends, no lbn example for a device
/// the chip cannot reach, and error_code without the game's rich-text tags.
/// </summary>
public sealed class ChipRetestTests
{
    private static readonly IcPlace Place =
        new IcPlace("world", new ThingId(338), new ThingView(new ThingId(338), "StructureCircuitHousing", "Housing"));

    private static string LongLineWarning(string source) =>
        Ic10Source.Notes(source, source).Single(note => note.Code == "over_editor_line_length").Message;

    [Fact]
    public void OneLongLineIsNamed()
    {
        string message = LongLineWarning("yield\n" + new string('#', 97));

        Assert.StartsWith("Line 1 (0-based) is longer", message);
    }

    [Fact]
    public void EveryLongLineIsNamed()
    {
        string message = LongLineWarning(new string('a', 97) + "\n" + new string('b', 97) + "\nyield");

        Assert.StartsWith("Lines 0, 1 (0-based) are longer", message);
    }

    [Fact]
    public void ManyLongLinesAreCountedWithTheFirstNamed()
    {
        string source = string.Join("\n", Enumerable.Repeat(new string('#', 91), 12));

        Assert.StartsWith("12 lines (0-based, the first 10: 0, 1, 2, 3, 4, 5, 6, 7, 8, 9) are longer",
            LongLineWarning(source));
    }

    [Fact]
    public void ALoneCrBecomesLf()
    {
        const string given = "move r0 4\rs db Setting r0\ryield\rj 1";
        string stored = Ic10Source.WithUnixLineEnds(given);

        Assert.Equal("move r0 4\ns db Setting r0\nyield\nj 1", stored);
        Assert.Equal("cr_normalised", Assert.Single(Ic10Source.Notes(given, stored)).Code);
    }

    [Fact]
    public void MixedLineEndsNameBoth()
    {
        const string given = "move r0 4\r\nyield\rj 1";
        string stored = Ic10Source.WithUnixLineEnds(given);

        Assert.Equal("move r0 4\nyield\nj 1", stored);
        Assert.Equal(new[] { "crlf_normalised", "cr_normalised" },
            Ic10Source.Notes(given, stored).Select(note => note.Code));
    }

    [Fact]
    public void CrlfAloneIsNotALoneCr()
    {
        const string given = "move r0 4\r\nyield\r\n";

        Assert.Equal("crlf_normalised",
            Assert.Single(Ic10Source.Notes(given, Ic10Source.WithUnixLineEnds(given))).Code);
    }

    [Fact]
    public void AnUnreachableDeviceHasNoExample()
    {
        ThingView sensor = new ThingView(new ThingId(442), "StructureGasSensor", "Coolant Room Sensor");
        StableSelectorView unreachable = new StableSelectorView(sensor, -1252983604, -582601036, 1, reachable: false);
        StableSelectorView reachable = new StableSelectorView(sensor, -1252983604, -582601036, 1, reachable: true);

        Assert.Null(unreachable.Ic10Example);
        Assert.False(unreachable.Unique);
        Assert.Equal(1, unreachable.CollisionCount);
        Assert.Equal("lbn r0 -1252983604 -582601036 <LogicType> Average", reachable.Ic10Example);
    }

    [Fact]
    public void CollisionsAreCountedOverReachableDevicesOnly()
    {
        BatchSelectors reach = new BatchSelectors(new (long, int, int?)[] { (377, 1, 2), (378, 1, 3) });

        Assert.False(reach.Reaches(442));
        Assert.Equal(1, reach.CountOf(1, 2));
        Assert.Equal(0, reach.CountOf(1, 4));
    }

    [Fact]
    public void ErrorCodeIsPlainText()
    {
        ChipState state = new ChipState(0, true, "0", "None",
            "Error <color=red>IncorrectArgumentCount</color> at line 0\n", new CompileError(0, "IncorrectArgumentCount"));
        IcChip chip = new IcChip(IcChip.Ic10, new ThingView(new ThingId(339), "ItemIntegratedCircuit10", "IC10"),
            "move r0 4 s db Setting r0", null);
        JObject json = JObject.Parse(WireCheck.New(new IcSourceSetView(Place, chip, state, new())));

        Assert.Equal("Error IncorrectArgumentCount at line 0", (string?)json["error_code"]);
        Assert.Equal(0, (int)json["compile_error_line"]!);
        Assert.Equal("IncorrectArgumentCount", (string?)json["compile_error_type"]);
    }

    [Fact]
    public void AnEmptyErrorCodeStaysEmpty()
    {
        Assert.Equal("OK", new ChipState(1, false, "", "None", "OK").ErrorCode);
        Assert.Equal(string.Empty, new ChipState(0, false, "", "None", string.Empty).ErrorCode);
        Assert.Null(new ChipState(0, false, "", "None", null).ErrorCode);
    }
}
