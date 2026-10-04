#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// find_items, item_totals, list_containers and container_contents: the old StationApi.Inventory shapes against the
/// new views; total_matches renamed total.
/// </summary>
public sealed class InventoryWireTests
{
    private static readonly PageRequest FirstPage =
        PageRequest.From(new Args(JObject.Parse("{\"limit\": 1}")), 100, 500);

    private static readonly Dictionary<string, string> TotalRename = new Dictionary<string, string>
    {
        ["total_matches"] = "total"
    };

    private static object OldItem() => new
    {
        reference_id = "30", prefab_name = "ItemIronIngot", display_name = "Iron", quantity = 50.0,
        max_quantity = (double?)500.0, location = "player", carried_by = (string?)"Player",
        held_in = new List<object>
        {
            new
            {
                reference_id = "20", prefab_name = "ItemBackpack", display_name = "Backpack", slot_index = 0,
                slot_name = "Slot"
            }
        },
        position = new { x = 1.0, y = 2.0, z = 3.0 }, distance_m = (double?)0.0
    };

    private static ItemView NewItem() => new ItemView(new ItemFields(
        new ThingView(new ThingId(30), "ItemIronIngot", "Iron"), 50.0, 500.0,
        new ItemPlace("player", "Player",
            new List<HeldInView>
            {
                new HeldInView(new ThingView(new ThingId(20), "ItemBackpack", "Backpack"), 0, "Slot")
            },
            new PositionView(1.0, 2.0, 3.0), 0.0)));

    private static object OldPlayer() => new
    {
        reference_id = "1", display_name = "Player", position = new { x = 1.0, y = 2.0, z = 3.0 }
    };

    private static LocalPlayerView NewPlayer() =>
        new LocalPlayerView(new ThingId(1), "Player", new PositionView(1.0, 2.0, 3.0));

    [Fact]
    public void FindItemsSameWireAfterRenames()
    {
        var old = new
        {
            items = new List<object> { OldItem() }, count = 1, total_matches = 7, offset = 0, limit = 1,
            has_more = true, local_player = OldPlayer()
        };
        FindItemsView view = new FindItemsView(
            Slice<IFoundItemView>.Page(new List<IFoundItemView> { NewItem() }, FirstPage, 7), NewPlayer());
        WireCheck.SameAfterRenames(old, view, TotalRename);
    }

    [Fact]
    public void FindItemsListsMachineStockLikeAnItem()
    {
        StockItemView stock = new StockItemView("ItemElectrumIngot", "Ingot (Electrum)", 63.0,
            new List<HeldInView>
            {
                new HeldInView(new ThingView(new ThingId(16449), "StructureHydraulicPipeBender", "Hydraulic Pipe Bender"),
                    StockItemView.NoSlot, StockItemView.StockSlotName)
            },
            new PositionView(1.0, 2.0, 3.0), 4.5, new MachineStockView("Electrum", "Electrum", "fabricator"));
        var expected = new
        {
            reference_id = (string?)null, prefab_name = "ItemElectrumIngot", display_name = "Ingot (Electrum)",
            quantity = 63.0, max_quantity = (double?)null, location = "machine_stock", carried_by = (string?)null,
            held_in = new List<object>
            {
                new
                {
                    reference_id = "16449", prefab_name = "StructureHydraulicPipeBender",
                    display_name = "Hydraulic Pipe Bender", slot_index = -1, slot_name = "reagent stock"
                }
            },
            position = new { x = 1.0, y = 2.0, z = 3.0 }, distance_m = (double?)4.5,
            machine_stock = new
            {
                reagent = "Electrum", reagent_name = (string?)"Electrum", kind = "fabricator", movable = false
            }
        };
        WireCheck.Same(expected, stock);
    }

    [Fact]
    public void ItemTotalsKeepsItsFieldsAndAddsMachineStock()
    {
        var old = new
        {
            totals = new List<object>
            {
                new
                {
                    prefab_name = "ItemIronIngot", display_name = "Iron", items = 2, quantity = 80.0,
                    on_ground = 30.0, carried = 50.0, stored = 0.0,
                    top_holders = new List<object>
                    {
                        new { reference_id = "1", prefab_name = "Character", display_name = "Player", quantity = 50.0 }
                    }
                }
            },
            prefab_count = 1, item_count = 2
        };
        ItemTotalsView view = new ItemTotalsView(
            new List<PrefabTotalView>
            {
                new PrefabTotalView("ItemIronIngot", "Iron", null, 2, new PlaceAmounts(80.0, 30.0, 50.0, 0.0, 0.0),
                    new List<HolderTotalView>
                    {
                        new HolderTotalView(new ThingView(new ThingId(1), "Character", "Player"), 50.0, "player",
                            new PositionView(1.0, 2.0, 3.0))
                    })
            },
            1, 2, 0);
        JObject oldShape = JObject.FromObject(old);
        WireCheck.SameAfterRenames(oldShape, view, new Dictionary<string, string>(),
            "totals[].machine_stock", "totals[].reagent", "totals[].top_holders[].kind",
            "totals[].top_holders[].position", "machine_stock_entries");
    }

