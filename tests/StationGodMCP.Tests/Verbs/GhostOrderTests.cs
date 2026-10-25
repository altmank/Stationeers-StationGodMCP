#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// show ghost without the game: placements read as show preview reads them, a top-level label for every placement
/// without its own, tints, remove ids, clear, and a call that names nothing to do.
/// </summary>
public sealed class GhostOrderTests
{
    private static GhostOrder? Order(string json, List<OptionProblem> problems) =>
        GhostOrder.Of(ShowGhostOptions.Bind(new OptionReader(ShowGhostOptions.Spec, JObject.Parse(json))), problems);

    [Fact]
    public void ATopLevelLabelGoesToEveryPlacementWithoutItsOwn()
    {
        GhostOrder order = Order("""
            {"label": "O2 line", "tint": "purple",
             "placements": [{"prefab": "A", "at": [0, 0, 0]}, {"prefab": "B", "at": [1, 0, 0], "label": "spare", "tint": "red"}]}
            """, new List<OptionProblem>())!;

        Assert.Equal("O2 line", order.LabelOf(0));
        Assert.Equal("spare", order.LabelOf(1));
        Assert.Null(order.Arguments.Placements[1].Label);
        Assert.Equal(GhostTints.ByName("purple"), order.TintOf(0));
        Assert.Equal(GhostTints.ByName("red"), order.TintOf(1));
        Assert.True(order.Arguments.Free);
    }

    [Fact]
    public void OnePlacementAtTheTopLevelTakesTheLabel()
    {
        GhostOrder order = Order("""{"prefab": "A", "at": [0, 0, 0], "label": "pump"}""", new List<OptionProblem>())!;

        Assert.Single(order.Arguments.Placements);
        Assert.Equal("pump", order.LabelOf(0));
    }

    // show ghost {run}: a run's pieces go on the board, as build would lay them (agents copied each piece by hand).
    [Fact]
    public void ARunNeedsNoPlacementAndIsReadAsBuildReadsIt()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        GhostOrder order = Order("""
            {"label": "header", "run": {"what": "pipe", "grade": "insulated_gas", "waypoints": [[0, 0, 0], [2, 0, 0]]}}
            """, problems)!;

        Assert.Empty(problems);
        Assert.False(order.HasPlacements);
        Assert.Equal("pipe", order.Run!.Kind);
        Assert.Equal("header", order.Label);
    }

    [Fact]
    public void ARunOfAnotherWhatIsRefusedUnderRun()
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        Assert.Null(Order("""{"run": {"what": "wall", "waypoints": [[0, 0, 0]]}}""", problems));
        Assert.Contains(problems, problem => problem.Path == "run.what");
    }

    [Fact]
    public void RemoveAndClearNeedNoPlacement()
    {
        GhostOrder order = Order("""{"remove": [3, 4], "clear": "agent"}""", new List<OptionProblem>())!;

        Assert.False(order.HasPlacements);
        Assert.Equal(new long[] { 3, 4 }, order.Remove);
        Assert.Equal("agent", order.Clear);
    }

    [Theory]
    [InlineData("{}", "placements")]
    [InlineData("""{"remove": [0]}""", "remove[0]")]
    [InlineData("""{"remove": ["x"]}""", "remove[0]")]
    [InlineData("""{"clear": "mine"}""", "clear")]
    [InlineData("""{"prefab": "A", "at": [0, 0, 0], "tint": "violet"}""", "tint")]
    public void AMalformedCallIsAProblemAtItsPath(string json, string path)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems));

        Assert.Contains(path, problems.ConvertAll(problem => problem.Path));
    }

    [Fact]
    public void TheGameSPaintNamesAreTints()
    {
        foreach (string name in new[] { "purple", "pink", "khaki", "brown", "black", "gray", "grey", " Cyan " })
        {
            Assert.NotNull(GhostTints.ByName(name));
        }

        Assert.Null(GhostTints.ByName("violet"));
        Assert.Equal(GhostTints.DefaultAlpha, GhostTints.ByName("pink")!.Value.Alpha);
    }
}
