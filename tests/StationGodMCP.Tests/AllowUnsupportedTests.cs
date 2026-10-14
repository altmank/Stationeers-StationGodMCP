#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// remove_structure allow_unsupported (LU 2026-10-05): a frame under a device that rests on nothing else (a solar
/// panel on a frame) is refused as has_mounted by default. The game lets a player deconstruct it and checks support
/// only when a thing is placed, so with allow_unsupported it goes and warns left_unsupported: the device stays and
/// works but cannot be rebuilt in place until something supports it again. A device the game counts as attached is
/// taken down with the piece, so allow_unsupported does not lift that.
/// </summary>
public sealed class AllowUnsupportedTests
{
    private const string Panel = "Solar Panel (StructureSolarPanel 4101)";

    private static readonly RemovalAllowance None = new(false, false);

    private static readonly RemovalAllowance Unsupported = new(false, false, unsupported: true);

    private static readonly RemovalAllowance EveryOther = new(true, true, broken: true, burst: true);

    // The order remove structure's options give, with 4100 read as its one reference id.
    private static RemoveStructureOrder? Order(string json, List<OptionProblem> problems) =>
        RemoveStructureOrder.Of(RemoveStructureOptions.Bind(new OptionReader(RemoveStructureOptions.Spec,
            JObject.Parse(json))), new List<ThingId?> { new ThingId(4100) }, new List<ReportProblem>(), problems);

    [Fact]
    public void ByDefaultAnUnsupportedDeviceRefusesAndNamesTheOverride()
    {
        GuardFinding refused = Assert.Single(RemovalRule.Judge(new RemovalFacts { Unsupported = Panel }, None));

        Assert.Equal("has_mounted", refused.Code);
        Assert.Equal(GuardLevel.Refusal, refused.Level);
        Assert.Contains(Panel, refused.Message);
        Assert.Contains("pass allow_unsupported", refused.Message);
        Assert.Contains("keeps working", refused.Message);
    }

    [Fact]
    public void NoOtherAllowFlagLiftsIt()
    {
        List<GuardFinding> findings = RemovalRule.Judge(new RemovalFacts { Unsupported = Panel }, EveryOther);

        Assert.Equal("has_mounted", Assert.Single(findings).Code);
        Assert.True(RemovalRule.Refused(findings));
    }

    [Fact]
    public void AllowedItWarnsThatTheDeviceStaysWorksAndCannotBeRebuiltInPlace()
    {
        List<GuardFinding> findings = RemovalRule.Judge(new RemovalFacts { Unsupported = Panel }, Unsupported);
        GuardFinding warning = Assert.Single(findings);

        Assert.Equal(RemovalRule.LeftUnsupported, warning.Code);
        Assert.Equal(GuardLevel.Warning, warning.Level);
        Assert.Contains(Panel, warning.Message);
        Assert.Contains("allow_unsupported is set", warning.Message);
        Assert.Contains("stays where it is and keeps working", warning.Message);
        Assert.Contains("cannot be rebuilt in place until something supports it again", warning.Message);
        Assert.Contains("not_replaceable", warning.Message);
        Assert.False(RemovalRule.Refused(findings));
    }

    [Fact]
    public void AnAttachedDeviceStaysRefusedWhateverIsAllowed()
    {
        RemovalAllowance all = new(true, true, true, true, true);
        RemovalFacts facts = new() { Attached = "Pipe Analyzer (StructurePipeAnalysizer 7)", Unsupported = Panel };

        GuardFinding refused = Assert.Single(RemovalRule.Judge(facts, all));

        Assert.Equal("has_mounted", refused.Code);
        Assert.Equal(GuardLevel.Refusal, refused.Level);
        Assert.Contains("takes it down with it", refused.Message);
        Assert.DoesNotContain("allow_unsupported", refused.Message);
    }

    [Fact]
    public void TheOtherGuardsStillApplyWithAllowUnsupported()
    {
        RemovalFacts facts = new() { Unsupported = Panel, BreachKpa = 40, BreachWhere = "w" };
        facts.Items.Add("ItemIronIngot x5 (9)");

        List<GuardFinding> findings = RemovalRule.Judge(facts, Unsupported);

        Assert.Equal(new[] { RemovalRule.LeftUnsupported, "holds_items", "would_breach" },
            findings.Select(finding => finding.Code));
        Assert.True(RemovalRule.Refused(findings));
    }

    [Fact]
    public void RemovalsReadAllowUnsupported()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        RemoveStructureOrder lifted = Order("{\"id\": \"4100\", \"allow\": [\"unsupported\"]}", problems)!;

        Assert.Empty(problems);
        Assert.True(lifted.Allow.Unsupported);
        Assert.False(lifted.Allow.Broken);

        problems.Clear();
        RemoveStructureOrder plain = Order("{\"id\": \"4100\"}", problems)!;
        Assert.False(plain.Allow.Unsupported);
    }
}
