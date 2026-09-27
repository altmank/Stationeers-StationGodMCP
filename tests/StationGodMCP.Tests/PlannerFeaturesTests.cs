#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// The 1.2.0 planner features on made-up worlds (the 2026-09-27 solar beams and a synthetic base with a floor slab):
/// planning as if old pieces were gone (assume_removed), prefer hidden, bus mode (a trunk and its drops), sixteen
/// starts, grid_survey's inside class and floating report, feed paths and the new report fields on the wire.
/// </summary>
public sealed class PlannerFeaturesTests
{
    private readonly ITestOutputHelper _output;

    public PlannerFeaturesTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static GridCell M(double x, double y, double z) =>
        new GridCell((int)Math.Round(x * 10), (int)Math.Round(y * 10), (int)Math.Round(z * 10));

    private static GridCell Large(int x0, int y0, int z0) => new GridCell(x0 * 10 + 10, y0 * 10 + 10, z0 * 10 + 10);

    /// <summary>
    /// Frames and walls by 2 m cell, and pieces of the route's kind by small cell (an old run), each with an id. A
    /// piece whose id is ignored is gone, as GridFacts treats a reroute's or assume_removed's pieces.
    /// </summary>
    private sealed class World
    {
        private readonly HashSet<GridCell> _frames = new HashSet<GridCell>();
        private readonly Dictionary<GridCell, int> _walls = new Dictionary<GridCell, int>();
        private readonly Dictionary<GridCell, long> _pieces = new Dictionary<GridCell, long>();

        internal HashSet<long> Ignore { get; } = new HashSet<long>();

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

        internal World Piece(long id, GridCell cell)
        {
            _pieces[cell] = id;
            return this;
        }

        /// <summary>Every standing piece's cells by id (a single straight each here).</summary>
        internal Dictionary<long, IReadOnlyList<GridCell>> PieceCells()
        {
            Dictionary<long, IReadOnlyList<GridCell>> cells = new Dictionary<long, IReadOnlyList<GridCell>>();
            foreach (KeyValuePair<GridCell, long> piece in _pieces)
            {
                cells[piece.Value] = new List<GridCell> { piece.Key };
            }

            return cells;
        }

        internal LargeCellFacts LargeAt(GridCell large) =>
            new LargeCellFacts(_frames.Contains(large), false, _walls.TryGetValue(large, out int faces) ? faces : 0);

        internal CellVisibility Visibility(GridCell small) => CellSupports.VisibilityOf(small, LargeAt);

        internal SmallCellFacts Small(GridCell small)
        {
            bool piece = _pieces.TryGetValue(small, out long id) && !Ignore.Contains(id);
            return new SmallCellFacts(null, 0, piece ? 900 : (long?)null, piece,
                LargeAt(SmallCellCode.LargeOf(small)), SmallCellCode.IndexOnAxis(small.X),
                SmallCellCode.IndexOnAxis(small.Y), SmallCellCode.IndexOnAxis(small.Z), new List<long>(),
                CellSupports.Of(small, LargeAt), Visibility(small));
        }
    }

    // As PlanRouteApi grows a tree: a box of margin_m around each leg's ends, the air bound under frames_first.
    private static RouteTree Grow(World world, IReadOnlyList<GridCell> starts, RouteMain main, bool framesFirst = true,
        RoutePreference prefer = RoutePreference.None, bool insideFrames = false, double marginM = 6.0)
    {
        RouteRuleSet rules = new RouteRuleSet(prefer, insideFrames, false, false, false, new HashSet<long>(),
            new HashSet<long>(), false, framesFirst);
        int pad = (int)Math.Ceiling(marginM * 10.0 / GridStep.CellSize) * GridStep.CellSize;
        RouteSearch search = new RouteSearch(cell => rules.Cost(world.Small(cell)),
            (from, to) => new RouteRules(2.0, AxisOrder.Any, 400,
                new GridCell(Math.Min(from.X, to.X) - pad, Math.Min(from.Y, to.Y) - pad, Math.Min(from.Z, to.Z) - pad),
                new GridCell(Math.Max(from.X, to.X) + pad, Math.Max(from.Y, to.Y) + pad, Math.Max(from.Z, to.Z) + pad)),
            (box, goals) => framesFirst
                ? AirBound.Build(box.Min, box.Max, new List<RouteEnd>(goals).ConvertAll(goal => goal.Cell),
                    cell => CellSupports.Of(cell, world.LargeAt),
                    cell => CellSupports.Anchors(world.LargeAt(cell)), RouteRuleSet.AirPenalty)
                : null);
        List<RouteEndpoint> endpoints = new List<GridCell>(starts)
            .ConvertAll(cell => new RouteEndpoint(RouteEnd.Open(cell), new List<long>()));
        return RouteTrees.Grow(endpoints, main, search, 3.0);
    }

