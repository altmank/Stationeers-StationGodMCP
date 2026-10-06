#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using StationGodMCP.Tests.Budget;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// From live use on a big base: find_things and find_items narrowed to one deck (an exact prefab and an area, near
/// with radius_m), describe_prefab without its cell list by default, and the plan route tools' dry run in short.
/// </summary>
public sealed class FindFiltersAndSlimRepliesTests
{
    // ---- prefab and prefab_contains ----

    [Fact]
    public void AnExactPrefabKeepsThatPrefabOnlyInAnyCase()
    {
        PrefabMatch frame = new PrefabMatch("StructureFrame", null);

        Assert.True(frame.Keeps("StructureFrame"));
        Assert.True(frame.Keeps("structureframe"));
        Assert.False(frame.Keeps("StructureFrameCorner"));
        Assert.False(frame.Keeps(null));
    }

    [Fact]
    public void PrefabAndPrefabContainsMustBothMatch()
    {
        Assert.True(new PrefabMatch("StructureFrame", "Frame").Keeps("StructureFrame"));
        Assert.False(new PrefabMatch("StructureFrame", "Wall").Keeps("StructureFrame"));
        Assert.True(new PrefabMatch(null, "frame").Keeps("StructureFrameCorner"));
    }

    [Fact]
    public void NeitherKeepsEveryName()
    {
        Assert.True(PrefabMatch.Any.Keeps("Anything"));
        Assert.True(new PrefabMatch("", "").Keeps(null));
    }

    [Fact]
    public void ThePrefabArgumentIsTrimmed()
    {
        PrefabMatch read = PrefabMatches.Parse(new Args(JObject.Parse("""{"prefab": " StructureFrame "}""")));

        Assert.Equal("StructureFrame", read.Exact);
        Assert.Null(read.Contains);
    }

    // ---- the area ----

    [Fact]
    public void NearWithRadiusKeepsTheSphere()
    {
        PointArea deck = AreaArgs.Parse(new Args(JObject.Parse("""{"near": [100, 20, 50], "radius_m": 9}""")));

        Assert.True(deck.Contains(new Vec3(107, 20, 50)));
        Assert.False(deck.Contains(new Vec3(110, 20, 50)));
    }

    [Theory]
    [InlineData("""{"near": [1, 2, 3]}""", "near needs radius_m")]
    [InlineData("""{"radius_m": 5}""", "radius_m needs near")]
    [InlineData("""{"near": [1, 2, 3], "radius_m": 5, "min": [0, 0, 0], "max": [1, 1, 1]}""", "not both")]
    [InlineData("""{"near": [1, 2, 3], "radius_m": 5000}""", "at most 1000")]
    public void AnIncompleteOrDoubleAreaIsRefused(string arguments, string reason)
    {
        ApiException refused = Assert.Throws<ApiException>(() => AreaArgs.Parse(new Args(JObject.Parse(arguments))));

        Assert.Equal(ApiErrors.InvalidArgumentCode, refused.Code);
        Assert.Contains(reason, refused.Message);
    }

    [Theory]
    [InlineData("find_things")]
    [InlineData("find_items")]
    [InlineData("item_totals")]
    public void TheListToolsTakeAPrefabAndAnArea(string tool)
    {
        Assert.Empty(Problems(tool, """{"prefab": "StructureFrame", "near": [100, 20, 50], "radius_m": 9}"""));
        Assert.Empty(Problems(tool, """{"prefab_contains": "Frame", "min": [90, 15, 40], "max": [105, 25, 50]}"""));
        Assert.Single(Problems(tool, """{"near": [1, 2, 3], "radius_m": "far"}"""));
    }

    [Fact]
    public void FindItemsKeepsItsLocations()
    {
        Assert.Empty(Problems("find_items", """{"prefab_contains": "Ingot", "location": "ground"}"""));
        Assert.Empty(Problems("find_items", """{"location": "player"}"""));
        Assert.Empty(Problems("find_items", """{"location": "stored"}"""));
    }

