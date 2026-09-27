#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>planet: the old StationApi.Planet shapes against the new views, from the same values.</summary>
public sealed class PlanetWireTests
{
    [Fact]
    public void NotLoadedSameWire()
    {
        WireCheck.Same(new { loaded = false, terraforming_mod = (object?)null }, new PlanetNotLoadedView(null));
        WireCheck.Same(
            new
            {
                loaded = false,
                terraforming_mod = new
                {
                    name = "Terraforming Reloaded",
                    version = "1.2.0",
                    state = "live",
                    live = true,
                    temperature_adjustment_k = (double?)0.25
                }
            },
            new PlanetNotLoadedView(new TerraformingModView("1.2.0", "live", 0.25)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoadedSameWire(bool stormAndMod)
    {
        object old = OldLoaded(stormAndMod);
        PlanetView view = NewLoaded(stormAndMod);
        WireCheck.SameAfterRenames(old, view, Renames());
    }

    // The planet renames.
    private static Dictionary<string, string> Renames()
    {
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["world_seconds"] = "world_time_s",
            ["size_share"] = "size_ratio",
            ["gas_moles"] = "gas_mol",
            ["liquid_moles"] = "liquid_mol",
            ["liquid_litres"] = "liquid_l",
            ["sea.litres"] = "liquid_l",
            ["sea.threshold_litres"] = "threshold_l",
            ["range.today_min_sun_angle"] = "today_min_sun_angle_deg",
            ["range.today_max_sun_angle"] = "today_max_sun_angle_deg",
            ["fuel_moles"] = "fuel_mol",
            ["oxidiser_moles"] = "oxidiser_mol",
            ["carbon_dioxide_share"] = "carbon_dioxide_ratio",
            ["gases[].moles"] = "amount_mol",
            ["gases[].per_cell"] = "per_cell_mol",
            ["gases[].condenses_below_k_today_coldest"] = "condenses_below_today_coldest_k",
            ["gases[].condenses_below_k_orbit_coldest"] = "condenses_below_orbit_coldest_k"
        };
        string[] parts = { "sun_angle", "sun_distance", "greenhouse", "density", "weather", "latent", "external" };
        foreach (string part in parts)
        {
            renames["temperature_parts." + part] = part + "_k";
        }

        foreach (string reservoir in new[] { "liquid_clouds", "ice_clouds", "ice_caps" })
        {
            renames[$"reservoirs.{reservoir}.liquid_litres"] = "liquid_l";
            renames[$"reservoirs.{reservoir}.moles"] = "amount_mol";
            renames[$"reservoirs.{reservoir}.contents[].moles"] = "amount_mol";
        }

        return renames;
    }

    // The old anonymous shape, member for member, with fixed values.
    private static object OldLoaded(bool stormAndMod)
    {
        List<object> gases = new List<object>
        {
            new
            {
                gas = "CarbonDioxide", display_name = "Carbon Dioxide", state = "gas", moles = 1234.5, per_cell = 0.5,
                partial_kpa = (double?)3.25, freezes_below_k = (double?)195.5, min_liquid_pressure_kpa = (double?)517.0,
                condenses_below_k_today_coldest = (double?)null, condenses_below_k_orbit_coldest = (double?)180.25
            },
            new
            {
                gas = "Water", display_name = (string?)null, state = "liquid", moles = 10.0, per_cell = 0.0,
                partial_kpa = (double?)null, freezes_below_k = (double?)273.15, min_liquid_pressure_kpa = (double?)null,
                condenses_below_k_today_coldest = (double?)null, condenses_below_k_orbit_coldest = (double?)null
            }
        };
        Dictionary<string, object?> reservoirs = new Dictionary<string, object?>
        {
            ["liquid_clouds"] = new
            {
                volume_l = 100.0, liquid_litres = 2.5, moles = 12.0,
                contents = new List<object> { new { gas = "Water", moles = 12.0 } }
            },
            ["ice_clouds"] = null,
            ["ice_caps"] = null
        };
        return new
        {
            loaded = true, world_seconds = 123456.5, day_length_s = 1200.0, volume_l = 8.0e9, gas_volume_l = 7.5e9,
            cells = 1.0e6, size_share = (double?)(stormAndMod ? 0.5 : (double?)null), gas_moles = 3.0e7,
            liquid_moles = 12.0, liquid_litres = 0.2,
            sea = new { litres = 5.0, threshold_litres = 1000.0, shown = false },
            pressure_kpa = 9.5, game_pressure_kpa = 9.25, temperature_k = 260.5,
            temperature_parts = new
            {
                sun_angle = 1.0, sun_distance = 2.0, greenhouse = 3.0, density = 4.0,
                weather = stormAndMod ? 5.0 : 0.0, latent = 6.0, external = 7.0
            },
            sun_angle_deg = 42.5f, solar_energy_percent = 88.25f,
            range = new
            {
                today_min_k = 200.0, today_min_sun_angle = 180.0, today_max_k = 300.0, today_max_sun_angle = 0.0,
                orbit_min_k = 190.0, orbit_max_k = 310.0
            },
            storm = stormAndMod ? new { id = "StormDust" } : null,
            oxygen_kpa = 0.01, toxins_kpa = 0.0, fuel_moles = 3.5, oxidiser_moles = 4.5, carbon_dioxide_share = 0.95,
            gases,
            reservoirs,
            starting_air = new
            {
                per_cell_mol = 30.0, mol = 3.0e7,
                gases = new[] { new { gas = "CarbonDioxide", amount_mol = 3.0e7 } }
            },
            terraforming_mod = stormAndMod
                ? new
                {
                    name = "Terraforming Reloaded", version = (string?)null, state = (string?)"not live",
                    live = false, temperature_adjustment_k = (double?)null
                }
                : null
        };
    }

    private static PlanetView NewLoaded(bool stormAndMod)
    {
        List<PlanetGasView> gases = new List<PlanetGasView>
        {
            new PlanetGasView(
                new PlanetGasAmount("CarbonDioxide", "Carbon Dioxide", "gas", 1234.5, 0.5, 3.25),
                new PlanetGasPhases(195.5, 517.0, null, 180.25)),
            new PlanetGasView(
                new PlanetGasAmount("Water", null, "liquid", 10.0, 0.0, null),
                new PlanetGasPhases(273.15, null, null, null))
        };
        ReservoirsView reservoirs = new ReservoirsView(
            new ReservoirView(100.0, 2.5, 12.0, new List<ReservoirGasView> { new ReservoirGasView("Water", 12.0) }),
            null,
            null);
        PlanetTank tank = new PlanetTank(
            8.0e9, 7.5e9, 1.0e6, stormAndMod ? 0.5 : (double?)null, 3.0e7, 12.0, 0.2, new SeaView(5.0, 1000.0, false));
        PlanetWeather weather = new PlanetWeather(
            9.5, 9.25, 260.5,
            new TemperaturePartsView(1.0, 2.0, 3.0, 4.0, stormAndMod ? 5.0 : 0.0, 6.0, 7.0),
            42.5f, 88.25f,
            new TemperatureRangeView(new TemperatureSweepView(200.0, 180.0, 300.0, 0.0), 190.0, 310.0),
            stormAndMod ? new StormView("StormDust") : null);
        return new PlanetView(
            new PlanetClock(123456.5, 1200.0),
            tank,
            weather,
            new PlanetAir(0.01, 0.0, 3.5, 4.5, 0.95),
            gases,
            reservoirs,
            new StartingAirView(30.0, 3.0e7, new List<ReservoirGasView> { new ReservoirGasView("CarbonDioxide", 3.0e7) }),
            stormAndMod ? new TerraformingModView(null, "not live", null) : null);
    }
}

public sealed class PlanetMathTests
{
    [Fact]
    public void PressureIsIdealGasAndZeroWithoutVolume()
    {
        Assert.Equal(1.0 * PlanetMath.GasConstant * 300.0 / 10.0, PlanetMath.PressureKpa(1.0, 300.0, 10.0));
        Assert.Equal(0.0, PlanetMath.PressureKpa(1.0, 300.0, 0.0));
    }

    [Fact]
    public void SweepKeepsTheFirstExtremesAndSkipsNaN()
    {
        TemperatureSweep sweep = new TemperatureSweep();
        sweep.Add(0, 250.0);
        sweep.Add(1, double.NaN);
        sweep.Add(2, 200.0);
        sweep.Add(3, 200.0);
        sweep.Add(4, 300.0);
        Assert.Equal(200.0, sweep.MinK);
        Assert.Equal(2.0, sweep.MinAngle);
        Assert.Equal(300.0, sweep.MaxK);
        Assert.Equal(4.0, sweep.MaxAngle);
    }
}
