#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// 1.7.0 smaller replies (live 2026-09-30: a 6 x 4 x 4 m grid_survey of 63 KB, a 22 KB Lua chip in every
/// get_ic_status, 20 KB place_structure polls): every switch leaves the default reply as it was where that was
/// promised, and each part left out is absent rather than empty.
/// </summary>
public sealed class HeavyPayloadTests
{
    private static readonly ThingView Chip = new ThingView(new ThingId(301), "ItemIntegratedCircuitLua", "Lua");

    [Fact]
    public void SectionsDefaultToEveryPart()
    {
        SurveySections sections = SurveySections.Parse(new Args(new JObject()));

        Assert.True(sections.Includes(SurveySection.All));
    }

    [Fact]
    public void SectionsKeepOnlyThePartsNamedIgnoringCase()
    {
        SurveySections sections = SurveySections.Parse(Args("""{"sections":["Pieces"," network_visibility "]}"""));

        Assert.True(sections.Includes(SurveySection.Pieces));
        Assert.True(sections.Includes(SurveySection.NetworkVisibility));
        Assert.False(sections.Includes(SurveySection.Cells));
        Assert.False(sections.Includes(SurveySection.Devices));
    }

    [Fact]
    public void AnUnknownSectionIsRefusedWithTheWordsItTakes()
    {
        ApiException refused =
            Assert.Throws<ApiException>(() => SurveySections.Parse(Args("""{"sections":["walls"]}""")));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains("cells, pieces, devices, networks, network_visibility, doors", refused.Message);
    }

    [Fact]
    public void ASurveyWithoutCellsLeavesCellsAndLegendOutButKeepsThePage()
    {
        SurveySections sections = SurveySections.Parse(Args("""{"sections":["pieces"]}"""));
        JObject json = JObject.Parse(WireCheck.New(new GridSurveyView(Page(), Contents(), sections, null)));

        Assert.Null(json["cells"]);
        Assert.Null(json["legend"]);
        Assert.Null(json["devices"]);
        Assert.Null(json["networks"]);
        Assert.Null(json["network_visibility"]);
        Assert.Null(json["doors"]);
        Assert.NotNull(json["pieces"]);
        Assert.Equal(1, (int)json["count"]!);
        Assert.Equal(1, (int)json["total"]!);
    }

    [Fact]
    public void TheDefaultSurveyKeepsEveryPartEvenWhenEmpty()
    {
        JObject json = JObject.Parse(WireCheck.New(new GridSurveyView(Page(), Contents(), SurveySections.All,
            "legend")));

        foreach (string part in new[] { "cells", "pieces", "devices", "networks", "network_visibility", "doors" })
        {
            Assert.NotNull(json[part]);
        }

        Assert.Equal("legend", (string?)json["legend"]);
        Assert.Equal(64, ((string?)json["cells"]![0]!["small"])!.Length);
    }

    [Fact]
    public void ANetworkFilterKeepsOnlyItsNetworksAndDropsPiecesOnNone()
    {
        SurveyNetworkFilter filter = SurveyNetworkFilter.Only(new HashSet<long> { 7 });

        Assert.True(filter.Admits(new ThingId(7)));
        Assert.False(filter.Admits(new ThingId(8)));
        Assert.False(filter.Admits(null));
        Assert.True(filter.AdmitsAny(new ThingId?[] { null, new ThingId(7) }));
        Assert.False(filter.AdmitsAny(new ThingId?[] { null }));
        Assert.True(SurveyNetworkFilter.Every.Admits(null));
    }

    [Fact]
    public void AStatusWithoutSourceStillCountsIt()
    {
        IcChip chip = new IcChip(IcChip.Lua, Chip, new string('x', 22000), null).WithoutSource();
        JObject json = JObject.Parse(WireCheck.New(new IcStatusView(
            new IcPlace("world", new ThingId(300), new ThingView(new ThingId(300), "StructureConsole", "Console")),
            chip, new ChipState(0.0, false, "", "None", "OK"), new IcHolderView("computer_board", true, true, true),
            new IcRuntimeParts(new List<IcPinView>(), IcWireRuntime()))));

        Assert.Equal(JTokenType.Null, json["source"]!.Type);
        Assert.Equal(22000, (int)json["source_length"]!);
    }

