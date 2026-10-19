#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>remove pipe allow contents: a split of a network holding gas becomes a warning; emptying one stays refused.</summary>
public sealed class RemovalContentsTests
{
    private static readonly LayoutIssue Split = new LayoutIssue(ContentsGuard.ContentsWouldMove, "Network 9 holds 3 mol");
    private static readonly LayoutIssue Emptied = new LayoutIssue("holds_contents", "Removing every pipe of network 9");

    [Fact]
    public void AllowContentsMakesASplitOfAHoldingNetworkAWarning()
    {
        EditAllowance allow = new EditAllowance(new HashSet<long>(), split: false, contents: true);

        Assert.True(ContentsGuard.Warns(Split, allow));
    }

    [Fact]
    public void WithoutItTheSplitIsRefused()
    {
        Assert.False(ContentsGuard.Warns(Split, EditAllowance.Nothing));
    }

    [Fact]
    public void EmptyingANetworkStaysRefused()
    {
        // The game deletes a network's contents with its last pipe; allow contents splits, it never deletes gas.
        Assert.False(ContentsGuard.Warns(Emptied, new EditAllowance(new HashSet<long>(), false, contents: true)));
    }
}
