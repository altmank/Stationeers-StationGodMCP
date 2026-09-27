#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class RunLoopsTests
{
    private const long NetworkN = 100;
    private const long NetworkM = 200;

    [Fact]
    public void ARunWhoseEndsBothReachOneNetworkClosesALoop()
    {
        Dictionary<long, long> nodeOf = new Dictionary<long, long>
        {
            [1] = NetworkN, [2] = NetworkN, [-1] = -1, [-2] = -2
        };
        List<Link> links = new List<Link> { new Link(1, -1), new Link(-1, 1), new Link(-1, -2), new Link(-2, 2) };

        Link closing = Assert.Single(RunLoops.Closing(links, nodeOf, new HashSet<Link>(), new long[] { -1, -2 }));
        Assert.True(closing.From < 0 || closing.To < 0);
    }

    [Fact]
    public void ARunBetweenTwoNetworksClosesNothing()
    {
        Dictionary<long, long> nodeOf = new Dictionary<long, long> { [1] = NetworkN, [2] = NetworkM, [-1] = -1 };
        List<Link> links = new List<Link> { new Link(1, -1), new Link(-1, 2) };

        Assert.Empty(RunLoops.Closing(links, nodeOf, new HashSet<Link>(), new long[] { -1 }));
    }

    [Fact]
    public void AChangedPieceKeepsItsOldLinksWithoutClosingALoop()
    {
        // Piece 5 of N becomes a junction to a new run that ends on network M.
        Dictionary<long, long> nodeOf = new Dictionary<long, long>
        {
            [5] = NetworkN, [6] = NetworkN, [7] = NetworkN, [9] = NetworkM, [-1] = -1
        };
        HashSet<Link> before = new HashSet<Link> { new Link(5, 6), new Link(7, 5) };
        List<Link> links = new List<Link> { new Link(5, 6), new Link(5, 7), new Link(5, -1), new Link(-1, 9) };

        Assert.Empty(RunLoops.Closing(links, nodeOf, before, new long[] { 5, -1 }));
    }

    [Fact]
    public void ALoopTheNetworkAlreadyHasIsNotReported()
    {
        // Pieces of a network losing a piece are modelled one by one; 1-2-3 already form a triangle.
        Dictionary<long, long> nodeOf = new Dictionary<long, long> { [1] = 1, [2] = 2, [3] = 3, [-1] = -1, [9] = 9 };
        List<Link> links = new List<Link> { new Link(1, 2), new Link(2, 3), new Link(3, 1), new Link(3, -1) };

        Assert.Empty(RunLoops.Closing(links, nodeOf, new HashSet<Link>(), new long[] { -1 }));
    }

    [Fact]
    public void TheIncidentsSecondRouteClosesALoop()
    {
        // The data route starts at the 4-way the power route left in the data port cell (piece 3 of network N,
        // changed by nothing) and ends on the trunk (piece 23, network N).
        Dictionary<long, long> nodeOf = new Dictionary<long, long>
        {
            [3] = NetworkN, [23] = NetworkN, [-1] = -1, [-2] = -2, [-3] = -3
        };
        List<Link> links = new List<Link>
        {
            new Link(3, -1), new Link(-1, -2), new Link(-2, -3), new Link(-3, 23)
        };

        Assert.Single(RunLoops.Closing(links, nodeOf, new HashSet<Link>(), new long[] { -1, -2, -3 }));
    }
}
