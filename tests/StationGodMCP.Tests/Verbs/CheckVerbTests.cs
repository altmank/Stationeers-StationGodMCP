#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The check whats as their handlers read their options (the lint world and the cursor need the game): layout's room
/// or box, its 4000-cell cap, codes and the things it narrows to; rules' action and explain's one subject.
/// </summary>
public sealed class CheckVerbTests
{
    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("check")!, JObject.Parse(options), out _);

    private static LayoutCheck? Layout(string json, List<OptionProblem> problems) =>
        LayoutCheck.Of(CheckLayoutOptions.Bind(new OptionReader(CheckLayoutOptions.Spec, JObject.Parse(json))), problems);

    private static RuleCheck? Rules(string json, List<OptionProblem> problems) =>
        RuleCheck.Of(CheckRulesOptions.Bind(new OptionReader(CheckRulesOptions.Spec, JObject.Parse(json))), problems);

    // ---- layout ----

    [Fact]
    public void LayoutLintsARoomOrABoxListingTwentyFiveFindings()
    {
        List<OptionProblem> problems = new();

        LayoutCheck room = Layout("""{"room": " 28 "}""", problems)!;
        LayoutCheck box = Layout("""{"box": [[4, 0, 4], [0, 4, 0]]}""", problems)!;

        Assert.Empty(problems);
        Assert.Equal(28, Assert.IsType<LayoutRegion.Room>(room.Region).Id);
        Assert.Equal(new Vec3(0, 4, 0), Assert.IsType<LayoutRegion.Box>(box.Region).Corners.Second);
        Assert.Equal(25, room.Limit);
        Assert.False(room.Codes.Narrows);
        Assert.False(room.Things.Narrows);
    }

    [Theory]
    [InlineData("{}", "room", "or box is needed: the cells to check")]
    [InlineData("""{"room": "28", "box": [[0, 0, 0], [2, 2, 2]]}""", "room", "goes without box")]
    [InlineData("""{"room": "kitchen"}""", "room", "is a room's id, as find room lists it")]
    [InlineData("""{"box": [[0, 0, 0], [40, 40, 40]]}""", "box", "holds 8000 2 m cells; at most 4000")]
    [InlineData("""{"room": "28", "codes": ["floating_run", " "]}""", "codes[1]", "is a lint code, as counts names them")]
    [InlineData("""{"room": "28", "id": ["9000", "pump"]}""", "id[1]", "is a thing's id")]
    [InlineData("""{"room": "28", "since": "latest"}""", "since", "is a thing's id")]
    public void LayoutRefusesWhatItCannotLint(string json, string path, string problem)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Layout(json, problems));

        OptionProblem only = Assert.Single(problems);
        Assert.Equal(path, only.Path);
        Assert.Equal(problem, only.Problem);
    }

    [Fact]
    public void LayoutNarrowsToCodesAndToTheThingsJustBuilt()
    {
        List<OptionProblem> problems = new();

        LayoutCheck check = Layout(
            """{"room": "28", "codes": ["floating_run", "controls_blocked"], "exclude_codes": "not_replaceable", "id": ["9000", "9001"], "since": "9500", "limit": 500}""",
            problems)!;

        Assert.Empty(problems);
        Assert.Equal(500, check.Limit);
        Assert.True(check.Codes.Narrows);
        Assert.True(check.Things.Narrows);
        List<LintFinding> findings = new()
        {
            LintTestKit.Finding("floating_run", "air", 9000, new Vec3(1, 1, 1)),
            LintTestKit.Finding("floating_run", "air", 42, new Vec3(1, 1, 3)),
            LintTestKit.Finding("controls_blocked", "wall", 9600, new Vec3(3, 1, 1)),
            LintTestKit.Finding("not_replaceable", "cladding", 9001, new Vec3(3, 1, 3))
        };
        Assert.Equal(new long?[] { 9000, 9600 },
            check.Things.Keep(check.Codes.Keep(findings)).Select(finding => finding.ThingId));
    }

    [Fact]
    public void LayoutNarrowsToAtMost256Things()
    {
        List<OptionProblem> problems = new();
        string ids = string.Join(", ", Enumerable.Range(1, 257).Select(id => $"\"{id}\""));

        Assert.Null(Layout($$"""{"room": "28", "id": [{{ids}}]}""", problems));
        Assert.Equal("narrows to at most 256 things", Assert.Single(problems).Problem);
    }

    [Fact]
    public void LayoutHasNoOffsetAndListsAtMost500()
    {
        Assert.NotNull(Check("""{"what": "layout", "room": "28", "offset": 25}"""));
        Assert.NotNull(Check("""{"what": "layout", "room": "28", "limit": 501}"""));
        Assert.Null(Check("""{"what": "layout", "room": "28", "limit": 500}"""));
    }

    // ---- rules ----

    [Fact]
    public void RulesListsByDefaultAndReadsRulesText()
    {
        List<OptionProblem> problems = new();

        RuleCheck list = Rules("{}", problems)!;
        RuleCheck one = Rules("""{"action": "test", "rule": " door_keepout ", "rules_text": "{}"}""", problems)!;

        Assert.Empty(problems);
        Assert.Equal(RuleCheck.List, list.Action);
        Assert.False(list.Full);
        Assert.Null(list.Subject);
        Assert.Equal("door_keepout", one.Rule);
        Assert.Equal("{}", one.RulesText);
    }

    [Fact]
    public void ExplainReadsOneRuleOnOneSubject()
    {
        List<OptionProblem> problems = new();

        RuleCheck port = Rules("""{"action": "explain", "rule": "port_stub", "id": "1199811", "port": 2, "other": "1199812"}""",
            problems)!;
        RuleCheck cell = Rules("""{"action": "explain", "rule": "floating_run", "at": [601, 211, 635]}""", problems)!;

        Assert.Empty(problems);
        Assert.Equal(1199811, port.Subject!.Id);
        Assert.Equal(2, port.Subject.Port);
        Assert.Equal(1199812, port.Subject.Other);
        Assert.Equal(new Vec3(601, 211, 635), cell.Subject!.At);
    }

    [Theory]
    [InlineData("""{"action": "explain", "id": "1199811"}""", "rule", "explain needs the rule to explain")]
    [InlineData("""{"action": "explain", "rule": "r"}""", "id", "or at is needed: explain reads a thing, network or room id, or a cell")]
    [InlineData("""{"action": "explain", "rule": "r", "id": ["1", "2"]}""", "id", "explain reads one subject: one thing, network or room id")]
    [InlineData("""{"action": "explain", "rule": "r", "at": [1, 2]}""", "at", "is a cell, [x, y, z]")]
    [InlineData("""{"action": "explain", "rule": "r", "id": "1", "other": "pump"}""", "other", "is the other thing's id")]
    public void ExplainRefusesWithoutOneSubject(string json, string path, string problem)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Rules(json, problems));

        OptionProblem only = Assert.Single(problems);
        Assert.Equal(path, only.Path);
        Assert.Equal(problem, only.Problem);
    }
}
