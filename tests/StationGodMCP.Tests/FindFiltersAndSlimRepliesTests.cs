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
/// From live use on a big base: the plan route tools' dry run in short.
/// </summary>
public sealed class FindFiltersAndSlimRepliesTests
{
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
    public void ASummarizedRouteCarriesTheReportInShortWithoutCellsOrJobKeys()
    {
        RunReportView report = Report();
        JObject json = new PlanRouteView("plan_cable_route", null, null, new JObject(), new List<string>())
            .Reply(PlanRouteView.SummaryKey, JobReports.Body(RunSummaryView.Of(report), report.Ready));

        Assert.Null(json["build_report"]);
        JObject summary = Assert.IsType<JObject>(json["build_report_summary"]);
        Assert.Null(summary["cells"]);
        Assert.Null(summary["status"]);
        Assert.False((bool)summary["ready"]!);
        Assert.Equal(12, (int)summary["cells_total"]!);
    }

    [Fact]
    public void AWholeRouteReportHasNoJobKeys()
    {
        RunReportView report = Report();
        JObject json = new PlanRouteView("plan_cable_route", null, null, new JObject(), new List<string>())
            .Reply(PlanRouteView.ReportKey, JobReports.Body(report, report.Ready));

        Assert.Null(json["build_report_summary"]);
        JObject whole = Assert.IsType<JObject>(json["build_report"]);
        Assert.Null(whole["tool"]);
        Assert.Null(whole["status"]);
        Assert.Null(whole["job_id"]);
        Assert.Equal("heavy", (string?)whole["grade"]);
        Assert.NotNull(whole["cells"]);
        Assert.Equal(new[] { "tool", "found", "build_arguments", "build_report", "notes" },
            json.Properties().Select(key => key.Name));
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
