#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The route search (RoutePlanner) and the rules as costs (RouteRuleSet), on made-up grids.</summary>
public sealed class RoutePlannerTests
{
    private static GridCell At(int x, int y, int z) => RunModels.At(x, y, z);

    private static RouteRules Box(int size, double bend = 2.0, AxisOrder order = AxisOrder.Any, int maxLength = 400) =>
        new RouteRules(bend, order, maxLength, At(-size, -size, -size), At(size, size, size));

    private static Func<GridCell, CellCost> Open(HashSet<GridCell>? walls = null) =>
        cell => walls != null && walls.Contains(cell) ? CellCost.Blocked : CellCost.Of(1.0);

    [Fact]
    public void AStraightLineIsTheShortestRoute()
    {
        RouteResult result = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(4, 0, 0)), Open(),
            Box(6));
        Assert.Equal(5, result.Cells!.Count);
        Assert.Equal(0, RunPath.Bends(result.Cells));
    }

    [Fact]
    public void ARouteGoesAroundAnObstacleIn3D()
    {
        HashSet<GridCell> walls = new HashSet<GridCell>();
        for (int y = -3; y <= 3; y++)
        {
            for (int z = -3; z <= 3; z++)
            {
                if (!(y == 3 && z == 0))
                {
                    walls.Add(At(2, y, z));
                }
            }
        }

        RouteResult result = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(4, 0, 0)), Open(walls),
            new RouteRules(2.0, AxisOrder.Any, 400, At(-1, -3, -3), At(6, 3, 3)));
        Assert.NotNull(result.Cells);
        Assert.Contains(At(2, 3, 0), result.Cells!);
        foreach (GridCell cell in result.Cells)
        {
            Assert.DoesNotContain(cell, walls);
        }

        Assert.NotNull(RunPath.FromCells(result.Cells, out _));
    }

    [Fact]
    public void AWalledOffGoalHasNoRoute()
    {
        HashSet<GridCell> walls = new HashSet<GridCell>();
        foreach (GridStep step in GridStep.All)
        {
            walls.Add(step.From(At(4, 0, 0)));
        }

        RouteResult result = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(4, 0, 0)), Open(walls),
            Box(6));
        Assert.Null(result.Cells);
        Assert.Equal("no_route", result.Failure);
    }

    [Fact]
    public void MaxLengthRefusesLongerRoutes()
    {
        RouteResult result = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(6, 0, 0)), Open(),
            Box(8, maxLength: 5));
        Assert.Null(result.Cells);
        Assert.Equal("too_long", result.Failure);
    }

    [Fact]
    public void AHighBendCostPrefersFewerTurns()
    {
        // A diagonal goal: any staircase is as short as one L; with bends dear the L wins.
        RouteResult result = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(3, 0, 3)), Open(),
            Box(6, bend: 25.0));
        Assert.Equal(1, RunPath.Bends(result.Cells!));
        Assert.Equal(7, result.Cells!.Count);
    }

    [Fact]
    public void VerticalFirstClimbsBeforeGoingAcross()
    {
        RouteResult result = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(3, 3, 0)), Open(),
            Box(6, order: AxisOrder.VerticalFirst));
        Assert.Equal(At(0, 1, 0), result.Cells![1]);
        Assert.Equal(At(0, 3, 0), result.Cells[3]);
        RouteResult across = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(3, 3, 0)), Open(),
            Box(6, order: AxisOrder.HorizontalFirst));
        Assert.Equal(At(1, 0, 0), across.Cells![1]);
    }

    [Fact]
    public void ABlockedAxisIsCrossedButNeverRunAlong()
    {
        // A pipe along x in (2,0,0): the cable may cross it along z but not run through it along x.
        Func<GridCell, CellCost> costs = cell => cell.Equals(At(2, 0, 0)) ? CellCost.Of(1.0, 1) : CellCost.Of(1.0);
        RouteResult along = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(4, 0, 0)), costs, Box(6));
        Assert.DoesNotContain(At(2, 0, 0), along.Cells!);
        RouteResult across = RoutePlanner.Find(RouteEnd.Open(At(2, 0, -2)), RouteEnd.Open(At(2, 0, 2)), costs,
            Box(6));
        Assert.Contains(At(2, 0, 0), across.Cells!);
    }

    [Fact]
    public void LeavingAPieceThroughAnOpenEndIsFree()
    {
        // The start is a piece with an open end towards -x; leaving through +x would make it a junction.
        RouteEnd start = new RouteEnd(At(0, 0, 0), RunModels.Ends("-x"), 50.0);
        RouteResult result = RoutePlanner.Find(start, RouteEnd.Open(At(-3, 0, 1)), Open(), Box(6));
        Assert.Equal(At(-1, 0, 0), result.Cells![1]);
    }
}

