#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A removal takes a piece out of its network first only where the piece's OnDestroy then leaves its neighbours
/// alone. Neighbouring chutes of a network kept whole, each taken out first, make Chute.OnDestroy throw on a
/// neighbour with no network; the piece is then never deregistered, and a live chute beside it keeps a link to it.
/// </summary>
public sealed class NetworkLeaveRuleTests
{
    [Fact]
    public void AChuteIsNeverTakenOutOfItsNetworkFirst() =>
        Assert.False(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.Always, networkKeptWhole: true));

    [Fact]
    public void ACableOrPipeOfANetworkKeptWholeLeavesItFirst() =>
        // Its network keeps its id, and a pipe network every mole of what is left.
        Assert.True(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.WhenItHadANetwork, networkKeptWhole: true));

    [Fact]
    public void ACableOrPipeOfANetworkThatSplitsStaysInItForTheGameToRebuild() =>
        Assert.False(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.WhenItHadANetwork, networkKeptWhole: false));

    [Fact]
    public void AChuteOfANetworkThatSplitsStaysInItForTheGameToRebuild() =>
        Assert.False(NetworkLeaveRule.LeavesFirst(NeighbourRebuild.Always, networkKeptWhole: false));

    [Fact]
    public void AChuteACleanRunRemovesStaysInItsNetwork() =>
        // Neighbouring dead chutes removed in one frame each find the other in a network.
        Assert.False(NetworkLeaveRule.SwapLeavesFirst(NeighbourRebuild.Always, hasReplacements: false));

    [Fact]
    public void AChuteACleanRunReplacesHandsItsDevicesOverFirst() =>
        Assert.True(NetworkLeaveRule.SwapLeavesFirst(NeighbourRebuild.Always, hasReplacements: true));

    [Fact]
    public void ACableOrPipeACleanRunRemovesLeavesItsNetworkFirst() =>
        Assert.True(NetworkLeaveRule.SwapLeavesFirst(NeighbourRebuild.WhenItHadANetwork, hasReplacements: false));
}
