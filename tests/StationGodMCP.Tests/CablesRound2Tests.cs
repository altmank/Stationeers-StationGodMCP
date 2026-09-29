#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 2 of the cables live test of 1.4.4.</summary>
public sealed class CablesRound2Tests
{
    private static readonly ExtraEnd[] NoExtra = new ExtraEnd[0];

    private static List<GridCell> Run(params GridCell[] waypoints) => RunPath.FromWaypoints(waypoints, out _)!;

    private static GridCell At(int x, int y, int z) => RunModels.At(x, y, z);

    // A long straight along x over `length` cells from `start`, ends at both tips.
    private static PieceModel LongX(long id, GridCell start, int length)
    {
        GridCell[] cells = new GridCell[length];
        for (int index = 0; index < length; index++)
        {
            cells[index] = At(index, 0, 0);
        }

        return new PieceModel(id, cells,
            new[]
            {
                new PieceEnd(At(-1, 0, 0), cells[0], RunModels.PowerAndData, 0),
                new PieceEnd(At(length, 0, 0), cells[length - 1], RunModels.PowerAndData, 0)
            }, null);
    }

    // cables-23: a route to a network ends on one of its pieces; the piece straight ahead is across a transformer.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARunEndOnAnExistingPieceDoesNotJoinThePieceStraightAhead(bool pipe)
    {
        PipeContent? content = pipe ? new PipeContent(1, false) : null;
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(EndSet.ModelAt(2111, At(2, 0, 0), RunModels.Ends("+z", "-z"), RunModels.PowerAndData, 0,
            content));
        around.AddPiece(EndSet.ModelAt(2024, At(3, 0, 0), RunModels.Ends("+z", "-z"), RunModels.PowerAndData, 0,
            content));

        RunLayout layout = RunLayoutPlanner.Plan(Run(At(0, 0, 0), At(2, 0, 0)), around, JoinMode.Ends, NoExtra,
            content);

        LayoutCell last = layout.At(At(2, 0, 0))!;
        Assert.Equal(RunModels.Ends("-x", "+z", "-z"), last.Ends);
        Assert.DoesNotContain(last.Joins, join => join.TargetId == 2024);
        Assert.Null(layout.At(At(3, 0, 0)));
    }

    [Fact]
    public void ARunEndJoiningAPiecesOpenEndDoesNotJoinAnotherStraightAhead()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(80, At(2, 0, 1), "-z", "+z"));
        around.AddPiece(RunModels.Piece(81, At(3, 0, 0), "+z", "-z"));

        RunLayout layout = RunLayoutPlanner.Plan(Run(At(0, 0, 0), At(2, 0, 0)), around, JoinMode.Ends, NoExtra,
            null);

        LayoutCell last = layout.At(At(2, 0, 0))!;
        Assert.Contains(last.Joins, join => join.Kind == "piece" && join.TargetId == 80);
        Assert.DoesNotContain(last.Joins, join => join.TargetId == 81);
    }

    // cables-26: a run ending on a long straight's end cell through its open end keeps it as it is.
    [Fact]
    public void ARunOntoALongStraightsOpenEndKeepsThePieceWithoutAnOpenEnd()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(LongX(2522, At(0, 0, 0), 3));

        RunLayout layout = RunLayoutPlanner.Plan(Run(At(5, 0, 0), At(2, 0, 0)), around, JoinMode.Ends, NoExtra,
            null);

        LayoutCell last = layout.At(At(2, 0, 0))!;
        Assert.Equal(CellAction.Keep, last.Action);
        Assert.Null(last.OpenEnd);
        Assert.DoesNotContain(layout.Warnings,
            issue => issue.Code == "open_end" && At(2, 0, 0).Equals(issue.Cell));
        Assert.Empty(layout.Problems);
    }

    // cables-24: undo of a removed long straight builds it again as singles, each with the ends of a straight.
    [Fact]
    public void ALongStraightsCellsEachHaveTheEndsOfASingle()
    {
        List<PieceCell> cells = NetworkPiece.CellsOf(LongX(2520, At(0, 0, 0), 5));

        Assert.Equal(5, cells.Count);
        Assert.All(cells, cell => Assert.Equal(RunModels.Ends("+x", "-x"), cell.Ends));
    }

    [Fact]
    public void ASinglesCellsAreItsOwnEnds()
    {
        PieceCell cell = Assert.Single(NetworkPiece.CellsOf(RunModels.Piece(1, At(0, 0, 0), "+x", "+z")));
        Assert.Equal(RunModels.Ends("+x", "+z"), cell.Ends);
    }

    // cables-10: stubs removed from two unrelated networks give one would_split entry per network, each with its id
    // and its own roots.
    [Fact]
    public void CutPortsOnTwoNetworksGiveOneEntryEach()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(1, 1219);
        edit.Remove(2, 1221);
        edit.AddPiece(3, 1221);
        edit.Ports.Add(new ForecastPort(1123, 2, true, 1219, null));
        edit.Ports.Add(new ForecastPort(1139, 0, true, 1221, null));
        edit.Ports.Add(new ForecastPort(1500, 0, true, 1221, 3));
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        NetworkRootSet roots = NetworkRootSet.None;
        roots.AddFeed(1123, 1219);

        List<SplitDetail> details = SplitAnalysis.Of(forecast, roots);

        Assert.Equal(2, details.Count);
        Assert.Equal(1219, details[0].Network);
        Assert.Equal(new long[] { 1123 }, details[0].Roots);
        Assert.Equal(1221, details[1].Network);
        Assert.Empty(details[1].Roots);
        Assert.Null(details[1].CutOff);
        List<LayoutIssue> issues = EditGuards.Check(forecast, EditAllowance.Nothing, roots);
        Assert.DoesNotContain(issues, issue => issue.Message.Contains("1221") && issue.Message.Contains("Cut off"));
    }

    // cables-10: a device cut from a network whose root stays on it names that root.
    [Fact]
    public void ACutPortNamesTheRootThatStaysOnItsNetwork()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(1, 1376);
        edit.AddPiece(2, 1376);
        edit.Ports.Add(new ForecastPort(362, 1, true, 1376, 2));
        edit.Ports.Add(new ForecastPort(365, 0, true, 1376, null));
        Forecast forecast = NetworkForecaster.Of(edit, out _);
        NetworkRootSet roots = NetworkRootSet.None;
        roots.AddFeed(362, 1376);

        SplitDetail detail = Assert.Single(SplitAnalysis.Of(forecast, roots));

        Assert.Equal(new long[] { 362 }, detail.Roots);
        Assert.Equal(new long[] { 365 }, detail.CutOff);
    }

    // devices cross-area: remove_cables refund false moves no item, so it needs neither a player nor from_id.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void OnlyAToolThatMovesItemsNeedsASource(bool builds, bool refund, bool needed) =>
        Assert.Equal(needed, SourceRule.NeedsSource(builds, refund));

    // cables-31: plan.notes says which override applied.
    [Fact]
    public void TheNotesNameTheCallersOverrides()
    {
        UndoSource source = UndoSource.Of(new JobSource(363, false, null, null), 2383, "ground");
        Assert.Contains(source.Notes, note => note.StartsWith("from_id 2383") && note.Contains("the job's own, 363"));
        Assert.Contains(source.Notes, note => note.StartsWith("refund_to ground"));
    }
}
