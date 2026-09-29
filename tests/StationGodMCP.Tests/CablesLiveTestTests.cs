#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from the cables live test of 1.4.4: a run end at a device port stops there, a new piece that takes a free
/// port's cell warns, would_split roots by the network a supplier feeds, a cut root port cuts off what is left, and
/// undo_job's source.
/// </summary>
public sealed class CablesLiveTestTests
{
    private static readonly ExtraEnd[] NoExtra = new ExtraEnd[0];

    private static List<GridCell> Run(params GridCell[] waypoints) => RunPath.FromWaypoints(waypoints, out _)!;

    // cables-2: a run into an APC's output port cell, heading on towards a cable of the APC's input network.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARunEndAtADevicePortDoesNotJoinThePieceStraightAhead(bool pipe)
    {
        PipeContent? content = pipe ? new PipeContent(1, false) : null;
        RunSurroundings around = new RunSurroundings();
        around.Ports.Add(RunModels.Port(900, 1, RunModels.At(2, 0, 0), RunModels.At(2, 0, 1)));
        around.DeviceCells[RunModels.At(2, 0, 1)] = 900;
        PieceModel ahead = EndSet.ModelAt(70, RunModels.At(3, 0, 0), RunModels.Ends("+z", "-z"),
            RunModels.PowerAndData, 0, content);
        around.AddPiece(ahead);

        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, content);

