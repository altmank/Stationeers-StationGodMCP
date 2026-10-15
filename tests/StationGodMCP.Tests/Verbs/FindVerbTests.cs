#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Scheduling;
using Xunit;
using VerbPaging = StationGodMCP.Pure.Verbs.Paging;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The find whats as their handlers read their options (the game's lists need the game): the prefab filter and the
/// prefab index's match; find item's places, groups and defaults; the whats that measure only from the player; the
/// lists that page by offset only; ore's catalogue defaults; plant and room answering a list of ids one row each;
/// and a list sorted in one go walked under a cursor (ReadyWalk).
/// </summary>
public sealed class FindVerbTests
{
    private static readonly CallContext Hub = new CallContext("c1", 2);

    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("find")!, JObject.Parse(options), out _);

    private static OptionReader Reader(WhatSpec what, string options) => new OptionReader(what, JObject.Parse(options));

    // ---- the prefab filter ----

    [Fact]
    public void AWholePrefabKeepsThatPrefabOnlyInAnyCaseAndPartsKeepEveryPartInAnyOrder()
    {
        PrefabFilter frame = Prefab("""{"prefab": " StructureFrame "}""");
        PrefabFilter parts = Prefab("""{"prefab": "*Tank*Insulated*"}""");

        Assert.True(frame.Keeps("structureframe", 0));
        Assert.False(frame.Keeps("StructureFrameCorner", 0));
        Assert.False(frame.Keeps(null, 0));
        Assert.True(parts.Keeps("StructureInsulatedTankConnector", 0));
        Assert.False(parts.Keeps("StructureTankSmall", 0));
        Assert.True(Prefab("""{"prefab": -1234}""").Keeps(null, -1234));
    }

    [Fact]
    public void ThePrefabIndexNarrowsByWholeNamesOrOnePartAndWalksEverythingElse()
    {
        PrefabMatch names = PrefabMatch.ForIndex(Prefab("""{"prefab": ["StructureFrame", "StructureWall"]}"""));
        PrefabMatch part = PrefabMatch.ForIndex(Prefab("""{"prefab": "*Ingot*"}"""));

        Assert.Equal(new[] { "StructureFrame", "StructureWall" }, names.Exact!.OrderBy(name => name));
        Assert.Equal("Ingot", part.Contains);
        Assert.False(PrefabMatch.ForIndex(Prefab("""{"prefab": 99}""")).IsActive);
        Assert.False(PrefabMatch.ForIndex(Prefab("""{"prefab": "*Tank*Insulated*"}""")).IsActive);
        Assert.False(PrefabMatch.ForIndex(null).IsActive);
    }

    // ---- find item ----

    [Fact]
    public void FindItemTakesAPrefabAnAreaAndEveryPlace()
    {
        Assert.Null(Check("""{"what": "item", "prefab": "StructureFrame", "near": [100, 20, 50], "radius": 9}"""));
        Assert.Null(Check("""{"what": "item", "prefab": "*Frame*", "box": [[90, 15, 40], [105, 25, 50]]}"""));
        Assert.NotNull(Check("""{"what": "item", "near": [1, 2, 3], "radius": "far"}"""));
        foreach (string place in new[] { "any", "ground", "player", "stored", "machine_stock", "silo" })
        {
            Assert.Null(Check($$"""{"what": "item", "location": "{{place}}"}"""));
        }

        Assert.NotNull(Check("""{"what": "item", "location": "deck"}"""));
    }

    [Fact]
    public void FindItemPagesTenAndGroupByPlaceShowsTwelveWithOneHolderEach()
    {
        FindItemOptions list = FindItemOptions.Bind(Reader(FindItemOptions.Spec, """{"within": "551"}"""));
        FindItemOptions place = FindItemOptions.Bind(Reader(FindItemOptions.Spec, """{"group_by": "place"}"""));

        Assert.Equal(10, list.Paging.Limit);
        Assert.Equal("551", list.Selector.Within);
        Assert.Equal("any", list.Location);
        Assert.Equal(12, place.Paging.Limit);
        Assert.Equal(1, place.Holders);
        Assert.False(place.ExcludeInUse);
    }

    // ---- every paged list walks under a cursor (cost design A4) ----

    [Fact]
    public void FindItemAndFindDeviceTakeACursorToOpenAndToGoOn()
    {
        Assert.Equal("k3", FindItemOptions.Bind(Reader(FindItemOptions.Spec, """{"cursor": "k3"}""")).Paging.CursorId);
        Assert.True(FindItemOptions.Bind(Reader(FindItemOptions.Spec, """{"cursor": "open"}""")).Paging.OpensCursor);
        Assert.Equal("k4", FindDeviceOptions.Bind(Reader(FindDeviceOptions.Spec, """{"cursor": "k4"}""")).Paging.CursorId);
    }

    [Fact]
    public void FindDeviceTakesNoOptionButItsWhatAndPagesTwentyFive()
    {
        FindDeviceOptions device = FindDeviceOptions.Bind(Reader(FindDeviceOptions.Spec, "{}"));

        Assert.Null(Check("""{"what": "device"}"""));
        Assert.Null(Check("""{"what": "device", "gateway": "world", "prefab": "*Pump*", "name": "deck 2"}"""));
        Assert.Equal(25, device.Paging.Limit);
    }

    // ---- the whats that measure from the player only ----

    [Theory]
    [InlineData("container")]
    [InlineData("frame")]
    [InlineData("damaged")]
    public void NearIsThePlayerOnlyAndGoesWithRadius(string what)
    {
        Assert.Empty(SelectorOf(what, """{"near": "player", "radius": 30}""").PlayerRadiusProblems());
        Assert.Equal(30, SelectorOf(what, """{"near": "player", "radius": 30}""").AroundPlayerM);
        Assert.Empty(SelectorOf(what, "{}").PlayerRadiusProblems());
        Assert.Contains("\"player\" only", Assert.Single(SelectorOf(what, """{"near": [1, 2, 3], "radius": 5}""")
            .PlayerRadiusProblems()).Problem);
        Assert.Contains("\"player\" only", Assert.Single(SelectorOf(what, """{"near": "looking_at", "radius": 5}""")
            .PlayerRadiusProblems()).Problem);
        Assert.Equal("near", Assert.Single(SelectorOf(what, """{"near": "player"}""").PlayerRadiusProblems()).Path);
        Assert.Equal("radius", Assert.Single(SelectorOf(what, """{"radius": 5}""").PlayerRadiusProblems()).Path);
    }

    [Fact]
    public void TheThingListsPageAtTheirCataloguesDefaults()
    {
        Assert.Equal(12, FindContainerOptions.Bind(Reader(FindContainerOptions.Spec, "{}")).Paging.Limit);
        Assert.Equal(20, FindFrameOptions.Bind(Reader(FindFrameOptions.Spec, "{}")).Paging.Limit);
        Assert.Equal(8, FindDamagedOptions.Bind(Reader(FindDamagedOptions.Spec, "{}")).Paging.Limit);
        Assert.True(FindFrameOptions.Bind(Reader(FindFrameOptions.Spec, """{"include": ["inner"]}""")).Fields
            .Includes("inner"));
        FindDamagedOptions damaged = FindDamagedOptions.Bind(Reader(FindDamagedOptions.Spec, "{}"));
        Assert.Equal(0.0, damaged.MinDamage);
        Assert.False(damaged.BrokenOnly);
        Assert.False(damaged.StructuresOnly);
    }

    // ---- find ore, water ----

    [Fact]
    public void FindOreSearchesFiveHundredMetresForFiveSpotsAHundredApart()
    {
        FindOreOptions ore = FindOreOptions.Bind(Reader(FindOreOptions.Spec, """{"ores": ["Cobalt"]}"""));

        Assert.Null(ore.Selector.Radius);
        Assert.Equal(500.0, FindOreOptions.Spec.Option("radius")!.DefaultNumber);
        Assert.Equal(5, ore.Paging.Limit);
        Assert.Equal(100.0, ore.Separation);
        Assert.Null(ore.Step);
        Assert.Equal(new[] { "Cobalt" }, ore.Ores);
        Assert.Null(Check("""{"what": "ore", "near": [10, 0, 20], "ores": ["Cobalt", "Iron"]}"""));
        Assert.NotNull(Check("""{"what": "ore", "near": "player"}"""));
        Assert.NotNull(Check("""{"what": "ore", "ores": ["A", "B", "C", "D", "E"]}"""));
    }

    [Fact]
    public void FindWaterSkipsSourcesUnderOneMole()
    {
        Assert.Equal(1.0, FindWaterOptions.Bind(Reader(FindWaterOptions.Spec, "{}")).MinMol);
    }

    // ---- plant and room: one id, or one row per id ----

    [Theory]
    [InlineData("plant")]
    [InlineData("room")]
    public void AListOfIdsAnswersRowsAndOneIdItsOwnShape(string what)
    {
        WhatSpec spec = VerbSurface.Verb("find")!.What(what)!;

        Assert.True(Selector.Bind(Reader(spec, """{"id": ["5", "6"]}""")).Ids!.AnswersRows);
        Assert.False(Selector.Bind(Reader(spec, """{"id": "5"}""")).Ids!.AnswersRows);
        Assert.Null(Selector.Bind(Reader(spec, "{}")).Ids);
    }

    [Fact]
    public void ARowsReadThatRefusesIsThatRowsErrorAndTheOthersGoOn()
    {
        Rows rows = new Rows();
        rows.Add("5", () => new JObject { ["room_id"] = "77" });
        rows.Add("6", () => throw new ApiException("not_in_room", "Thing 6 is in no closed room."));

        JObject reply = Replies.Tree(rows.Reply(ReplyFields.Of(("local_player_room_id", null))));

        Assert.Equal(JTokenType.Null, reply["local_player_room_id"]!.Type);
        Assert.Equal("77", (string?)reply["results"]![0]!["room_id"]);
        Assert.Equal("not_in_room", (string?)reply["results"]![1]!["error"]!["code"]);
        Assert.Equal(1, (int)reply["success_count"]!);
        Assert.Equal(1, (int)reply["error_count"]!);
    }

    [Fact]
    public void TheFieldWriterKeepsTheDeclaredOrderAndWritesNullAsNull()
    {
        JObject reply = Replies.Tree(ReplyFields.Of(("pads", new List<int> { 1, 2 }), ("count", 2), ("search", null)));

        Assert.Equal(new[] { "pads", "count", "search" }, reply.Properties().Select(key => key.Name));
        Assert.Equal(JTokenType.Null, reply["search"]!.Type);
    }

    // ---- a list sorted in one go, walked ----

    [Fact]
    public void AListSortedInOneGoOpensACursorAndItsLaterPagePassesOverAGoneThing()
    {
        using CallContext.Scope scope = CallContext.Enter(Hub);
        WalkCursors cursors = new WalkCursors();
        long[] sorted = { 50, 40, 30, 20, 10 };
        HashSet<long> gone = new HashSet<long>();

        FramedResult<string> first = Take(FramedPage<string>.Scan(
            WalkRequest.Of(PagingOf("""{"cursor": "open", "limit": 2}"""), cursors), 0, 2, new ReadyWalk(sorted, 9),
            id => gone.Contains(id) ? null : "t" + id));
        gone.Add(30);
        FramedResult<string> later = Take(FramedPage<string>.Later(
            WalkRequest.Of(PagingOf($$"""{"cursor": "{{first.CursorId}}", "limit": 2}"""), cursors), 2,
            id => gone.Contains(id) ? null : "t" + id));

        Assert.Equal(new[] { "t50", "t40" }, first.Page.Entries);
        Assert.Equal(5, first.Page.Total);
        Assert.True(first.Page.HasMore);
        Assert.NotNull(first.CursorId);
        Assert.Equal(new[] { "t20", "t10" }, later.Page.Entries);
        Assert.False(later.Page.HasMore);
    }

    [Fact]
    public void EveryLaterPageOfAWalkAnswersTheCountsItsFirstPageKept()
    {
        using CallContext.Scope scope = CallContext.Enter(Hub);
        WalkCursors cursors = new WalkCursors();
        long[] sorted = { 50, 40, 30, 20, 10 };
        object counts = new { structures = 3, broken = 1, scanned = 9 };

        FramedResult<string> first = Take(FramedPage<string>.Scan(
            WalkRequest.Of(PagingOf("""{"cursor": "open", "limit": 2}"""), cursors), 0, 2, new ReadyWalk(sorted, 9),
            id => "t" + id), counts);
        FramedResult<string> second = Take(FramedPage<string>.Later(
            WalkRequest.Of(PagingOf($$"""{"cursor": "{{first.CursorId}}", "limit": 2}"""), cursors), 2,
            id => "t" + id));
        FramedResult<string> last = Take(FramedPage<string>.Later(
            WalkRequest.Of(PagingOf($$"""{"cursor": "{{first.CursorId}}", "limit": 2}"""), cursors), 2,
            id => "t" + id));

        Assert.Same(counts, first.Kept);
        Assert.Same(counts, second.Kept);
        Assert.Same(counts, last.Kept);
        Assert.False(last.Page.HasMore);
    }

    [Fact]
    public void AListSortedInOneGoWithoutACursorPagesAtItsOffsetAndKeepsNothing()
    {
        WalkCursors cursors = new WalkCursors();

        FramedResult<string> page = Take(FramedPage<string>.Scan(
            WalkRequest.Of(PagingOf("""{"offset": 3, "limit": 2}"""), cursors), 3, 2,
            new ReadyWalk(new long[] { 5, 4, 3, 2, 1 }, 5), id => "t" + id));

        Assert.Equal(new[] { "t2", "t1" }, page.Page.Entries);
        Assert.Equal(3, page.Page.Offset);
        Assert.False(page.Page.HasMore);
        Assert.Null(page.CursorId);
        Assert.Equal(0, cursors.Count);
    }

    private static FramedResult<string> Take(FramedPage<string> page, object? kept = null)
    {
        FakeClock clock = new FakeClock();
        while (!page.Step(clock, clock.NowMs + 4.0, 0.0))
        {
        }

        return page.Result(kept);
    }

    private static VerbPaging PagingOf(string options) =>
        FindContainerOptions.Bind(Reader(FindContainerOptions.Spec, options)).Paging;

    private static PrefabFilter Prefab(string options) =>
        PrefabFilter.Bind(Reader(FindItemOptions.Spec, options))!;

    private static Selector SelectorOf(string what, string options) =>
        Selector.Bind(Reader(VerbSurface.Verb("find")!.What(what)!, options));
}
