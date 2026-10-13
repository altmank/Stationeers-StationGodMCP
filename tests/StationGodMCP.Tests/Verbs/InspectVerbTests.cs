#nullable enable

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
        Assert.Equal(new PrefabPattern.Hash(-1234), hashed.Patterns[0]);
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
    }

    [Fact]
    public void ScreenElementsPageByOffsetOnly()
    {
        Pure.Verbs.Paging later = InspectScreenOptions.Bind(Reader(InspectScreenOptions.Spec, """{"id": "5", "cursor": "k3"}""")).Paging;
        Pure.Verbs.Paging opened = InspectScreenOptions.Bind(Reader(InspectScreenOptions.Spec, """{"id": "5", "cursor": "open"}""")).Paging;

        Assert.Equal("cursor", later.OffsetOnly()!.Path);
        Assert.Null(opened.OffsetOnly());
    }
}