        LayoutCell last = layout.At(RunModels.At(2, 0, 0))!;
        Assert.Equal(RunModels.Ends("-x", "+z"), last.Ends);
        Assert.Contains(last.Joins, join => join.Kind == "port" && join.TargetId == 900);
        Assert.DoesNotContain(last.Joins, join => join.Kind == "piece");
        Assert.Null(layout.At(RunModels.At(3, 0, 0)));
    }

    [Fact]
    public void ARunEndWithoutAPortStillJoinsThePieceStraightAhead()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(70, RunModels.At(3, 0, 0), "+z", "-z"));
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, null);
        Assert.Contains(layout.At(RunModels.At(2, 0, 0))!.Joins, join => join.Kind == "piece" && join.TargetId == 70);
    }

    // cables-21: a route through a battery's free input port cell takes it without joining it.
    [Fact]
    public void ANewPieceInAFreePortCellWithoutAnEndTowardsItWarns()
    {
        RunSurroundings around = new RunSurroundings();
        around.Ports.Add(RunModels.Port(460, 1, RunModels.At(1, 0, 0), RunModels.At(1, 0, 1)));
        around.DeviceCells[RunModels.At(1, 0, 1)] = 460;
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.Ends, NoExtra, null);
        LayoutIssue warning = Assert.Single(layout.Warnings, issue => issue.Code == RunLayoutPlanner.BlocksPort);
        Assert.Equal(460, warning.Id);
        Assert.Equal(RunModels.At(1, 0, 0), warning.Cell);
        Assert.Empty(layout.Problems);
    }

    [Fact]
    public void JoiningThePortInItsCellDoesNotWarn()
    {
        RunSurroundings around = new RunSurroundings();
        around.Ports.Add(RunModels.Port(460, 1, RunModels.At(1, 0, 0), RunModels.At(1, 0, 1)));
        around.DeviceCells[RunModels.At(1, 0, 1)] = 460;
        RunLayout layout = RunLayoutPlanner.Plan(Run(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0)), around,
            JoinMode.All, NoExtra, null);
        Assert.DoesNotContain(layout.Warnings, issue => issue.Code == RunLayoutPlanner.BlocksPort);
    }

    private const long Charging = 1149;
    private const long Supply = 1155;
    private const long Transformer = 463;
    private const long Battery = 460;
    private const long Apc = 461;
    private const long Console = 464;

    // cables-11: transformer output -> 1 - 2 - 3 -> battery input on the charging network; the battery's output
    // feeds the supply network. Removing 2 cuts the battery off its feed.
    private static Forecast ChargingSplit()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.AddPiece(1, Charging);
        edit.Remove(2, Charging);
        edit.AddPiece(3, Charging);
        edit.Ports.Add(new ForecastPort(Transformer, 1, true, Charging, 1));
        edit.Ports.Add(new ForecastPort(Battery, 1, true, Charging, 3));
        return NetworkForecaster.Of(edit, out _);
    }

    private static NetworkRootSet Suppliers()
    {
        NetworkRootSet roots = NetworkRootSet.None;
        roots.AddFeed(Transformer, Charging);
        roots.AddFeed(Battery, Supply);
        return roots;
    }

    [Fact]
    public void ABatterysInputIsNotARootOfTheNetworkThatChargesIt()
    {
        SplitDetail detail = Assert.Single(SplitAnalysis.Of(ChargingSplit(), Suppliers()));
        Assert.Equal(new[] { Transformer }, detail.Roots);
        Assert.Equal(new[] { Battery }, detail.CutOff);
        Assert.Single(detail.Parts, part => part.HoldsRoot);
    }

    [Fact]
    public void ANamedRootCountsOnEveryNetwork()
    {
        SplitDetail detail = Assert.Single(SplitAnalysis.Of(ChargingSplit(), NetworkRootSet.Named(Battery)));
        Assert.Equal(new[] { Transformer }, detail.CutOff);
    }

    // cables-6 and cables-10: APC output -> 1 - 2 - 3 with a console on 3. Removing 1 (the APC's port piece) leaves 2-3
    // whole but without its feed.
    [Fact]
    public void CuttingTheRootsOwnPortCutsOffEveryDeviceLeftOnTheNetwork()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(1, Supply);
        edit.AddPiece(2, Supply);
        edit.AddPiece(3, Supply);
        edit.Link(2, 3);
        edit.Ports.Add(new ForecastPort(Apc, 1, true, Supply, null));
        edit.Ports.Add(new ForecastPort(Console, 0, true, Supply, 3));
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        NetworkRootSet roots = NetworkRootSet.None;
        roots.AddFeed(Apc, Supply);

        Assert.Empty(forecast.Splits);
        SplitDetail detail = Assert.Single(SplitAnalysis.Of(forecast, roots));
        Assert.Equal(Supply, detail.Network);
        Assert.Equal(new[] { Apc }, detail.Roots);
        Assert.Equal(new[] { Console }, detail.CutOff);
        LayoutIssue issue = Assert.Single(EditGuards.Check(forecast, EditAllowance.Nothing, roots));
        Assert.Contains($"Cut off from the root: {Console}.", issue.Message);
    }

    // cables-5: a split whose network the named root is not on says so instead of asking for a root.
    [Fact]
    public void ANamedRootOffTheSplitNetworkIsNamedAsSuch()
    {
        SplitDetail detail = Assert.Single(SplitAnalysis.Of(ChargingSplit(), NetworkRootSet.Named(Apc)));
        Assert.Null(detail.CutOff);
        Assert.Contains("The root named is not on it", SplitAnalysis.Describe(detail, true));
        Assert.Contains("pass root to name one", SplitAnalysis.Describe(detail));
    }

    // cables-7 and structures-5.
    [Fact]
    public void AnUndoUsesTheJobsOwnSource()
    {
        UndoSource source = UndoSource.Of(new JobSource(462, false, null, true), null, null);
        Assert.Equal(462, source.FromId);
        Assert.Null(source.RefundTo);
        Assert.Contains(source.Notes, note => note.Contains("from_id 462"));
    }

    [Fact]
    public void TheCallersSourceOverridesTheJobs()
    {
        UndoSource source = UndoSource.Of(new JobSource(462, true, null, null), 500, RefundRoute.WherePieceStood);
        Assert.Equal(500, source.FromId);
        Assert.Same(RefundRoute.WherePieceStood, source.RefundTo);
    }

    [Fact]
    public void UndoingAFreePlacementRefundsNothing()
    {
        UndoSource source = UndoSource.Of(new JobSource(null, true, null, null), null, null);
        Assert.Null(source.FromId);
        Assert.Same(RefundRoute.Nothing, source.RefundTo);
    }

    [Fact]
    public void AnUnrecordedJobLeavesTheDefaults()
    {
        UndoSource source = UndoSource.Of(JobSource.Unknown, null, null);
        Assert.Null(source.FromId);
        Assert.Null(source.RefundTo);
        Assert.Empty(source.Notes);
    }
}