    private static RouteMain To(GridCell cell) =>
        new RouteMain.ToTarget(new RouteEndpoint(RouteEnd.Open(cell), new List<long>()));

    // The solar panel incident's frames: two beams along z at y 202-204 and a crossbar at z 662-666.
    private static World Solar() =>
        new World()
            .Frames(698, 702, 202, 204, 652, 664)
            .Frames(706, 710, 202, 204, 652, 664)
            .Frames(698, 710, 202, 204, 662, 666);

    // A synthetic base: a 30 m x 12 m floor slab of frames at y 0-2, open ground around it.
    private static World Base() => new World().Frames(0, 30, 0, 2, 0, 12);

    // The panel's port on the east beam top, boxed in on all six sides by old cable (ids 101 to 106).
    private static World BoxedPort(GridCell port)
    {
        World world = Solar();
        long id = 101;
        foreach (GridStep step in GridStep.All)
        {
            world.Piece(id++, step.From(port));
        }

        return world;
    }

    [Fact]
    public void OldCableAroundAPortGivesNoRouteUntilItIsAssumedRemoved()
    {
        GridCell port = M(708, 204, 657);
        World world = BoxedPort(port);
        RouteTree boxed = Grow(world, new[] { port }, To(M(700, 204, 664)), prefer: RoutePreference.FrameEdges);
        Assert.False(boxed.Found);
        Assert.Equal("no_route", boxed.Failure);

        foreach (long id in world.PieceCells().Keys)
        {
            world.Ignore.Add(id);
        }

        RouteTree free = Grow(world, new[] { port }, To(M(700, 204, 664)), prefer: RoutePreference.FrameEdges);
        Assert.True(free.Found);
        // The route LU accepted for the panel: north along the east beam top, west along the crossbar.
        Assert.Equal(M(708, 204, 664), free.Main[(6640 - 6570) / GridStep.CellSize]);
        Assert.Equal(1, RunPath.Bends(free.Main));
        Assert.All(free.Main, cell => Assert.NotEqual(CellVisibility.Air, world.Visibility(cell)));
        // Only the piece north of the port (+z, id 105) stands in the route's way; the other five may go later.
        Assert.Equal(new List<long> { 105 }, RemovedInTheWay.Of(free.Cells, world.PieceCells()));
    }

    [Fact]
    public void AssumingOnlyThePieceInTheWayRemovedIsEnough()
    {
        GridCell port = M(708, 204, 657);
        World world = BoxedPort(port);
        world.Ignore.Add(105);
        RouteTree tree = Grow(world, new[] { port }, To(M(700, 204, 664)), prefer: RoutePreference.FrameEdges);
        Assert.True(tree.Found);
        Assert.Equal(M(708, 204, 657.5), tree.Main[1]);
        Assert.Equal(new List<long> { 105 }, RemovedInTheWay.Of(tree.Cells, world.PieceCells()));
    }

