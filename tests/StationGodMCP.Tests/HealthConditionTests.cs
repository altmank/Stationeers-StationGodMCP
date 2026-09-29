#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The one-word condition and the scan's broken rule: the game's broken state wins over every number.</summary>
public sealed class HealthConditionTests
{
    [Fact]
    public void ABrokenStructureHealedTo100PercentIsBroken()
    {
        // The live case: fire-burnt Active Vents, 0 damage, 100 % health, build state below 0.
        Assert.Equal("broken", HealthCondition.Of(true, true, false, 0.0));
        Assert.Equal("broken", HealthCondition.Of(true, false, false, null));
    }

    [Theory]
    [InlineData(false, false, null, "none")]
    [InlineData(true, true, null, "indestructible")]
    [InlineData(true, false, 0.0, "intact")]
    [InlineData(true, false, null, "intact")]
    [InlineData(true, false, 0.3, "damaged")]
    [InlineData(true, false, 0.9999, "damaged")]
    public void NotBrokenReadsTheNumbers(bool hasDamageState, bool indestructible, double? ratio, string expected)
    {
        Assert.Equal(expected, HealthCondition.Of(false, hasDamageState, indestructible, ratio));
    }

    [Fact]
    public void TheScanListsBrokenThingsWhateverTheirNumbers()
    {
        Assert.True(HealthCondition.ScanKeeps(true, true, 0.0, 0.0, false));
        Assert.True(HealthCondition.ScanKeeps(true, true, 0.0, 0.75, false));
        Assert.True(HealthCondition.ScanKeeps(true, false, 0.0, 0.0, true));
    }

    [Fact]
    public void TheScanKeepsDamagedThingsAboveTheFloorUnlessBrokenOnly()
    {
        Assert.True(HealthCondition.ScanKeeps(false, true, 0.3, 0.25, false));
        Assert.False(HealthCondition.ScanKeeps(false, true, 0.2, 0.25, false));
        Assert.False(HealthCondition.ScanKeeps(false, true, 0.0, 0.0, false));
        Assert.False(HealthCondition.ScanKeeps(false, false, 0.5, 0.0, false));
        Assert.False(HealthCondition.ScanKeeps(false, true, 0.9, 0.0, true));
    }

    [Fact]
    public void BrokenRanksAsFullyDamaged()
    {
        Assert.Equal(1.0, HealthCondition.RankRatio(true, 0.0));
        Assert.Equal(0.4, HealthCondition.RankRatio(false, 0.4));
    }
}
