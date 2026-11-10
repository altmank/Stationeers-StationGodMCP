#nullable enable

using System.Collections.Generic;
using System.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>How early ghosts replayed into Lattice are paired with their new ids and their refusals reported.</summary>
public sealed class GhostReplayTests
{
    private static BoardGhost Ghost(long id, string name, double x) =>
        new BoardGhost(id, (int)id, name, 0, new Vec3(x, 1, 2), new Vec3(0, 0, 0), new Rgba(1, 1, 1, 1),
            GhostSource.Agent, "agent", null, 0);

    [Fact]
    public void AllPlacedPairsInOrder()
    {
        GhostReplay result = GhostReplay.Of(new[] { Ghost(1, "A", 0), Ghost(2, "B", 1) }, new long[] { 7, 8 },
            new string[0]);

        Assert.Equal(new[] { (1L, 7L), (2L, 8L) }, result.Mapped);
        Assert.Empty(result.RefusedLines);
    }

    [Fact]
    public void PartialRefusalMapsThePlacedAndNamesTheRefused()
    {
        GhostReplay result = GhostReplay.Of(new[] { Ghost(1, "A", 0), Ghost(2, "B", 1.5), Ghost(3, "C", 2) },
            new long[] { 9, 10 }, new[] { "1: no structure prefab" });

        Assert.Equal(new[] { (1L, 9L), (3L, 10L) }, result.Mapped);
        Assert.Equal(new[] { "2 B at (1.5, 1, 2): no structure prefab" }, result.RefusedLines);
    }

    [Fact]
    public void WholeBatchRefusalNamesEveryGhostWithLatticesReason()
    {
        GhostReplay result = GhostReplay.Of(new[] { Ghost(1, "A", 0), Ghost(2, "B", 1) }, new long[0],
            new[] { "Lattice refused it: board full" });

        Assert.Empty(result.Mapped);
        Assert.Equal(2, result.RefusedLines.Count);
        Assert.All(result.RefusedLines, line => Assert.EndsWith("Lattice refused it: board full", line));
    }

    [Fact]
    public void NoAnswerIsSaidSoNotZeroRefused()
    {
        GhostReplay result = GhostReplay.Of(new[] { Ghost(1, "A", 0) }, new long[0], new string[0]);

        Assert.Empty(result.Mapped);
        Assert.EndsWith(": no answer", result.RefusedLines.Single());
    }

    [Fact]
    public void FewerIdsThanGhostsLeavesTheRestNotPlaced()
    {
        GhostReplay result = GhostReplay.Of(new[] { Ghost(1, "A", 0), Ghost(2, "B", 1) }, new long[] { 5 },
            new string[0]);

        Assert.Equal(new[] { (1L, 5L) }, result.Mapped);
        Assert.EndsWith(": not placed", result.RefusedLines.Single());
    }
}