    [Fact]
    public void HiddenCostsRiseWithVisibility()
    {
        Assert.Equal(0.0, RouteRuleSet.HiddenCost(CellVisibility.Inside));
        Assert.True(RouteRuleSet.HiddenCost(CellVisibility.Inside) < RouteRuleSet.HiddenCost(CellVisibility.FrameSurface));
        Assert.True(RouteRuleSet.HiddenCost(CellVisibility.FrameSurface) < RouteRuleSet.HiddenCost(CellVisibility.Wall));
        Assert.True(RouteRuleSet.HiddenCost(CellVisibility.Wall) < RouteRuleSet.HiddenCost(CellVisibility.Air));
        World world = Base();
        RouteRuleSet hidden = new RouteRuleSet(RoutePreference.Hidden, false, false, false, false,
            new HashSet<long>(), new HashSet<long>());
        Assert.Equal(1.0, hidden.Cost(world.Small(M(5, 1.5, 5))).Cost);
        Assert.Equal(1.0 + RouteRuleSet.SurfaceCost, hidden.Cost(world.Small(M(5, 2, 5))).Cost);
        Assert.Equal(1.0 + RouteRuleSet.AirCost, hidden.Cost(world.Small(M(5, 3, 5))).Cost);
    }

    [Fact]
    public void AHiddenRouteDivesIntoTheSlabAndSurfacesOnlyAtItsEnds()
    {
        World world = Base();
        GridCell from = M(1, 2, 5);
        GridCell to = M(19, 2, 5);
        RouteTree plain = Grow(world, new[] { from }, To(to));
        VisibilityTally top = VisibilityTally.Of(plain.Cells, world.Visibility);
        Assert.Equal(plain.Cells.Count, top.FrameSurface);
        RouteTree hidden = Grow(world, new[] { from }, To(to), prefer: RoutePreference.Hidden);
        VisibilityTally tally = VisibilityTally.Of(hidden.Cells, world.Visibility);
        Assert.Equal(2, tally.FrameSurface);
        Assert.Equal(hidden.Cells.Count - 2, tally.Inside);
        Assert.Equal(0, tally.Air);
        Assert.Equal(M(1, 1.5, 5), hidden.Main[1]);
        _output.WriteLine($"hidden: {hidden.Cells.Count} cells ({tally.Inside} inside), plain {plain.Cells.Count}");
    }

    [Fact]
    public void OnTheSolarBeamsAHiddenRouteRunsInsideTheBeams()
    {
        World world = Solar();
        RouteTree hidden = Grow(world, new[] { M(708, 204, 657) }, To(M(700, 204, 664)),
            prefer: RoutePreference.Hidden);
        VisibilityTally tally = VisibilityTally.Of(hidden.Cells, world.Visibility);
        Assert.Equal(0, tally.Air);
        Assert.True(tally.Inside > tally.FrameSurface, $"inside {tally.Inside}, surface {tally.FrameSurface}");
        Assert.DoesNotContain(hidden.Cells, cell => cell.X > 7020 && cell.X < 7060 && cell.Z < 6620);
    }

    [Fact]
    public void HiddenFindsTheLeastVisibleRouteWhereInsideFramesFindsNone()
    {
        // A regulator's port 2 m up over the slab: nothing but air leads to it.
        World world = Base();
        GridCell port = M(10, 4, 5);
        RouteTree strict = Grow(world, new[] { M(1, 2, 5) }, To(port), insideFrames: true);
        Assert.False(strict.Found);
        RouteTree hidden = Grow(world, new[] { M(1, 2, 5) }, To(port), prefer: RoutePreference.Hidden);
        Assert.True(hidden.Found);
        VisibilityTally tally = VisibilityTally.Of(hidden.Cells, world.Visibility);
        // y 2.5 to 4: the four cells between the slab's top face and the port, nothing fewer.
        Assert.Equal(4, tally.Air);
        Assert.True(tally.Inside > 10);
    }

    // Sixteen device ports on the slab's top face, eight along each long side.
    private static List<GridCell> SixteenPorts()
    {
        List<GridCell> ports = new List<GridCell>();
        for (int index = 0; index < 8; index++)
        {
            ports.Add(M(2 + 3.5 * index, 2, 1.5));
            ports.Add(M(3.5 + 3.5 * index, 2, 10.5));
        }

        return ports;
    }

