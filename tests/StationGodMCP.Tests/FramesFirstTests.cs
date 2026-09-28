#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using StationGodMCP.Pure;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// frames_first: what counts as air (CellSupports), the air penalty in the rules, the air bound that keeps the search
/// fast, and whole searches on made-up worlds, the 2026-09-27 solar panel incident among them.
/// </summary>
public sealed class FramesFirstTests
{
    private readonly ITestOutputHelper _output;

    public FramesFirstTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // A position in metres as a small cell (Grid3 decimetres).
    private static GridCell M(double x, double y, double z) =>
        new GridCell((int)Math.Round(x * 10), (int)Math.Round(y * 10), (int)Math.Round(z * 10));

    // The 2 m cell spanning [x0, x0 + 2] x [y0, y0 + 2] x [z0, z0 + 2] metres, by its centre.
    private static GridCell Large(int x0, int y0, int z0) => new GridCell(x0 * 10 + 10, y0 * 10 + 10, z0 * 10 + 10);

    /// <summary>Frames and walls by 2 m cell; nothing else stands anywhere.</summary>
    private sealed class World
    {
        private readonly HashSet<GridCell> _frames = new HashSet<GridCell>();
        private readonly Dictionary<GridCell, int> _walls = new Dictionary<GridCell, int>();

        /// <summary>Frames in every 2 m cell of the box, corners in metres (even, minimum corners of cells).</summary>
        internal World Frames(int x0, int x1, int y0, int y1, int z0, int z1)
        {
            for (int x = x0; x < x1; x += 2)
            {
                for (int y = y0; y < y1; y += 2)
                {
                    for (int z = z0; z < z1; z += 2)
                    {
                        _frames.Add(Large(x, y, z));
                    }
                }
            }

            return this;
        }

        internal World Wall(GridCell large, string face)
        {
            _walls[large] = (_walls.TryGetValue(large, out int faces) ? faces : 0) | (1 << RunModels.Step(face).Index);
            return this;
        }

        internal int LargeLookups { get; private set; }

        internal LargeCellFacts LargeAt(GridCell large)
        {
            LargeLookups++;
            return new LargeCellFacts(_frames.Contains(large), false, _walls.TryGetValue(large, out int faces) ? faces : 0);
        }

        internal CellSupport Support(GridCell small) => CellSupports.Of(small, LargeAt);

        internal SmallCellFacts Small(GridCell small) =>
            new SmallCellFacts(null, 0, null, false, LargeAt(SmallCellCode.LargeOf(small)),
                SmallCellCode.IndexOnAxis(small.X), SmallCellCode.IndexOnAxis(small.Y),
                SmallCellCode.IndexOnAxis(small.Z), new List<long>(), Support(small),
                CellSupports.VisibilityOf(small, LargeAt));
    }

    private sealed class Plan
    {
        internal Plan(RouteResult result, int air, long milliseconds)
        {
            Result = result;
            Air = air;
            Milliseconds = milliseconds;
        }

        internal RouteResult Result { get; }

        internal List<GridCell> Cells => Result.Cells!;

        internal int Air { get; }

        internal long Milliseconds { get; }
    }

    // As PlanRouteApi does it: the box is the ends plus margin_m, the air bound only under frames_first.
    private static Plan Route(World world, GridCell from, GridCell to, bool framesFirst,
        RoutePreference prefer = RoutePreference.None, double marginM = 6.0, double bend = 2.0,
        int fieldLimit = AirBound.MaximumSmallCells, bool insideFrames = false)
    {
        int pad = (int)Math.Ceiling(marginM * 10.0 / GridStep.CellSize) * GridStep.CellSize;
        GridCell min = new GridCell(Math.Min(from.X, to.X) - pad, Math.Min(from.Y, to.Y) - pad,
            Math.Min(from.Z, to.Z) - pad);
        GridCell max = new GridCell(Math.Max(from.X, to.X) + pad, Math.Max(from.Y, to.Y) + pad,
            Math.Max(from.Z, to.Z) + pad);
        RouteRules search = new RouteRules(bend, AxisOrder.Any, 400, min, max);
        RouteRuleSet rules = new RouteRuleSet(prefer, insideFrames, false, false, false, new HashSet<long>(),
            new HashSet<long>(), false, framesFirst);
        Stopwatch watch = Stopwatch.StartNew();
        AirBound? bound = framesFirst
            ? AirBound.Build(min, max, new[] { to }, world.Support,
                cell => CellSupports.Anchors(world.LargeAt(cell)), RouteRuleSet.AirPenalty, fieldLimit)
            : null;
        RouteResult result = RoutePlanner.Find(RouteEnd.Open(from), RouteEnd.Open(to),
            cell => rules.Cost(world.Small(cell)), search, bound);
        watch.Stop();
        int air = 0;
        foreach (GridCell cell in result.Cells ?? new List<GridCell>())
        {
            air += world.Support(cell) == CellSupport.Air ? 1 : 0;
        }

        return new Plan(result, air, watch.ElapsedMilliseconds);
    }

