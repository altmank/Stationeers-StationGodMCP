#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// find_items group_by: holder sums one prefab's entries per outermost holder (loose ones together, with no holder),
/// prefab sums every entry of it; groups keep the list's order. 360 stacks in a 12-locker row: about 130 KB each,
/// about 25 KB by holder, under 1 KB by prefab. exclude_in_use reports how many parts in use it left out.
/// </summary>
public sealed class FindItemsGroupTests(ITestOutputHelper output)
{
    private const int Lockers = 12;
    private const int SlotsPerLocker = 30;

    private static readonly string[] Prefabs =
    {
        "ItemIronIngot", "ItemCopperIngot", "ItemGoldIngot", "ItemSilverIngot", "ItemSteelIngot", "ItemSolderIngot",
        "ItemElectrumIngot", "ItemInvarIngot"
    };

    [Theory]
    [InlineData(null, "None", true)]
    [InlineData("holder", "Holder", true)]
    [InlineData(" Prefab ", "Prefab", true)]
    [InlineData("container", "None", false)]
    public void GroupByReadsHolderOrPrefab(string? given, string expected, bool known)
    {
        Assert.Equal(known, ItemGroupings.TryParse(given, out ItemGrouping grouping));
        Assert.Equal(expected, grouping.ToString());
    }

    [Fact]
    public void ByHolderSumsAPrefabPerHolderAndLooseItemsTogether()
    {
        List<Row> rows = new List<Row>
        {
            new Row(10, "ItemIronIngot", 50), new Row(null, "ItemIronIngot", 3), new Row(10, "ItemIronIngot", 20),
            new Row(11, "ItemIronIngot", 7), new Row(10, "ItemCopperIngot", 5), new Row(null, "ItemIronIngot", 4)
        };

        List<ItemGroup<Row>> groups = Groups(rows, ItemGrouping.Holder);

        Assert.Equal(4, groups.Count);
        Assert.Equal((10L, "ItemIronIngot", 2, 70.0), Summary(groups[0]));
        Assert.Equal((null, "ItemIronIngot", 2, 7.0), Summary(groups[1]));
        Assert.Equal((11L, "ItemIronIngot", 1, 7.0), Summary(groups[2]));
        Assert.Equal((10L, "ItemCopperIngot", 1, 5.0), Summary(groups[3]));
    }

    [Fact]
    public void ByPrefabSumsEveryEntryAndCountsDistinctHolders()
    {
        List<Row> rows = new List<Row>
        {
            new Row(10, "ItemIronIngot", 50), new Row(null, "ItemIronIngot", 3), new Row(10, "ItemIronIngot", 20),
            new Row(11, "ItemIronIngot", 7), new Row(10, "ItemCopperIngot", 5)
        };

        List<ItemGroup<Row>> groups = Groups(rows, ItemGrouping.Prefab);

        Assert.Equal(2, groups.Count);
        Assert.Equal(4, groups[0].Entries);
        Assert.Equal(80.0, groups[0].Quantity);
        Assert.Equal(2, groups[0].Holders);
        Assert.Equal("ItemCopperIngot", groups[1].First.Prefab);
    }

    [Fact]
    public void ALooseGroupHasNoHolderAndNoPosition()
    {
        JObject loose = Wire(new HolderGroupView(Facts(null, "ItemIronIngot"), 2, 7));
        JObject held = Wire(new HolderGroupView(Facts(10, "ItemIronIngot"), 2, 70));

        Assert.Equal(JTokenType.Null, loose["holder"]!.Type);
        Assert.Null(loose["position"]);
        Assert.Null(loose["reagent"]);
        Assert.Equal("10", (string?)held["holder"]!["reference_id"]);
        Assert.NotNull(held["position"]);
    }

    [Fact]
    public void InUseLeftOutIsWrittenOnlyWhenAsked()
    {
        Slice<IFoundItemView> page = Slice<IFoundItemView>.Page(new List<IFoundItemView>(),
            PageRequest.From(new Args(new JObject()), 10, 500), 0);

        Assert.Null(Wire(new FindItemsView(page, null))["in_use_left_out"]);
        Assert.Equal(31, (int)Wire(new FindItemsView(page, null, 31))["in_use_left_out"]!);
    }

