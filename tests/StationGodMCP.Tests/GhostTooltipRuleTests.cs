#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The ghost tooltip is off by default (LU, 2026-11-01: "Disabled by default for now"); [Ghosts] ShowTooltip turns it
/// on, and it still hides while the game's UI is hidden (Loom planning, the hide-UI key).
/// </summary>
public sealed class GhostTooltipRuleTests
{
    [Fact]
    public void TheTooltipIsOffByDefault()
    {
        Assert.False(GhostTooltipRule.ShownByDefault);
        Assert.False(GhostTooltipRule.Shows(GhostTooltipRule.ShownByDefault, gameUiShown: true, ghostUnderCrosshair: true));
    }

    [Fact]
    public void TheSettingTurnsItOnForAGhostUnderTheCrosshair()
    {
        Assert.True(GhostTooltipRule.Shows(setting: true, gameUiShown: true, ghostUnderCrosshair: true));
    }

    [Fact]
    public void TurnedOnItStillHidesWithTheGameUi()
    {
        Assert.False(GhostTooltipRule.Shows(setting: true, gameUiShown: false, ghostUnderCrosshair: true));
    }

    [Fact]
    public void NoGhostUnderTheCrosshairShowsNothing()
    {
        Assert.False(GhostTooltipRule.Shows(setting: true, gameUiShown: true, ghostUnderCrosshair: false));
    }
}