    // The solar panel incident, 2026-09-27: frames at y 202-204, a west beam (x 698-702) and an east beam (x 706-710)
    // along z 652-664, a crossbar along x 698-710 at z 662-666; x 702-706 is open air for z < 662.
    private static World Solar() =>
        new World()
            .Frames(698, 702, 202, 204, 652, 664)
            .Frames(706, 710, 202, 204, 652, 664)
            .Frames(698, 710, 202, 204, 662, 666);

    private static bool InTheGap(GridCell cell) => cell.X > 7020 && cell.X < 7060 && cell.Z < 6620;

    [Fact]
    public void TheTopOfABeamIsAFrameAndTheGapIsAir()
    {
        World world = Solar();
        Assert.Equal(CellSupport.FrameEdge, world.Support(M(708, 204, 657)));
        Assert.Equal(CellSupport.Frame, world.Support(M(707, 204, 657)));
        Assert.Equal(CellSupport.Frame, world.Support(M(707.5, 203, 657)));
        Assert.Equal(CellSupport.FrameEdge, world.Support(M(700, 204, 657.5)));
        Assert.Equal(CellSupport.FrameEdge, world.Support(M(706, 204, 657)));
        Assert.Equal(CellSupport.FrameEdge, world.Support(M(702, 204, 657)));
        Assert.Equal(CellSupport.Air, world.Support(M(704, 204, 657)));
        Assert.Equal(CellSupport.Air, world.Support(M(705.5, 204, 657)));
        Assert.Equal(CellSupport.Air, world.Support(M(708, 204.5, 657)));
        Assert.Equal(CellSupport.FrameEdge, world.Support(M(704, 204, 664)));
    }

    [Fact]
    public void InsideFramesTakesTheTopOfABeamAndRefusesTheGap()
    {
        // The beam top y 204 belongs to the empty 2 m cell above (y 204 to 206); inside_frames once judged it by that
        // cell alone and refused it.
        World world = Solar();
        RouteRuleSet rules = new RouteRuleSet(RoutePreference.None, true, false, false, false, new HashSet<long>(),
            new HashSet<long>());
        Assert.False(world.Small(M(708, 204, 657)).Large.Frame);
        Assert.True(rules.Cost(world.Small(M(708, 204, 657))).Passable);
        Assert.True(rules.Cost(world.Small(M(707, 204, 657))).Passable);
        Assert.True(rules.Cost(world.Small(M(707.5, 203, 657))).Passable);
        Assert.True(rules.Cost(world.Small(M(698, 203, 657))).Passable);
        Assert.True(rules.Cost(world.Small(M(704, 204, 664))).Passable);
        Assert.False(rules.Cost(world.Small(M(704, 204, 657))).Passable);
        Assert.False(rules.Cost(world.Small(M(705.5, 203, 657))).Passable);
        Assert.False(rules.Cost(world.Small(M(708, 204.5, 657))).Passable);
        Assert.False(rules.Cost(world.Small(M(697.5, 203, 657))).Passable);
    }

