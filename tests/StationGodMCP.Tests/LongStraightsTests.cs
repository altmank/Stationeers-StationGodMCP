#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// clean_cables and clean_pipes, split_long_straights: a long straight becomes one single per cell it covers, in its
/// line, with the long piece's tip ends, id and content, and the links out of the run stay exactly the long piece's.
/// Cells are in the game's units: Grid3 of a small cell's centre in tenths of a metre, so neighbours are 5 apart.
/// </summary>
public sealed class LongStraightsTests
{
    private const int Step = 5;
    private const int PowerAndData = 6;
    private const int Gas = 1;
    private const int Liquid = 2;

    private static GridCell At(int x, int y, int z) => new GridCell(x, y, z);

    // An end in `own` pointing to `to`: it sits in the neighbour's cell and faces back into its own.
    private static PieceEnd EndTo(GridCell own, GridCell to, int type = PowerAndData) =>
        new PieceEnd(to, own, type, 0);

    // A long straight of `length` cells along the axis (dx, dy, dz) from `start`, ends at both tips.
    private static PieceModel Long(long id, GridCell start, int dx, int dy, int dz, int length,
        int type = PowerAndData, PipeContent? content = null)
    {
        GridCell[] cells = new GridCell[length];
        for (int index = 0; index < length; index++)
        {
            cells[index] = At(start.X + index * dx * Step, start.Y + index * dy * Step, start.Z + index * dz * Step);
        }

        GridCell last = cells[length - 1];
        GridCell before = At(start.X - dx * Step, start.Y - dy * Step, start.Z - dz * Step);
        GridCell after = At(last.X + dx * Step, last.Y + dy * Step, last.Z + dz * Step);
        return new PieceModel(id, cells, new[] { EndTo(start, before, type), EndTo(last, after, type) }, content);
    }

    private static PieceModel Single(long id, GridCell cell, GridCell back, GridCell ahead) =>
        new PieceModel(id, new[] { cell }, new[] { EndTo(cell, back), EndTo(cell, ahead) }, null);

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void ALongStraightSplitsIntoOneSinglePerCellInItsLine(int length)
    {
        PieceModel run = Long(7, At(10, 0, -20), 1, 0, 0, length);

        List<PieceModel>? parts = LongStraights.Split(run);

        Assert.NotNull(parts);
        Assert.Equal(length, parts!.Count);
        for (int index = 0; index < length; index++)
        {
            PieceModel part = parts[index];
            Assert.Equal(7, part.Id);
            Assert.Equal(new[] { At(10 + index * Step, 0, -20) }, part.Cells);
            Assert.Equal(new[] { "-x", "+x" }, EndCleanup.DirectionsOf(part.Ends));
            Assert.All(part.Ends, end => Assert.Equal(PowerAndData, end.Type));
        }
    }

    [Fact]
    public void TheSinglesAreTheStraightsAlongTheRunNotAcrossIt()
    {
        List<PieceModel> parts = LongStraights.Split(Long(1, At(0, 0, 0), 0, 0, 1, 3))!;
        GridCell middle = At(0, 0, Step);

        Assert.True(Connectivity.SameShape(parts[1], Single(1, middle, At(0, 0, 0), At(0, 0, 2 * Step))));
        Assert.False(Connectivity.SameShape(parts[1], Single(1, middle, At(-Step, 0, Step), At(Step, 0, Step))));
        Assert.Equal(new[] { "-z", "+z" }, EndCleanup.DirectionsOf(parts[1].Ends));
    }

    [Fact]
    public void AVerticalRunSplitsUpwardAndItsTipEndsAreTheLongPiecesOwn()
    {
        PieceModel run = Long(3, At(0, 5, 0), 0, 1, 0, 5);
        List<PieceModel> parts = LongStraights.Split(run)!;

        Assert.Equal(At(0, 5 + 4 * Step, 0), parts[4].Cells[0]);
        Assert.Contains(run.Ends[0], parts[0].Ends);
        Assert.Contains(run.Ends[1], parts[4].Ends);
    }

