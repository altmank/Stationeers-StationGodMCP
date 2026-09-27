#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class RunShapeTests
{
    private static List<GridCell> Line(params (int X, int Y, int Z)[] cells) =>
        new List<(int X, int Y, int Z)>(cells).ConvertAll(cell => RunModels.At(cell.X, cell.Y, cell.Z));

    [Fact]
    public void ABranchAttachesToTheNamedCellWithAJunction()
    {
        // Main: power port cell (0,0,0) west to (-2,0,0). Branch: data port cell (0,0,2) south to (0,0,1), joining
        // the main run at its first cell.
        List<GridCell> main = Line((0, 0, 0), (-1, 0, 0), (-2, 0, 0));
        RunBranch branch = new RunBranch(Line((0, 0, 2), (0, 0, 1)), RunModels.At(0, 0, 0));

        RunShape shape = RunShape.Of(main, new[] { branch }, out string? error)!;

        Assert.Null(error);
        Assert.Equal(5, shape.Cells.Count);
        Assert.Equal(RunModels.Ends("-x", "+z"), shape.Ends[RunModels.At(0, 0, 0)]);
        Assert.Equal(RunModels.Ends("+z", "-z"), shape.Ends[RunModels.At(0, 0, 1)]);
        Assert.Equal(RunModels.Ends("-z"), shape.Ends[RunModels.At(0, 0, 2)]);
        Assert.True(shape.IsTip(RunModels.At(0, 0, 2)));
        Assert.False(shape.IsTip(RunModels.At(0, 0, 1)));
        Assert.Contains(shape.Legs, leg => leg.Cell.Equals(RunModels.At(0, 0, 1)) && leg.Toward.Name == "-z");
    }

    [Fact]
    public void ABranchMayNotReuseACellOrFloatFree()
    {
        List<GridCell> main = Line((0, 0, 0), (1, 0, 0));
        Assert.Null(RunShape.Of(main, new[] { new RunBranch(Line((1, 0, 0), (1, 0, 1)), null) }, out string? reuse));
        Assert.Contains("already", reuse);
        Assert.Null(RunShape.Of(main, new[] { new RunBranch(Line((5, 0, 5)), null) }, out string? floating));
        Assert.Contains("next to no cell", floating);
    }

    [Fact]
    public void TheTreeLaysOneJunctionAndJoinsEveryPort()
    {
        // Device 900: power port joined from (0,0,0), data port from (0,0,2), both towards +x.
        RunSurroundings around = new RunSurroundings();
        around.Ports.Add(RunModels.Port(900, 3, RunModels.At(0, 0, 0), RunModels.At(1, 0, 0)));
        around.Ports.Add(RunModels.Port(900, 2, RunModels.At(0, 0, 2), RunModels.At(1, 0, 2), false));
        around.AddPiece(RunModels.Piece(50, RunModels.At(-3, 0, 0), "-z", "+z"));
        RunShape shape = RunShape.Of(Line((0, 0, 0), (-1, 0, 0), (-2, 0, 0)),
            new[] { new RunBranch(Line((0, 0, 2), (0, 0, 1)), RunModels.At(0, 0, 0)) }, out _)!;

        RunLayout layout = RunLayoutPlanner.Plan(shape, around, JoinMode.Ends, new List<ExtraEnd>(), null);

        Assert.Empty(layout.Problems);
        Assert.Equal(RunModels.Ends("+x", "-x", "+z"), layout.At(RunModels.At(0, 0, 0))!.Ends);
        Assert.Equal(RunModels.Ends("+x", "-z"), layout.At(RunModels.At(0, 0, 2))!.Ends);
        Assert.Contains(layout.At(RunModels.At(0, 0, 2))!.Joins, join => join.Kind == "port" && join.PortIndex == 2);
        // The trunk straight ahead of the main run's far end becomes a junction: one link to the network.
        Assert.Equal(RunModels.Ends("-z", "+z", "+x"), layout.At(RunModels.At(-3, 0, 0))!.Ends);
    }

    [Fact]
    public void ARunEndingBesideASplitLongStraightJoinsItsSingle()
    {
        // A 5-long straight along x at z = 5 is split (its cells are fills); the run comes up from z = 3.
        Dictionary<GridCell, EndSet> fills = new Dictionary<GridCell, EndSet>();
        for (int x = 0; x <= 4; x++)
        {
            EndSet ends = RunModels.Ends("+x", "-x");
            fills[RunModels.At(x, 0, 5)] = ends;
        }

        RunShape shape = RunShape.Line(Line((2, 0, 3), (2, 0, 4))).WithFills(fills);
        RunLayout layout = RunLayoutPlanner.Plan(shape, new RunSurroundings(), JoinMode.Ends, new List<ExtraEnd>(),
            null);

        Assert.Equal(RunModels.Ends("+x", "-x", "-z"), layout.At(RunModels.At(2, 0, 5))!.Ends);
        Assert.Equal(RunModels.Ends("-z", "+z"), layout.At(RunModels.At(2, 0, 4))!.Ends);
        Assert.Equal(RunModels.Ends("+x", "-x"), layout.At(RunModels.At(0, 0, 5))!.Ends);
        Assert.True(shape.IsFill(RunModels.At(0, 0, 5)));
        Assert.DoesNotContain(layout.Problems, problem => problem.Code == "long_piece");
    }

    [Fact]
    public void TheSearchStopsAtTheNearestOfSeveralGoals()
    {
        List<RouteEnd> goals = new List<RouteEnd>
        {
            new RouteEnd(RunModels.At(10, 0, 0), EndSet.None, 3.0),
            new RouteEnd(RunModels.At(0, 0, 3), EndSet.None, 3.0)
        };
        RouteRules rules = new RouteRules(2.0, AxisOrder.Any, 100, RunModels.At(-20, -20, -20),
            RunModels.At(20, 20, 20));

        RouteResult result = RoutePlanner.FindAny(RouteEnd.Open(RunModels.At(0, 0, 0)), goals,
            _ => CellCost.Of(1.0), rules);

        Assert.NotNull(result.Cells);
        Assert.Equal(RunModels.At(0, 0, 3), result.Cells![result.Cells.Count - 1]);
        Assert.Equal(4, result.Cells.Count);
    }
}