    [Fact]
    public void InsideFramesRefusesAWallPlaneWithoutAFrame()
    {
        World world = new World().Wall(Large(0, 0, 0), "-x");
        RouteRuleSet rules = new RouteRuleSet(RoutePreference.None, true, false, false, false, new HashSet<long>(),
            new HashSet<long>());
        Assert.Equal(CellSupport.Wall, world.Support(M(0, 1, 1)));
        Assert.False(rules.Cost(world.Small(M(0, 1, 1))).Passable);
    }

    [Fact]
    public void AnInsideFramesRouteRunsAlongTheBeamTops()
    {
        // The accepted solar route's ends on the beam tops: inside_frames alone finds a route, every cell on a frame
        // and none in the gap, the same one frames_first with frame_edges finds.
        foreach (bool framesFirst in new[] { false, true })
        {
            Plan plan = Route(Solar(), M(708, 204, 657), M(700, 204, 664), framesFirst, RoutePreference.FrameEdges,
                insideFrames: true);
            Assert.NotNull(plan.Result.Cells);
            Assert.Equal(0, plan.Air);
            Assert.DoesNotContain(plan.Cells, InTheGap);
            Assert.All(plan.Cells, cell => Assert.True(RouteRuleSet.OnFrame(Solar().Small(cell)), $"{cell}"));
        }

        Plan corner = Route(Solar(), M(708, 204, 657), M(700, 204, 657.5), false, insideFrames: true);
        Assert.NotNull(corner.Result.Cells);
        Assert.Equal(0, corner.Air);
        Assert.DoesNotContain(corner.Cells, InTheGap);
    }

    [Fact]
    public void AWallPlaneIsSupportAndTheLayerBesideItIsNot()
    {
        World world = new World().Wall(Large(0, 0, 0), "-x");
        Assert.Equal(CellSupport.Wall, world.Support(M(0, 1, 1)));
        Assert.Equal(CellSupport.Wall, world.Support(M(0, 0, 0)));
        Assert.Equal(CellSupport.Air, world.Support(M(0.5, 1, 1)));
        Assert.Equal(CellSupport.Air, world.Support(M(0, 2.5, 1)));
        World above = new World().Wall(Large(0, 0, 0), "+y");
        Assert.Equal(CellSupport.Wall, above.Support(M(1, 2, 1)));
        Assert.Equal(CellSupport.Air, above.Support(M(1, 2.5, 1)));
    }

    [Fact]
    public void TheSolarRouteFollowsTheEastBeamAndTheCrossbar()
    {
        // The accepted route: north along the east beam's top (x 708) to z 664, west along the crossbar to x 700.
        Plan plan = Route(Solar(), M(708, 204, 657), M(700, 204, 664), true, RoutePreference.FrameEdges);
        List<GridCell> expected = new List<GridCell>();
        for (int z = 6570; z <= 6640; z += GridStep.CellSize)
        {
            expected.Add(new GridCell(7080, 2040, z));
        }

        for (int x = 7075; x >= 7000; x -= GridStep.CellSize)
        {
            expected.Add(new GridCell(x, 2040, 6640));
        }

        Assert.Equal(expected, plan.Cells);
        Assert.Equal(0, plan.Air);
        Assert.Equal(1, RunPath.Bends(plan.Cells));
    }

    [Fact]
    public void TheSolarRouteToTheCornerNeverCrossesTheGap()
    {
        foreach (RoutePreference prefer in new[] { RoutePreference.None, RoutePreference.FrameEdges })
        {
            Plan plan = Route(Solar(), M(708, 204, 657), M(700, 204, 657.5), true, prefer);
            Assert.Equal(0, plan.Air);
            Assert.DoesNotContain(plan.Cells, InTheGap);
            _output.WriteLine($"{prefer}: {plan.Cells.Count} cells, {RunPath.Bends(plan.Cells)} bends, " +
                              $"expanded {plan.Result.Expanded}");
        }
    }

    [Fact]
    public void WithoutFramesFirstTheRouteCrossesTheGapAsBefore()
    {
        Plan old = Route(Solar(), M(708, 204, 657), M(700, 204, 657), false);
        Assert.Equal(17, old.Cells.Count);
        Assert.Equal(0, RunPath.Bends(old.Cells));
        Assert.Equal(7, old.Air);
        Plan first = Route(Solar(), M(708, 204, 657), M(700, 204, 657), true);
        Assert.Equal(0, first.Air);
        Assert.DoesNotContain(first.Cells, InTheGap);
    }

