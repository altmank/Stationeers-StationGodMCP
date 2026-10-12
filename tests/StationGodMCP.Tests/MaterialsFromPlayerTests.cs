#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// materials_from_player: a build a player pays for (from_id left out, so the player's inventory, or from_id naming a
/// player) warns; a build paid from storage, a free build and one that costs nothing do not. place_structure,
/// place_cables, place_pipes, place_chutes, replace_walls, replace_frames, upgrade_cables, upgrade_pipes and
/// advance_build_state ask PaySources.PlayerPays, which finds the first player among the payers and asks this rule.
/// </summary>
public sealed class MaterialsFromPlayerTests
{
    [Fact]
    public void ABuildWithNoFromIdWarnsMaterialsFromPlayer()
    {
        GuardFinding? finding = PayerRule.FromPlayer(pays: true, "Engineer", 4021, named: false);

        Assert.NotNull(finding);
        Assert.Equal("materials_from_player", finding!.Code);
        Assert.Equal(GuardLevel.Warning, finding.Level);
        Assert.Contains("Engineer's inventory (4021)", finding.Message);
        Assert.Contains("from_id was left out", finding.Message);
    }

    [Fact]
    public void AFromIdNamingAPlayerStillWarns()
    {
        GuardFinding? finding = PayerRule.FromPlayer(pays: true, "Engineer", 4021, named: true);

        Assert.Equal("materials_from_player", finding!.Code);
        Assert.Contains("whom from_id names", finding.Message);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "Engineer")]
    public void StorageFreeAndCostlessBuildsDoNotWarn(bool pays, string? player)
    {
        Assert.Null(PayerRule.FromPlayer(pays, player, 4021, named: false));
    }
}
