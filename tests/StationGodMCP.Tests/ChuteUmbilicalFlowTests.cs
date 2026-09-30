#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A rocket chute umbilical's one chute port is labelled Input but carries items both ways (CODE: a chute pushes into
/// it, and the partner pushes its item out through it: RocketChuteUmbilicalMale/Female.OnServerTick). Live case: male
/// 702126 on LU's pad refused the last chute corner with flow_conflict.
/// </summary>
public sealed class ChuteUmbilicalFlowTests
{
    private static readonly GridCell A = RunModels.At(0, 0, 0);
    private static readonly GridCell B = RunModels.At(1, 0, 0);

    private static readonly int Umbilical = ChuteRoles.OfDevicePort(true, ChuteRoles.Input);

    [Fact]
    public void ATwoWayPortFixesNoDirectionWhateverItsLabel()
    {
        Assert.Equal(ChuteRoles.None, ChuteRoles.OfDevicePort(true, ChuteRoles.Input));
        Assert.Equal(ChuteRoles.None, ChuteRoles.OfDevicePort(true, ChuteRoles.Output));
        Assert.Equal(ChuteRoles.Input, ChuteRoles.OfDevicePort(false, ChuteRoles.Input));
        Assert.Equal(ChuteRoles.Output2, ChuteRoles.OfDevicePort(false, ChuteRoles.Output2));
    }

    [Fact]
    public void ARouteMayStartOrEndAtAnUmbilical()
    {
        Assert.Null(ChuteRoles.WrongWay(Umbilical, target: true));
        Assert.Null(ChuteRoles.WrongWay(Umbilical, target: false));
        Assert.NotNull(ChuteRoles.WrongWay(ChuteRoles.Input, target: false));
    }

    [Fact]
    public void AnUmbilicalFeedsALineIntoAStationInput()
    {
        // The umbilical is towards -x of A; a station device's Input port takes from B towards +x. The route runs
        // from the umbilical (A) to the device (B).
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b },
            new[]
            {
                ChuteModels.Port(702126, 0, A, "-x", Umbilical),
                ChuteModels.Port(200, 0, B, "+x", ChuteRoles.Input)
            }, new long[] { -1, -2 }, new[] { A, B });

        Assert.Empty(flow.Conflicts);
        Assert.Empty(flow.Outlets);
        Assert.Equal(FlowDirection.In, ChuteModels.At(flow, a, "-x"));
        Assert.Equal(FlowDirection.Out, ChuteModels.At(flow, b, "+x"));
    }

    [Fact]
    public void TheUmbilicalsInputLabelAloneWouldHaveRefusedThatLine()
    {
        // The bug: read as a plain Input, the umbilical and the station input both take items: flow_conflict.
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b },
            new[]
            {
                ChuteModels.Port(702126, 0, A, "-x", ChuteRoles.Input),
                ChuteModels.Port(200, 0, B, "+x", ChuteRoles.Input)
            }, new long[] { -1, -2 });

        Assert.Equal(FlowConflict.Conflict, Assert.Single(flow.Conflicts).Code);
    }

    [Fact]
    public void AStationOutputFeedsALineIntoAnUmbilical()
    {
        // A station device's Output port pushes into A from -x; the umbilical takes from B towards +x.
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b },
            new[]
            {
                ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output),
                ChuteModels.Port(702126, 0, B, "+x", Umbilical)
            }, new long[] { -1, -2 }, new[] { A, B });

        Assert.Empty(flow.Conflicts);
        Assert.Empty(flow.Outlets);
        Assert.Equal(FlowDirection.Out, ChuteModels.At(flow, b, "+x"));
    }

    [Fact]
    public void AnUmbilicalLineStillRefusesARunLaidAgainstAStationOutput()
    {
        // The guard stays for genuine conflicts: the station Output pushes into A, so a run laid from the umbilical
        // (B) towards the device (A) runs against it.
        PieceModel a = ChuteModels.Plain(-1, A, "-x", "+x");
        PieceModel b = ChuteModels.Plain(-2, B, "-x", "+x");
        ChuteFlowResult flow = ChuteModels.Solve(new[] { a, b },
            new[]
            {
                ChuteModels.Port(100, 1, A, "-x", ChuteRoles.Output),
                ChuteModels.Port(702126, 0, B, "+x", Umbilical)
            }, new long[] { -1, -2 }, new[] { B, A });

        Assert.Equal(FlowConflict.Reversed, Assert.Single(flow.Conflicts).Code);
    }
}