    [Fact]
    public void WhereOnlyAirJoinsTheEndsTheRouteCrossesItWithTheFewestAirCells()
    {
        // Two frame cells 10 m apart and nothing between: the only way is through air, straight across.
        World world = new World().Frames(0, 2, 0, 2, 0, 2).Frames(12, 14, 0, 2, 0, 2);
        Plan plan = Route(world, M(1, 2, 1), M(13, 2, 1), true);
        Assert.NotNull(plan.Result.Cells);
        Assert.Equal(0, RunPath.Bends(plan.Cells));
        Assert.Equal(19, plan.Air);
        Plan old = Route(world, M(1, 2, 1), M(13, 2, 1), false);
        Assert.Equal(old.Cells, plan.Cells);
        Assert.True(plan.Result.Expanded < 2000, $"expanded {plan.Result.Expanded}");
        _output.WriteLine($"only air: expanded {plan.Result.Expanded} with the bound, {plan.Milliseconds} ms");
    }

    [Fact]
    public void AWideGapCrossedFromFramesStaysInsideTheSearchLimit()
    {
        // 30 m of open air between two frames: the bound cannot see that the two are not joined, so the search looks
        // around both before paying for the crossing; it must still finish under the expansion limit.
        World world = new World().Frames(0, 2, 0, 2, 0, 2).Frames(32, 34, 0, 2, 0, 2);
        Plan plan = Route(world, M(1, 2, 1), M(33, 2, 1), true);
        Assert.NotNull(plan.Result.Cells);
        Assert.Equal(0, RunPath.Bends(plan.Cells));
        Assert.Equal(59, plan.Air);
        Assert.True(plan.Result.Expanded < 5000, $"expanded {plan.Result.Expanded}");
        Plan gaps = Route(world, M(1, 2, 1), M(33, 2, 1), true, fieldLimit: 0);
        Assert.Equal(plan.Cells, gaps.Cells);
        _output.WriteLine($"30 m gap: expanded {plan.Result.Expanded} ({plan.Milliseconds} ms) with the field, " +
                          $"{gaps.Result.Expanded} ({gaps.Milliseconds} ms) with the gaps; air {plan.Air}");
    }

    [Fact]
    public void AnAirHopLosesToAMuchLongerRouteOverFrames()
    {
        // A 2 m gap in a straight beam; a U of frames goes around it 8 m out and back.
        World world = new World()
            .Frames(0, 10, 0, 2, 0, 2)
            .Frames(12, 22, 0, 2, 0, 2)
            .Frames(8, 10, 0, 2, 0, 10)
            .Frames(8, 14, 0, 2, 8, 10)
            .Frames(12, 14, 0, 2, 0, 10);
        Plan plan = Route(world, M(1, 2, 1), M(21, 2, 1), true, marginM: 12.0);
        Assert.Equal(0, plan.Air);
        Plan old = Route(world, M(1, 2, 1), M(21, 2, 1), false, marginM: 12.0);
        Assert.True(old.Air > 0);
        Assert.True(plan.Cells.Count > old.Cells.Count + 10);
    }

    [Fact]
    public void TheAirPenaltyAppliesOnlyUnderFramesFirst()
    {
        World world = Solar();
        SmallCellFacts air = world.Small(M(704, 204, 657));
        SmallCellFacts beam = world.Small(M(708, 204, 657));
        RouteRuleSet on = new RouteRuleSet(RoutePreference.None, false, false, false, false, new HashSet<long>(),
            new HashSet<long>(), false, true);
        RouteRuleSet off = new RouteRuleSet(RoutePreference.None, false, false, false, false, new HashSet<long>(),
            new HashSet<long>());
        Assert.Equal(1.0 + RouteRuleSet.AirPenalty, on.Cost(air).Cost);
        Assert.Equal(1.0, on.Cost(beam).Cost);
        Assert.Equal(1.0, off.Cost(air).Cost);
        Assert.Equal(1.0, on.WithoutFramesFirst().Cost(air).Cost);
        Assert.False(on.WithoutFramesFirst().FramesFirst);
    }