    // ---- describe_prefab ----

    [Fact]
    public void DescribePrefabCountsItsCellsAndListsThemOnlyWhenAsked()
    {
        ReplyShape[] shapes = ReplyShapes.ByMethod["describe_prefab"];
        JObject plain = JObject.Parse(ReplyBudgetTests.Measure(shapes[0]).Json);
        JObject withCells = JObject.Parse(ReplyBudgetTests.Measure(shapes[1]).Json);

        Assert.Null(plain["small_cells"]);
        Assert.NotNull(plain["small_cell_count"]);
        Assert.NotNull(plain["grid_box"]);
        Assert.Equal(16, ((JArray)withCells["small_cells"]!).Count);
        Assert.Empty(Problems("describe_prefab", """{"prefab": "StructureFrame", "include_small_cells": true}"""));
    }

    // ---- plan route summary ----

    [Fact]
    public void TheSummaryKeepsProblemsWholeAndWarningsAsCodes()
    {
        RunSummaryView summary = RunSummaryView.Of(Report());

        Assert.False(summary.Ready);
        Assert.Equal("blocked", Assert.Single(summary.Problems).Code);
        Assert.Equal(new[] { "open_end", "through_air" }, summary.Warnings);
        Assert.Equal(12, summary.CellsTotal);
        Assert.Equal(10, summary.Placed);
        RunNeedView need = Assert.Single(summary.Needed);
        Assert.Equal(("ItemCableCoilHeavy", 10, 4), (need.PrefabName, need.Needed, need.Available));
    }

    [Fact]
    public void ASummaryReplyCarriesNoDryRunAndNoCells()
    {
        PlanRouteView view = new PlanRouteView("plan_cable_route", null, null, new JObject(), null,
            new List<string>(), RunSummaryView.Of(Report()));
        JObject json = JObject.Parse(WireCheck.New(view));

        Assert.Null(json["dry_run"]);
        Assert.NotNull(json["dry_run_summary"]);
        Assert.Null(json["dry_run_summary"]!["cells"]);
    }

    [Theory]
    [InlineData("plan_cable_route")]
    [InlineData("plan_pipe_route")]
    [InlineData("plan_chute_route")]
    public void TheSummaryIsAFractionOfTheWholeDryRun(string tool)
    {
        ReplyShape[] shapes = ReplyShapes.ByMethod[tool];
        IReadOnlyDictionary<string, int> limits = ReplyBudgetTests.DefaultLimitsOf(tool);
        int whole = ReplyBudgetTests.Measure(shapes[0].WithTopLimits(limits)).Bytes;
        int summary = ReplyBudgetTests.Measure(shapes[1].WithTopLimits(limits)).Bytes;

        Assert.True(summary * 2 < whole, $"{tool}: summary {summary} bytes, whole {whole} bytes");
        Assert.Empty(Problems(tool, """{"summary": true}""").Where(problem => problem.Contains("summary")));
    }

    private static RunReportView Report() => new RunReportView(
        new RunHeaderView("place_cables", "dry_run", null, "heavy", null),
        new RunCellsView(new List<RunCellView>(), 12, 10, 2, 0, new List<RunRemovalView>(), 0),
        new RunMaterialsView(null,
            new List<UpgradeCoilView>
            {
                new UpgradeCoilView("ItemCableCoilHeavy", "Heavy Cable Coil", 10, 4, new List<UpgradeStackView>())
            }, false, new List<UpgradeAmountView>()),
        new RunNetworksView(new List<object>(), new List<RunNetworkAfterView>(), new List<RunBridgeView>(),
            new List<RunSplitView>(), null),
        new List<RunIssueView> { new RunIssueView("blocked", "a wall at (1, 2, 3)", null, null) },
        new List<RunIssueView>
        {
            new RunIssueView("open_end", "a", null, null),
            new RunIssueView("open_end", "b", null, null),
            new RunIssueView("through_air", "c", null, null)
        });

    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }
}