    [Fact]
    public void ItemTotalsCountsStockAndWorkingLoads()
    {
        ItemTotalsView view = new ItemTotalsView(
            new List<PrefabTotalView>
            {
                new PrefabTotalView("ItemElectrumIngot", "Ingot (Electrum)", null, 1,
                    new PlaceAmounts(73.0, 10.0, 0.0, 0.0, 63.0),
                    new List<HolderTotalView>
                    {
                        new HolderTotalView(new ThingView(new ThingId(16449), "StructureHydraulicPipeBender", "Bender"),
                            63.0, "machine_stock", new PositionView(1.0, 2.0, 3.0))
                    }),
                new PrefabTotalView(null, "Iron", "Iron", 0, new PlaceAmounts(30.0, 0.0, 0.0, 0.0, 30.0),
                    new List<HolderTotalView>())
            },
            2, 1, 2);
        var expected = new
        {
            totals = new List<object>
            {
                new
                {
                    prefab_name = (string?)"ItemElectrumIngot", display_name = "Ingot (Electrum)", items = 1,
                    quantity = 73.0, on_ground = 10.0, carried = 0.0, stored = 0.0, machine_stock = 63.0,
                    reagent = (string?)null,
                    top_holders = new List<object>
                    {
                        new
                        {
                            reference_id = "16449", prefab_name = "StructureHydraulicPipeBender",
                            display_name = "Bender", quantity = 63.0, kind = "machine_stock",
                            position = new { x = 1.0, y = 2.0, z = 3.0 }
                        }
                    }
                },
                new
                {
                    prefab_name = (string?)null, display_name = "Iron", items = 0, quantity = 30.0, on_ground = 0.0,
                    carried = 0.0, stored = 0.0, machine_stock = 30.0, reagent = (string?)"Iron",
                    top_holders = new List<object>()
                }
            },
            prefab_count = 2, item_count = 1, machine_stock_entries = 2
        };
        WireCheck.Same(expected, view);
    }

    [Fact]
    public void ListContainersSameWireAfterRenames()
    {
        ListContainersView view = new ListContainersView(
            Slice<ContainerView>.Page(
                new List<ContainerView>
                {
                    new ContainerView(new ThingView(new ThingId(40), "StructureStorageLocker", "Locker"),
                        new SlotUse(30, 2, 3),
                        new List<PrefabQuantityView> { new PrefabQuantityView("ItemIronIngot", 100.0) },
                        new PositionView(4.0, 5.0, 6.0), 12.3)
                },
                FirstPage, 4),
            null);
        var old = new
        {
            containers = new List<object>
            {
                new
                {
                    reference_id = "40", prefab_name = "StructureStorageLocker", display_name = "Locker",
                    slots_total = 30, slots_used = 2, item_count = 3,
                    items = new List<object> { new { prefab_name = "ItemIronIngot", quantity = 100.0 } },
                    position = new { x = 4.0, y = 5.0, z = 6.0 }, distance_m = (double?)12.3
                }
            },
            count = 1, total_matches = 4, offset = 0, limit = 1, has_more = true, local_player = (object?)null
        };
        WireCheck.SameAfterRenames(old, view, TotalRename);
    }

    [Fact]
    public void ContainerContentsSameWire()
    {
        var old = new
        {
            reference_id = "40", prefab_name = "StructureStorageLocker", display_name = "Locker",
            position = new { x = 4.0, y = 5.0, z = 6.0 }, distance_m = (double?)null,
            slots = new List<object>
            {
                new
                {
                    index = 0, name = "Slot", slot_class = "None", empty = false,
                    occupant = new
                    {
                        reference_id = "41", prefab_name = "ItemBackpack", display_name = "Backpack", quantity = 1.0,
                        max_quantity = (double?)null, slots = (object?)null, slots_not_shown = (int?)6
                    }
                },
                new
                {
                    index = 1, name = "Slot", slot_class = "None", empty = true, occupant = (object?)null
                }
            }
        };
        ContainerContentsView view = new ContainerContentsView(
            new ThingView(new ThingId(40), "StructureStorageLocker", "Locker"), new PositionView(4.0, 5.0, 6.0), null,
            new List<SlotView>
            {
                new SlotView(0, "Slot", "None",
                    new OccupantView(new ThingView(new ThingId(41), "ItemBackpack", "Backpack"), 1.0, null, null, 6)),
                new SlotView(1, "Slot", "None", null)
            });
        WireCheck.Same(old, view);
    }
}
