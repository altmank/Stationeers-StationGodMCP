#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Chute pieces and ports in the game's cell units, with the chute NetworkType (8) and ConnectionRoles.</summary>
internal static class ChuteModels
{
    internal const int Chute = 8;

    /// <summary>A straight or corner: ends with no role towards the named directions.</summary>
    internal static PieceModel Plain(long id, GridCell cell, params string[] ends) =>
        EndSet.ModelAt(id, cell, RunModels.Ends(ends), Chute, ChuteRoles.None, null);

    /// <summary>A junction: inputs towards the first two directions, its output towards the third.</summary>
    internal static PieceModel Junction(long id, GridCell cell, string input, string input2, string output) =>
        new PieceModel(id, new[] { cell }, new[]
        {
            End(cell, input, ChuteRoles.Input), End(cell, input2, ChuteRoles.Input2),
            End(cell, output, ChuteRoles.Output)
        }, null);

    /// <summary>A device port joined by a chute standing in `local`, the device being towards `toward`.</summary>
    internal static DevicePort Port(long device, int index, GridCell local, string toward, int role) =>
        new DevicePort(device, index, new PieceEnd(local, RunModels.Step(toward).From(local), Chute, role), false);

    internal static ChuteFlowResult Solve(IReadOnlyList<PieceModel> pieces, IReadOnlyList<DevicePort> ports,
        long[]? edited = null, IReadOnlyList<GridCell>? run = null) =>
        ChuteFlow.Solve(new ChuteFlowGraph(pieces, ports, edited ?? new long[0],
            run != null ? RunLeg.Of(run) : new List<RunLeg>()));

    /// <summary>The direction of the piece's end towards the step.</summary>
    internal static FlowDirection At(ChuteFlowResult flow, PieceModel piece, string toward)
    {
        GridCell cell = piece.Cells[0];
        GridCell next = RunModels.Step(toward).From(cell);
        for (int index = 0; index < piece.Ends.Count; index++)
        {
            if (piece.Ends[index].Local.Equals(next))
            {
                return flow.Of(piece.Id, index);
            }
        }

        Assert.Fail($"piece {piece.Id} has no end towards {toward}");
        return FlowDirection.Unknown;
    }

    private static PieceEnd End(GridCell cell, string toward, int role) =>
        new PieceEnd(RunModels.Step(toward).From(cell), cell, Chute, role);
}

public sealed class ChuteFlowTests
{
    private static readonly GridCell A = RunModels.At(0, 0, 0);
    private static readonly GridCell B = RunModels.At(1, 0, 0);
    private static readonly GridCell C = RunModels.At(2, 0, 0);
    private static readonly GridCell D = RunModels.At(3, 0, 0);

