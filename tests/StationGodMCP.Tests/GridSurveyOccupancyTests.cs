#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// grid_survey cells by default answer what occupies each 2 m cell (cell_detail occupancy): empty, the frame's id, the
/// kind of structure on each face and the small string while it shows anything. cell_detail full keeps the cell as it
/// was. A 125-cell page of a framed, walled hub drops from about 99 KB to about 13 KB.
/// </summary>
public sealed class GridSurveyOccupancyTests(ITestOutputHelper output)
{
    private const int PageCells = 125;
    private static readonly string Blank = new string('.', 64);

    [Fact]
    public void ACellHoldingNothingSaysEmptyAndNothingElse()
    {
        JObject cell = Wire(new SurveyOccupancyView(At(), null, new List<SurveyFace>(), Blank));

        Assert.True((bool)cell["empty"]!);
        Assert.Equal(new[] { "at", "empty" }, Keys(cell));
    }

    [Fact]
    public void AFramedCellNamesItsFrameAndIsNotEmpty()
    {
        JObject cell = Wire(new SurveyOccupancyView(At(), new ThingId(2234500), new List<SurveyFace>(), Blank));

        Assert.Equal("2234500", (string?)cell["frame_id"]);
        Assert.Null(cell["empty"]);
        Assert.Null(cell["small"]);
    }

    [Fact]
    public void FacesAreOneCharacterEachInStepOrderStrongestFirst()
    {
        List<SurveyFace> faces = new List<SurveyFace>
        {
            new SurveyFace(GridStep.All[0], OpeningKind.Wall),
            new SurveyFace(GridStep.All[0], OpeningKind.Door),
            new SurveyFace(GridStep.All[5], OpeningKind.Window),
            new SurveyFace(GridStep.All[5], OpeningKind.Wall),
            new SurveyFace(GridStep.All[2], OpeningKind.Wall)
        };

        JObject cell = Wire(new SurveyOccupancyView(At(), null, faces, Blank));

        Assert.Equal("x.w..g", (string?)cell["faces"]);
        Assert.Null(cell["empty"]);
    }

    [Fact]
    public void SmallIsKeptWhileAnySmallCellShowsSomething()
    {
        string piped = new string('.', 60) + "pppp";

        JObject cell = Wire(new SurveyOccupancyView(At(), null, new List<SurveyFace>(), piped));

        Assert.Equal(piped, (string?)cell["small"]);
        Assert.Null(cell["empty"]);
    }

    [Fact]
    public void ARocketsEmptyCellsShowButHoldNothing()
    {
        string rocket = new string('r', 64);

        JObject cell = Wire(new SurveyOccupancyView(At(), null, new List<SurveyFace>(), rocket));

        Assert.Equal(rocket, (string?)cell["small"]);
        Assert.True((bool)cell["empty"]!);
    }

    [Fact]
    public void CellDetailDefaultsToOccupancy()
    {
        Assert.Equal(SurveyCellDetail.Occupancy, SurveyCellDetails.Parse(new Args(new JObject())));
        Assert.Equal(SurveyCellDetail.Full, SurveyCellDetails.Parse(Args("""{"cell_detail":" Full "}""")));
    }

    [Fact]
    public void AnUnknownCellDetailIsRefusedWithTheWordsItTakes()
    {
        ApiException refused =
            Assert.Throws<ApiException>(() => SurveyCellDetails.Parse(Args("""{"cell_detail":"compact"}""")));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains("occupancy or full", refused.Message);
    }

    [Fact]
    public void AFullCellKeepsEverything()
    {
        JObject cell = Wire(FullCell(0));

        Assert.Equal(new[] { "at", "room_id", "frame", "walls", "small", "support" }, Keys(cell));
    }

    [Fact]
    public void TheSidecarTakesCellDetailAndRefusesCompact()
    {
        Assert.Empty(Problems("""{"min":[0,0,0],"max":[6,4,4],"cell_detail":"occupancy"}"""));
        Assert.Single(Problems("""{"min":[0,0,0],"max":[6,4,4],"cell_detail":"brief"}"""));
        Assert.Single(Problems("""{"min":[0,0,0],"max":[6,4,4],"compact":true}"""));
    }

