#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A Logic Rocket Uplink's link on the wire: describe_device's uplink (the downlink it follows and its rocket, whether
/// the data connection is live, the downlinks it may follow) and set_uplink's reply.
/// </summary>
public sealed class UplinkWireTests
{
    private static readonly RocketPartView Rocket = new RocketPartView(new ThingId(900), "Kestrel", "Landed", 4200.0, 18, 6);

    private static DownlinkView Downlink(long id, RocketPartView? rocket) =>
        new DownlinkView(new ThingView(new ThingId(id), "StructureRocketDataDownLink", "Logic Rocket Downlink"), rocket);

    [Fact]
    public void DescribeDeviceCarriesTheUplinkOnlyForAnUplink()
    {
        UplinkView uplink = new UplinkView(Downlink(41, Rocket), true,
            new List<DownlinkView> { Downlink(41, Rocket), Downlink(42, null) });
        JObject described = JObject.Parse(WireCheck.New(new DescribeDeviceView(DeviceWireTests.NewDevice(),
            new List<LogicAccessView>(), false, null, null, uplink)));
        JObject plain = JObject.Parse(WireCheck.New(new DescribeDeviceView(DeviceWireTests.NewDevice(),
            new List<LogicAccessView>(), false)));

        Assert.Equal("41", (string?)described["uplink"]!["downlink"]!["reference_id"]);
        Assert.Equal("900", (string?)described["uplink"]!["downlink"]!["rocket"]!["network_id"]);
        Assert.True((bool)described["uplink"]!["connected"]!);
        Assert.Equal(2, ((JArray)described["uplink"]!["choices"]!).Count);
        Assert.Equal(JTokenType.Null, described["uplink"]!["choices"]![1]!["rocket"]!.Type);
        Assert.Null(plain["uplink"]);
        Assert.Null(plain["note"]);
    }

    [Fact]
    public void SetUplinkSaysWhatItFollowedBefore()
    {
        UplinkSetView set = new UplinkSetView(new ThingView(new ThingId(7), "StructureRocketDataUpLink", "Uplink"), null,
            new UplinkView(Downlink(41, Rocket), false, new List<DownlinkView> { Downlink(41, Rocket) }));
        JObject json = JObject.Parse(WireCheck.New(set));

        Assert.Equal("7", (string?)json["uplink"]!["reference_id"]);
        Assert.Equal(JTokenType.Null, json["previous_downlink"]!.Type);
        Assert.Equal("41", (string?)json["link"]!["downlink"]!["reference_id"]);
        Assert.False((bool)json["link"]!["connected"]!);
    }
}
