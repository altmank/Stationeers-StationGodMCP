#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A swap job's verification lists each network as it is after the swap: with its device count, and its device list
/// only when the call includes network_devices (an 87-device network sent ~7 KB of devices on every swap).
/// </summary>
public sealed class SwapVerificationDevicesTests
{
    private static RunCableNetworkView Network() =>
        new RunCableNetworkView(new ThingId(5), 3, new CableNetworkRatings(100, 200, 5000, null, null),
            new List<ThingView>
            {
                new ThingView(new ThingId(1), "StructureBattery", "Station Battery"),
                new ThingView(new ThingId(2), "StructureAreaPowerControl", "Area Power Control")
            });

    private static JObject Verification(bool listDevices) =>
        JObject.Parse(WireCheck.New(new UpgradeVerificationView(new List<UpgradeProblemView>(),
            new UpgradeLinkCounts(4, 4), new List<UpgradeLinkView>(), new List<UpgradeLinkView>(),
            new List<object> { NetworkDeviceLists.Shown(Network(), listDevices) })));

    [Fact]
    public void ByDefaultAVerifiedNetworkCountsItsDevicesWithoutListingThem()
    {
        JObject network = (JObject)Verification(listDevices: false)["networks"]![0]!;

        Assert.Null(network["devices"]);
        Assert.Equal(2, (int)network["device_count"]!);
    }

    [Fact]
    public void IncludeNetworkDevicesListsThem()
    {
        JObject network = (JObject)Verification(listDevices: true)["networks"]![0]!;

        Assert.Equal(2, ((JArray)network["devices"]!).Count);
    }
}
