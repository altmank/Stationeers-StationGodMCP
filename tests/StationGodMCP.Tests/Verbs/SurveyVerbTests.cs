#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The survey whats as their handlers read their options (the game's grid needs the game): area's place, network list
/// and defaults, paged by offset only; wall's free_rects; network's limit by form and its {id, port}; feeds' root, a
/// list of roots answering one row each.
/// </summary>
public sealed class SurveyVerbTests
{
    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("survey")!, JObject.Parse(options), out _);

    private static OptionReader Reader(WhatSpec what, string options) => new OptionReader(what, JObject.Parse(options));

    // ---- area ----

    [Fact]
    public void AreaTakesABoxOrARoomAndPagesEightCellsByOffset()
    {
        SurveyAreaOptions box = SurveyAreaOptions.Bind(Reader(SurveyAreaOptions.Spec,
            """{"box": [[0, 0, 0], [6, 4, 4]]}"""));
        SurveyAreaOptions room = SurveyAreaOptions.Bind(Reader(SurveyAreaOptions.Spec, """{"room": " 28 "}"""));

        Assert.Equal(new Vec3(6, 4, 4), box.Selector.Box!.Second);
        Assert.Null(box.Selector.Room);
        Assert.Equal("28", room.Selector.Room);
        Assert.Equal(8, box.Paging.Limit);
        Assert.Equal("occupancy", box.CellDetail);
        Assert.True(box.Networks);
        Assert.False(box.OccupiedOnly);
        Assert.Null(box.Paging.CursorId);
        Assert.Equal("k2", SurveyAreaOptions.Bind(Reader(SurveyAreaOptions.Spec, """{"cursor": "k2"}""")).Paging
            .CursorId);
        Assert.NotNull(Check("""{"what": "area", "room": "28", "limit": 126}"""));
    }

    [Fact]
    public void AreaNamesSeveralNetworksAndEachBadOneAtItsPlace()
    {
        OptionReader reader = Reader(SurveyAreaOptions.Spec, """{"network": ["17", "", {"id": "40", "port": 2}]}""");
        Selector selector = Selector.Bind(reader);

        Assert.Null(selector.Network);
        Assert.Equal(new[] { ("17", (string?)null), ("40", "2") }, selector.Networks!.Select(Fields));
        Assert.Equal("network[1]", Assert.Single(reader.Problems).Path);
        Assert.Null(Check("""{"what": "area", "room": "28", "network": ["17", "18"], "kind": ["pipe"]}"""));
    }

    [Fact]
    public void AreaTakesItsPartsAndIncludes()
    {
        SurveyAreaOptions options = SurveyAreaOptions.Bind(Reader(SurveyAreaOptions.Spec,
            """{"sections": ["pieces", "doors"], "include": ["refund"], "cell_detail": "full"}"""));
        SurveySections sections = SurveySections.Of(options.Sections);

        Assert.True(sections.Includes(SurveySection.Doors));
        Assert.False(sections.Includes(SurveySection.Cells));
        Assert.True(options.Fields.Includes("refund"));
        Assert.False(options.Fields.Includes("piece_cells"));
        Assert.Equal(SurveyCellDetail.Full, SurveyCellDetails.Of(options.CellDetail));
    }

    // ---- wall ----

    [Fact]
    public void WallMapsFourMetresAroundAndAtMostSixteen()
    {
        Assert.Equal(4.0, SurveyWallOptions.Bind(Reader(SurveyWallOptions.Spec, """{"plane": "z=668"}""")).HalfWidth);
        Assert.Null(Check("""{"what": "wall", "plane": "z=668", "side": "+z", "at": [700, 101, 668]}"""));
        Assert.NotNull(Check("""{"what": "wall", "plane": "z=668", "half_width": 17}"""));
        Assert.NotNull(Check("""{"what": "wall", "side": "+w"}"""));
    }

    [Fact]
    public void FreeRectsTakeTheirSizeAndDefaultToTwentyInOneWalledSection()
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        FreeRectRequest? request = FreeRectRequest.Of(JObject.Parse("""{"w": 1.5, "h": 2}"""), problems);

        Assert.Empty(problems);
        Assert.Equal((1.5, 2.0, true, true, FreeRectRequest.DefaultLimit), Fields(request));
        Assert.Equal((1.0, 1.0, false, true, 5),
            Fields(FreeRectRequest.Of(JObject.Parse("""{"w": 1, "h": 1, "one_section": false, "limit": 5}"""), problems)));
        Assert.Null(FreeRectRequest.Of(null, problems));
    }

    [Theory]
    [InlineData("""{"h": 2}""", "free_rects.w")]
    [InlineData("""{"w": 0, "h": 2}""", "free_rects.w")]
    [InlineData("""{"w": 1}""", "free_rects.h")]
    [InlineData("""{"w": 1, "h": 1, "limit": 51}""", "free_rects.limit")]
    public void AFreeRectRequestThatDoesNotFitIsAProblemAtItsPlace(string given, string path)
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        Assert.Null(FreeRectRequest.Of(JObject.Parse(given), problems));
        Assert.Equal(path, Assert.Single(problems).Path);
    }

    // ---- network ----

    [Theory]
    [InlineData("""{"network": "17"}""", 30)]
    [InlineData("""{"box": [[0, 0, 0], [8, 4, 8]]}""", 25)]
    [InlineData("""{"network": "17", "summarize": true}""", 10)]
    [InlineData("""{"network": "17", "limit": 3}""", 3)]
    public void NetworkPagesByItsForm(string options, int limit)
    {
        Assert.Equal(limit, SurveyNetworkOptions.Bind(Reader(SurveyNetworkOptions.Spec, options)).Paging.Limit);
    }

    [Fact]
    public void NetworkNamesANetworkAPieceADeviceOrADevicesPort()
    {
        Assert.Equal(("17", (string?)null),
            Fields(SurveyNetworkOptions.Bind(Reader(SurveyNetworkOptions.Spec, """{"network": " 17 "}""")).Selector.Network));
        Assert.Equal(("552", (string?)"1"),
            Fields(SurveyNetworkOptions.Bind(Reader(SurveyNetworkOptions.Spec, """{"network": {"id": "552", "port": 1}}"""))
                .Selector.Network));
        Assert.Null(Check("""{"what": "network", "network": "17", "kind": "pipe", "prefab": "*Valve*", "open_ends": true}"""));
        Assert.NotNull(Check("""{"what": "network", "network": "17", "kind": "wire"}"""));
        Assert.NotNull(Check("""{"what": "network", "network": "17", "radius": 1001, "near": [0, 0, 0]}"""));
    }

    // ---- feeds ----

    [Fact]
    public void FeedsNeedTheirRootAndTraceCableByDefault()
    {
        SurveyFeedsOptions feeds = SurveyFeedsOptions.Bind(Reader(SurveyFeedsOptions.Spec, """{"id": "551", "port": 2}"""));

        Assert.Equal("cable", feeds.Kind);
        Assert.Equal(2, feeds.Port);
        Assert.False(feeds.Selector.Ids!.AnswersRows);
        Assert.True(SurveyFeedsOptions.Bind(Reader(SurveyFeedsOptions.Spec, """{"id": ["551", "552"]}""")).Selector
            .Ids!.AnswersRows);
        Assert.NotNull(Check("""{"what": "feeds"}"""));
        Assert.NotNull(Check("""{"what": "feeds", "id": "551", "port": 65}"""));
    }

    [Fact]
    public void OneRootAnswersItsFeedsAndAListAnswersARowEach()
    {
        PerIdList one = SurveyFeedsOptions.Bind(Reader(SurveyFeedsOptions.Spec, """{"id": "551"}""")).Selector.Ids!;
        PerIdList two = SurveyFeedsOptions.Bind(Reader(SurveyFeedsOptions.Spec, """{"id": ["551", "9"]}""")).Selector.Ids!;
        Rows single = new Rows();
        single.Add("551", () => new JObject { ["network_id"] = "17" });
        Rows rows = new Rows();
        rows.Add("551", () => new JObject { ["network_id"] = "17" });
        rows.Add("9", () => throw new ApiException("thing_not_found", "No thing has id 9."));

        JObject feeds = Replies.Tree(Assert.IsType<VerbOutcome.Answered>(single.For(one)).Reply);
        JObject listed = Replies.Tree(Assert.IsType<VerbOutcome.Answered>(rows.For(two)).Reply);

        Assert.Equal("17", (string?)feeds["network_id"]);
        Assert.Null(feeds["results"]);
        Assert.Equal(2, (int)listed["count"]!);
        Assert.Equal("thing_not_found", (string?)listed["results"]![1]!["error"]!["code"]);
    }

    private static (double W, double H, bool OneSection, bool RequireWall, int Limit) Fields(FreeRectRequest? request) =>
        (request!.W, request.H, request.OneSection, request.RequireWall, request.Limit);

    private static (string Id, string? Port) Fields(NetworkTarget? network) => (network!.Id, network.Port);
}
