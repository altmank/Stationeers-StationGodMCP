#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Door keep-out and window zones around 2 m faces (1.4.3).</summary>
public sealed class OpeningsTests
{
    private static readonly IReadOnlyList<FaceOpening> None = new List<FaceOpening>();

    // A 1x1 door on the plane z = 678 at the face centred on (701, 203), id 983; a window at (705, 203), id 500.
    private static IReadOnlyList<FaceOpening> Faces(GridCell point, params (GridCell Face, FaceOpening Opening)[] faces)
    {
        List<FaceOpening> found = new List<FaceOpening>();
        foreach ((GridCell face, FaceOpening opening) in faces)
        {
            if (face.Equals(point))
            {
                found.Add(opening);
            }
        }

        return found.Count > 0 ? found : None;
    }

    private static readonly (GridCell, FaceOpening)[] OneDoorOneWindow =
    {
        (new GridCell(7010, 2030, 6780), new FaceOpening(OpeningKind.Door, 983)),
        (new GridCell(7050, 2030, 6780), new FaceOpening(OpeningKind.Window, 500)),
        (new GridCell(7030, 2030, 6780), new FaceOpening(OpeningKind.Wall, 7))
    };

    private static OpeningZone Zone(int x, int y, int z, int bandCells = 1, bool hidden = false,
        (GridCell, FaceOpening)[]? faces = null)
    {
        DoorBand band = DoorBand.FromMetres(bandCells * 0.5, out _)!.Value;
        (GridCell, FaceOpening)[] list = faces ?? OneDoorOneWindow;
        return OpeningZones.At(new GridCell(x, y, z), band, point => Faces(point, list), hidden);
    }

    [Theory]
    [InlineData(7010, 2030, 6780)] // the middle of the doorway
    [InlineData(7000, 2020, 6780)] // jamb meets threshold
    [InlineData(7020, 2040, 6780)] // jamb meets top edge
    [InlineData(7010, 2020, 6775)] // the band in front, on the floor
    [InlineData(7015, 2035, 6785)] // the band behind
    public void CellsOnTheDoorFaceAndInTheBandAreKeptOut(int x, int y, int z)
    {
        OpeningZone zone = Zone(x, y, z);
        Assert.True(zone.IsDoor);
        Assert.Equal(983, zone.Id);
    }

    [Theory]
    [InlineData(7025, 2030, 6780)] // beside the jamb
    [InlineData(7010, 2030, 6790)] // a metre in front
    [InlineData(7010, 2015, 6780)] // below the threshold, in the floor slab
    [InlineData(7010, 2045, 6780)] // above the top edge
    public void CellsOutsideTheDoorsRectangleOrBandAreClear(int x, int y, int z)
    {
        Assert.False(Zone(x, y, z).IsDoor);
    }

    [Fact]
    public void ACellHiddenInsideAFrameIsNeverKeptOut()
    {
        Assert.False(Zone(7010, 2020, 6780, hidden: true).IsDoor);
    }

    [Fact]
    public void ABandOfZeroKeepsOnlyTheFacePlane()
    {
        Assert.True(Zone(7010, 2030, 6780, bandCells: 0).IsDoor);
        Assert.False(Zone(7010, 2030, 6785, bandCells: 0).IsDoor);
        Assert.True(Zone(7010, 2030, 6790, bandCells: 2).IsDoor);
    }

    [Fact]
    public void AMultiCellDoorKeepsOutItsWholeRectangleIncludingTheSharedEdge()
    {
        (GridCell, FaceOpening)[] wide =
        {
            (new GridCell(7010, 2030, 6780), new FaceOpening(OpeningKind.Door, 1)),
            (new GridCell(6990, 2030, 6780), new FaceOpening(OpeningKind.Door, 1)),
            (new GridCell(7010, 2050, 6780), new FaceOpening(OpeningKind.Door, 1)),
            (new GridCell(6990, 2050, 6780), new FaceOpening(OpeningKind.Door, 1))
        };
        Assert.True(Zone(7000, 2040, 6780, faces: wide).IsDoor);
        Assert.True(Zone(6980, 2060, 6785, faces: wide).IsDoor);
        Assert.False(Zone(6975, 2040, 6780, faces: wide).IsDoor);
    }

    [Fact]
    public void AWindowTakesOnlyTheInsideOfItsSquareOnItsPlane()
    {
        OpeningZone zone = Zone(7050, 2030, 6780);
        Assert.True(zone.IsWindow);
        Assert.Equal(500, zone.Id);
        Assert.True(Zone(7045, 2025, 6780).IsWindow);
        Assert.False(Zone(7040, 2030, 6780).IsWindow);
        Assert.False(Zone(7050, 2030, 6785).IsWindow);
    }

    [Fact]
    public void AWallIsNeitherADoorNorAWindow()
    {
        Assert.Equal(OpeningZone.Clear, Zone(7030, 2030, 6780));
    }

    [Fact]
    public void ADoorWinsOverAWindowItsBandReaches()
    {
        (GridCell, FaceOpening)[] faces =
        {
            (new GridCell(7010, 2030, 6780), new FaceOpening(OpeningKind.Window, 2)),
            (new GridCell(7010, 2030, 6780), new FaceOpening(OpeningKind.Door, 3))
        };
        Assert.True(Zone(7010, 2030, 6780, faces: faces).IsDoor);
    }

