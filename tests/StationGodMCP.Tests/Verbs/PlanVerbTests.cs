#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The plan whats as their handlers read their options (the cursor, rockets and dishes need the game): spot's prefab,
/// place, turn and requirements.
/// </summary>
public sealed class PlanVerbTests
{
    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("plan")!, JObject.Parse(options), out _);

    private static SpotOrder? Spot(string json, List<OptionProblem> problems) =>
        SpotOrder.Of(PlanSpotOptions.Bind(new OptionReader(PlanSpotOptions.Spec, JObject.Parse(json))), problems);

    // ---- spot ----

    [Fact]
    public void SpotSearchesNearTheCrosshairWithTheDefaults()
    {
        List<OptionProblem> problems = new();

        SpotOrder order = Spot("""{"prefab": "StructureWallLight", "looking": true}""", problems)!;

        Assert.Empty(problems);
        Assert.Equal("StructureWallLight", Assert.IsType<PrefabRef.Named>(order.Prefab).Name);
        Assert.IsType<NearPoint.LookingAt>(order.Near);
        Assert.Equal(4.0, order.RadiusM);
        Assert.Equal(5, order.Limit);
        Assert.Equal(40, order.MaxChecks);
        Assert.Null(order.Room);
        Assert.True(order.NamesPlane);
        Assert.True(order.Require.OneSection);
        Assert.True(order.Require.AvoidDoors);
        Assert.True(order.Require.NoVisualOverlap);
        Assert.False(order.Require.PortsReachable);
    }

    [Fact]
    public void SpotReadsARoomsWallsAPlaneTheTurnAndTheRequirements()
    {
        List<OptionProblem> problems = new();

        SpotOrder order = Spot("""
            {"prefab": -1234, "room": "28", "plane": "y=222", "near": [700, 223, 680], "radius": 6, "facing": "-z",
             "around": [701, 223, 681], "limit": 20, "max_checks": 200,
             "require": {"ports_reachable": true, "front_clear_m": 1.0, "min_bottom_above_floor_m": 0.5}}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal(-1234, Assert.IsType<PrefabRef.Hashed>(order.Prefab).Hash);
        Assert.Equal(28, order.Room);
        Assert.Equal("y=222", order.Plane);
        Assert.Equal(new Vec3(700, 223, 680), Assert.IsType<NearPoint.At>(order.Near).Point);
        Assert.Equal(6.0, order.RadiusM);
        Assert.Equal("-z", order.Facing!.Value.Name);
        Assert.Equal(new Vec3(701, 223, 681), order.Around);
        Assert.Equal(20, order.Limit);
        Assert.Equal(200, order.MaxChecks);
        Assert.True(order.Require.PortsReachable);
        Assert.Equal(2, order.Require.FrontClearCells);
        Assert.Equal(0.5, order.Require.MinBottomAboveFloorM);
    }

    [Theory]
    [InlineData("""{"prefab": "*Light*", "looking": true}""", "prefab", "is one prefab: its whole name or its hash")]
    [InlineData("""{"prefab": "StructureWallLight"}""", "plane", "or looking, or room, is needed: where to search")]
    [InlineData("""{"prefab": "StructureWallLight", "room": "kitchen"}""", "room", "is a room's id, as find room lists it")]
    [InlineData("""{"prefab": "StructureWallLight", "looking": true, "around": [1, 2]}""", "around", "is a point, [x, y, z]")]
    [InlineData("""{"prefab": "StructureWallLight", "looking": true, "require": {"avoid_doors": "no"}}""", "require.avoid_doors", "is true or false")]
    [InlineData("""{"prefab": "StructureWallLight", "looking": true, "require": {"front_clear_m": -1}}""", "require.front_clear_m", "is a distance in metres, 0 or more")]
    public void SpotRefusesWhatItCannotSearch(string json, string path, string problem)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Spot(json, problems));

        OptionProblem only = Assert.Single(problems);
        Assert.Equal(path, only.Path);
        Assert.Equal(problem, only.Problem);
    }

    [Fact]
    public void SpotChecksAtMost200AndListsAtMost20()
    {
        Assert.NotNull(Check("""{"what": "spot", "prefab": "StructureWallLight", "looking": true, "max_checks": 201}"""));
        Assert.NotNull(Check("""{"what": "spot", "prefab": "StructureWallLight", "looking": true, "limit": 21}"""));
        Assert.NotNull(Check("""{"what": "spot", "prefab": "StructureWallLight", "looking": true, "radius": 13}"""));
        Assert.Null(Check("""{"what": "spot", "prefab": "StructureWallLight", "looking": true, "max_checks": 200}"""));
    }

    // ---- mining ----

    [Fact]
    public void MiningReadsTheRocketTheNodeAndExplain()
    {
        PlanMiningOptions every = PlanMiningOptions.Bind(new OptionReader(PlanMiningOptions.Spec, JObject.Parse("{}")));
        PlanMiningOptions one = PlanMiningOptions.Bind(new OptionReader(PlanMiningOptions.Spec,
            JObject.Parse("""{"id": ["Ore rocket", "2"], "node": "Asteroid 7", "collectable_only": true, "include": ["explain"]}""")));

        Assert.Null(every.Selector.Ids);
        Assert.False(every.CollectableOnly);
        Assert.False(every.Fields.Includes("explain"));
        Assert.True(one.Selector.Ids!.AnswersRows);
        Assert.Equal("Asteroid 7", one.Node);
        Assert.True(one.CollectableOnly);
        Assert.True(one.Fields.Includes("explain"));
        Assert.NotNull(Check("""{"what": "mining", "to": "Asteroid 7"}"""));
    }
}
