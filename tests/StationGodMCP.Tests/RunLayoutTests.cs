#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Helpers for the run tools' tests, in the game's cell units (small-grid neighbours 5 apart).</summary>
internal static class RunModels
{
    internal const int PowerAndData = 6;

    internal static GridCell At(int x, int y, int z) => CleanModels.At(x, y, z);

    internal static GridStep Step(string name)
    {
        Assert.True(GridStep.TryParse(name, out GridStep step));
        return step;
    }

    internal static EndSet Ends(params string[] names)
    {
        EndSet set = EndSet.None;
        foreach (string name in names)
        {
            set = set.With(Step(name));
        }

        return set;
    }

    /// <summary>A one-cell piece at the cell with ends towards the named directions.</summary>
    internal static PieceModel Piece(long id, GridCell cell, params string[] ends) =>
        EndSet.ModelAt(id, cell, Ends(ends), PowerAndData, 0, null);

    /// <summary>A device port: a piece standing in `local` joins it through its end towards `device`.</summary>
    internal static DevicePort Port(long device, int index, GridCell local, GridCell deviceCell, bool power = true) =>
        new DevicePort(device, index, new PieceEnd(local, deviceCell, PowerAndData, 0), power);
}

public sealed class GridStepTests
{
    [Fact]
    public void NeighboursAreFiveApartAlongOneAxis()
    {
        Assert.Equal("+x", GridStep.Between(RunModels.At(0, 0, 0), RunModels.At(1, 0, 0))!.Value.Name);
        Assert.Equal("-y", GridStep.Between(RunModels.At(0, 0, 0), RunModels.At(0, -1, 0))!.Value.Name);
        Assert.Null(GridStep.Between(RunModels.At(0, 0, 0), RunModels.At(1, 1, 0)));
        Assert.Null(GridStep.Between(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)));
    }

    [Theory]
    [InlineData("straight", "+x", "-x")]
    [InlineData("straight", "+y", "-y")]
    [InlineData("corner", "+x", "+z")]
    [InlineData("corner", "-y", "+z")]
    [InlineData("tee", "+x", "-x", "+z")]
    [InlineData("tee", "+y", "-y", "-x")]
    [InlineData("corner3", "+x", "+y", "+z")]
    [InlineData("cross", "+x", "-x", "+z", "-z")]
    [InlineData("cross", "+x", "-x", "+y", "-y")]
    [InlineData("corner4", "+x", "-x", "+z", "-y")]
    [InlineData("five_way", "+x", "-x", "+z", "-z", "+y")]
    [InlineData("six_way", "+x", "-x", "+y", "-y", "+z", "-z")]
    public void ShapesAreNamedByTheirEnds(string shape, params string[] ends) =>
        Assert.Equal(shape, RunModels.Ends(ends).Shape);

    [Fact]
    public void EndsAtACellComeFromTheModelsFacingCell()
    {
        PieceModel corner = RunModels.Piece(1, RunModels.At(2, 0, 0), "-x", "+y");
        Assert.Equal(RunModels.Ends("-x", "+y"), EndSet.AtCell(corner, RunModels.At(2, 0, 0)));
        Assert.True(EndSet.AtCell(corner, RunModels.At(3, 0, 0)).IsEmpty);
    }
}

public sealed class RunPathTests
{
    [Fact]
    public void WaypointsBecomeEveryCellAlongStraightLines()
    {
        List<GridCell>? cells = RunPath.FromWaypoints(
            new[] { RunModels.At(0, 0, 0), RunModels.At(2, 0, 0), RunModels.At(2, 0, 2), RunModels.At(2, 1, 2) },
            out string? error);
        Assert.Null(error);
        Assert.Equal(new[]
        {
            RunModels.At(0, 0, 0), RunModels.At(1, 0, 0), RunModels.At(2, 0, 0), RunModels.At(2, 0, 1),
            RunModels.At(2, 0, 2), RunModels.At(2, 1, 2)
        }, cells);
        Assert.Equal(2, RunPath.Bends(cells!));
        Assert.Equal(new[] { RunModels.At(0, 0, 0), RunModels.At(2, 0, 0), RunModels.At(2, 0, 2), RunModels.At(2, 1, 2) },
            RunPath.Waypoints(cells!));
    }

