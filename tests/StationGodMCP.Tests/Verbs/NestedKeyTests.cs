#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Sampling;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// Review D, R-D-22: a nested option object's key that is not one of its own is a problem at its path with the nearest
/// names, never dropped silently: build structure's spread, plan route's start, end, reroute, trunk and reserve_ports,
/// plan spot's require, survey wall's free_rects and read sample's reads (placements, at, orient and show preview's
/// boxes: RelativeAtTests, PreviewOrderTests).
/// </summary>
public sealed class NestedKeyTests
{
    [Fact]
    public void ASpreadLineTakesFromToAndStepOnly()
    {
        List<OptionProblem> problems = new();

        StructureBuildOrder.Of(BuildStructureOptions.Bind(new OptionReader(BuildStructureOptions.Spec, JObject.Parse(
            """{"prefab": "StructureWallIron", "spread": {"from": [0, 0, 0], "to": [4, 0, 0], "step": 2, "stride": 1}}"""))),
            problems, out _);

        Assert.Equal("spread.stride", Assert.Single(problems).Path);
    }

    [Theory]
    [InlineData("""{"start": {"at": [0, 0, 0], "prot": 1}, "end": {"at": [4, 0, 0]}}""", "start.prot", "port")]
    [InlineData("""{"start": [{"at": [0, 0, 0]}, {"id": "5", "ports": [0], "netwrok": "3"}], "end": {"at": [4, 0, 0]}}""",
        "start[1].netwrok", null)]
    [InlineData("""{"start": {"at": [0, 0, 0]}, "end": {"netwrok": "3"}}""", "end.netwrok", "network")]
    [InlineData("""{"reroute": {"id": ["5"], "betwen": ["1", "2"]}}""", "reroute.betwen", "between")]
    [InlineData("""{"start": {"at": [0, 0, 0]}, "trunk": {"waypoint": [[0, 0, 0], [4, 0, 0]]}}""", "trunk.waypoint", "waypoints")]
    [InlineData("""{"start": {"at": [0, 0, 0]}, "end": {"at": [4, 0, 0]}, "reserve_ports": [{"id": "5", "port": 0, "side": 1}]}""",
        "reserve_ports[0].side", null)]
    public void ARouteEndsKeysAreItsOwn(string json, string path, string? nearest)
    {
        List<OptionProblem> problems = new();

        Assert.Null(RouteOrder.Of(PlanRouteOptions.Bind(new OptionReader(PlanRouteOptions.Spec, JObject.Parse(json))),
            problems));

        OptionProblem problem = Assert.Single(problems, problem => problem.Path == path);
        if (nearest != null)
        {
            Assert.Contains(nearest, problem.Nearest!);
        }
    }

    [Fact]
    public void ASpotsRequireTakesItsOwnKeys()
    {
        List<OptionProblem> problems = new();

        SpotOrder.Of(PlanSpotOptions.Bind(new OptionReader(PlanSpotOptions.Spec, JObject.Parse(
            """{"prefab": "StructureConsole", "require": {"one_sektion": true}}"""))), problems);

        OptionProblem problem = Assert.Single(problems, problem => problem.Path == "require.one_sektion");
        Assert.Contains("one_section", problem.Nearest!);
    }

    [Fact]
    public void FreeRectsTakesItsOwnKeys()
    {
        List<OptionProblem> problems = new();

        Assert.Null(FreeRectRequest.Of(JObject.Parse("""{"w": 1.5, "h": 2, "limt": 3}"""), problems));

        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("free_rects.limt", problem.Path);
        Assert.Contains("limit", problem.Nearest!);
    }

    [Fact]
    public void ASamplesReadsTakeIdAndLogicOnly()
    {
        List<OptionProblem> problems = SampleRequests.Problems(JObject.Parse(
            """{"reads": [{"id": "1", "logic": "On"}, {"id": "2", "logik": "On"}]}"""));

        OptionProblem problem = Assert.Single(problems, problem => problem.Path == "reads[1].logik");
        Assert.Contains("logic", problem.Nearest!);
    }
}