    [Fact]
    public void ABusTrunkTakesSixteenDropsInOneTree()
    {
        World world = Base();
        List<GridCell>? trunk = RunPath.FromWaypoints(new[] { M(0.5, 1.5, 6), M(29.5, 1.5, 6) }, out string? error);
        Assert.NotNull(trunk);
        List<GridCell> ports = SixteenPorts();
        Assert.Equal(RouteTrees.MaximumStarts, ports.Count);
        Stopwatch watch = Stopwatch.StartNew();
        RouteTree tree = Grow(world, ports, new RouteMain.Trunk(trunk!), prefer: RoutePreference.Hidden);
        watch.Stop();
        Assert.True(tree.Found, tree.Failure);
        Assert.Equal(trunk, tree.Main);
        Assert.Equal(16, tree.Branches.Count);
        Assert.NotNull(RunShape.Of(tree.Main, tree.Branches, out error));
        Assert.Equal(tree.Cells.Count, new HashSet<GridCell>(tree.Cells).Count);
        VisibilityTally tally = VisibilityTally.Of(tree.Cells, world.Visibility);
        Assert.Equal(0, tally.Air);
        Assert.Equal(16, tally.FrameSurface);
        Assert.True(watch.ElapsedMilliseconds < 10000, $"{watch.ElapsedMilliseconds} ms");
        _output.WriteLine($"bus: {tree.Cells.Count} cells, expanded {tree.Expanded}, {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void SixteenStartsGrowOneTreeToATargetInTime()
    {
        World world = Base();
        Stopwatch watch = Stopwatch.StartNew();
        RouteTree tree = Grow(world, SixteenPorts(), To(M(29.5, 2, 6)), prefer: RoutePreference.FrameEdges);
        watch.Stop();
        Assert.True(tree.Found, tree.Failure);
        Assert.Equal(15, tree.Branches.Count + tree.Extra.Count);
        Assert.NotNull(RunShape.Of(tree.Main, tree.Branches, out _));
        Assert.True(watch.ElapsedMilliseconds < 10000, $"{watch.ElapsedMilliseconds} ms");
        _output.WriteLine($"16 starts: {tree.Cells.Count} cells, expanded {tree.Expanded}, {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AFailingDropNamesItsStart()
    {
        World world = Base();
        List<GridCell> ports = new List<GridCell> { M(2, 2, 1.5), M(5, 2, 1.5) };
        world.Piece(1, M(5.5, 2, 1.5)).Piece(2, M(4.5, 2, 1.5)).Piece(3, M(5, 2.5, 1.5)).Piece(4, M(5, 1.5, 1.5))
            .Piece(5, M(5, 2, 1)).Piece(6, M(5, 2, 2));
        List<GridCell>? trunk = RunPath.FromWaypoints(new[] { M(0.5, 1.5, 6), M(29.5, 1.5, 6) }, out _);
        RouteTree tree = Grow(world, ports, new RouteMain.Trunk(trunk!));
        Assert.False(tree.Found);
        Assert.Equal("from[1]", tree.FailedAt);
    }

    [Fact]
    public void TheSurveySplitsInsideAFrameFromItsSurface()
    {
        World world = Solar();
        Assert.Equal('i', Code(world, M(707, 203, 657)));
        Assert.Equal('i', Code(world, M(708, 203, 657)));
        Assert.Equal('e', Code(world, M(708, 204, 657)));
        Assert.Equal('f', Code(world, M(707, 204, 657)));
        Assert.Equal('a', Code(world, M(704, 204, 657)));
        World walled = new World().Wall(Large(0, 0, 0), "-x");
        Assert.Equal('w', Code(walled, M(0, 1, 1)));
        string support = CellSupports.Encode(Large(706, 202, 656), world.LargeAt);
        Assert.Equal('i', support[SmallCellCode.IndexOf(M(707, 203, 657))]);
        Assert.Equal('e', support[0]);
        Assert.DoesNotContain('a', support);
    }

    private static char Code(World world, GridCell cell) =>
        CellSupports.Code(CellSupports.Of(cell, world.LargeAt), world.Visibility(cell));

    [Fact]
    public void VisibilityAgreesWithSupportOnEveryCellOfTheSolarBox()
    {
        World world = Solar();
        for (int x = 6960; x <= 7120; x += 5)
        {
            for (int y = 2000; y <= 2060; y += 5)
            {
                for (int z = 6500; z <= 6680; z += 10)
                {
                    GridCell cell = new GridCell(x, y, z);
                    CellSupport support = CellSupports.Of(cell, world.LargeAt);
                    CellVisibility visibility = world.Visibility(cell);
                    Assert.Equal(support == CellSupport.Air, visibility == CellVisibility.Air);
                    Assert.Equal(support == CellSupport.Wall, visibility == CellVisibility.Wall);
                }
            }
        }
    }

    [Fact]
    public void AFloatingRunIsCountedAndItsAirCellsKeptInOrder()
    {
        World world = Solar();
        List<GridCell> run = new List<GridCell>();
        for (int x = 7080; x >= 7000; x -= GridStep.CellSize)
        {
            run.Add(new GridCell(x, 2040, 6570));
        }

        run.Add(run[0]);
        VisibilityTally tally = VisibilityTally.Of(run, world.Visibility);
        Assert.Equal(17, tally.Total);
        Assert.Equal(7, tally.Air);
        Assert.Equal(new GridCell(7055, 2040, 6570), tally.AirCells[0]);
        Assert.Equal(new GridCell(7025, 2040, 6570), tally.AirCells[6]);
    }

    // A base: APC 1 in room 9, a run through room 9, the slab (no room) and room 5 to a vault in room 5, which goes
    // on to a purifier in room 8 (a daisy chain through room 5), a second feed into room 5 straight from room 9, and a
    // device on the network that nothing reaches. The vault also touches piece 17 (a device is never passed through).
    private static Dictionary<long, FeedNode> FeedBase(out List<Link> links)
    {
        Dictionary<long, FeedNode> nodes = new Dictionary<long, FeedNode>();
        void Node(long id, bool device, long? room) => nodes[id] = new FeedNode(id, device, room);
        Node(1, true, 9);
        Node(10, false, 9);
        Node(11, false, 9);
        Node(12, false, null);
        Node(13, false, 5);
        Node(14, false, 5);
        Node(2, true, 5);
        Node(15, false, 5);
        Node(16, false, null);
        Node(17, false, 8);
        Node(3, true, 8);
        Node(20, false, 9);
        Node(21, false, 5);
        Node(4, true, 5);
        Node(5, true, 9);
        Node(6, true, 5);
        links = new List<Link>
        {
            new Link(1, 10), new Link(10, 11), new Link(11, 12), new Link(12, 13), new Link(13, 14),
            new Link(14, 2), new Link(14, 15), new Link(15, 16), new Link(16, 17), new Link(17, 3),
            new Link(10, 20), new Link(20, 21), new Link(21, 4), new Link(11, 5), new Link(2, 17)
        };
        return nodes;
    }

    [Fact]
    public void FeedPathsFlagADaisyChainAndARoomWithTwoFeeds()
    {
        FeedReport report = FeedPaths.Of(1, FeedBase(out List<Link> links), links);
        FeedPath purifier = report.Paths.Find(path => path.Device == 3)!;
        Assert.Equal(new List<long> { 10, 11, 12, 13, 14, 15, 16, 17 }, purifier.Pieces);
        Assert.Equal(new List<long> { 9, 5, 8 }, purifier.Rooms);
        Assert.Equal(new List<long> { 5 }, purifier.Through);
        FeedPath vault = report.Paths.Find(path => path.Device == 2)!;
        Assert.Empty(vault.Through);
        Assert.Equal(13, vault.Entry);
        Assert.Empty(report.Paths.Find(path => path.Device == 5)!.Through);
        RoomFeed manufacturing = report.Rooms.Find(room => room.Room == 5)!;
        Assert.Equal(new List<long> { 21, 13 }, manufacturing.Entries);
        Assert.Equal(new List<long> { 6 }, report.Unreached);
        Assert.Equal(new List<long> { 5, 4, 2, 3 }, report.Paths.ConvertAll(path => path.Device));
    }

    [Fact]
    public void AFeedNeverPassesThroughADevice()
    {
        // The vault (2) also links piece 17, a shortcut past 15 and 16: the purifier's feed still runs over pieces only.
        FeedReport report = FeedPaths.Of(1, FeedBase(out List<Link> links), links);
        Assert.DoesNotContain(2L, report.Paths.Find(path => path.Device == 3)!.Pieces);
    }

    [Fact]
    public void TheNewReportFieldsReadInSnakeCaseAndStayOutWhenEmpty()
    {
        RouteView route = new RouteView(new List<PositionView>(), 3, 0, 3.0, 10, new List<ThingId>(), 0, null, null,
            null, new RouteVisibilityView(2, 1, 0, 0),
            new RouteAssumedView(new List<ThingId> { new ThingId(106) }, new List<ThingId>(), new List<ThingId>(),
                new List<ThingId> { new ThingId(106) }),
            new List<UpgradeAmountView> { new UpgradeAmountView("ItemCableCoilHeavy", 1) });
        JObject json = JObject.Parse(WireCheck.New(route));
        Assert.Equal(2, (int)json["visibility"]!["inside"]!);
        Assert.Equal(1, (int)json["visibility"]!["frame_surface"]!);
        Assert.Equal("106", (string)json["assumed_removed"]!["in_the_way"]![0]!);
        Assert.Equal(1, (int)json["removal_refund"]![0]!["quantity"]!);
        JObject plain = JObject.Parse(WireCheck.New(new RouteView(new List<PositionView>(), 1, 0, 0, 0,
            new List<ThingId>(), 0, null)));
        Assert.Null(plain["visibility"]);
        Assert.Null(plain["assumed_removed"]);
        Assert.Null(plain["removal_refund"]);

        JObject removal = JObject.Parse(WireCheck.New(new RunRemovalView(new ThingView(new ThingId(5), "C", null),
            new PositionView(0, 0, 0), null, new List<UpgradeAmountView>(), true)));
        Assert.True((bool)removal["assumed"]!);
        Assert.Null(JObject.Parse(WireCheck.New(new RunRemovalView(new ThingView(new ThingId(5), "C", null),
            new PositionView(0, 0, 0), null, new List<UpgradeAmountView>())))["assumed"]);

        JObject network = JObject.Parse(WireCheck.New(new SurveyNetworkVisibilityView(new ThingId(7), "cable", 2,
            new RouteVisibilityView(0, 1, 0, 1), new List<PositionView> { new PositionView(1, 2, 3) }, null)));
        Assert.Equal(1, (int)network["cells"]!["air"]!);
        Assert.NotNull(network["air_at"]);
        Assert.Null(network["refund"]);

        FeedPathsView feed = new FeedPathsView(new ThingView(new ThingId(1), "APC", null), new ThingId(9), "cable",
            "9", new List<FeedDeviceView>
            {
                new FeedDeviceView(new ThingView(new ThingId(3), "P", null), "8", 8, new List<string> { "9", "5", "8" },
                    new List<string> { "5" })
            },
            new List<FeedRoomView>
            {
                new FeedRoomView("5", 2, new List<ThingId> { new ThingId(21), new ThingId(13) },
                    new List<PositionView>())
            }, new List<ThingView>());
        JObject feedJson = JObject.Parse(WireCheck.New(feed));
        Assert.Equal(1, (int)feedJson["daisy_chains"]!);
        Assert.Equal(1, (int)feedJson["multiple_feeds"]!);
        Assert.True((bool)feedJson["devices"]![0]!["fed_through_other_rooms"]!);
        Assert.True((bool)feedJson["rooms"]![0]!["multiple_feeds"]!);
    }
}