    [Fact]
    public void DiagonalWaypointsAreRefused()
    {
        Assert.Null(RunPath.FromWaypoints(new[] { RunModels.At(0, 0, 0), RunModels.At(2, 0, 2) }, out string? error));
        Assert.Contains("axis", error);
    }

    [Fact]
    public void ListedCellsMustBeNeighbours()
    {
        Assert.Null(RunPath.FromCells(new[] { RunModels.At(0, 0, 0), RunModels.At(2, 0, 0) }, out string? error));
        Assert.Contains("neighbours", error);
    }

    [Fact]
    public void ARunNeverVisitsACellTwice()
    {
        Assert.Null(RunPath.FromWaypoints(
            new[] { RunModels.At(0, 0, 0), RunModels.At(2, 0, 0), RunModels.At(1, 0, 0) }, out string? error));
        Assert.Contains("twice", error);
    }

    [Fact]
    public void CellsOffTheSmallGridAreRefused() =>
        Assert.Null(RunPath.FromCells(new[] { new GridCell(1, 0, 0) }, out _));

    [Fact]
    public void EachCellGetsEndsTowardsItsRunNeighbours()
    {
        List<GridCell> cells = RunPath.FromWaypoints(
            new[] { RunModels.At(0, 0, 0), RunModels.At(1, 0, 0), RunModels.At(1, 1, 0) }, out _)!;
        Dictionary<GridCell, EndSet> ends = RunPath.Ends(cells);
        Assert.Equal(RunModels.Ends("+x"), ends[RunModels.At(0, 0, 0)]);
        Assert.Equal(RunModels.Ends("-x", "+y"), ends[RunModels.At(1, 0, 0)]);
        Assert.Equal(RunModels.Ends("-y"), ends[RunModels.At(1, 1, 0)]);
    }
}

public sealed class RunLayoutTests
{
    private static readonly ExtraEnd[] NoExtra = new ExtraEnd[0];

    private static List<GridCell> Run(params GridCell[] waypoints) => RunPath.FromWaypoints(waypoints, out _)!;

