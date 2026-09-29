#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from round 4 of the 1.4.4 pipes live test (2026-09-29). pipes-23: changing the only pipe of a gas network
/// (straight to tee) spawned the new piece with a network of its own, and the old piece left its network as its last
/// pipe, deleting the gas (only the gas check's refill put it back, under a new id); the build now merges the new
/// piece's network into the old one (RunBuilder.Change, live check needed) and grows the run's new pieces outward from
/// the changed ones. pipes-24: a chute start never leaves through an open end the line's flow sends items in by.
/// pipes-25: remove_structure refuses a tank removal that splits a network holding gas, as remove_pipes does.
/// </summary>
public sealed class PipesRound4Tests
{
    private static GridCell AtZ(int z) => CleanModels.At(0, 0, z);

    [Fact]
    public void TheRunGrowsOutwardFromAChangedPiece()
    {
        // The lone single at z 0 changed into a tee; the run planned from its first waypoint at z -3 toward it.
        List<PieceModel> changed = new List<PieceModel> { CleanModels.Piece(2658, AtZ(0), AtZ(-1), AtZ(1)) };
        List<PieceModel> run = new List<PieceModel>
        {
            CleanModels.Piece(-1, AtZ(-3), AtZ(-4), AtZ(-2)),
            CleanModels.Piece(-2, AtZ(-2), AtZ(-3), AtZ(-1)),
            CleanModels.Piece(-3, AtZ(-1), AtZ(-2), AtZ(0))
        };

        List<PieceModel> ordered = GrowthOrder.From(changed, run, static piece => piece);

        Assert.Equal(new long[] { -3, -2, -1 }, ordered.ConvertAll(static piece => piece.Id));
    }

    [Fact]
    public void AChuteStartNeverLeavesThroughAnOpenEndItemsEnterBy()
    {
        // A side straight feeding a junction's second input from +z: its open +z end is where items enter the line.
        GridCell main = RunModels.At(1, 0, 0);
        GridCell side = RunModels.At(1, 0, 1);
        PieceModel junction = ChuteModels.Junction(1806, main, "-x", "+z", "+x");
        PieceModel straight = ChuteModels.Plain(1809, side, "-z", "+z");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { junction, straight }, new DevicePort[0]);
        PieceEnd joined = straight.Ends[0].Local.Equals(main) ? straight.Ends[0] : straight.Ends[1];

        EndSet withFlow = RouteEnd.OpenAt(straight, side, new[] { joined }, true,
            index => flow.Of(straight.Id, index));
        EndSet withoutFlow = RouteEnd.OpenAt(straight, side, new[] { joined }, true);

        Assert.Equal(FlowDirection.In, ChuteModels.At(flow, straight, "+z"));
        Assert.Equal(0, withFlow.Count);
        Assert.Equal(RunModels.Ends("+z").Mask, withoutFlow.Mask);
    }

    [Fact]
    public void AChuteStartLeavesThroughAnOpenEndItemsLeaveBy()
    {
        // A straight fed by a junction's output: its open far end lets items out.
        GridCell main = RunModels.At(1, 0, 0);
        GridCell next = RunModels.At(2, 0, 0);
        PieceModel junction = ChuteModels.Junction(1806, main, "-x", "+z", "+x");
        PieceModel straight = ChuteModels.Plain(1810, next, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { junction, straight }, new DevicePort[0]);
        PieceEnd joined = straight.Ends[0].Local.Equals(main) ? straight.Ends[0] : straight.Ends[1];

        EndSet open = RouteEnd.OpenAt(straight, next, new[] { joined }, true, index => flow.Of(straight.Id, index));

        Assert.Equal(RunModels.Ends("+x").Mask, open.Mask);
    }

    [Fact]
    public void ATankRemovalSplittingANetworkWithGasIsRefusedUnlessContentsAreAllowed()
    {
        double[] shares = { 9.64, 35.36 };

        GuardFinding? refused = RemovalRule.Divided(2189, 45, shares, new RemovalAllowance(false, false));
        GuardFinding? allowed = RemovalRule.Divided(2189, 45, shares, new RemovalAllowance(true, false));

        Assert.NotNull(refused);
        Assert.Equal("contents_would_move", refused!.Code);
        Assert.Equal(GuardLevel.Refusal, refused.Level);
        Assert.Contains("9.64 + 35.36 mol", refused.Message);
        Assert.Contains("allow_contents", refused.Message);
        Assert.NotNull(allowed);
        Assert.Equal(GuardLevel.Warning, allowed!.Level);
    }

    [Fact]
    public void ATankRemovalThatSplitsNothingOrAnEmptyNetworkMovesNothing()
    {
        RemovalAllowance none = new RemovalAllowance(false, false);

        Assert.Null(RemovalRule.Divided(2189, 45, new[] { 45.0 }, none));
        Assert.Null(RemovalRule.Divided(2189, 0, new[] { 0.0, 0.0 }, none));
    }
}
