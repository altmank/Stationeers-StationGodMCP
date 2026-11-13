#nullable enable

using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class PrefabSlotsTests
{
    private static readonly int[] None = Array.Empty<int>();

    private static PrefabSlotFacts Unclassed() => new PrefabSlotFacts(true, None);

    private static PrefabSlotFacts Classed() => new PrefabSlotFacts(false, None);

    private static PrefabSlotFacts NamesPrefabs() => new PrefabSlotFacts(true, new[] { 1234 });

    private static PrefabSlotsView View(params PrefabSlotFacts[] slots) =>
        new PrefabSlotsView(
            slots.Select((slot, index) => new PrefabSlotView(index, "Slot " + index, "key" + index,
                new EnumValueView(slot.ClassIsNone ? 0 : 5, slot.ClassIsNone ? "None" : "Battery"),
                PrefabSlotRule.IsGeneral(slot))).ToList(),
            PrefabSlotRule.GeneralCount(slots));

    private static JObject Facts(PrefabSlotsView? slots) =>
        JObject.Parse(WireCheck.New(new PrefabFactsView("ItemX", 1, "X", "Item", false, null, null, slots)));

    [Fact]
    public void UnclassedSlotThatNamesNoPrefabsIsGeneral() => Assert.True(PrefabSlotRule.IsGeneral(Unclassed()));

    [Fact]
    public void ClassedSlotIsNotGeneral() => Assert.False(PrefabSlotRule.IsGeneral(Classed()));

    [Fact]
    public void UnclassedSlotThatNamesPrefabsIsNotGeneral() => Assert.False(PrefabSlotRule.IsGeneral(NamesPrefabs()));

    [Fact]
    public void BackpackOfStorageSlotsIsAllGeneral()
    {
        PrefabSlotsView view = View(Unclassed(), Unclassed(), Unclassed(), Unclassed());

        Assert.Equal(4, view.GeneralSlots);
        Assert.All(view.Slots, slot => Assert.True(slot.General));
    }

    [Fact]
    public void SuitCountsOnlyItsGeneralSlots()
    {
        PrefabSlotsView view = View(Classed(), Classed(), Unclassed(), NamesPrefabs());

        Assert.Equal(1, view.GeneralSlots);
        Assert.Equal(4, view.Slots.Count);
    }

    [Fact]
    public void PrefabWithoutSlotsLeavesTheFieldsOut()
    {
        JObject reply = Facts(null);

        Assert.False(reply.ContainsKey("slots"));
        Assert.False(reply.ContainsKey("general_slots"));
    }

    [Fact]
    public void PrefabWithSlotsReplyCarriesRowsAndCount()
    {
        JObject reply = Facts(View(Unclassed(), Classed()));

        Assert.Equal(2, reply["slots"]!.Count());
        Assert.Equal(1, (int)reply["general_slots"]!);
        Assert.True((bool)reply["slots"]![0]!["general"]!);
        Assert.Equal("None", (string?)reply["slots"]![0]!["slot_class"]!["name"]);
    }
}
