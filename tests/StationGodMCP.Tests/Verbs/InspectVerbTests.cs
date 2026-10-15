#nullable enable

using StationGodMCP.Api.Shared;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The inspect whats as their handlers read their options (what each reads needs the game): ids one or a list, the
/// parts include adds, the catalogue's defaults, prefab's whole names, health's two forms and the lists that page by
/// offset only.
/// </summary>
public sealed class InspectVerbTests
{
    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("inspect")!, JObject.Parse(options), out _);

    private static OptionReader Reader(WhatSpec what, string options) => new OptionReader(what, JObject.Parse(options));

    [Fact]
    public void AListOfIdsAnswersRowsAndOneIdItsOwnShape()
    {
        PerIdList one = InspectDeviceOptions.Bind(Reader(InspectDeviceOptions.Spec, """{"id": "5"}""")).Selector.Ids!;
        PerIdList many = InspectDeviceOptions.Bind(Reader(InspectDeviceOptions.Spec, """{"id": ["5", "looking_at"]}"""))
            .Selector.Ids!;

        Assert.False(one.AnswersRows);
        Assert.True(many.AnswersRows);
        Assert.Equal(new[] { "5", "looking_at" }, many.Ids);
        Assert.NotNull(Check("""{"what": "device"}"""));
    }

    [Fact]
    public void IncludeNamesTheWhatsOwnPartsOnly()
    {
        Assert.True(InspectDeviceOptions.Bind(Reader(InspectDeviceOptions.Spec,
            """{"id": "5", "include": ["logic_types"]}""")).Fields.Includes("logic_types"));
        Assert.Null(Check("""{"what": "prefab", "prefab": "StructureFrame", "include": ["small_cells"]}"""));
        Assert.NotNull(Check("""{"what": "prefab", "prefab": "StructureFrame", "include": ["cells"]}"""));
        Assert.Null(Check("""{"what": "looking_at", "include": ["hit", "body"]}"""));
        Assert.NotNull(Check("""{"what": "genes", "id": "5", "include": ["logic_types"]}"""));
    }

    [Fact]
    public void TheCatalogueDefaultsFillWhatACallLeavesOut()
    {
        InspectRocketOptions rocket = InspectRocketOptions.Bind(Reader(InspectRocketOptions.Spec, "{}"));
        InspectRocketOptions whole = InspectRocketOptions.Bind(Reader(InspectRocketOptions.Spec, """{"compact": false}"""));
        InspectScreenOptions screen = InspectScreenOptions.Bind(Reader(InspectScreenOptions.Spec, """{"id": "5"}"""));

        Assert.True(rocket.Compact);
        Assert.False(rocket.SelfTest);
        Assert.True(whole.SelfTest);
        Assert.Equal(10.0, InspectLookingAtOptions.Bind(Reader(InspectLookingAtOptions.Spec, "{}")).Distance);
        Assert.Equal(3, InspectContentsOptions.Bind(Reader(InspectContentsOptions.Spec, """{"id": "5"}""")).Depth);
        Assert.True(screen.ShownOnly);
        Assert.Equal(25, screen.Paging.Limit);
        Assert.Equal(8, InspectHealthOptions.Bind(Reader(InspectHealthOptions.Spec, """{"network": "7"}""")).Paging.Limit);
        Assert.Equal("pipe", InspectHealthOptions.Bind(Reader(InspectHealthOptions.Spec, """{"network": "7"}""")).Kind);
    }

    [Fact]
    public void PrefabIsAWholeNameOrAHash()
    {
        PrefabFilter named = PrefabFilter.Bind(Reader(InspectPrefabOptions.Spec, """{"prefab": "StructureFrame"}"""))!;
        PrefabFilter hashed = PrefabFilter.Bind(Reader(InspectPrefabOptions.Spec, """{"prefab": -1234}"""))!;
        PrefabFilter parts = PrefabFilter.Bind(Reader(InspectPrefabOptions.Spec, """{"prefab": "*Frame*"}"""))!;

        Assert.IsType<PrefabPattern.Whole>(named.Patterns[0]);
        Assert.Equal(-1234, Assert.IsType<PrefabPattern.Hash>(hashed.Patterns[0]).Value);
        Assert.IsType<PrefabPattern.Parts>(parts.Patterns[0]);
        Assert.NotNull(Check("""{"what": "prefab", "prefab": ["StructureFrame"]}"""));
    }

    [Fact]
    public void HealthReadsThingsByIdOrOneNetwork()
    {
        InspectHealthOptions network = InspectHealthOptions.Bind(Reader(InspectHealthOptions.Spec,
            """{"network": "7", "kind": "cable", "damaged_only": true}"""));
        InspectHealthOptions things = InspectHealthOptions.Bind(Reader(InspectHealthOptions.Spec, """{"id": ["1", "2"]}"""));

        Assert.Equal("7", network.Selector.Network!.Id);
        Assert.Null(network.Selector.Ids);
        Assert.True(network.DamagedOnly);
        Assert.Null(things.Selector.Network);
        Assert.True(things.Selector.Ids!.AnswersRows);
        Assert.NotNull(Check("""{"what": "health", "kind": "steam"}"""));
        Assert.True(InspectHealthOptions.Bind(Reader(InspectHealthOptions.Spec, """{"network": "7", "cursor": "open"}"""))
            .Paging.OpensCursor);
        Assert.Equal("k5", InspectHealthOptions.Bind(Reader(InspectHealthOptions.Spec, """{"network": "7", "cursor": "k5"}"""))
            .Paging.CursorId);
    }

    [Fact]
    public void ScreenElementsWalkOneScreenUnderACursor()
    {
        Pure.Verbs.Paging later = InspectScreenOptions.Bind(Reader(InspectScreenOptions.Spec, """{"id": "5", "cursor": "k3"}""")).Paging;
        Pure.Verbs.Paging opened = InspectScreenOptions.Bind(Reader(InspectScreenOptions.Spec, """{"id": "5", "cursor": "open"}""")).Paging;

        Assert.Null(KeyedPage.OneThing(later, 1));
        Assert.Equal("cursor", KeyedPage.OneThing(later, 3)!.Path);
        Assert.Null(KeyedPage.OneThing(opened, 3));
    }

    // ---- inspect chip's parts ----

    [Fact]
    public void ChipPartsDefaultToStatus()
    {
        Assert.Equal(new[] { "status" }, InspectChipOptions.Bind(Reader(InspectChipOptions.Spec, """{"id": "5"}""")).Parts);
        Assert.Equal(new[] { "source", "selectors" }, InspectChipOptions.Bind(Reader(InspectChipOptions.Spec,
            """{"id": "5", "parts": ["source", "selectors"]}""")).Parts);
    }

    [Fact]
    public void OnePartAnswersItsReplyAndSeveralAnswerEachByName()
    {
        JObject one = Answered(PartAnswers.Of("inspect chip", new[] { "status" },
            part => new JObject { ["line_number"] = 3 }));
        JObject several = Answered(PartAnswers.Of("inspect chip", new[] { "status", "source" },
            part => new JObject { ["part"] = part }));

        Assert.Equal(3, (int)one["line_number"]!);
        Assert.Equal(new[] { "status", "source" }, several.Properties().Select(key => key.Name));
        Assert.Equal("source", (string?)several["source"]!["part"]);
    }

    [Fact]
    public void EveryPartThatCannotBeAnsweredRefusesTheCallAtOnce()
    {
        List<string> read = new List<string>();
        VerbOutcome outcome = PartAnswers.Of("inspect chip", new[] { "source", "status", "selectors" }, part =>
        {
            read.Add(part);
            return part == "status"
                ? new JObject()
                : throw new Api.Shared.ApiException("no_programmable_chip", "Holder 5 has no chip.");
        });

        VerbError error = Assert.IsType<VerbOutcome.Refused>(outcome).Error;
        Assert.Equal(new[] { "source", "status", "selectors" }, read);
        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal(new[] { "parts[0]", "parts[2]" },
            ((JArray)error.Data!["problems"]!).Select(problem => (string?)problem["path"]));
        Assert.Contains("source cannot be answered (no_programmable_chip): Holder 5 has no chip.", error.Message);
        Assert.Contains("selectors cannot be answered", error.Message);
    }

    private static JObject Answered(VerbOutcome outcome) => Assert.IsType<VerbOutcome.Answered>(outcome).Reply;

    // ---- inspect job ----

    [Fact]
    public void TheHubsJobsAreRefusedInTheHubOnlyFormAndTheRestGoToTheGame()
    {
        VerbError none = HubJobs.Refusal(InspectJobOptions.Spec, null)!;
        VerbError runs = HubJobs.Refusal(InspectJobOptions.Spec, InspectJobOptions.Bind(Reader(InspectJobOptions.Spec,
            """{"id": ["place-4", "script-14", "write-2"]}""")).Selector.Ids)!;

        Assert.Equal("invalid_argument", none.Code);
        Assert.Contains("taken by the MCP server (or sg), not by the game", none.Message);
        Assert.Equal(new[] { "id[1]", "id[2]" }, ((JArray)runs.Data!["problems"]!).Select(problem => (string?)problem["path"]));
        Assert.Null(HubJobs.Refusal(InspectJobOptions.Spec, InspectJobOptions.Bind(Reader(InspectJobOptions.Spec,
            """{"id": "place-4"}""")).Selector.Ids));
        Assert.Null(HubJobs.Refusal(InspectJobOptions.Spec, InspectJobOptions.Bind(Reader(InspectJobOptions.Spec,
            """{"id": "paste", "verbose": true}""")).Selector.Ids));
        Assert.True(InspectJobOptions.Bind(Reader(InspectJobOptions.Spec, """{"id": "7", "verbose": true}""")).Fields.Verbose);
    }
}
