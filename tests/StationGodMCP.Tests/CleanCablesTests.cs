#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// clean_cables: which ends of a piece are connected, what that makes of the piece, and that the piece with only
/// those ends is the one that matches and keeps every link.
/// </summary>
public sealed class CleanCablesTests
{
    private const int Power = 4;
    private const int Data = 2;
    private const int PowerAndData = Power | Data;

    private static readonly GridCell Origin = new GridCell(0, 0, 0);

    // An end of the piece in its own cell pointing one step along (dx, dy, dz): it sits in the neighbour's cell and
    // faces back into its own, as the game's ends do.
    private static PieceEnd End(GridCell own, int dx, int dy, int dz, int type = PowerAndData) =>
        new PieceEnd(new GridCell(own.X + dx, own.Y + dy, own.Z + dz), own, type, 0);

    private static PieceModel Piece(long id, GridCell cell, params PieceEnd[] ends) =>
        new PieceModel(id, new[] { cell }, ends, null);

    private static GridCell At(int x, int y, int z) => new GridCell(x, y, z);

    // A 3-way junction at the origin with ends along +x, -x and +z.
    private static PieceModel Tee(long id = 1) =>
        Piece(id, Origin, End(Origin, 1, 0, 0), End(Origin, -1, 0, 0), End(Origin, 0, 0, 1));

    private static PieceModel StraightX(long id, GridCell cell) =>
        Piece(id, cell, End(cell, 1, 0, 0), End(cell, -1, 0, 0));

    private static PieceModel StraightZ(long id, GridCell cell) =>
        Piece(id, cell, End(cell, 0, 0, 1), End(cell, 0, 0, -1));

    private static List<string> Directions(IReadOnlyList<PieceEnd> ends) => EndCleanup.DirectionsOf(ends);

    [Fact]
    public void ATeeBetweenTwoOppositeNeighboursBecomesAStraight()
    {
        PieceModel tee = Tee();
        List<PieceModel> others = new List<PieceModel> { StraightX(2, At(1, 0, 0)), StraightX(3, At(-1, 0, 0)) };

        List<PieceEnd> connected = Connectivity.ConnectedEnds(tee, others);
        PieceModel wanted = EndCleanup.WithEnds(tee, connected);

        Assert.Equal(new[] { "+x", "-x" }, Directions(connected));
        Assert.Equal(EndUse.Shrink, EndCleanup.Of(tee.Ends.Count, connected.Count));
        Assert.True(Connectivity.SameShape(wanted, StraightX(1, Origin)));
        Assert.False(Connectivity.SameShape(wanted, StraightZ(1, Origin)));
        Assert.False(Connectivity.SameShape(wanted,
            Piece(1, Origin, End(Origin, 1, 0, 0), End(Origin, 0, 0, 1))));
    }

    [Fact]
    public void ATeeBetweenTwoAdjacentNeighboursBecomesTheCornerTurnedTheirWay()
    {
        PieceModel tee = Tee();
        List<PieceModel> others = new List<PieceModel> { StraightX(2, At(1, 0, 0)), StraightZ(3, At(0, 0, 1)) };

        PieceModel wanted = EndCleanup.WithEnds(tee, Connectivity.ConnectedEnds(tee, others));

        PieceModel cornerThere = Piece(1, Origin, End(Origin, 0, 0, 1), End(Origin, 1, 0, 0));
        PieceModel cornerTurnedAway = Piece(1, Origin, End(Origin, 1, 0, 0), End(Origin, 0, 0, -1));
        Assert.True(Connectivity.SameShape(wanted, cornerThere));
        Assert.False(Connectivity.SameShape(wanted, cornerTurnedAway));
        Assert.False(Connectivity.SameShape(wanted, StraightX(1, Origin)));
    }

    [Fact]
    public void ACrossJoiningThreeBecomesATeeAndAFiveWayJoiningFourACross()
    {
        PieceModel cross = Piece(1, Origin, End(Origin, 1, 0, 0), End(Origin, -1, 0, 0), End(Origin, 0, 0, 1),
            End(Origin, 0, 0, -1));
        List<PieceModel> three = new List<PieceModel>
        {
            StraightX(2, At(1, 0, 0)), StraightX(3, At(-1, 0, 0)), StraightZ(4, At(0, 0, 1))
        };
        Assert.True(Connectivity.SameShape(EndCleanup.WithEnds(cross, Connectivity.ConnectedEnds(cross, three)),
            Tee()));

        PieceModel fiveWay = Piece(1, Origin, End(Origin, 1, 0, 0), End(Origin, -1, 0, 0), End(Origin, 0, 0, 1),
            End(Origin, 0, 0, -1), End(Origin, 0, 1, 0));
        List<PieceModel> four = new List<PieceModel>(three) { StraightZ(5, At(0, 0, -1)) };
        List<PieceEnd> connected = Connectivity.ConnectedEnds(fiveWay, four);
        Assert.Equal(EndUse.Shrink, EndCleanup.Of(fiveWay.Ends.Count, connected.Count));
        Assert.True(Connectivity.SameShape(EndCleanup.WithEnds(fiveWay, connected), cross));
    }

