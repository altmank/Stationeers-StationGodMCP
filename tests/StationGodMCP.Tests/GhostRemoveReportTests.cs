#nullable enable

using System.Collections.Generic;
using System.Linq;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The notes a ghost removal gives: refusals reach the reply, "named no ghost" only for absent ids.</summary>
public sealed class GhostRemoveReportTests
{
    private static List<string> Run(long[] ids, long[] board, GhostRemoveReport.Remover remove, out List<long> gone)
    {
        List<string> notes = new List<string>();
        gone = GhostRemoveReport.Run(ids, board.Contains, remove, notes);
        return notes;
    }

    [Fact]
    public void ARefusalCarriesLatticesReasonAndNoNamedNoGhost()
    {
        List<string> notes = Run(new long[] { 1, 2 }, new long[] { 1, 2 },
            (ids, refused) => { refused.Add("Lattice refused it: locked"); return new List<long>(); }, out List<long> gone);
        Assert.Empty(gone);
        Assert.Equal(new[] { "Lattice refused it: locked" }, notes);
    }

    [Fact]
    public void AnAbsentIdIsNamedNoGhost()
    {
        List<string> notes = Run(new long[] { 1, 9 }, new long[] { 1 }, (ids, refused) => ids.ToList(), out List<long> gone);
        Assert.Equal(new long[] { 1 }, gone);
        Assert.Equal(new[] { "1 of the ids named no ghost on the board." }, notes);
    }

    [Fact]
    public void AllRemovedGivesNoNote()
    {
        List<string> notes = Run(new long[] { 1, 2 }, new long[] { 1, 2 }, (ids, refused) => ids.ToList(), out _);
        Assert.Empty(notes);
    }

    [Fact]
    public void ASentButUnansweredRemovalIsNotNamedNoGhost()
    {
        List<string> notes = Run(new long[] { 1, 2 }, new long[] { 1, 2 }, (ids, refused) => new List<long>(), out _);
        Assert.Single(notes);
        Assert.Contains("not yet answered", notes[0]);
        Assert.DoesNotContain("named no ghost", notes[0]);
    }

    [Fact]
    public void AClearWithARefusalShowsTheReason()
    {
        List<string> notes = Run(new long[] { 5, 6, 7 }, new long[] { 5, 6, 7 },
            (ids, refused) => { refused.Add("Lattice refused it: not yours"); return new List<long> { 5 }; }, out List<long> gone);
        Assert.Equal(new long[] { 5 }, gone);
        Assert.Equal(new[] { "Lattice refused it: not yours" }, notes);
    }

    [Fact]
    public void TwoStepsWithARefusalAndAnUnansweredRemovalEachKeepTheirNote()
    {
        List<string> notes = new List<string>();
        notes.AddRange(GhostRemoveReport.Notes(0, 2, 0, new[] { "Lattice refused it: locked" }));
        notes.AddRange(GhostRemoveReport.Notes(0, 3, 0, new string[0]));
        Assert.Equal(new[] { "Lattice refused it: locked", "3 ghost removals were sent to Lattice and are not yet answered (or their ids are unknown)." },
            notes);
    }

    [Fact]
    public void OneUnansweredRemovalReadsSingular()
    {
        List<string> notes = Run(new long[] { 1 }, new long[] { 1 }, (ids, refused) => new List<long>(), out _);
        Assert.Equal(new[] { "1 ghost removal was sent to Lattice and is not yet answered (or its id is unknown)." }, notes);
    }

    [Fact]
    public void OnTheHostAnIdLatticeNeitherRemovedNorRefusedNamedNoGhost()
    {
        List<string> notes = new List<string>();
        GhostRemoveReport.Run(new long[] { 1, 99 }, _ => true, (ids, refused) => new List<long> { 1 }, notes, immediate: true);
        Assert.Equal(new[] { "1 of the ids named no ghost on the board." }, notes);
    }

    [Fact]
    public void ARepeatedIdCountsOnce()
    {
        List<string> notes = Run(new long[] { 9, 9 }, new long[0], (ids, refused) => ids.ToList(), out _);
        Assert.Equal(new[] { "1 of the ids named no ghost on the board." }, notes);
    }
}