public sealed class RunShapeSplitTests
{
    [Fact]
    public void AnExtraEndIntoASplitLongStraightJoinsItsSingle()
    {
        Dictionary<GridCell, EndSet> fills = new Dictionary<GridCell, EndSet>();
        for (int x = 0; x <= 2; x++)
        {
            fills[RunModels.At(x, 0, 5)] = RunModels.Ends("+x", "-x");
        }

        RunShape shape = RunShape.Line(new List<GridCell> { RunModels.At(1, 0, 3), RunModels.At(1, 0, 4) })
            .WithFills(fills);
        RunLayout layout = RunLayoutPlanner.Plan(shape, new RunSurroundings(), JoinMode.None,
            new List<ExtraEnd> { new ExtraEnd(RunModels.At(1, 0, 4), RunModels.Step("+z")) }, null);

        Assert.Equal(RunModels.Ends("+x", "-x", "-z"), layout.At(RunModels.At(1, 0, 5))!.Ends);
        Assert.Contains(layout.At(RunModels.At(1, 0, 4))!.Joins, join => join.Kind == "split");
    }

    [Fact]
    public void ManyGoalsUseTheBoxHeuristicAndStillFindTheNearest()
    {
        List<RouteEnd> goals = new List<RouteEnd>();
        for (int x = 0; x < 20; x++)
        {
            goals.Add(new RouteEnd(RunModels.At(x, 0, 8), EndSet.None, 3.0));
        }

        goals.Add(new RouteEnd(RunModels.At(0, 0, 2), EndSet.None, 3.0));
        RouteRules rules = new RouteRules(2.0, AxisOrder.Any, 100, RunModels.At(-20, -20, -20),
            RunModels.At(30, 30, 30));
        RouteResult result = RoutePlanner.FindAny(RouteEnd.Open(RunModels.At(0, 0, 0)), goals,
            _ => CellCost.Of(1.0), rules);

        Assert.Equal(RunModels.At(0, 0, 2), result.Cells![result.Cells.Count - 1]);
    }
}
