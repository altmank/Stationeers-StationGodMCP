#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The ghost board file follows the save folder as it is at write time: a new world's first save and a save under a new
/// name both get the board, and a write with no folder stays pending instead of being dropped.
/// </summary>
public sealed class GhostSaveTargetTests
{
    private const string A = @"C:\saves\A\stationgod-ghosts.json";
    private const string B = @"C:\saves\B\stationgod-ghosts.json";

    [Fact]
    public void ANewWorldsFirstSaveGetsTheFile()
    {
        Assert.Equal(A, GhostSaveTarget.Of(captured: null, current: A));
        Assert.True(GhostSaveTarget.Moved(captured: null, current: A));
    }

    [Fact]
    public void ASaveUnderANewNameMovesTheFileToTheNewFolder()
    {
        Assert.Equal(B, GhostSaveTarget.Of(captured: A, current: B));
        Assert.True(GhostSaveTarget.Moved(captured: A, current: B));
    }

    [Fact]
    public void TheSameFolderIsNotAMove()
    {
        Assert.Equal(A, GhostSaveTarget.Of(captured: A, current: A));
        Assert.False(GhostSaveTarget.Moved(captured: A, current: A));
        Assert.False(GhostSaveTarget.Moved(captured: A, current: A.ToUpperInvariant()));
    }

    [Fact]
    public void NoFolderLeavesTheWritePending()
    {
        Assert.Null(GhostSaveTarget.Of(captured: null, current: null));
        Assert.False(GhostSaveTarget.Moved(captured: null, current: null));
    }

    [Fact]
    public void AFolderThatWentAwayKeepsTheCapturedFile()
    {
        Assert.Equal(A, GhostSaveTarget.Of(captured: A, current: null));
        Assert.False(GhostSaveTarget.Moved(captured: A, current: null));
    }

    [Fact]
    public void TheLastWriteAfterTheWorldWasLeftGoesToTheCapturedFile()
    {
        Assert.Equal(A, GhostSaveTarget.Of(captured: A, current: B, worldLeft: true));
        Assert.Null(GhostSaveTarget.Of(captured: null, current: B, worldLeft: true));
    }
}
