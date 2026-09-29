#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from the pipes live test of 1.4.4 (2026-09-29): a split long straight builds its singles from a connected
/// end, a network the game deregistered while pipes still name it fails the gas check, remove_redundant keeps what
/// joins an in-line tank, route ends beyond an in-line tank's free end, a chute start leaves only through an open
/// output end, one flow_reversed per run, and a ring's loop lists every piece.
/// </summary>
public sealed class PipesLiveTestTests
{
    private static GridCell At(int x) => CleanModels.At(x, 0, 0);

    // A 3-long straight over cells 0..2 along x, its ends one step beyond each tip facing back into it.
    private static PieceModel LongStraight() =>
        new PieceModel(70, new[] { At(0), At(1), At(2) },
            new[] { CleanModels.EndTo(At(0), At(-1)), CleanModels.EndTo(At(2), At(3)) }, null);

    [Fact]
    public void ASplitStartsAtTheOnlyConnectedTip()
    {
        PieceModel piece = LongStraight();
        List<PieceModel> singles = LongStraights.Split(piece)!;

        List<PieceModel> fromLast = LongStraights.FromConnectedEnd(singles, new[] { piece.Ends[1] });
        List<PieceModel> fromFirst = LongStraights.FromConnectedEnd(singles, new[] { piece.Ends[0] });
        List<PieceModel> neither = LongStraights.FromConnectedEnd(singles, new PieceEnd[0]);

        Assert.Equal(new[] { At(2), At(1), At(0) }, fromLast.ConvertAll(static single => single.Cells[0]));
        Assert.Equal(new[] { At(0), At(1), At(2) }, fromFirst.ConvertAll(static single => single.Cells[0]));
        Assert.Equal(new[] { At(0), At(1), At(2) }, neither.ConvertAll(static single => single.Cells[0]));
    }

    private static GasMix Gas(double mol, double joules) => new GasMix(new[] { mol }, new[] { joules });

    private static NetworkGas Net(long id, double mol, double volumeL, params long[] members) =>
        new NetworkGas(id, Gas(mol, mol * 6000), volumeL, members, new long[0]);

    // pipes-1: network 917's first long piece split into singles that stayed on 917 after the game merged 917 into
    // 6755 and deregistered it; 917 kept a copy of its 230 mol.
    [Fact]
    public void AnOrphanNetworkHoldingACopyOfTheGasFailsTheCheck()
    {
        List<NetworkGas> before = new List<NetworkGas> { Net(917, 230, 320, 1, 2, 3, 4) };
        NetworkGas orphan = Net(917, 230, 50, 11, 12, 13, 14, 15);
        List<NetworkGas> after = new List<NetworkGas> { Net(6755, 230, 270, 2, 3, 4), orphan };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);
        GasOrphans orphans = GasOrphans.Of(new List<NetworkGas>(), new List<NetworkGas> { orphan });
        GasCheckView check = GasCheckView.Of(audit, new List<GasRefill>(), new List<long>(), orphans);

        Assert.False(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.Equal(-230.0, family.MissingMol, 6);
        Assert.False(check.Ok);
        GasOrphanView view = Assert.Single(check.Orphans);
        Assert.Equal(5, view.Pipes);
        Assert.Equal(230.0, view.Mol, 6);
        Assert.Contains("orphans", check.Summary);
        Assert.Equal(GasCheckView.GasLostStatus, GasCheckView.JobStatus("applied", check));
    }

    [Fact]
    public void AnOrphanThatWasThereBeforeTheJobIsReportedButNotTheJobs()
    {
        NetworkGas old = Net(917, 230, 50, 11, 12);
        GasOrphans orphans = GasOrphans.Of(new List<NetworkGas> { old }, new List<NetworkGas> { old });
        List<NetworkGas> readings = new List<NetworkGas> { Net(10, 5, 10, 1), old };
        GasCheckView check = GasCheckView.Of(GasAudit.Of(readings, readings, GasTolerance.Default),
            new List<GasRefill>(), new List<long>(), orphans);

        Assert.True(orphans.Ok);
        Assert.True(check.Ok);
        Assert.Empty(check.Orphans);
        Assert.Single(check.OldOrphans);
    }

    // pipes-3: in-line tank 50 -- pipes 1, 2, 3 -- pump port piece 4 -- pump 900. The tank is a member of the network
    // that is no candidate; the pipes between it and the pump hold it on the network.
    [Fact]
    public void RemoveRedundantKeepsThePipesThatJoinAnInLineTank()
    {
        List<Link> links = new List<Link>
        {
            new Link(50, 1), new Link(1, 2), new Link(2, 3), new Link(3, 4), new Link(4, 900), new Link(900, 4)
        };

        RedundancyResult result = RedundantPieces.Find(new long[] { 50, 1, 2, 3, 4 }, links,
            new HashSet<long> { 900 }, new long[] { 1, 2, 3, 4 }, new HashSet<long>(),
            new Dictionary<long, string>(), new HashSet<long> { 900 });

        Assert.Empty(result.Removed);
        Assert.Equal(RedundantPieces.DevicePort, result.Kept.Find(static kept => kept.Id == 4)!.Reason);
        Assert.All(result.Kept.FindAll(static kept => kept.Id != 4),
            kept => Assert.Equal(RedundantPieces.Needed, kept.Reason));
    }