    [Theory]
    [InlineData(AirBound.MaximumSmallCells, "field")]
    [InlineData(0, "gaps")]
    public void TheAirBoundNeverOverestimates(int fieldLimit, string kind)
    {
        // Every cell of a small world: the bound from it is at most the true cheapest cost to the goal minus the
        // distance part, found by a search from that cell without the bound.
        World world = new World().Frames(0, 4, 0, 2, 0, 2).Frames(8, 10, 0, 2, 0, 6);
        GridCell goal = M(9, 2, 5);
        GridCell min = M(-2, 0, -2);
        GridCell max = M(12, 4, 8);
        AirBound bound = AirBound.Build(min, max, new[] { goal }, world.Support,
            cell => CellSupports.Anchors(world.LargeAt(cell)), RouteRuleSet.AirPenalty, fieldLimit);
        Assert.Equal(kind, bound.Kind);
        RouteRuleSet rules = new RouteRuleSet(RoutePreference.None, false, false, false, false, new HashSet<long>(),
            new HashSet<long>(), false, true);
        RouteRules search = new RouteRules(0.0, AxisOrder.Any, 400, min, max);
        int checkedCells = 0;
        for (int x = min.X; x <= max.X; x += 30)
        {
            for (int y = min.Y; y <= max.Y; y += 20)
            {
                for (int z = min.Z; z <= max.Z; z += 25)
                {
                    GridCell from = new GridCell(x, y, z);
                    RouteResult exact = RoutePlanner.Find(RouteEnd.Open(from), RouteEnd.Open(goal),
                        cell => rules.Cost(world.Small(cell)), search);
                    double distance = (Math.Abs(from.X - goal.X) + Math.Abs(from.Y - goal.Y) +
                                       Math.Abs(from.Z - goal.Z)) / (double)GridStep.CellSize;
                    Assert.True(distance + bound.Extra(from, distance) <= exact.Cost + 1e-9,
                        $"{from}: bound {distance + bound.Extra(from, distance)} > cost {exact.Cost}");
                    checkedCells++;
                }
            }
        }

        Assert.True(checkedCells > 50);
    }

    [Fact]
    public void TheBoundKeepsALongRouteThroughOpenGroundCheap()
    {
        // No frames at all: 40 m straight across open ground, the widest search box.
        World world = new World();
        Plan plan = Route(world, M(0, 0, 0), M(40, 0, 0), true, marginM: 32.0);
        Assert.Equal(81, plan.Cells.Count);
        Assert.Equal(81, plan.Air);
        Assert.True(plan.Result.Expanded < 2000, $"expanded {plan.Result.Expanded}");
        _output.WriteLine($"open ground 40 m: expanded {plan.Result.Expanded}, {plan.Milliseconds} ms, " +
                          $"{world.LargeLookups} 2 m lookups");
    }

    [Fact]
    public void ALargeFramedFloorIsSearchedQuickly()
    {
        // A 60 m x 60 m floor of frames with a 10 m hole in the middle; corner to corner over the top.
        World world = new World()
            .Frames(0, 60, 0, 2, 0, 24)
            .Frames(0, 24, 0, 2, 24, 34)
            .Frames(34, 60, 0, 2, 24, 34)
            .Frames(0, 60, 0, 2, 34, 60);
        Plan plan = Route(world, M(1, 2, 1), M(59, 2, 59), true, RoutePreference.FrameEdges);
        Assert.NotNull(plan.Result.Cells);
        Assert.Equal(0, plan.Air);
        Assert.True(plan.Milliseconds < 5000, $"{plan.Milliseconds} ms");
        Plan old = Route(world, M(1, 2, 1), M(59, 2, 59), false, RoutePreference.FrameEdges);
        _output.WriteLine($"framed floor: expanded {plan.Result.Expanded} ({plan.Milliseconds} ms) with " +
                          $"frames_first, {old.Result.Expanded} ({old.Milliseconds} ms) without; " +
                          $"{plan.Cells.Count} cells");
    }
}
