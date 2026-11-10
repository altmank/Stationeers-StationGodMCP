#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class GhostLoadRuleTests
{
    [Fact]
    public void LoadsOnceOnARunningHostWithoutLattice()
    {
        Assert.True(GhostLoadRule.ShouldLoad(keeps: true, running: true, latticeLoaded: false, loaded: false));
        Assert.False(GhostLoadRule.ShouldLoad(true, true, false, loaded: true));
        Assert.False(GhostLoadRule.ShouldLoad(true, true, latticeLoaded: true, false));
        Assert.False(GhostLoadRule.ShouldLoad(true, running: false, false, false));
        Assert.False(GhostLoadRule.ShouldLoad(keeps: false, true, false, false));
    }
}