    // pipes-4: a 1x2 in-line tank's ends sit beyond it and face back in (local beyond, facing inside), as a pipe's do.
    [Fact]
    public void TheCellBeyondAnEndIsTheOneTheThingDoesNotOccupy()
    {
        PieceModel tank = new PieceModel(824, new[] { At(0), At(1) },
            new[] { CleanModels.EndTo(At(1), At(2)), CleanModels.EndTo(At(0), At(-1)) }, null);

        (GridCell inside, GridCell beyond) = EndCells.Of(tank, tank.Ends[0].Local, tank.Ends[0].Facing);
        (GridCell insideSwapped, GridCell beyondSwapped) = EndCells.Of(tank, tank.Ends[0].Facing, tank.Ends[0].Local);

        Assert.Equal(At(1), inside);
        Assert.Equal(At(2), beyond);
        Assert.Equal(At(1), insideSwapped);
        Assert.Equal(At(2), beyondSwapped);
    }

    // pipes-10: a chute start leaves only through an open end items can leave by.
    [Fact]
    public void AChuteStartLeavesOnlyThroughAnOpenOutputEnd()
    {
        PieceModel junction = ChuteModels.Junction(7071, At(0), "-x", "-z", "+x");

        EndSet leaving = RouteEnd.OpenAt(junction, At(0), new PieceEnd[0], true);
        EndSet any = RouteEnd.OpenAt(junction, At(0), new PieceEnd[0], false);
        EndSet joined = RouteEnd.OpenAt(junction, At(0), new[] { junction.Ends[2] }, true);

        Assert.Equal(RunModels.Ends("+x").Mask, leaving.Mask);
        Assert.Equal(RunModels.Ends("-x", "-z", "+x").Mask, any.Mask);
        Assert.Equal(0, joined.Count);
    }

    [Fact]
    public void ARouteFromALeavingStartNeverTurnsItIntoAJunction()
    {
        RouteRules rules = new RouteRules(2.0, AxisOrder.Any, 400, CleanModels.At(-6, -6, -6),
            CleanModels.At(6, 6, 6));
        RouteResult result = RoutePlanner.Find(RouteEnd.Leaving(At(0), RunModels.Ends("-x")),
            RouteEnd.Open(At(4)), static _ => CellCost.Of(1.0), rules);

        Assert.NotNull(result.Cells);
        Assert.Equal(At(-1), result.Cells![1]);
    }

    [Fact]
    public void AReversedRunIsOneProblemNamingEveryCell()
    {
        GridCell[] cells = { At(0), At(1), At(2), At(3) };
        List<PieceModel> pieces = new List<PieceModel>();
        long[] ids = new long[cells.Length];
        for (int index = 0; index < cells.Length; index++)
        {
            ids[index] = -1 - index;
            pieces.Add(ChuteModels.Plain(ids[index], cells[index], "-x", "+x"));
        }

        ChuteFlowResult flow = ChuteModels.Solve(pieces,
            new[]
            {
                ChuteModels.Port(100, 0, At(0), "-x", ChuteRoles.Input),
                ChuteModels.Port(200, 1, At(3), "+x", ChuteRoles.Output)
            }, ids, cells);

        FlowConflict conflict = Assert.Single(flow.Conflicts);
        Assert.Equal(FlowConflict.Reversed, conflict.Code);
        Assert.Contains(At(2).ToString(), conflict.Message);
    }

    // A ring of four corners: one piece breaks it, and the loop lists all four.
    [Fact]
    public void ARingsLoopListsEveryPieceOfTheRing()
    {
        GridCell a = CleanModels.At(0, 0, 0);
        GridCell b = CleanModels.At(1, 0, 0);
        GridCell c = CleanModels.At(1, 0, 1);
        GridCell d = CleanModels.At(0, 0, 1);
        List<PieceModel> ring = new List<PieceModel>
        {
            CleanModels.Piece(1, a, b, d), CleanModels.Piece(2, b, a, c), CleanModels.Piece(3, c, b, d),
            CleanModels.Piece(4, d, c, a)
        };

        LoopResult result = LoopCutting.Find(ring, new PieceModel[0], new HashSet<long>(),
            new Dictionary<long, string>(), new HashSet<long>());

        NetworkLoop loop = Assert.Single(result.Loops);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, loop.Pieces);
        Assert.Single(loop.Cut);
    }
}
