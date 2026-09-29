#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The fixes of 1.4.4, from the live test of 1.4.3 on Vulcan (2026-09-29): mesh boxes for face occupancy, seams, the
/// wall map and find_spot; the undo plan's pieces; the wall map's corner; the floor face plane; lint codes; bridging.
/// </summary>
public sealed class LiveTest144Tests
{
    private static GridStep S(string name) => RunModels.Step(name);

    [Fact]
    public void TheConsolesMeshCrossesTheSeamItsCellsDoNot()
    {
        MountRect cells = MountRect.Of(VisualClashTests.ConsoleCells, S("+z"))!;
        Assert.False(cells.CrossesSeam);
        MountRect body = MountRect.Of(VisualClashTests.ConsoleCells, S("+z"), VisualClashTests.ConsoleMesh)!;
        Assert.Equal("z=668", body.Plane.ToString());
        Assert.True(body.CrossesSeam);
        Assert.Equal(new List<GridCell> { new GridCell(7170, 2010, 6680), new GridCell(7190, 2010, 6680) },
            body.Faces());
    }

    [Fact]
    public void AMeshRimJustPastASeamIsStillOneSection()
    {
        Box3 cells = Box3.OfSmallCells(new List<GridCell> { new GridCell(7185, 2010, 6680) });
        Box3 rim = new Box3(new Vec3(717.95, 200.8, 667.95), new Vec3(718.7, 201.2, 668.2));
        Assert.False(MountRect.Of(cells, S("+z"), rim)!.CrossesSeam);
    }

    [Fact]
    public void FindSpotRejectsASpotUnderAnotherBodysMesh()
    {
        SpotRequirements require = new SpotRequirements(true, null, false, true, 0, true);
        SpotGeometry underConsole = new SpotGeometry(1, 0, 0, 1, 1.5, 0, 1);
        Assert.Contains(SpotSearch.Filter(underConsole, require), reason => reason.Contains("clashes with 1"));
        SpotRequirements allowOverlap = new SpotRequirements(true, null, false, true, 0, false);
        Assert.Empty(SpotSearch.Filter(underConsole, allowOverlap));
    }

    [Fact]
    public void TheWallMapKeysEveryCellTheConsolesMeshCovers()
    {
        // Seen from +z the right axis is -x (0) and up +y (1); cells every 0.5 m.
        FacePlane plane = FacePlane.Of(2, 6680);
        List<(double X, double Y)> covered = new List<(double, double)>();
        for (double x = 717.0; x <= 720.0; x += 0.5)
        {
            for (double y = 199.5; y <= 202.5; y += 0.5)
            {
                if (PlaneCells.Covers(VisualClashTests.ConsoleMesh, PlaneCells.CellBox(plane, S("+z"), 0, 1, x, y)))
                {
                    covered.Add((x, y));
                }
            }
        }

        // 3 x 3 cells, x 718..719 and y 200.5..201.5: not the 2 x 2 its registered cells give.
        Assert.Equal(9, covered.Count);
        Assert.Contains((718.0, 201.5), covered);
        Assert.DoesNotContain((719.5, 200.5), covered);
        Assert.False(PlaneCells.Covers(VisualClashTests.SensorAt(719.5, 200.5),
            PlaneCells.CellBox(plane, S("+z"), 0, 1, 719.0, 200.5)));
    }

    [Fact]
    public void AMeshOverACellHidesARunButNotAnotherBody()
    {
        Assert.Equal('B', new WallCell(FaceLook.Wall, 'c', false).WithBody('B').Symbol);
        Assert.Equal('B', new WallCell(FaceLook.Wall, '\0', false).WithBody('B').Symbol);
        Assert.Equal('A', new WallCell(FaceLook.Wall, 'A', false).WithBody('B').Symbol);
        Assert.False(new WallCell(FaceLook.Wall, '\0', false).WithBody('B').IsFree);
    }