    [Fact]
    public void ALineFromAnOutputPortToAnInputPortCarriesItemsThatWay()
    {
        // A device's Output port feeds A from -x; another device's Input port takes from C towards +x.
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        PieceModel c = ChuteModels.Plain(-3, C, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b, c },
            new[]
            {
                ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output),
                ChuteModels.Port(200, 0, C, "+x", ChuteRoles.Input)
            }, new long[] { -1, -2, -3 }, new[] { A, B, C });

        Assert.Empty(flow.Conflicts);
        Assert.Empty(flow.Outlets);
        Assert.Equal(FlowDirection.In, ChuteModels.At(flow, a, "-x"));
        Assert.Equal(FlowDirection.Out, ChuteModels.At(flow, b, "+x"));
        Assert.Equal(FlowDirection.Out, ChuteModels.At(flow, c, "+x"));
    }

    [Fact]
    public void ARunLaidFromTheSinkToTheSourceIsReversed()
    {
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b },
            new[]
            {
                ChuteModels.Port(100, 0, A, "-x", ChuteRoles.Input),
                ChuteModels.Port(200, 1, B, "+x", ChuteRoles.Output)
            }, new long[] { -1, -2 }, new[] { A, B });

        FlowConflict conflict = Assert.Single(flow.Conflicts);
        Assert.Equal(FlowConflict.Reversed, conflict.Code);
    }

    [Fact]
    public void TwoSourcesPushingIntoOneLineMeetHeadOn()
    {
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b },
            new[]
            {
                ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output),
                ChuteModels.Port(200, 1, B, "+x", ChuteRoles.Output)
            }, new long[] { -1, -2 });

        Assert.Equal(FlowConflict.Conflict, Assert.Single(flow.Conflicts).Code);
    }

    [Fact]
    public void AJunctionMergesASideLineIntoTheMainLine()
    {
        // Main line A -> B (junction) -> C -> sink; a side source feeds the junction from +z.
        PieceModel a = ChuteModels.Plain(1, A, "-x", "+x");
        PieceModel junction = ChuteModels.Junction(-1, B, "-x", "+z", "+x");
        PieceModel c = ChuteModels.Plain(3, C, "-x", "+x");
        GridCell side = RunModels.At(1, 0, 1);
        PieceModel s = ChuteModels.Plain(-2, side, "-z", "+z");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, junction, c, s },
            new[]
            {
                ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output),
                ChuteModels.Port(200, 0, C, "+x", ChuteRoles.Input),
                ChuteModels.Port(300, 3, side, "+z", ChuteRoles.Output)
            }, new long[] { -1, -2 }, new[] { side, B });

        Assert.Empty(flow.Conflicts);
        Assert.Equal(FlowDirection.In, ChuteModels.At(flow, junction, "+z"));
        Assert.Equal(FlowDirection.Out, ChuteModels.At(flow, junction, "+x"));
    }

    [Fact]
    public void AJunctionFacingUpstreamTakesItemsThroughItsOutput()
    {
        PieceModel a = ChuteModels.Plain(1, A, "-x", "+x");
        PieceModel junction = ChuteModels.Junction(-1, B, "+x", "+z", "-x");
        PieceModel c = ChuteModels.Plain(3, C, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, junction, c },
            new[]
            {
                ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output),
                ChuteModels.Port(200, 0, C, "+x", ChuteRoles.Input)
            }, new long[] { -1 });

        Assert.NotEmpty(flow.Conflicts);
        Assert.All(flow.Conflicts, conflict => Assert.Equal(FlowConflict.Conflict, conflict.Code));
    }

    [Fact]
    public void AnOpenEndThatLetsItemsOutIsAnOutlet()
    {
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b },
            new[] { ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output) }, new long[] { -1, -2 }, new[] { A, B });

        PieceEndRef outlet = Assert.Single(flow.Outlets);
        Assert.Equal(-2, outlet.Piece);
        Assert.Equal(FlowDirection.Out, ChuteModels.At(flow, b, "+x"));
    }

    [Fact]
    public void AContradictionTheNetworkAlreadyHadIsNotBlamedOnTheEdit()
    {
        // Two sources already push into the existing line A-B; the edit adds a piece far away on another line.
        PieceModel a = ChuteModels.Plain(1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(2, B, "-x", "+x");
        PieceModel d = ChuteModels.Plain(-1, D, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b, d },
            new[]
            {
                ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output),
                ChuteModels.Port(200, 1, B, "+x", ChuteRoles.Output)
            }, new long[] { -1 });

        Assert.Empty(flow.Conflicts);
    }

    [Fact]
    public void ALineNothingFeedsHasNoDirection()
    {
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+z");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b }, new DevicePort[0], new long[] { -1, -2 });

        Assert.Empty(flow.Conflicts);
        Assert.False(flow.IsDirected(a));
        Assert.False(flow.IsDirected(b));
        Assert.Empty(flow.Outlets);
    }

    [Fact]
    public void ADirectionAlongALongLineComesFromItsFarEnd()
    {
        // The sink at the far end of an existing corner line fixes the new piece's direction at the near end.
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(2, B, "-x", "+z");
        GridCell up = RunModels.At(1, 0, 1);
        PieceModel c = ChuteModels.Plain(3, up, "-z", "+z");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b, c },
            new[] { ChuteModels.Port(200, 0, up, "+z", ChuteRoles.Input) }, new long[] { -1 });

        Assert.Equal(FlowDirection.In, ChuteModels.At(flow, a, "-x"));
        Assert.Equal(FlowDirection.Out, ChuteModels.At(flow, a, "+x"));
        Assert.Empty(flow.Outlets);
    }

    [Fact]
    public void CellFlowNamesTheWorldDirections()
    {
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a },
            new[] { ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output) }, new long[] { -1 });

        CellFlow cell = CellFlow.Of(a, A, flow);
        Assert.Equal(new[] { "-x" }, cell.Into.Names());
        Assert.Equal(new[] { "+x" }, cell.OutOf.Names());
    }
}