    [Fact]
    public void AStraightRunInEmptySpaceGetsStraightsAndOpenEnds()
    {
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(3, 0, 0)),
            new RunSurroundings(), JoinMode.Ends, NoExtra, null);
        Assert.Empty(layout.Problems);
        Assert.Equal(4, layout.Cells.Count);
        foreach (LayoutCell cell in layout.Cells)
        {
            Assert.Equal("straight", cell.Ends.Shape);
            Assert.Equal(CellAction.Place, cell.Action);
        }

        Assert.Equal(2, layout.Warnings.Count);
        Assert.All(layout.Warnings, warning => Assert.Equal("open_end", warning.Code));
    }

    [Fact]
    public void ARunIn3DGetsCornersWhereItTurnsVertically()
    {
        RunLayout layout = RunLayoutPlanner.Plan(
            Run(RunModels.At(0, 0, 0), RunModels.At(1, 0, 0), RunModels.At(1, 2, 0), RunModels.At(1, 2, 1)),
            new RunSurroundings(), JoinMode.Ends, NoExtra, null);
        Assert.Equal(RunModels.Ends("-x", "+y"), layout.At(RunModels.At(1, 0, 0))!.Ends);
        Assert.Equal("straight", layout.At(RunModels.At(1, 1, 0))!.Ends.Shape);
        Assert.Equal(RunModels.Ends("-y", "+z"), layout.At(RunModels.At(1, 2, 0))!.Ends);
    }

    [Fact]
    public void ARunEndJoinsAnOpenEndPointingAtIt()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(50, RunModels.At(-1, 0, 0), "-x", "+x"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, null);
        LayoutCell first = layout.At(RunModels.At(0, 0, 0))!;
        Assert.Equal(RunModels.Ends("-x", "+x"), first.Ends);
        Assert.Contains(first.Joins, join => join.Kind == "piece" && join.TargetId == 50);
        Assert.Null(layout.At(RunModels.At(-1, 0, 0)));
    }

    [Fact]
    public void ARunEndMeetingTheSideOfAStraightTurnsItIntoATee()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(60, RunModels.At(3, 0, 0), "+z", "-z"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, null);
        Assert.Empty(layout.Problems);
        LayoutCell neighbour = layout.At(RunModels.At(3, 0, 0))!;
        Assert.False(neighbour.InRun);
        Assert.Equal(CellAction.Change, neighbour.Action);
        Assert.Equal(RunModels.Ends("+z", "-z", "-x"), neighbour.Ends);
        Assert.Equal("tee", neighbour.Ends.Shape);
        Assert.Equal(RunModels.Ends("-x", "+x"), layout.At(RunModels.At(2, 0, 0))!.Ends);
    }

    [Fact]
    public void JoinModeNoneLeavesTheStraightAlone()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(60, RunModels.At(3, 0, 0), "+z", "-z"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.None, NoExtra, null);
        Assert.Null(layout.At(RunModels.At(3, 0, 0)));
    }

    [Fact]
    public void ARunThroughAnExistingStraightMakesItACross()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(70, RunModels.At(1, 0, 0), "+z", "-z"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.None, NoExtra, null);
        LayoutCell crossed = layout.At(RunModels.At(1, 0, 0))!;
        Assert.Equal(CellAction.Change, crossed.Action);
        Assert.Equal("cross", crossed.Ends.Shape);
    }

    [Fact]
    public void ARunAlongAnExistingPieceKeepsIt()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(71, RunModels.At(1, 0, 0), "+x", "-x"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.None, NoExtra, null);
        Assert.Equal(CellAction.Keep, layout.At(RunModels.At(1, 0, 0))!.Action);
    }

    [Fact]
    public void AnUpwardRunMeetingAHorizontalCornerMakesACorner3()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(72, RunModels.At(0, 2, 0), "+x", "+z"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(0, 1, 0)), around,
            JoinMode.Ends, NoExtra, null);
        LayoutCell corner = layout.At(RunModels.At(0, 2, 0))!;
        Assert.Equal("corner3", corner.Ends.Shape);
        Assert.Equal(RunModels.Ends("+x", "+z", "-y"), corner.Ends);
    }

    [Fact]
    public void ARunEndJoinsADevicePortFacingIt()
    {
        RunSurroundings around = new RunSurroundings();
        around.Ports.Add(RunModels.Port(900, 0, RunModels.At(0, 0, 0), RunModels.At(0, -1, 0)));
        around.DeviceCells[RunModels.At(0, -1, 0)] = 900;
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, null);
        LayoutCell first = layout.At(RunModels.At(0, 0, 0))!;
        Assert.Equal(RunModels.Ends("+x", "-y"), first.Ends);
        Assert.Equal("corner", first.Ends.Shape);
        Assert.Contains(first.Joins, join => join.Kind == "port" && join.TargetId == 900 && join.PortIndex == 0);
    }

    [Fact]
    public void JoinModeAllJoinsOpenEndsAlongTheWholeRun()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(80, RunModels.At(1, 0, 1), "-z", "+z"));
        RunLayout ends = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, null);
        Assert.Equal("straight", ends.At(RunModels.At(1, 0, 0))!.Ends.Shape);
        RunLayout all = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.All, NoExtra, null);
        Assert.Equal("tee", all.At(RunModels.At(1, 0, 0))!.Ends.Shape);
    }

    [Fact]
    public void AnExplicitJoinTurnsTheNeighbourIntoAJunction()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(81, RunModels.At(1, 1, 0), "+x", "-x"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.None, new[] { new ExtraEnd(RunModels.At(1, 0, 0), RunModels.Step("+y")) }, null);
        Assert.Equal("tee", layout.At(RunModels.At(1, 0, 0))!.Ends.Shape);
        Assert.Equal(RunModels.Ends("+x", "-x", "-y"), layout.At(RunModels.At(1, 1, 0))!.Ends);
    }

    [Fact]
    public void BlockedCellsLongPiecesAndFixedPiecesAreRefused()
    {
        RunSurroundings around = new RunSurroundings();
        around.Blocked[RunModels.At(1, 0, 0)] = "a pipe fitting stands there";
        PieceModel longStraight = new PieceModel(90, new[] { RunModels.At(2, 0, 1), RunModels.At(2, 0, 2) },
            new[]
            {
                new PieceEnd(RunModels.At(2, 0, 0), RunModels.At(2, 0, 1), RunModels.PowerAndData, 0),
                new PieceEnd(RunModels.At(2, 0, 3), RunModels.At(2, 0, 2), RunModels.PowerAndData, 0)
            }, null);
        around.AddPiece(longStraight);
        around.AddPiece(RunModels.Piece(91, RunModels.At(4, 0, 0), "+z", "-z"));
        around.Fixed[91] = "device_mounted";
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(4, 0, 0)), around,
            JoinMode.None, new[] { new ExtraEnd(RunModels.At(2, 0, 0), RunModels.Step("+z")) }, null);
        List<string> codes = layout.Problems.ConvertAll(problem => problem.Code);
        Assert.Contains("cell_blocked", codes);
        Assert.Contains("cannot_change", codes);
        Assert.DoesNotContain("long_piece", codes);
        Assert.Equal("tee", layout.At(RunModels.At(2, 0, 0))!.Ends.Shape);
    }

    [Fact]
    public void AJoinIntoTheMiddleOfALongStraightIsRefused()
    {
        RunSurroundings around = new RunSurroundings();
        PieceModel longStraight = new PieceModel(92, new[] { RunModels.At(0, 1, -1), RunModels.At(0, 1, 0), RunModels.At(0, 1, 1) },
            new[]
            {
                new PieceEnd(RunModels.At(0, 1, -2), RunModels.At(0, 1, -1), RunModels.PowerAndData, 0),
                new PieceEnd(RunModels.At(0, 1, 2), RunModels.At(0, 1, 1), RunModels.PowerAndData, 0)
            }, null);
        around.AddPiece(longStraight);
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, -2, 0), RunModels.At(0, 0, 0)), around,
            JoinMode.Ends, NoExtra, null);
        Assert.Contains(layout.Problems, problem => problem.Code == "long_piece");
    }

    [Fact]
    public void PipesOfOtherContentAreNeverJoined()
    {
        PipeContent gas = new PipeContent(1, false);
        PipeContent liquid = new PipeContent(2, false);
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(EndSet.ModelAt(95, RunModels.At(3, 0, 0), RunModels.Ends("-x", "+x"), 1, 0, liquid));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, gas);
        Assert.Equal("straight", layout.At(RunModels.At(2, 0, 0))!.Ends.Shape);
        Assert.DoesNotContain(layout.At(RunModels.At(2, 0, 0))!.Joins, join => join.Kind == "piece");
    }

    [Fact]
    public void ASinglePieceTakesItsExplicitEnds()
    {
        RunLayout layout = RunLayoutPlanner.Plan(new[] { RunModels.At(0, 0, 0) }, new RunSurroundings(),
            JoinMode.None,
            new[]
            {
                new ExtraEnd(RunModels.At(0, 0, 0), RunModels.Step("+x")),
                new ExtraEnd(RunModels.At(0, 0, 0), RunModels.Step("+y")),
                new ExtraEnd(RunModels.At(0, 0, 0), RunModels.Step("+z"))
            }, null);
        Assert.Equal("corner3", layout.Cells[0].Ends.Shape);
    }
}

public sealed class PieceCatalogueTests
{
    [Fact]
    public void TheFirstPieceInKitOrderWithExactlyTheEndsIsChosen()
    {
        PieceCatalogue catalogue = new PieceCatalogue(new[]
        {
            new PieceOption(0, 0, RunModels.Ends("+z", "-z")),
            new PieceOption(0, 5, RunModels.Ends("+x", "-x")),
            new PieceOption(1, 0, RunModels.Ends("-x", "-z")),
            new PieceOption(2, 3, RunModels.Ends("+x", "-x")),
            new PieceOption(3, 0, RunModels.Ends("-x", "+x", "-z"))
        });
        PieceOption? straight = catalogue.Find(RunModels.Ends("-x", "+x"));
        Assert.Equal(0, straight!.Value.Piece);
        Assert.Equal(5, straight.Value.Rotation);
        Assert.Equal(3, catalogue.Find(RunModels.Ends("+x", "-x", "-z"))!.Value.Piece);
        Assert.Null(catalogue.Find(RunModels.Ends("+x", "-x", "+y", "-y", "+z")));
        Assert.Equal(new[] { "corner", "straight", "tee" }, catalogue.Shapes());
    }
}
