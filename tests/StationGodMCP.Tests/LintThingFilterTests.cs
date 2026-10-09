#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// lint_layout reference_ids and since_id: only the findings on the things a build just placed (as subject or as the
/// other thing named), composed with codes and exclude_codes; counts still counts every finding and filtered_out the
/// rest.
/// </summary>
public sealed class LintThingFilterTests(ITestOutputHelper output)
{
    [Fact]
    public void NoThingFilterKeepsEveryFinding()
    {
        Assert.False(LintThingFilter.Every.Narrows);
        Assert.Equal(5, LintThingFilter.Every.Keep(Findings()).Count);
    }

    [Fact]
    public void ReferenceIdsKeepFindingsNamingThemEitherWay()
    {
        List<LintFinding> kept = LintThingFilter.Of(new long[] { 4 }, null).Keep(Findings());

        Assert.Equal(new long?[] { 3, 4 }, kept.ConvertAll(finding => finding.ThingId));
    }

    [Fact]
    public void SinceIdKeepsThingsMadeSinceAndBothTogetherKeepEither()
    {
        Assert.Equal(new long?[] { 9000, 9001 },
            LintThingFilter.Of(null, 9000).Keep(Findings()).ConvertAll(finding => finding.ThingId));
        Assert.Equal(new long?[] { 1, 9000, 9001 },
            LintThingFilter.Of(new long[] { 1 }, 9000).Keep(Findings()).ConvertAll(finding => finding.ThingId));
    }

    [Fact]
    public void ItComposesWithTheCodeFilter()
    {
        List<LintFinding> kept = LintThingFilter.Of(null, 9000)
            .Keep(LintCodeFilter.Of(null, new[] { "floating_run" }).Keep(Findings()));

        Assert.Equal(new[] { "controls_blocked" }, kept.ConvertAll(finding => finding.Code));
    }

    [Fact]
    public void TheSidecarTakesReferenceIdsAndSinceId()
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            """{"room_id":"28","reference_ids":["9000","9001"],"since_id":"9000","exclude_codes":["not_replaceable"]}""");
        Assert.Empty(StationGodMCP.Server.ArgumentCheck.Problems(
            StationGodMCP.Server.Program.InputSchemas["lint_layout"], document.RootElement));
    }

    /// <summary>
    /// A room whose 90 standing findings (cladding pairs, floating runs) bury the two on the pump just placed: lint
    /// with since_id the pump's id lists those two, counts still names all of them.
    /// </summary>
    [Fact]
    public void LintingWhatWasJustBuiltListsOnlyItsFindings()
    {
        List<LintFinding> all = new List<LintFinding>();
        for (int index = 0; index < 90; index++)
        {
            all.Add(new LintFinding(index % 3 == 0 ? "floating_run" : "not_replaceable",
                "Composite Wall (StructureCompositeWall " + (2_200_000 + index) + ") could not be placed again: " +
                "Placement is blocked by Composite Wall", 2_200_000 + index, new Vec3(593 + index % 9 * 2, 211, 627),
                2_200_100 + index));
        }

        all.Add(new LintFinding("controls_blocked", "Turbo Volume Pump (StructureTurboVolumePump 2500001): the side " +
            "its controls face, +z, has a wall right in front of it.", 2_500_001, new Vec3(601, 211, 633)));
        all.Add(new LintFinding("floating_run", "Pipe (StructurePipeStraight 2500002) stands in air.", 2_500_002,
            new Vec3(601, 211, 635)));
        int before = Reply(all, LintCodeFilter.Every, LintThingFilter.Every, 500).Length;
        string after = Reply(all, LintCodeFilter.Every, LintThingFilter.Of(null, 2_500_001), 500);
        output.WriteLine($"lint_layout, 92 findings (limit 500): all {before} bytes, since_id {after.Length} bytes");

        JObject json = JObject.Parse(after);
        Assert.Equal(2, (int)json["total"]!);
        Assert.Equal(90, (int)json["filtered_out"]!);
        Assert.Equal(60, (int)json["counts"]!["not_replaceable"]!);
        Assert.True(after.Length * 10 <= before, $"since_id {after.Length} bytes against {before}");
    }

    private static string Reply(List<LintFinding> all, LintCodeFilter codes, LintThingFilter things, int limit)
    {
        List<LintFinding> kept = things.Keep(codes.Keep(LintReport.Ordered(all)));
        List<LintFindingView> views = kept.GetRange(0, System.Math.Min(limit, kept.Count))
            .ConvertAll(finding => new LintFindingView(finding));
        return WireCheck.New(new LintLayoutView("room 28 (40 cells)", 40, 120, 6, 90, 1, LintReport.Counts(all),
            views, kept.Count,
            new LintRuleSourceView(new LintRuleSet(new List<LintRule>(), new List<LintRuleError>(), new List<string>(),
                new List<string>())), 1.0, codes.Narrows || things.Narrows ? all.Count - kept.Count : null));
    }

    private static List<LintFinding> Findings() => new List<LintFinding>
    {
        new LintFinding("not_replaceable", "cladding", 1, new Vec3(1, 1, 1), 2),
        new LintFinding("not_replaceable", "triangle", 3, new Vec3(1, 1, 3), 4),
        new LintFinding("floating_run", "air", 4, new Vec3(3, 1, 1)),
        new LintFinding("controls_blocked", "pump", 9000, new Vec3(5, 1, 1), 7),
        new LintFinding("floating_run", "pipe", 9001, new Vec3(5, 1, 3))
    };
}
