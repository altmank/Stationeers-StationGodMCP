#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Model builders for the clean operations' tests, in the game's cell units (neighbours 5 apart).</summary>
internal static class CleanModels
{
    internal const int Step = 5;
    internal const int PowerAndData = 6;

    internal static GridCell At(int x, int y, int z) => new GridCell(x * Step, y * Step, z * Step);

    // An end of the piece in `own` towards the neighbouring cell `to`.
    internal static PieceEnd EndTo(GridCell own, GridCell to, int type = PowerAndData) =>
        new PieceEnd(to, own, type, 0);

    internal static PieceModel Piece(long id, GridCell cell, params GridCell[] towards)
    {
        PieceEnd[] ends = new PieceEnd[towards.Length];
        for (int index = 0; index < towards.Length; index++)
        {
            ends[index] = EndTo(cell, towards[index]);
        }

        return new PieceModel(id, new[] { cell }, ends, null);
    }

    // A single straight along x at (x, 0, 0).
    internal static PieceModel StraightX(long id, int x) => Piece(id, At(x, 0, 0), At(x - 1, 0, 0), At(x + 1, 0, 0));
}

/// <summary>operations: names, order, default and conflicts.</summary>
public sealed class CleanOperationSetTests
{
    [Fact]
    public void OperationsRunInTheirFixedOrderWhateverOrderTheyAreNamedIn()
    {
        List<string>? ordered = CleanOperationSet.Parse(
            new[] { "simplify_junctions", " MERGE_STRAIGHTS ", "remove_dead_ends", "simplify_junctions" },
            out string? error);
        Assert.Null(error);
        Assert.Equal(new[] { "remove_dead_ends", "merge_straights", "simplify_junctions" }, ordered);
    }

    [Fact]
    public void EveryOperationCanRunAlone()
    {
        foreach (string name in CleanOperationSet.Order)
        {
            Assert.Equal(new[] { name }, CleanOperationSet.Parse(new[] { name }, out _));
        }
    }

    [Fact]
    public void TheDefaultIsJunctionSimplificationOnly() =>
        Assert.Equal(new[] { "simplify_junctions" }, CleanOperationSet.Default);