    [Fact]
    public void ThePieceWithOnlyTheConnectedEndsKeepsEveryLink()
    {
        PieceModel tee = Tee();
        PieceModel left = StraightX(2, At(1, 0, 0));
        PieceModel right = StraightX(3, At(-1, 0, 0));
        PieceModel straight = EndCleanup.WithEnds(tee, Connectivity.ConnectedEnds(tee,
            new List<PieceModel> { left, right }));
        HashSet<long> focus = new HashSet<long> { 1 };

        HashSet<Link> before = Connectivity.LinksTouching(new List<PieceModel> { tee, left, right }, focus);
        HashSet<Link> after = Connectivity.LinksTouching(new List<PieceModel> { straight, left, right }, focus);

        Assert.Equal(4, before.Count);
        Assert.True(Connectivity.Compare(before, after).Same);
    }

    [Fact]
    public void ACablePassingAcrossAnEndIsNotConnectedToIt()
    {
        PieceModel tee = Tee();
        List<PieceModel> others = new List<PieceModel>
        {
            StraightX(2, At(1, 0, 0)), StraightX(3, At(-1, 0, 0)), StraightX(4, At(0, 0, 1))
        };

        Assert.Equal(new[] { "+x", "-x" }, Directions(Connectivity.ConnectedEnds(tee, others)));
    }

    [Fact]
    public void ADeviceEndFacingThePieceConnectsItsEndThroughASharedNetworkType()
    {
        PieceModel tee = Tee();
        GridCell deviceCell = At(0, 0, 1);
        PieceModel dataDevice = Piece(9, deviceCell, End(deviceCell, 0, 0, -1, Data));
        PieceModel otherDevice = Piece(9, deviceCell, End(deviceCell, 0, 0, -1, 8));

        Assert.Equal(new[] { "+z" }, Directions(Connectivity.ConnectedEnds(tee, new List<PieceModel> { dataDevice })));
        Assert.Empty(Connectivity.ConnectedEnds(tee, new List<PieceModel> { otherDevice }));
    }

    [Fact]
    public void EndsWithoutASharedNetworkTypeAreOpen()
    {
        PieceModel dataOnly = Piece(1, Origin, End(Origin, 1, 0, 0, Data), End(Origin, -1, 0, 0, Data));
        GridCell next = At(1, 0, 0);
        PieceModel powerOnly = Piece(2, next, End(next, 1, 0, 0, Power), End(next, -1, 0, 0, Power));

        Assert.Empty(Connectivity.ConnectedEnds(dataOnly, new List<PieceModel> { powerOnly }));
    }

    [Theory]
    [InlineData(3, 3, EndUse.AllConnected)]
    [InlineData(2, 2, EndUse.AllConnected)]
    [InlineData(0, 0, EndUse.AllConnected)]
    [InlineData(3, 2, EndUse.Shrink)]
    [InlineData(6, 3, EndUse.Shrink)]
    [InlineData(4, 1, EndUse.DeadEnd)]
    [InlineData(2, 1, EndUse.DeadEnd)]
    [InlineData(4, 0, EndUse.Isolated)]
    internal void ConnectedEndsDecideWhatHappens(int ends, int connected, EndUse expected) =>
        Assert.Equal(expected, EndCleanup.Of(ends, connected));

    [Fact]
    public void AStubAtTheEndOfARunIsADeadEnd()
    {
        PieceModel stub = StraightX(1, Origin);
        List<PieceEnd> connected = Connectivity.ConnectedEnds(stub,
            new List<PieceModel> { StraightX(2, At(-1, 0, 0)) });
        Assert.Equal(EndUse.DeadEnd, EndCleanup.Of(stub.Ends.Count, connected.Count));
        Assert.Equal(new[] { "-x" }, Directions(connected));
    }

    [Fact]
    public void DirectionsAreWorldAxesFromTheOwnCellToTheNeighbours()
    {
        GridCell cell = At(4, -2, 7);
        Assert.Equal(new[] { "+x", "-x", "+y", "-y", "+z", "-z" }, Directions(new[]
        {
            End(cell, 1, 0, 0), End(cell, -1, 0, 0), End(cell, 0, 1, 0), End(cell, 0, -1, 0), End(cell, 0, 0, 1),
            End(cell, 0, 0, -1)
        }));
        Assert.Equal("(2, 0, 1)", EndCleanup.DirectionOf(End(cell, 2, 0, 1)));
    }

    [Fact]
    public void DirectionsCountOnlyTheSignOfTheGridsOwnStep()
    {
        // The game's small-grid cells are Grid3 of their centres in tenths of a metre: neighbours are 5 apart.
        GridCell cell = At(25, 5, -35);
        Assert.Equal("+x", EndCleanup.DirectionOf(End(cell, 5, 0, 0)));
        Assert.Equal("-z", EndCleanup.DirectionOf(End(cell, 0, 0, -5)));
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, -1)]
    [InlineData(1, 3, 2)]
    public void TheCoilChargesTheDifferenceOfEntryQuantities(int oldCost, int newCost, int expected) =>
        Assert.Equal(expected, EndCleanup.CostDifference(oldCost, newCost));
}

