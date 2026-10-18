#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// build structure's logic: values set once a placement stands built ({On: 1}), at the top level or per placement, in
/// order, a number or true/false each; anything else is refused before the game is asked.
/// </summary>
public sealed class BuildLogicTests
{
    private static StructureBuildOrder? Order(string json, List<OptionProblem> problems) =>
        StructureBuildOrder.Of(BuildStructureOptions.Bind(new OptionReader(BuildStructureOptions.Spec,
            JObject.Parse(json))), problems, out _);

    [Fact]
    public void ATopLevelPlacementCarriesItsLogicInOrder()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order(
            """{"prefab": "StructureBatteryLarge", "at": [1, 2, 3], "logic": {"On": 1, "Mode": 2, "Lock": true}}""",
            problems)!;

        Assert.Empty(problems);
        PlacementRequest placement = Assert.Single(order.Placements);
        Assert.Equal(new[] { "On", "Mode", "Lock" }, placement.Logic.Select(set => set.Name).ToArray());
        Assert.Equal(new[] { 1.0, 2.0, 1.0 }, placement.Logic.Select(set => set.Value).ToArray());
    }

    [Fact]
    public void EachPlacementOfAListHasItsOwnLogic()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder order = Order("""
            {"placements": [
              {"prefab": "StructureWallLight", "at": [1, 2, 3], "logic": {"On": 1}},
              {"prefab": "StructureWallLight", "at": [3, 2, 3]}
            ]}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal("On", Assert.Single(order.Placements[0].Logic).Name);
        Assert.Empty(order.Placements[1].Logic);
    }

    [Fact]
    public void AValueThatIsNoNumberIsRefused()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"prefab": "StructureWallLight", "at": [1, 2, 3], "logic": {"On": "yes"}}""", problems));

        Assert.Contains(problems, problem => problem.Path == "logic.On" && problem.Problem.Contains("is a number"));
    }
}