public sealed class TurnSearchTests
{
    [Fact]
    public void EachCellGetsTheTurnEveryValidCombinationAgreesOn()
    {
        // Cell 0 must take turn 1; cell 1 is valid either way.
        TurnVerdict[] verdicts = TurnSearch.Agreed(new[] { 2, 2 }, turns => turns[0] == 1);

        Assert.Equal(TurnAgreement.Unique, verdicts[0].Agreement);
        Assert.Equal(1, verdicts[0].Turn);
        Assert.Equal(TurnAgreement.Several, verdicts[1].Agreement);
    }

    [Fact]
    public void NoValidCombinationLeavesTheFirstTurn()
    {
        TurnVerdict verdict = Assert.Single(TurnSearch.Agreed(new[] { 2 }, _ => false));
        Assert.Equal(TurnAgreement.NoneValid, verdict.Agreement);
        Assert.Equal(0, verdict.Turn);
    }

    [Fact]
    public void TurnsDependOnEachOther()
    {
        // Two junctions in a row: valid only when they face the same way.
        TurnVerdict[] verdicts = TurnSearch.Agreed(new[] { 2, 2 }, turns => turns[0] == turns[1] && turns[0] == 0);
        Assert.All(verdicts, verdict => Assert.Equal(TurnAgreement.Unique, verdict.Agreement));
        Assert.All(verdicts, verdict => Assert.Equal(0, verdict.Turn));
    }
}

public sealed class PieceCatalogueOrientationTests
{
    [Fact]
    public void PiecesWithoutDirectionOfferOneTurnPerShape()
    {
        EndSet straight = RunModels.Ends("-x", "+x");
        PieceCatalogue catalogue = new PieceCatalogue(new[]
        {
            new PieceOption(0, 0, straight), new PieceOption(0, 5, straight), new PieceOption(1, 0, straight)
        });

        PieceOption only = Assert.Single(catalogue.Orientations(straight));
        Assert.Equal(0, only.Piece);
        Assert.Equal(0, only.Rotation);
        Assert.Equal(only.Rotation, catalogue.Find(straight)!.Value.Rotation);
    }

    [Fact]
    public void AJunctionOffersOneTurnPerOutputDirection()
    {
        EndSet tee = RunModels.Ends("-x", "+x", "+z");
        PieceCatalogue catalogue = new PieceCatalogue(new[]
        {
            new PieceOption(3, 2, tee, RunModels.Step("+x")), new PieceOption(3, 7, tee, RunModels.Step("+x")),
            new PieceOption(3, 9, tee, RunModels.Step("-x"))
        });

        List<PieceOption> turns = catalogue.Orientations(tee);
        Assert.Equal(2, turns.Count);
        Assert.Equal("+x", turns[0].Output!.Value.Name);
        Assert.Equal(2, turns[0].Rotation);
        Assert.Equal("-x", turns[1].Output!.Value.Name);
        Assert.Empty(catalogue.Orientations(RunModels.Ends("+x", "+y", "+z")));
    }
}
