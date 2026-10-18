#nullable enable

using System.Collections.Generic;
using System.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// inspect contents as its handler reads it (the slots themselves need the game): prefab and name keep slots holding a
/// match at any depth, empty slots are left out and the holder's own counted, occupied slots are compact unless asked in
/// detail, fields paths reach nested slots, and a silo's entries page by the catalogue's limit (10, at most 600) and by
/// offset only.
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
    public void EmptySlotsAreLeftOutAtEveryDepthAndOnlyTheHoldersOwnCounted()
    {
        ShownSlots shown = ShownSlots.Occupied(PlayerWithEmptyOreBag());

        Assert.Equal(1, shown.EmptyOmitted);
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

        Assert.Contains("\"empty_slots_omitted\":1", pruned);
        Assert.DoesNotContain("\"empty\":true", pruned);
        Assert.DoesNotContain("empty_slots_omitted", whole);
    }

    // A 30-slot locker holding two suits, each with ten empty slots of its own, and one stack.
    private static List<SlotView> LockerOfSuits()
    {
        List<SlotView> slots = new List<SlotView>();
        for (int index = 0; index < 30; index++)
        {
            OccupantView? occupant = index switch
            {
                0 or 1 => Item(10 + index, "ItemHardSuit", "Hardsuit", SuitSlots()),
                2 => new OccupantView(new ThingView(new ThingId(20), "ItemSteelSheets", "Steel Sheets"), 37, 50, null,
                    null),
                _ => null
            };
            slots.Add(new SlotView(index, "Slot", "None", occupant));
        }

        return slots;
    }

    private static List<SlotView> SuitSlots()
    {
        List<SlotView> slots = new List<SlotView>();
        for (int index = 0; index < 10; index++)
        {
            slots.Add(new SlotView(index, "Slot", "None", null));
        }

        return slots;
    }

    [Fact]
    public void ALockerCountsItsOwnEmptySlotsNotItsSuits()
    {
        ShownSlots shown = ShownSlots.Occupied(LockerOfSuits());

        Assert.Equal(27, shown.EmptyOmitted);
        Assert.Equal(3, shown.Slots.Count);
    }

    [Fact]
    public void TheCompactFormIsEachOccupiedSlotsIdPrefabAndQuantity()
    {
        ThingView locker = new ThingView(new ThingId(40), "StructureStorageLocker", "Locker");
        PositionView here = new PositionView(1, 2, 3);

        string compact = WireCheck.New(new ContainerContentsView(locker, here, null, ShownSlots.Occupied(LockerOfSuits()),
            form: SlotForm.Compact));

        Assert.Equal("""{"reference_id":"40","prefab_name":"StructureStorageLocker","display_name":"Locker","position":{"x":1.0,"y":2.0,"z":3.0},"slots":[{"slot":0,"reference_id":"10","prefab_name":"ItemHardSuit","quantity":1.0},{"slot":1,"reference_id":"11","prefab_name":"ItemHardSuit","quantity":1.0},{"slot":2,"reference_id":"20","prefab_name":"ItemSteelSheets","quantity":37.0}],"empty_slots_omitted":27}""",
            compact);
        Assert.DoesNotContain("null", compact);
    }

    [Fact]
    public void TheCompactFormNestsAnOccupantsOwnOccupiedSlots()
    {
        ThingView player = new ThingView(new ThingId(9), "Character", "LU");
        JObject reply = JObject.Parse(WireCheck.New(new ContainerContentsView(player, new PositionView(0, 0, 0), 2.5,
            ShownSlots.Occupied(PlayerWithEmptyOreBag()), form: SlotForm.Compact)));

        JToken backpack = reply["slots"]![1]!;
        Assert.Equal(2, (int)backpack["slot"]!);
        Assert.Equal(new[] { 0, 2 }, ((JArray)backpack["slots"]!).Select(slot => (int)slot["slot"]!).ToArray());
        Assert.Null(backpack["slots"]![0]!["slots"]);
        Assert.Null(reply["slots"]![0]!["slots"]);
        Assert.Equal(2.5, (double)reply["distance_m"]!);
    }

    [Fact]
    public void ThePastDepthCountStaysInTheCompactForm()
    {
        ThingView locker = new ThingView(new ThingId(40), "StructureStorageLocker", "Locker");
        List<SlotView> slots = new List<SlotView>
        {
            new SlotView(0, "Slot", "None",
                new OccupantView(new ThingView(new ThingId(41), "ItemBackpack", "Backpack"), 1.0, null, null, 6))
        };

        JObject reply = JObject.Parse(WireCheck.New(new ContainerContentsView(locker, new PositionView(0, 0, 0), null,
            ShownSlots.Occupied(slots), form: SlotForm.Compact)));

        Assert.Equal(6, (int)reply["slots"]![0]!["slots_not_shown"]!);
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
