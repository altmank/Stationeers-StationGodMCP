#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// lint_layout codes and exclude_codes: only the findings asked for are listed (a room's 76 not_replaceable cladding
/// pairs no longer bury the other 14), a rule_error goes with its rule, and counts still counts every finding.
/// </summary>
public sealed class LintCodeFilterTests
{
    [Fact]
    public void NoFilterKeepsEveryFinding()
    {
        Assert.Equal(4, LintCodeFilter.Every.Keep(Findings()).Count);
        Assert.False(LintCodeFilter.Every.Narrows);
    }

    [Fact]
    public void ExcludeCodesLeavesThoseOut()
    {
        List<LintFinding> kept = LintCodeFilter.Of(null, new[] { "not_replaceable" }).Keep(Findings());

        Assert.Equal(new[] { "floating_run" }, kept.ConvertAll(finding => finding.Code));
    }

    [Fact]
    public void CodesKeepOnlyThoseAndARuleErrorGoesWithItsRule()
    {
        List<LintFinding> kept = LintCodeFilter.Of(new[] { "not_replaceable" }, null).Keep(Findings());

        Assert.Equal(3, kept.Count);
        Assert.Contains(kept, finding => finding.Code == "rule_error");
    }

    [Fact]
    public void BothTogetherKeepTheNamedCodesLessTheExcluded()
    {
        LintCodeFilter filter = LintCodeFilter.Of(new[] { "not_replaceable", "floating_run" }, new[] { "floating_run" });

        Assert.True(filter.Narrows);
        Assert.Equal(3, filter.Keep(Findings()).Count);
    }

    [Fact]
    public void TheReplyCountsEveryFindingAndSaysHowManyWereLeftOut()
    {
        List<LintFinding> all = Findings();
        List<LintFinding> kept = LintCodeFilter.Of(new[] { "floating_run" }, null).Keep(all);
        JObject json = JObject.Parse(WireCheck.New(new LintLayoutView("room 28 (40 cells)", 40, 1, 0, 3, 0,
            LintReport.Counts(all), kept.ConvertAll(finding => new LintFindingView(finding)), kept.Count,
            new LintRuleSourceView(new LintRuleSet(new List<StationGodMCP.Pure.Lint.LintRule>(), new List<StationGodMCP.Pure.Lint.LintRuleError>(), new List<string>(), new List<string>())), 1.0, all.Count - kept.Count)));

        Assert.Equal(1, (int)json["total"]!);
        Assert.Equal(3, (int)json["filtered_out"]!);
        Assert.Equal(2, (int)json["counts"]!["not_replaceable"]!);
        Assert.False((bool)json["has_more"]!);
    }

    [Fact]
    public void TheSidecarTakesBothLists()
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            """{"room_id":"28","codes":["floating_run"],"exclude_codes":["not_replaceable"]}""");
        Assert.Empty(StationGodMCP.Server.ArgumentCheck.Problems(
            StationGodMCP.Server.Program.InputSchemas["lint_layout"], document.RootElement));
    }

    private static List<LintFinding> Findings() => new List<LintFinding>
    {
        new LintFinding("not_replaceable", "cladding", 1, new Vec3(1, 1, 1), 2),
        new LintFinding("not_replaceable", "triangle", 3, new Vec3(1, 1, 3), 4),
        new LintFinding("rule_error", ConflictLevel.Warning, 0, "failed", null, new Vec3(1, 1, 1), null, null, null,
            "not_replaceable"),
        new LintFinding("floating_run", "air", 5, new Vec3(3, 1, 1))
    };
}