    [Fact]
    public void AMapCellBoxReachesToTheViewersSide()
    {
        FacePlane plane = FacePlane.Of(2, 6680);
        Box3 plus = PlaneCells.CellBox(plane, S("+z"), 0, 1, 719, 201);
        Assert.Equal(667.75, plus.Min.Z, 6);
        Assert.Equal(668.75, plus.Max.Z, 6);
        Box3 minus = PlaneCells.CellBox(plane, S("-z"), 0, 1, 719, 201);
        Assert.Equal(667.25, minus.Min.Z, 6);
        Assert.Equal(668.25, minus.Max.Z, 6);
    }

    [Fact]
    public void TheWallMapsCornerLiesOnThePlane()
    {
        // top_left read {x: 722, y: 204, z: 0} on plane z = 668.
        Vec3 corner = PlaneCells.PointAt(FacePlane.Of(2, 6680), 0, 1, 722, 204);
        Assert.Equal(new Vec3(722, 204, 668), corner);
    }

    [Theory]
    [InlineData(200.13, "y=200")]
    [InlineData(199.9, "y=200")]
    [InlineData(200.29, "y=200")]
    [InlineData(200.5, null)]
    [InlineData(201.0, null)]
    public void AFloorHitNamesItsFacePlane(double y, string? plane)
    {
        Assert.Equal(plane, FacePlane.Near(1, y, MountRect.OnPlaneM)?.ToString());
    }

    [Fact]
    public void RunsAlongADoorHaveAKindNeutralCode()
    {
        Assert.Equal("run_along_door", LintCodes.RunAlongDoor);
        Assert.Equal(ConflictLevel.Info, LintCodes.LevelOf("run_along_door"));
        Assert.Throws<System.ArgumentException>(() => LintCodes.LevelOf("pipe_along_door"));
    }

    [Fact]
    public void APortWhoseNetworkDoesNotChangeIsNotBridging()
    {
        // Force field 122643's ports 0 and 1 were both on 255268; a run across its doorway joins 255268 again.
        const long network = 255268;
        const long door = 122643;
        NetworkEdit edit = new NetworkEdit();
        edit.AddNetwork(network);
        edit.AddPiece(-1, null);
        edit.Link(-1, network);
        edit.Ports.Add(new ForecastPort(door, 0, true, network, network));
        edit.Ports.Add(new ForecastPort(door, 1, true, network, network));
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        Assert.Empty(forecast.Bridges);
        Assert.All(edit.Ports, port => Assert.False(forecast.IsBridging(port)));
    }