    [Theory]
    [InlineData("split_long_straights", "merge_straights")]
    [InlineData("frobnicate")]
    public void SplitWithMergeAndUnknownNamesAreRefused(params string[] names)
    {
        Assert.Null(CleanOperationSet.Parse(names, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void NoOperationIsRefused() => Assert.Null(CleanOperationSet.Parse(new string[0], out _));
}

/// <summary>remove_dead_ends in isolation (DeadEndPruning).</summary>
public sealed class DeadEndPruningTests
{
    private static readonly Dictionary<long, string> NoBlocks = new Dictionary<long, string>();

    // A T at the origin: -x and +z lead to fixed pieces outside the selection, +x to a run of three ending in a stub.
    private static (List<PieceModel> Candidates, List<PieceModel> Others) TeeWithStubRun()
    {
        GridCell origin = CleanModels.At(0, 0, 0);
        PieceModel tee = CleanModels.Piece(1, origin, CleanModels.At(1, 0, 0), CleanModels.At(-1, 0, 0),
            CleanModels.At(0, 0, 1));
        PieceModel a = CleanModels.StraightX(2, 1);
        PieceModel b = CleanModels.StraightX(3, 2);
        PieceModel c = CleanModels.Piece(4, CleanModels.At(3, 0, 0), CleanModels.At(2, 0, 0), CleanModels.At(3, 0, 1));
        PieceModel west = CleanModels.StraightX(10, -1);
        PieceModel north = CleanModels.Piece(11, CleanModels.At(0, 0, 1), CleanModels.At(0, 0, 0),
            CleanModels.At(0, 0, 2));
        return (new List<PieceModel> { tee, a, b, c }, new List<PieceModel> { west, north });
    }

    [Fact]
    public void AStubRunGoesRoundByRoundAndTheJunctionItHungFromStays()
    {
        (List<PieceModel> candidates, List<PieceModel> others) = TeeWithStubRun();

        PruneResult result = DeadEndPruning.Prune(candidates, others, new HashSet<long>(), NoBlocks);

        Assert.Equal(new[] { new KeyValuePair<long, int>(4, 1), new KeyValuePair<long, int>(3, 2),
            new KeyValuePair<long, int>(2, 3) }, result.Removed);
        Assert.False(result.IsRemoved(1));
        Assert.Empty(result.Stops);
    }

    [Fact]
    public void AStubWhoseOneConnectionIsADeviceStays()
    {
        (List<PieceModel> candidates, List<PieceModel> others) = TeeWithStubRun();
        GridCell stubCell = CleanModels.At(5, 0, 0);
        candidates.Add(CleanModels.Piece(5, stubCell, CleanModels.At(6, 0, 0), CleanModels.At(5, 0, 1)));
        GridCell deviceCell = CleanModels.At(6, 0, 0);
        others.Add(CleanModels.Piece(20, deviceCell, stubCell));

        PruneResult result = DeadEndPruning.Prune(candidates, others, new HashSet<long> { 20 }, NoBlocks);

        Assert.Equal(DeadEndPruning.DeviceConnected, result.Stops[5]);
        Assert.False(result.IsRemoved(5));
        Assert.True(result.IsRemoved(4));
    }

    [Fact]
    public void ARunEndingAtADeviceIsNoStubAtAll()
    {
        (List<PieceModel> candidates, List<PieceModel> others) = TeeWithStubRun();
        candidates[3] = CleanModels.StraightX(4, 3);
        others.Add(CleanModels.Piece(20, CleanModels.At(4, 0, 0), CleanModels.At(3, 0, 0)));

        PruneResult result = DeadEndPruning.Prune(candidates, others, new HashSet<long> { 20 }, NoBlocks);

        Assert.Empty(result.Removed);
        Assert.Empty(result.Stops);
    }

    [Fact]
    public void ABlockedStubStaysWithItsReasonAndStopsTheRounds()
    {
        (List<PieceModel> candidates, List<PieceModel> others) = TeeWithStubRun();
        Dictionary<long, string> blocked = new Dictionary<long, string> { [3] = "device_mounted" };

        PruneResult result = DeadEndPruning.Prune(candidates, others, new HashSet<long>(), blocked);

        Assert.Equal(new[] { new KeyValuePair<long, int>(4, 1) }, result.Removed);
        Assert.Equal("device_mounted", result.Stops[3]);
        Assert.False(result.IsRemoved(2));
    }

    [Fact]
    public void AnIsolatedPieceGoesInTheFirstRoundAndPiecesOutsideTheSelectionNever()
    {
        PieceModel alone = CleanModels.StraightX(1, 10);
        PieceModel outsideStub = CleanModels.StraightX(9, 20);

        PruneResult result = DeadEndPruning.Prune(new List<PieceModel> { alone }, new List<PieceModel> { outsideStub },
            new HashSet<long>(), NoBlocks);

        Assert.Equal(new[] { new KeyValuePair<long, int>(1, 1) }, result.Removed);
    }

    [Fact]
    public void ALoopIsNeverADeadEnd()
    {
        GridCell a = CleanModels.At(0, 0, 0);
        GridCell b = CleanModels.At(1, 0, 0);
        GridCell c = CleanModels.At(1, 0, 1);
        GridCell d = CleanModels.At(0, 0, 1);
        List<PieceModel> ring = new List<PieceModel>
        {
            CleanModels.Piece(1, a, b, d), CleanModels.Piece(2, b, a, c), CleanModels.Piece(3, c, b, d),
            CleanModels.Piece(4, d, c, a)
        };

        PruneResult result = DeadEndPruning.Prune(ring, new List<PieceModel>(), new HashSet<long>(), NoBlocks);

        Assert.Empty(result.Removed);
        Assert.Empty(result.Stops);
    }

    [Fact]
    public void LinksAfterAreTheLinksBeforeLessTheRemovedPiecesOwn()
    {
        (List<PieceModel> candidates, List<PieceModel> others) = TeeWithStubRun();
        PruneResult result = DeadEndPruning.Prune(candidates, others, new HashSet<long>(), NoBlocks);
        HashSet<long> gone = new HashSet<long>();
        foreach (KeyValuePair<long, int> removal in result.Removed)
        {
            gone.Add(removal.Key);
        }

        List<PieceModel> all = new List<PieceModel>(candidates);
        all.AddRange(others);
        List<PieceModel> left = all.FindAll(model => !gone.Contains(model.Id));
        HashSet<long> focus = new HashSet<long> { 1, 2, 3, 4 };

        HashSet<Link> expected = Connectivity.Without(Connectivity.LinksTouching(all, focus), gone);
        HashSet<Link> after = Connectivity.LinksTouching(left, focus);

        Assert.True(Connectivity.Compare(expected, after).Same);
        Assert.Equal(4, after.Count);
    }
}

/// <summary>merge_straights in isolation (StraightRuns).</summary>
public sealed class StraightRunsTests
{
    private static Dictionary<long, int> SameKey(IEnumerable<PieceModel> models)
    {
        Dictionary<long, int> keys = new Dictionary<long, int>();
        foreach (PieceModel model in models)
        {
            keys[model.Id] = 1;
        }

        return keys;
    }

    private static List<PieceModel> Run(int from, int count, long firstId = 1)
    {
        List<PieceModel> run = new List<PieceModel>(count);
        for (int index = 0; index < count; index++)
        {
            run.Add(CleanModels.StraightX(firstId + index, from + index));
        }

        return run;
    }

    [Theory]
    [InlineData(12, new[] { 0, 10 })]
    [InlineData(8, new[] { 0, 5, 5, 3 })]
    [InlineData(10, new[] { 0, 10 })]
    [InlineData(4, new[] { 0, 3 })]
    [InlineData(2, new int[0])]
    public void RunsAreCoveredLongestFirstAndLeftoversStaySingle(int count, int[] expected)
    {
        List<KeyValuePair<int, int>> cover = StraightRuns.Cover(count, new[] { 3, 10, 5 });
        List<int> flat = new List<int>();
        foreach (KeyValuePair<int, int> part in cover)
        {
            flat.Add(part.Key);
            flat.Add(part.Value);
        }

        Assert.Equal(expected, flat);
    }

    [Fact]
    public void FiveSinglesInALineBecomeOneFiveLongWithTheRunsTipEnds()
    {
        List<PieceModel> singles = Run(0, 5);

        List<StraightSegment> segments = StraightRuns.Plan(singles, SameKey(singles), new[] { 3, 5, 10 });

        StraightSegment only = Assert.Single(segments);
        Assert.Equal(5, only.Singles.Count);
        Assert.Equal(1, only.Long.Id);
        Assert.Equal(5, only.Long.Cells.Count);
        Assert.NotNull(LongStraights.Split(only.Long));
        Assert.Equal(new[] { "-x", "+x" }, EndCleanup.DirectionsOf(only.Long.Ends));
    }

    [Fact]
    public void MergingKeepsEveryLinkOutOfTheRun()
    {
        List<PieceModel> singles = Run(0, 3);
        PieceModel west = CleanModels.StraightX(8, -1);
        PieceModel east = CleanModels.StraightX(9, 3);
        StraightSegment segment = StraightRuns.Plan(singles, SameKey(singles), new[] { 3 })[0];
        Dictionary<long, long> group = new Dictionary<long, long> { [1] = 1, [2] = 1, [3] = 1 };
        HashSet<long> focus = new HashSet<long> { 1, 2, 3 };
        List<PieceModel> before = new List<PieceModel>(singles) { west, east };

        HashSet<Link> expected = Connectivity.Grouped(Connectivity.LinksTouching(before, focus), group);
        HashSet<Link> after = Connectivity.LinksTouching(new List<PieceModel> { segment.Long, west, east }, focus);

        Assert.Equal(4, expected.Count);
        Assert.True(Connectivity.Compare(expected, after).Same);
    }

    [Fact]
    public void ADifferentKeyOrAJunctionBreaksTheRun()
    {
        List<PieceModel> singles = Run(0, 7);
        Dictionary<long, int> keys = SameKey(singles);
        keys[4] = 2;
        Assert.Equal(new[] { 3, 3 }, StraightRuns.Runs(singles, keys).ConvertAll(run => run.Count));

        List<PieceModel> withTee = Run(0, 3);
        withTee.Add(CleanModels.Piece(4, CleanModels.At(3, 0, 0), CleanModels.At(2, 0, 0), CleanModels.At(4, 0, 0),
            CleanModels.At(3, 0, 1)));
        withTee.AddRange(Run(4, 3, 5));
        Assert.Equal(new[] { 3, 3 },
            StraightRuns.Runs(withTee, SameKey(withTee)).ConvertAll(run => run.Count));
    }

    [Fact]
    public void SinglesSideBySideOrAcrossAreNoRun()
    {
        PieceModel along = CleanModels.StraightX(1, 0);
        PieceModel beside = CleanModels.Piece(2, CleanModels.At(0, 0, 1), CleanModels.At(-1, 0, 1),
            CleanModels.At(1, 0, 1));
        PieceModel across = CleanModels.Piece(3, CleanModels.At(1, 0, 0), CleanModels.At(1, 0, 1),
            CleanModels.At(1, 0, -1));
        List<PieceModel> models = new List<PieceModel> { along, beside, across };

        Assert.Empty(StraightRuns.Runs(models, SameKey(models)));
        Assert.False(StraightRuns.IsSingleStraight(CleanModels.Piece(4, CleanModels.At(0, 0, 0),
            CleanModels.At(1, 0, 0), CleanModels.At(0, 0, 1))));
    }
}

/// <summary>Operations together: simplify_junctions and merge_straights work on what remove_dead_ends leaves.</summary>
public sealed class CleanCombinationTests
{
    [Fact]
    public void PruningThenSimplifyingThenMergingKeepsExactlyTheLinksOfWhatStays()
    {
        // A tee at the origin: -x a run of three singles to a fixed piece, +x a stub run of two, +z a fixed piece.
        GridCell origin = CleanModels.At(0, 0, 0);
        PieceModel tee = CleanModels.Piece(1, origin, CleanModels.At(1, 0, 0), CleanModels.At(-1, 0, 0),
            CleanModels.At(0, 0, 1));
        List<PieceModel> run = new List<PieceModel>
        {
            CleanModels.StraightX(2, -1), CleanModels.StraightX(3, -2), CleanModels.StraightX(4, -3)
        };
        PieceModel stubA = CleanModels.StraightX(5, 1);
        PieceModel stubB = CleanModels.Piece(6, CleanModels.At(2, 0, 0), CleanModels.At(1, 0, 0),
            CleanModels.At(2, 0, 1));
        PieceModel westEnd = CleanModels.StraightX(10, -4);
        PieceModel north = CleanModels.Piece(11, CleanModels.At(0, 0, 1), origin, CleanModels.At(0, 0, 2));
        List<PieceModel> candidates = new List<PieceModel>(run) { tee, stubA, stubB };
        List<PieceModel> others = new List<PieceModel> { westEnd, north };

        PruneResult pruned = DeadEndPruning.Prune(candidates, others, new HashSet<long>(),
            new Dictionary<long, string>());
        HashSet<long> gone = new HashSet<long> { 5, 6 };
        Assert.Equal(gone, new HashSet<long>(new[] { pruned.Removed[0].Key, pruned.Removed[1].Key }));

        List<PieceModel> remaining = new List<PieceModel>(run) { westEnd, north };
        List<PieceEnd> teeConnected = Connectivity.ConnectedEnds(tee, remaining);
        Assert.Equal(EndUse.Shrink, EndCleanup.Of(tee.Ends.Count, teeConnected.Count));
        PieceModel corner = EndCleanup.WithEnds(tee, teeConnected);

        Dictionary<long, int> keys = new Dictionary<long, int> { [2] = 1, [3] = 1, [4] = 1 };
        StraightSegment merged = Assert.Single(StraightRuns.Plan(run, keys, new[] { 3 }));

        List<PieceModel> all = new List<PieceModel>(candidates);
        all.AddRange(others);
        HashSet<long> focus = new HashSet<long> { 1, 2, 3, 4, 5, 6 };
        Dictionary<long, long> group = new Dictionary<long, long> { [2] = 2, [3] = 2, [4] = 2 };
        HashSet<Link> expected = Connectivity.Without(
            Connectivity.Grouped(Connectivity.LinksTouching(all, focus), group), gone);
        HashSet<Link> after = Connectivity.LinksTouching(
            new List<PieceModel> { corner, merged.Long, westEnd, north }, focus);

        Assert.True(Connectivity.Compare(expected, after).Same);
        Assert.Contains(new Link(1, 2), after);
        Assert.Contains(new Link(2, 10), after);
    }
}
