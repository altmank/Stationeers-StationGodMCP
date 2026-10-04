#nullable enable

using StationGodMCP.Pure.Rockets;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>rocket_mining_options with collectable_only and nothing kept says why.</summary>
public sealed class MiningEmptyReasonTests
{
    [Fact]
    public void NoSiteNoMachineOrNothingCollectableEachSaySo()
    {
        Assert.Contains("no charted node has a deposit", MiningEmptyReason.Of(0, 2, new string[0], 0, new string[0], null));
        Assert.Contains("Thin Ice has no deposit", MiningEmptyReason.Of(0, 2, new string[0], 0, new string[0], "Thin Ice"));
        Assert.Contains("no miner or gas collector", MiningEmptyReason.Of(3, 0, new[] { "ore", "ice", "gas" }, 0, new string[0], null));

        string judged = MiningEmptyReason.Of(3, 1, new[] { "ore", "ore", "gas" }, 1,
            new[] { "An ice head at an ore site gets nothing.", "An ice head at an ore site gets nothing." }, null);
        Assert.Equal("No site listed: none of the 3 sites (1 gas, 2 ore; 1 depleted) gives anything the loadout collects. " +
                     "An ice head at an ore site gets nothing.", judged);
    }
}