    [Fact]
    public void EndsListedEitherWayRoundAndCellsInAnyOrderSplitTheSame()
    {
        PieceModel run = Long(1, At(0, 0, 0), 1, 0, 0, 3);
        PieceModel shuffled = new PieceModel(1, new[] { run.Cells[2], run.Cells[0], run.Cells[1] },
            new[] { run.Ends[1], run.Ends[0] }, null);

        List<PieceModel> a = LongStraights.Split(run)!;
        List<PieceModel> b = LongStraights.Split(shuffled)!;

        for (int index = 0; index < a.Count; index++)
        {
            Assert.True(Connectivity.SameShape(a[index], b[index]));
        }
    }

    [Fact]
    public void ThePipeContentStaysOnEverySingle()
    {
        PipeContent liquid = new PipeContent(Liquid, false);
        List<PieceModel> parts = LongStraights.Split(Long(1, At(0, 0, 0), 1, 0, 0, 3, Gas, liquid))!;
        Assert.All(parts, part => Assert.Same(liquid, part.Content));
        Assert.All(parts, part => Assert.All(part.Ends, end => Assert.Equal(Gas, end.Type)));
    }

    [Fact]
    public void SplittingKeepsEveryLinkOutOfTheRunAndAddsNone()
    {
        PieceModel run = Long(2, At(0, 0, 0), 1, 0, 0, 5);
        PieceModel left = Single(1, At(-Step, 0, 0), At(-2 * Step, 0, 0), At(0, 0, 0));
        PieceModel right = Single(3, At(5 * Step, 0, 0), At(4 * Step, 0, 0), At(6 * Step, 0, 0));
        PieceModel beside = Single(4, At(2 * Step, 0, Step), At(Step, 0, Step), At(3 * Step, 0, Step));
        HashSet<long> focus = new HashSet<long> { 2 };
        List<PieceModel> after = new List<PieceModel>(LongStraights.Split(run)!) { left, right, beside };

        HashSet<Link> linksBefore = Connectivity.LinksTouching(new List<PieceModel> { run, left, right, beside },
            focus);
        HashSet<Link> linksAfter = Connectivity.LinksTouching(after, focus);

        Assert.Equal(4, linksBefore.Count);
        Assert.True(Connectivity.Compare(linksBefore, linksAfter).Same);
    }

    [Fact]
    public void NoLongStraightIsNotSplit()
    {
        GridCell origin = At(0, 0, 0);
        PieceModel single = Single(1, origin, At(-Step, 0, 0), At(Step, 0, 0));
        PieceModel bent = new PieceModel(1, new[] { origin, At(Step, 0, 0), At(Step, 0, Step) },
            new[] { EndTo(origin, At(-Step, 0, 0)), EndTo(At(Step, 0, Step), At(Step, 0, 2 * Step)) }, null);
        PieceModel gapped = new PieceModel(1, new[] { origin, At(2 * Step, 0, 0) },
            new[] { EndTo(origin, At(-Step, 0, 0)), EndTo(At(2 * Step, 0, 0), At(3 * Step, 0, 0)) }, null);
        PieceModel sideEnd = new PieceModel(1, new[] { origin, At(Step, 0, 0) },
            new[] { EndTo(origin, At(-Step, 0, 0)), EndTo(At(Step, 0, 0), At(Step, 0, Step)) }, null);
        PieceModel mixedTypes = new PieceModel(1, new[] { origin, At(Step, 0, 0) },
            new[] { EndTo(origin, At(-Step, 0, 0), 2), EndTo(At(Step, 0, 0), At(2 * Step, 0, 0), 4) }, null);
        PieceModel threeEnds = new PieceModel(1, new[] { origin, At(Step, 0, 0) },
            new[]
            {
                EndTo(origin, At(-Step, 0, 0)), EndTo(At(Step, 0, 0), At(2 * Step, 0, 0)),
                EndTo(origin, At(0, Step, 0))
            }, null);

        Assert.Null(LongStraights.Split(single));
        Assert.Null(LongStraights.Split(bent));
        Assert.Null(LongStraights.Split(gapped));
        Assert.Null(LongStraights.Split(sideEnd));
        Assert.Null(LongStraights.Split(mixedTypes));
        Assert.Null(LongStraights.Split(threeEnds));
    }
}