    [Fact]
    public void APortTheEditJoinsToItsDevicesOtherPortIsBridging()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.AddNetwork(1000);
        edit.AddNetwork(2000);
        edit.AddPiece(-1, null);
        edit.Link(1000, -1);
        edit.Link(-1, 2000);
        ForecastPort input = new ForecastPort(500, 0, true, 1000, 1000);
        ForecastPort output = new ForecastPort(500, 1, true, 2000, 2000);
        ForecastPort data = new ForecastPort(600, 0, true, 1000, 1000);
        edit.Ports.Add(input);
        edit.Ports.Add(output);
        edit.Ports.Add(data);
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        Assert.True(forecast.IsBridging(input));
        Assert.True(forecast.IsBridging(output));
        Assert.False(forecast.IsBridging(data));
    }

    private static ThingSnapshot Piece(long id, string tool, string? grade, params (GridCell Cell, string[] Ends)[] cells)
    {
        List<PieceCell> parts = new List<PieceCell>();
        foreach ((GridCell cell, string[] ends) in cells)
        {
            parts.Add(new PieceCell(cell, RunModels.Ends(ends)));
        }

        return new ThingSnapshot(id, "StructureCableStraightH", new Vec3(719, 200, 667.5),
            CubeRotation.FromFacing(S("+x"), S("+y")), 0, null, new NetworkPiece(tool, grade, parts));
    }

    [Fact]
    public void RemovedNetworkPiecesAreRestoredByTheirPlaceToolPerGrade()
    {
        Dictionary<long, ThingSnapshot> snapshots = new Dictionary<long, ThingSnapshot>
        {
            [400] = Piece(400, "place_cables", "heavy", (RunModels.At(0, 0, 0), new[] { "+x", "-x" })),
            [401] = Piece(401, "place_cables", "normal", (RunModels.At(1, 0, 0), new[] { "-x", "+z" })),
            [402] = Piece(402, "place_cables", "heavy", (RunModels.At(2, 0, 0), new[] { "+x", "-x" })),
            [403] = new ThingSnapshot(403, "StructureWallLight", new Vec3(720, 201, 668),
                CubeRotation.FromFacing(S("+z"), S("+y")), 0, null)
        };
        JobFacts job = new JobFacts("run-9", "remove_cables", "applied", new List<(long, string?)>(),
            new List<long> { 400, 401, 402, 403 }, snapshots);
        UndoPlan plan = UndoPlanner.Plan(job, _ => null);
        Assert.True(plan.Ready);
        Assert.Equal(new List<long> { 403 }, plan.RestoreStructures.ConvertAll(snapshot => snapshot.Id));
        List<PieceRestore> groups = plan.RestorePieces;
        Assert.Equal(2, groups.Count);
        Assert.Equal(("place_cables", "heavy"), (groups[0].Tool, groups[0].Grade));
        Assert.Equal(new List<long> { 400, 402 }, groups[0].Pieces.ConvertAll(snapshot => snapshot.Id));
        Assert.Equal("normal", groups[1].Grade);
    }

    [Fact]
    public void ANetworkPieceNoCoilLaysIsRefusedNotPlacedUnguarded()
    {
        Dictionary<long, ThingSnapshot> snapshots = new Dictionary<long, ThingSnapshot>
        {
            [410] = Piece(410, "place_pipes", null, (RunModels.At(0, 0, 0), new[] { "+x", "-x" }))
        };
        JobFacts job = new JobFacts("run-10", "remove_pipes", "applied", new List<(long, string?)>(),
            new List<long> { 410 }, snapshots);
        UndoPlan plan = UndoPlanner.Plan(job, _ => null);
        Assert.False(plan.Ready);
        Assert.Contains(plan.Diverged, reason => reason.Contains("place_pipes"));
        Assert.Empty(plan.RestorePieces);
        Assert.Empty(plan.RestoreStructures);
    }

    [Fact]
    public void ThePiecesFormLaysSeparatePiecesWithTheirOwnEnds()
    {
        GridCell a = RunModels.At(0, 0, 0);
        GridCell b = RunModels.At(1, 0, 0);
        GridCell far = RunModels.At(5, 0, 0);
        RunShape shape = RunShape.Pieces(new List<GridCell> { a, b, far }, out string? error)!;
        Assert.Null(error);
        Assert.True(shape.IsTip(far));
        List<ExtraEnd> extra = new List<ExtraEnd>
        {
            new ExtraEnd(a, S("-x")), new ExtraEnd(a, S("+x")), new ExtraEnd(b, S("-x")), new ExtraEnd(b, S("+z")),
            new ExtraEnd(far, S("+y")), new ExtraEnd(far, S("-y"))
        };
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(70, RunModels.At(-1, 0, 0), "+x", "-x"));
        RunLayout layout = RunLayoutPlanner.Plan(shape, around, JoinMode.None, extra, null);
        Assert.Equal(RunModels.Ends("-x", "+x"), layout.At(a)!.Ends);
        Assert.Equal(RunModels.Ends("-x", "+z"), layout.At(b)!.Ends);
        Assert.Equal(RunModels.Ends("+y", "-y"), layout.At(far)!.Ends);
        Assert.Contains(layout.At(a)!.Joins, join => join.Kind == "piece" && join.TargetId == 70);
        Assert.Null(RunShape.Pieces(new List<GridCell> { a, a }, out string? twice));
        Assert.Contains("Two pieces", twice);
    }
}
