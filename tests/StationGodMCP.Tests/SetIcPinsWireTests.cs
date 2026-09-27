#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>set_ic_pins (and get_ic_status's pins): the old StationApi.IcPins shapes against the new views.</summary>
public sealed class SetIcPinsWireTests
{
    [Fact]
    public void SetPinsSameWire()
    {
        var old = new
        {
            gateway_id = "world",
            reference_id = "400",
            has_data_network = true,
            changes = new List<object>
            {
                new
                {
                    index = 0, name = "d0", previous_reference_id = (string?)null, reference_id = "401",
                    changed = true
                },
                new
                {
                    index = 3, name = "d3", previous_reference_id = "402", reference_id = (string?)null,
                    changed = true
                }
            },
            pins = new List<object>
            {
                new
                {
                    index = 0, name = "d0", reference_id = "401", prefab_name = "StructureGasSensor",
                    display_name = "Gas Sensor", label = "Sensor", reachable = true
                },
                new
                {
                    index = 1, name = "d1", reference_id = (string?)null, prefab_name = (string?)null,
                    display_name = (string?)null, label = (string?)null, reachable = false
                }
            }
        };
        SetIcPinsView view = new SetIcPinsView("world", new ThingId(400), true,
            new List<PinChangeView>
            {
                new PinChangeView(0, null, new ThingId(401), true),
                new PinChangeView(3, new ThingId(402), null, true)
            },
            new List<IcPinView>
            {
                new IcPinView(0, new ThingView(new ThingId(401), "StructureGasSensor", "Gas Sensor"), "Sensor", true),
                new IcPinView(1, null, string.Empty, false)
            });
        WireCheck.Same(old, view);
    }
}