/// <summary>clean_cables: reply shapes, and that the upgrade tools' replies keep theirs.</summary>
public sealed class CleanCablesWireTests
{
    private static UpgradeReportView Report(UpgradeCleanView? ends, UpgradeDeadEnds? deadEnds)
    {
        UpgradeHeader header = new UpgradeHeader("clean_cables", "minimal", "dry_run", null, new List<string>());
        UpgradeTargetView target =
            new UpgradeTargetView("StructureCableStraight", new RotationView(0, 90, 0), false, 0);
        UpgradePieceView piece = new UpgradePieceView(
            new ThingView(new ThingId(101), "StructureCableJunction", "Cable"), new PositionView(1, 2, 3),
            new RotationView(0, 0, 0), target, new ThingId(900), ends);
        UpgradeLists lists = new UpgradeLists(new List<UpgradeProblemView>(), new List<UpgradePieceView> { piece },
            new List<object>(), new List<UpgradeSkippedView>(), new List<UpgradeSkippedView>());
        UpgradeResources resources = new UpgradeResources(null, new List<UpgradeCoilView>(), true,
            new List<UpgradeAmountView>(), new List<object>());
        return new UpgradeReportView(header, new UpgradeCounts(3, 1, 0, 0), lists, resources, null, deadEnds);
    }

    [Fact]
    public void APieceListsItsEndsAndTheConnectedOnes()
    {
        string json = WireCheck.New(Report(new UpgradeCleanView("simplify_junction",
            new List<string> { "+x", "-x", "+z" }, new List<string> { "+x", "-x" }, 1),
            new UpgradeDeadEnds(0, new List<UpgradeDeadEndView>())));
        Assert.Contains(
            "\"rotation_kept\":false,\"cost\":0,\"network_id\":\"900\",\"operation\":\"simplify_junction\","
            + "\"ends\":[\"+x\",\"-x\",\"+z\"],\"connected_ends\":[\"+x\",\"-x\"],\"replacement_count\":1,"
            + "\"refund_count\":0}]",
            json);
        Assert.Contains("\"unmatched_pieces\":[],\"dead_ends\":0,\"dead_end_pieces\":[],\"from\":null", json);
    }

    [Fact]
    public void ADeadEndIsListedWithWhereItIsAndWhichEndIsConnected()
    {
        UpgradeDeadEndView stub = new UpgradeDeadEndView(new ThingView(new ThingId(7), "StructureCableStraight",
            "Cable"), new PositionView(1, 2, 3), "dead_end", new List<string> { "+x", "-x" },
            new List<string> { "-x" });
        Assert.Equal(
            "{\"reference_id\":\"7\",\"prefab_name\":\"StructureCableStraight\",\"position\":{\"x\":1.0,\"y\":2.0,"
            + "\"z\":3.0},\"reason\":\"dead_end\",\"ends\":[\"+x\",\"-x\"],\"connected_ends\":[\"-x\"]}",
            WireCheck.New(stub));
    }

    [Fact]
    public void AJobListsRemovedPiecesOnlyWhenThereAreAny()
    {
        UpgradeSwapLog log = new UpgradeSwapLog();
        log.Removed.Add(new ThingId(5));
        UpgradeJobView removed = new UpgradeJobView("upgrade-3", "clean_pipes", "applied", Report(null, null),
            new UpgradeJobResult(null, log, null, null));
        UpgradeJobView none = new UpgradeJobView("upgrade-4", "clean_pipes", "applied", Report(null, null),
            new UpgradeJobResult(null, new UpgradeSwapLog(), null, null));

        Assert.Contains("\"swapped_count\":0,\"removed\":[\"5\"],\"not_swapped\":[]", WireCheck.New(removed));
        Assert.Contains("\"swapped_count\":0,\"not_swapped\":[]", WireCheck.New(none));
    }

    [Fact]
    public void ADeadEndLeftByRemovalSaysWhy()
    {
        UpgradeDeadEndView held = new UpgradeDeadEndView(
            new ThingView(new ThingId(8), "StructurePipeStraight", "Pipe"), new PositionView(0, 0, 0), "isolated",
            new List<string> { "+x", "-x" }, new List<string>(),
            "holds_contents: 1.5 mol");
        Assert.EndsWith("\"connected_ends\":[],\"stopped_by\":\"holds_contents: 1.5 mol\"}", WireCheck.New(held));
    }

    [Fact]
    public void AnUpgradeReportCarriesNoEndsOrDeadEnds()
    {
        string json = WireCheck.New(Report(null, null));
        Assert.Contains("\"network_id\":\"900\"}]", json);
        Assert.DoesNotContain("\"ends\"", json);
        Assert.DoesNotContain("operation", json);
        Assert.DoesNotContain("replacement_count", json);
        Assert.DoesNotContain("dead_end", json);
    }
}
