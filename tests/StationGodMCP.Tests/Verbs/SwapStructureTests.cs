#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// swap wall and swap frame without the game: how a call names its pieces (by id or by room, exactly one), wall's
/// required into, and the report's body and problems taken from the planned view.
/// </summary>
public sealed class SwapStructureTests
{
    private static SwapWallOptions Wall(string json) =>
        SwapWallOptions.Bind(new OptionReader(SwapWallOptions.Spec, JObject.Parse(json)));

    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("swap")!, JObject.Parse(options), out _);

    private static bool ByIds(string json, List<OptionProblem> problems, out StructureScope.Room? room,
        out VerbError? missing) =>
        StructureScopes.ByIds(Wall(json).Selector, "swap wall", "wall", problems, out room, out missing);

    [Fact]
    public void IdsOrARoomNameThePieces()
    {
        List<OptionProblem> problems = new();

        Assert.True(ByIds("""{"id": ["11", "12"], "into": "W"}""", problems, out _, out _));
        Assert.False(ByIds("""{"room": "4711", "into": "W"}""", problems, out StructureScope.Room? room, out _));
        Assert.Equal(4711L, room!.Id);
        Assert.Empty(problems);

        Assert.False(ByIds("""{"room": "kitchen", "into": "W"}""", problems, out room, out _));
        Assert.Null(room);
        Assert.Equal("is a room's id, as find room gives it", Assert.Single(problems).Problem);
    }

    [Fact]
    public void BothIsAProblemAndNeitherIsMissingTarget()
    {
        List<OptionProblem> problems = new();

        Assert.False(ByIds("""{"id": "2", "room": "1", "into": "W"}""", problems, out _, out VerbError? missing));
        Assert.Null(missing);
        Assert.Equal("room", Assert.Single(problems).Path);

        problems.Clear();
        Assert.False(ByIds("""{"into": "W"}""", problems, out _, out missing));
        Assert.Empty(problems);
        Assert.Equal("missing_target", missing!.Code);
        Assert.Equal(new[] { "id", "room" }, missing.Data!["options"]!.Select(option => (string)option!));
    }

    [Fact]
    public void AWallNeedsIntoAndAFrameDoesNot()
    {
        Assert.Equal("invalid_argument", Check("""{"what": "wall", "room": "1"}""")!.Code);
        Assert.Null(Check("""{"what": "frame", "room": "1"}"""));

        List<OptionProblem> problems = new();
        StructureScopes.Into("  ", problems);
        StructureScopes.Into("StructureCompositeWall", problems);
        Assert.Equal("into", Assert.Single(problems).Path);
    }

    [Fact]
    public void AReportsBodyIsThePlannedViewWithoutTheJobKeys()
    {
        StructureSwapReportView view = StructureSwapWireTests.Report(new List<UpgradeProblemView>());

        JObject body = Replies.Tree(JobReports.Body(view, ready: false));

        Assert.Null(body["tool"]);
        Assert.Null(body["status"]);
        Assert.Null(body["job_id"]);
        Assert.Equal("StructureCompositeWall", (string?)body["target"]);
        Assert.False((bool)body["ready"]!);
        Assert.Equal("target", body.Properties().First().Name);
    }

    [Fact]
    public void AProblemIsIndexedByTheIdItIsAbout()
    {
        List<ThingId?> named = new() { new ThingId(11), null, new ThingId(13) };

        List<ReportProblem> problems = JobReports.Problems(new[]
        {
            new UpgradeProblemView("thing_not_found", "No thing with reference id 13.", new ThingId(13)),
            new UpgradeProblemView("unmatched_pieces", "2 piece(s) cannot be swapped.")
        }, named);

        Assert.Equal("""{"index":2,"reference_id":"13","code":"thing_not_found","message":"No thing with reference id 13."}""",
            StationGodMCP.Api.Shared.ApiJson.WriteShared(problems[0]));
        Assert.Equal("""{"code":"unmatched_pieces","message":"2 piece(s) cannot be swapped."}""",
            StationGodMCP.Api.Shared.ApiJson.WriteShared(problems[1]));
    }
}