    [Fact]
    public void AHorizontalHatchKeepsOutItsBandAboveAndBelow()
    {
        (GridCell, FaceOpening)[] hatch = { (new GridCell(7010, 2040, 6790), new FaceOpening(OpeningKind.Door, 9)) };
        Assert.True(Zone(7010, 2045, 6790, faces: hatch).IsDoor);
        Assert.True(Zone(7000, 2035, 6800, faces: hatch).IsDoor);
        Assert.False(Zone(7010, 2050, 6790, faces: hatch).IsDoor);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 1)]
    [InlineData(2.0, 4)]
    public void DoorBandsAreHalfMetreSteps(double metres, int cells)
    {
        Assert.Equal(cells, DoorBand.FromMetres(metres, out _)!.Value.Cells);
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(2.5)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void DoorBandsOutsideTheStepsAreRefused(double metres)
    {
        Assert.Null(DoorBand.FromMetres(metres, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void FacePointsLieOnOnePlaneAtAFaceCentre()
    {
        Assert.True(FacePoints.IsFace(new GridCell(7010, 2030, 6780)));
        Assert.Equal(2, FacePoints.AxisOf(new GridCell(7010, 2030, 6780)));
        Assert.Equal(0, FacePoints.AxisOf(new GridCell(7000, 2030, 6790)));
        Assert.False(FacePoints.IsFace(new GridCell(7010, 2030, 6790)));
        Assert.False(FacePoints.IsFace(new GridCell(7000, 2020, 6780)));
        Assert.False(FacePoints.IsFace(new GridCell(-10, -10, -10)));
        Assert.True(FacePoints.IsFace(new GridCell(-10, -20, 10)));
        Assert.Equal("z=678", FacePlane.Of(new GridCell(7010, 2030, 6780)).ToString());
    }

    [Fact]
    public void EveryFaceWhoseClosedSquareHoldsACoordinateIsFound()
    {
        Assert.Equal(new List<int> { 6990, 7010 }, OpeningZones.FaceCentres(7000));
        Assert.Equal(new List<int> { 7010 }, OpeningZones.FaceCentres(7005));
        Assert.Equal(new List<int> { 7010 }, OpeningZones.FaceCentres(7010));
        Assert.Equal(new List<int> { -10, 10 }, OpeningZones.FaceCentres(0));
    }

    [Fact]
    public void TheSurveyStringMarksKeepOutAndWindows()
    {
        List<OpeningZone> zones = new List<OpeningZone>
            { OpeningZone.DoorKeepOut(1), OpeningZone.Clear, OpeningZone.OnWindow(2) };
        Assert.Equal("xfg", OpeningZones.Overlay("ffw", zones));
    }

    [Fact]
    public void TheRouteGuardBlocksKeepOutReleasesEndsAndChargesWindows()
    {
        GridCell doorway = new GridCell(0, 0, 0);
        GridCell end = new GridCell(5, 0, 0);
        GridCell glass = new GridCell(10, 0, 0);
        GridCell open = new GridCell(15, 0, 0);
        OpeningZone ZoneOf(GridCell cell) =>
            cell.Equals(doorway) || cell.Equals(end) ? OpeningZone.DoorKeepOut(4)
            : cell.Equals(glass) ? OpeningZone.OnWindow(5)
            : OpeningZone.Clear;

        OpeningGuard guard = new OpeningGuard(ZoneOf, new[] { end }, false);
        System.Func<GridCell, CellCost> cost = guard.Guard(_ => CellCost.Of(1.0));
        Assert.False(cost(doorway).Passable);
        Assert.True(cost(end).Passable);
        Assert.Equal(1.0 + OpeningGuard.WindowPenalty, cost(glass).Cost);
        Assert.Equal(1.0, cost(open).Cost);
        Assert.Equal(new List<GridCell> { end }, guard.ReleasedInKeepOut());

        OpeningGuard allowed = new OpeningGuard(ZoneOf, new GridCell[0], true);
        Assert.True(allowed.Guard(_ => CellCost.Of(1.0))(doorway).Passable);
    }

    [Fact]
    public void OnlyReleasedEndsOnOrBesideTheRouteAreReported()
    {
        GridCell onRoute = new GridCell(0, 0, 0);
        GridCell diagonal = new GridCell(10, 5, 5);
        GridCell far = new GridCell(500, 0, 0);
        GridCell clearEnd = new GridCell(5, 0, 0);
        OpeningGuard guard = new OpeningGuard(
            cell => cell.Equals(clearEnd) ? OpeningZone.Clear : OpeningZone.DoorKeepOut(1),
            new[] { onRoute, diagonal, far, clearEnd }, false);

        List<GridCell> route = new List<GridCell> { onRoute, new GridCell(5, 0, 0) };
        Assert.Equal(new HashSet<GridCell> { onRoute, diagonal },
            new HashSet<GridCell>(guard.ReleasedInKeepOutBeside(route)));
        Assert.Equal(3, guard.ReleasedInKeepOut().Count);
    }

    [Fact]
    public void TheSurveyNamesEachFaceStructuresKindAndListsDoors()
    {
        string wall = WireCheck.New(new SurveyWallView("+z", new ThingView(new ThingId(983), "StructureGlassDoor",
            "Glass Door"), true, "door"));
        Assert.Equal("{\"face\":\"+z\",\"reference_id\":\"983\",\"prefab_name\":\"StructureGlassDoor\"," +
                     "\"blocks_air\":true,\"kind\":\"door\"}", wall);
        string door = WireCheck.New(new SurveyDoorView(new ThingView(new ThingId(983), "StructureGlassDoor",
                "Glass Door"), new List<PositionView> { new PositionView(701, 203, 678) }, "z=678", 0.5,
            new List<PositionView>()));
        Assert.Contains("\"plane\":\"z=678\",\"band_m\":0.5", door);
    }
}
