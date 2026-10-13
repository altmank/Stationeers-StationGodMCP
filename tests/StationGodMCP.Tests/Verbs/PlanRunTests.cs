#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// plan route and plan removal without the game. route: its call read (RouteOrder: the ends one way, start and end, a
/// reroute or a trunk; the words of allow, avoid and axis_order; the handles of join_to, allow_bridge and
/// avoid_networks; reserve_ports and assume_removed) and the build_arguments it answers, in build's options. removal:
/// its pieces are named one way, by id, waypoints, cells or network; two ways is a problem, none is missing_target
/// naming all four; kind is cable and rows 5 when left out, and rows is its own option.
/// </summary>
public sealed class PlanRunTests
{
    private static RouteOrder? Route(string json, List<OptionProblem> problems) =>
        RouteOrder.Of(PlanRouteOptions.Bind(new OptionReader(PlanRouteOptions.Spec, JObject.Parse(json))), problems);

    private static List<string> Paths(string json)
    {
        List<OptionProblem> problems = new();
        Assert.Null(Route(json, problems));
        return problems.ConvertAll(problem => problem.Path);
    }

    // ---- route ----

    [Fact]
    public void ARouteTakesTheCatalogueDefaults()
    {
        List<OptionProblem> problems = new();

        RouteOrder order = Route("""
            {"kind": "pipe", "grade": "gas", "start": {"id": "720259", "port": 1},
            "end": {"network": "5012345"}}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal("pipe", order.Kind);
        Assert.Equal("gas", order.Grade);
        Assert.Equal("supported", order.Style.Name);
        Assert.Equal(AxisOrder.Any, order.AxisOrder);
        Assert.Equal(400, order.MaxLength);
        Assert.Equal(6.0, order.MarginM);
        Assert.Equal(JoinMode.Ends, order.Join);
        Assert.Equal(5, order.Rows);
        Assert.True(order.SplitLong);
        Assert.False(order.Summarize || order.MinBends || order.AllowSplit || order.AllowDoorKeepOut);
        Assert.False(order.Reserves);
        Assert.Null(order.JoinTo);
        Assert.Null(order.Trunk);
    }

    [Fact]
    public void TheWordsOfAllowAvoidAndTheSearchAreRead()
    {
        List<OptionProblem> problems = new();

        RouteOrder order = Route("""
            {"kind": "cable", "start": {"at": [1, 2, 3]}, "end": {"id": "7"},
            "allow": ["door_keepout", "split"], "avoid": ["walkways"], "axis_order": "vertical_first",
            "style": "hidden", "join": "all", "min_bends": true, "margin": 0, "max_length": 1024,
            "avoid_networks": true, "split_long": false, "summarize": true, "rows": 0}
            """, problems)!;

        Assert.Empty(problems);
        Assert.True(order.AllowDoorKeepOut && order.AllowSplit && order.AvoidWalkways && order.AvoidAllNetworks);
        Assert.False(order.AvoidRoomInterior || order.SplitLong);
        Assert.Equal(AxisOrder.VerticalFirst, order.AxisOrder);
        Assert.Equal(RoutePreference.Hidden, order.Style.Prefer);
        Assert.Equal(JoinMode.All, order.Join);
        Assert.Equal("all", order.JoinWord);
        Assert.Equal(0.0, order.MarginM);
        Assert.Equal(1024, order.MaxLength);
        Assert.Equal(0, order.Rows);
        Assert.True(order.Summarize && order.MinBends);
    }

    [Fact]
    public void HandlesAreANetworksIdAPieceOrIdAndPort()
    {
        List<OptionProblem> problems = new();

        RouteOrder order = Route("""
            {"kind": "cable", "start": {"at": [1, 2, 3]}, "end": {"at": [4, 2, 3]},
            "join_to": {"id": "1378", "port": 1}, "allow_bridge": ["5001", {"id": "1378", "port": 0}],
            "avoid_networks": ["5002"], "root": "88", "join_trunk": true}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal(("1378", "1"), (order.JoinTo!.Id, order.JoinTo.Port));
        Assert.Equal(new[] { "5001", "1378" }, order.AllowBridge.Select(target => target.Id));
        Assert.Equal("5002", Assert.Single(order.AvoidNetworks).Id);
        Assert.False(order.AvoidAllNetworks);
        Assert.Equal(88L, order.Root!.Value.Value);
        Assert.True(order.JoinTrunk);

        List<string> paths = Paths("""
            {"kind": "cable", "start": {"at": [1, 2, 3]}, "end": {"at": [4, 2, 3]},
            "join_to": {"port": 1}, "allow_bridge": ["5001", {"reference_id": "2"}], "avoid_networks": [true],
            "root": "pump"}
            """);
        Assert.Equal(new[] { "allow_bridge[1]", "avoid_networks[0]", "join_to", "root" },
            paths.OrderBy(path => path, System.StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("""{"kind": "cable", "end": {"at": [1, 2, 3]}}""", "start")]
    [InlineData("""{"kind": "cable", "start": {"at": [1, 2, 3]}}""", "end")]
    [InlineData("""{"kind": "cable", "start": {"id": "1"}, "reroute": {"id": ["2"]}}""", "reroute")]
    [InlineData("""{"kind": "cable", "reroute": {"id": ["2"], "between": ["1", "2"]}}""", "reroute")]
    [InlineData("""{"kind": "cable", "reroute": {"between": ["1"]}}""", "reroute.between")]
    [InlineData("""{"kind": "cable", "reroute": {"id": ["2", "x"]}}""", "reroute.id[1]")]
    [InlineData("""{"kind": "cable", "trunk": {"waypoints": [[0, 0, 0], [2, 0, 0]]}}""", "trunk")]
    [InlineData("""{"kind": "cable", "start": {"at": [1, 2, 3]}, "trunk": {"cells": [], "waypoints": []}}""", "trunk")]
    [InlineData("""{"kind": "cable", "start": {"at": [1, 2, 3]}, "end": {"at": [1, 2, 4]}, "reserve_ports": [{"id": "5"}]}""",
        "reserve_ports[0].port")]
    [InlineData("""{"kind": "cable", "start": {"at": [1, 2, 3]}, "end": {"at": [1, 2, 4]}, "reserve_ports": [{"port": 1}]}""",
        "reserve_ports[0]")]
    [InlineData("""{"kind": "cable", "start": {"at": [1, 2, 3]}, "end": {"at": [1, 2, 4]}, "assume_removed": ["9", "nine"]}""",
        "assume_removed[1]")]
    public void TheEndsAreGivenOneWay(string json, string path)
    {
        Assert.Equal(path, Paths(json)[0]);
    }

    [Fact]
    public void ARerouteOrTrunkStandsInForTheEnds()
    {
        List<OptionProblem> problems = new();

        RouteOrder reroute = Route("""{"kind": "chute", "reroute": {"between": [{"id": "1", "port": 2}, "3"]}}""",
            problems)!;
        RouteOrder pieces = Route("""{"kind": "chute", "reroute": {"id": "4"}}""", problems)!;
        RouteOrder trunk = Route("""
            {"kind": "cable", "start": [{"id": "1", "port": 0}, {"at": [1, 2, 3]}],
            "trunk": {"cells": [[0, 0, 0], [0.5, 0, 0]]}, "reserve_cells": [[1, 1, 1]],
            "reserve_ports": [{"id": "5", "port": 2}], "assume_removed": ["9"]}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal(2, reroute.RerouteBetween![0].Port);
        Assert.Equal(3L, reroute.RerouteBetween[1].Id.Value);
        Assert.Equal(4L, Assert.Single(pieces.ReroutePieces!).Value);
        Assert.True(trunk.TrunkByCells);
        Assert.True(trunk.Reserves);
        Assert.Equal((5L, 2), (trunk.ReservePorts[0].Id.Value, trunk.ReservePorts[0].Port));
        Assert.Equal(9L, Assert.Single(trunk.AssumeRemoved).Value);
    }

    [Fact]
    public void BuildArgumentsAreBuildsOptions()
    {
        List<OptionProblem> problems = new();
        RouteOrder order = Route("""
            {"kind": "pipe", "grade": "gas", "start": {"at": [1, 2, 3]},
            "end": {"at": [4, 2, 3]}, "join": "all", "allow": ["split"], "split_long": false, "materials": "720",
            "root": "88", "join_trunk": true, "allow_bridge": ["5001"]}
            """, problems)!;
        RouteTree tree = new RouteTree(new List<GridCell> { Cell(0), Cell(1), Cell(2) }, 3.0, 9);

        JObject build = RouteBuildArguments.Of(order, "gas", tree, new[] { new ThingId(31) },
            new[] { new ThingId(32) }, new NetworkTarget("1378", "1"),
            cell => new Vec3(cell.X / 10.0 + 0.004, 1.0 / 3.0, 0));

        Assert.Empty(problems);
        Assert.Equal(new[] { "what", "waypoints", "grade", "join", "remove_ids", "assume_removed", "allow_bridge",
            "allow", "split_long", "materials", "root", "join_to", "join_trunk" }, build.Properties().Select(key => key.Name));
        Assert.Equal("pipe", (string?)build["what"]);
        Assert.Equal("[[0.0,0.33,0.0],[1.0,0.33,0.0]]", build["waypoints"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("all", (string?)build["join"]);
        Assert.Equal("31", (string?)build["remove_ids"]![0]);
        Assert.Equal("32", (string?)build["assume_removed"]![0]);
        Assert.False((bool)build["split_long"]!);
        Assert.Equal("720", (string?)build["materials"]);
        Assert.Equal("88", (string?)build["root"]);
        Assert.Equal("{\"id\":\"1378\",\"port\":1}", build["join_to"]!.ToString(Newtonsoft.Json.Formatting.None));

        RouteOrder plain = Route("""{"kind": "cable", "start": {"at": [1, 2, 3]}, "end": {"at": [4, 2, 3]}}""",
            problems)!;
        JObject least = RouteBuildArguments.Of(plain, "heavy", tree, new ThingId[0], new ThingId[0], null,
            cell => new Vec3(0, 0, 0));
        Assert.Equal(new[] { "what", "waypoints", "grade", "join" }, least.Properties().Select(key => key.Name));
        Assert.Equal("ends", (string?)least["join"]);
    }

    private static GridCell Cell(int x) => new GridCell(x * GridStep.CellSize, 0, 0);

    private static PlanRemovalOptions Removal(string json) =>
        PlanRemovalOptions.Bind(new OptionReader(PlanRemovalOptions.Spec, JObject.Parse(json)));

    private static string? RemovalForm(string json, List<OptionProblem> problems, out VerbError? missing)
    {
        PlanRemovalOptions options = Removal(json);
        return RemoveRunOrders.Form(options.Selector.Ids != null, options.Waypoints, options.Cells,
            options.Selector.Network != null, "plan removal", "cable", problems, out missing);
    }

    [Theory]
    [InlineData("""{"id": ["3763565", "3763566"]}""", "id")]
    [InlineData("""{"waypoints": [[0, 0, 0], [2, 0, 0]]}""", "waypoints")]
    [InlineData("""{"cells": [[0, 0, 0], [0.5, 0, 0]]}""", "cells")]
    [InlineData("""{"network": "3768865"}""", "network")]
    [InlineData("""{"network": {"id": "720259", "port": 1}}""", "network")]
    public void ARemovalIsPricedByIdWaypointsCellsOrNetwork(string json, string form)
    {
        List<OptionProblem> problems = new();

        Assert.Equal(form, RemovalForm(json, problems, out VerbError? missing));
        Assert.Null(missing);
        Assert.Empty(problems);
    }

    [Fact]
    public void TwoWaysIsAProblemAndNoneIsMissingTargetNamingNetwork()
    {
        List<OptionProblem> problems = new();

        Assert.Null(RemovalForm("""{"id": "1", "network": "2"}""", problems, out VerbError? missing));
        Assert.Null(missing);
        OptionProblem two = Assert.Single(problems);
        Assert.Equal("network", two.Path);
        Assert.Contains("by id, waypoints, cells or network", two.Problem);

        problems.Clear();
        Assert.Null(RemovalForm("{}", problems, out missing));
        Assert.Empty(problems);
        Assert.Equal("missing_target", missing!.Code);
        Assert.Equal("plan removal: name the cable pieces by id, waypoints, cells or network.", missing.Message);
        Assert.Equal(new[] { "id", "waypoints", "cells", "network" },
            missing.Data!["options"]!.Select(option => (string)option!));
    }

    [Fact]
    public void RemoveStillNamesItsThreeWays()
    {
        List<OptionProblem> problems = new();

        Assert.Null(RemoveRunOrders.Form(false, null, null, "remove cable", "cable", problems, out VerbError? missing));
        Assert.Equal("remove cable: name the cable pieces by id, waypoints or cells.", missing!.Message);
        Assert.Equal(3, missing.Data!["options"]!.Count());
    }

    [Fact]
    public void ARemovalDefaultsToCableAndFiveRows()
    {
        PlanRemovalOptions options = Removal("""{"network": "3768865"}""");

        Assert.Equal("cable", options.Kind);
        Assert.Equal(5L, options.Rows);
        Assert.Equal(0L, Removal("""{"network": "3768865", "rows": 0}""").Rows);
    }

    [Fact]
    public void ARemovalReplyHasNoJobKeys()
    {
        JObject body = JobReports.Body(new { tool = "plan_removal", status = "dry_run", job_id = (string?)null,
            ready = false, removed_count = 2 }, true);

        Assert.Equal(new[] { "ready", "removed_count" }, body.Properties().Select(key => key.Name));
        Assert.True((bool)body["ready"]!);
    }
}
