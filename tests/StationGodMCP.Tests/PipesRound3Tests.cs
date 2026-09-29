#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from round 3 of the 1.4.4 pipes live test (2026-09-29). pipes-21: a waypoints run ending on cell 0 of a lone
/// gas-filled Straight3 (network 1193, 30 mol CO2, long 1192) built its first piece far from the long, which stood
/// alone on a new network (1223); the last piece merged 1193 into 1223. The gas check linked networks by pipe id
/// only, so 1193 (whose only pipe, the long, was swapped for singles with new ids) counted as emptied by removal and
/// 1223 as 30 mol from nowhere: gas_lost, although every mole was there. Families now link by the cells pipes fill,
/// and the run grows outward from the long's singles.
/// </summary>
public sealed class PipesRound3Tests
{
    private static GridCell AtZ(int z) => CleanModels.At(0, 0, z);

    private static GasMix Co2(double mol) => new GasMix(new[] { mol }, new[] { mol * 5800 });

    private static NetworkGas Net(long id, double mol, IReadOnlyList<long> members, IReadOnlyList<GridCell> cells) =>
        new NetworkGas(id, Co2(mol), members.Count * 10.0, members, new long[0], cells);

    // Before: the lone Straight3 1192 over z 0..2, the only pipe of network 1193.
    private static List<NetworkGas> LoneLong() => new List<NetworkGas>
    {
        Net(1193, 30, new long[] { 1192 }, new[] { AtZ(0), AtZ(1), AtZ(2) })
    };

    // After: the tee 1217 and singles 1220, 1221 over the long's cells, the run's pieces 1222, 1225, 1226 beyond,
    // all on the new network 1223 the long's network was merged into.
    private static NetworkGas Merged(double mol) =>
        Net(1223, mol, new long[] { 1217, 1220, 1221, 1222, 1225, 1226 },
            new[] { AtZ(0), AtZ(1), AtZ(2), AtZ(-1), AtZ(-2), AtZ(-3) });

    [Fact]
    public void ALongSwappedForSinglesAndMergedIntoANewNetworkKeepsItsFamily()
    {
        GasAudit audit = GasAudit.Of(LoneLong(), new List<NetworkGas> { Merged(30) }, GasTolerance.Default);

        Assert.True(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.Equal(new long[] { 1193 }, family.Before.ConvertAll(static network => network.Id));
        Assert.Equal(new long[] { 1223 }, family.After.ConvertAll(static network => network.Id));
        Assert.False(family.Emptied);
        Assert.True(family.Conserved);
    }

    [Fact]
    public void GasThatVanishesInThatMergeIsCaughtAndPutBack()
    {
        // The same job, but the merge lost the long's 30 mol: before the fix this passed as "emptied" (ok) and the
        // loss was hidden.
        GasAudit audit = GasAudit.Of(LoneLong(), new List<NetworkGas> { Merged(0) }, GasTolerance.Default);
        GasCheckView check = GasCheckView.Of(audit, new List<GasRefill>(), new List<long>(),
            GasOrphans.Of(new List<NetworkGas>(), new List<NetworkGas>()));

        Assert.False(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.False(family.Emptied);
        Assert.Equal(30.0, family.MissingMol, 6);
        GasRefill refill = Assert.Single(GasRefills.For(audit, GasTolerance.Default));
        Assert.Equal(1223, refill.Into);
        Assert.Equal(30.0, refill.Gas.TotalMol, 6);
        Assert.Equal(GasCheckView.GasLostStatus, GasCheckView.JobStatus("applied", check));
    }

    [Fact]
    public void GasThatVanishesWhenTheMergeGoesTheOtherWayIsCaughtToo()
    {
        // The long's network survives and the run's network merges into it, the gas lost on the way.
        List<NetworkGas> after = new List<NetworkGas>
        {
            new NetworkGas(1193, Co2(0), 60, new long[] { 1217, 1220, 1221, 1222, 1225, 1226 }, new long[0],
                new[] { AtZ(0), AtZ(1), AtZ(2), AtZ(-1), AtZ(-2), AtZ(-3) })
        };

        GasAudit audit = GasAudit.Of(LoneLong(), after, GasTolerance.Default);

        Assert.False(audit.Ok);
        Assert.Equal(30.0, Assert.Single(audit.Families).MissingMol, 6);
    }

    [Fact]
    public void ARemovalWithNothingInItsCellsIsStillEmptied()
    {
        // remove_pipes with allow_contents takes a lone pipe away; an unrelated new network elsewhere is its own family.
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(2000, 0, new long[] { 1999 }, new[] { AtZ(10) })
        };

        GasAudit audit = GasAudit.Of(LoneLong(), after, GasTolerance.Default);

        Assert.True(audit.Ok);
        Assert.Equal(2, audit.Families.Count);
        Assert.True(audit.Families[0].Emptied);
        Assert.Equal(new long[] { 1193 }, audit.Families[0].Before.ConvertAll(static network => network.Id));
        Assert.True(audit.Families[1].Conserved);
    }

    [Fact]
    public void TheRunGrowsOutwardFromTheSwappedSingles()
    {
        // The long's singles over z 0..2; the run's pieces planned from its first waypoint at z -3 toward the long.
        List<PieceModel> singles = new List<PieceModel>
        {
            CleanModels.Piece(1, AtZ(0), AtZ(-1), AtZ(1)),
            CleanModels.Piece(2, AtZ(1), AtZ(0), AtZ(2)),
            CleanModels.Piece(3, AtZ(2), AtZ(1), AtZ(3))
        };
        List<PieceModel> run = new List<PieceModel>
        {
            CleanModels.Piece(-1, AtZ(-3), AtZ(-4), AtZ(-2)),
            CleanModels.Piece(-2, AtZ(-2), AtZ(-3), AtZ(-1)),
            CleanModels.Piece(-3, AtZ(-1), AtZ(-2), AtZ(0))
        };

        List<PieceModel> ordered = GrowthOrder.From(singles, run, static piece => piece);

        Assert.Equal(new long[] { -3, -2, -1 }, ordered.ConvertAll(static piece => piece.Id));
    }

    [Fact]
    public void WithNothingStandingTheRunKeepsItsOrder()
    {
        List<PieceModel> run = new List<PieceModel>
        {
            CleanModels.Piece(-1, AtZ(-3), AtZ(-4), AtZ(-2)),
            CleanModels.Piece(-2, AtZ(-2), AtZ(-3), AtZ(-1))
        };

        List<PieceModel> ordered = GrowthOrder.From(new List<PieceModel>(), run, static piece => piece);

        Assert.Equal(new long[] { -1, -2 }, ordered.ConvertAll(static piece => piece.Id));
    }

    [Fact]
    public void APieceTouchingNothingWaitsAndADetachedChainStillGetsBuilt()
    {
        List<PieceModel> singles = new List<PieceModel> { CleanModels.Piece(1, AtZ(0), AtZ(-1), AtZ(1)) };
        List<PieceModel> run = new List<PieceModel>
        {
            CleanModels.Piece(-9, AtZ(20), AtZ(19), AtZ(21)),
            CleanModels.Piece(-1, AtZ(-1), AtZ(-2), AtZ(0))
        };

        List<PieceModel> ordered = GrowthOrder.From(singles, run, static piece => piece);

        Assert.Equal(new long[] { -1, -9 }, ordered.ConvertAll(static piece => piece.Id));
    }
}
