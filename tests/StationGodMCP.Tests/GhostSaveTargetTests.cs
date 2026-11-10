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
    private const string C = @"C:\saves\C\stationgod-ghosts.json";

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
    public void ASaveIntoANewFolderThenLeavingAtOnceWritesThereNotToTheOldFolder()
    {
        Assert.Equal(new[] { B }, GhostSaveTarget.OnLeaving(A, new[] { B }, pending: true, ghosts: 1, out bool notKept));
        Assert.False(notKept);
    }

    [Fact]
    public void TwoSavesInARowBothFoldersGetTheBoard()
    {
        Assert.Equal(new[] { B, C }, GhostSaveTarget.OnLeaving(A, new[] { B, C }, pending: true, ghosts: 1, out bool notKept));
        Assert.False(notKept);
    }

    [Fact]
    public void LeavingWithAPendingWriteGoesToTheCapturedFile()
    {
        Assert.Equal(new[] { A }, GhostSaveTarget.OnLeaving(A, new string[0], pending: true, ghosts: 1, out bool notKept));
        Assert.False(notKept);
    }

    [Fact]
    public void LeavingANeverSavedWorldWritesNothingAndSaysSo()
    {
        Assert.Empty(GhostSaveTarget.OnLeaving(null, new string[0], pending: true, ghosts: 2, out bool notKept));
        Assert.True(notKept);
    }

    [Fact]
    public void LeavingWithNothingOwedWritesNothingAndSaysNothing()
    {
        Assert.Empty(GhostSaveTarget.OnLeaving(A, new string[0], pending: false, ghosts: 2, out bool notKept));
        Assert.False(notKept);
        Assert.Empty(GhostSaveTarget.OnLeaving(null, new string[0], pending: true, ghosts: 0, out notKept));
        Assert.False(notKept);
    }
}
