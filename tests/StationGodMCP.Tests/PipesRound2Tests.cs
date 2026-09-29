#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 2 of the pipes live test of 1.4.4.</summary>
public sealed class PipesRound2Tests
{
    private static readonly PipeContent Gas = new PipeContent(1, false);
    private static readonly PipeContent Liquid = new PipeContent(2, false);

    private static GridCell At(int x, int y, int z) => RunModels.At(x, y, z);

    private static RunShape OneCell(GridCell cell) =>
        RunShape.Of(new List<GridCell> { cell }, new List<RunBranch>(), out _)!;

    // A long straight along z over `length` cells from z = firstZ, ends at both tips.
    private static PieceModel LongZ(long id, int firstZ, int length)
    {
        GridCell[] cells = new GridCell[length];
        for (int index = 0; index < length; index++)
        {
            cells[index] = At(0, 0, firstZ + index);
        }

        return new PieceModel(id, cells,
            new[]
            {
                new PieceEnd(At(0, 0, firstZ - 1), cells[0], RunModels.PowerAndData, 0),
                new PieceEnd(At(0, 0, firstZ + length), cells[length - 1], RunModels.PowerAndData, 0)
            }, null);
    }

    // pipes-15: the only pipe of a gas network, split so a run can join its middle. Its singles carry the network on:
    // it is not gone (no holds_contents), and the network after pools it with the one the tee joins.
    [Fact]
    public void ASplitLongsSinglesCarryItsNetworkOn()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(1842, 1843);
        edit.AddSuccessor(-1, 1843);
        edit.AddSuccessor(-2, 1843);
        edit.AddSuccessor(-3, 1843);
        edit.AddNetwork(1985);
        edit.Link(-1, -2);
        edit.Link(-2, -3);
        edit.Link(-2, 1985);

        Forecast forecast = NetworkForecaster.Of(edit, out _);

        Assert.Empty(forecast.Gone);
        Assert.Equal(new long[] { 1843 }, forecast.Carried);
        Assert.Empty(forecast.Splits);
        ForecastNetwork after = Assert.Single(forecast.Networks);
        Assert.Equal(new long[] { 1843, 1985 }, after.NetworksBefore);
        Assert.Equal(new long[] { -3, -2, -1 }, after.NewPieces);
        Assert.Same(after, Assert.Single(forecast.Merges));
    }

    [Fact]
    public void ALongRemovedWithoutSuccessorsEmptiesItsNetwork()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(1842, 1843);
        edit.AddPiece(-1, null);

        Forecast forecast = NetworkForecaster.Of(edit, out _);

        Assert.Equal(new long[] { 1843 }, forecast.Gone);
        Assert.Empty(forecast.Carried);
    }

    // m3: a long that is not its network's only pipe; the network keeps a piece of its own, so it is not carried.
    [Fact]
    public void ASplitLongBesideOtherPiecesIsNeitherGoneNorCarried()
    {
        NetworkEdit edit = new NetworkEdit();
        edit.Remove(2189, 2190);
        edit.AddPiece(2200, 2190);
        edit.AddSuccessor(-1, 2190);
        edit.AddSuccessor(-2, 2190);
        edit.Link(2200, -1);
        edit.Link(-1, -2);

        Forecast forecast = NetworkForecaster.Of(edit, out _);

        Assert.Empty(forecast.Gone);
        Assert.Empty(forecast.Carried);
        Assert.Empty(forecast.Splits);
        Assert.Equal(new long[] { 2190 }, Assert.Single(forecast.Networks).NetworksBefore);
    }

    // pipes-16: a one-cell run that joins nothing has no direction: nothing_to_join, not no_piece_for_ends [].
    [Fact]
    public void AOneCellRunJoiningNothingIsNothingToJoin()
    {
        RunLayout layout = RunLayoutPlanner.Plan(OneCell(At(0, 0, 0)), new RunSurroundings(), JoinMode.Ends,
            new ExtraEnd[0], Gas);

        LayoutIssue issue = Assert.Single(layout.Problems);
        Assert.Equal(RunLayoutPlanner.NothingToJoin, issue.Code);
        Assert.True(layout.At(At(0, 0, 0))!.Ends.IsEmpty);
    }

    // pipes-16: the same between two liquid pipes pointing at it is content_mismatch.
    [Fact]
    public void AOneCellGasRunBetweenLiquidPipesIsContentMismatch()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(EndSet.ModelAt(2774, At(-1, 0, 0), RunModels.Ends("+x", "-x"), RunModels.PowerAndData, 0,
            Liquid));
        around.AddPiece(EndSet.ModelAt(2775, At(1, 0, 0), RunModels.Ends("+x", "-x"), RunModels.PowerAndData, 0,
            Liquid));

        RunLayout layout = RunLayoutPlanner.Plan(OneCell(At(0, 0, 0)), around, JoinMode.Ends, new ExtraEnd[0], Gas);

        LayoutIssue issue = Assert.Single(layout.Problems);
        Assert.Equal("content_mismatch", issue.Code);
    }

    // pipes-17: the tap check never offers a tap into a pipe of other content.
    [Fact]
    public void PipesOfOtherContentNeverJoin()
    {
        Assert.False(PipeContent.Join(Gas, Liquid));
        Assert.True(PipeContent.Join(Gas, Gas));
        Assert.True(PipeContent.Join(Gas, null));
        Assert.True(PipeContent.Join(null, null));
        Assert.True(PipeContent.Join(new PipeContent(1, true), new PipeContent(2, true)));
    }

    // pipes-18 (fixed with cables-26): a piece beside a long straight's end cell joins that end; the long is kept as
    // it is, with no phantom change and no open_end there.
    [Fact]
    public void APieceBesideALongStraightsEndKeepsTheLong()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(LongZ(1066, -10, 10));
        ExtraEnd[] extra =
        {
            new ExtraEnd(At(0, 0, 0), RunModels.Step("-z")), new ExtraEnd(At(0, 0, 0), RunModels.Step("+z")),
            new ExtraEnd(At(0, 0, 0), RunModels.Step("+x"))
        };

        RunLayout layout = RunLayoutPlanner.Plan(OneCell(At(0, 0, 0)), around, JoinMode.None, extra, Gas);

        LayoutCell end = layout.At(At(0, 0, -1))!;
        Assert.Equal(CellAction.Keep, end.Action);
        Assert.Null(end.OpenEnd);
        Assert.Empty(layout.Problems);
        Assert.DoesNotContain(layout.Warnings, issue => At(0, 0, -1).Equals(issue.Cell));
        Assert.Contains(layout.At(At(0, 0, 0))!.Joins, join => join.Kind == "piece" && join.TargetId == 1066);
    }

    // pipes-19: a chute route runs with the items; it never ends at an output port nor starts at an input port.
    [Theory]
    [InlineData(ChuteRoles.Output, true, "pushes items out")]
    [InlineData(ChuteRoles.Output2, true, "pushes items out")]
    [InlineData(ChuteRoles.Input, true, null)]
    [InlineData(ChuteRoles.None, true, null)]
    [InlineData(ChuteRoles.Input, false, "takes items in")]
    [InlineData(ChuteRoles.Input2, false, "takes items in")]
    [InlineData(ChuteRoles.Output, false, null)]
    [InlineData(ChuteRoles.None, false, null)]
    public void AChuteRouteEndMustRunWithTheItems(int role, bool target, string? refusal) =>
        Assert.Equal(refusal, ChuteRoles.WrongWay(role, target));
}
