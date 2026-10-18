#nullable enable

using System.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Routing;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A run that ends on an existing piece needs no hand swap: the pieces form's last piece naming an end into the side
/// of a standing straight makes it a tee in the same job, as a waypoints run ending beside or on it does; and each run
/// what's help says so, with join_to + join_trunk for ending on a network.
/// </summary>
public sealed class RunJunctionTests
{
    [Fact]
    public void APiecesRunWhoseLastEndPointsIntoAStandingStraightMakesItATee()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(500, RunModels.At(3, 0, 0), "+z", "-z"));
        GridCell last = RunModels.At(2, 0, 0);
        RunLayout layout = RunLayoutPlanner.Plan(
            new[] { RunModels.At(1, 0, 0), last }, around, JoinMode.None,
            new[]
            {
                new ExtraEnd(RunModels.At(1, 0, 0), RunModels.Step("-x")),
                new ExtraEnd(RunModels.At(1, 0, 0), RunModels.Step("+x")),
                new ExtraEnd(last, RunModels.Step("-x")),
                new ExtraEnd(last, RunModels.Step("+x"))
            }, null);

        Assert.Empty(layout.Problems);
        LayoutCell trunk = layout.At(RunModels.At(3, 0, 0))!;
        Assert.Equal(CellAction.Change, trunk.Action);
        Assert.Equal("tee", trunk.Ends.Shape);
        Assert.Contains(layout.At(last)!.Joins, join => join.Kind == "piece" && join.TargetId == 500);
    }

    [Theory]
    [InlineData("cable")]
    [InlineData("pipe")]
    [InlineData("chute")]
    public void EachRunWhatSaysEndingOnAPieceMakesTheJunction(string what)
    {
        string text = string.Join("\n", VerbCatalogue.Verb("build")["whats"]![what]!["text"]!.AsArray()
            .Select(line => (string)line!));
        Assert.Contains("junction it needs in the same job", text);
        Assert.Contains("join_trunk: true", text);
    }
}
