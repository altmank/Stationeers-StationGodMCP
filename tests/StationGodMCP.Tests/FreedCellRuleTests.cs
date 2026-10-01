#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// remove_structure gives a filled cell back as the last hand deconstruction step does: only a structure that fills
/// its cell and still blocks air there holds a cell without an atmosphere, so only its removal releases the cell.
/// </summary>
public sealed class FreedCellRuleTests
{
    [Fact]
    public void AFinishedFrameReleasesItsCell() =>
        Assert.True(FreedCellRule.Releases(fillsCell: true, blocksAir: true));

    [Fact]
    public void AFrameThatLetsAirPassHasNothingToRelease() =>
        // An unfinished frame, or a broken one (build state below 0): its cell already holds air of its own.
        Assert.False(FreedCellRule.Releases(fillsCell: true, blocksAir: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStructureThatDoesNotFillItsCellIsLeftToTheGame(bool blocksAir) =>
        // A wall on a face, a device or a pipe: the game's own OnDeregistered handles its cells the same either way.
        Assert.False(FreedCellRule.Releases(fillsCell: false, blocksAir: blocksAir));
}