    [Fact]
    public void TheSidecarTakesGroupByAndExcludeInUse()
    {
        Assert.Empty(Problems("""{"prefab_contains":"Ingot","group_by":"holder","exclude_in_use":true}"""));
        Assert.Single(Problems("""{"group_by":"locker"}"""));
    }

    /// <summary>
    /// The 2026-10-08 call: a row of 12 lockers, 30 stacks each of 8 ingots, limit 500. Each item lists its holder
    /// chain and position (about 130 KB here, 160 KB live with longer names); by holder 96 groups, by prefab 8.
    /// </summary>
    [Fact]
    public void ATwelveLockerRowDropsToAFifthByHolderAndUnderAKilobyteByPrefab()
    {
        List<Row> rows = new List<Row>();
        for (int locker = 0; locker < Lockers; locker++)
        {
            for (int slot = 0; slot < SlotsPerLocker; slot++)
            {
                rows.Add(new Row(5000 + locker, Prefabs[slot % Prefabs.Length], 50, slot));
            }
        }

        int each = Page(rows.ConvertAll(static row => (IFoundItemView)row.Item())).Length;
        int byHolder = Page(Groups(rows, ItemGrouping.Holder).ConvertAll(static group =>
            (IFoundItemView)new HolderGroupView(group.First.Facts(), group.Entries, group.Quantity))).Length;
        int byPrefab = Page(Groups(rows, ItemGrouping.Prefab).ConvertAll(static group =>
            (IFoundItemView)new PrefabGroupView(group.First.Facts(), group.Entries, group.Quantity, group.Holders))).Length;
        output.WriteLine($"find_items, {rows.Count} stacks in {Lockers} lockers: each {each} bytes, group_by holder " +
                         $"{byHolder} bytes, group_by prefab {byPrefab} bytes");

        Assert.InRange(each, 110_000, 180_000);
        Assert.True(byHolder * 5 <= each, $"by holder {byHolder} bytes against {each}");
        Assert.True(byPrefab <= 1_000, $"by prefab {byPrefab} bytes");
    }

    private static List<ItemGroup<Row>> Groups(List<Row> rows, ItemGrouping by) =>
        ItemGroups.Of(rows, by, static row => row.Holder, static row => row.Prefab, static row => row.Quantity);

    private static (long?, string, int, double) Summary(ItemGroup<Row> group) =>
        (group.First.Holder, group.First.Prefab, group.Entries, group.Quantity);

    private static string Page(List<IFoundItemView> entries) =>
        WireCheck.New(new FindItemsView(Slice<IFoundItemView>.Page(entries,
                PageRequest.From(new Args(JObject.Parse("""{"limit":500}""")), 10, 500), entries.Count),
            new LocalPlayerView(new ThingId(1), "Player", new PositionView(593, 211, 627))));

    private static FoundGroupFacts Facts(long? holder, string prefab) =>
        new FoundGroupFacts(prefab, null, null, holder.HasValue ? "stored" : "ground",
            holder.HasValue ? new ThingView(new ThingId(holder.Value), "StructureStorageLocker", "Locker") : null,
            new PositionView(601, 211, 633), 12.3);

    private static JObject Wire(object view) => JObject.Parse(WireCheck.New(view));

    private static IReadOnlyList<string> Problems(string arguments)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(arguments);
        return StationGodMCP.Server.ArgumentCheck.Problems(StationGodMCP.Server.Program.InputSchemas["find_items"],
            document.RootElement);
    }

    private sealed class Row
    {
        internal Row(long? holder, string prefab, double quantity, int slot = 0)
        {
            Holder = holder;
            Prefab = prefab;
            Quantity = quantity;
            Slot = slot;
        }

        internal long? Holder { get; }

        internal string Prefab { get; }

        internal double Quantity { get; }

        private int Slot { get; }

        internal FoundGroupFacts Facts() => FindItemsGroupTests.Facts(Holder, Prefab);

        internal ItemView Item() => new ItemView(new ItemFields(
            new ThingView(new ThingId(900_000 + (Holder ?? 0) * 100 + Slot), Prefab, Prefab.Substring(4)), Quantity,
            500, new ItemPlace("stored", null,
                new List<HeldInView>
                {
                    new HeldInView(new ThingView(new ThingId(Holder ?? 0), "StructureStorageLocker", "Locker"), Slot,
                        "Storage")
                },
                new PositionView(601, 211, 633), 12.3)));
    }
}
