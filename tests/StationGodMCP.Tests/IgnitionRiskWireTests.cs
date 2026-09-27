#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>ignition_risk: the wire shape of IgnitionRiskView, and IgnitionRule's arithmetic.</summary>
public sealed class IgnitionRiskWireTests
{
    [Fact]
    public void WireShape()
    {
        var expected = new
        {
            player = new
            {
                reference_id = "151", display_name = "xceled", position = new { x = 700.9, y = 202.0, z = 679.5 }
            },
            cell = new
            {
                temperature_k = 775.5, pressure_kpa = 46.0, energy_j = 1300000.0, inflamed = false, oxygen_mol = 0.0,
                in_room = false
            },
            autoignition_min_energy_j = 10000000.0,
            minimum_ignition_pressure_kpa = 1.5,
            items = new List<object>
            {
                new
                {
                    reference_id = "4452", prefab_name = "ItemGasCanisterOxygen", display_name = "Canister",
                    slot = "Right Hand", in_hand = true,
                    holder = new { reference_id = "151", prefab_name = "Character", display_name = "xceled" },
                    atmosphere = "world", burning = false, hidden = false, flashpoint_k = (double?)373.0,
                    flashpoint_effective_k = (double?)818.0, autoignition_k = (double?)null, ignites_now = false,
                    health_ratio = (double?)1.0
                }
            }
        };
        IgnitionRiskView view = new IgnitionRiskView(
            new LocalPlayerView(new ThingId(151), "xceled", new PositionView(700.9, 202.0, 679.5)),
            new IgnitionCellView(775.5, 46.0, 1300000.0, false, 0.0, false),
            new List<CarriedIgnitionView>
            {
                new CarriedIgnitionView(new ThingView(new ThingId(4452), "ItemGasCanisterOxygen", "Canister"),
                    new CarriedPlaceView("Right Hand", true, new ThingView(new ThingId(151), "Character", "xceled"),
                        "world"),
                    new IgnitionTemperaturesView(373.0, 818.0, null),
                    new IgnitionStateView(false, false, false, 1.0))
            },
            null);
        WireCheck.Same(expected, view);
    }

    [Fact]
    public void PrefabsAppearOnlyWhenAsked()
    {
        IgnitionRiskView view = new IgnitionRiskView(null, null, new List<CarriedIgnitionView>(),
            new List<PrefabIgnitionView> { new PrefabIgnitionView("ItemHay", "Hay", 400.0, null) });
        var expected = new
        {
            player = (object?)null,
            cell = (object?)null,
            autoignition_min_energy_j = 10000000.0,
            minimum_ignition_pressure_kpa = 1.5,
            items = new List<object>(),
            prefabs = new List<object>
            {
                new { prefab_name = "ItemHay", display_name = "Hay", flashpoint_k = (double?)400.0, autoignition_k = (double?)null }
            }
        };
        WireCheck.Same(expected, view);
    }

    [Fact]
    public void ThinAirRaisesTheFlashpoint()
    {
        Assert.Equal(373.0, IgnitionRule.EffectiveFlashpointK(373.0, 1.0));
        Assert.Equal(373.0 / 0.45, IgnitionRule.EffectiveFlashpointK(373.0, 0.45)!.Value, 9);
        Assert.Null(IgnitionRule.EffectiveFlashpointK(373.0, 0.0));     // no air: the flashpoint path never lights
        Assert.Null(IgnitionRule.EffectiveFlashpointK(null, 0.5));
    }

    [Fact]
    public void FireTickGuards()
    {
        Assert.True(IgnitionRule.FireTickChecks(true, false, 1.5));
        Assert.False(IgnitionRule.FireTickChecks(true, false, 1.4));
        Assert.False(IgnitionRule.FireTickChecks(true, true, 50.0));
        Assert.False(IgnitionRule.FireTickChecks(false, false, 50.0));
    }
}
