#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// inspect contents as its handler reads it (the slots themselves need the game): prefab and name keep slots holding a
/// match at any depth, empty slots are left out and counted, fields paths reach nested slots, and a silo's entries page
/// by the catalogue's limit (10, at most 600) and by offset only.
/// </summary>
public sealed class InspectContentsTests
{
    private static OccupantView Item(long id, string prefab, string name, List<SlotView>? slots = null) =>
        new OccupantView(new ThingView(new ThingId(id), prefab, name), 1, null, slots, null);

    private static List<SlotView> Locker() => new List<SlotView>
    {
        new SlotView(0, "Slot", "None", Item(1, "ItemIronIngot", "Ingot (Iron)")),
        new SlotView(1, "Slot", "None", null),
        new SlotView(2, "Slot", "Back", Item(2, "ItemHardBackpack", "Hardsuit Backpack", new List<SlotView>
        {
            new SlotView(0, "Slot", "None", Item(3, "ItemSteelSheets", "Steel Sheets")),
            new SlotView(1, "Slot", "None", Item(4, "ItemIronIngot", "Ingot (Iron)"))
        }))
    };

    [Fact]
    public void AFilterKeepsMatchesAtAnyDepthAndOnlyTheirSlots()
    {
        PrefabFilter steel = PrefabFilter.Bind(new OptionReader(InspectContentsOptions.Spec,
            JObject.Parse("""{"prefab": "*steel*"}""")))!;
        List<SlotView> kept = new SlotFilter(name => steel.Keeps(name, 0), null).Apply(Locker());

        SlotView backpack = Assert.Single(kept);
        Assert.Equal(2, backpack.Index);
        Assert.Equal(0, Assert.Single(backpack.Occupant!.Slots!).Index);
        Assert.Equal(2, new SlotFilter(null, "INGOT (iron)").Apply(Locker()).Count);
        Assert.Equal(3, new SlotFilter(null, null).Apply(Locker()).Count);
    }

    private static List<SlotView> PlayerWithEmptyOreBag() => new List<SlotView>
    {
        new SlotView(0, "Hand", "None", Item(1, "ItemIronIngot", "Ingot (Iron)")),
        new SlotView(1, "Hand", "None", null),
        new SlotView(2, "Back", "Back", Item(2, "ItemHardBackpack", "Hardsuit Backpack", new List<SlotView>
        {
            new SlotView(0, "Slot", "Ore", Item(5, "ItemOreBag", "Ore Bag", new List<SlotView>
            {
                new SlotView(0, "Slot", "Ore", null),
                new SlotView(1, "Slot", "Ore", null),
                new SlotView(2, "Slot", "Ore", null)
            })),
            new SlotView(1, "Slot", "None", null),
            new SlotView(2, "Slot", "None", Item(3, "ItemSteelSheets", "Steel Sheets"))
        }))
    };

    [Fact]
    public void EmptySlotsAreLeftOutAtEveryDepthAndCounted()
    {
        ShownSlots shown = ShownSlots.Occupied(PlayerWithEmptyOreBag());

        Assert.Equal(5, shown.EmptyOmitted);
        Assert.Equal(new[] { 0, 2 }, Indexes(shown.Slots));
        List<SlotView> backpack = shown.Slots[1].Occupant!.Slots!;
        Assert.Equal(new[] { 0, 2 }, Indexes(backpack));
        Assert.Null(backpack[0].Occupant!.Slots);
    }

    [Fact]
    public void IncludeEmptyKeepsEverySlot()
    {
        ShownSlots shown = ShownSlots.All(PlayerWithEmptyOreBag());

        Assert.Equal(0, shown.EmptyOmitted);
        Assert.Equal(3, shown.Slots.Count);
        Assert.Equal(3, shown.Slots[2].Occupant!.Slots![0].Occupant!.Slots!.Count);
    }

    [Fact]
    public void TheReplyCountsOmittedSlotsOnlyWhenAnyWent()
    {
        ThingView player = new ThingView(new ThingId(9), "Character", "LU");
        PositionView here = new PositionView(0, 0, 0);

        string pruned = WireCheck.New(new ContainerContentsView(player, here, null, ShownSlots.Occupied(PlayerWithEmptyOreBag())));
        string whole = WireCheck.New(new ContainerContentsView(player, here, null, ShownSlots.All(PlayerWithEmptyOreBag())));

        Assert.Contains("\"empty_slots_omitted\":5", pruned);
        Assert.DoesNotContain("\"empty\":true", pruned);
        Assert.DoesNotContain("empty_slots_omitted", whole);
    }

    private static int[] Indexes(List<SlotView> slots)
    {
        int[] indexes = new int[slots.Count];
        for (int index = 0; index < slots.Count; index++)
        {
            indexes[index] = slots[index].Index;
        }

        return indexes;
    }

    [Fact]
    public void FieldPathsReachNestedSlots()
    {
        string reply = """{"reference_id":"9","slots":[{"index":0,"name":"Slot","occupant":{"prefab_name":"ItemHardBackpack","quantity":1,"slots":[{"index":0,"occupant":{"prefab_name":"ItemSteelSheets","quantity":50}}]}},{"index":1,"name":"Slot","occupant":null}]}""";

        Assert.Equal("""{"reference_id":"9","slots":[{"index":0,"occupant":{"prefab_name":"ItemHardBackpack"}},{"index":1,"occupant":null}]}""",
            ShapingChecks.ModFields(reply, new[] { "slots.index", "slots.occupant.prefab_name" }));
        Assert.Equal("""{"reference_id":"9","slots":[{"occupant":{"slots":[{"occupant":{"prefab_name":"ItemSteelSheets"}}]}},{"occupant":null}]}""",
            ShapingChecks.ModFields(reply, new[] { "occupant.slots.occupant.prefab_name" }));
        Assert.Equal("""{"reference_id":"9","slots":[{"index":0,"name":"Slot","occupant":{"prefab_name":"ItemHardBackpack","quantity":1}},{"index":1,"name":"Slot","occupant":null}]}""",
            ShapingChecks.ModOmit(reply, new[] { "slots.occupant.slots" }));
    }

    [Fact]
    public void ASilosEntriesPageTenAtATimeUpTo600AndWalkOneSilosStore()
    {
        Pure.Verbs.Paging paging = InspectContentsOptions.Bind(new OptionReader(InspectContentsOptions.Spec,
            JObject.Parse("""{"id": "5"}"""))).Paging;
        Pure.Verbs.Paging later = InspectContentsOptions.Bind(new OptionReader(InspectContentsOptions.Spec,
            JObject.Parse("""{"id": "5", "cursor": "k4"}"""))).Paging;

        Assert.Equal(10, paging.Limit);
        Assert.Equal(0, paging.Offset);
        Assert.Null(Check("""{"what": "contents", "id": "5", "limit": 600, "offset": 20}"""));
        Assert.NotNull(Check("""{"what": "contents", "id": "5", "limit": 601}"""));
        Assert.Null(KeyedPage.OneThing(paging, 2));
        Assert.Null(KeyedPage.OneThing(later, 1));
        Assert.Equal("cursor", KeyedPage.OneThing(later, 2)!.Path);
    }

    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("inspect")!, JObject.Parse(options), out _);
}
