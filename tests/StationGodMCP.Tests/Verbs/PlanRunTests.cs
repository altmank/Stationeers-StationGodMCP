#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// plan removal without the game: its pieces are named one way, by id, waypoints, cells or network; two ways is a
/// problem, none is missing_target naming all four; kind is cable and rows 5 when left out, and rows is its own option.
/// </summary>
public sealed class PlanRunTests
{
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