public sealed class RouteRuleSetTests
{
    // A small cell at indices x, y, z of a large cell at the origin with the given facts; every other large cell is
    // empty, so the support is what that one cell gives.
    private static SmallCellFacts Facts(LargeCellFacts large, int x, int y, int z, string? blocked = null,
        bool family = false, params long[] neighbours)
    {
        GridCell centre = new GridCell(10, 10, 10);
        GridCell small = new GridCell(x * GridStep.CellSize, y * GridStep.CellSize, z * GridStep.CellSize);
        Func<GridCell, LargeCellFacts> read = cell => cell.Equals(centre) ? large : Empty;
        return new SmallCellFacts(blocked, 0, family ? 9 : (long?)null, family, large, x, y, z,
            new List<long>(neighbours), CellSupports.Of(small, read), CellSupports.VisibilityOf(small, read));
    }

    private static RouteRuleSet Rules(RoutePreference prefer = RoutePreference.None, bool insideFrames = false,
        bool interior = false, bool walkways = false, bool avoidNetworks = false, params long[] own) =>
        new RouteRuleSet(prefer, insideFrames, interior, walkways, avoidNetworks, new HashSet<long>(own),
            new HashSet<long>());

    private static readonly LargeCellFacts Frame = new LargeCellFacts(true, false, 0);
    private static readonly LargeCellFacts Empty = new LargeCellFacts(false, false, 0);
    private static readonly LargeCellFacts Room = new LargeCellFacts(false, true, 0);

    [Fact]
    public void BlockedAndOwnPiecesAreNeverEntered()
    {
        Assert.False(Rules().Cost(Facts(Empty, 1, 1, 1, "a device stands there")).Passable);
        Assert.False(Rules().Cost(Facts(Empty, 1, 1, 1, family: true)).Passable);
        Assert.True(Rules().Cost(Facts(Empty, 1, 1, 1)).Passable);
    }

    [Fact]
    public void InsideFramesRefusesCellsOutsideFrames()
    {
        Assert.False(Rules(insideFrames: true).Cost(Facts(Empty, 1, 1, 1)).Passable);
        Assert.True(Rules(insideFrames: true).Cost(Facts(Frame, 1, 1, 1)).Passable);
    }

    [Fact]
    public void FrameEdgesAreCheaperThanTheMiddleOfAFrame()
    {
        RouteRuleSet rules = Rules(RoutePreference.FrameEdges);
        Assert.Equal(1.0, rules.Cost(Facts(Frame, 0, 0, 2)).Cost);
        Assert.Equal(1.0, rules.Cost(Facts(Frame, 0, 0, 0)).Cost);
        Assert.Equal(1.0 + RouteRuleSet.PreferencePenalty, rules.Cost(Facts(Frame, 0, 2, 2)).Cost);
        Assert.Equal(1.0 + RouteRuleSet.PreferencePenalty, rules.Cost(Facts(Empty, 0, 0, 2)).Cost);
    }

    [Fact]
    public void AlongWallsMeansOnTheWallPlaneOrTheLayerBesideIt()
    {
        LargeCellFacts wallMinusX = new LargeCellFacts(false, true, 1 << RunModels.Step("-x").Index);
        LargeCellFacts wallPlusZ = new LargeCellFacts(false, true, 1 << RunModels.Step("+z").Index);
        Assert.True(RouteRuleSet.AlongWall(Facts(wallMinusX, 0, 2, 2)));
        Assert.True(RouteRuleSet.AlongWall(Facts(wallMinusX, 1, 2, 2)));
        Assert.False(RouteRuleSet.AlongWall(Facts(wallMinusX, 2, 2, 2)));
        Assert.True(RouteRuleSet.AlongWall(Facts(wallPlusZ, 2, 2, 3)));
        Assert.False(RouteRuleSet.AlongWall(Facts(wallPlusZ, 2, 2, 1)));
    }

    [Fact]
    public void RoomInteriorAndWalkwaysCostExtra()
    {
        Assert.Equal(1.0 + RouteRuleSet.InteriorPenalty, Rules(interior: true).Cost(Facts(Room, 1, 1, 1)).Cost);
        Assert.Equal(1.0, Rules(interior: true).Cost(Facts(Room, 0, 1, 1)).Cost);
        Assert.Equal(1.0, Rules(interior: true).Cost(Facts(Empty, 1, 1, 1)).Cost);
        Assert.Equal(1.0 + RouteRuleSet.InteriorPenalty, Rules(walkways: true).Cost(Facts(Room, 2, 1, 2)).Cost);
        Assert.Equal(1.0, Rules(walkways: true).Cost(Facts(Room, 2, 0, 2)).Cost);
        Assert.Equal(1.0, Rules(walkways: true).Cost(Facts(Room, 0, 2, 2)).Cost);
    }

