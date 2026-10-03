#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// list_gateways, list_devices, describe_device, inspect_slots and network_snapshot: the old StationApi shapes against
/// the new views; no renames. Non-finite logic values stay the strings "NaN", "Infinity" and "-Infinity".
/// </summary>
public sealed class DeviceWireTests
{
    internal static object OldDevice() => new
    {
        reference_id = "100", prefab_name = "StructureGasSensor", prefab_hash = -1252983604,
        display_name = "Gas Sensor", runtime_type = "Assets.Scripts.Objects.Pipes.GasSensor", slot_count = 0,
        is_gateway = false, worn_by = (string?)null, worn_slot = (string?)null
    };

    internal static DeviceView NewDevice() => new DeviceView(
        new ThingView(new ThingId(100), "StructureGasSensor", "Gas Sensor"), -1252983604,
        "Assets.Scripts.Objects.Pipes.GasSensor", 0, new DeviceWear(false, null, null));

    [Fact]
    public void ListGatewaysSameWire()
    {
        var old = new
        {
            gateways = new List<object>
            {
                new
                {
                    gateway_id = "world", display_name = "Whole world", prefab_name = (string?)null, available = true,
                    status = "bypass", data_network_count = 0, visible_device_count = 40, worn_item_count = 2
                },
                new
                {
                    gateway_id = "55", display_name = "Gateway", prefab_name = "StructureStationGodGateway",
                    available = false, status = "no_data_network", data_network_count = 0, visible_device_count = 0,
                    worn_item_count = 2
                }
            },
            count = 2, bypass_gateway = true
        };
        GatewaysView view = new GatewaysView(new List<GatewayView>
        {
            new GatewayView("world", "Whole world", null, new GatewayState(true, "bypass"),
                new GatewayCounts(0, 40, 2)),
            new GatewayView("55", "Gateway", "StructureStationGodGateway", new GatewayState(false, "no_data_network"),
                new GatewayCounts(0, 0, 2))
        });
        WireCheck.Same(old, view);
    }

    [Fact]
    public void ListAndDescribeDevicesSameWire()
    {
        WireCheck.Same(new { gateway_id = "world", devices = new List<object> { OldDevice() }, count = 1 },
            new DevicesView("world", new List<DeviceView> { NewDevice() }));
        var old = new
        {
            device = OldDevice(),
            logic_types = new List<object>
            {
                new { id = (ushort)6, name = "Pressure", readable = true, writable = false }
            },
            logic_type_count = 1
        };
        WireCheck.Same(old, new DescribeDeviceView(NewDevice(),
            new List<LogicAccessView> { new LogicAccessView(new LogicTypeView(6, "Pressure"), true, false) }));
    }

    [Fact]
    public void InspectSlotsSameWire()
    {
        var old = new
        {
            gateway_id = "world", reference_id = "100", device = OldDevice(),
            slots = new List<object>
            {
                new
                {
                    index = 0, underlying_slot_index = (int?)0, display_name = "Import", string_key = "Import",
                    slot_class = new { id = 0, name = "None" }, empty = false, interactable = true, locked = false,
                    swappable = true, hides_occupant = false, specific_prefab_hashes = new int[0],
                    occupant = new
                    {
                        reference_id = "101", prefab_name = "ItemIronOre", prefab_hash = 1758427767,
                        display_name = "Iron Ore", runtime_type = "Assets.Scripts.Objects.Items.Ore"
                    },
                    logic_values = new List<object>
                    {
                        new { logic_slot_type = new { id = (ushort)1, name = "Occupied" }, value = (object)1.0 },
                        new { logic_slot_type = new { id = (ushort)2, name = "OccupantHash" }, value = (object)"NaN" }
                    },
                    logic_value_count = 2
                },
                new
                {
                    index = 1, underlying_slot_index = (int?)null, display_name = (string?)null,
                    string_key = (string?)null, slot_class = (object?)null, empty = true, interactable = false,
                    locked = false, swappable = false, hides_occupant = false,
                    specific_prefab_hashes = (int[]?)null, occupant = (object?)null,
                    logic_values = new List<object>(), logic_value_count = 0
                }
            },
            count = 2, total_slots = 2
        };
        InspectSlotsView view = new InspectSlotsView("world", NewDevice(), new List<SlotDetailView>
        {
            new SlotDetailView(0,
                new SlotFacts(0, "Import", "Import", new EnumValueView(0, "None"),
                    new SlotFlags(true, false, true, false, new int[0])),
                new SlotOccupantView(new ThingView(new ThingId(101), "ItemIronOre", "Iron Ore"), 1758427767,
                    "Assets.Scripts.Objects.Items.Ore"),
                new List<SlotLogicView>
                {
                    new SlotLogicView(new LogicTypeView(1, "Occupied"), 1.0),
                    new SlotLogicView(new LogicTypeView(2, "OccupantHash"), double.NaN)
                }),
            new SlotDetailView(1, null, null, new List<SlotLogicView>())
        }, 2);
        WireCheck.Same(old, view);
    }

    [Fact]
    public void NetworkSnapshotSameWire()
    {
        var old = new
        {
            gateway_id = "world",
            devices = new List<object>
            {
                new
                {
                    device = OldDevice(),
                    logic_values = new List<object>
                    {
                        new { logic_type = new { id = (ushort)6, name = "Pressure" }, ok = true, value = 101.3 },
                        new
                        {
                            logic_type = new { id = (ushort)12, name = "On" }, ok = false,
                            error = new { code = "logic_not_readable", message = "no" }
                        }
                    },
                    logic_value_count = 2
                }
            },
            count = 1, matched_count = 3, truncated = true
        };
        NetworkSnapshotView view = new NetworkSnapshotView("world", new List<DeviceSnapshotView>
        {
            new DeviceSnapshotView(NewDevice(), new List<object>
            {
                new LogicValueView(new LogicTypeView(6, "Pressure"), 101.3),
                new LogicValueErrorView(new LogicTypeView(12, "On"), new ErrorView("logic_not_readable", "no"))
            })
        }, 3);
        WireCheck.Same(old, view);
    }
}
