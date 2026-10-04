#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>A pin with no device and no alias holds nothing, and the pin lists of get_ic_status and set_ic_pins leave it out.</summary>
public sealed class IcPinEmptyTests
{
    [Fact]
    public void APinWithNeitherDeviceNorAliasHoldsNothing()
    {
        Assert.True(new IcPinView(2, null, null, false).HoldsNothing);
        Assert.True(new IcPinView(2, null, string.Empty, false).HoldsNothing);
    }

    [Fact]
    public void ADeviceOrAnAliasIsSomethingToList()
    {
        Assert.False(new IcPinView(0, new ThingView(new ThingId(5), "StructureGasSensor", "Gas Sensor"), null, true).HoldsNothing);
        Assert.False(new IcPinView(1, null, "Furnace", false).HoldsNothing);
    }
}