    [Fact]
    public void AvoidNetworksRefusesCellsBesideAForeignNetworkOnly()
    {
        RouteRuleSet rules = Rules(avoidNetworks: true, own: 77);
        Assert.False(rules.Cost(Facts(Empty, 1, 1, 1, null, false, 88)).Passable);
        Assert.True(rules.Cost(Facts(Empty, 1, 1, 1, null, false, 77)).Passable);
        Assert.True(Rules().Cost(Facts(Empty, 1, 1, 1, null, false, 88)).Passable);
    }

    [Fact]
    public void ARouteUnderRulesStaysInsideFramesIn3D()
    {
        // Frames fill x 0..3 (small cells), any y and z; the ends are inside; the direct line leaves the frames.
        RouteRuleSet rules = Rules(insideFrames: true);
        Func<GridCell, CellCost> costs = cell =>
        {
            int x = cell.X / GridStep.CellSize;
            bool framed = x >= 0 && x <= 3 && !(cell.Y == 0 && x == 2);
            return rules.Cost(Facts(framed ? Frame : Empty, 1, 1, 1));
        };
        RouteResult result = RoutePlanner.Find(RouteEnd.Open(RunModels.At(0, 0, 0)),
            RouteEnd.Open(RunModels.At(3, 0, 0)), costs,
            new RouteRules(2.0, AxisOrder.Any, 100, RunModels.At(-4, -4, -4), RunModels.At(8, 4, 4)));
        Assert.NotNull(result.Cells);
        foreach (GridCell cell in result.Cells!)
        {
            int x = cell.X / GridStep.CellSize;
            Assert.InRange(x, 0, 3);
            Assert.False(cell.Y == 0 && x == 2);
        }
    }
}

public sealed class SmallCellCodeTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(5, 10, 1)]
    [InlineData(15, 10, 3)]
    [InlineData(20, 30, 0)]
    [InlineData(-5, -10, 3)]
    [InlineData(-20, -10, 0)]
    [InlineData(-25, -30, 3)]
    public void SmallCellsFallInTheirLargeCell(int small, int large, int index)
    {
        Assert.Equal(large, SmallCellCode.LargeOf(new GridCell(small, 0, 0)).X);
        Assert.Equal(index, SmallCellCode.IndexOnAxis(small));
    }

    [Fact]
    public void IndexAndSmallAtAreInverse()
    {
        GridCell large = new GridCell(30, -10, 50);
        for (int index = 0; index < SmallCellCode.PerCell; index++)
        {
            GridCell small = SmallCellCode.SmallAt(large, index);
            Assert.Equal(large, SmallCellCode.LargeOf(small));
            Assert.Equal(index, SmallCellCode.IndexOf(small));
        }
    }

    [Fact]
    public void TheEncodingPutsEachCellAtItsIndex()
    {
        GridCell large = new GridCell(10, 10, 10);
        GridCell cable = SmallCellCode.SmallAt(large, 1 + 4 * 2 + 16 * 3);
        GridCell both = SmallCellCode.SmallAt(large, 0);
        string text = SmallCellCode.Encode(large, cell =>
            new SmallOccupancy(cell.Equals(cable) || cell.Equals(both), cell.Equals(both), false, false, false, false));
        Assert.Equal(64, text.Length);
        Assert.Equal('b', text[0]);
        Assert.Equal('c', text[1 + 4 * 2 + 16 * 3]);
        Assert.Equal(62, text.Split('.').Length - 1);
    }

    [Theory]
    [InlineData(true, true, true, false, false, false, 'h')]
    [InlineData(true, false, false, true, false, false, 'd')]
    [InlineData(false, false, false, false, true, false, 'o')]
    [InlineData(false, true, false, false, false, false, 'p')]
    [InlineData(false, false, false, false, false, true, 'r')]
    [InlineData(false, false, false, false, false, false, '.')]
    public void OccupancyCodes(bool cable, bool pipe, bool chute, bool device, bool other, bool rocket, char code) =>
        Assert.Equal(code, SmallCellCode.Encode(new SmallOccupancy(cable, pipe, chute, device, other, rocket)));

    [Fact]
    public void ABoxListsItsLargeCellsInOrder()
    {
        List<GridCell> cells = SmallCellCode.LargeCellsIn(new GridCell(0, 0, 0), new GridCell(35, 5, 15));
        Assert.Equal(2, cells.Count);
        Assert.Equal(new GridCell(10, 10, 10), cells[0]);
        Assert.Equal(new GridCell(30, 10, 10), cells[1]);
        Assert.Equal(2, SmallCellCode.CountIn(new GridCell(0, 0, 0), new GridCell(35, 5, 15)));
    }
}