    [Fact]
    public void ALuaHubChipsDefaultStatusCarriesNeitherItsSourceNorAnIc10Runtime()
    {
        string source = "-- hub" + Environment.NewLine + new string('x', 60000);
        IcChip chip = new IcChip(IcChip.Lua, Chip, source, null).WithoutSource();
        string written = WireCheck.New(new IcStatusView(
            new IcPlace("world", new ThingId(300), new ThingView(new ThingId(300), "StructureConsole", "Console")),
            chip, new ChipState(0.0, false, "", "None", "OK"), new IcHolderView("computer_board", true, true, true),
            new IcRuntimeParts(new List<IcPinView>(), null)));
        JObject json = JObject.Parse(written);

        Assert.DoesNotContain("xxxxxxxxxx", written);
        Assert.Equal(JTokenType.Null, json["source"]!.Type);
        Assert.Equal(JTokenType.Null, json["runtime"]!.Type);
        Assert.Equal(source.Length, (int)json["source_length"]!);
        Assert.True(written.Length < 2048, $"{written.Length} bytes");
    }

    [Fact]
    public void HoldersLimitZeroLeavesTopHoldersOut()
    {
        JObject json = JObject.Parse(WireCheck.New(new PrefabTotalView("ItemIronIngot", "Iron", null, 2,
            new PlaceAmounts(80.0, 30.0, 50.0, 0.0, 0.0, 0.0), null)));

        Assert.Null(json["top_holders"]);
        Assert.Equal(80.0, (double)json["quantity"]!);
    }

    [Fact]
    public void ABriefBuildPollDropsPreflightAndFinalCheck()
    {
        BuildJobView job = new BuildJobView("place-3", "place_structure", "applied", new { big = true },
            new BuildJobResult(new { big = true }, new BuildLog(), new List<BuildCheckView>(), null));
        JObject json = JObject.Parse(WireCheck.New(BuildJobView.Brief(job)));

        Assert.Equal(JTokenType.Null, json["preflight"]!.Type);
        Assert.Equal(JTokenType.Null, json["result"]!["final_check"]!.Type);
        Assert.NotNull(json["result"]!["verification"]);
        Assert.Equal("applied", (string?)json["status"]);
    }

    [Fact]
    public void ARefusedBuildPollKeepsTheFinalCheckThatSaysWhy()
    {
        BuildJobView job = new BuildJobView("place-4", "place_structure", "refused", new { big = true },
            new BuildJobResult(new { problems = 1 }, new BuildLog(), new List<BuildCheckView>(),
                new ErrorView("final_check_failed", "x")));
        JObject json = JObject.Parse(WireCheck.New(BuildJobView.Brief(job)));

        Assert.Equal(JTokenType.Null, json["preflight"]!.Type);
        Assert.Equal(1, (int)json["result"]!["final_check"]!["problems"]!);
    }

    [Fact]
    public void ABriefPollPassesAnyOtherViewThrough()
    {
        JobQueuedView queued = new JobQueuedView("run-8", "place_structure", 1, "run-7", null);

        Assert.Same(queued, BuildJobView.Brief(queued));
        Assert.Same(queued, RunJobView.Brief(queued));
    }

    [Fact]
    public void LinksWithoutIncludeLinksAreCountsOnly()
    {
        UpgradeLinkView link = new UpgradeLinkView(new ThingView(new ThingId(1), "a", null),
            new ThingView(new ThingId(2), "b", null));
        RunLinksView links = new RunLinksView(3, 5, new List<UpgradeLinkView> { link, link },
            new List<UpgradeLinkView> { link }, new List<UpgradeLinkView>());
        JObject full = JObject.Parse(WireCheck.New(links));
        JObject brief = JObject.Parse(WireCheck.New(links.CountsOnly()));

        Assert.Equal(2, ((JArray)full["added"]!).Count);
        Assert.Null(brief["added"]);
        Assert.Null(brief["lost"]);
        Assert.Equal(2, (int)brief["added_count"]!);
        Assert.Equal(1, (int)brief["lost_count"]!);
        Assert.True((bool)brief["model_matches_game"]!);
    }