    /// <summary>
    /// The 2026-10-08 call: sections ["cells"] over 125 cells of a framed hub, each with a frame, up to six face
    /// structures and some pipes and cables. Full it is about 99 KB; by occupancy about a seventh of that.
    /// </summary>
    [Fact]
    public void A125CellHubPageDropsToAboutASeventh()
    {
        List<SurveyCell> full = new List<SurveyCell>();
        List<SurveyCell> occupancy = new List<SurveyCell>();
        for (int index = 0; index < PageCells; index++)
        {
            full.Add(FullCell(index));
            occupancy.Add(OccupancyCell(index));
        }

        int before = Survey(full, SurveyLegends.Full).Length;
        int after = Survey(occupancy, SurveyLegends.Occupancy).Length;
        output.WriteLine($"grid_survey sections [cells], {PageCells} cells: full {before} bytes, occupancy {after} bytes");

        Assert.InRange(before, 90_000, 110_000);
        Assert.True(after <= 15_000, $"occupancy page is {after} bytes");
        Assert.True(after * 6 <= before, $"occupancy {after} bytes against full {before}");
    }

    // A hub cell: a frame, a structure on index % 7 of its faces (the fourth a window), and every third cell with runs.
    private static SurveyCellView FullCell(int index)
    {
        List<SurveyWallView> walls = new List<SurveyWallView>();
        for (int face = 0; face < index % 7; face++)
        {
            walls.Add(new SurveyWallView(GridStep.All[face].Name,
                new ThingView(new ThingId(1_234_500 + index * 10 + face), "StructureCompositeWallFlatCornerSquare", null),
                true, face == 3 ? "window" : "wall"));
        }

        return new SurveyCellView(CellAt(index), "1234567",
            new SurveyFrameView(new ThingView(new ThingId(2_234_500 + index), "StructureFrameIron", null), 2, 3,
                true, true), walls, Small(index), "eeeeffffiiiiffffeeeeffffiiiiffffeeeeffffiiiiffffeeeeffffiiiiwwww");
    }

    private static SurveyOccupancyView OccupancyCell(int index)
    {
        List<SurveyFace> faces = new List<SurveyFace>();
        for (int face = 0; face < index % 7; face++)
        {
            faces.Add(new SurveyFace(GridStep.All[face], face == 3 ? OpeningKind.Window : OpeningKind.Wall));
        }

        return new SurveyOccupancyView(CellAt(index), new ThingId(2_234_500 + index), faces, Small(index));
    }

    private static string Small(int index) => index % 3 == 0 ? new string('.', 48) + "ccccpppp....bbbb" : Blank;

    private static PositionView CellAt(int index) =>
        new PositionView(593 + index % 5 * 2, 211 + index / 25 * 2, 627 + index / 5 % 5 * 2);

    private static string Survey(List<SurveyCell> cells, string legend)
    {
        Slice<SurveyCell> page = Slice<SurveyCell>.Page(cells,
            PageRequest.From(Args("""{"limit":125}"""), 8, PageCells), cells.Count);
        SurveySections sections = SurveySections.Parse(Args("""{"sections":["cells"]}"""));
        return WireCheck.New(new GridSurveyView(page, Contents(), sections, legend));
    }

    private static SurveyContents Contents() =>
        new SurveyContents(new List<SurveyPieceView>(), new List<SurveyDeviceView>(), new List<object>(),
            new List<SurveyNetworkVisibilityView>(), new List<SurveyDoorView>());

    private static JObject Wire(object view) => JObject.Parse(WireCheck.New(view));

    private static string[] Keys(JObject json)
    {
        List<string> keys = new List<string>();
        foreach (JProperty property in json.Properties())
        {
            keys.Add(property.Name);
        }

        return keys.ToArray();
    }

    private static PositionView At() => new PositionView(593, 211, 627);

    private static Args Args(string json) => new Args(JObject.Parse(json));

    private static IReadOnlyList<string> Problems(string arguments)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(arguments);
        return StationGodMCP.Server.ArgumentCheck.Problems(StationGodMCP.Server.Program.InputSchemas["grid_survey"],
            document.RootElement);
    }
}
