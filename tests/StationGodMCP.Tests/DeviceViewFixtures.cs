#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A device view and its wire form, for the device tests; inspect device in short counts its logic types and names the
/// writable ones.
/// </summary>
public sealed class DeviceViewFixtures
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
    public void DescribeDeviceInShortCountsAndNamesTheWritableOnes()
    {
        List<LogicAccessView> types = new List<LogicAccessView>
        {
            new LogicAccessView(new LogicTypeView(6, "Pressure"), true, false),
            new LogicAccessView(new LogicTypeView(28, "On"), true, true),
            new LogicAccessView(new LogicTypeView(1200, null), false, true)
        };
        var expected = new { device = OldDevice(), logic_type_count = 3, writable = new List<string> { "On", "1200" } };
        WireCheck.Same(expected, new DescribeDeviceView(NewDevice(), types, listed: false));
    }
}