    [Fact]
    public void FootprintWithoutCellsKeepsTheCount()
    {
        List<GridCell> cells = new List<GridCell>();
        for (int index = 0; index < 36; index++)
        {
            cells.Add(new GridCell(index, 0, 0));
        }

        FootprintView footprint = new FootprintView(new CellListView(cells, 64),
            new List<PositionView> { new PositionView(1, 1, 1) },
            new BodyView(new Vec3(0, 0, 0), Box3.OfSmallCells(cells), Box3.OfSmallCells(cells), 36), null);
        JObject json = JObject.Parse(WireCheck.New(footprint.WithoutCells()));

        Assert.Equal(36, (int)json["small_cells"]!["count"]!);
        Assert.Null(json["small_cells"]!["cells"]);
        Assert.Null(json["large_cells"]);
        Assert.NotNull(json["body"]);
    }

    [Theory]
    [InlineData("grid_survey", """{"min":[0,0,0],"max":[6,4,4],"sections":["pieces","devices"],"network_ids":["17"],"cell_detail":"full"}""")]
    [InlineData("get_ic_status", """{"reference_id":"300","include_source":false}""")]
    [InlineData("item_totals", """{"prefab_contains":"Ingot","holders_limit":0}""")]
    [InlineData("place_structure", """{"job_id":"place-3","verbose":true}""")]
    [InlineData("place_structure", """{"prefab":"StructureFrame","at":[1,1,1],"include_footprint_cells":true}""")]
    [InlineData("remove_structure", """{"job_id":"remove-3","verbose":true}""")]
    [InlineData("place_pipes", """{"waypoints":[[1,1,1],[1,1,3]],"grade":"gas","include_links":true,"include_notes":true,"limit":0}""")]
    [InlineData("place_pipes", """{"job_id":"run-3","verbose":true}""")]
    [InlineData("plan_pipe_route", """{"from":{"at":[1,1,1]},"to":{"at":[1,1,3]},"grade":"gas","include_links":false,"limit":0}""")]
    [InlineData("plan_removal", """{"reference_ids":["5"],"include_notes":true,"limit":0}""")]
    public void TheSidecarTakesTheNewArguments(string tool, string arguments)
    {
        Assert.Empty(Problems(tool, arguments));
    }

    [Fact]
    public void TheSidecarRefusesAnUnknownSection()
    {
        Assert.Single(Problems("grid_survey", """{"min":[0,0,0],"max":[6,4,4],"sections":["walls"]}"""));
    }

    private static Args Args(string json) => new Args(JObject.Parse(json));

    private static Slice<SurveyCell> Page()
    {
        SurveyCellView cell = new SurveyCellView(new PositionView(1, 1, 1), null, null, new List<SurveyWallView>(),
            new string('.', 64), new string('a', 64));
        return Slice<SurveyCell>.Page(new List<SurveyCell> { cell },
            PageRequest.From(new Args(new JObject()), 27, 125), 1);
    }

    private static SurveyContents Contents() =>
        new SurveyContents(new List<SurveyPieceView>(), new List<SurveyDeviceView>(), new List<object>(),
            new List<SurveyNetworkVisibilityView>(), new List<SurveyDoorView>());

    private static IcRuntimeView IcWireRuntime() =>
        new IcRuntimeView(new IcExecutionView(0, string.Empty, false), new List<RegisterView>(),
            new SpecialRegisters(16, 0.0, 17, null),
            new StackWindowView(512, 0, new List<double>()),
            new IcSymbols(new List<AliasView>(), new List<DefineView>(), new List<JumpTagView>()));

    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }
}
